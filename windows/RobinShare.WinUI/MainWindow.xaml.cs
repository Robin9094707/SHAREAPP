using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using Forms = System.Windows.Forms;

namespace RobinShare;

public sealed class ShareFile(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail
    {
        get
        {
            try
            {
                var file = new FileInfo(Path);
                return ByteSize(file.Length) + " · " + file.DirectoryName;
            }
            catch { return Path; }
        }
    }

    public static string ByteSize(long bytes)
    {
        if(bytes >= 1024L*1024*1024) return $"{bytes/(1024d*1024*1024):N2} GB";
        if(bytes >= 1024*1024) return $"{bytes/(1024d*1024):N1} MB";
        if(bytes >= 1024) return $"{bytes/1024d:N0} KB";
        return $"{bytes} B";
    }
}

public sealed record TargetChoice(string Id, string Name);

/// All visible controls on the main window come from the WinUI 3 / Windows App SDK control library.
/// The WinForms NotifyIcon is intentionally retained solely for the Windows notification area.
public sealed partial class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly ObservableCollection<ShareFile> files = [];
    private readonly LocalServer local;
    private readonly Discovery discovery = new();
    private readonly DispatcherQueueTimer refreshTimer;
    private readonly Forms.NotifyIcon? tray;
    private WindowsShareBridge? nativeSharing;
    private DeviceState? remote;
    private Identity? Identity => settings.Identities.GetValueOrDefault(settings.Server);
    private readonly HashSet<string> active = [];
    private readonly HashSet<string> notified = [];
    private CancellationTokenSource? transferStop;
    private bool sending;
    private bool quitting;
    private bool refreshing;

    private static string Downloads => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Robins Share");

    private API Client() => new(settings.Server, Identity?.Token ?? "");

    public MainWindow()
    {
        InitializeComponent();
        Title = "Robins Share · Native WinUI 3";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 830)); } catch { }
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "RobinShare.ico")); } catch { }

        FilesList.ItemsSource = files;
        ServerAddress.Text = settings.Server;
        DeviceName.Text = settings.Name;
        AutoStart.IsOn = settings.AutoStart;
        ContextIntegration.IsOn = settings.ContextMenu;
        ThemeCombo.SelectedIndex = settings.UiTheme switch { "Light" => 1, "Default" => 2, _ => 0 };
        ApplyTheme(settings.UiTheme);
        SideNav.SelectedItem = SendNavItem;

        local = new LocalServer(settings);
        local.Changed += () => DispatcherQueue.TryEnqueue(() => { RenderLocal(); RenderInbox(); });
        refreshTimer = DispatcherQueue.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(10);
        refreshTimer.Tick += async (_, _) =>
        {
            refreshTimer.Stop();
            try { await Refresh(); }
            catch(Exception ex) { Status("Server nicht erreichbar. Lokale Freigabe bleibt verfügbar: " + ex.Message); }
            finally { if(!quitting) refreshTimer.Start(); }
        };

        try
        {
            nativeSharing = new WindowsShareBridge(WindowNative.GetWindowHandle(this));
        }
        catch(Exception e) { StartupDiagnostics.Log("Systemfreigabe", e); }

        Forms.NotifyIcon? icon = null;
        try
        {
            icon = new Forms.NotifyIcon {
                Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory,"RobinShare.ico")),
                Text = "Robins Share · WinUI 3",
                Visible = true
            };
            icon.DoubleClick += (_, _) => DispatcherQueue.TryEnqueue(ShowAgain);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Robins Share öffnen", null, (_, _) => DispatcherQueue.TryEnqueue(ShowAgain));
            menu.Items.Add("Downloads öffnen", null, (_, _) => OpenDownloadFolder());
            menu.Items.Add("Beenden", null, (_, _) => DispatcherQueue.TryEnqueue(() => _ = ExitAsync()));
            icon.ContextMenuStrip = menu;
        }
        catch(Exception e) { StartupDiagnostics.Log("Tray", e); }
        tray = icon;

        AppWindow.Closing += (_, e) =>
        {
            if(!quitting) { e.Cancel = true; AppWindow.Hide(); Status("Im Hintergrund aktiv."); }
        };

        AppRoot.AllowDrop = true;
        AppRoot.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        AppRoot.Drop += async (_, e) =>
        {
            try
            {
                if(e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    AddPaths(items.Select(x => x.Path));
                }
            }
            catch(Exception ex) { Status("Drag & Drop: " + ex.Message, InfoBarSeverity.Error); }
        };

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await local.Start();
            RenderLocal();
            await discovery.Start(settings.Name, local.Address, local.Fingerprint);
            DiscoveryStatus.Text = discovery.Status;
        }
        catch(Exception ex) { Status("Lokale Verbindung: " + ex.Message, InfoBarSeverity.Warning); }

        await Connect();
        refreshTimer.Start();
    }

    private async Task ExitAsync()
    {
        if(quitting)return;
        quitting = true;
        refreshTimer.Stop();
        transferStop?.Cancel();
        if(tray != null){tray.Visible = false;tray.Dispose();}
        discovery.Dispose();
        try { await local.DisposeAsync(); } catch(Exception e) { StartupDiagnostics.Log("Serverstopp",e); }
        Close();
        Environment.Exit(0);
    }

    private void ShowAgain()
    {
        AppWindow.Show();
        Activate();
    }

    private void Status(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Message = message;
        StatusInfo.IsOpen = true;
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "send";
        SendPage.Visibility = tag == "send" ? Visibility.Visible : Visibility.Collapsed;
        ReceivePage.Visibility = tag == "receive" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if(tag == "receive") RenderInbox();
        if(tag == "devices") RenderLocal();
    }

    private void ChangeTheme(object sender, SelectionChangedEventArgs args)
    {
        if(ThemeCombo.SelectedItem is not ComboBoxItem selected) return;
        var theme = selected.Tag as string ?? "Dark";
        settings.UiTheme = theme;
        ApplyTheme(theme);
        settings.Save();
    }

    private void ApplyTheme(string theme)
    {
        AppRoot.RequestedTheme = theme switch {
            "Light" => ElementTheme.Light,
            "Default" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };
    }

    public void AddPaths(IEnumerable<string> values) => _ = AddPathsAsync(values.Distinct().ToArray());

    private async Task AddPathsAsync(string[] incoming)
    {
        if(sending) { Status("Während eines Uploads ist die Auswahl gesperrt."); return; }
        foreach(var path in incoming)
        {
            try
            {
                if(File.Exists(path))
                {
                    if(files.All(x => !string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                        files.Add(new ShareFile(path));
                }
                else if(Directory.Exists(path))
                {
                    Status("Ordner wird als ZIP vorbereitet: " + System.IO.Path.GetFileName(path));
                    var zip = await Task.Run(() => FolderPackager.Pack(path));
                    files.Add(new ShareFile(zip));
                }
            }
            catch(Exception exception) { Status("Datei konnte nicht hinzugefügt werden: " + exception.Message, InfoBarSeverity.Error); }
        }
        if(files.Count>0) { SideNav.SelectedItem = SendNavItem; Status(files.Count + " Datei(en) ausgewählt."); }
    }

    private async void ChooseFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var items = await picker.PickMultipleFilesAsync();
            if(items != null) AddPaths(items.Select(i => i.Path));
        }
        catch(Exception ex) { Status("Dateiauswahl fehlgeschlagen: " + ex.Message, InfoBarSeverity.Error); }
    }

    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if(folder != null) AddPaths([folder.Path]);
        }
        catch(Exception ex) { Status("Ordnerauswahl fehlgeschlagen: " + ex.Message, InfoBarSeverity.Error); }
    }

    private void PasteFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            if(Forms.Clipboard.ContainsFileDropList())
                AddPaths(Forms.Clipboard.GetFileDropList().Cast<string>());
            else Status("Die Zwischenablage enthält keine Dateien.");
        }
        catch(Exception ex) { Status("Zwischenablage: "+ex.Message,InfoBarSeverity.Error); }
    }

    private void ClearSelection(object sender, RoutedEventArgs e)
    {
        if(!sending) { files.Clear(); Status("Auswahl geleert."); }
    }

    private async Task Connect()
    {
        try
        {
            settings.Server = Safety.Server(ServerAddress.Text).AbsoluteUri.TrimEnd('/');
            settings.Name = DeviceName.Text.Trim();
            using var client = Client();
            var config = await client.Call<ServerConfig>("/api/config");
            if(Identity == null)
            {
                settings.Identities[settings.Server] = await client.Call<Identity>(
                    "/api/devices", HttpMethod.Post, new {name = settings.Name, platform = "windows"});
            }
            settings.Save();
            await Refresh();
            Status("Mit deinem HTTPS-Server verbunden. Lokale Geräte bleiben unabhängig erreichbar.", InfoBarSeverity.Success);
        }
        catch(Exception e) { Status("Serververbindung: " + e.Message, InfoBarSeverity.Warning); }
    }

    private async Task Refresh()
    {
        if(refreshing)return;
        refreshing = true;
        try
        {
            RenderLocal();
            if(Identity == null) { UpdateTargets(); RenderInbox(); return; }
            using var client=Client();
            remote = await client.Call<DeviceState>("/api/devices/me");
            PairCode.Text = remote.PairCode;
            UpdateTargets();
            RenderPeers();
            RenderInbox();
        }
        finally { refreshing = false; }
    }

    private void UpdateTargets()
    {
        var selected = (TargetCombo.SelectedItem as TargetChoice)?.Id ?? "";
        var items = new List<TargetChoice> { new("", "Code und HTTPS-Link erstellen · ohne App") };
        items.AddRange((remote?.Devices ?? []).Select(d => new TargetChoice(d.Id,d.Name+" · über Server")));
        items.AddRange(settings.LocalTokens.Select(t => new TargetChoice("local:"+t.Key,t.Value+" · direkt im WLAN")));
        TargetCombo.ItemsSource = items;
        TargetCombo.SelectedItem = items.FirstOrDefault(x => x.Id == selected) ?? items[0];
    }

    private void RenderLocal()
    {
        LocalAddress.Text = local.Address;
        LocalPairCode.Text = local.PairCode;
        Fingerprint.Text = local.Fingerprint;
        LocalStatus.Text = local.Status;
        LocalPeersPanel.Children.Clear();
        foreach(var kv in settings.LocalTokens.ToArray())
        {
            var token = kv.Key;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text=kv.Value+" · iPhone/WLAN", VerticalAlignment=VerticalAlignment.Center });
            var remove = new Button { Content="Entfernen" };
            remove.Click += async (_, _) =>
            {
                if(!await ConfirmAsync("Lokale Kopplung löschen?",kv.Value+" wird abgemeldet.")) return;
                settings.LocalTokens.Remove(token);
                settings.Save();
                RenderLocal();
                UpdateTargets();
            };
            row.Children.Add(remove);
            LocalPeersPanel.Children.Add(row);
        }
        UpdateTargets();
    }

    private void RenderPeers()
    {
        PeersPanel.Children.Clear();
        foreach(var peer in remote?.Devices ?? [])
        {
            var row = new StackPanel { Orientation=Orientation.Horizontal, Spacing=12 };
            row.Children.Add(new TextBlock { Text=peer.Name+" · "+peer.Platform,VerticalAlignment=VerticalAlignment.Center });
            var remove=new Button { Content="Entfernen" };
            remove.Click += async (_, _) =>
            {
                if(!await ConfirmAsync("Kopplung entfernen?", peer.Name+" verliert den Zugriff auf diesen Server."))return;
                try
                {
                    using var client=Client();
                    await client.Call<JsonElement>("/api/devices/"+peer.Id,HttpMethod.Delete);
                    await Refresh();
                }
                catch(Exception exception) { Status(exception.Message,InfoBarSeverity.Error); }
            };
            row.Children.Add(remove);
            PeersPanel.Children.Add(row);
        }
    }

    private void RenderInbox()
    {
        InboxPanel.Children.Clear();
        foreach(var t in remote?.Inbox ?? []) InboxPanel.Children.Add(CreateTransferCard(t, false));
        foreach(var upload in local.Pending)
        {
            InboxPanel.Children.Add(CreateLocalTransferCard(upload));
            NotifyOnce(upload.Id,upload.Name);
        }
        if(InboxPanel.Children.Count == 0)
            InboxPanel.Children.Add(new TextBlock {
                Text = "Noch keine Sendungen. Dein Posteingang wird automatisch aktualisiert.",
                Opacity = 0.7
            });
    }

    private Border CreateTransferCard(Transfer transfer, bool localTransfer)
    {
        var container = new StackPanel { Spacing=12 };
        container.Children.Add(new TextBlock { Text=transfer.Filename,FontSize=17,TextWrapping=TextWrapping.Wrap });
        container.Children.Add(new TextBlock { Text=ShareFile.ByteSize(transfer.Size)+" · verschlüsselte Serverfreigabe" });
        var actions = new StackPanel { Orientation=Orientation.Horizontal,Spacing=10 };
        var accept = new Button { Content="Annehmen",IsEnabled=!active.Contains(transfer.Id) };
        var decline = new Button { Content="Ablehnen",IsEnabled=!active.Contains(transfer.Id) };
        accept.Click += async (_,_) =>
        {
            if(!active.Add(transfer.Id))return;
            accept.IsEnabled=false;decline.IsEnabled=false;
            try
            {
                using var client=Client();
                var path = await client.Download(transfer,Downloads,
                    new Progress<double>(p=>TransferProgress.Value=p*100),CancellationToken.None);
                Status("Sicher heruntergeladen: "+System.IO.Path.GetFileName(path),InfoBarSeverity.Success);
                await Refresh();
            }
            catch(Exception e) { Status("Download: "+e.Message,InfoBarSeverity.Error); }
            finally { active.Remove(transfer.Id);RenderInbox(); }
        };
        decline.Click += async (_,_) =>
        {
            if(!await ConfirmAsync("Datei ablehnen?", "Die Serverfreigabe wird als erledigt markiert."))return;
            try{using var client=Client();await client.Call<JsonElement>(transfer.AckUrl,HttpMethod.Post,new{});await Refresh();}
            catch(Exception e){Status("Ablehnen: "+e.Message,InfoBarSeverity.Error);}
        };
        actions.Children.Add(accept); actions.Children.Add(decline);container.Children.Add(actions);
        return new Border {
            Child=container,CornerRadius=new CornerRadius(14),Padding=new Thickness(18),
            Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness=new Thickness(1),
            BorderBrush=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"]
        };
    }

    private Border CreateLocalTransferCard(LocalUpload upload)
    {
        var stack = new StackPanel { Spacing=12 };
        stack.Children.Add(new TextBlock{Text=upload.Name,FontSize=17,TextWrapping=TextWrapping.Wrap});
        stack.Children.Add(new TextBlock{Text=ShareFile.ByteSize(upload.Size)+" · direkte WLAN-Übertragung"});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        var accept=new Button{Content="Annehmen",IsEnabled=!active.Contains(upload.Id)};
        var reject=new Button{Content="Ablehnen",IsEnabled=!active.Contains(upload.Id)};
        accept.Click+=async (_,_)=>{
            if(!active.Add(upload.Id))return;
            accept.IsEnabled=false;reject.IsEnabled=false;
            try {
                var path=await local.Accept(upload.Id,Downloads);
                Status("Direkt gespeichert: "+System.IO.Path.GetFileName(path),InfoBarSeverity.Success);
            }
            catch(Exception exception){Status(exception.Message,InfoBarSeverity.Error);}
            finally{active.Remove(upload.Id);RenderInbox();}
        };
        reject.Click+=async (_,_)=>{try{await local.Reject(upload.Id);RenderInbox();}catch(Exception ex){Status(ex.Message,InfoBarSeverity.Error);}};
        actions.Children.Add(accept);actions.Children.Add(reject);stack.Children.Add(actions);
        return new Border{
            Child=stack,CornerRadius=new CornerRadius(14),Padding=new Thickness(18),
            Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
        };
    }

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        var dialog = new ContentDialog {
            XamlRoot=AppRoot.XamlRoot,
            Title=title,Content=body,
            PrimaryButtonText="Bestätigen",CloseButtonText="Abbrechen",
            DefaultButton=ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void NotifyOnce(string id, string name)
    {
        if(tray == null || !notified.Add(id))return;
        tray.BalloonTipTitle = "Neue Datei in SHAREAPP";
        tray.BalloonTipText = name;
        tray.ShowBalloonTip(3500);
    }

    private async void SendFiles(object sender, RoutedEventArgs e)
    {
        if(sending || files.Count == 0) { Status("Bitte zuerst Dateien hinzufügen."); return; }
        sending = true;
        SendButton.IsEnabled=false;ClearButton.IsEnabled=false;SystemShareButton.IsEnabled=false;
        CancelButton.IsEnabled=true;TransferProgress.Visibility=Visibility.Visible;
        TransferProgress.Value=0;ShareResults.Text="";
        transferStop = new CancellationTokenSource();
        var destination = (TargetCombo.SelectedItem as TargetChoice)?.Id ?? "";
        TargetCombo.IsEnabled=false;
        try
        {
            foreach(var item in files.ToArray())
            {
                Status("Sende "+item.Name+" …");
                TransferCaption.Text=item.Name;
                TransferProgress.Value=0;
                if(destination.StartsWith("local:",StringComparison.Ordinal))
                {
                    await local.QueueOutgoing(item.Path,destination["local:".Length..],transferStop.Token);
                    ShareResults.Text += item.Name + " · Auf dem iPhone in Empfangen annehmen.\n";
                }
                else
                {
                    using var client=Client();
                    var result=await client.Send(item.Path,string.IsNullOrEmpty(destination)?null:destination,
                        new Progress<double>(value=>TransferProgress.Value=value*100),transferStop.Token);
                    ShareResults.Text+=item.Name+"\nCode: "+result.Pin+"\n"+result.Link+"\n\n";
                }
                files.Remove(item);
            }
            Status("Alle Dateien erfolgreich bereitgestellt.",InfoBarSeverity.Success);
        }
        catch(OperationCanceledException) { Status("Upload pausiert. Du kannst später fortsetzen."); }
        catch(Exception exception) { Status("Übertragung: "+exception.Message,InfoBarSeverity.Error); }
        finally
        {
            sending=false;SendButton.IsEnabled=true;ClearButton.IsEnabled=true;
            SystemShareButton.IsEnabled=true;CancelButton.IsEnabled=false;
            TargetCombo.IsEnabled=true;
            TransferProgress.Visibility=Visibility.Collapsed;
            transferStop?.Dispose();transferStop=null;
        }
    }

    private void Cancel(object sender,RoutedEventArgs e) => transferStop?.Cancel();

    private void ShareThroughWindows(object sender,RoutedEventArgs e)
    {
        try
        {
            if(nativeSharing == null) throw new InvalidOperationException("Windows-Freigabe ist auf diesem PC nicht verfügbar.");
            nativeSharing.Show(files.Select(x=>x.Path));
            Status("Nativer Windows-Freigabedialog geöffnet.",InfoBarSeverity.Success);
        }
        catch(Exception exception) { Status(exception.Message,InfoBarSeverity.Error); }
    }

    private void CopyResults(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(ShareResults.Text)){Status("Noch keine Freigabelinks vorhanden.");return;}
        Forms.Clipboard.SetText(ShareResults.Text);
        Status("Links und Codes kopiert.",InfoBarSeverity.Success);
    }

    private async void Lookup(object sender,RoutedEventArgs e)
    {
        try
        {
            var code = ReceiveCode.Text.Trim();
            if(code.Length!=6 || !code.All(char.IsAsciiDigit))throw new InvalidDataException("Bitte sechs Ziffern eingeben.");
            using var client=Client();
            var found=await client.Call<Lookup>("/api/lookup/pin/"+code);
            FoundPanel.Children.Clear();
            FoundPanel.Children.Add(CreateTransferCard(found.Transfer,false));
        }
        catch(Exception exception){Status("Code: "+exception.Message,InfoBarSeverity.Error);}
    }

    private async void Pair(object sender,RoutedEventArgs e)
    {
        try
        {
            var code=PeerCode.Text.Trim();
            if(code.Length!=6 || !code.All(char.IsAsciiDigit))throw new InvalidDataException("Bitte den sechsstelligen Code eingeben.");
            using var client=Client();
            await client.Call<JsonElement>("/api/devices/pair",HttpMethod.Post,new{code});
            PeerCode.Text="";
            await Refresh();
            Status("Geräte verbunden.",InfoBarSeverity.Success);
        }
        catch(Exception exception){Status("Kopplung: "+exception.Message,InfoBarSeverity.Error);}
    }

    private async void SaveServer(object sender,RoutedEventArgs e) => await Connect();

    private async void ResetIdentity(object sender,RoutedEventArgs e)
    {
        if(!await ConfirmAsync("Identität zurücksetzen?", "Bisherige Serverkopplungen müssen danach erneuert werden."))return;
        settings.Identities.Remove(settings.Server);
        settings.Save();
        await Connect();
    }

    private void SaveIntegration(object sender,RoutedEventArgs e)
    {
        try
        {
            settings.AutoStart=AutoStart.IsOn;
            settings.ContextMenu=ContextIntegration.IsOn;
            settings.ApplyIntegration();
            settings.Save();
            Status("Windows-Integration gespeichert.",InfoBarSeverity.Success);
        }
        catch(Exception exception){Status(exception.Message,InfoBarSeverity.Error);}
    }

    private void RenewLocal(object sender,RoutedEventArgs e) {local.RenewCode();RenderLocal();}
    private void OpenDownloads(object sender,RoutedEventArgs e) => OpenDownloadFolder();

    private static void OpenDownloadFolder()
    {
        Directory.CreateDirectory(Downloads);
        Process.Start(new ProcessStartInfo(Downloads){UseShellExecute=true});
    }

    private void OpenHotspot(object sender,RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot"){UseShellExecute=true});
}

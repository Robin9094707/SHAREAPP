using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Orientation=System.Windows.Controls.Orientation;
using Button=System.Windows.Controls.Button;
using MessageBox=System.Windows.MessageBox;
using DataFormats=System.Windows.DataFormats;
using DragEventArgs=System.Windows.DragEventArgs;
namespace RobinShare;
public partial class MainWindow : Window
{
    private readonly Settings settings=Settings.Load();
    private readonly LocalServer local;
    private readonly Discovery discovery=new();
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly ObservableCollection<string> paths=[];
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(10)};
    private readonly HashSet<string> notified=[];
    private readonly HashSet<string> active=[];
    private DeviceState? state;
    private string? serverSnapshot;
    private string? localSnapshot;
    private CancellationTokenSource? transferStop;
    private bool quitting;
    private bool sending;
    private Identity? Identity=>settings.Identities.GetValueOrDefault(settings.Server);
    private API Client()=>new(settings.Server,Identity?.Token??"");
    private static string DownloadFolder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","Robins Share");
    public MainWindow(bool smokeTest=false)
    {
        InitializeComponent();
        ServerAddress.Text=settings.Server;DeviceName.Text=settings.Name;Autostart.IsChecked=settings.AutoStart;ContextIntegration.IsChecked=settings.ContextMenu;
        SelectedFiles.ItemsSource=paths;
        local=new(settings);
        var stream=System.Windows.Application.GetResourceStream(new Uri("Assets/RobinShare.ico",UriKind.Relative))?.Stream;
        tray=new System.Windows.Forms.NotifyIcon{Text="Robins Share · Bereit",Visible=true,Icon=stream==null?System.Drawing.SystemIcons.Application:new System.Drawing.Icon(stream)};
        tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();});
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Robins Share öffnen",null,(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();}));
        menu.Items.Add("Empfangsordner öffnen",null,(_,_)=>OpenFolder());
        menu.Items.Add("Beenden",null,async(_,_)=>await Dispatcher.InvokeAsync(Exit));tray.ContextMenuStrip=menu;
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;Hide();}};
        local.Changed+=()=>Dispatcher.BeginInvoke(()=>{UpdateLocal();RenderInbox();});
        timer.Tick+=async(_,_)=>{timer.Stop();try{await Refresh();}catch{}finally{if(!quitting)timer.Start();}};
        if(!smokeTest)_=InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        try{await local.Start();UpdateLocal();await discovery.Start(settings.Name,local.Address,local.Fingerprint);DiscoveryStatus.Text=discovery.Status;}
        catch(Exception e){LocalStatus.Text="Lokale Verbindung nicht verfügbar: "+e.Message;}
        await Connect();timer.Start();
    }
    private async void Exit()
    {
        quitting=true;timer.Stop();transferStop?.Cancel();tray.Visible=false;tray.Dispose();discovery.Dispose();await local.DisposeAsync();System.Windows.Application.Current.Shutdown();
    }
    private async Task Connect()
    {
        try
        {
            settings.Server=Safety.Server(ServerAddress.Text).AbsoluteUri.TrimEnd('/');settings.Name=DeviceName.Text.Trim();
            using var client=Client();var _=await client.Call<ServerConfig>("/api/config");
            if(Identity==null){settings.Identities[settings.Server]=await client.Call<Identity>("/api/devices",HttpMethod.Post,new{name=settings.Name,platform="windows"});settings.Save();}
            await Refresh();Status.Text="Mit deinem Server verbunden. Lokale Übertragung ist separat verfügbar.";
        }
        catch(Exception e){Status.Text="Serververbindung prüfen: "+e.Message;}
    }
    private async Task Refresh()
    {
        if(Identity==null)return;
        using var client=Client();state=await client.Call<DeviceState>("/api/devices/me");PairCode.Text=state.PairCode;
        var snapshot=JsonSerializer.Serialize(state.Devices);
        if(snapshot!=serverSnapshot){serverSnapshot=snapshot;RenderPeers();UpdateTargets();}
        RenderInbox();
    }
    private void UpdateLocal()
    {
        LocalAddress.Text=local.Address;LocalPairCode.Text=local.PairCode;Fingerprint.Text=local.Fingerprint;LocalStatus.Text=local.Status;
        var snapshot=JsonSerializer.Serialize(settings.LocalTokens);
        if(snapshot!=localSnapshot)
        {
            localSnapshot=snapshot;LocalPeers.Children.Clear();
            foreach(var pair in settings.LocalTokens.ToArray())
            {
                var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};row.Children.Add(new TextBlock{Text=pair.Value+" · Lokal",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)});
                var remove=new Button{Content="Entfernen"};var token=pair.Key;
                remove.Click+=(_,_)=>{if(MessageBox.Show("Lokale Kopplung entfernen?","Robins Share",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;settings.LocalTokens.Remove(token);settings.Save();UpdateLocal();};row.Children.Add(remove);LocalPeers.Children.Add(row);
            }
            UpdateTargets();
        }
    }
    private void UpdateTargets()
    {
        var selected=Target.SelectedValue as string;
        var options=new List<Device>{new("","Mit Code oder Link teilen","code")};options.AddRange((state?.Devices??[]).Select(d=>d with{Name=d.Name+" · Server"}));options.AddRange(settings.LocalTokens.Select(p=>new Device("local:"+p.Key,p.Value+" · Direkt im WLAN","local")));
        Target.ItemsSource=options;Target.SelectedValue=options.Any(d=>d.Id==selected)?selected:"";
    }
    private void RenderPeers()
    {
        Peers.Children.Clear();foreach(var device in state?.Devices??[])
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};row.Children.Add(new TextBlock{Text=device.Name+" · "+device.Platform,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)});
            var button=new Button{Content="Entfernen"};button.Click+=async(_,_)=>{if(MessageBox.Show("Kopplung entfernen?","Robins Share",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;try{using var client=Client();await client.Call<JsonElement>("/api/devices/"+device.Id,HttpMethod.Delete);await Refresh();}catch(Exception e){Fail(e);}};row.Children.Add(button);Peers.Children.Add(row);
        }
    }
    private void RenderInbox()
    {
        Inbox.Children.Clear();
        foreach(var transfer in state?.Inbox??[])Inbox.Children.Add(Card(transfer));
        foreach(var transfer in local.Pending)
        {
            Notify(transfer.Id,transfer.Name);
            var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=transfer.Name,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});panel.Children.Add(new TextBlock{Text=Size(transfer.Size)+" · Direkt im WLAN",Opacity=.65,Margin=new Thickness(0,5,0,8)});
            var row=new WrapPanel();var accept=new Button{Content="Annehmen",IsEnabled=!active.Contains(transfer.Id)};var reject=new Button{Content="Ablehnen",IsEnabled=!active.Contains(transfer.Id)};
            accept.Click+=async(_,_)=>{if(!active.Add(transfer.Id))return;accept.IsEnabled=false;reject.IsEnabled=false;try{var path=await local.Accept(transfer.Id,DownloadFolder);Status.Text="Gespeichert: "+path;}catch(Exception e){Fail(e);}finally{active.Remove(transfer.Id);RenderInbox();}};
            reject.Click+=async(_,_)=>{try{await local.Reject(transfer.Id);}catch(Exception e){Fail(e);}};row.Children.Add(accept);row.Children.Add(reject);panel.Children.Add(row);Inbox.Children.Add(Wrap(panel));
        }
        if(Inbox.Children.Count==0)Inbox.Children.Add(new TextBlock{Text="Hier ist noch alles ruhig. Neue Sendungen erscheinen automatisch.",Opacity=.6,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,15,0,20)});
    }
    private Border Card(Transfer transfer)
    {
        Notify(transfer.Id,transfer.Filename);
        var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=transfer.Filename,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});panel.Children.Add(new TextBlock{Text=Size(transfer.Size)+" · Serverfreigabe",Opacity=.65,Margin=new Thickness(0,5,0,8)});
        var row=new WrapPanel();var accept=new Button{Content="Annehmen",IsEnabled=!active.Contains(transfer.Id)};var reject=new Button{Content="Ablehnen",IsEnabled=!active.Contains(transfer.Id)};
        accept.Click+=async(_,_)=>{
            if(!active.Add(transfer.Id))return;accept.IsEnabled=false;reject.IsEnabled=false;Progress.Visibility=Visibility.Visible;
            try{using var client=Client();var path=await client.Download(transfer,DownloadFolder,new Progress<double>(p=>Progress.Value=p*100),CancellationToken.None);Status.Text="Gespeichert und geprüft: "+path;if(Found.Children.Contains(panel.Parent as UIElement))Found.Children.Clear();await Refresh();}
            catch(Exception e){Fail(e);}finally{active.Remove(transfer.Id);Progress.Visibility=Visibility.Collapsed;RenderInbox();accept.IsEnabled=true;reject.IsEnabled=true;}
        };
        reject.Click+=async(_,_)=>{if(MessageBox.Show("Diese Serverkopie endgültig löschen?","Robins Share",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;try{using var client=Client();await client.Call<JsonElement>(transfer.AckUrl,HttpMethod.Post,new{});Found.Children.Clear();await Refresh();}catch(Exception e){Fail(e);}};
        row.Children.Add(accept);row.Children.Add(reject);panel.Children.Add(row);return Wrap(panel);
    }
    private Border Wrap(UIElement child)=>new(){Child=child,Style=(Style)FindResource("Card")};
    private void Notify(string id,string name){if(!notified.Add(id))return;tray.BalloonTipTitle="Neue Datei für dich";tray.BalloonTipText=name+" – in Robins Share annehmen oder ablehnen.";tray.ShowBalloonTip(5000);}
    public void AddPaths(IEnumerable<string> values){foreach(var path in values)if(File.Exists(path)&&!paths.Contains(path))paths.Add(path);if(paths.Count>0)Pages.SelectedIndex=0;}
    private void ChooseFiles(object sender,RoutedEventArgs e){var picker=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Title="Dateien zum Senden auswählen"};if(picker.ShowDialog()==true)AddPaths(picker.FileNames);}
    private void ClearSelection(object sender,RoutedEventArgs e){if(!sending)paths.Clear();}
    private async void SendFiles(object sender,RoutedEventArgs e)
    {
        if(sending || paths.Count==0)return;sending=true;SendButton.IsEnabled=false;ClearFiles.IsEnabled=false;CancelButton.IsEnabled=true;Progress.Visibility=Visibility.Visible;ShareResults.Visibility=Visibility.Visible;ShareResults.Text="";transferStop=new();
        var target=Target.SelectedValue as string??"";Target.IsEnabled=false;
        try
        {
            foreach(var path in paths.ToArray())
            {
                Status.Text="Sende "+Path.GetFileName(path)+" …";Progress.Value=0;
                if(target.StartsWith("local:")){await local.QueueOutgoing(path,target[6..],transferStop.Token);ShareResults.Text+=Path.GetFileName(path)+": auf dem iPhone in Empfangen annehmen.\n";}
                else{using var client=Client();var result=await client.Send(path,target==""?null:target,new Progress<double>(p=>Progress.Value=p*100),transferStop.Token);ShareResults.Text+=Path.GetFileName(path)+"\nCode: "+result.Pin+"\n"+result.Link+"\n\n";}
                paths.Remove(path);
            }
            Status.Text=target.StartsWith("local:")?"Bereit zum direkten Abruf. Öffne Empfangen auf deinem iPhone.":"Deine Dateien sind bereit.";
        }
        catch(OperationCanceledException){Status.Text="Senden abgebrochen. Die übrigen Dateien bleiben in deiner Auswahl.";}
        catch(Exception error){Fail(error);}
        finally{sending=false;SendButton.IsEnabled=true;ClearFiles.IsEnabled=true;CancelButton.IsEnabled=false;Target.IsEnabled=true;Progress.Visibility=Visibility.Collapsed;transferStop.Dispose();transferStop=null;}
    }
    private void Cancel(object sender,RoutedEventArgs e)=>transferStop?.Cancel();
    private async void Lookup(object sender,RoutedEventArgs e){try{var code=ReceiveCode.Text;if(code.Length!=6||!code.All(char.IsAsciiDigit))throw new Exception("Bitte sechs Ziffern eingeben.");using var client=Client();var result=await client.Call<Lookup>("/api/lookup/pin/"+code);Found.Children.Clear();Found.Children.Add(Card(result.Transfer));}catch(Exception error){Fail(error);}}
    private async void Pair(object sender,RoutedEventArgs e){try{using var client=Client();await client.Call<JsonElement>("/api/devices/pair",HttpMethod.Post,new{code=PeerCode.Text});PeerCode.Clear();await Refresh();Status.Text="Geräte gekoppelt.";}catch(Exception error){Fail(error);}}
    private async void SaveServer(object sender,RoutedEventArgs e){await Connect();settings.Save();}
    private async void ResetIdentity(object sender,RoutedEventArgs e){if(MessageBox.Show("Neue Serveridentität erstellen? Bestehende Serverkopplungen müssen danach neu verbunden werden.","Robins Share",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;settings.Identities.Remove(settings.Server);settings.Save();await Connect();}
    private void SaveIntegration(object sender,RoutedEventArgs e){try{settings.AutoStart=Autostart.IsChecked==true;settings.ContextMenu=ContextIntegration.IsChecked==true;settings.ApplyIntegration();settings.Save();Status.Text="Windows-Integration gespeichert.";}catch(Exception error){Fail(error);}}
    private void RenewLocal(object sender,RoutedEventArgs e){local.RenewCode();UpdateLocal();}
    private void FileDrop(object sender,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop)&&e.Data.GetData(DataFormats.FileDrop) is string[] p)AddPaths(p);e.Handled=true;}
    private void FileDragOver(object sender,DragEventArgs e){e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?System.Windows.DragDropEffects.Copy:System.Windows.DragDropEffects.None;e.Handled=true;}
    private void ShowSend(object sender,RoutedEventArgs e)=>Pages.SelectedIndex=0;
    private void ShowReceive(object sender,RoutedEventArgs e)=>Pages.SelectedIndex=1;
    private void ShowDevices(object sender,RoutedEventArgs e)=>Pages.SelectedIndex=2;
    private void ShowSettings(object sender,RoutedEventArgs e)=>Pages.SelectedIndex=3;
    private void OpenDownloads(object sender,RoutedEventArgs e)=>OpenFolder();
    private static void OpenFolder(){Directory.CreateDirectory(DownloadFolder);Process.Start(new ProcessStartInfo(DownloadFolder){UseShellExecute=true});}
    private void OpenHotspot(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot"){UseShellExecute=true});
    private void Fail(Exception error){Status.Text=error.Message;MessageBox.Show(error.Message,"Robins Share",MessageBoxButton.OK,MessageBoxImage.Information);}
    private static string Size(long n)=>n>=1073741824?$"{n/1073741824.0:N1} GB":n>=1048576?$"{n/1048576.0:N1} MB":$"{n/1024.0:N1} KB";
}

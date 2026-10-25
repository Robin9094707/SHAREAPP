using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;

namespace RobinShare;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private CancellationTokenSource pipeStop = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException+=(_,eventArgs)=>{
            StartupDiagnostics.Log("Unbehandelter UI-Fehler",eventArgs.Exception);
            StartupDiagnostics.Result(false,"dispatcher",eventArgs.Exception);
            eventArgs.Handled=true;
            if(e.Args.Contains("--startup-smoke-test")){Shutdown(1);return;}
            System.Windows.MessageBox.Show("Robins Share hat einen Fehler festgestellt.\n\n"+eventArgs.Exception.GetBaseException().Message+"\n\nProtokoll: "+StartupDiagnostics.LogFile,"Robins Share");
        };
        AppDomain.CurrentDomain.UnhandledException+=(_,a)=>StartupDiagnostics.Log("Prozessfehler",a.ExceptionObject as Exception);
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { Shutdown(await Task.Run(CoreTests.Run)); return; }
        var resultIndex=Array.IndexOf(e.Args,"--result-file");
        if(resultIndex>=0 && resultIndex+1<e.Args.Length)StartupDiagnostics.ResultFile=e.Args[resultIndex+1];
        StartupDiagnostics.Log("Anwendungsstart");
        var smoke=e.Args.Contains("--startup-smoke-test");
        try
        {
        mutex = new Mutex(true, @"Local\RobinsShare-"+Environment.UserName, out var first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".","RobinsShare-"+Environment.UserName,PipeDirection.Out);
                await pipe.ConnectAsync(3000);
                await new StreamWriter(pipe) { AutoFlush = true }.WriteLineAsync(JsonSerializer.Serialize(e.Args));
            } catch { }
            Shutdown(); return;
        }
        StartupDiagnostics.Log("Hauptfenster wird erstellt");
        var window = new MainWindow(); MainWindow = window;
        if(smoke)
        {
            window.ContentRendered+=async(_,_)=>{
                await Task.Delay(3000);
                if(!window.IsVisible || System.Windows.PresentationSource.FromVisual(window)==null){StartupDiagnostics.Result(false,"window-not-visible");Shutdown(1);return;}
                StartupDiagnostics.Result(true,"window-rendered");
                StartupDiagnostics.Log("Veröffentlichtes Hauptfenster sichtbar");
                Shutdown(0);
            };
        }
        if (!e.Args.Contains("--background")) window.Show();
        window.AddPaths(e.Args.Where(File.Exists));
        _ = Listen(window);
        }
        catch(Exception error)
        {
            StartupDiagnostics.Log("Start fehlgeschlagen",error);StartupDiagnostics.Result(false,"startup",error);
            if(!smoke)System.Windows.MessageBox.Show("Robins Share konnte nicht gestartet werden.\n\n"+error.GetBaseException().Message+"\n\nProtokoll: "+StartupDiagnostics.LogFile,"Robins Share",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private async Task Listen(MainWindow window)
    {
        while (!pipeStop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream("RobinsShare-"+Environment.UserName, PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(pipeStop.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(pipeStop.Token);
                if (line?.Length > 65536) continue;
                var args = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? [];
                await Dispatcher.InvokeAsync(() => { window.AddPaths(args.Where(File.Exists)); window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            } catch (OperationCanceledException) { break; } catch { }
        }
    }
    protected override void OnExit(ExitEventArgs e) { pipeStop.Cancel(); mutex?.Dispose(); base.OnExit(e); }
}

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
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { Shutdown(await Task.Run(CoreTests.Run)); return; }
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
        var window = new MainWindow(); MainWindow = window;
        if (!e.Args.Contains("--background")) window.Show();
        window.AddPaths(e.Args.Where(File.Exists));
        _ = Listen(window);
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

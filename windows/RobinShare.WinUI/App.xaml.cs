using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace RobinShare;

/// WinUI 3 app entrypoint. The XAML compiler generates the native WinUI Main().
public partial class App : Application
{
    private MainWindow? window;
    private Mutex? mutex;
    private readonly CancellationTokenSource pipeStop = new();

    public App()
    {
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Log("Unerwarteter Prozessfehler", e.ExceptionObject as Exception);
        UnhandledException += (_, e) =>
        {
            StartupDiagnostics.Log("WinUI-Fehler", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var resultAt = Array.IndexOf(command, "--result-file");
        if(resultAt >= 0 && resultAt + 1 < command.Length)
            StartupDiagnostics.ResultFile = command[resultAt + 1];

        if(command.Contains("--self-test"))
        {
            _ = Task.Run(() => Environment.Exit(NativeCoreTests.Run()));
            return;
        }

        try
        {
            mutex = new Mutex(true, @"Local\RobinsShare-" + Environment.UserName, out var first);
            if(!first)
            {
                _ = ForwardArguments(command);
                return;
            }

            window = new MainWindow();
            window.AddPaths(command.Where(File.Exists));
            window.Activate();
            if(command.Contains("--background")) window.AppWindow.Hide();
            _ = Listen(window);

            if(command.Contains("--startup-smoke-test"))
                _ = SmokeTest(window);
        }
        catch(Exception exception)
        {
            StartupDiagnostics.Log("Startfehler", exception);
            StartupDiagnostics.Result(false, "winui-startup", exception);
            Environment.Exit(1);
        }
    }

    private static async Task SmokeTest(MainWindow window)
    {
        await Task.Delay(1800);
        // A native WinUI AppWindow was successfully created and activated.
        StartupDiagnostics.Result(true, "winui-window-created");
        Environment.Exit(0);
    }

    private static async Task ForwardArguments(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "RobinsShare-" + Environment.UserName, PipeDirection.Out);
            await pipe.ConnectAsync(2500);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(args));
        }
        catch(Exception exception) { StartupDiagnostics.Log("Instanzweiterleitung", exception); }
        Environment.Exit(0);
    }

    private async Task Listen(MainWindow target)
    {
        while(!pipeStop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    "RobinsShare-" + Environment.UserName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(pipeStop.Token);
                using var reader = new StreamReader(pipe);
                var payload = await reader.ReadLineAsync(pipeStop.Token);
                if(payload is null || payload.Length > 65536) continue;
                var args = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                target.DispatcherQueue.TryEnqueue(() =>
                {
                    target.AddPaths(args.Where(File.Exists));
                    target.AppWindow.Show();
                    target.Activate();
                });
            }
            catch(OperationCanceledException) { break; }
            catch(Exception exception) { StartupDiagnostics.Log("Instanzverbindung", exception); }
        }
    }
}

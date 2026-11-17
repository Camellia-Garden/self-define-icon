using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;

namespace Hanairo;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;
    private readonly CancellationTokenSource stop = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--self-test", var scratch, ..])
        {
            Shutdown(SelfTest.Run(scratch, e.Args.Length == 3 ? e.Args[2] : null));
            return;
        }

        string? target = e.Args is ["--target", var path] && Path.IsPathFullyQualified(path)
            ? path : null;

        try
        {
            string user = WindowsIdentity.GetCurrent().User!.Value;
            string channel = $"CamelliaGarden.Hanairo.{user}";
            instance = new Mutex(initiallyOwned: true, $@"Local\{channel}", out ownsInstance);
            if (!ownsInstance)
            {
                using var client = new NamedPipeClientStream(".", channel, PipeDirection.Out);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(timeout.Token);
                using var writer = new StreamWriter(client);
                await writer.WriteLineAsync(target ?? "");
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (target != null)
                window.RequestTarget(target);

            _ = AcceptRequests(channel, window);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "花色图标工房", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task AcceptRequests(string channel, MainWindow window)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(channel, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server);
                string? request = await reader.ReadLineAsync(stop.Token);
                if (request != null)
                    window.RequestTarget(request);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!stop.IsCancellationRequested)
            {
                await Task.Delay(150);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        stop.Cancel();
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        stop.Dispose();
        base.OnExit(e);
    }
}

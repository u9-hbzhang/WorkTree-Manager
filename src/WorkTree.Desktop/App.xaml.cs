using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WorkTree.Desktop.Explorer;

namespace WorkTree.Desktop;

public partial class App : Application
{
    private readonly Queue<ExplorerRequest> _requests = new();
    private ExplorerCommandServer? _server;
    private DispatcherTimer? _activationTimeout;
    private bool _processingRequests;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var embedded = e.Args.Any(arg => arg.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));
        if (!embedded)
            ShowMainWindow(fromExplorer: false);

        try
        {
            if (embedded)
            {
                _server = new ExplorerCommandServer(EnqueueRequest);
                _activationTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _activationTimeout.Tick += (_, _) => Shutdown();
                _activationTimeout.Start();
            }
            else
                UpdateExplorerServer();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            if (embedded)
                Shutdown(1);
            else
                ((MainWindow)MainWindow).ShowExplorerError(ex.Message);
        }
    }

    internal void UpdateExplorerServer()
    {
        var (enabled, needsRepair) = ExplorerMenu.GetState();
        if (enabled && !needsRepair)
            _server ??= new ExplorerCommandServer(EnqueueRequest);
        else
        {
            _server?.Dispose();
            _server = null;
        }
    }

    private MainWindow ShowMainWindow(bool fromExplorer)
    {
        var window = new MainWindow(fromExplorer);
        MainWindow = window;
        window.Show();
        return window;
    }

    private void EnqueueRequest(ExplorerRequest request) => _ = Dispatcher.InvokeAsync(() =>
    {
        _activationTimeout?.Stop();
        _requests.Enqueue(request);
        if (!_processingRequests)
            _ = ProcessRequestsAsync();
    });

    private async Task ProcessRequestsAsync()
    {
        _processingRequests = true;
        try
        {
            while (_requests.TryDequeue(out var request) && !Dispatcher.HasShutdownStarted)
            {
                var window = MainWindow as MainWindow ?? ShowMainWindow(fromExplorer: true);
                await window.HandleExplorerRequestAsync(request);
            }
        }
        finally { _processingRequests = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationTimeout?.Stop();
        _server?.Dispose();
        base.OnExit(e);
    }
}

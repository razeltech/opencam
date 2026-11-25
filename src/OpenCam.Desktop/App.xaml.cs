using System.Drawing;
using System.IO;
using System.Windows;
using OpenCam.Core.Logging;
using OpenCam.Desktop.Services;
using OpenCam.Security;
using OpenCam.Server;
using OpenCam.Storage;
using OpenCam.VirtualCamera;
using OpenCam.WebRTC;
using Forms = System.Windows.Forms;

namespace OpenCam.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private AppLogger? _logger;
    private SqliteStorageService? _storage;
    private PairingAuthService? _auth;
    private LanCertificateService? _certs;
    private WebRtcSessionManager? _webRtc;
    private VirtualCameraService? _virtualCam;
    private LocalWebServer? _server;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("OPENCAM_SOFTWARE_RENDER") == "1")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "OpenCam_SingleInstance_Mutex", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OpenCam is already running in the system tray.",
                "OpenCam",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _logger = new AppLogger();
        _storage = new SqliteStorageService();
        _auth = new PairingAuthService();
        _certs = new LanCertificateService(_logger);
        _webRtc = new WebRtcSessionManager(_logger);
        _virtualCam = new VirtualCameraService(_logger);
        _server = new LocalWebServer(_logger, _auth, _certs, _storage, _webRtc);

        var settings = await _storage.LoadSettingsAsync();
        ThemeService.ApplyTheme(settings.Theme);

        _mainWindow = new MainWindow(
            _logger,
            _storage,
            _auth,
            _webRtc,
            _virtualCam,
            _server);

        SetupTrayIcon();
        _mainWindow.Show();
        await _mainWindow.InitializeAsync();
    }

    private void SetupTrayIcon()
    {
        Icon? icon = null;
        try
        {
            var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/opencam.ico"))?.Stream;
            if (iconStream is not null)
            {
                icon = new Icon(iconStream);
            }
        }
        catch { }

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "OpenCam",
            Icon = icon ?? SystemIcons.Application,
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open OpenCam", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("Copy Pairing URL", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(_server?.PairingUrl))
            {
                Clipboard.SetText(_server.PairingUrl);
            }
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit OpenCam", null, (_, _) => Dispatcher.Invoke(() => _ = ExitApplicationAsync()));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    public async Task ExitApplicationAsync()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        var port = _server?.BoundPort ?? 0;
        if (_server is not null) await _server.DisposeAsync();
        if (port > 0 && _logger is not null)
        {
            FirewallService.RemovePortRuleOnExit(port, _logger);
        }
        _virtualCam?.Dispose();
        _webRtc?.Dispose();
        _storage?.Dispose();
        _logger?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Shutdown();
    }
}

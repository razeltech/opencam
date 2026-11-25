using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCam.Core.Logging;
using OpenCam.Core.Models;
using OpenCam.Core.Networking;
using OpenCam.Desktop.Services;
using OpenCam.Security;
using OpenCam.Server;
using OpenCam.Storage;
using OpenCam.VirtualCamera;
using OpenCam.WebRTC;

namespace OpenCam.Desktop;

public partial class MainWindow : Window
{
    private readonly AppLogger _logger;
    private readonly SqliteStorageService _storage;
    private readonly PairingAuthService _auth;
    private readonly WebRtcSessionManager _webRtc;
    private readonly VirtualCameraService _virtualCam;
    private readonly LocalWebServer _server;

    private readonly ObservableCollection<LogRowViewModel> _logRows = [];
    private AppSettings _settings = new();
    private WriteableBitmap? _previewBitmap;
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];
    private bool _suppressSelectionEvents;

    public MainWindow(
        AppLogger logger,
        SqliteStorageService storage,
        PairingAuthService auth,
        WebRtcSessionManager webRtc,
        VirtualCameraService virtualCam,
        LocalWebServer server)
    {
        InitializeComponent();
        _logger = logger;
        _storage = storage;
        _auth = auth;
        _webRtc = webRtc;
        _virtualCam = virtualCam;
        _server = server;

        LogsGrid.ItemsSource = _logRows;

        _logger.EntryAdded += OnLogEntryAdded;
        _webRtc.VideoFrameDecoded += OnVideoFrameDecoded;
        _server.DeviceConnected += _ => Dispatcher.Invoke(RefreshDevicesUi);
        _server.DeviceDisconnected += _ => Dispatcher.Invoke(RefreshDevicesUi);
        _server.DeviceUpdated += _ => Dispatcher.Invoke(RefreshDevicesUi);
    }

    public async Task InitializeAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
        LoadLogsFromMemory();
        PopulateSettingsUi();

        _virtualCam.EnsureDriverExtracted();
        RefreshDriverStatus();

        if (!_settings.BlankWhenDisconnected)
        {
            _virtualCam.Start();
        }

        if (_settings.AutoStartServer)
        {
            await StartServerAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private async Task StartServerAsync()
    {
        var adapter = LanNetworkService.SelectPreferredAdapter(_settings.PreferredAdapterId);
        var ip = adapter?.IPv4Address ?? "127.0.0.1";
        var port = LanNetworkService.FindAvailableTcpPort(_settings.PreferredPort);

        if (!FirewallService.IsPortRulePresent(port))
        {
            await FirewallService.EnsurePortAllowedAsync(port, _logger);
        }

        await _server.StartAsync(ip, port, _settings);
        UpdateServerUi(true, ip, port);
    }

    private async void BtnToggleServer_Click(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning)
        {
            await _server.StopAsync();
            UpdateServerUi(false, null, 0);
        }
        else
        {
            await StartServerAsync();
        }
    }

    private void UpdateServerUi(bool running, string? ip, int port)
    {
        if (running && _server.PairingUrl is not null)
        {
            BtnToggleServer.Content = "Stop Server";
            WaitingStatusTitle.Text = $"Waiting for phone on {ip}:{port}...";
            PairingUrlText.Text = _server.PairingUrl;

            var qrPng = QrCodeService.GeneratePngBytes(_server.PairingUrl, 8);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(qrPng);
            bmp.EndInit();
            bmp.Freeze();
            QrCodeImage.Source = bmp;
        }
        else
        {
            BtnToggleServer.Content = "Start Server";
            WaitingStatusTitle.Text = "Server stopped";
            PairingUrlText.Text = "Server offline";
            QrCodeImage.Source = null;
        }
    }

    private void RefreshDriverStatus()
    {
        var registered = VirtualCameraService.IsDriverRegistered();
        var upToDate = _virtualCam.IsDriverUpToDate();
        if (registered && upToDate)
        {
            DriverStatusBadge.Text = "Virtual Cam: OpenCam Ready";
            BtnDriverAction.Content = "OpenCam Driver Ready";
            BtnDriverAction.IsEnabled = false;
        }
        else if (registered)
        {
            DriverStatusBadge.Text = "Virtual Cam: Update Available";
            BtnDriverAction.Content = "Update Virtual Camera Driver";
            BtnDriverAction.IsEnabled = true;
        }
        else
        {
            DriverStatusBadge.Text = "Virtual Cam: Not Registered";
            BtnDriverAction.Content = "Register Virtual Camera";
            BtnDriverAction.IsEnabled = true;
        }
    }

    private async void BtnDriverAction_Click(object sender, RoutedEventArgs e)
    {
        await _virtualCam.RegisterDriverElevatedAsync();
        RefreshDriverStatus();
    }

    private async void BtnUnregisterDriver_Click(object sender, RoutedEventArgs e)
    {
        await _virtualCam.UnregisterDriverElevatedAsync();
        RefreshDriverStatus();
    }

    private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_server.PairingUrl))
        {
            Clipboard.SetText(_server.PairingUrl);
        }
    }

    private void OnVideoFrameDecoded(DecodedVideoFrame frame)
    {
        if (!_virtualCam.IsActive)
        {
            _virtualCam.Start();
        }

        if (frame.DeviceId == _webRtc.ActiveVirtualCamDeviceId)
        {
            _virtualCam.PushFrame(frame.Width, frame.Height, frame.Bgr24Data);
        }

        if (frame.DeviceId != _webRtc.ActiveVirtualCamDeviceId && _webRtc.ActiveVirtualCamDeviceId is not null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_previewBitmap is null ||
                _previewBitmap.PixelWidth != frame.Width ||
                _previewBitmap.PixelHeight != frame.Height)
            {
                _previewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                PreviewImage.Source = _previewBitmap;
            }

            var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
            _previewBitmap.WritePixels(rect, frame.Bgr24Data, frame.Width * 3, 0);
        });
    }

    private void RefreshDevicesUi()
    {
        var devices = _server.GetConnectedDevices();
        _suppressSelectionEvents = true;
        try
        {
            ComboActiveSource.Items.Clear();
            foreach (var d in devices)
            {
                var item = new ComboBoxItem { Content = $"{d.Name} [WiFi]", Tag = d.DeviceId };
                ComboActiveSource.Items.Add(item);
                if (d.DeviceId == _webRtc.ActiveVirtualCamDeviceId)
                {
                    ComboActiveSource.SelectedItem = item;
                }
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        if (devices.Count > 0)
        {
            WaitingView.Visibility = Visibility.Collapsed;
            StreamingView.Visibility = Visibility.Visible;

            var activeDev = devices.FirstOrDefault(d => d.DeviceId == _webRtc.ActiveVirtualCamDeviceId) ?? devices[0];
            var t = activeDev.Telemetry;
            var res = t.Width > 0 ? $"{t.Width}x{t.Height}" : "Negotiating";
            var fps = t.Fps > 0 ? $"{t.Fps:F0} fps" : "-- fps";
            var lat = t.LatencyMs > 0 ? $"{t.LatencyMs:F0} ms" : "-- ms";
            TelemetryPill.Text = $"{res}  |  {fps}  |  {lat}";
        }
        else
        {
            StreamingView.Visibility = Visibility.Collapsed;
            WaitingView.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            _virtualCam.ClearLiveFeed();

            if (_settings.BlankWhenDisconnected)
            {
                _virtualCam.Stop();
            }
        }
    }

    private void ComboActiveSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents) return;
        if (ComboActiveSource.SelectedItem is ComboBoxItem item && item.Tag is string devId)
        {
            _webRtc.SetActiveVirtualCamDevice(devId);
            RefreshDevicesUi();
        }
    }

    private async void ComboQualityTop_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents) return;
        if (ComboQualityTop.SelectedItem is ComboBoxItem item && item.Tag is string resValue)
        {
            var activeId = _webRtc.ActiveVirtualCamDeviceId;
            if (!string.IsNullOrEmpty(activeId))
            {
                await _server.SendCommandAsync(activeId, new CameraCommandPayload
                {
                    Action = "set-resolution",
                    Value = resValue
                });
            }
        }
    }

    private void PopulateSettingsUi()
    {
        _suppressSelectionEvents = true;
        try
        {
            _adapters = LanNetworkService.GetActiveIPv4Adapters();
            ComboAdapters.Items.Clear();
            foreach (var a in _adapters)
            {
                var item = new ComboBoxItem { Content = $"{a.Name} ({a.IPv4Address})", Tag = a.Id };
                ComboAdapters.Items.Add(item);
                if (a.Id == _settings.PreferredAdapterId) ComboAdapters.SelectedItem = item;
            }
            if (ComboAdapters.SelectedIndex < 0 && ComboAdapters.Items.Count > 0)
            {
                ComboAdapters.SelectedIndex = 0;
            }

            TxtPort.Text = _settings.PreferredPort.ToString();
            ComboQualityTop.Items.Clear();
            var dimensions = new (string Label, string Tag)[]
            {
                ("Automatic", "720p"),
                ("640 x 360", "360p"),
                ("640 x 480", "480p"),
                ("960 x 540", "540p"),
                ("1280 x 720", "720p"),
                ("1920 x 1080", "1080p"),
                ("2560 x 1440", "1440p")
            };
            foreach (var (label, tag) in dimensions)
            {
                ComboQualityTop.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
            }
            ComboQualityTop.SelectedIndex = 0;

            ComboTheme.SelectedIndex = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ChkBlankWhenDisconnected.IsChecked = _settings.BlankWhenDisconnected;
            ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
            ChkRequirePin.IsChecked = _settings.RequirePin;
            TxtPin.Text = _settings.PairingPin ?? string.Empty;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private async void ChkQuickOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressSelectionEvents) return;
        _settings.BlankWhenDisconnected = ChkBlankWhenDisconnected.IsChecked == true;
        _settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        await _storage.SaveSettingsAsync(_settings);
        ApplyStartupRegistry(_settings.StartWithWindows);

        if (_server.GetConnectedDevices().Count == 0)
        {
            if (_settings.BlankWhenDisconnected) _virtualCam.Stop();
            else _virtualCam.Start();
        }
    }

    private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var restartNeeded = false;
        if (ComboAdapters.SelectedItem is ComboBoxItem adapterItem && adapterItem.Tag is string adapterId &&
            adapterId != _settings.PreferredAdapterId)
        {
            _settings.PreferredAdapterId = adapterId;
            restartNeeded = true;
        }
        if (int.TryParse(TxtPort.Text, out var port) && port is >= 1024 and <= 65535 && port != _settings.PreferredPort)
        {
            _settings.PreferredPort = port;
            restartNeeded = true;
        }

        _settings.Theme = ComboTheme.SelectedIndex == 1 ? "Light" : "Dark";
        _settings.RequirePin = ChkRequirePin.IsChecked == true;
        _settings.PairingPin = string.IsNullOrWhiteSpace(TxtPin.Text) ? null : TxtPin.Text.Trim();

        await _storage.SaveSettingsAsync(_settings);
        ThemeService.ApplyTheme(_settings.Theme);
        SettingsOverlay.Visibility = Visibility.Collapsed;

        if (restartNeeded && _server.IsRunning)
        {
            await _server.StopAsync();
            await StartServerAsync();
        }
    }

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        PopulateSettingsUi();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        LogsOverlay.Visibility = Visibility.Visible;
    }

    private void BtnCloseOverlays_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        LogsOverlay.Visibility = Visibility.Collapsed;
    }

    private static void ApplyStartupRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("OpenCam", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("OpenCam", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private void LoadLogsFromMemory()
    {
        _logRows.Clear();
        foreach (var entry in _logger.GetEntries())
        {
            _logRows.Add(new LogRowViewModel(entry));
        }
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logRows.Add(new LogRowViewModel(entry));
            if (_logRows.Count > 1000) _logRows.RemoveAt(0);
        });
    }

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(_logger.ExportAsText());

    private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = _logger.LogDirectory, UseShellExecute = true });

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _logger.ClearMemory();
        _logRows.Clear();
    }

    public sealed record LogRowViewModel(LogEntry Entry)
    {
        public string TimeText => Entry.Timestamp.ToString("HH:mm:ss.fff");
        public string Level => Entry.Level.ToString();
        public string Category => Entry.Category;
        public string Message => Entry.Message;
    }
}

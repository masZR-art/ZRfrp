using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using WpfClipboard = System.Windows.Clipboard;
using WpfColor = System.Windows.Media.Color;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCursors = System.Windows.Input.Cursors;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfPoint = System.Windows.Point;

namespace FrpDesktop;

public partial class MainWindow : Window
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex AnsiColorRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex ProxyStartSuccessRegex = new(@"\[(?<name>[^\]]+)\]\s+start proxy success", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly MediaBrush LogTimeBrush = new SolidColorBrush(WpfColor.FromRgb(126, 143, 165));
    private static readonly MediaBrush LogInfoBrush = new SolidColorBrush(WpfColor.FromRgb(209, 250, 229));
    private static readonly MediaBrush LogSuccessBrush = new SolidColorBrush(WpfColor.FromRgb(91, 226, 166));
    private static readonly MediaBrush LogWarningBrush = new SolidColorBrush(WpfColor.FromRgb(251, 113, 133));
    private static readonly MediaBrush LogNoticeBrush = new SolidColorBrush(WpfColor.FromRgb(96, 165, 250));
    private const int MaxLogParagraphs = 600;
    private static readonly TimeSpan LatencyTimeout = TimeSpan.FromMilliseconds(2500);

    private readonly AppSettingsStore _store = new();
    private readonly FrpProcessRunner _runner = new();
    private readonly ZRfrpControlClient _controlClient = new();
    private readonly DesktopUpdateService _updateService = new();
    private readonly SemaphoreSlim _authorizationRefreshGate = new(1, 1);
    private readonly StringBuilder _logBuffer = new();
    private readonly FrpEnvironmentService _environmentService;
    private readonly HashSet<string> _announcedProxyAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Forms.NotifyIcon _trayIcon;
    private TrayMenuWindow? _trayMenuWindow;

    private AppState _state = new();
    private FrpProfile? _selectedProfile;
    private FrpProxy? _editingProxy;
    private string? _detectedFrpcPath;
    private TaskCompletionSource<bool>? _confirmCompletion;
    private CancellationTokenSource? _toastCancellation;
    private bool _isCreatingProxy;
    private bool _isStopping;
    private bool _closingAfterStop;
    private bool _isTestingAllLatency;
    private bool _isReallyExiting;
    private bool _trayDisposed;
    private bool _isLoadingAppSettings;
    private bool _isApplyingProxyToggle;
    private int _proxyEditorSuggestedRemotePort;
    private DesktopUpdateInfo? _desktopUpdate;
    private Window? _floatingPanelWindow;
    private FrameworkElement? _floatingPanelContent;
    private FrameworkElement? _draggingFloatingPanel;
    private FrameworkElement? _draggingFloatingHandle;
    private WpfPoint _floatingPanelDragStart;
    private double _floatingPanelDragStartX;
    private double _floatingPanelDragStartY;

    public MainWindow()
    {
        InitializeComponent();
        _environmentService = new FrpEnvironmentService(_store.AppDataDirectory);
        _trayIcon = CreateTrayIcon();

        _runner.LogReceived += line => Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
        _runner.RunningChanged += running => Dispatcher.BeginInvoke(new Action(() => UpdateRunningState(running)));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _state = _store.Load();
        ApplyNetworkProxySettings();
        ProfilesList.ItemsSource = _state.Profiles;
        ProxyProfileComboBox.ItemsSource = _state.Profiles;

        var selectedProfile = _state.Profiles.FirstOrDefault(profile => profile.Id == _state.LastProfileId)
            ?? _state.Profiles.FirstOrDefault();
        if (selectedProfile is not null)
        {
            ProfilesList.SelectedItem = selectedProfile;
            if (_selectedProfile is null)
            {
                LoadProfile(selectedProfile);
            }
        }
        else
        {
            LoadProfile(null);
        }

        AppendLog($"配置目录：{_store.AppDataDirectory}");
        AppendLog("管理器已就绪。");
        UpdateRunningState(false);
        await CheckDesktopUpdateAsync(showNotification: true);
        await RefreshAuthorizedNodesAsync();
        _ = TestAllProfilesLatencyAsync();
        if (ShouldShowFirstLoginDialog())
        {
            ShowFirstLoginDialog();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingAfterStop || _isReallyExiting)
        {
            DisposeTrayIcon();
            return;
        }

        SaveState();

        if (!_runner.IsRunning && _state.ExitOnCloseWhenDisconnected)
        {
            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse button is released during the call.
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FloatingPanelClose_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDialogs();
    }

    private void FloatingPanelHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle || handle.Tag is not FrameworkElement panel)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source
            && FindVisualParent<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }

        if (_floatingPanelWindow is not null && ReferenceEquals(_floatingPanelContent, panel))
        {
            try
            {
                _floatingPanelWindow.DragMove();
            }
            catch
            {
                // DragMove can throw if the mouse button is released during the call.
            }

            e.Handled = true;
            return;
        }

        var transform = EnsureFloatingPanelTransform(panel);
        _draggingFloatingPanel = panel;
        _draggingFloatingHandle = handle;
        _floatingPanelDragStart = e.GetPosition(this);
        _floatingPanelDragStartX = transform.X;
        _floatingPanelDragStartY = transform.Y;

        handle.CaptureMouse();
        handle.MouseMove += FloatingPanelHeader_MouseMove;
        handle.MouseLeftButtonUp += FloatingPanelHeader_MouseLeftButtonUp;
        e.Handled = true;
    }

    private void FloatingPanelHeader_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_draggingFloatingPanel is null || _draggingFloatingHandle is null || e.LeftButton != MouseButtonState.Pressed)
        {
            StopFloatingPanelDrag();
            return;
        }

        var current = e.GetPosition(this);
        var transform = EnsureFloatingPanelTransform(_draggingFloatingPanel);
        var nextX = _floatingPanelDragStartX + current.X - _floatingPanelDragStart.X;
        var nextY = _floatingPanelDragStartY + current.Y - _floatingPanelDragStart.Y;
        var bounds = GetFloatingPanelBounds(_draggingFloatingPanel);

        transform.X = Math.Clamp(nextX, bounds.minX, bounds.maxX);
        transform.Y = Math.Clamp(nextY, bounds.minY, bounds.maxY);
    }

    private void FloatingPanelHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopFloatingPanelDrag();
    }

    private void StopFloatingPanelDrag()
    {
        if (_draggingFloatingHandle is not null)
        {
            _draggingFloatingHandle.MouseMove -= FloatingPanelHeader_MouseMove;
            _draggingFloatingHandle.MouseLeftButtonUp -= FloatingPanelHeader_MouseLeftButtonUp;
            _draggingFloatingHandle.ReleaseMouseCapture();
        }

        _draggingFloatingPanel = null;
        _draggingFloatingHandle = null;
    }

    private static TranslateTransform EnsureFloatingPanelTransform(FrameworkElement panel)
    {
        if (panel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        panel.RenderTransform = transform;
        return transform;
    }

    private (double minX, double maxX, double minY, double maxY) GetFloatingPanelBounds(FrameworkElement panel)
    {
        var width = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
        var height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
        var visibleHandle = 80d;
        var centerLeft = Math.Max(0, (ActualWidth - width) / 2d);
        var centerTop = Math.Max(0, (ActualHeight - height) / 2d);

        var minX = -centerLeft + visibleHandle - width;
        var maxX = ActualWidth - visibleHandle - centerLeft;
        var minY = -centerTop;
        var maxY = ActualHeight - visibleHandle - centerTop;
        return (minX, maxX, minY, maxY);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        var icon = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? DrawingIcon.ExtractAssociatedIcon(executablePath)
            : null;

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "ZRfrp",
            Visible = true
        };

        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
            }
            else if (e.Button == Forms.MouseButtons.Left && !IsVisible)
            {
                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            }
        };
        return notifyIcon;
    }

    private void HideToTray()
    {
        CloseOpenDialogs();
        Hide();
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayIcon.Text = _runner.IsRunning ? "ZRfrp - 连接运行中" : "ZRfrp - 后台待命";
    }

    private void RestoreFromTray()
    {
        _trayMenuWindow?.Close();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UpdateTrayText();
    }

    private void ShowTrayMenu()
    {
        _trayMenuWindow?.Close();
        _trayMenuWindow = new TrayMenuWindow(
            _state,
            _selectedProfile,
            _runner.IsRunning,
            ToggleProxyFromTrayAsync,
            RestoreFromTray,
            ExitApplicationAsync);
        _trayMenuWindow.Closed += (_, _) => _trayMenuWindow = null;
        _trayMenuWindow.Show();
        _trayMenuWindow.UpdateLayout();

        var cursor = Forms.Cursor.Position;
        _trayMenuWindow.PlaceNear(cursor.X, cursor.Y);
        _trayMenuWindow.Activate();
    }

    private async Task ToggleProxyFromTrayAsync(FrpProfile profile, FrpProxy proxy, bool enabled)
    {
        proxy.Enabled = enabled;
        SaveState();
        UpdateSummary();
        ProxiesList.Items.Refresh();

        var isActiveProfile = profile.Id == _selectedProfile?.Id;
        if (!_runner.IsRunning || !isActiveProfile)
        {
            AppendLog($"节点“{profile.Name}”的通道“{proxy.Name}”已{(enabled ? "启用" : "停用")}。");
            return;
        }

        AppendLog($"节点“{profile.Name}”的通道“{proxy.Name}”已{(enabled ? "启用" : "停用")}，正在重启连接以应用更改。");
        await StopRunnerAsync();
        await StartSelectedProfileAsync(showDialogOnError: false);
    }

    private async Task ExitApplicationAsync()
    {
        if (_isReallyExiting)
        {
            return;
        }

        _isReallyExiting = true;
        SaveState();

        if (_runner.IsRunning)
        {
            await StopRunnerAsync();
        }

        DisposeTrayIcon();
        _closingAfterStop = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayDisposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenuWindow?.Close();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not FrpProfile profile)
        {
            return;
        }

        CloseOpenDialogs();
        LoadProfile(profile);
    }

    private void ProfilesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectItemUnderMouse<FrpProfile>(ProfilesList, e.OriginalSource);
    }

    private void ProxiesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectItemUnderMouse<FrpProxy>(ProxiesList, e.OriginalSource);
    }

    private void ProxiesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProxiesList.SelectedItem is FrpProxy proxy)
        {
            OpenProxyEditor(proxy, isNew: false);
        }
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateBlankProfile();
        _state.Profiles.Add(profile);
        ProfilesList.SelectedItem = profile;
        ProxyProfileComboBox.Items.Refresh();
        SaveState();
        OpenNodeSettings(profile);
        AppendLog("已新增节点。");
        _ = TestProfileLatencyAsync(profile);
    }

    private async void DeleteProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextData<FrpProfile>(sender) is FrpProfile profile)
        {
            await DeleteProfileAsync(profile);
        }
    }

    private void ProfileSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextData<FrpProfile>(sender) is FrpProfile profile)
        {
            ProfilesList.SelectedItem = profile;
            OpenNodeSettings(profile);
        }
    }

    private async void TestAllLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        await TestAllProfilesLatencyAsync();
    }

    private async void RetestProfileLatency_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextData<FrpProfile>(sender) is FrpProfile profile)
        {
            await TestProfileLatencyAsync(profile);
        }
    }

    private async void ConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStopping)
        {
            return;
        }

        if (_runner.IsRunning)
        {
            await StopRunnerAsync();
            return;
        }

        await StartSelectedProfileAsync(showDialogOnError: true);
    }

    private async Task<bool> StartSelectedProfileAsync(bool showDialogOnError)
    {
        var profile = _selectedProfile;
        if (profile is null)
        {
            return false;
        }

        try
        {
            if (profile.ServerManaged)
            {
                await EnsureAuthorizationAsync(profile);
            }
            if (string.IsNullOrWhiteSpace(profile.AccountAccessToken)
                || profile.AccountTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                throw new InvalidOperationException("请先登录控制平台账号。");
            }
            ValidateProfile(profile);
            if (profile.ServerManaged)
            {
                foreach (var proxy in profile.Proxies.Where(proxy => proxy.Enabled))
                {
                    await ApplyServerAllocationAsync(profile, proxy);
                }
                SaveState();
            }
            profile.AdminPort = GetAvailableLoopbackPort();
            var configPath = _store.GetGeneratedConfigPath(profile);
            File.WriteAllText(configPath, FrpConfigSerializer.ToToml(profile), Utf8NoBom);
            NodeGeneratedConfigTextBox.Text = configPath;
            UpdateHeaderSummary();
            AppendLog($"已生成 frpc 配置：{configPath}");

            SetBusy(true);
            AppendLog("正在校验 frpc 配置。");
            var verifyResult = await _runner.VerifyAsync(profile.FrpcPath, configPath, CancellationToken.None);
            if (!verifyResult.Success)
            {
                AppendLog(string.IsNullOrWhiteSpace(verifyResult.Output) ? "配置校验失败。" : verifyResult.Output);
                if (showDialogOnError)
                {
                    await ShowConfirmAsync("配置校验失败", "frpc 配置校验失败，请查看运行日志。");
                }

                return false;
            }

            if (!string.IsNullOrWhiteSpace(verifyResult.Output))
            {
                AppendLog(verifyResult.Output);
            }

            _announcedProxyAddresses.Clear();
            _runner.Start(profile.FrpcPath, configPath);
            return true;
        }
        catch (AccountAuthorizationRequiredException exception)
        {
            InvalidateAccountAuthorization();
            AppendLog(exception.Message);
            if (showDialogOnError)
            {
                await ShowConfirmAsync("需要重新登录", exception.Message);
                ShowFirstLoginDialog();
            }

            return false;
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message);
            if (showDialogOnError)
            {
                await ShowConfirmAsync("启动失败", exception.Message);
            }

            return false;
        }
        finally
        {
            SetBusy(false);
            UpdateRunningState(_runner.IsRunning);
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "导入 frpc.toml",
            Filter = "FRP 配置 (*.toml;*.ini)|*.toml;*.ini|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var frpcPath = ResolveFrpcPathForImportedConfig(dialog.FileName);
            var profileName = Path.GetFileNameWithoutExtension(dialog.FileName);
            var toml = File.ReadAllText(dialog.FileName);
            var profile = FrpConfigSerializer.FromToml(toml, frpcPath, profileName);
            _state.Profiles.Add(profile);
            ProfilesList.SelectedItem = profile;
            ProxyProfileComboBox.Items.Refresh();
            SaveState();
            AppendLog($"已导入配置：{dialog.FileName}");
        }
        catch (Exception exception)
        {
            _ = ShowConfirmAsync("导入失败", exception.Message);
        }
    }

    private void ImportNodesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "导入 ZRfrp 节点配置",
            Filter = "ZRfrp 节点配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<NodeExportDocument>(
                File.ReadAllText(dialog.FileName),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (document is null || !string.Equals(document.Kind, "zrfrp-node-export", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("这不是有效的 ZRfrp 节点配置文件。");
            }

            var imported = ImportNodeDocument(document, null);
            SaveState();
            AppendLog(imported == 0
                ? "节点配置已导入，没有发现新的节点。"
                : $"节点配置已导入，新增 {imported} 个节点。");
        }
        catch (Exception exception)
        {
            _ = ShowConfirmAsync("导入失败", exception.Message);
        }
    }

    private int ImportNodeDocument(NodeExportDocument document, ClientAccountSession? session)
    {
        var imported = 0;
        var matchedProfileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.ServerAddress) || node.ServerPort <= 0)
            {
                continue;
            }

            var controlUrl = string.IsNullOrWhiteSpace(node.ControlApiUrl)
                ? document.PlatformUrl
                : node.ControlApiUrl;
            var normalizedName = NormalizeImportedNodeName(node, _state.Profiles.Count + 1);
            var candidates = _state.Profiles.Where(profile =>
                profile.ServerManaged && !matchedProfileIds.Contains(profile.Id)).ToArray();
            var profile = candidates.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(profile.ManagedNodeId)
                    && profile.ManagedNodeId.Equals(node.Id, StringComparison.Ordinal))
                ?? candidates.FirstOrDefault(profile =>
                    profile.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(profile =>
                    profile.NameWithoutFlag.Equals(
                        new FrpProfile { Name = normalizedName }.NameWithoutFlag,
                        StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(profile =>
                    profile.ServerAddr.Equals(node.ServerAddress, StringComparison.OrdinalIgnoreCase)
                    && profile.ServerPort == node.ServerPort);
            if (profile is null)
            {
                profile = new FrpProfile
                {
                    FrpcPath = _state.ClientFrpcPath,
                    ServerManaged = true
                };
                _state.Profiles.Add(profile);
                imported++;
            }

            profile.ManagedNodeId = node.Id;
            profile.Name = normalizedName;
            profile.ServerAddr = node.ServerAddress;
            profile.ServerPort = node.ServerPort;
            profile.Token = node.FrpToken;
            profile.ServerManaged = true;
            profile.ControlApiUrl = controlUrl;
            if (session is not null)
            {
                profile.AccountId = session.AccountId;
                profile.AccountAccessToken = session.AccessToken;
                profile.AccountTokenExpiresAt = session.ExpiresAt;
                profile.AccountRefreshToken = session.RefreshToken;
                profile.AccountRefreshExpiresAt = session.RefreshExpiresAt;
            }
            matchedProfileIds.Add(profile.Id);
        }

        if (_selectedProfile is null && _state.Profiles.Count > 0)
        {
            ProfilesList.SelectedItem = _state.Profiles[0];
            LoadProfile(_state.Profiles[0]);
        }

        ProxyProfileComboBox.Items.Refresh();
        return imported;
    }

    private static string NormalizeImportedNodeName(NodeExportEntry node, int index)
    {
        var name = (node.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 18
            || Regex.IsMatch(name, @"^[A-Za-z0-9_-]{16,}$"))
        {
            var address = string.IsNullOrWhiteSpace(node.ServerAddress)
                ? index.ToString()
                : node.ServerAddress;
            return $"节点-{address}";
        }

        return name;
    }

    private void BrowseFrpcButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择 frpc.exe",
            Filter = "frpc.exe|frpc.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            NodeFrpcPathTextBox.Text = dialog.FileName;
        }
    }

    private async void OpenAppSettings_Click(object sender, RoutedEventArgs e)
    {
        _isLoadingAppSettings = true;
        ExitOnCloseWhenDisconnectedCheckBox.IsChecked = _state.ExitOnCloseWhenDisconnected;
        SelectComboBoxItemByTag(NetworkProxyModeComboBox, _state.NetworkProxyMode);
        SelectComboBoxItemByContent(NetworkProxyTypeComboBox, _state.NetworkProxyType);
        NetworkProxyHostTextBox.Text = _state.NetworkProxyHost;
        NetworkProxyPortTextBox.Text = _state.NetworkProxyPort > 0 ? _state.NetworkProxyPort.ToString() : "";
        NetworkProxyUsernameTextBox.Text = _state.NetworkProxyUsername;
        NetworkProxyPasswordBox.Password = _state.NetworkProxyPassword;
        PlatformUrlTextBox.Text = _state.PlatformUrl;
        AccountUsernameTextBox.Text = _state.AccountUsername;
        AccountPasswordBox.Password = "";
        UpdateAccountStatus();
        UpdateNetworkProxyControls();
        _isLoadingAppSettings = false;
        ShowOverlayPanel(AppSettingsDialogPanel);
        await DetectFrpcEnvironmentAsync();
    }

    private async void AccountLoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await LoginAndSyncNodesAsync(
                PlatformUrlTextBox.Text.Trim(),
                AccountUsernameTextBox.Text.Trim(),
                AccountPasswordBox.Password);
            AccountPasswordBox.Password = "";
            SaveState();
            UpdateAccountStatus();
            AppendLog($"控制平台账号“{session.Username}”登录成功。");
        }
        catch (Exception exception)
        {
            AccountStatusText.Text = exception.Message;
            AccountStatusText.Foreground = LogWarningBrush;
        }
    }

    private void UpdateAccountStatus()
    {
        var accessValid = HasUsableAccessToken(TimeSpan.Zero);
        var refreshValid = HasUsableRefreshToken();
        var isLoggedIn = accessValid || refreshValid;
        AccountStatusText.Text = accessValid
            ? $"已登录：{_state.AccountUsername}，授权有效至 {_state.AccountTokenExpiresAt.LocalDateTime:g}"
            : refreshValid
                ? $"已登录：{_state.AccountUsername}，访问授权将在联网后自动续期。"
                : "未登录，登录后才能获取服务授权并启动连接。";
        AccountStatusText.Foreground = isLoggedIn ? LogSuccessBrush : LogWarningBrush;
        AccountLoginFormPanel.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountLogoutButton.Visibility = isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AccountLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning)
        {
            await StopRunnerAsync();
        }

        foreach (var profile in _state.Profiles.Where(profile => profile.ServerManaged))
        {
            profile.AccountId = "";
            profile.AccountAccessToken = "";
            profile.AccountTokenExpiresAt = default;
            profile.AccountRefreshToken = "";
            profile.AccountRefreshExpiresAt = default;
        }

        ClearCentralAccountSession();

        _state.PlatformUrl = "";
        _state.AccountUsername = "";
        _state.AccountLoginSkipped = false;
        _selectedProfile = _state.Profiles.FirstOrDefault();
        ProfilesList.SelectedItem = _selectedProfile;
        ProxyProfileComboBox.Items.Refresh();
        LoadProfile(_selectedProfile);
        SaveState();
        UpdateAccountStatus();
        CloseOpenDialogs();
        ShowFirstLoginDialog();
        AppendLog("已退出控制平台账号，节点和隧道配置已保留，重新登录后可继续使用。");
    }

    private bool ShouldShowFirstLoginDialog()
    {
        if (_state.AccountLoginSkipped)
        {
            return false;
        }

        return !HasUsableAccessToken(TimeSpan.Zero) && !HasUsableRefreshToken();
    }

    private void ShowFirstLoginDialog()
    {
        FirstLoginPlatformUrlTextBox.Text = _state.PlatformUrl;
        FirstLoginUsernameTextBox.Text = _state.AccountUsername;
        FirstLoginPasswordBox.Password = "";
        FirstLoginErrorText.Text = "";
        ShowOverlayPanel(FirstLoginDialogPanel);
    }

    private async void FirstLoginAccept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = await LoginAndSyncNodesAsync(
                FirstLoginPlatformUrlTextBox.Text.Trim(),
                FirstLoginUsernameTextBox.Text.Trim(),
                FirstLoginPasswordBox.Password);
            FirstLoginPasswordBox.Password = "";
            SaveState();
            CloseOpenDialogs();
            AppendLog($"控制平台账号“{session.Username}”登录成功。");
        }
        catch (Exception exception)
        {
            FirstLoginErrorText.Text = exception.Message;
        }
    }

    private void FirstLoginSkip_Click(object sender, RoutedEventArgs e)
    {
        _state.AccountLoginSkipped = true;
        SaveState();
        CloseOpenDialogs();
    }

    private async Task<ClientAccountSession> LoginAndSyncNodesAsync(string platformUrl, string username, string password)
    {
        var normalizedPlatformUrl = ZRfrpControlClient.NormalizePlatformUrl(platformUrl);
        ClientAccountSession session;
        try
        {
            session = await _controlClient.LoginAsync(
                normalizedPlatformUrl, username, password, Environment.MachineName);
        }
        catch (System.Net.Http.HttpRequestException) when (!platformUrl.Contains("://", StringComparison.Ordinal))
        {
            normalizedPlatformUrl = "http://" + platformUrl.Trim().TrimEnd('/');
            session = await _controlClient.LoginAsync(
                normalizedPlatformUrl, username, password, Environment.MachineName);
        }
        _state.PlatformUrl = normalizedPlatformUrl;
        _state.AccountUsername = session.Username;
        _state.AccountLoginSkipped = false;
        ApplyAccountSession(session, normalizedPlatformUrl);
        SaveState();

        try
        {
            var document = await _controlClient.ExportNodesAsync(normalizedPlatformUrl, session.AccessToken);
            var imported = ImportNodeDocument(document, session);
            ApplyAccountSession(session, normalizedPlatformUrl);
            SaveState();
            AppendLog(imported == 0 ? "节点配置已是最新。" : $"已自动载入 {imported} 个节点。");
        }
        catch (ControlApiException exception)
            when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateAccountAuthorization();
            throw new AccountAuthorizationRequiredException("登录会话未能通过控制平台验证，请重新登录。", exception);
        }
        catch (Exception exception)
        {
            AppendLog($"账号登录成功，但节点同步暂时失败：{exception.Message}");
        }

        if (_selectedProfile is null && _state.Profiles.Count > 0)
        {
            ProfilesList.SelectedItem = _state.Profiles[0];
            LoadProfile(_state.Profiles[0]);
        }

        return session;
    }

    private async Task RefreshAuthorizedNodesAsync()
    {
        var source = _state.Profiles.FirstOrDefault(profile => profile.ServerManaged);
        var platformUrl = string.IsNullOrWhiteSpace(_state.PlatformUrl)
            ? source?.ControlApiUrl ?? ""
            : _state.PlatformUrl;
        if (string.IsNullOrWhiteSpace(platformUrl)
            || (!HasUsableAccessToken(TimeSpan.Zero) && !HasUsableRefreshToken()))
        {
            return;
        }

        try
        {
            await EnsureAuthorizationAsync(source);
            var rejectedAccessToken = _state.AccountAccessToken;
            NodeExportDocument document;
            try
            {
                document = await _controlClient.ExportNodesAsync(platformUrl, rejectedAccessToken);
            }
            catch (ControlApiException exception)
                when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await EnsureAuthorizationAsync(source, forceRefresh: true, rejectedAccessToken);
                document = await _controlClient.ExportNodesAsync(platformUrl, _state.AccountAccessToken);
            }
            var session = CurrentAccountSession();
            ImportNodeDocument(document, session);
            ApplyAccountSession(session, platformUrl);
            SaveState();
            AppendLog("已从控制平台刷新节点地址与认证信息。");
        }
        catch (AccountAuthorizationRequiredException exception)
        {
            InvalidateAccountAuthorization();
            AppendLog(exception.Message);
        }
        catch (ControlApiException exception)
            when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateAccountAuthorization();
            AppendLog("控制平台登录会话已失效，请重新登录。");
        }
        catch (Exception exception)
        {
            AppendLog($"节点自动同步失败：{exception.Message}");
        }
    }

    private async Task EnsureAuthorizationAsync(
        FrpProfile? profile, bool forceRefresh = false, string? rejectedAccessToken = null)
    {
        if (!forceRefresh && HasUsableAccessToken(TimeSpan.FromMinutes(5)))
        {
            SynchronizeProfileAuthorization();
            return;
        }

        await _authorizationRefreshGate.WaitAsync();
        try
        {
            if (!forceRefresh && HasUsableAccessToken(TimeSpan.FromMinutes(5)))
            {
                SynchronizeProfileAuthorization();
                return;
            }
            if (forceRefresh
                && !string.IsNullOrWhiteSpace(rejectedAccessToken)
                && !_state.AccountAccessToken.Equals(rejectedAccessToken, StringComparison.Ordinal)
                && HasUsableAccessToken(TimeSpan.FromMinutes(1)))
            {
                SynchronizeProfileAuthorization();
                return;
            }
            if (!HasUsableRefreshToken())
            {
                throw new AccountAuthorizationRequiredException("登录会话已失效，请重新登录。");
            }

            var platformUrl = string.IsNullOrWhiteSpace(_state.PlatformUrl)
                ? profile?.ControlApiUrl ?? "" : _state.PlatformUrl;
            if (string.IsNullOrWhiteSpace(platformUrl))
            {
                throw new AccountAuthorizationRequiredException("控制平台地址缺失，请重新登录。");
            }

            ClientAccountSession refreshed;
            try
            {
                refreshed = await _controlClient.RefreshAsync(
                    platformUrl, _state.AccountRefreshToken, Environment.MachineName);
            }
            catch (ControlApiException exception)
                when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new AccountAuthorizationRequiredException("登录会话已失效，请重新登录。", exception);
            }

            ApplyAccountSession(refreshed, platformUrl);
            SaveState();
            AppendLog("控制平台登录授权已自动续期。");
        }
        finally
        {
            _authorizationRefreshGate.Release();
        }
    }

    private void ApplyAccountSession(ClientAccountSession session, string platformUrl)
    {
        _state.AccountId = session.AccountId;
        _state.AccountUsername = session.Username;
        _state.AccountAccessToken = session.AccessToken;
        _state.AccountTokenExpiresAt = session.ExpiresAt;
        _state.AccountRefreshToken = session.RefreshToken;
        _state.AccountRefreshExpiresAt = session.RefreshExpiresAt;
        _state.PlatformUrl = platformUrl;
        SynchronizeProfileAuthorization();
    }

    private void SynchronizeProfileAuthorization()
    {
        foreach (var profile in _state.Profiles.Where(item => item.ServerManaged))
        {
            profile.AccountId = _state.AccountId;
            profile.AccountAccessToken = _state.AccountAccessToken;
            profile.AccountTokenExpiresAt = _state.AccountTokenExpiresAt;
            profile.AccountRefreshToken = _state.AccountRefreshToken;
            profile.AccountRefreshExpiresAt = _state.AccountRefreshExpiresAt;
            profile.ControlApiUrl = _state.PlatformUrl;
        }
    }

    private bool HasUsableAccessToken(TimeSpan minimumRemaining) =>
        !string.IsNullOrWhiteSpace(_state.AccountAccessToken)
        && _state.AccountTokenExpiresAt > DateTimeOffset.UtcNow.Add(minimumRemaining);

    private bool HasUsableRefreshToken() =>
        !string.IsNullOrWhiteSpace(_state.AccountRefreshToken)
        && _state.AccountRefreshExpiresAt > DateTimeOffset.UtcNow;

    private ClientAccountSession CurrentAccountSession() => new(
        _state.AccountId,
        _state.AccountUsername,
        _state.AccountAccessToken,
        _state.AccountTokenExpiresAt,
        _state.AccountRefreshToken,
        _state.AccountRefreshExpiresAt,
        "",
        0,
        "",
        0,
        0);

    private void ClearCentralAccountSession()
    {
        _state.AccountId = "";
        _state.AccountAccessToken = "";
        _state.AccountTokenExpiresAt = default;
        _state.AccountRefreshToken = "";
        _state.AccountRefreshExpiresAt = default;
    }

    private void InvalidateAccountAuthorization()
    {
        ClearCentralAccountSession();
        SynchronizeProfileAuthorization();
        SaveState();
        UpdateAccountStatus();
    }

    private async Task CheckDesktopUpdateAsync(bool showNotification)
    {
        try
        {
            _desktopUpdate = await _updateService.CheckAsync();
            VersionButton.Content = _desktopUpdate.UpdateAvailable
                ? $"ZRfrp v{_desktopUpdate.CurrentVersion} · 可更新"
                : $"ZRfrp v{_desktopUpdate.CurrentVersion}";
            if (_desktopUpdate.UpdateAvailable && showNotification)
            {
                ShowToast($"发现新版本 v{_desktopUpdate.LatestVersion}，点击左下角版本号更新。");
            }
        }
        catch
        {
            VersionButton.Content = $"ZRfrp v{DesktopUpdateService.CurrentVersion}";
        }
    }

    private async void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckDesktopUpdateAsync(showNotification: false);
        if (_desktopUpdate?.UpdateAvailable != true)
        {
            ShowToast("当前已是最新版本。");
            return;
        }
        if (!await ShowConfirmAsync(
                "更新 ZRfrp",
                $"下载并安装 v{_desktopUpdate.LatestVersion}？软件会停止当前连接并重新启动。"))
        {
            return;
        }
        try
        {
            if (_runner.IsRunning)
            {
                await StopRunnerAsync();
            }
            await _updateService.DownloadAndApplyAsync(_desktopUpdate, _store.AppDataDirectory);
            _isReallyExiting = true;
            _floatingPanelWindow?.Close();
            DisposeTrayIcon();
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception exception)
        {
            await ShowConfirmAsync("更新失败", exception.Message);
        }
    }

    private void CloseAppSettings_Click(object sender, RoutedEventArgs e)
    {
        _state.ExitOnCloseWhenDisconnected = ExitOnCloseWhenDisconnectedCheckBox.IsChecked == true;
        SaveNetworkProxySettings();
        SaveState();
        CloseOpenDialogs();
    }

    private void ExitOnCloseWhenDisconnectedChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _state.ExitOnCloseWhenDisconnected = ExitOnCloseWhenDisconnectedCheckBox.IsChecked == true;
        SaveState();
    }

    private void NetworkProxyChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingAppSettings)
        {
            return;
        }

        SaveNetworkProxySettings();
    }

    private void NetworkProxyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isLoadingAppSettings)
        {
            return;
        }

        SaveNetworkProxySettings();
    }

    private void NetworkProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingAppSettings)
        {
            return;
        }

        SaveNetworkProxySettings();
    }

    private void SaveNetworkProxySettings()
    {
        _state.NetworkProxyMode = GetSelectedComboBoxTag(NetworkProxyModeComboBox, "none");
        _state.NetworkProxyType = GetSelectedComboBoxContent(NetworkProxyTypeComboBox, "HTTP");
        _state.NetworkProxyHost = NetworkProxyHostTextBox.Text.Trim();
        _state.NetworkProxyPort = int.TryParse(NetworkProxyPortTextBox.Text.Trim(), out var port) ? port : 0;
        _state.NetworkProxyUsername = NetworkProxyUsernameTextBox.Text.Trim();
        _state.NetworkProxyPassword = NetworkProxyPasswordBox.Password;
        UpdateNetworkProxyControls();
        ApplyNetworkProxySettings();
        SaveState();
    }

    private void ApplyNetworkProxySettings()
    {
        var options = new NetworkProxyOptions(
            _state.NetworkProxyMode,
            _state.NetworkProxyType,
            _state.NetworkProxyHost,
            _state.NetworkProxyPort,
            _state.NetworkProxyUsername,
            _state.NetworkProxyPassword);
        _environmentService.ConfigureProxy(options);
        _controlClient.ConfigureProxy(options);
    }

    private void UpdateNetworkProxyControls()
    {
        var mode = GetSelectedComboBoxTag(NetworkProxyModeComboBox, _state.NetworkProxyMode);
        var manual = mode == "manual";
        ManualProxyPanel.IsEnabled = manual;
        ManualProxyPanel.Opacity = manual ? 1 : 0.46;

        switch (mode)
        {
            case "system":
                NetworkProxyStatusDot.Fill = BrushFromHex("#60A5FA");
                NetworkProxyStatusText.Text = "自动检测系统代理，软件联网会跟随 Windows 当前代理配置。";
                break;
            case "manual":
                NetworkProxyStatusDot.Fill = string.IsNullOrWhiteSpace(NetworkProxyHostTextBox.Text) || !int.TryParse(NetworkProxyPortTextBox.Text, out _)
                    ? BrushFromHex("#F59E0B")
                    : BrushFromHex("#17C964");
                NetworkProxyStatusText.Text = "使用手动代理。主机和端口填写完整后，自动下载会通过该代理访问网络。";
                break;
            default:
                NetworkProxyStatusDot.Fill = BrushFromHex("#94A3B8");
                NetworkProxyStatusText.Text = "不使用代理，软件联网会绕过系统代理。";
                break;
        }
    }

    private static void SelectComboBoxItemByTag(WpfComboBox comboBox, string? tag)
    {
        var normalizedTag = string.IsNullOrWhiteSpace(tag) ? "none" : tag;
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalizedTag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 1;
    }

    private static void SelectComboBoxItemByContent(WpfComboBox comboBox, string? content)
    {
        var normalizedContent = string.IsNullOrWhiteSpace(content) ? "HTTP" : content;
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), normalizedContent, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string GetSelectedComboBoxTag(WpfComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private static string GetSelectedComboBoxContent(WpfComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Content is not null
            ? item.Content.ToString() ?? fallback
            : fallback;
    }

    private async void DetectFrpcButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectFrpcEnvironmentAsync();
    }

    private async Task DetectFrpcEnvironmentAsync()
    {
        SetEnvironmentOperationState(isBusy: true, isDownloading: false);
        SetEnvironmentStatus("正在检测", "正在检查节点配置、应用目录、系统 PATH 和下载目录。", "#F59E0B");

        try
        {
            var configuredPaths = _state.Profiles
                .Select(profile => profile.FrpcPath)
                .Prepend(_state.ClientFrpcPath)
                .ToArray();
            var detectedPath = await _environmentService.DetectAsync(configuredPaths);

            if (string.IsNullOrWhiteSpace(detectedPath))
            {
                _detectedFrpcPath = null;
                FrpcEnvironmentPathTextBox.Text = "";
                SetEnvironmentStatus(
                    "未找到 frpc",
                    "可以选择已有的 frpc.exe，或使用右侧按钮自动下载安装。",
                    "#D64545");
                return;
            }

            await SetDetectedFrpcAsync(detectedPath, "已识别可用环境");
        }
        catch (Exception exception)
        {
            _detectedFrpcPath = null;
            SetEnvironmentStatus("检测失败", exception.Message, "#D64545");
        }
        finally
        {
            SetEnvironmentOperationState(isBusy: false, isDownloading: false);
        }
    }

    private async void ChooseEnvironmentFrpcButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择 frpc.exe",
            Filter = "frpc.exe|frpc.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await SetDetectedFrpcAsync(dialog.FileName, "已选择可用环境");
            SetEnvironmentOperationState(isBusy: false, isDownloading: false);
        }
    }

    private async void InstallFrpcButton_Click(object sender, RoutedEventArgs e)
    {
        SetEnvironmentOperationState(isBusy: true, isDownloading: true);
        SetEnvironmentStatus("正在下载安装", "正在从 FRP 官方 GitHub 发布页获取 Windows AMD64 客户端。", "#F59E0B");
        EnvironmentProgressBar.Value = 0;
        EnvironmentProgressText.Text = "准备下载...";
        var progress = new Progress<int>(value =>
        {
            EnvironmentProgressBar.Value = value;
            EnvironmentProgressText.Text = value < 100 ? $"下载中 {value}%" : "正在完成安装...";
        });

        try
        {
            var result = await _environmentService.InstallLatestAsync(progress);
            await SetDetectedFrpcAsync(result.FrpcPath, $"FRP {result.Version} 已安装");
            ApplyFrpcEnvironmentToAllProfiles(result.FrpcPath);
            EnvironmentProgressText.Text = "安装完成，已应用到全部节点。";
            AppendLog($"frpc {result.Version} 已安装：{result.FrpcPath}");
        }
        catch (Exception exception)
        {
            SetEnvironmentStatus("安装失败", exception.Message, "#D64545");
            EnvironmentProgressText.Text = "安装未完成。";
            AppendLog($"frpc 自动安装失败：{exception.Message}");
        }
        finally
        {
            SetEnvironmentOperationState(isBusy: false, isDownloading: true);
        }
    }

    private void ApplyFrpcEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_detectedFrpcPath) && File.Exists(_detectedFrpcPath))
        {
            ApplyFrpcEnvironmentToAllProfiles(_detectedFrpcPath);
        }

        _state.ExitOnCloseWhenDisconnected = ExitOnCloseWhenDisconnectedCheckBox.IsChecked == true;
        SaveNetworkProxySettings();
        SaveState();
        AppendLog("软件设置已保存并应用到全部节点。");
        CloseOpenDialogs();
    }

    private async Task SetDetectedFrpcAsync(string path, string title)
    {
        _detectedFrpcPath = Path.GetFullPath(path);
        FrpcEnvironmentPathTextBox.Text = _detectedFrpcPath;
        var version = await _environmentService.GetVersionAsync(_detectedFrpcPath);
        ApplyFrpcEnvironmentToAllProfiles(_detectedFrpcPath);
        var detail = string.IsNullOrWhiteSpace(version)
            ? "frpc.exe 可访问，已设为全部节点的默认客户端。"
            : $"版本 {version}，已设为全部节点的默认客户端。";
        SetEnvironmentStatus(title, detail, "#17C964");
    }

    private void ApplyFrpcEnvironmentToAllProfiles(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _state.ClientFrpcPath = fullPath;
        foreach (var profile in _state.Profiles)
        {
            profile.FrpcPath = fullPath;
        }

        if (_selectedProfile is not null)
        {
            NodeFrpcPathTextBox.Text = fullPath;
        }

        SaveState();
    }

    private void SetEnvironmentOperationState(bool isBusy, bool isDownloading)
    {
        DetectFrpcButton.IsEnabled = !isBusy;
        ChooseEnvironmentFrpcButton.IsEnabled = !isBusy;
        InstallFrpcButton.IsEnabled = !isBusy;
        ApplyFrpcEnvironmentButton.IsEnabled = !isBusy;
        EnvironmentProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentProgressText.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetEnvironmentStatus(string title, string detail, string color)
    {
        EnvironmentStatusTitle.Text = title;
        EnvironmentStatusDetail.Text = detail;
        EnvironmentStatusDot.Fill = BrushFromHex(color);
    }

    private void OpenGeneratedFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(NodeGeneratedConfigTextBox.Text);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _store.GeneratedConfigDirectory;
        }

        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        OpenProxyEditor(CreateDefaultEditorProxy(), isNew: true);
    }

    private void EditProxyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextData<FrpProxy>(sender) is FrpProxy proxy)
        {
            ProxiesList.SelectedItem = proxy;
            OpenProxyEditor(proxy, isNew: false);
        }
    }

    private async void RemoveProxyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextData<FrpProxy>(sender) is FrpProxy proxy)
        {
            ProxiesList.SelectedItem = proxy;
            await RemoveProxyAsync(proxy);
        }
    }

    private async void ProxyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingProxyToggle)
        {
            return;
        }
        var proxy = (sender as FrameworkElement)?.DataContext as FrpProxy;
        var previousEnabled = proxy is not null && !proxy.Enabled;
        SaveState();
        UpdateSummary();
        ProxiesList.Items.Refresh();
        if (!_runner.IsRunning || proxy is null || _selectedProfile is null)
        {
            return;
        }

        try
        {
            _isApplyingProxyToggle = true;
            await ReloadSelectedProfileAsync(proxy.Enabled ? proxy : null);
            AppendLog($"隧道“{proxy.Name}”已{(proxy.Enabled ? "热开启" : "热关闭")}，节点连接保持运行。");
        }
        catch (Exception exception)
        {
            proxy.Enabled = previousEnabled;
            SaveState();
            ProxiesList.Items.Refresh();
            AppendLog($"隧道热切换失败，已恢复原状态：{exception.Message}");
            await ShowConfirmAsync("热切换失败", exception.Message);
        }
        finally
        {
            _isApplyingProxyToggle = false;
        }
    }

    private async Task ReloadSelectedProfileAsync(FrpProxy? newlyEnabledProxy)
    {
        var profile = _selectedProfile ?? throw new InvalidOperationException("当前节点不存在。");
        if (newlyEnabledProxy is not null && profile.ServerManaged)
        {
            await ApplyServerAllocationAsync(profile, newlyEnabledProxy);
        }
        var configPath = _store.GetGeneratedConfigPath(profile);
        File.WriteAllText(configPath, FrpConfigSerializer.ToToml(profile), Utf8NoBom);
        var verify = await _runner.VerifyAsync(profile.FrpcPath, configPath, CancellationToken.None);
        if (!verify.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(verify.Output)
                ? "frpc 配置校验失败。" : verify.Output);
        }
        var reload = await _runner.ReloadAsync(profile.FrpcPath, configPath);
        if (!reload.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reload.Output)
                ? "frpc 热重载失败。" : reload.Output);
        }
        SaveState();
    }

    private async void SaveProxyEdit_Click(object sender, RoutedEventArgs e)
    {
        ProxyEditorErrorText.Text = "";

        try
        {
            var editedProxy = ReadProxyFromEditor();
            var editorProfile = ProxyProfileComboBox.SelectedItem as FrpProfile ?? _selectedProfile
                ?? throw new InvalidOperationException("请选择部署节点。");
            ValidateProxy(editedProxy, editorProfile.ServerManaged);

            if (_isCreatingProxy)
            {
                if (ProxyProfileComboBox.SelectedItem is not FrpProfile targetProfile)
                {
                    throw new InvalidOperationException("请选择部署节点。");
                }

                targetProfile = await ApplyBestServerAllocationAsync(targetProfile, editedProxy);
                targetProfile.Proxies.Add(editedProxy);
                ProfilesList.SelectedItem = targetProfile;
                ProxiesList.SelectedItem = editedProxy;
            }
            else if (_editingProxy is not null)
            {
                var targetProfile = ProxyProfileComboBox.SelectedItem as FrpProfile ?? _selectedProfile
                    ?? throw new InvalidOperationException("找不到隧道所属节点。");
                editedProxy.Id = _editingProxy.Id;
                editedProxy.AllocationId = _editingProxy.AllocationId;
                editedProxy.RemotePortLocked = _editingProxy.RemotePortLocked;
                await ApplyServerAllocationAsync(targetProfile, editedProxy);
                CopyProxyValues(_editingProxy, editedProxy);
                ProxiesList.Items.Refresh();
            }

            CloseOpenDialogs();
            SaveState();
            UpdateSummary();
            AppendLog("隧道已保存。");
        }
        catch (Exception exception)
        {
            ProxyEditorErrorText.Text = exception.Message;
        }
    }

    private void CancelProxyEdit_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDialogs();
    }

    private void ProxyProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyRemotePortTextBox is null || ProxyProfileComboBox.SelectedItem is not FrpProfile profile)
        {
            return;
        }
        ProxyRemotePortTextBox.IsReadOnly = profile.ServerManaged || _editingProxy?.RemotePortLocked == true;
        if (_isCreatingProxy)
        {
            ProxyRemotePortTextBox.Text = profile.ServerManaged
                ? "系统自动分配"
                : Math.Max(1, _proxyEditorSuggestedRemotePort).ToString();
        }
        ProxyRemotePortTextBox.ToolTip = ProxyRemotePortTextBox.IsReadOnly
            ? "远程端口由 ZRfrp Server 自动分配，保存后不可修改。"
            : null;
    }

    private void SaveNodeSettings_Click(object sender, RoutedEventArgs e)
    {
        NodeSettingsErrorText.Text = "";

        if (_selectedProfile is null)
        {
            return;
        }

        try
        {
            ReadNodeSettingsIntoProfile(_selectedProfile);
            if (File.Exists(_selectedProfile.FrpcPath))
            {
                _state.ClientFrpcPath = _selectedProfile.FrpcPath;
            }

            SaveState();
            LoadProfile(_selectedProfile);
            CloseOpenDialogs();
            AppendLog("节点设置已保存。");
            _ = TestProfileLatencyAsync(_selectedProfile);
        }
        catch (Exception exception)
        {
            NodeSettingsErrorText.Text = exception.Message;
        }
    }

    private void CancelNodeSettings_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDialogs();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _announcedProxyAddresses.Clear();
        LogDocument.Blocks.Clear();
    }

    private void ConfirmAccept_Click(object sender, RoutedEventArgs e)
    {
        _confirmCompletion?.TrySetResult(true);
        CloseOpenDialogs();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _confirmCompletion?.TrySetResult(false);
        CloseOpenDialogs();
    }

    private Task TestAllProfilesLatencyAsync()
    {
        if (_isTestingAllLatency)
        {
            return Task.CompletedTask;
        }

        var profiles = _state.Profiles.ToArray();
        return RunAllLatencyTestsAsync(profiles);
    }

    private async Task RunAllLatencyTestsAsync(FrpProfile[] profiles)
    {
        _isTestingAllLatency = true;
        SetAllLatencyButtonTesting(true);
        try
        {
            await Task.WhenAll(profiles.Select(TestProfileLatencyAsync));
        }
        finally
        {
            _isTestingAllLatency = false;
            SetAllLatencyButtonTesting(false);
        }
    }

    private async Task TestProfileLatencyAsync(FrpProfile profile)
    {
        if (profile.IsLatencyTesting)
        {
            return;
        }

        profile.IsLatencyTesting = true;
        profile.LatencyMs = null;
        profile.LatencyStatus = "测速中";

        var result = await NetworkLatencyTester.TestAsync(profile.ServerAddr, profile.ServerPort, LatencyTimeout);

        profile.LatencyMs = result.Success ? result.Milliseconds : null;
        profile.LatencyStatus = result.Success ? "正常" : result.Message;
        profile.IsLatencyTesting = false;
    }

    private void SetAllLatencyButtonTesting(bool isTesting)
    {
        TestAllLatencyButton.IsEnabled = !isTesting;
        TestAllLatencyButton.Opacity = isTesting ? 0.72 : 1;

        if (isTesting)
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(900),
                RepeatBehavior = RepeatBehavior.Forever
            };
            TestAllLatencyIconRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
        else
        {
            TestAllLatencyIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            TestAllLatencyIconRotate.Angle = 0;
        }
    }

    private void LoadProfile(FrpProfile? profile)
    {
        _selectedProfile = profile;
        _state.LastProfileId = profile?.Id;

        HeaderTitle.Text = profile?.NameWithoutFlag ?? "尚未添加节点";
        if (profile?.HasFlagIcon == true)
        {
            HeaderFlagImage.Source = new BitmapImage(new Uri(profile.FlagIconPath, UriKind.Relative));
            HeaderFlagImage.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderFlagImage.Source = null;
            HeaderFlagImage.Visibility = Visibility.Collapsed;
        }
        NodeGeneratedConfigTextBox.Text = profile is null ? "" : _store.GetGeneratedConfigPath(profile);
        ProxiesList.ItemsSource = profile?.Proxies;
        ProxyProfileComboBox.SelectedItem = profile;

        UpdateSummary();
        UpdateHeaderSummary();
    }

    private void OpenNodeSettings(FrpProfile profile)
    {
        _selectedProfile = profile;
        ProfilesList.SelectedItem = profile;
        NodeSettingsErrorText.Text = "";
        NodeNameTextBox.Text = profile.Name;
        NodeServerAddrTextBox.Text = profile.ServerAddr;
        NodeServerPortTextBox.Text = profile.ServerPort.ToString();
        NodeTokenPasswordBox.Password = profile.Token;
        NodeServerManagedCheckBox.IsChecked = profile.ServerManaged;
        NodeControlApiUrlTextBox.Text = profile.ControlApiUrl;
        NodeControlApiKeyPasswordBox.Password = profile.ControlApiKey;
        NodeFrpcPathTextBox.Text = profile.FrpcPath;
        NodeGeneratedConfigTextBox.Text = _store.GetGeneratedConfigPath(profile);

        ShowOverlayPanel(NodeSettingsDialogPanel);
        NodeNameTextBox.Focus();
    }

    private void ReadNodeSettingsIntoProfile(FrpProfile profile)
    {
        var profileName = NodeNameTextBox.Text.Trim();
        var serverAddr = NodeServerAddrTextBox.Text.Trim();
        var frpcPath = NodeFrpcPathTextBox.Text.Trim();

        if (!int.TryParse(NodeServerPortTextBox.Text.Trim(), out var serverPort))
        {
            throw new InvalidOperationException("服务端端口必须是数字。");
        }

        ValidatePort(serverPort, "服务端端口");

        profile.Name = string.IsNullOrWhiteSpace(profileName) ? "未命名节点" : profileName;
        profile.ServerAddr = serverAddr;
        profile.ServerPort = serverPort;
        profile.FrpcPath = frpcPath;
        profile.Token = NodeTokenPasswordBox.Password;
        profile.ServerManaged = NodeServerManagedCheckBox.IsChecked == true;
        profile.ControlApiUrl = NodeControlApiUrlTextBox.Text.Trim();
        profile.ControlApiKey = NodeControlApiKeyPasswordBox.Password;
        if (profile.ServerManaged)
        {
            if (!Uri.TryCreate(profile.ControlApiUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("请填写有效的 ZRfrp Server 控制面板地址。");
            }
            if (string.IsNullOrWhiteSpace(profile.ControlApiKey)
                && string.IsNullOrWhiteSpace(profile.AccountAccessToken))
            {
                throw new InvalidOperationException("托管节点需要登录控制平台账号。");
            }
        }
    }

    private async Task DeleteProfileAsync(FrpProfile profile)
    {
        var result = await ShowConfirmAsync("删除节点", $"删除节点“{profile.Name}”？这个节点下的隧道配置也会一起移除。");
        if (!result)
        {
            return;
        }

        if (profile.ServerManaged)
        {
            foreach (var proxy in profile.Proxies.Where(item => !string.IsNullOrWhiteSpace(item.AllocationId)))
            {
                try
                {
                    await _controlClient.ReleaseAsync(profile, proxy.AllocationId);
                }
                catch (Exception exception)
                {
                    AppendLog($"节点租约释放失败：{exception.Message}");
                    await ShowConfirmAsync("无法删除", "该节点仍有服务端端口租约，请确认控制面板可访问后重试。");
                    return;
                }
            }
        }

        var index = _state.Profiles.IndexOf(profile);
        _state.Profiles.Remove(profile);
        if (_state.Profiles.Count == 0)
        {
            ProfilesList.SelectedItem = null;
            LoadProfile(null);
        }
        else
        {
            ProfilesList.SelectedIndex = Math.Clamp(index - 1, 0, _state.Profiles.Count - 1);
        }
        ProxyProfileComboBox.Items.Refresh();
        SaveState();
        AppendLog("节点已删除。");
    }

    private async Task RemoveProxyAsync(FrpProxy proxy)
    {
        var profile = _selectedProfile;
        if (profile is null)
        {
            return;
        }

        var result = await ShowConfirmAsync("移除隧道", $"移除隧道“{proxy.Name}”？");
        if (!result)
        {
            return;
        }

        if (profile.ServerManaged && !string.IsNullOrWhiteSpace(proxy.AllocationId))
        {
            try
            {
                var rejectedAccessToken = profile.AccountAccessToken;
                try
                {
                    await _controlClient.ReleaseAsync(profile, proxy.AllocationId);
                }
                catch (ControlApiException exception)
                    when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await EnsureAuthorizationAsync(profile, forceRefresh: true, rejectedAccessToken);
                    await _controlClient.ReleaseAsync(profile, proxy.AllocationId);
                }
            }
            catch (AccountAuthorizationRequiredException exception)
            {
                InvalidateAccountAuthorization();
                AppendLog(exception.Message);
                await ShowConfirmAsync("需要重新登录", exception.Message);
                ShowFirstLoginDialog();
                return;
            }
            catch (Exception exception)
            {
                AppendLog($"服务端端口租约释放失败：{exception.Message}");
                await ShowConfirmAsync("无法移除", "服务端端口租约释放失败，请确认控制面板可访问后重试。");
                return;
            }
        }

        profile.Proxies.Remove(proxy);
        SaveState();
        UpdateSummary();
        AppendLog("隧道已移除。");
    }

    private void SaveState()
    {
        if (_selectedProfile is not null)
        {
            _state.LastProfileId = _selectedProfile.Id;
        }

        _store.Save(_state);
        ProfilesList.Items.Refresh();
        ProxyProfileComboBox.Items.Refresh();
        UpdateSummary();
        UpdateHeaderSummary();
    }

    private static void ValidateProfile(FrpProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidOperationException("连接名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(profile.ServerAddr))
        {
            throw new InvalidOperationException("服务端地址不能为空。");
        }

        ValidatePort(profile.ServerPort, "服务端端口");

        if (string.IsNullOrWhiteSpace(profile.FrpcPath) || !File.Exists(profile.FrpcPath))
        {
            throw new InvalidOperationException("找不到 frpc.exe，请确认路径是否正确。");
        }

        var enabledProxies = profile.Proxies.Where(proxy => proxy.Enabled).ToList();
        if (enabledProxies.Count == 0)
        {
            throw new InvalidOperationException("至少需要启用一个隧道。");
        }

        foreach (var proxy in enabledProxies)
        {
            ValidateProxy(proxy, profile.ServerManaged);
        }
    }

    private static void ValidateProxy(FrpProxy proxy, bool remotePortManaged = false)
    {
        if (string.IsNullOrWhiteSpace(proxy.Name))
        {
            throw new InvalidOperationException("隧道名称不能为空。");
        }

        var type = proxy.Type.Trim().ToLowerInvariant();
        if (type is not ("tcp" or "udp" or "http" or "https"))
        {
            throw new InvalidOperationException($"暂不支持隧道类型“{proxy.Type}”。当前界面支持 tcp、udp、http、https。");
        }

        if (string.IsNullOrWhiteSpace(proxy.LocalIP))
        {
            throw new InvalidOperationException($"隧道“{proxy.Name}”的本地 IP 不能为空。");
        }

        ValidatePort(proxy.LocalPort, $"隧道“{proxy.Name}”的本地端口");

        if ((type is "tcp" or "udp") && !remotePortManaged)
        {
            ValidatePort(proxy.RemotePort, $"隧道“{proxy.Name}”的远程端口");
        }

        if (type is "http" or "https")
        {
            var hasDomains = proxy.CustomDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0;
            if (!hasDomains)
            {
                throw new InvalidOperationException($"HTTP/HTTPS 隧道“{proxy.Name}”需要填写域名。");
            }
        }
    }

    private static void ValidatePort(int port, string label)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{label}必须在 1 到 65535 之间。");
        }
    }

    private async Task StopRunnerAsync()
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        SetStoppingState(true);

        try
        {
            await _runner.StopAsync();
        }
        finally
        {
            _isStopping = false;
            UpdateRunningState(_runner.IsRunning);
        }
    }

    private void OpenProxyEditor(FrpProxy proxy, bool isNew)
    {
        _isCreatingProxy = isNew;
        _editingProxy = isNew ? null : proxy;
        _proxyEditorSuggestedRemotePort = proxy.RemotePort;
        ProxyEditorErrorText.Text = "";

        ProxyEditorTitle.Text = isNew ? "创建隧道" : "编辑隧道";
        ProxyEditorSubtitle.Text = isNew ? "选择节点并填写本地服务信息，远程端口由托管节点自动分配。" : "修改保存后，下次启动会使用新的配置。";
        ProxyProfileComboBox.IsEnabled = isNew;
        ProxyProfileComboBox.SelectedItem = _selectedProfile;

        ProxyEnabledCheckBox.IsChecked = proxy.Enabled;
        ProxyNameTextBox.Text = proxy.Name;
        SetProxyType(proxy.Type);
        ProxyLocalIPTextBox.Text = proxy.LocalIP;
        ProxyLocalPortTextBox.Text = proxy.LocalPort.ToString();
        var targetProfile = ProxyProfileComboBox.SelectedItem as FrpProfile ?? _selectedProfile;
        ProxyRemotePortTextBox.Text = isNew && targetProfile?.ServerManaged == true
            ? "系统自动分配"
            : proxy.RemotePort <= 0 ? "" : proxy.RemotePort.ToString();
        ProxyCustomDomainsTextBox.Text = proxy.CustomDomains;
        ProxyRemotePortTextBox.IsReadOnly = proxy.RemotePortLocked || targetProfile?.ServerManaged == true;
        ProxyRemotePortTextBox.ToolTip = ProxyRemotePortTextBox.IsReadOnly
            ? "远程端口由 ZRfrp Server 自动分配，保存后不可修改。"
            : null;

        ShowOverlayPanel(ProxyEditorPanel);
        ProxyNameTextBox.Focus();
    }

    private void CloseOpenDialogs()
    {
        StopFloatingPanelDrag();
        CloseFloatingPanelWindow();
        ConfirmDialogPanel.Visibility = Visibility.Collapsed;
        FirstLoginDialogPanel.Visibility = Visibility.Collapsed;
        NodeSettingsDialogPanel.Visibility = Visibility.Collapsed;
        AppSettingsDialogPanel.Visibility = Visibility.Collapsed;
        ProxyEditorPanel.Visibility = Visibility.Collapsed;
        DialogOverlay.Visibility = Visibility.Collapsed;
        _editingProxy = null;
        _isCreatingProxy = false;
    }

    private void ShowOverlayPanel(UIElement panel)
    {
        ConfirmDialogPanel.Visibility = Visibility.Collapsed;
        FirstLoginDialogPanel.Visibility = Visibility.Collapsed;
        NodeSettingsDialogPanel.Visibility = Visibility.Collapsed;
        AppSettingsDialogPanel.Visibility = Visibility.Collapsed;
        ProxyEditorPanel.Visibility = Visibility.Collapsed;

        if (panel is FrameworkElement element && IsFloatingDialogPanel(element))
        {
            ShowFloatingPanelWindow(element);
            return;
        }

        CloseFloatingPanelWindow();
        ResetFloatingPanelPosition(panel);
        panel.Visibility = Visibility.Visible;
        DialogOverlay.Visibility = Visibility.Visible;
    }

    private bool IsFloatingDialogPanel(FrameworkElement panel)
    {
        return ReferenceEquals(panel, NodeSettingsDialogPanel)
            || ReferenceEquals(panel, AppSettingsDialogPanel)
            || ReferenceEquals(panel, ProxyEditorPanel);
    }

    private void ShowFloatingPanelWindow(FrameworkElement panel)
    {
        CloseFloatingPanelWindow();
        ReturnFloatingPanelToOverlay(panel);
        ResetFloatingPanelPosition(panel);

        if (panel.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(panel);
        }

        panel.Visibility = Visibility.Visible;
        DialogOverlay.Visibility = Visibility.Collapsed;

        var host = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        _floatingPanelWindow = host;
        _floatingPanelContent = panel;

        host.Closed += (_, _) =>
        {
            host.Content = null;
            ReturnFloatingPanelToOverlay(panel);
            panel.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_floatingPanelWindow, host))
            {
                _floatingPanelWindow = null;
                _floatingPanelContent = null;
            }
        };

        host.Show();
    }

    private void CloseFloatingPanelWindow()
    {
        if (_floatingPanelWindow is null)
        {
            return;
        }

        var host = _floatingPanelWindow;
        _floatingPanelWindow = null;
        _floatingPanelContent = null;
        host.Close();
    }

    private void ReturnFloatingPanelToOverlay(FrameworkElement panel)
    {
        if (panel.Parent is not null)
        {
            return;
        }

        DialogOverlay.Children.Add(panel);
    }

    private static void ResetFloatingPanelPosition(UIElement panel)
    {
        if (panel is FrameworkElement element)
        {
            var transform = EnsureFloatingPanelTransform(element);
            transform.X = 0;
            transform.Y = 0;
        }
    }

    private Task<bool> ShowConfirmAsync(string title, string message)
    {
        ConfirmTitleText.Text = title;
        ConfirmMessageText.Text = message;
        _confirmCompletion = new TaskCompletionSource<bool>();
        ShowOverlayPanel(ConfirmDialogPanel);
        return _confirmCompletion.Task;
    }

    private FrpProxy ReadProxyFromEditor()
    {
        var type = GetSelectedProxyType();
        var localPort = ParsePort(ProxyLocalPortTextBox.Text, "本地端口");
        var managedProfile = ProxyProfileComboBox.SelectedItem as FrpProfile;
        var remotePort = managedProfile?.ServerManaged == true
            ? 0
            : string.IsNullOrWhiteSpace(ProxyRemotePortTextBox.Text)
            ? 0
            : ParsePort(ProxyRemotePortTextBox.Text, "远程端口");

        return new FrpProxy
        {
            Enabled = ProxyEnabledCheckBox.IsChecked == true,
            Name = ProxyNameTextBox.Text.Trim(),
            Type = type,
            LocalIP = ProxyLocalIPTextBox.Text.Trim(),
            LocalPort = localPort,
            RemotePort = remotePort,
            BandwidthLimit = "",
            CustomDomains = ProxyCustomDomainsTextBox.Text.Trim()
        };
    }

    private static void CopyProxyValues(FrpProxy target, FrpProxy source)
    {
        target.Enabled = source.Enabled;
        target.Name = source.Name;
        target.Type = source.Type;
        target.LocalIP = source.LocalIP;
        target.LocalPort = source.LocalPort;
        target.RemotePort = source.RemotePort;
        target.AllocationId = source.AllocationId;
        target.RemotePortLocked = source.RemotePortLocked;
        target.BandwidthLimit = "";
        target.CustomDomains = source.CustomDomains;
    }

    private async Task ApplyServerAllocationAsync(FrpProfile profile, FrpProxy proxy)
    {
        if (!profile.ServerManaged)
        {
            return;
        }

        if (proxy.Type is not ("tcp" or "udp"))
        {
            throw new InvalidOperationException("当前服务端自动分配仅支持 TCP/UDP 隧道。");
        }

        ManagedAllocation allocation;
        var rejectedAccessToken = profile.AccountAccessToken;
        try
        {
            allocation = await _controlClient.AllocateAsync(profile, proxy);
        }
        catch (ControlApiException exception)
            when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await EnsureAuthorizationAsync(profile, forceRefresh: true, rejectedAccessToken);
            allocation = await _controlClient.AllocateAsync(profile, proxy);
        }
        if (string.IsNullOrWhiteSpace(profile.ManagedNodeId)
            || !profile.ManagedNodeId.Equals(allocation.NodeId, StringComparison.Ordinal)
            || !profile.ServerAddr.Equals(allocation.ServerAddress, StringComparison.OrdinalIgnoreCase)
            || profile.ServerPort != allocation.ServerPort)
        {
            throw new InvalidOperationException(
                $"服务端返回了不匹配的节点分配：期望 {profile.Name} ({profile.ServerAddr}:{profile.ServerPort})，"
                + $"实际为 {allocation.NodeName} ({allocation.ServerAddress}:{allocation.ServerPort})。已拒绝切换节点。");
        }
        proxy.RemotePort = allocation.RemotePort;
        proxy.AllocationId = allocation.AllocationId;
        proxy.RemotePortLocked = allocation.Locked;
        AppendLog($"服务端已为“{proxy.Name}”分配 {allocation.NodeName}:{allocation.RemotePort}。");
    }

    private async Task<FrpProfile> ApplyBestServerAllocationAsync(FrpProfile preferred, FrpProxy proxy)
    {
        if (!preferred.ServerManaged)
        {
            return preferred;
        }

        await ApplyServerAllocationAsync(preferred, proxy);
        ProxyProfileComboBox.SelectedItem = preferred;
        return preferred;
    }

    private FrpProxy CreateDefaultEditorProxy()
    {
        var profile = _selectedProfile;
        var index = profile?.Proxies.Count + 1 ?? 1;

        return new FrpProxy
        {
            Name = $"tunnel-{index}",
            Type = "tcp",
            LocalIP = "127.0.0.1",
            LocalPort = 80,
            RemotePort = 6000 + index - 1,
            Enabled = true
        };
    }

    private static int ParsePort(string value, string label)
    {
        if (!int.TryParse(value.Trim(), out var port))
        {
            throw new InvalidOperationException($"{label}必须是数字。");
        }

        return port;
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private string GetSelectedProxyType()
    {
        if (ProxyTypeComboBox.SelectedItem is ComboBoxItem item && item.Content is not null)
        {
            return item.Content.ToString() ?? "tcp";
        }

        return "tcp";
    }

    private void SetProxyType(string type)
    {
        foreach (var item in ProxyTypeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), type, StringComparison.OrdinalIgnoreCase))
            {
                ProxyTypeComboBox.SelectedItem = item;
                return;
            }
        }

        ProxyTypeComboBox.SelectedIndex = 0;
    }

    private string ResolveFrpcPathForImportedConfig(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var siblingFrpc = Path.Combine(directory, "frpc.exe");
            if (File.Exists(siblingFrpc))
            {
                return siblingFrpc;
            }
        }

        return _selectedProfile?.FrpcPath ?? "";
    }

    private FrpProfile CreateBlankProfile()
    {
        var source = _selectedProfile;
        var profile = new FrpProfile
        {
            Name = "新节点",
            FrpcPath = !string.IsNullOrWhiteSpace(_state.ClientFrpcPath)
                ? _state.ClientFrpcPath
                : source?.FrpcPath ?? "",
            ServerAddr = source?.ServerAddr ?? "",
            ServerPort = source?.ServerPort ?? 7000,
            Token = source?.Token ?? ""
        };
        profile.Proxies.Add(FrpConfigSerializer.CreateDefaultProxy());
        return profile;
    }

    private void SetBusy(bool busy)
    {
        if (_isStopping)
        {
            return;
        }

        SetConnectionButton(
            busy ? "正在校验" : (_runner.IsRunning ? "停止连接" : "启动连接"),
            _runner.IsRunning ? "DangerButton" : "PrimaryButton",
            !busy);
        StatusText.Text = busy ? "校验中" : (_runner.IsRunning ? "运行中" : "未运行");
        StatusDot.Fill = busy ? BrushFromHex("#F59E0B") : (_runner.IsRunning ? BrushFromHex("#17C964") : BrushFromHex("#94A3B8"));
    }

    private void SetStoppingState(bool stopping)
    {
        SetConnectionButton(
            stopping ? "正在停止" : (_runner.IsRunning ? "停止连接" : "启动连接"),
            _runner.IsRunning ? "DangerButton" : "PrimaryButton",
            !stopping);
        StatusText.Text = stopping ? "停止中" : (_runner.IsRunning ? "运行中" : "未运行");
        StatusDot.Fill = stopping ? BrushFromHex("#F59E0B") : (_runner.IsRunning ? BrushFromHex("#17C964") : BrushFromHex("#94A3B8"));
    }

    private void UpdateRunningState(bool running)
    {
        if (_isStopping)
        {
            SetStoppingState(true);
            return;
        }

        SetConnectionButton(
            running ? "停止连接" : "启动连接",
            running ? "DangerButton" : "PrimaryButton",
            isEnabled: true);
        StatusText.Text = running ? "运行中" : "未运行";
        StatusDot.Fill = running ? BrushFromHex("#17C964") : BrushFromHex("#94A3B8");
        UpdateTrayText();
    }

    private void SetConnectionButton(string content, string styleKey, bool isEnabled)
    {
        ConnectionButton.Content = content;
        ConnectionButton.Style = (Style)FindResource(styleKey);
        ConnectionButton.IsEnabled = isEnabled;
    }

    private void UpdateSummary()
    {
        if (_selectedProfile is null)
        {
            TunnelSummaryText.Text = "0 个隧道";
            return;
        }

        var enabledCount = _selectedProfile.Proxies.Count(proxy => proxy.Enabled);
        TunnelSummaryText.Text = $"{_selectedProfile.Proxies.Count} 个隧道，{enabledCount} 个启用";
    }

    private void UpdateHeaderSummary()
    {
        if (_selectedProfile is null)
        {
            ServerSummaryText.Text = "未配置";
            GeneratedSummaryText.Text = "等待生成";
            return;
        }

        ServerSummaryText.Text = $"{_selectedProfile.ServerAddr}:{_selectedProfile.ServerPort}";
        var configPath = _store.GetGeneratedConfigPath(_selectedProfile);
        GeneratedSummaryText.Text = string.IsNullOrWhiteSpace(configPath)
            ? "等待生成"
            : Path.GetFileName(configPath);
    }

    private void AppendLog(string line)
    {
        foreach (var rawLine in line.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var cleanLine = AnsiColorRegex.Replace(rawLine, string.Empty);
            if (string.IsNullOrWhiteSpace(cleanLine))
            {
                continue;
            }

            AddLogLine(cleanLine);
            TryAppendProxyAddressAnnouncement(cleanLine);
        }
    }

    private void AddLogLine(string line)
    {
        _logBuffer.Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss"))
            .Append("] ")
            .AppendLine(line);

        if (_logBuffer.Length > 200_000)
        {
            _logBuffer.Remove(0, 80_000);
        }

        var paragraph = CreateLogParagraph();
        paragraph.Inlines.Add(CreateTimestampRun());
        paragraph.Inlines.Add(new Run(line)
        {
            Foreground = GetLogBrush(line)
        });
        AppendLogParagraph(paragraph);
    }

    private void TryAppendProxyAddressAnnouncement(string line)
    {
        var match = ProxyStartSuccessRegex.Match(line);
        if (!match.Success || _selectedProfile is null)
        {
            return;
        }

        var proxyName = match.Groups["name"].Value.Trim();
        var proxy = _selectedProfile.Proxies.FirstOrDefault(item => string.Equals(item.Name, proxyName, StringComparison.OrdinalIgnoreCase));
        if (proxy is null)
        {
            return;
        }

        var addresses = BuildProxyConnectionAddresses(_selectedProfile, proxy);
        if (addresses.Count == 0)
        {
            return;
        }

        var announceKey = $"{_selectedProfile.Id}:{proxy.Name}:{string.Join('|', addresses.Select(item => item.Address))}";
        if (!_announcedProxyAddresses.Add(announceKey))
        {
            return;
        }

        AddProxySuccessLines(proxy.Name, addresses);
    }

    private void AddProxySuccessLines(string proxyName, IReadOnlyList<ProxyConnectionAddress> addresses)
    {
        for (var index = 0; index < addresses.Count; index++)
        {
            var item = addresses[index];
            var message = item.IsDomain
                ? $"{proxyName} 隧道开启成功，推荐使用域名地址 "
                : index > 0
                    ? "IP 地址（不推荐使用） "
                    : $"{proxyName} 隧道开启成功，可以通过 ";

            _logBuffer.Append('[')
                .Append(DateTime.Now.ToString("HH:mm:ss"))
                .Append("] ")
                .Append(message)
                .Append('>')
                .Append(item.Address)
                .AppendLine("< 进行连接");

            var paragraph = CreateLogParagraph();
            paragraph.Inlines.Add(CreateTimestampRun());
            paragraph.Inlines.Add(new Run(message)
            {
                Foreground = item.Recommended ? LogSuccessBrush : LogWarningBrush
            });
            paragraph.Inlines.Add(new Run(">")
            {
                Foreground = LogNoticeBrush
            });

            var hyperlink = new Hyperlink(new Run(item.Address))
            {
                Foreground = LogNoticeBrush,
                Cursor = WpfCursors.Hand,
                Tag = item.Address,
                TextDecorations = TextDecorations.Underline
            };
            hyperlink.Click += CopyAddressHyperlink_Click;
            paragraph.Inlines.Add(hyperlink);

            paragraph.Inlines.Add(new Run("< 进行连接")
            {
                Foreground = item.Recommended ? LogSuccessBrush : LogWarningBrush
            });

            AppendLogParagraph(paragraph);
        }
    }

    private static Paragraph CreateLogParagraph()
    {
        return new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 2),
            LineHeight = 16
        };
    }

    private static Run CreateTimestampRun()
    {
        return new Run($"[{DateTime.Now:HH:mm:ss}] ")
        {
            Foreground = LogTimeBrush
        };
    }

    private void AppendLogParagraph(Paragraph paragraph)
    {
        if (LogDocument.Blocks.Count == 1 && LogDocument.Blocks.FirstBlock is Paragraph firstParagraph && !firstParagraph.Inlines.Any())
        {
            LogDocument.Blocks.Clear();
        }

        LogDocument.Blocks.Add(paragraph);
        while (LogDocument.Blocks.Count > MaxLogParagraphs)
        {
            LogDocument.Blocks.Remove(LogDocument.Blocks.FirstBlock);
        }

        LogTextBox.ScrollToEnd();
    }

    private static MediaBrush GetLogBrush(string line)
    {
        if (line.Contains("[E]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || line.Contains("失败", StringComparison.OrdinalIgnoreCase)
            || line.Contains("错误", StringComparison.OrdinalIgnoreCase)
            || line.Contains("退出码 1", StringComparison.OrdinalIgnoreCase))
        {
            return LogWarningBrush;
        }

        if (line.Contains("[W]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return LogWarningBrush;
        }

        if (line.Contains("success", StringComparison.OrdinalIgnoreCase)
            || line.Contains("已启动", StringComparison.OrdinalIgnoreCase)
            || line.Contains("已保存", StringComparison.OrdinalIgnoreCase)
            || line.Contains("已生成", StringComparison.OrdinalIgnoreCase)
            || line.Contains("开启成功", StringComparison.OrdinalIgnoreCase))
        {
            return LogSuccessBrush;
        }

        return LogInfoBrush;
    }

    private static IReadOnlyList<ProxyConnectionAddress> BuildProxyConnectionAddresses(FrpProfile profile, FrpProxy proxy)
    {
        var type = proxy.Type.Trim().ToLowerInvariant();
        if ((type is "tcp" or "udp") && proxy.RemotePort > 0)
        {
            var addresses = new List<ProxyConnectionAddress>();
            if (Uri.TryCreate(profile.ControlApiUrl, UriKind.Absolute, out var platformUri)
                && platformUri.Scheme is "http" or "https"
                && !System.Net.IPAddress.TryParse(platformUri.Host, out _))
            {
                addresses.Add(new($"{platformUri.Host}:{proxy.RemotePort}", true, true));
            }

            var serverAddress = $"{profile.ServerAddr}:{proxy.RemotePort}";
            if (!addresses.Any(item => item.Address.Equals(serverAddress, StringComparison.OrdinalIgnoreCase)))
            {
                addresses.Add(new(serverAddress, addresses.Count == 0, false));
            }
            return addresses;
        }

        if ((type is "http" or "https") && !string.IsNullOrWhiteSpace(proxy.CustomDomains))
        {
            var domain = proxy.CustomDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(domain))
            {
                return [new(type == "https" ? $"https://{domain}" : $"http://{domain}", true, true)];
            }
        }

        return proxy.RemotePort > 0
            ? [new($"{profile.ServerAddr}:{proxy.RemotePort}", true, false)]
            : [];
    }

    private sealed record ProxyConnectionAddress(string Address, bool Recommended, bool IsDomain);

    private void CopyAddressHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string address } || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        WpfClipboard.SetText(address);
        ShowToast("地址已复制到剪贴板");
    }

    private async void ShowToast(string message)
    {
        _toastCancellation?.Cancel();
        _toastCancellation = new CancellationTokenSource();
        var token = _toastCancellation.Token;

        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(1800, token);
            if (!token.IsCancellationRequested)
            {
                ToastPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private static T? GetContextData<T>(object sender)
        where T : class
    {
        if (sender is FrameworkElement element)
        {
            return element.DataContext as T;
        }

        return null;
    }

    private static void SelectItemUnderMouse<T>(WpfListBox listBox, object originalSource)
        where T : class
    {
        if (originalSource is not DependencyObject dependencyObject)
        {
            return;
        }

        var item = FindVisualParent<ListBoxItem>(dependencyObject);
        if (item?.DataContext is T data)
        {
            listBox.SelectedItem = data;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }

    private sealed class AccountAuthorizationRequiredException : InvalidOperationException
    {
        public AccountAuthorizationRequiredException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}

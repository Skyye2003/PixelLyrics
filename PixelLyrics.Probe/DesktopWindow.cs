using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;

namespace PixelLyrics.Probe;

internal sealed partial class DesktopWindow : Window, IDisposable
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "desktop-settings.json");
    private readonly ComboBox _player = new() { Width = 180, DisplayMemberPath = "Value", SelectedValuePath = "Key" };
    private MusicPlayer SelectedPlayer => _player.SelectedValue is MusicPlayer player ? player : MusicPlayer.QQMusic;
    private readonly CheckBox _authorization = new() { Content = "允许本次联网获取 QQ 音乐歌词并输出到 USB 歌词屏", IsChecked = false };
    private readonly Button _start = MakeButton("开始运行");
    private readonly Button _stop = MakeButton("停止并清屏");
    private readonly Button _recheck = MakeButton("重新核对曲目");
    private readonly TextBlock _sessionPanel = Text("尚无运行会话。");
    private readonly TextBlock _parameterHint = Text("等待配置快照。");
    private DesktopSession? _session;
    private TrackVersion? _displayedTrack;
    private DesktopParameters? _savedParameters;
    private readonly Button _clock = MakeButton("停止歌词并显示时钟");
    private readonly ComboBox _width = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _hold = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _clockStyle = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _idleClock = new() { Content = "空闲自动返回时钟", IsChecked = true };
    private readonly ComboBox _idleClockDelay = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _trackIntro = new() { Content = "新曲显示歌名与歌手", IsChecked = false };
    private readonly ComboBox _trackIntroHold = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _noLyricsTitleHold = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _pollInterval = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _trackSettle = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _requestTimeout = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _clockBusy;
    private Task? _clockTask;
    private readonly Button _save = MakeButton("保存设置");
    private readonly TextBlock _saveHint = Text("设置不会自动保存；修改后请保存设置。");
    private bool _closePromptOpen;
    private string _lastConfigurationResult = "";
    private readonly Button _reload = MakeButton("重新加载");
    private readonly Slider _offset = new() { Minimum = -5000, Maximum = 5000, TickFrequency = 100, SmallChange = 100, LargeChange = 100, IsSnapToTickEnabled = true };
    private readonly TextBlock _offsetText = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = Text("");
    private readonly TextBlock _configuration = Text("正在读取配置；此操作不会联网或写屏。");
    private readonly TextBox _logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 240, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12 };
    private readonly Queue<string> _pendingLogs = new();
    private readonly Queue<string> _visibleLogs = new();
    private readonly object _logLock = new();
    private readonly DispatcherTimer _logTimer;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Windows.Forms.ContextMenuStrip _trayMenu;
    private readonly System.Windows.Forms.ToolStripMenuItem _trayStop;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private Task? _configurationTask;
    private Task? _stopTask;
    private bool _settingsValid;
    private bool _configurationBusy;
    private bool _running;
    private bool _stopping;
    private bool _exiting;
    private bool _allowClose;
    private bool _disposed;
    private int _acceptLogs;

    private readonly CheckBox _loginStartup = new() { Content = "登录后启动到托盘", IsChecked = false };
    private readonly TextBlock _startupStatus = Text("正在读取实际登录自启状态……");
    private readonly LoginStartup _startup = new(new CurrentUserLoginStartupStore(), LoginStartup.CurrentExecutablePath);

    private readonly DesktopTheme _theme;
    private bool? _savedStartup;
    private string _configurationError = "";
    private readonly TextBox _title = ScreenText("尚未开始", 32);
    private readonly TextBox _artist = ScreenText("");
    private readonly TextBlock _playback = Text("");
    private readonly TextBox _check = ScreenText("");
    private readonly TextBlock _output = Text("");
    private readonly TextBox _headline = ScreenText("未启用");
    private readonly TextBlock _runtimeHint = Text("");

    public DesktopWindow(bool startInTray = false)
    {
        Title = "PixelLyrics · 歌词屏";
        Width = 900;
        Height = 620;
        MinWidth = 860;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        _theme = new DesktopTheme(this);
        for (int units = 8; units <= 64; units++) _width.Items.Add(units);
        for (int ms = 500; ms <= 10000; ms += 100) _hold.Items.Add(ms);
        for (int style = 1; style <= 11; style++) _clockStyle.Items.Add(style);
        for (int ms = 1000; ms <= 60000; ms += 1000) _idleClockDelay.Items.Add(ms);
        for (int ms = 1000; ms <= 15000; ms += 100) _trackIntroHold.Items.Add(ms);
        for (int ms = 1000; ms <= 60000; ms += 1000) _noLyricsTitleHold.Items.Add(ms);
        for (int ms = 100; ms <= 1000; ms += 100) _pollInterval.Items.Add(ms);
        for (int ms = 200; ms <= 5000; ms += 100) _trackSettle.Items.Add(ms);
        for (int ms = 2000; ms <= 30000; ms += 1000) _requestTimeout.Items.Add(ms);
        _width.SelectedItem = 32;
        _hold.SelectedItem = 2800;
        _clockStyle.SelectedItem = 1;
        _idleClockDelay.SelectedItem = 5000;
        _trackIntroHold.SelectedItem = 3000;
        _noLyricsTitleHold.SelectedItem = 5000;
        _pollInterval.SelectedItem = 200;
        _trackSettle.SelectedItem = 600;
        _requestTimeout.SelectedItem = 10000;
        BuildConsoleLayout(out var earlier, out var later, out var exit);

        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add("打开控制台", null, (_, _) => Dispatch(ShowFromTray));
        _trayStop = new System.Windows.Forms.ToolStripMenuItem("停止并清屏");
        _trayStop.Click += (_, _) => Dispatch(async () => await StopAsync());
        _trayMenu.Items.Add(_trayStop);
        _trayMenu.Items.Add("退出", null, (_, _) => Dispatch(async () => await ExitAsync()));
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "PixelLyrics · 未启用",
            Icon = AppIcon.CreateTrayIcon(),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatch(ShowFromTray);
        _logTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _logTimer.Tick += DrainLogs;
        _logTimer.Start();
        Program.DesktopLog += ReceiveLog;

        _player.SelectionChanged += (_, _) =>
        {
            _authorization.IsChecked = false;
            string text = $"允许本次联网获取 {SelectedPlayer.DisplayName()}歌词并输出到 USB 歌词屏";
            _authorization.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(_authorization, text);
            UpdateControls();
        };
        _authorization.Checked += (_, _) => UpdateControls();
        _authorization.Unchecked += (_, _) => UpdateControls();
        _offset.ValueChanged += (_, _) => UpdateOffsetText();
        earlier.Click += (_, _) => _offset.Value = Math.Max(-5000, _offset.Value - 100);
        later.Click += (_, _) => _offset.Value = Math.Min(5000, _offset.Value + 100);
        _recheck.Click += (_, _) =>
        {
            if (_session is not null && !_stopping && !_exiting && !_clockBusy)
                _session.QueueRecheck(_displayedTrack, _session.Clock.Elapsed);
            RefreshSessionPanel();
        };
        _start.Click += (_, _) => Start();
        _stop.Click += async (_, _) => await StopAsync();
        _clock.Click += async (_, _) =>
        {
            if (_clockBusy || _exiting || _configurationBusy || _stopping) return;
            _clockTask = SwitchClockAsync();
            await _clockTask;
        };
        _save.Click += (_, _) => BeginConfiguration(save: true);
        _reload.Click += (_, _) => BeginConfiguration(save: false);
        exit.Click += (_, _) => Close();
        Loaded += (_, _) =>
        {
            if (startInTray) Hide();
            BeginConfiguration(save: false);
        };
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && !_allowClose) Hide(); };
        foreach (var combo in new[] { _width, _hold, _clockStyle, _idleClockDelay, _trackIntroHold, _noLyricsTitleHold, _pollInterval, _trackSettle, _requestTimeout })
            combo.SelectionChanged += (_, _) => RefreshSettingsFeedback();
        foreach (var check in new[] { _idleClock, _trackIntro, _loginStartup })
        {
            check.Checked += (_, _) => { UpdateControls(); RefreshSettingsFeedback(); };
            check.Unchecked += (_, _) => { UpdateControls(); RefreshSettingsFeedback(); };
        }
        UpdateOffsetText();
        UpdateControls();
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0),
        MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand
    };

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), LineHeight = 22 };
        var style = new Style(typeof(TextBlock));
        var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        style.Triggers.Add(empty);
        block.Style = style;
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }

    private static Grid Columns(FrameworkElement left, FrameworkElement right, double leftWeight = 1, double rightWeight = 1)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(right);
        grid.SizeChanged += (_, _) =>
        {
            bool wide = grid.ActualWidth >= 700;
            grid.ColumnDefinitions[0].Width = new GridLength(wide ? leftWeight : 1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(wide ? 24 : 0);
            grid.ColumnDefinitions[2].Width = wide ? new GridLength(rightWeight, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(right, wide ? 2 : 0);
            Grid.SetRow(right, wide ? 0 : 1);
        };
        return grid;
    }

    private static Grid InlineActions(FrameworkElement text, FrameworkElement actions, double breakpoint, bool keepActionsRight = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(actions);
        grid.SizeChanged += (_, _) =>
        {
            bool narrow = grid.ActualWidth < breakpoint;
            Grid.SetColumn(actions, narrow ? 0 : 1);
            Grid.SetRow(actions, narrow ? 1 : 0);
            actions.HorizontalAlignment = narrow && !keepActionsRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumnSpan(text, narrow ? 2 : 1);
        };
        return grid;
    }

    private static Button Info(string title, string explanation)
    {
        var button = new Button { Content = "ⓘ", Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, FontSize = 16, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(Control.StyleProperty, "InfoButtonStyle");
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, explanation);
        var tip = new ToolTip { Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        tip.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        tip.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tip.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        button.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(button, 300);
        ToolTipService.SetShowDuration(button, int.MaxValue);
        button.GotKeyboardFocus += (_, _) => tip.IsOpen = true;
        button.LostKeyboardFocus += (_, _) => tip.IsOpen = false;
        button.Click += (_, _) => tip.IsOpen = true;
        button.MouseLeave += (_, _) => { if (!button.IsKeyboardFocusWithin) tip.IsOpen = false; };
        button.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { tip.IsOpen = false; e.Handled = true; } };
        button.Unloaded += (_, _) => tip.IsOpen = false;
        return button;
    }

    private static Grid Setting(string title, Control control, string? hint = null)
    {
        var row = new Grid { Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new Label { Content = title, Target = control, Padding = new Thickness(0, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        labelPanel.Children.Add(label);
        if (hint is not null) labelPanel.Children.Add(Info(title + "说明", hint));
        AutomationProperties.SetName(control, title);
        row.Children.Add(labelPanel);
        row.Children.Add(control);
        control.Width = 124;
        control.Height = 34;
        control.VerticalAlignment = VerticalAlignment.Center;
        control.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        control.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        Grid.SetColumn(control, 1);
        return row;
    }

    private void UpdateOffsetText()
    {
        _offsetText.Text = $"{(int)_offset.Value:+0;-0;0} ms";
        RefreshSettingsFeedback();
    }

    private void RefreshSettingsFeedback()
    {
        if (_requestTimeout.SelectedItem is null) return;
        bool dirty = _savedParameters is not null && (CaptureParameters() != _savedParameters || _loginStartup.IsChecked != _savedStartup);
        bool pending = _session?.Latest is { Running: true, Active: not null } snapshot && CaptureParameters() != snapshot.Active;
        _saveHint.Text = _exiting ? "正在安全退出，请等待"
            : _configurationBusy ? "正在读取或保存设置…"
            : _configurationError.Length > 0 ? _configurationError
            : !_settingsValid ? "配置不可用，请在设置中重新加载"
            : dirty ? (_running || _stopping || _clockBusy ? "有未保存更改 · 停止后可保存" : "有未保存更改")
            : "";
        _save.ToolTip = _saveHint.Text;
        _runtimeHint.Text = pending ? "更改将在下次开始时生效" : "";
        _runtimeHint.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        _headline.Text = _exiting ? "安全退出中" : _stopping ? "停止中" : _clockBusy ? "切换时钟中" : _running ? (_playback.Text.Length > 0 ? _playback.Text : "运行中") : "未启用";
        RefreshPlaybackVisuals();
    }

    private DesktopParameters CaptureParameters() => DesktopParameters.From(CaptureRuntimeSettings()) with
    { OffsetMs = (int)_offset.Value, Width = (int)_width.SelectedItem, Hold = (int)_hold.SelectedItem };

    private void RefreshSessionPanel()
    {
        var s = _session?.Latest;
        _displayedTrack = s?.Track;
        _recheck.IsEnabled = !_exiting && !_stopping && !_clockBusy && _running
            && _session?.CanRecheck(_session.Clock.Elapsed) == true;
        _title.Text = s?.Title ?? (s?.Running == true ? $"等待 {SelectedPlayer.DisplayName()}播放" : "尚未开始");
        _artist.Text = s?.Artist ?? "";
        _diagnosticValues[0].Text = (s?.Active?.Player ?? SelectedPlayer).DisplayName();
        _diagnosticValues[1].Text = s?.RunId.ToString()[..8] ?? "尚未开始";
        _diagnosticValues[2].Text = s?.Title ?? "—";
        _diagnosticValues[3].Text = s is null ? "未启用" : s.Stopping ? "停止中" : s.Running ? "运行中" : "已结束";
        _diagnosticValues[4].Text = s?.Check ?? "—";
        _diagnosticValues[5].Text = s?.Display ?? "—";
        _artist.ToolTip = _artist.Text;
        string playback = s?.Playback switch { "Playing" => "播放中", "Paused" => "已暂停", "Stopped" => "已停止", "Closed" => "播放器已关闭", "Opened" => "等待播放", _ => s?.Playback ?? "" };
        _playback.Text = s is null ? "" : playback;
        _check.Text = s is null ? "" : "歌词 · " + s.Check;
        _output.Text = s?.Display == "设备写入失败；实际显示未知" ? "设备写入失败，请停止后检查 USB 连接" : "";
        _artist.Visibility = _artist.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _check.Visibility = _check.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _output.Visibility = _output.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _recheck.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshSettingsFeedback();
        if (s is not null)
            _sessionPanel.Text = $"播放器及歌词来源：{(s.Active?.Player ?? SelectedPlayer).DisplayName()}\n会话 {s.RunId.ToString()[..8]} · {(s.Running ? s.Stopping ? "停止中" : "运行中" : "已结束")}\n"
                + $"曲目：{s.Title ?? "不可用"} / {s.Artist ?? "未知"} · {s.Playback}\n"
                + $"核对：{s.Check} · attempt {s.AttemptId} · {(s.Busy ? "忙碌" : _session!.Clock.Elapsed < s.CooldownUntil ? "冷却中" : "空闲")}\n"
                + $"输出：{s.Display}";
        if (_width.SelectedItem is null || _hold.SelectedItem is null || _requestTimeout.SelectedItem is null) return;
        var draft = CaptureParameters();
        _parameterHint.Text = $"编辑值 ↔ 已保存：{draft.Differences(_savedParameters)}\n"
            + (s?.Running == true ? $"编辑值 ↔ 本次运行：{draft.Differences(s.Active)}（差异下次开始生效）\n已保存 ↔ 本次运行：{_savedParameters?.Differences(s.Active) ?? "无快照"}" : "本次运行值：当前未运行")
            + "\n自启独立于 JSON；授权和绑定不参与参数差异比较。";
    }

    private void UpdateControls()
    {
        _start.IsEnabled = !_exiting && !_running && !_stopping && !_clockBusy && !_configurationBusy && _settingsValid && _authorization.IsChecked == true;
        _stop.IsEnabled = !_exiting && _running && !_stopping;
        _trayStop.Enabled = _stop.IsEnabled;
        _save.IsEnabled = !_exiting && !_running && !_stopping && !_clockBusy && !_configurationBusy && _settingsValid;
        _reload.IsEnabled = !_exiting && !_running && !_stopping && !_clockBusy && !_configurationBusy;
        _authorization.IsEnabled = !_exiting && !_running;
        _player.IsEnabled = !_exiting && !_running && !_stopping && !_clockBusy && !_configurationBusy;
        _loginStartup.IsEnabled = !_exiting && !_running && !_stopping && !_clockBusy && !_configurationBusy;
        _offset.IsEnabled = !_exiting;
        _clock.IsEnabled = !_exiting && !_clockBusy && !_stopping && !_configurationBusy && _settingsValid;
        _idleClock.IsEnabled = !_exiting && !_clockBusy && !_configurationBusy;
        _trackIntro.IsEnabled = _width.IsEnabled = _hold.IsEnabled = _clockStyle.IsEnabled = !_exiting && !_clockBusy && !_configurationBusy;
        _noLyricsTitleHold.IsEnabled = _pollInterval.IsEnabled = _trackSettle.IsEnabled = _requestTimeout.IsEnabled
            = !_exiting && !_clockBusy && !_configurationBusy;
        _idleClockDelay.IsEnabled = _idleClock.IsEnabled && _idleClock.IsChecked == true;
        _trackIntroHold.IsEnabled = _trackIntro.IsEnabled && _trackIntro.IsChecked == true;
        RefreshSettingsFeedback();
        _tray.Text = _exiting ? "PixelLyrics · 等待安全退出" : _stopping ? "PixelLyrics · 等待停止" : _running ? "PixelLyrics · 运行中" : "PixelLyrics · 未启用";
    }

    private void BeginConfiguration(bool save)
    {
        if (_exiting || _running || _stopping || _clockBusy || _configurationBusy || (save && !_settingsValid)) return;
        if (save && _loginStartup.IsChecked is null)
        {
            _lastConfigurationResult = "自启状态未知，请明确勾选或取消自启后再保存，或重新加载。 ";
            _configurationError = _lastConfigurationResult;
            UpdateControls();
            return;
        }
        _configurationError = "";
        _configurationBusy = true;
        int offset = (int)_offset.Value;
        UpdateControls();
        _configurationTask = ConfigureAsync(save, offset, (int)_width.SelectedItem, (int)_hold.SelectedItem, (int)_clockStyle.SelectedItem, CaptureRuntimeSettings(), _loginStartup.IsChecked == true);
    }

    private ProbeSettings CaptureRuntimeSettings() => new()
    {
        Player = SelectedPlayer,
        IdleClockEnabled = _idleClock.IsChecked == true,
        IdleClockDelayMs = (int)_idleClockDelay.SelectedItem,
        TrackIntroEnabled = _trackIntro.IsChecked == true,
        TrackIntroHoldMs = (int)_trackIntroHold.SelectedItem,
        ClockStyle = (int)_clockStyle.SelectedItem,
        NoLyricsTitleHoldMs = (int)_noLyricsTitleHold.SelectedItem,
        PollIntervalMs = (int)_pollInterval.SelectedItem,
        TrackSettleMs = (int)_trackSettle.SelectedItem,
        RequestTimeoutMs = (int)_requestTimeout.SelectedItem
    };

    private static void SelectMilliseconds(ComboBox control, int value)
    {
        if (!control.Items.Contains(value)) control.Items.Add(value);
        control.SelectedItem = value;
    }

    private async Task ConfigureAsync(bool save, int offset, int width, int hold, int clockStyle, ProbeSettings runtimeSettings, bool loginStartup)
    {
        _configuration.Text = save ? "正在保存配置，请等待实际 I/O 完成……" : "正在读取配置……";
        try
        {
            var settings = await Task.Run(() =>
            {
                // 每次保存重新验证磁盘内容，禁止以默认配置覆盖已经损坏的文件。
                var current = ProbeSettings.Load(_settingsPath);
                if (save)
                {
                    current.OffsetMs = offset;
                    current.DisplayWidthUnits = width;
                    current.PageHoldMs = hold;
                    current.ClockStyle = clockStyle;
                    current.ApplyRuntimeOverrides(runtimeSettings);
                    current.Save(_settingsPath);
                }
                return current;
            });
            _settingsValid = true;
            // 使用实际保存的捕获值，不能把 I/O 期间仍可编辑的 Draft 当作已保存。
            _savedParameters = DesktopParameters.From(settings);
            if (!save)
            {
                _player.SelectedValue = settings.Player;
                _offset.Value = settings.OffsetMs;
                _width.SelectedItem = settings.DisplayWidthUnits;
                if (!_hold.Items.Contains(settings.PageHoldMs)) _hold.Items.Add(settings.PageHoldMs);
                _hold.SelectedItem = settings.PageHoldMs;
                _clockStyle.SelectedItem = settings.ClockStyle;
                _idleClock.IsChecked = settings.IdleClockEnabled;
                SelectMilliseconds(_idleClockDelay, settings.IdleClockDelayMs);
                _trackIntro.IsChecked = settings.TrackIntroEnabled;
                SelectMilliseconds(_trackIntroHold, settings.TrackIntroHoldMs);
                SelectMilliseconds(_noLyricsTitleHold, settings.NoLyricsTitleHoldMs);
                SelectMilliseconds(_pollInterval, settings.PollIntervalMs);
                SelectMilliseconds(_trackSettle, settings.TrackSettleMs);
                SelectMilliseconds(_requestTimeout, settings.RequestTimeoutMs);
            }
            _lastConfigurationResult = save ? "JSON 已保存，正在确认自启设置。 " : "配置已加载。 ";
            _configuration.Text = save ? "JSON 配置已保存；授权未保存。" : "配置已加载；未自动联网或写屏。";
        }
        catch (Exception exception)
        {
            _settingsValid = false;
            _configurationError = $"配置读取或保存失败（{exception.GetType().Name}），请修复配置后重新加载";
            _lastConfigurationResult = "配置失败，自启更改未应用。 ";
            _configuration.Text = $"配置读取或保存失败（{exception.GetType().Name}）。已禁用启动和保存；请手动修复下方文件，再点“重新加载”。未用默认值覆盖坏配置。";
        }
        finally
        {
            // JSON 失败时只读自启状态，不应用未完整保存的更改；仍由退出互锁等待本任务。
            bool applyStartup = save && _settingsValid;
            try
            {
                bool enabled = await Task.Run(() =>
                {
                    if (applyStartup) _startup.Apply(loginStartup);
                    bool actual = _startup.ReadEnabled();
                    if (applyStartup && actual != loginStartup) throw new IOException("自启状态在确认期间变化。");
                    return actual;
                });
                _loginStartup.IsChecked = enabled;
                _savedStartup = enabled;
                _startupStatus.Text = "";
                if (applyStartup) _lastConfigurationResult = $"全部设置已保存（{DateTime.Now:HH:mm:ss}），自启已回读确认；不含授权。 ";
            }
            catch (Exception exception)
            {
                string actual;
                try
                {
                    bool enabled = await Task.Run(_startup.ReadEnabled);
                    _loginStartup.IsChecked = enabled;
                    actual = enabled ? "回读实际状态：已启用。" : "回读实际状态：已关闭。";
                }
                catch { _loginStartup.IsChecked = null; actual = "实际状态未知，未标示为开启或关闭；请重新加载，或明确选择后保存修复。"; }
                _savedStartup = null;
                if (_settingsValid) _configurationError = save ? "部分设置保存失败：自启未确认，请查看高级设置" : "自启状态读取失败，请查看高级设置";
                _startupStatus.Text = $"自启读取或应用失败（{exception.GetType().Name}）。{actual}";
                _lastConfigurationResult = _settingsValid
                    ? (save ? "JSON 已保存，但自启未确认成功；并非全部保存成功。 " : "JSON 已加载，但自启状态未确认。 ")
                    : "配置失败，自启更改未应用。 ";
            }
            _configuration.Text += "\n" + _settingsPath + "\n自启开关独立存储在当前用户注册表，不在此 JSON 内。";
            _configurationBusy = false;
            UpdateControls();
        }
    }

    private void Start()
    {
        if (_exiting || _running || _stopping || _clockBusy || _configurationBusy || !_settingsValid || _authorization.IsChecked != true) return;
        int capturedOffset = (int)_offset.Value;
        _running = true;
        _session = new DesktopSession();
        _stopTask = null;
        _cancellation = new CancellationTokenSource();
        lock (_logLock) _pendingLogs.Clear();
        _visibleLogs.Clear();
        _logs.Clear();
        Volatile.Write(ref _acceptLogs, 1);
        _status.Text = "正在启动，等待曲目与设备初始化";
        UpdateControls();
        _runTask = RunSessionAsync(capturedOffset, _cancellation, (int)_width.SelectedItem, (int)_hold.SelectedItem, CaptureRuntimeSettings());
    }

    private async Task RunSessionAsync(int offset, CancellationTokenSource cancellation, int width, int hold, ProbeSettings runtimeSettings)
    {
        try
        {
            // 设备或配置 I/O 可能同步阻塞，不能在 UI 线程直接调用入口。
            int result = await Task.Run(() => Program.RunDesktopAsync(offset, _settingsPath, false, cancellation.Token, width, hold, runtimeSettings, _session));
            _status.Text = result == 0 ? "运行任务已退出；不会自动重新启动。" : $"运行任务已退出（返回码 {result}）；请查看安全日志，勿视为清屏成功。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "运行任务已响应取消并退出；清屏结果以安全日志为准。";
        }
        catch (Exception exception)
        {
            _status.Text = $"运行任务异常退出（{exception.GetType().Name}）；未自动重试，不能确认清屏成功。";
        }
        finally
        {
            Volatile.Write(ref _acceptLogs, 0);
            DrainLogs(null, EventArgs.Empty);
            _session?.End();
            RefreshSessionPanel();
            _running = false;
            _authorization.IsChecked = false;
            // 停止流程可能仍在等待取消回调，交由该流程在回调结束后释放。
            if (!_stopping)
            {
                cancellation.Dispose();
                _cancellation = null;
            }
            UpdateControls();
        }
    }

    private Task StopAsync()
    {
        if (_stopTask is not null) return _stopTask;
        if (!_running || _runTask is null || _cancellation is null) return Task.CompletedTask;
        _stopping = true;
        _session?.Stop();
        RefreshSessionPanel();
        _status.Text = "正在请求取消，等待运行任务退出及正常清屏流程；I/O 阻塞时将继续等待，不强杀。";
        UpdateControls();
        _stopTask = CancelAndWaitAsync(_cancellation, _runTask);
        return _stopTask;
    }

    private async Task CancelAndWaitAsync(CancellationTokenSource cancellation, Task run)
    {
        try
        {
            try { await cancellation.CancelAsync(); }
            catch (Exception exception)
            {
                _status.Text = $"取消回调异常（{exception.GetType().Name}），仍在等待运行任务真实退出。";
            }
            await run;
        }
        finally
        {
            cancellation.Dispose();
            _cancellation = null;
            _stopping = false;
            UpdateControls();
        }
    }

    private async Task SwitchClockAsync()
    {
        _clockBusy = true;
        int style = (int)_clockStyle.SelectedItem;
        UpdateControls();
        try
        {
            await StopAsync(); // Await the lyric writer and its final clear before mode switch.
            if (_exiting) return;
            _status.Text = "正在请求切换时钟；等待USB写入完成……";
            bool? result = await Task.Run(() =>
            {
                var device = ScreenOutput.Enumerate(true);
                if (device is null) return (bool?)null;
                return device.ShowClock(style);
            });
            _status.Text = result switch
            {
                true => $"时钟样式 {style} 指令已发送；请确认屏幕，不代表已校准时间。",
                false => "时钟写入失败；未重试，无法确认设备当前显示。",
                null => "未找到唯一可用USB屏幕端点，未发送时钟指令。请确认USB模式；蓝牙模式下本机USB接口会消失。"
            };
        }
        catch (Exception exception)
        {
            _status.Text = $"时钟切换异常（{exception.GetType().Name}）；未重试，实际显示待确认。";
        }
        finally
        {
            _clockBusy = false;
            UpdateControls();
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        ShowFromTray();
        UpdateControls();
        await StopAsync();
        if (_clockTask is not null) await _clockTask;
        if (_configurationTask is not null) await _configurationTask;
        _allowClose = true;
        Dispose();
        System.Windows.Application.Current.Shutdown(0);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_exiting || _closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            var dialog = new Window
            {
                Title = "关闭 PixelLyrics", Owner = this, Width = 460,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, Icon = Icon,
                FontFamily = FontFamily, FontSize = 14
            };
            dialog.Resources = Resources;
#pragma warning disable WPF0001
            dialog.ThemeMode = ThemeMode;
#pragma warning restore WPF0001
            dialog.SetResourceReference(Control.BackgroundProperty, "PageBrush");
            dialog.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "接下来如何处理？", FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(Text("缩到托盘：隐藏窗口，当前歌词任务继续运行。\n退出程序：先停止任务并尝试清屏，再安全退出。\n未保存的设置不会自动写入，请需要时先取消并保存。"));
            var buttons = new WrapPanel();
            var tray = MakeButton("缩到托盘");
            var quit = MakeButton("退出程序");
            var cancel = MakeButton("取消");
            int choice = 0;
            tray.Click += (_, _) => { choice = 1; dialog.Close(); };
            quit.Click += (_, _) => { choice = 2; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            cancel.IsCancel = true;
            buttons.Children.Add(tray); buttons.Children.Add(quit); buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            void SyncDialogTheme()
            {
#pragma warning disable WPF0001
                dialog.ThemeMode = ThemeMode;
#pragma warning restore WPF0001
            }
            _theme.Applied += SyncDialogTheme;
            try { dialog.ShowDialog(); }
            finally { _theme.Applied -= SyncDialogTheme; }
            if (choice == 1) Hide();
            // Defer shutdown until WPF has completed the original cancelled Closing event.
            else if (choice == 2) Dispatch(async () => await ExitAsync());
        }
        finally { _closePromptOpen = false; }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Dispatch(Action action)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(action);
    }

    private void ReceiveLog(string message)
    {
        if (Volatile.Read(ref _acceptLogs) == 0 || string.IsNullOrEmpty(message)) return;
        // 仅订阅主入口的安全日志事件，不捕获 Console 或异常正文。
        string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] " + new string(message.Take(400).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        lock (_logLock)
        {
            if (_pendingLogs.Count == 150) _pendingLogs.Dequeue();
            _pendingLogs.Enqueue(line);
        }
    }

    private void DrainLogs(object? sender, EventArgs e)
    {
        RefreshSessionPanel();
        if (_running && !_stopping && !_clockBusy && _session?.Latest is { } live)
            _status.Text = live.NetworkBlocked ? "本次联网已锁止，请停止后重新开始" : "";
        bool changed = false;
        lock (_logLock)
        {
            while (_pendingLogs.Count > 0)
            {
                if (_visibleLogs.Count == 150) _visibleLogs.Dequeue();
                _visibleLogs.Enqueue(_pendingLogs.Dequeue());
                changed = true;
            }
        }
        if (!changed) return;
        _logs.Text = string.Join(Environment.NewLine, _visibleLogs);
        _logs.ScrollToEnd();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _theme.Dispose();
        Volatile.Write(ref _acceptLogs, 0);
        Program.DesktopLog -= ReceiveLog;
        _logTimer.Stop();
        _logTimer.Tick -= DrainLogs;
        _tray.Visible = false;
        _tray.Dispose();
        _trayMenu.Dispose();
    }
}

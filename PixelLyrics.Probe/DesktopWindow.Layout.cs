using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PixelLyrics.Probe;

internal sealed partial class DesktopWindow
{
    private readonly Grid _pages = new() { ClipToBounds = true };
    private readonly List<FrameworkElement> _pageViews = new();
    private readonly TextBlock _pageName = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _settingsActions = new() { Orientation = Orientation.Horizontal };
    private readonly Button _back = MakeButton("设置");
    private readonly TextBox _screenPlayer = ScreenText("");
    private readonly TextBlock[] _diagnosticValues = Enumerable.Range(0, 6).Select(_ => Text("—")).ToArray();
    private int _pageIndex;
    private int _navigationVersion;

    private static void IconLabel(Button button, string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        AutomationProperties.SetName(button, label);
    }

    private void BuildConsoleLayout(out Button earlier, out Button later, out Button exit)
    {
        var root = new DockPanel { Margin = new Thickness(24, 16, 24, 16), MaxWidth = 1120 };
        root.SetResourceReference(Panel.BackgroundProperty, "PageBrush");
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
        var header = new DockPanel { Height = 44, Margin = new Thickness(0, 0, 0, 16) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        var settings = _back;
        var more = MakeButton("更多");
        IconLabel(settings, "\uE713", "设置");
        IconLabel(more, "\uE712", "更多");
        foreach (var button in new[] { settings, more }) { button.Width = 96; button.Height = 40; button.Margin = new Thickness(8, 0, 0, 0); navigation.Children.Add(button); }
        DockPanel.SetDock(navigation, Dock.Right);
        header.Children.Add(navigation);
        _back.Width = 144;
        _recheck.Visibility = Visibility.Collapsed;
        _back.Click += (_, _) => Navigate(_pageIndex == 0 ? 1 : _pageIndex == 2 ? 1 : 0);
        var logo = new Image { Width = 30, Height = 30, Source = AppIcon.CreateImage(), Margin = new Thickness(0, 0, 10, 0) };
        Icon = logo.Source;
        DockPanel.SetDock(logo, Dock.Left); header.Children.Add(logo);
        _pageName.Text = "PixelLyrics"; _pageName.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_pageName);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0) };
        footer.Children.Add(_status);
        var feedback = new DockPanel();
        IconLabel(_reload, "\uE72C", "重新加载"); IconLabel(_save, "\uE74E", "保存设置");
        _save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _settingsActions.Children.Add(_reload); _settingsActions.Children.Add(_save);
        _settingsActions.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_settingsActions, Dock.Right); feedback.Children.Add(_settingsActions);
        _saveHint.VerticalAlignment = VerticalAlignment.Center; feedback.Children.Add(_saveHint); footer.Children.Add(feedback);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(_pages);

        var console = new Grid { MinHeight = 360 };
        console.RowDefinitions.Add(new RowDefinition());
        console.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var screen = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        screen.RowDefinitions.Add(new RowDefinition());
        screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _headline.SetResourceReference(TextBlock.ForegroundProperty, "ScreenStatusBrush");
        AutomationProperties.SetName(_headline, "播放状态");
        AutomationProperties.SetName(_screenPlayer, "当前播放器");
        AutomationProperties.SetName(_artist, "歌手");
        AutomationProperties.SetName(_check, "歌词匹配状态");
        _screenPlayer.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("SelectedValue") { Source = _player, Converter = new PlayerNameConverter() });
        _screenPlayer.SetResourceReference(TextBlock.ForegroundProperty, "ScreenMutedBrush");
        _playbackIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ScreenStatusBrush");
        _playbackIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ScreenStatusBrush");
        var playbackStatus = new StackPanel { Orientation = Orientation.Horizontal };
        playbackStatus.Children.Add(_playbackIcon); playbackStatus.Children.Add(_headline);
        screen.Children.Add(InlineActions(playbackStatus, _screenPlayer, 0));
        var song = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "ScreenTextBrush");
        var titleTip = new ToolTip { PlacementTarget = _title };
        titleTip.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Text") { Source = _title });
        _title.ToolTip = titleTip;
        _title.GotKeyboardFocus += (_, _) => titleTip.IsOpen = true;
        _title.LostKeyboardFocus += (_, _) => titleTip.IsOpen = false;
        _title.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) titleTip.IsOpen = false; };
        AutomationProperties.SetName(_title, "当前歌曲");
        song.Children.Add(_title); _artist.FontSize = 16;
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "ScreenMutedBrush");
        song.Children.Add(_artist);
        var songRow = new Grid();
        songRow.ColumnDefinitions.Add(new ColumnDefinition());
        songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        songRow.Children.Add(song); BuildRhythm(); Grid.SetColumn(_rhythm, 1); songRow.Children.Add(_rhythm);
        Grid.SetRow(songRow, 1); screen.Children.Add(songRow);
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        _check.VerticalAlignment = VerticalAlignment.Center; _check.Margin = new Thickness(0);
        _output.VerticalAlignment = VerticalAlignment.Center; _output.Margin = new Thickness(0);
        _recheck.VerticalAlignment = VerticalAlignment.Center; _recheck.Margin = new Thickness(8, 0, 0, 0);
        _recheck.MinHeight = 32; _recheck.Padding = new Thickness(6, 4, 6, 4);
        var match = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; match.Children.Add(_check); match.Children.Add(_recheck);
        var matchAndError = new StackPanel(); matchAndError.Children.Add(match); matchAndError.Children.Add(_output);
        bottom.Children.Add(matchAndError);
        _output.HorizontalAlignment = HorizontalAlignment.Left;
        _check.SetResourceReference(TextBlock.ForegroundProperty, "ScreenMutedBrush");
        _output.SetResourceReference(TextBlock.ForegroundProperty, "ScreenMutedBrush");
        var bottomFrame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        bottomFrame.SetResourceReference(Border.BorderBrushProperty, "ScreenLineBrush");
        Grid.SetRow(bottomFrame, 2); screen.Children.Add(bottomFrame);
        var screenFrame = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = screen, MinHeight = 220, Margin = new Thickness(0, 0, 0, 16) };
        screenFrame.SetResourceReference(Border.BackgroundProperty, "ScreenBrush"); screenFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); console.Children.Add(screenFrame);
        var controls = new StackPanel();
        _player.DisplayMemberPath = "";
        var playerTemplate = new DataTemplate();
        var playerText = new FrameworkElementFactory(typeof(TextBlock));
        playerText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value"));
        playerTemplate.VisualTree = playerText; _player.ItemTemplate = playerTemplate;
        _player.ItemsSource = Enum.GetValues<PlayerMode>().Select(p => new KeyValuePair<PlayerMode, string>(p, p.DisplayName())); _player.SelectedValue = PlayerMode.Auto;
        var playerRow = new StackPanel { Orientation = Orientation.Horizontal };
        playerRow.Children.Add(new Label { Content = "播放器", Target = _player, Width = 88, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        _player.Width = 180; _player.Height = 34; AutomationProperties.SetName(_player, "播放器"); playerRow.Children.Add(_player);
        _authorization.Content = new TextBlock { Text = "允许本次联网获取 QQ、网易云音乐歌词并输出到 USB 歌词屏", TextWrapping = TextWrapping.Wrap };
        _authorization.Margin = new Thickness(0, 12, 0, 0); AutomationProperties.SetName(_authorization, "允许本次联网获取 QQ、网易云音乐歌词并输出到 USB 歌词屏");
        IconLabel(_start, "\uE768", "开始运行"); IconLabel(_stop, "\uE71A", "停止并清屏"); IconLabel(_recheck, "\uE72C", "重新核对曲目");
        _start.Width = 136; _stop.Width = 136;
        _start.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(_start); actions.Children.Add(_stop);
        controls.Children.Add(InlineActions(playerRow, actions, 0)); controls.Children.Add(_authorization);
        _recheck.SetResourceReference(StyleProperty, "TextActionStyle");
        _recheck.SetResourceReference(Control.ForegroundProperty, "ScreenTextBrush");
        var operations = new Border { Child = controls, Padding = new Thickness(20, 14, 20, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        operations.SetResourceReference(Border.BackgroundProperty, "CardBrush"); operations.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(operations, 1); console.Children.Add(operations); AddPage(console, false);

        var display = new StackPanel(); display.Children.Add(_runtimeHint);
        var advancedButton = MakeButton("高级设置"); IconLabel(advancedButton, "", "高级设置"); advancedButton.Click += (_, _) => Navigate(2);
        var timing = new StackPanel();
        var timingHeading = new DockPanel(); DockPanel.SetDock(_offsetText, Dock.Right); _offsetText.Margin = new Thickness(0, 0, 20, 0); timingHeading.Children.Add(_offsetText); timingHeading.Children.Add(Heading("", "同步微调")); timing.Children.Add(timingHeading);
        AutomationProperties.SetName(_offset, "歌词时间偏移（毫秒）"); _offset.Margin = new Thickness(0, 0, 0, 4); timing.Children.Add(_offset);
        earlier = MakeButton("提前 100 ms"); later = MakeButton("延后 100 ms"); var reset = MakeButton("归零"); reset.Click += (_, _) => _offset.Value = 0;
        var steps = new StackPanel { Orientation = Orientation.Horizontal }; steps.Children.Add(earlier); steps.Children.Add(later); steps.Children.Add(reset); foreach (var button in new[] { earlier, later, reset }) { button.MinHeight = 34; button.Padding = new Thickness(12, 4, 12, 4); } timing.Children.Insert(1, Text("负值提前，正值延后")); timing.Children.Add(steps);
        display.Children.Add(timing); display.Children.Add(Rule());
        var lyrics = new StackPanel(); lyrics.Children.Add(Setting("显示宽度", _width, "以英文字符估算，汉字约占 2 单位；每段受 54 字节限制。")); lyrics.Children.Add(Setting("每段停留（ms）", _hold)); lyrics.Children.Add(ToggleRow("新曲显示歌名与歌手", _trackIntro)); lyrics.Children.Add(Setting("开场时长（ms）", _trackIntroHold));
        var clock = new StackPanel(); clock.Children.Add(Setting("时钟样式", _clockStyle)); clock.Children.Add(Setting("等待时间（ms）", _idleClockDelay)); clock.Children.Add(ToggleRow("空闲自动回钟", _idleClock));
        IconLabel(_clock, "\uE823", "停止歌词并显示时钟"); _clock.Margin = new Thickness(0, 0, 0, 0); _clock.MinHeight = 34; _clock.Padding = new Thickness(8, 4, 8, 4); clock.Children.Add(_clock); clock.Children.Add(Text("恢复歌词需重新授权并开始"));
        display.Children.Add(Columns(Section("\uE8D2", "歌词显示", lyrics), Section("\uE823", "时钟", clock)));
        advancedButton.MinHeight = 34; advancedButton.Padding = new Thickness(12, 4, 12, 4); advancedButton.Margin = new Thickness(0, 4, 0, 0); display.Children.Add(advancedButton);
        AddPage(display);

        var advanced = new StackPanel();
        var parameters = new StackPanel(); parameters.Children.Add(Setting("无词停留（ms）", _noLyricsTitleHold, "无可信匹配或确认无词时生效；网络故障不视为无词。")); parameters.Children.Add(Setting("会话轮询（ms）", _pollInterval)); parameters.Children.Add(Setting("曲目稳定（ms）", _trackSettle)); parameters.Children.Add(Setting("请求超时（ms）", _requestTimeout));
        var startup = new StackPanel(); _loginStartup.Margin = new Thickness(0, 10, 0, 10); startup.Children.Add(_loginStartup); startup.Children.Add(Text("不会自动开始歌词同步")); startup.Children.Add(_startupStatus);
        advanced.Children.Add(Columns(Section("\uE8A5", "曲目跟随", parameters), Section("\uE7F4", "登录自启", startup))); AddPage(advanced);

        var diagnostics = new Grid();
        diagnostics.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        diagnostics.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        var session = new StackPanel(); session.Children.Add(Heading("\uE9D9", "当前会话"));
        var summary = new Grid(); summary.ColumnDefinitions.Add(new ColumnDefinition()); summary.ColumnDefinitions.Add(new ColumnDefinition());
        string[] names = { "播放器", "会话", "当前曲目", "运行状态", "歌词核对", "输出" };
        for (int i = 0; i < names.Length; i++)
        {
            if (i % 2 == 0) summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            var caption = Text(names[i]); caption.Width = 88; pair.Children.Add(caption); pair.Children.Add(_diagnosticValues[i]);
            _diagnosticValues[i].MaxWidth = 260;
            _diagnosticValues[i].SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetRow(pair, i / 2); Grid.SetColumn(pair, i % 2); summary.Children.Add(pair);
        }
        session.Children.Add(summary);
        var details = new StackPanel(); details.Children.Add(_sessionPanel); details.Children.Add(_parameterHint); details.Children.Add(_configuration); details.Children.Add(Text(_settingsPath));
        session.Children.Add(new Expander { Header = "参数与配置详情", Content = details });
        diagnostics.Children.Add(new ScrollViewer { Content = session, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var logPanel = new DockPanel(); var divider = Rule(); DockPanel.SetDock(divider, Dock.Top); logPanel.Children.Add(divider);
        var logHeading = Heading("\uE9D5", "安全日志"); DockPanel.SetDock(logHeading, Dock.Top); logPanel.Children.Add(logHeading);
        _logs.Height = double.NaN; _logs.FontSize = 14; _logs.SetResourceReference(Control.BackgroundProperty, "CardBrush"); _logs.SetResourceReference(Control.ForegroundProperty, "TextBrush"); AutomationProperties.SetName(_logs, "安全日志"); logPanel.Children.Add(_logs);
        Grid.SetRow(logPanel, 1); diagnostics.Children.Add(logPanel); AddPage(diagnostics, scroll: false);
        var menu = new ContextMenu(); var diagnosticItem = new MenuItem { Header = "诊断", Icon = MenuIcon("\uE9D9") }; diagnosticItem.Click += (_, _) => Navigate(3); menu.Items.Add(diagnosticItem); menu.Items.Add(new Separator());
        exit = MakeButton("退出程序"); var exitItem = new MenuItem { Header = "退出程序…", Icon = MenuIcon("\uE7E8") }; var exitButton = exit; exitItem.Click += (_, _) => exitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); menu.Items.Add(exitItem);
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
        Content = root;
    }

    private static FrameworkElement Heading(string glyph, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (title == "曲目跟随")
        {
            var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 1,3 L 11,3 M 1,8 L 8,8 M 1,13 L 8,13 M 12,7 L 17,10 L 12,13 Z"), StrokeThickness = 1.5, Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
            row.Children.Add(icon);
        }
        else row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold }); return row;
    }
    private static FrameworkElement Section(string glyph, string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(0) }; panel.Children.Add(Heading(glyph, title)); panel.Children.Add(content); return panel;
    }
    private static FrameworkElement ToggleRow(string title, CheckBox control)
    {
        control.Content = null; AutomationProperties.SetName(control, title); return Setting(title, control);
    }
    private static TextBlock MenuIcon(string glyph) => new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 };
    private static Border Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); return line;
    }
    private void AddPage(FrameworkElement content, bool card = true, bool scroll = true)
    {
        var page = new Border { Child = scroll ? new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : content, Padding = new Thickness(card ? 16 : 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(card ? 1 : 0), Visibility = _pageViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed };
        if (card) { page.SetResourceReference(Border.BackgroundProperty, "CardBrush"); page.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); }
        _pageViews.Add(page); _pages.Children.Add(page);
    }
    private sealed class PlayerNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is MusicPlayer player ? player.DisplayName() : "";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private void Navigate(int index)
    {
        if (_pageIndex == index) return;
        int previous = _pageIndex; _pageIndex = index; int version = ++_navigationVersion;
        foreach (var page in _pageViews) { if (page.RenderTransform is TranslateTransform old) old.BeginAnimation(TranslateTransform.XProperty, null); page.Visibility = Visibility.Collapsed; page.RenderTransform = Transform.Identity; }
        var incoming = _pageViews[index]; incoming.Visibility = Visibility.Visible;
        _pageName.Text = new[] { "PixelLyrics", "设置", "高级设置", "诊断" }[index];
        IconLabel(_back, index == 0 ? "\uE713" : "\uE72B", index == 0 ? "设置" : index == 2 ? "返回设置" : "返回控制台");
        _settingsActions.Visibility = index is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        if (SystemParameters.ClientAreaAnimation)
        {
            var outgoing = _pageViews[previous]; outgoing.Visibility = Visibility.Visible;
            var leave = new TranslateTransform(); outgoing.RenderTransform = leave;
            var departure = new DoubleAnimation(0, (index > previous ? -1 : 1) * _pages.ActualWidth, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            departure.Completed += (_, _) => { if (_navigationVersion == version && _pageIndex != previous) outgoing.Visibility = Visibility.Collapsed; };
            leave.BeginAnimation(TranslateTransform.XProperty, departure);
            var transform = new TranslateTransform(); incoming.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation((index > previous ? 1 : -1) * _pages.ActualWidth, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (index != 0) _back.Focus();
    }
}

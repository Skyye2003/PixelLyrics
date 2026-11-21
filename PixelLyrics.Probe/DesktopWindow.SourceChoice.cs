using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using PixelLyrics.OnlineProbe;

namespace PixelLyrics.Probe;

internal sealed partial class DesktopWindow
{
    private readonly Button _chooseSource = MakeButton("选择歌词来源");
    private Window? _sourcePicker;
    private string? _notifiedSource;
    private string? _openedSource;
    private string? _sourceOccurrence;
    private System.Drawing.Icon? _normalTrayIcon;
    private System.Drawing.Icon? _blankTrayIcon;
    private readonly HashSet<string> _historySeen = new();

    private void RefreshSourceChoice()
    {
        var state = _session?.Latest;
        if (state is { NeedsSource: true, Song: not null, Source: not null, Track: not null })
        {
            string key = state.RunId + ":" + state.Track.Generation;
            if (_historySeen.Add(key))
                try { SourceHistory.Record(_settingsPath, state.Source.Value, state.Song, null, false); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { _status.Text = "历史记录保存失败：" + ex.Message; }
        }
        string? occurrence = state?.Track is not null ? state.RunId + ":" + state.Source + ":" + state.Track.SongId : null;
        if (occurrence is not null && occurrence != _sourceOccurrence)
        { _sourceOccurrence = occurrence; _notifiedSource = null; _openedSource = null; }
        _chooseSource.Visibility = state?.Song is not null && state.Running ? Visibility.Visible : Visibility.Collapsed;
        IconLabel(_chooseSource, "", state?.Trial == true ? "试用中 · 确认来源" : state?.Mapping is not null ? "更换来源" : "选择歌词来源");
        _chooseSource.IsEnabled = state is { Running: true, Stopping: false, NetworkBlocked: false, DeviceReady: true, Settled: true, Busy: false } && _sourcePicker is null;
        if (_tray is null) return;
        _normalTrayIcon ??= _tray.Icon;
        if (_blankTrayIcon is null)
        {
            using var bitmap = new System.Drawing.Bitmap(16, 16);
            using var stream = new System.IO.MemoryStream();
            // A transparent ICO avoids removing/reinserting the notification-area entry.
            using var png = new System.IO.MemoryStream(); bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            { writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)16); writer.Write((byte)16); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((int)png.Length); writer.Write(22); writer.Write(png.ToArray()); }
            stream.Position = 0; using var icon = new System.Drawing.Icon(stream); _blankTrayIcon = (System.Drawing.Icon)icon.Clone();
        }
        bool pending = state is { NeedsSource: true, Running: true, Stopping: false, Track: not null };
        if (pending && _notifiedSource != occurrence)
        {
            _notifiedSource = occurrence;
            if (_sourcePicker is null) PlaySourceNotification();
        }
        bool flash = pending && _openedSource != occurrence && _sourcePicker is null;
        _tray.Icon = flash && Environment.TickCount64 / 500 % 2 == 0 ? _blankTrayIcon : _normalTrayIcon;
    }

    private static void PlaySourceNotification()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var audio = typeof(DesktopWindow).Assembly.GetManifestResourceStream("PixelLyrics.Notification.wav")
                    ?? throw new InvalidOperationException("缺少通知音资源");
                using var player = new System.Media.SoundPlayer(audio);
                player.PlaySync();
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or TimeoutException)
            { SafetyLog.Append("通知音播放失败：" + ex.Message); }
        });
    }

    private void OpenSourceChoice()
    {
        if (_sourcePicker is not null) { _sourcePicker.Activate(); return; }
        var session = _session;
        var state = session?.Latest;
        if (state?.Song is null || state.Track is null || state.Source is null || !state.Running || state.Stopping || state.NetworkBlocked) return;
        _openedSource = state.RunId + ":" + state.Source + ":" + state.Track.SongId;
        session!.SourceReminderSuppressed = true;
        var song = state.Song;
        var track = state.Track;
        var playback = state.Source.Value;
        var cancellation = new CancellationTokenSource();
        var dialog = new Window { Title = "选择歌词来源", Width = 690, Height = 660, MinWidth = 600, MinHeight = 540,
            Owner = IsVisible ? this : null, WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = FontFamily, FontSize = 14 };
        var theme = new DesktopTheme(dialog);
        dialog.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="TextBox">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource CardBrush}"/>
                <Setter Property="CaretBrush" Value="{DynamicResource TextBrush}"/><Setter Property="Padding" Value="12,8"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                  <Border x:Name="frame" Background="{TemplateBinding Background}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}"><ScrollViewer x:Name="PART_ContentHost"/></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ListBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                  <Border x:Name="frame" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6" Margin="4"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="frame" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="frame" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource MutedBrush}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="SourcePlatformStyle" TargetType="RadioButton">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
                  <Border x:Name="frame" CornerRadius="8" BorderThickness="1" BorderBrush="{DynamicResource BorderBrush}" Background="{DynamicResource CardBrush}" Padding="18,10"><ContentPresenter HorizontalAlignment="Center"/></Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="frame" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="frame" Property="BorderThickness" Value="2"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """));
        dialog.SetResourceReference(BackgroundProperty, "PageBrush"); dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        _sourcePicker = dialog;
        var root = new DockPanel { Margin = new Thickness(24) }; dialog.Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock { Text = song.Title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = song.Artist + " · " + song.Album + " · " + song.Duration.ToString("m':'ss"), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) }; error.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var use = MakeButton("试用这版歌词"); use.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var remember = MakeButton("合适，记住来源"); IconLabel(remember, "", "合适，记住来源");
        var stopTrial = MakeButton("停止试用"); IconLabel(stopTrial, "", "停止试用");
        var cancel = MakeButton("取消"); var forget = MakeButton("忘记此来源"); forget.Visibility = state.Mapping is null ? Visibility.Collapsed : Visibility.Visible;
        IconLabel(use, "", "试用这版歌词"); IconLabel(cancel, "", "取消"); IconLabel(forget, "", "忘记此来源");
        use.Margin = new Thickness(0, 0, 0, 8);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(forget); actions.Children.Add(cancel); actions.Children.Add(use);
        footer.Children.Add(error); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var trialActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; trialActions.Children.Add(stopTrial); trialActions.Children.Add(remember); footer.Children.Add(trialActions);
        SourceChoice? trial = state.Trial && state.Mapping is not null ? new(track, song, playback, state.Mapping, null, Trial: true) : null;
        var searchArea = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var platforms = new StackPanel { Orientation = Orientation.Horizontal };
        var qq = new RadioButton { Content = "QQ 音乐", GroupName = "来源", IsChecked = (state.Mapping?.Platform ?? playback) == MusicPlayer.QQMusic, Margin = new Thickness(0, 0, 24, 12) };
        var ne = new RadioButton { Content = "网易云音乐", GroupName = "来源", IsChecked = qq.IsChecked != true };
        foreach (var option in new[] { qq, ne }) { option.SetResourceReference(StyleProperty, "SourcePlatformStyle"); option.Margin = new Thickness(0, 0, 8, 12); }
        platforms.Children.Add(qq); platforms.Children.Add(ne); searchArea.Children.Add(platforms);
        var query = new TextBox { Text = song.Title + " " + song.Artist, MinHeight = 36, MaxLength = 300, VerticalContentAlignment = VerticalAlignment.Center };
        var search = MakeButton("搜索"); IconLabel(search, "", "搜索"); search.Margin = new Thickness(12, 0, 0, 0);
        var searchRow = new DockPanel(); DockPanel.SetDock(search, Dock.Right); searchRow.Children.Add(search); searchRow.Children.Add(query);
        searchArea.Children.Add(new Label { Content = "关键词或歌曲链接", Target = query }); searchArea.Children.Add(searchRow);
        AutomationProperties.SetName(query, "关键词或歌曲链接");
        DockPanel.SetDock(searchArea, Dock.Top); root.Children.Add(searchArea);
        var results = new ListBox { BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        results.SetResourceReference(Control.BackgroundProperty, "CardBrush"); results.SetResourceReference(Control.ForegroundProperty, "TextBrush"); results.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetName(results, "歌词来源候选"); root.Children.Add(results);
        bool busy = false;
        MusicPlayer Platform() => ne.IsChecked == true ? MusicPlayer.NetEaseMusic : MusicPlayer.QQMusic;
        void Refresh()
        {
            trialActions.Visibility = trial is null ? Visibility.Collapsed : Visibility.Visible;
            use.IsEnabled = !busy && results.SelectedItem is ListBoxItem { Tag: SongScore { Category: MatchCategory.Accepted } };
            remember.IsEnabled = !busy && trial is not null; stopTrial.IsEnabled = !busy && trial is not null && session!.Latest.Track == track && session.Latest.Trial;
            search.IsEnabled = !busy; forget.IsEnabled = !busy; query.IsEnabled = !busy; qq.IsEnabled = !busy; ne.IsEnabled = !busy;
        }
        void Guard()
        {
            var latest = session!.Latest;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!latest.Running || latest.Stopping || latest.NetworkBlocked || !latest.DeviceReady || session.Lifecycle.IsPaused)
                throw new InvalidOperationException("当前运行不可访问歌词接口，请恢复运行后重试。");
        }
        ILyricsSource Client()
        {
            Guard();
            int timeout = session!.Latest.Active?.Timeout ?? 10000;
            var platform = Platform();
            if (platform == MusicPlayer.NetEaseMusic) return new NetEaseLyricsClient(timeout) { Recovery = session.Recoveries[platform], BeforeRequest = _ => { Guard(); return Task.CompletedTask; } };
            return new OnlineLyricsClient(timeout) { Recovery = session.Recoveries[platform], BeforeRequest = _ => { Guard(); return Task.CompletedTask; } };
        }
        void ShowError(Exception ex) => error.Text = ex is OperationCanceledException ? "操作已取消" : ex is ProbeException probe ? FollowRunner.SafeFailureReason(probe) : ex.Message;
        async Task Search()
        {
            if (busy) return; busy = true; results.Items.Clear(); error.Text = ""; Refresh();
            try
            {
                using var client = Client();
                string input = query.Text.Trim();
                if (input.Length == 0) throw new InvalidOperationException("请输入关键词或歌曲链接。");
                string[] ids;
                if (Uri.TryCreate(input, UriKind.Absolute, out _))
                    ids = [LyricSourceMappings.ParseLink(input, Platform()) ?? throw new InvalidOperationException("请使用所选平台的完整 HTTPS 歌曲链接。")];
                else ids = client is NetEaseLyricsClient netease ? await netease.SearchIdsAsync(input, cancellation.Token)
                    : (await ((OnlineLyricsClient)client).SearchAsync(input, cancellation.Token)).Select(s => s.Mid).Take(20).ToArray();
                foreach (string id in ids.Distinct().Take(20))
                {
                    Guard();
                    SongDetails details;
                    try { details = await client.GetDetailsAsync(id, cancellation.Token); }
                    catch (CandidateMetadataException) { continue; }
                    var score = SongMatcher.EvaluateTrial(details, song.Title, song.Artist, song.Album, song.Duration.TotalSeconds);
                    var text = new StackPanel { Margin = new Thickness(8) };
                    text.Children.Add(new TextBlock { Text = details.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    text.Children.Add(new TextBlock { Text = string.Join(" / ", details.Singers) + " · " + details.Album + " · " + TimeSpan.FromSeconds(details.Interval).ToString("m':'ss"), TextWrapping = TextWrapping.Wrap });
                    if (score.Category != MatchCategory.Accepted) text.Children.Add(new TextBlock { Text = score.Reason, TextWrapping = TextWrapping.Wrap });
                    results.Items.Add(new ListBoxItem { Content = text, Tag = score, IsEnabled = score.Category == MatchCategory.Accepted });
                }
                if (results.Items.Count == 0) error.Text = "未找到来源，请修改关键词或粘贴歌曲链接。";
            }
            catch (Exception ex) when (ex is ProbeException or CandidateMetadataException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException) { ShowError(ex); }
            finally
            {
                var items = results.Items.Cast<ListBoxItem>().ToDictionary(item => (SongScore)item.Tag);
                var ranked = LyricSourceMappings.RankCandidates(items.Keys, song);
                results.Items.Clear();
                foreach (var candidate in ranked) results.Items.Add(items[candidate]);
                results.SelectedItem = results.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.IsEnabled);
                if (results.SelectedItem is not null) results.ScrollIntoView(results.SelectedItem);
                busy = false; Refresh();
            }
        }
        search.Click += async (_, _) => await Search();
        query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Search(); } };
        use.Click += async (_, _) =>
        {
            if (busy || results.SelectedItem is not ListBoxItem { Tag: SongScore score } || score.Category != MatchCategory.Accepted) return;
            busy = true; error.Text = ""; Refresh();
            try
            {
                using var client = Client();
                var details = await client.GetDetailsAsync(score.Details.Mid, cancellation.Token);
                var verified = SongMatcher.EvaluateTrial(details, song.Title, song.Artist, song.Album, song.Duration.TotalSeconds);
                if (verified.Category != MatchCategory.Accepted) throw new InvalidOperationException(verified.Reason);
                var lyrics = await client.FetchAsync(details.Mid, cancellation.Token);
                Guard();
                if (!lyrics.Document.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text))) throw new InvalidOperationException("没有可同步歌词，未保存来源。");
                var choice = new SourceChoice(track, song, playback, new(Platform(), details.Mid), lyrics.Document, Trial: true);
                if (!await session!.QueueSource(choice))
                    throw new InvalidOperationException("无法试用，请确认仍在播放本曲且进度及设备正常。");
                trial = choice with { Document = null };
                error.Text = "正在试用 · 未确认";
            }
            catch (Exception ex) when (ex is ProbeException or CandidateMetadataException or NoLyricsException or UntimedLyricsException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException) { ShowError(ex); }
            finally { busy = false; Refresh(); }
        };
        remember.Click += async (_, _) =>
        {
            if (busy || trial is null) return; busy = true; Refresh();
            try
            {
                if (!await session!.QueueSource(trial with { Trial = false })) throw new InvalidOperationException("来源未保存，请检查运行状态。");
                trial = null; error.Text = "已记住来源";
            }
            catch (Exception ex) when (ex is InvalidOperationException) { ShowError(ex); }
            finally { busy = false; Refresh(); }
        };
        stopTrial.Click += async (_, _) =>
        {
            if (busy || trial is null) return; busy = true; Refresh();
            if (await session!.QueueSource(new(track, song, playback, null, null, Trial: true))) { trial = null; error.Text = "已停止试用，记录保留在历史中"; }
            else error.Text = "无法停止试用，请检查当前曲目和运行状态。";
            busy = false; Refresh();
        };
        forget.Click += async (_, _) =>
        {
            if (busy) return;
            if (MessageBox.Show(dialog, "删除本曲的来源映射并恢复自动匹配？", "忘记此来源", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            busy = true; Refresh();
            if (await session!.QueueSource(new(track, song, playback, null, null))) dialog.Close();
            else error.Text = "来源未删除，请检查运行状态后重试。";
            busy = false; Refresh();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
        qq.Checked += (_, _) => { results.Items.Clear(); Refresh(); }; ne.Checked += (_, _) => { results.Items.Clear(); Refresh(); };
        results.SelectionChanged += (_, _) => Refresh();
        foreach (var button in new[] { use, cancel, forget, search, remember, stopTrial }) { button.Height = 42; button.MinHeight = 42; button.FontSize = 15; }
        dialog.Closed += (_, _) => { cancellation.Cancel(); _sourcePicker = null; theme.Dispose(); };
        Refresh(); dialog.Show();
    }
}

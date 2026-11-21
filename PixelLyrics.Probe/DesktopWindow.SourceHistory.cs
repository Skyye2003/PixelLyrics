using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace PixelLyrics.Probe;

internal sealed partial class DesktopWindow
{
    private Window? _historyWindow;
    private void OpenSourceHistory()
    {
        if (_historyWindow is not null) { _historyWindow.Activate(); return; }
        var dialog = new Window { Title = "歌词来源历史", Width = 1040, Height = 700, MinWidth = 700, MinHeight = 500,
            Owner = IsVisible ? this : null, FontFamily = FontFamily, FontSize = 14, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _historyWindow = dialog;
        var theme = new DesktopTheme(dialog);
        dialog.SetResourceReference(BackgroundProperty, "PageBrush"); dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; dialog.Content = root;
        void Top(UIElement item) { DockPanel.SetDock(item, Dock.Top); root.Children.Add(item); }
        Top(new TextBlock { Text = "历史记录", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var navigation = new WrapPanel(); var pending = MakeButton("待确认"); var confirmed = MakeButton("已确认");
        navigation.Children.Add(pending); navigation.Children.Add(confirmed); Top(navigation);
        var query = new TextBox { MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 6, 12, 6) };
        query.SetResourceReference(Control.BackgroundProperty, "CardBrush"); query.SetResourceReference(Control.ForegroundProperty, "TextBrush"); query.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        var searchRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
        searchRow.Children.Add(new Label { Content = "搜索", Target = query, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }); searchRow.Children.Add(query);
        AutomationProperties.SetName(query, "搜索当前分类的歌名、歌手或专辑"); Top(searchRow);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var selectPage = MakeButton("全选本页"); var clear = MakeButton("取消选择"); var batch = MakeButton("忽略所选");
        IconLabel(selectPage, "", "全选本页"); IconLabel(clear, "", "取消选择");
        var selection = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 8) };
        foreach (var control in new UIElement[] { selectPage, clear, batch, selection }) toolbar.Children.Add(control); Top(toolbar);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(feedback, Dock.Bottom); root.Children.Add(feedback);
        var pager = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var previous = MakeButton("上一页"); var next = MakeButton("下一页");
        var pages = new ComboBox { Width = 96, MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        AutomationProperties.SetName(pages, "跳转到页");
        var total = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) };
        foreach (var control in new UIElement[] { total, previous, pages, next }) pager.Children.Add(control); DockPanel.SetDock(pager, Dock.Bottom); root.Children.Add(pager);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        bool showConfirmed = false, refreshing = false, saving = false; int page = 0; const int pageSize = 8;
        var selected = new HashSet<string>(StringComparer.Ordinal); var visible = new List<SourceHistoryEntry>();
        string Key(SourceHistoryEntry entry) => LyricSourceMappings.Key(entry.Playback, entry.Song.Id);
        void SelectionChanged() { selection.Text = selected.Count == 0 ? "" : $"已选 {selected.Count} 项"; batch.IsEnabled = selected.Count > 0 && !saving; clear.IsEnabled = selected.Count > 0; }
        void Hide(IEnumerable<string> keys)
        {
            try { SourceHistory.Hide(_settingsPath, keys); selected.Clear(); feedback.Text = showConfirmed ? "已移除历史记录，来源映射保留" : "已忽略"; Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { feedback.Text = "操作失败：" + ex.Message; }
        }
        void Refresh()
        {
            if (refreshing) return; refreshing = true; list.Children.Clear();
            try
            {
                var entries = SourceHistory.ForDisplay(SourceHistory.Read(_settingsPath));
                selected.IntersectWith(entries.Where(e => e.Confirmed == showConfirmed).Select(Key));
                IconLabel(pending, "", "待确认 " + entries.Count(e => !e.Confirmed)); IconLabel(confirmed, "", "已确认 " + entries.Count(e => e.Confirmed));
                pending.SetResourceReference(StyleProperty, showConfirmed ? typeof(Button) : "PrimaryButtonStyle"); confirmed.SetResourceReference(StyleProperty, showConfirmed ? "PrimaryButtonStyle" : typeof(Button));
                IconLabel(batch, "", showConfirmed ? "移除所选记录" : "忽略所选");
                string term = query.Text.Trim();
                var matches = entries.Where(e => e.Confirmed == showConfirmed && (e.Song.Title + " " + e.Song.Artist + " " + e.Song.Album + " " + e.Mapping?.SongId).Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
                int count = Math.Max(1, (matches.Count + pageSize - 1) / pageSize); page = Math.Clamp(page, 0, count - 1);
                pages.Items.Clear(); for (int i = 1; i <= count; i++) pages.Items.Add($"{i} / {count}"); pages.SelectedIndex = page;
                total.Text = $"共 {matches.Count} 首"; previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < count;
                visible = matches.Skip(page * pageSize).Take(pageSize).ToList();
                foreach (var entry in visible)
                {
                    var row = new Grid { Margin = new Thickness(12, 12, 4, 12) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var check = new CheckBox { IsChecked = selected.Contains(Key(entry)), VerticalAlignment = VerticalAlignment.Center, MinWidth = 32, MinHeight = 40, Margin = new Thickness(0, 0, 12, 0) };
                    AutomationProperties.SetName(check, "选择 " + entry.Song.Title);
                    check.Checked += (_, _) => { selected.Add(Key(entry)); SelectionChanged(); }; check.Unchecked += (_, _) => { selected.Remove(Key(entry)); SelectionChanged(); }; row.Children.Add(check);
                    var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
                    details.Children.Add(new TextBlock { Text = entry.Song.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Song.Title });
                    details.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { entry.Song.Artist, entry.Song.Album }.Where(s => !string.IsNullOrWhiteSpace(s))), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
                    Grid.SetColumn(details, 1); row.Children.Add(details);
                    var operations = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var status = new TextBlock { Text = entry.Mapping is null ? "尚未试用" : entry.Mapping.Platform.DisplayName() + (entry.Confirmed ? " · 已确认" : " · 试用未确认"), Margin = new Thickness(0, 0, 0, 8) }; operations.Children.Add(status);
                    var actions = new WrapPanel();
                    var retry = MakeButton("试用"); IconLabel(retry, "", "试用"); AutomationProperties.SetName(retry, "试用 " + entry.Song.Title);
                    retry.Click += (_, _) => { var state = _session?.Latest; if (state is { Running: true, Stopping: false, Song: not null } && state.Source == entry.Playback && SourceHistory.SameSong(entry, entry.Playback, state.Song)) OpenSourceChoice(); else feedback.Text = "请先播放这首歌并开始运行，再重新试用。"; }; actions.Children.Add(retry);
                    if (!entry.Confirmed && entry.Mapping is not null)
                    {
                        var remember = MakeButton("记住来源"); IconLabel(remember, "", "记住来源"); remember.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
                        remember.Click += async (_, _) =>
                        {
                            if (saving) return; saving = true; root.IsEnabled = false;
                            try
                            {
                                var session = _session; var state = session?.Latest;
                                if (state is { Running: true })
                                {
                                    var track = state.Source == entry.Playback && state.Song?.Id == entry.Song.Id && state.Track is not null ? state.Track : new TrackVersion(Guid.Empty, -1, entry.Song.Id, -1);
                                    if (!await session!.QueueSource(new(track, entry.Song, entry.Playback, entry.Mapping, null))) throw new InvalidOperationException("未保存，请检查运行状态或稍后重试。");
                                }
                                else lock (ProbeSettings.FileSync)
                                {
                                    var settings = ProbeSettings.Load(_settingsPath); settings.SourceMappings[Key(entry)] = entry.Mapping; settings.Save(_settingsPath); SourceHistory.Record(_settingsPath, entry.Playback, entry.Song, entry.Mapping, true);
                                }
                                feedback.Text = "已记住来源";
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { feedback.Text = "保存失败：" + ex.Message; }
                            finally { saving = false; root.IsEnabled = true; Refresh(); }
                        }; actions.Children.Add(remember);
                    }
                    if (!entry.Confirmed)
                    {
                        var ignore = MakeButton("忽略"); IconLabel(ignore, "", "忽略"); AutomationProperties.SetName(ignore, "忽略 " + entry.Song.Title); ignore.Click += (_, _) => Hide(new[] { Key(entry) }); actions.Children.Add(ignore);
                    }
                    operations.Children.Add(actions); Grid.SetColumn(operations, 2); row.Children.Add(operations);
                    var surface = new Border { Child = row, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
                    surface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                    surface.MouseEnter += (_, _) => surface.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); surface.MouseLeave += (_, _) => surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); list.Children.Add(surface);
                }
                if (visible.Count == 0) list.Children.Add(new TextBlock { Text = term.Length > 0 ? "没有匹配的记录" : showConfirmed ? "暂无已确认来源" : "暂无待确认歌曲", Margin = new Thickness(12) });
                selectPage.IsEnabled = visible.Count > 0; SelectionChanged(); scroll.ScrollToTop();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { feedback.Text = "历史记录读取失败：" + ex.Message; }
            finally { refreshing = false; }
        }
        pending.Click += (_, _) => { showConfirmed = false; page = 0; selected.Clear(); Refresh(); }; confirmed.Click += (_, _) => { showConfirmed = true; page = 0; selected.Clear(); Refresh(); };
        query.TextChanged += (_, _) => { page = 0; Refresh(); };
        selectPage.Click += (_, _) => { foreach (var entry in visible) selected.Add(Key(entry)); Refresh(); }; clear.Click += (_, _) => { selected.Clear(); Refresh(); };
        batch.Click += (_, _) => Hide(selected.ToArray());
        previous.Click += (_, _) => { page--; Refresh(); }; next.Click += (_, _) => { page++; Refresh(); };
        pages.SelectionChanged += (_, _) => { if (!refreshing && pages.SelectedIndex >= 0) { page = pages.SelectedIndex; Refresh(); } };
        dialog.Activated += (_, _) => { if (!saving) Refresh(); }; dialog.Closed += (_, _) => { _historyWindow = null; theme.Dispose(); };
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) dialog.Close(); if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { query.Focus(); query.SelectAll(); e.Handled = true; } };
        Refresh(); dialog.Show();
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PixelLyrics.Probe;

internal sealed partial class DesktopWindow
{
    private readonly System.Windows.Shapes.Path _playbackIcon = new() { Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rhythm = new() { Orientation = Orientation.Horizontal, Height = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
    private bool _rhythmActive;

    private static TextBox ScreenText(string text, double size = 14)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, FontSize = size,
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxHeight = size > 20 ? 92 : double.PositiveInfinity,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{x:Type TextBox}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><ScrollViewer x:Name='PART_ContentHost'/></ControlTemplate>")
        };
        box.SetResourceReference(Control.ForegroundProperty, "ScreenMutedBrush");
        box.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        return box;
    }

    private void BuildRhythm()
    {
        AutomationProperties.SetName(_rhythm, "播放律动（装饰动画）");
        _rhythm.ToolTip = "随播放状态律动，不代表实际音频频谱";
        for (int i = 0; i < 22; i++)
        {
            var bar = new Rectangle
            {
                Width = 5, Height = 42 + (i * 17 % 53), RadiusX = 2.5, RadiusY = 2.5,
                Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(.5, 1), RenderTransform = new ScaleTransform(1, .12), Opacity = .8
            };
            bar.SetResourceReference(Shape.FillProperty, "ScreenTextBrush");
            _rhythm.Children.Add(bar);
        }
        _rhythm.IsVisibleChanged += (_, _) => RefreshPlaybackVisuals();
        _rhythm.Unloaded += (_, _) => SetRhythmActive(false);
    }

    private void RefreshPlaybackVisuals()
    {
        string state = _headline.Text;
        string geometry = state switch
        {
            "播放中" => "M2,0 L13,7 2,14 Z",
            "已暂停" => "M1,0 L5,0 5,14 1,14 Z M9,0 L13,0 13,14 9,14 Z",
            "已停止" or "未启用" => "M1,1 L13,1 13,13 1,13 Z",
            _ => "M7,0 A7,7 0 1 1 6.9,0 M7,3 L7,7 10,9"
        };
        if (_playbackIcon.Tag as string != geometry)
        {
            _playbackIcon.Data = Geometry.Parse(geometry);
            _playbackIcon.Tag = geometry;
        }
        SetRhythmActive(state == "播放中" && _rhythm.IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);
    }

    private void SetRhythmActive(bool active)
    {
        if (_rhythmActive == active) return;
        _rhythmActive = active;
        for (int i = 0; i < _rhythm.Children.Count; i++)
        {
            var scale = (ScaleTransform)((Rectangle)_rhythm.Children[i]).RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, active ? new DoubleAnimation
            {
                From = .15 + i % 4 * .05, To = .55 + i % 5 * .11,
                Duration = TimeSpan.FromMilliseconds(320 + i * 67 % 470),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            } : null);
        }
    }
}

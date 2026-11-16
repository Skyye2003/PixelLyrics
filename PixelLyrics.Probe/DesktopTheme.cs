using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PixelLyrics.Probe;

// Window-local surfaces and controls follow the system theme.
internal sealed class DesktopTheme : IDisposable
{
    private readonly Window window;
    internal event Action? Applied;
    internal DesktopTheme(Window window)
    {
        this.window = window;
        AddLayoutStyles();
        Apply();
        SystemEvents.UserPreferenceChanged += Changed;
        SystemParameters.StaticPropertyChanged += AccessibilityChanged;
    }

    private void Changed(object sender, UserPreferenceChangedEventArgs e) => Refresh();
    private void AddLayoutStyles()
    {
        const string markup = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="{x:Type ContextMenu}">
                <Setter Property="Background" Value="{DynamicResource CardBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="{x:Type ContextMenu}"><Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="10" Padding="6"><ItemsPresenter/></Border></ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="{x:Type MenuItem}">
                <Setter Property="MinWidth" Value="184"/><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="{x:Type MenuItem}">
                  <Border x:Name="row" Background="Transparent" CornerRadius="6" Padding="10,10"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="28"/><ColumnDefinition/></Grid.ColumnDefinitions><ContentPresenter ContentSource="Icon" VerticalAlignment="Center"/><ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/></Grid></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="{x:Type Expander}"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/></Style>
              <Style TargetType="{x:Type Label}"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/></Style>
              <Style TargetType="{x:Type CheckBox}"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
              <Style TargetType="{x:Type ComboBox}">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource CardBrush}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="{x:Type ComboBox}">
                  <Grid>
                    <Border x:Name="outline" Background="{TemplateBinding Background}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4"/>
                    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" Margin="10,0,24,0" VerticalAlignment="Center" IsHitTestVisible="False"/>
                    <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template><ControlTemplate TargetType="{x:Type ToggleButton}"><Border Background="Transparent"><TextBlock Text="⌄" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,8,0" Foreground="{DynamicResource TextBrush}"/></Border></ControlTemplate></ToggleButton.Template>
                    </ToggleButton>
                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                      <Border Background="{DynamicResource CardBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                        <ScrollViewer MaxHeight="240" CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="{x:Type ComboBoxItem}"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource CardBrush}"/><Style.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger></Style.Triggers></Style>
              <Style TargetType="{x:Type Button}">
                <Setter Property="Background" Value="{DynamicResource CardBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="{x:Type Button}">
                    <Border x:Name="frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="frame" Property="Background" Value="{DynamicResource HoverBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/></Trigger>
                      <Trigger Property="IsPressed" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource TextBrush}"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="frame" Property="BorderThickness" Value="2"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style x:Key="PrimaryButtonStyle" TargetType="{x:Type Button}" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Background" Value="{DynamicResource AccentBrush}"/><Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/>
              </Style>
              <Style x:Key="TextActionStyle" TargetType="{x:Type Button}">
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="{x:Type Button}">
                      <Border x:Name="outline" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4" Padding="6,4">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.75"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style x:Key="InfoButtonStyle" TargetType="{x:Type Button}" BasedOn="{StaticResource TextActionStyle}">
                <Setter Property="Foreground" Value="{DynamicResource MutedBrush}"/>
              </Style>
            </ResourceDictionary>
            """;
        window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup));
    }
    private void AccessibilityChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(() => Apply());
    }

    internal void Apply(bool? darkOverride = null)
    {
        bool dark = darkOverride ?? (Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int value && value == 0);
        bool contrast = SystemParameters.HighContrast;
        // Window-scoped Fluent mode is experimental in WPF 10; keep opt-in local.
#pragma warning disable WPF0001
        window.ThemeMode = contrast ? ThemeMode.None : dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Brush Color(string light, string night) => new SolidColorBrush(
            (System.Windows.Media.Color)ColorConverter.ConvertFromString(dark ? night : light));
        var r = window.Resources;
        r["PageBrush"] = contrast ? SystemColors.WindowBrush : Color("#F4F5FC", "#161A2C");
        r["CardBrush"] = contrast ? SystemColors.WindowBrush : Color("#FFFFFF", "#22283E");
        r["TextBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#202842", "#F1F2FC");
        r["MutedBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#616B85", "#B3BDD8");
        r["AccentBrush"] = contrast ? SystemColors.HighlightBrush : Color("#5856D6", "#7775FF");
        r["AccentTextBrush"] = contrast ? SystemColors.HighlightTextBrush : Color("#FFFFFF", "#121A36");
        r["BorderBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#DCDFF0", "#404A6C");
        r["HoverBrush"] = contrast ? SystemColors.HighlightBrush : Color("#ECEBFF", "#343C5C");
        r["ScreenTextBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#AAA5FF", "#AAA5FF");
        r["ScreenStatusBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#38E5CB", "#38E5CB");
        r["ScreenMutedBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#B3BDD8", "#B3BDD8");
        r["ScreenLineBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#313B67", "#313B67");
        r["ScreenBrush"] = contrast ? SystemColors.WindowBrush : Color("#121A36", "#121A36");
        window.SetResourceReference(Control.BackgroundProperty, "PageBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        Applied?.Invoke();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= Changed;
        SystemParameters.StaticPropertyChanged -= AccessibilityChanged;
    }
}

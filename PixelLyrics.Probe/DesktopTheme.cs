using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PixelLyrics.Probe;

// Fluent owns control states; these resources supply the application surfaces.
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
              <ControlTemplate x:Key="PageTabsTemplate" TargetType="{x:Type TabControl}">
                <Grid>
                  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                  <TabPanel IsItemsHost="True" Margin="8,0,0,8" KeyboardNavigation.TabIndex="0"/>
                  <Border Grid.Row="1" Background="{DynamicResource CardBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="12" ClipToBounds="True">
                    <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" TextElement.Foreground="{DynamicResource TextBrush}"/>
                  </Border>
                </Grid>
              </ControlTemplate>
              <Style x:Key="PageTabStyle" TargetType="{x:Type TabItem}">
                <Setter Property="Foreground" Value="{DynamicResource MutedBrush}"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="{x:Type TabItem}">
                      <Border x:Name="focus" Background="Transparent" Padding="16,10" BorderThickness="1" BorderBrush="Transparent" CornerRadius="4">
                        <Grid><ContentPresenter ContentSource="Header" RecognizesAccessKey="True" Margin="0,0,0,6"/>
                          <Border x:Name="line" Height="3" Width="24" Background="Transparent" VerticalAlignment="Bottom" CornerRadius="1"/>
                        </Grid>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter TargetName="line" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
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
        r["PageBrush"] = contrast ? SystemColors.WindowBrush : Color("#F5F6FA", "#171A22");
        r["CardBrush"] = contrast ? SystemColors.WindowBrush : Color("#FFFFFF", "#222633");
        r["TextBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#202638", "#F2F4FA");
        r["MutedBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#5F6B7C", "#AFB8CA");
        r["AccentBrush"] = contrast ? SystemColors.HighlightBrush : Color("#6554C0", "#B4A7FF");
        r["AccentTextBrush"] = contrast ? SystemColors.HighlightTextBrush : Color("#FFFFFF", "#171A22");
        r["BorderBrush"] = contrast ? SystemColors.WindowTextBrush : Color("#DDE1EA", "#424A5D");
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

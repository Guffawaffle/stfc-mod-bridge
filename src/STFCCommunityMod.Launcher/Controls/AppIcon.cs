using System.Windows;
using FluentIcons.Common;
using FluentIcons.Wpf;

namespace STFCCommunityMod.Launcher.Controls;

public enum AppIconKind
{
    Settings,
    Search,
    Back,
    Appearance,
    SystemAppearance,
    LightAppearance,
    DarkAppearance,
    RestoreDefault,
    Notification,
    Sound,
    Add,
    Remove,
    NextLaunch,
    Warning,
    Keyboard,
    Sync,
    Save,
    Checkmark,
}

public sealed class AppIcon : SymbolIcon
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(AppIconKind),
            typeof(AppIcon),
            new PropertyMetadata(AppIconKind.Settings, OnKindChanged));

    public static readonly DependencyProperty IsFilledProperty =
        DependencyProperty.Register(
            nameof(IsFilled),
            typeof(bool),
            typeof(AppIcon),
            new PropertyMetadata(false, OnIsFilledChanged));

    public AppIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        UpdateSymbol();
        UpdateVariant();
    }

    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    private static void OnKindChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((AppIcon)dependencyObject).UpdateSymbol();
    }

    private static void OnIsFilledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((AppIcon)dependencyObject).UpdateVariant();
    }

    private void UpdateSymbol()
    {
        Symbol = Kind switch
        {
            AppIconKind.Settings => Symbol.Settings,
            AppIconKind.Search => Symbol.Search,
            AppIconKind.Back => Symbol.ArrowLeft,
            AppIconKind.Appearance => Symbol.DarkTheme,
            AppIconKind.SystemAppearance => Symbol.Color,
            AppIconKind.LightAppearance => Symbol.WeatherSunny,
            AppIconKind.DarkAppearance => Symbol.WeatherMoon,
            AppIconKind.RestoreDefault => Symbol.ArrowReset,
            AppIconKind.Notification => Symbol.Alert,
            AppIconKind.Sound => Symbol.Speaker2,
            AppIconKind.Add => Symbol.Add,
            AppIconKind.Remove => Symbol.Dismiss,
            AppIconKind.NextLaunch => Symbol.Clock,
            AppIconKind.Warning => Symbol.Warning,
            AppIconKind.Keyboard => Symbol.Keyboard,
            AppIconKind.Sync => Symbol.CloudSync,
            AppIconKind.Save => Symbol.Save,
            AppIconKind.Checkmark => Symbol.Checkmark,
            _ => Symbol.Settings,
        };
    }

    private void UpdateVariant()
    {
        IconVariant = IsFilled ? IconVariant.Filled : IconVariant.Regular;
    }
}

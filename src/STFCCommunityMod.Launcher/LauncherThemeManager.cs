using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal enum LauncherTheme
{
    Dark,
    Light,
}

internal static class LauncherThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const string WindowsThemeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0B1220",
            ["SurfaceBrush"] = "#111B2C",
            ["SurfaceMutedBrush"] = "#172337",
            ["TextPrimaryBrush"] = "#F7FAFC",
            ["TextSecondaryBrush"] = "#A9B7CA",
            ["BorderBrush"] = "#2A3950",
            ["ControlBorderBrush"] = "#60738E",
            ["AccentBrush"] = "#0B70C9",
            ["AccentHoverBrush"] = "#0A68B8",
            ["AccentForegroundBrush"] = "#FFFFFF",
            ["QuietHoverBrush"] = "#223149",
            ["FocusOuterBrush"] = "#F7FAFC",
            ["FocusInnerBrush"] = "#0B1220",
            ["SuccessBrush"] = "#57D17C",
            ["SuccessSoftBrush"] = "#183A2A",
            ["WarningBrush"] = "#F6BE4F",
            ["WarningSoftBrush"] = "#3A2F18",
            ["ErrorBrush"] = "#FF7070",
            ["ErrorSoftBrush"] = "#3C2025",
            ["DialogBackdropBrush"] = "#B3000000",
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#F5F7FA",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceMutedBrush"] = "#F0F3F7",
            ["TextPrimaryBrush"] = "#152238",
            ["TextSecondaryBrush"] = "#5C6C80",
            ["BorderBrush"] = "#D8DEE8",
            ["ControlBorderBrush"] = "#7A8798",
            ["AccentBrush"] = "#0068C0",
            ["AccentHoverBrush"] = "#005AA6",
            ["AccentForegroundBrush"] = "#FFFFFF",
            ["QuietHoverBrush"] = "#E8EEF6",
            ["FocusOuterBrush"] = "#152238",
            ["FocusInnerBrush"] = "#FFFFFF",
            ["SuccessBrush"] = "#107A3E",
            ["SuccessSoftBrush"] = "#E6F5EC",
            ["WarningBrush"] = "#9A6700",
            ["WarningSoftBrush"] = "#FFF3D6",
            ["ErrorBrush"] = "#C73535",
            ["ErrorSoftBrush"] = "#FDEAEA",
            ["DialogBackdropBrush"] = "#730B1220",
        };

    public static LauncherTheme ApplyColorMode(LauncherColorMode colorMode)
    {
        var theme = ResolveColorMode(colorMode, IsSystemLightTheme());
        return Apply(theme);
    }

    internal static LauncherTheme ResolveColorMode(
        LauncherColorMode colorMode,
        bool isSystemLightTheme) =>
        colorMode switch
        {
            LauncherColorMode.System =>
                isSystemLightTheme ? LauncherTheme.Light : LauncherTheme.Dark,
            LauncherColorMode.Light => LauncherTheme.Light,
            LauncherColorMode.Dark => LauncherTheme.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(colorMode)),
        };

    public static void ApplyWindowChrome(Window window, LauncherTheme theme)
    {
        ArgumentNullException.ThrowIfNull(window);

        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var enabled = theme == LauncherTheme.Dark ? 1 : 0;
        var result = DwmSetWindowAttribute(
            windowHandle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
        if (result != 0)
        {
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                Marshal.SizeOf<int>());
        }

        var cornerPreference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            Marshal.SizeOf<int>());
    }

    private static LauncherTheme Apply(LauncherTheme theme)
    {
        var palette = theme == LauncherTheme.Light ? LightPalette : DarkPalette;
        foreach (var (resourceName, colorValue) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorValue);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[resourceName] = brush;
        }

        return theme;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsThemeRegistryPath);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

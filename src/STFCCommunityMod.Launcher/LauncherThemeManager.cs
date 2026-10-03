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
            ["WindowBackgroundBrush"] = "#0C1421",
            ["SurfaceBrush"] = "#172233",
            ["SurfaceMutedBrush"] = "#202E43",
            ["NavigationBrush"] = "#101C2E",
            ["NavigationSelectedBrush"] = "#213D5E",
            ["TextPrimaryBrush"] = "#F1F5FC",
            ["TextSecondaryBrush"] = "#B3C3DA",
            ["BorderBrush"] = "#35455E",
            ["ControlBorderBrush"] = "#60738E",
            ["AccentBrush"] = "#145A9F",
            ["AccentHoverBrush"] = "#1969B7",
            ["AccentTextBrush"] = "#73B9FF",
            ["AccentForegroundBrush"] = "#FFFFFF",
            ["QuietHoverBrush"] = "#202E43",
            ["FocusOuterBrush"] = "#F1F5FC",
            ["FocusInnerBrush"] = "#0C1421",
            ["SuccessBrush"] = "#82D8B3",
            ["SuccessSoftBrush"] = "#183B35",
            ["WarningBrush"] = "#F4CF8A",
            ["WarningSoftBrush"] = "#3B3021",
            ["ErrorBrush"] = "#FFA1AF",
            ["ErrorSoftBrush"] = "#3C2025",
            ["DialogBackdropBrush"] = "#B3000000",
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#F6F8FC",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceMutedBrush"] = "#F0F4FB",
            ["NavigationBrush"] = "#EDF1F8",
            ["NavigationSelectedBrush"] = "#E0EDFF",
            ["TextPrimaryBrush"] = "#16233A",
            ["TextSecondaryBrush"] = "#4E6079",
            ["BorderBrush"] = "#D3DDEB",
            ["ControlBorderBrush"] = "#7A8798",
            ["AccentBrush"] = "#135BA8",
            ["AccentHoverBrush"] = "#0F5098",
            ["AccentTextBrush"] = "#155BAE",
            ["AccentForegroundBrush"] = "#FFFFFF",
            ["QuietHoverBrush"] = "#F0F4FB",
            ["FocusOuterBrush"] = "#16233A",
            ["FocusInnerBrush"] = "#FFFFFF",
            ["SuccessBrush"] = "#136346",
            ["SuccessSoftBrush"] = "#E7F5EE",
            ["WarningBrush"] = "#785012",
            ["WarningSoftBrush"] = "#FFF3D9",
            ["ErrorBrush"] = "#AA293B",
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

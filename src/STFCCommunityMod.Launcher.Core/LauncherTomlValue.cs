using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherTomlValue
{
    public static string RenderString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value);
    }

    public static bool TryReadString(
        string renderedValue,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(renderedValue);
        value = string.Empty;
        if (renderedValue.Length < 2)
        {
            return false;
        }

        if (renderedValue[0] == '\'' && renderedValue[^1] == '\'')
        {
            value = renderedValue[1..^1];
            return !value.Contains('\'');
        }

        if (renderedValue[0] != '"' || renderedValue[^1] != '"')
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<string>(renderedValue) ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

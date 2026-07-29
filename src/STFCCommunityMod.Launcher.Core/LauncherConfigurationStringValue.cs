using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherConfigurationStringValue
{
    public static bool TryNormalize(
        LauncherConfigurationSetting setting,
        string value,
        out string normalized,
        out string validationError)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(value);

        normalized = value;
        validationError = string.Empty;
        switch (ReadFormat(setting))
        {
            case "uri":
                normalized = value.Trim();
                if (normalized.Length > 0
                    && (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                        || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    validationError = "Enter an absolute HTTP or HTTPS URL, or leave the value empty.";
                    return false;
                }
                break;

            case "comma-separated-list":
                if (value.Any(char.IsControl))
                {
                    validationError = "Names cannot contain control characters.";
                    return false;
                }

                normalized = string.Join(
                    ", ",
                    value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                break;
        }

        return true;
    }

    public static string? ReadFormat(LauncherConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.ValueTypeDefinition.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.String
                ? format.GetString()
                : null;
    }
}

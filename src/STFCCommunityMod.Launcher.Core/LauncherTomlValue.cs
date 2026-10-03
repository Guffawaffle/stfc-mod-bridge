using System.Globalization;
using System.Text;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherTomlValue
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string RenderInteger(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public static string RenderNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A TOML number must be finite.");
        }

        var rendered = value.ToString("R", CultureInfo.InvariantCulture);
        return rendered.IndexOfAny(['.', 'E', 'e']) >= 0
            ? rendered
            : rendered + ".0";
    }

    public static bool TryReadInteger(string renderedValue, out long value)
    {
        value = default;
        var normalized = renderedValue.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var signLength = normalized[0] is '+' or '-' ? 1 : 0;
        if (signLength == 0
            && normalized.Length > 2
            && normalized[0] == '0')
        {
            var numberBase = char.ToLowerInvariant(normalized[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0,
            };
            if (numberBase != 0)
            {
                var digits = normalized[2..];
                if (!TryRemoveNumericSeparators(digits, IsBaseDigit, numberBase, out var compact)
                    || !ConvertUnsigned(compact, numberBase, out var unsigned)
                    || unsigned > long.MaxValue)
                {
                    return false;
                }

                value = (long)unsigned;
                return true;
            }
        }

        var unsignedDigits = normalized[signLength..];
        if (!TryRemoveNumericSeparators(unsignedDigits, IsDecimalDigit, 10, out var decimalDigits))
        {
            return false;
        }

        var decimalValue = signLength == 0
            ? decimalDigits
            : normalized[..signLength] + decimalDigits;
        return long.TryParse(
            decimalValue,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    public static bool TryReadNumber(string renderedValue, out double value)
    {
        value = default;
        var normalized = renderedValue.Trim();
        if (!TryRemoveNumericSeparators(normalized, IsDecimalDigit, 10, out var compact))
        {
            return false;
        }

        return double.TryParse(
            compact,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && !double.IsInfinity(value)
        && !double.IsNaN(value);
    }

    public static string RenderString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder("\"");
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("A TOML string must contain valid Unicode.", nameof(value));
                result.Append(character).Append(value[++index]);
                continue;
            }
            if (char.IsLowSurrogate(character))
                throw new ArgumentException("A TOML string must contain valid Unicode.", nameof(value));
            result.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\t' => "\\t", '\n' => "\\n",
                '\f' => "\\f", '\r' => "\\r",
                < ' ' or '\u007f' => "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        }
        return result.Append('"').ToString();
    }

    private static bool TryRemoveNumericSeparators(
        string value,
        Func<char, int, bool> isDigit,
        int numberBase,
        out string compact)
    {
        compact = value;
        if (!value.Contains('_'))
        {
            return value.Length > 0;
        }

        for (var index = 0; index < value.Length; ++index)
        {
            if (value[index] != '_')
            {
                continue;
            }

            if (index == 0
                || index == value.Length - 1
                || !isDigit(value[index - 1], numberBase)
                || !isDigit(value[index + 1], numberBase))
            {
                return false;
            }
        }

        compact = value.Replace("_", string.Empty, StringComparison.Ordinal);
        return true;
    }

    private static bool IsDecimalDigit(char value, int numberBase)
    {
        _ = numberBase;
        return value is >= '0' and <= '9';
    }

    private static bool IsBaseDigit(char value, int numberBase)
    {
        var digit = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
        return digit >= 0 && digit < numberBase;
    }

    private static bool ConvertUnsigned(string value, int numberBase, out ulong result)
    {
        result = 0;
        foreach (var character in value)
        {
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= numberBase)
            {
                return false;
            }

            var unsignedDigit = (ulong)digit;
            if (result > (ulong.MaxValue - unsignedDigit) / (ulong)numberBase)
            {
                return false;
            }

            result = (result * (ulong)numberBase) + unsignedDigit;
        }

        return value.Length > 0;
    }

    public static bool TryReadString(string renderedValue, out string value)
    {
        ArgumentNullException.ThrowIfNull(renderedValue);
        value = string.Empty;
        try { StrictUtf8.GetByteCount(renderedValue); }
        catch (EncoderFallbackException) { return false; }
        var decoded = TomlNativeRuntime.Request(new("decode_string", string.Empty, Value: renderedValue));
        if (!decoded.Ok || decoded.Value is null) return false;
        value = decoded.Value;
        return true;
    }
}

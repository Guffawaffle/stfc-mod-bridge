namespace STFCCommunityMod.Launcher.Core;

[Flags]
public enum LauncherKeybindingModifierGroups
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Windows = 1 << 3,
    Command = 1 << 4,
    AltGr = 1 << 5,
}

public sealed record LauncherKeybindingChord(
    string Canonical,
    string Key,
    LauncherKeybindingModifierGroups EffectiveModifiers)
{
    public string Display =>
        string.Join(
            " + ",
            Canonical.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(DisplayToken));

    private static string DisplayToken(string token) =>
        token switch
        {
            "CTRL" => "Ctrl",
            "LCTRL" => "Left Ctrl",
            "RCTRL" => "Right Ctrl",
            "SHIFT" => "Shift",
            "LSHIFT" => "Left Shift",
            "RSHIFT" => "Right Shift",
            "ALT" => "Alt",
            "LALT" => "Left Alt",
            "RALT" => "Right Alt",
            "WIN" => "Win",
            "LWIN" => "Left Win",
            "RWIN" => "Right Win",
            "CMD" => "Command",
            "LCMD" => "Left Command",
            "RCMD" => "Right Command",
            "ALTGR" => "AltGr",
            "PGUP" => "Page Up",
            "PGDOWN" => "Page Down",
            "RETURN" => "Enter",
            "MINUS" => "Minus",
            _ when token.StartsWith("MOUSE", StringComparison.Ordinal) =>
                $"Mouse {token[5..]}",
            _ => token,
        };
}

public sealed record LauncherKeybindingParseResult(
    bool IsValid,
    bool IsUnbound,
    string Normalized,
    IReadOnlyList<LauncherKeybindingChord> Chords,
    string? Error = null)
{
    public string Display =>
        IsUnbound
            ? "Unbound"
            : string.Join("  /  ", Chords.Select(chord => chord.Display));
}

public static class LauncherKeybindingValue
{
    private static readonly Dictionary<string, ModifierToken> ModifierTokens =
        new Dictionary<string, ModifierToken>(StringComparer.Ordinal)
        {
            ["CTRL"] = new("CTRL", LauncherKeybindingModifierGroups.Control, 0),
            ["LCTRL"] = new("LCTRL", LauncherKeybindingModifierGroups.Control, 0),
            ["RCTRL"] = new("RCTRL", LauncherKeybindingModifierGroups.Control, 0),
            ["SHIFT"] = new("SHIFT", LauncherKeybindingModifierGroups.Shift, 1),
            ["LSHIFT"] = new("LSHIFT", LauncherKeybindingModifierGroups.Shift, 1),
            ["RSHIFT"] = new("RSHIFT", LauncherKeybindingModifierGroups.Shift, 1),
            ["ALT"] = new("ALT", LauncherKeybindingModifierGroups.Alt, 2),
            ["LALT"] = new("LALT", LauncherKeybindingModifierGroups.Alt, 2),
            ["RALT"] = new("RALT", LauncherKeybindingModifierGroups.Alt, 2),
            ["WIN"] = new("WIN", LauncherKeybindingModifierGroups.Windows, 3),
            ["LWIN"] = new("LWIN", LauncherKeybindingModifierGroups.Windows, 3),
            ["RWIN"] = new("RWIN", LauncherKeybindingModifierGroups.Windows, 3),
            ["CMD"] = new("CMD", LauncherKeybindingModifierGroups.Command, 4),
            ["APPLE"] = new("CMD", LauncherKeybindingModifierGroups.Command, 4),
            ["LAPPLE"] = new("LCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["LCOM"] = new("LCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["LCMD"] = new("LCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["RAPPLE"] = new("RCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["RCOM"] = new("RCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["RCMD"] = new("RCMD", LauncherKeybindingModifierGroups.Command, 4),
            ["ALTGR"] = new("ALTGR", LauncherKeybindingModifierGroups.AltGr, 5),
        };

    private static readonly Dictionary<string, string> KeyAliases =
        BuildKeyAliases();

    public static LauncherKeybindingParseResult Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
        if (compact.Length == 0 || compact == "NONE")
        {
            return new(true, true, "NONE", []);
        }

        var chords = new List<LauncherKeybindingChord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chordText in compact.Split('|', StringSplitOptions.None))
        {
            if (!TryParseChord(chordText, out var chord, out var error))
            {
                return Invalid(error);
            }

            if (!seen.Add(chord.Canonical))
            {
                return Invalid($"Shortcut '{chord.Display}' is assigned more than once.");
            }

            chords.Add(chord);
        }

        return new(
            true,
            false,
            string.Join('|', chords.Select(chord => chord.Canonical)),
            chords.AsReadOnly());
    }

    private static bool TryParseChord(
        string value,
        out LauncherKeybindingChord chord,
        out string error)
    {
        chord = new(string.Empty, string.Empty, LauncherKeybindingModifierGroups.None);
        error = string.Empty;
        if (value.Length == 0)
        {
            error = "A shortcut alternative cannot be empty.";
            return false;
        }

        if (KeyAliases.TryGetValue(value, out var directKey))
        {
            chord = new(directKey, directKey, LauncherKeybindingModifierGroups.None);
            return true;
        }

        var modifiers = new List<ModifierToken>();
        var modifierNames = new HashSet<string>(StringComparer.Ordinal);
        var modifierGroups = LauncherKeybindingModifierGroups.None;
        string? key = null;
        foreach (var token in value.Split('-', StringSplitOptions.None))
        {
            if (ModifierTokens.TryGetValue(token, out var modifier))
            {
                if (!modifierNames.Add(modifier.Canonical))
                {
                    error = $"Shortcut '{value}' assigns modifier '{modifier.Canonical}' more than once.";
                    return false;
                }

                modifiers.Add(modifier);
                modifierGroups |= modifier.Group;
                continue;
            }

            if (!KeyAliases.TryGetValue(token, out var parsedKey))
            {
                error = $"Shortcut token '{token}' is not supported.";
                return false;
            }

            if (key is not null)
            {
                error = $"Shortcut '{value}' contains more than one primary key.";
                return false;
            }

            key = parsedKey;
        }

        if (key is null)
        {
            error = $"Shortcut '{value}' does not contain a primary key.";
            return false;
        }

        var canonicalTokens = modifiers
            .OrderBy(modifier => modifier.Order)
            .Select(modifier => modifier.Canonical)
            .Append(key);
        var canonical = string.Join('-', canonicalTokens);
        chord = new(canonical, key, modifierGroups);
        return true;
    }

    private static LauncherKeybindingParseResult Invalid(string error) =>
        new(false, false, string.Empty, [], error);

    private static Dictionary<string, string> BuildKeyAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[]
        {
            "END", "HOME", "PGDOWN", "PGUP", "DOWN", "LEFT", "RIGHT", "UP",
            "BACKSPACE", "BREAK", "CAPS", "CLEAR", "DELETE", "ESCAPE", "HELP",
            "INSERT", "MENU", "PAUSE", "PRINT", "RETURN", "SCROLL", "SYSREQ",
            "TAB", "MOUSE0", "MOUSE1", "MOUSE2", "MOUSE3", "MOUSE4", "MOUSE5",
            "MOUSE6", "SPACE", "MINUS", "_", ",", ";", ":", "!", "?", ".",
            "'", "[", "]", "/", "\\", "`", "+", "=",
        })
        {
            aliases[key] = key;
        }

        aliases["-"] = "MINUS";
        aliases["PLUS"] = "+";
        foreach (var character in "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            aliases[character.ToString()] = character.ToString();
        }

        for (var index = 1; index <= 12; index++)
        {
            aliases[$"F{index}"] = $"F{index}";
        }

        return aliases;
    }

    private sealed record ModifierToken(
        string Canonical,
        LauncherKeybindingModifierGroups Group,
        int Order);
}

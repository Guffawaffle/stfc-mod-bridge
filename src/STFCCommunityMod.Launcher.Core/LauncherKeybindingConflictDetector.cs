namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherKeybindingAssignment(
    LauncherConfigurationSetting Setting,
    LauncherKeybindingParseResult Binding);

public sealed record LauncherKeybindingConflict(
    LauncherConfigurationSetting First,
    LauncherConfigurationSetting Second,
    LauncherKeybindingChord Chord);

public static class LauncherKeybindingConflictDetector
{
    public static IReadOnlyList<LauncherKeybindingConflict> FindConflicts(
        IEnumerable<LauncherKeybindingAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var candidates = assignments
            .Where(candidate =>
                candidate.Binding.IsValid
                && !candidate.Binding.IsUnbound
                && candidate.Setting.KeybindingMetadata is
                {
                    ConflictGroup: not "None",
                })
            .ToArray();
        var conflicts = new List<LauncherKeybindingConflict>();
        for (var firstIndex = 0; firstIndex < candidates.Length; ++firstIndex)
        {
            var first = candidates[firstIndex];
            var firstMetadata = first.Setting.KeybindingMetadata!;
            for (var secondIndex = firstIndex + 1; secondIndex < candidates.Length; ++secondIndex)
            {
                var second = candidates[secondIndex];
                var secondMetadata = second.Setting.KeybindingMetadata!;
                if (!string.Equals(
                        firstMetadata.TriggerMode,
                        secondMetadata.TriggerMode,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        firstMetadata.ConflictGroup,
                        secondMetadata.ConflictGroup,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var firstChord in first.Binding.Chords)
                {
                    var secondChord = second.Binding.Chords.FirstOrDefault(
                        candidate =>
                            string.Equals(candidate.Key, firstChord.Key, StringComparison.Ordinal)
                            && candidate.EffectiveModifiers == firstChord.EffectiveModifiers);
                    if (secondChord is not null)
                    {
                        conflicts.Add(new(first.Setting, second.Setting, firstChord));
                    }
                }
            }
        }

        return conflicts.AsReadOnly();
    }
}

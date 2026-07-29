namespace STFCCommunityMod.Launcher.ViewModels;

public sealed record SettingsValueState(
    object? EffectiveValue,
    bool HasOverride,
    bool IsStaged = false,
    bool IsRemoval = false);

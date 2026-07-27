namespace STFCCommunityMod.Launcher.ViewModels;

public sealed record SettingsValueState(
    object? EffectiveValue,
    bool HasOverride,
    string ApplyState);

using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

public sealed record SettingsValueState(
    object? SchemaDefaultValue,
    object? SavedValue,
    bool SavedHasOverride,
    object? DraftValue,
    bool DraftHasOverride,
    bool IsDirty,
    LauncherConfigurationValueOrigin SavedOrigin,
    LauncherConfigurationValueOrigin DraftOrigin,
    IReadOnlyList<string> CompatibilitySourcePaths);

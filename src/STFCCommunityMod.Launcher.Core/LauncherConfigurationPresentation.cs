namespace STFCCommunityMod.Launcher.Core;

public enum LauncherConfigurationApplyBehavior
{
    Live,
    NextSession,
    RestartRequired,
}

public enum LauncherConfigurationEditorWidth
{
    Compact,
    Standard,
    Wide,
}

public sealed record LauncherConfigurationPresentationOption(
    string Value,
    string Label,
    string? Help);

public sealed record LauncherConfigurationPresentationFamily(
    string Id,
    string ParentGroup,
    string Label,
    string? Help,
    int DisplayOrder,
    string PresentationHint,
    string MemberLabel,
    int MemberOrder);

public sealed class LauncherConfigurationPresentation
{
    internal LauncherConfigurationPresentation(
        string label,
        string? help,
        string group,
        IReadOnlyList<string> searchTerms,
        IReadOnlyList<LauncherConfigurationPresentationOption> enumOptions,
        LauncherConfigurationPresentationFamily? family,
        string? unit,
        double? sliderMinimum,
        double? sliderMaximum,
        double? sliderStep,
        LauncherConfigurationEditorWidth editorWidth,
        string applyTiming,
        string accessibleName,
        string accessibleHelp)
    {
        Label = label;
        Help = help;
        Group = group;
        SearchTerms = searchTerms;
        EnumOptions = enumOptions;
        Family = family;
        Unit = unit;
        SliderMinimum = sliderMinimum;
        SliderMaximum = sliderMaximum;
        SliderStep = sliderStep;
        EditorWidth = editorWidth;
        ApplyTiming = applyTiming;
        AccessibleName = accessibleName;
        AccessibleHelp = accessibleHelp;
    }

    public string Label { get; }

    public string? Help { get; }

    public string Group { get; }

    public IReadOnlyList<string> SearchTerms { get; }

    public IReadOnlyList<LauncherConfigurationPresentationOption> EnumOptions { get; }

    public LauncherConfigurationPresentationFamily? Family { get; }

    public string? Unit { get; }

    public double? SliderMinimum { get; }

    public double? SliderMaximum { get; }

    public double? SliderStep { get; }

    public LauncherConfigurationEditorWidth EditorWidth { get; }

    public string ApplyTiming { get; }

    public string AccessibleName { get; }

    public string AccessibleHelp { get; }

    internal static string ApplyTimingFor(LauncherConfigurationApplyBehavior behavior) =>
        behavior switch
        {
            LauncherConfigurationApplyBehavior.Live => "Immediate",
            LauncherConfigurationApplyBehavior.NextSession => "Next launch",
            LauncherConfigurationApplyBehavior.RestartRequired => "Restart required",
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        };

    internal static string ApplyTokenFor(LauncherConfigurationApplyBehavior behavior) =>
        behavior switch
        {
            LauncherConfigurationApplyBehavior.Live => "live",
            LauncherConfigurationApplyBehavior.NextSession => "next-session",
            LauncherConfigurationApplyBehavior.RestartRequired => "restart-required",
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        };
}

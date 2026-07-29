namespace STFCCommunityMod.Launcher.Core;

public enum LauncherConfigurationApplySummaryKind
{
    None,
    Immediate,
    NextLaunch,
    MixedRelaunch,
    RestartRequired,
}

public sealed record LauncherConfigurationApplySummary(
    LauncherConfigurationApplySummaryKind Kind,
    string Text)
{
    public static LauncherConfigurationApplySummary From(
        IEnumerable<LauncherConfigurationApplyBehavior> behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);

        var values = behaviors.Distinct().ToArray();
        if (values.Any(value => !Enum.IsDefined(value)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(behaviors),
                "The apply summary contains an unsupported behavior.");
        }
        if (values.Length == 0)
        {
            return new(LauncherConfigurationApplySummaryKind.None, "No pending changes");
        }

        if (values.Contains(LauncherConfigurationApplyBehavior.RestartRequired))
        {
            return new(
                LauncherConfigurationApplySummaryKind.RestartRequired,
                "One or more changes require a game restart");
        }

        if (values.Length == 1 && values[0] == LauncherConfigurationApplyBehavior.Live)
        {
            return new(LauncherConfigurationApplySummaryKind.Immediate, "Applies immediately");
        }

        if (values.Length == 1 && values[0] == LauncherConfigurationApplyBehavior.NextSession)
        {
            return new(LauncherConfigurationApplySummaryKind.NextLaunch, "Applies next launch");
        }

        return new(
            LauncherConfigurationApplySummaryKind.MixedRelaunch,
            "Some changes require a relaunch");
    }
}

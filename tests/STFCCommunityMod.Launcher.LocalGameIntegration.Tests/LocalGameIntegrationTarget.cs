using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

internal enum LocalGameIntegrationTargetState
{
    Disabled,
    MissingPath,
    Invalid,
    Ready,
}

internal sealed record LocalGameIntegrationTargetResolution(
    LocalGameIntegrationTargetState State,
    GameInstallValidation? Validation = null);

internal static class LocalGameIntegrationTarget
{
    public const string EnableEnvironmentVariable = "STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION";
    public const string DirectoryEnvironmentVariable = "STFC_BRIDGE_INTEGRATION_GAME_DIR";

    public static LocalGameIntegrationTargetResolution Resolve(bool enabled, string? gameDirectory)
    {
        if (!enabled)
        {
            return new(LocalGameIntegrationTargetState.Disabled);
        }
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new(LocalGameIntegrationTargetState.MissingPath);
        }

        var validation = GameInstallValidator.Validate(gameDirectory);
        return new(
            validation.IsValid
                ? LocalGameIntegrationTargetState.Ready
                : LocalGameIntegrationTargetState.Invalid,
            validation);
    }

    public static string RequireOptedInDirectory()
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var resolution = Resolve(
            enabled,
            Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable));

        if (resolution.State == LocalGameIntegrationTargetState.Disabled)
        {
            Assert.Inconclusive(
                "Local game integration is disabled. Use scripts/test-local-game-install.ps1 to opt in explicitly.");
        }
        if (resolution.State == LocalGameIntegrationTargetState.MissingPath)
        {
            Assert.Fail($"{DirectoryEnvironmentVariable} is required when local integration is enabled.");
        }
        if (resolution.State != LocalGameIntegrationTargetState.Ready
            || resolution.Validation is null)
        {
            Assert.Fail(
                $"The opted-in local game target failed production validation: "
                + $"{resolution.Validation?.Code.ToString() ?? "unknown"}.");
        }

        return resolution.Validation.GameDirectory;
    }
}

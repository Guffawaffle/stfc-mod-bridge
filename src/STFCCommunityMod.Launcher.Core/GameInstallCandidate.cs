namespace STFCCommunityMod.Launcher.Core;

public enum GameInstallCandidateSource
{
    PersistedSelection,
    EnvironmentOverride,
    OfficialLauncherSettings,
    ConventionalLocation,
    ManualSelection,
}

public enum GameInstallConfidence
{
    Conventional = 10,
    EnvironmentProvided = 20,
    OfficialLauncherMetadata = 25,
    UserConfirmed = 30,
}

public enum GameInstallValidationCode
{
    Valid,
    InvalidPath,
    DirectoryMissing,
    OfficialLauncherDirectory,
    PrimeExecutableMissing,
}

public sealed record GameInstallEvidence(
    GameInstallCandidateSource Source,
    GameInstallConfidence Confidence,
    string Detail);

public sealed record GameInstallCandidateSeed(
    string GameDirectory,
    IReadOnlyList<GameInstallEvidence> Evidence);

public sealed record GameInstallValidation(
    GameInstallValidationCode Code,
    string GameDirectory,
    string? PrimeExecutablePath,
    string Message)
{
    public bool IsValid => Code == GameInstallValidationCode.Valid;
}

public sealed record GameInstallCandidate(
    string GameDirectory,
    GameInstallConfidence Confidence,
    IReadOnlyList<GameInstallEvidence> Evidence,
    GameInstallValidation Validation);

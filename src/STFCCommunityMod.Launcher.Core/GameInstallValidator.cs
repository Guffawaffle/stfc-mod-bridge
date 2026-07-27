namespace STFCCommunityMod.Launcher.Core;

public static class GameInstallValidator
{
    private static readonly string[] OfficialLauncherFileNames =
    [
        "launcher.exe",
        "Star Trek Fleet Command.exe",
        "Star Trek Fleet Command Launcher.exe",
    ];

    public static GameInstallValidation Validate(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new(
                GameInstallValidationCode.InvalidPath,
                gameDirectory,
                null,
                "Select the game folder that directly contains prime.exe.");
        }

        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return new(
                GameInstallValidationCode.InvalidPath,
                gameDirectory,
                null,
                "The selected game folder is not a valid Windows path.");
        }

        if (!Directory.Exists(normalizedDirectory))
        {
            return new(
                GameInstallValidationCode.DirectoryMissing,
                normalizedDirectory,
                null,
                "The selected game folder does not exist.");
        }

        var primeExecutable = Path.Combine(normalizedDirectory, "prime.exe");
        if (File.Exists(primeExecutable))
        {
            return new(
                GameInstallValidationCode.Valid,
                normalizedDirectory,
                primeExecutable,
                "The folder contains prime.exe and is a valid STFC game target.");
        }

        var looksLikeOfficialLauncher = OfficialLauncherFileNames
            .Select(fileName => Path.Combine(normalizedDirectory, fileName))
            .Any(File.Exists)
            || Directory.Exists(Path.Combine(normalizedDirectory, "default"));

        return looksLikeOfficialLauncher
            ? new(
                GameInstallValidationCode.OfficialLauncherDirectory,
                normalizedDirectory,
                null,
                "This appears to be the official launcher folder, not the game folder that contains prime.exe.")
            : new(
                GameInstallValidationCode.PrimeExecutableMissing,
                normalizedDirectory,
                null,
                "The selected folder does not contain prime.exe.");
    }
}

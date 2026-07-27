namespace STFCCommunityMod.Launcher.Core;

public sealed class OfficialLauncherSettingsCandidateProvider(string settingsPath)
    : IGameInstallCandidateProvider
{
    public IEnumerable<GameInstallCandidateSeed> GetCandidates(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(settingsPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (!key.Equals("GAME_PATH", StringComparison.OrdinalIgnoreCase)
                && !key.EndsWith(".GAME_PATH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            yield return new(
                value,
                [
                    new(
                        GameInstallCandidateSource.OfficialLauncherSettings,
                        GameInstallConfidence.OfficialLauncherMetadata,
                        $"Official launcher setting {Path.GetFileName(settingsPath)} records this game path."),
                ]);
        }
    }

    public static OfficialLauncherSettingsCandidateProvider FromCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new(
            Path.Combine(
                localApplicationData,
                "Star Trek Fleet Command",
                "launcher_settings.ini"));
    }
}

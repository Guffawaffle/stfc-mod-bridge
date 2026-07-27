using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class OfficialLauncherSettingsCandidateProviderTests
{
    [TestMethod]
    public void ReadsExactGamePathWithoutScanning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "launcher_settings.ini");
        File.WriteAllLines(
            settingsPath,
            [
                "INSTALLATION_DRIVE=D:/",
                "152033..GAME_PATH=D:/Games/艦隊/default/game/",
                "152033..GAME_TEMP_PATH=D:/Games/艦隊/temp/",
            ]);
        var provider = new OfficialLauncherSettingsCandidateProvider(settingsPath);

        var candidates = provider.GetCandidates(CancellationToken.None).ToArray();

        Assert.AreEqual(1, candidates.Length);
        Assert.AreEqual("D:/Games/艦隊/default/game/", candidates[0].GameDirectory);
        Assert.AreEqual(
            GameInstallCandidateSource.OfficialLauncherSettings,
            candidates[0].Evidence[0].Source);
        Assert.AreEqual(
            GameInstallConfidence.OfficialLauncherMetadata,
            candidates[0].Evidence[0].Confidence);
    }

    [TestMethod]
    public void MissingSettingsFileReturnsNoCandidates()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var provider = new OfficialLauncherSettingsCandidateProvider(
            Path.Combine(temporaryDirectory.Path, "missing.ini"));

        var candidates = provider.GetCandidates(CancellationToken.None).ToArray();

        Assert.AreEqual(0, candidates.Length);
    }
}

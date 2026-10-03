namespace STFCCommunityMod.Launcher.Core;

public sealed record RuntimeInstallationBinding(string InstallationId, string PhysicalIdentity, string GameDirectory);

internal sealed class RuntimeInstallationCustody(RuntimeInstallationBinding binding, IDisposable lease) : IDisposable
{
    public RuntimeInstallationBinding Binding { get; } = binding;
    public string GameDirectory => Binding.GameDirectory;
    public void Dispose() => lease.Dispose();

    public static async Task<RuntimeInstallationCustody?> AcquireAsync(
        NativeLauncherProfilesStore? store, string gameDirectory, RuntimeInstallationBinding? expected = null,
        bool recovery = false, bool requireBinding = false, CancellationToken cancellationToken = default)
    {
        if (store is null) return null; // Explicit synthetic service seam, never production composition.
        if (expected is null)
        {
            if (recovery || requireBinding)
                throw new InvalidDataException("The saved transaction has no physical installation binding. Preserve its backups for review.");
            var registered = await store.RegisterInstallationAsync("STFC installation", gameDirectory, cancellationToken).ConfigureAwait(false);
            expected = new(registered.Id, registered.PhysicalIdentity, registered.GameDirectory);
            gameDirectory = registered.GameDirectory;
        }
        Validate(expected, gameDirectory);
        var lease = store.AcquireRecoveryInstallationLease(expected);
        try
        {
            if (!recovery && store.InstallationPaths(expected.InstallationId).State != "available")
                throw new InvalidOperationException("The selected game image is incomplete. Recover its game update before changing the runtime.");
            return new(expected, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static void Validate(RuntimeInstallationBinding binding, string gameDirectory)
    {
        if (!LauncherProfiles.ValidId(binding.InstallationId)
            || binding.PhysicalIdentity is not { Length: 64 }
            || !binding.PhysicalIdentity.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(binding.GameDirectory)
            || !Path.IsPathFullyQualified(binding.GameDirectory)
            || !GameDirectoryIdentity.SameLocation(binding.GameDirectory, gameDirectory))
            throw new InvalidDataException("The saved physical installation binding is invalid or belongs to another directory.");
    }

    public void ValidateReceipt(RuntimeInstallationBinding? expected)
    {
        if (expected is not null && (expected.InstallationId != Binding.InstallationId
            || expected.PhysicalIdentity != Binding.PhysicalIdentity
            || !GameDirectoryIdentity.SameLocation(expected.GameDirectory, GameDirectory)))
            throw new InvalidDataException("The ownership receipt belongs to a different physical installation.");
    }
}

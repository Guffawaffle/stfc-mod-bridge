namespace STFCCommunityMod.Launcher.Core;

public sealed class LauncherFeatureRemediationReview : IAsyncDisposable
{
    private readonly ReviewedModArtifactCandidateLease candidateLease;

    internal LauncherFeatureRemediationReview(
        string featureId,
        LauncherActivationPlan currentPlan,
        ReviewedModArtifactCandidateLease candidateLease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(currentPlan);
        this.candidateLease = candidateLease ?? throw new ArgumentNullException(nameof(candidateLease));

        var receipt = candidateLease.Receipt;
        var activation = receipt.RuntimeActivation
            ?? throw new InvalidDataException(
                "The exact reviewed candidate has no authorized runtime activation evidence.");
        var artifactManifest = receipt.Artifact.RuntimeManifest
            ?? throw new InvalidDataException(
                "The exact reviewed candidate has no runtime-manifest artifact binding.");
        var manifestIdentity = receipt.RuntimeManifestIdentity
            ?? throw new InvalidDataException(
                "The exact reviewed candidate has no runtime-manifest identity receipt.");
        if (receipt.DllIdentity.Size != receipt.Artifact.Size
            || !FixedTimeEquals(receipt.DllIdentity.Sha256, receipt.Artifact.Sha256)
            || manifestIdentity.Size != artifactManifest.Size
            || !FixedTimeEquals(manifestIdentity.Sha256, artifactManifest.Sha256)
            || !FixedTimeEquals(activation.EvidenceSourceSha256, artifactManifest.Sha256)
            || !string.Equals(
                activation.RuntimeProfile.SourceRevision,
                artifactManifest.ExpectedSourceRevision,
                StringComparison.Ordinal)
            || !string.Equals(
                activation.RuntimeProfile.DistributionId,
                receipt.InstallationAttribution.RuntimeDistributionId,
                StringComparison.Ordinal)
            || !ReferenceEquals(activation.RuntimeProfile, activation.ActivationPlan.Runtime))
        {
            throw new InvalidDataException(
                "The exact reviewed candidate receipt and runtime activation evidence disagree.");
        }

        var currentDecision = currentPlan.GetDecision(featureId);
        if (currentDecision.IsActive)
        {
            throw new InvalidOperationException(
                "Feature remediation can start only for a currently unavailable feature.");
        }

        FeatureId = featureId;
        ProviderId = receipt.InstallationAttribution.ProviderId;
        ReleaseChannelId = receipt.InstallationAttribution.ReleaseChannelId;
        RuntimeDistributionId = receipt.InstallationAttribution.RuntimeDistributionId;
        SourceRepository = artifactManifest.ExpectedRepository;
        SourceTag = artifactManifest.ExpectedTag;
        SourceRevision = artifactManifest.ExpectedSourceRevision;
        DllSha256 = receipt.DllIdentity.Sha256.ToLowerInvariant();
        RuntimeManifestSha256 = manifestIdentity.Sha256.ToLowerInvariant();
        CurrentDecision = currentDecision;
        TargetDecision = activation.ActivationPlan.GetDecision(featureId);
        CurrentCatalogSource = currentPlan.CatalogSource;
        CurrentPolicySource = currentPlan.PolicySource;
        TargetCatalogSource = activation.ActivationPlan.CatalogSource;
        TargetPolicySource = activation.ActivationPlan.PolicySource;
    }

    public string FeatureId { get; }

    public string ProviderId { get; }

    public string ReleaseChannelId { get; }

    public string RuntimeDistributionId { get; }

    public string SourceRepository { get; }

    public string SourceTag { get; }

    public string SourceRevision { get; }

    public string DllSha256 { get; }

    public string RuntimeManifestSha256 { get; }

    public LauncherFeatureDecision CurrentDecision { get; }

    public LauncherFeatureDecision TargetDecision { get; }

    public LauncherFeatureSourceIdentity CurrentCatalogSource { get; }

    public LauncherFeatureSourceIdentity CurrentPolicySource { get; }

    public LauncherFeatureSourceIdentity TargetCatalogSource { get; }

    public LauncherFeatureSourceIdentity TargetPolicySource { get; }

    public bool WouldActivate => TargetDecision.IsActive;

    public string ConfirmationText =>
        $"Review {FeatureId} using exact candidate evidence from {SourceRepository} {SourceTag} "
        + $"at {SourceRevision}. DLL SHA-256: {DllSha256}. Runtime manifest SHA-256: "
        + $"{RuntimeManifestSha256}. Current decision: {FormatEvidence(CurrentDecision)} "
        + $"Target decision: {FormatEvidence(TargetDecision)} "
        + $"Current catalog source: {CurrentCatalogSource.Id}@{CurrentCatalogSource.Version}. "
        + $"Current product-policy source: {CurrentPolicySource.Id}@{CurrentPolicySource.Version}. "
        + $"Target catalog source: {TargetCatalogSource.Id}@{TargetCatalogSource.Version}. "
        + $"Target product-policy source: {TargetPolicySource.Id}@{TargetPolicySource.Version}. "
        + "Player preference is applied only after "
        + "a successful provider transaction.";

    internal ReviewedModArtifactCandidateLease CandidateLease => candidateLease;

    public ValueTask DisposeAsync() => candidateLease.DisposeAsync();

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == 64
        && right.Length == 64
        && left.All(Uri.IsHexDigit)
        && right.All(Uri.IsHexDigit)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private static string FormatEvidence(LauncherFeatureDecision decision)
    {
        var eligibilitySubjects = string.Join(",", decision.EligibilityEvidence.Subjects);
        var selectionSubjects = string.Join(",", decision.SelectionEvidence.Subjects);
        var context = string.IsNullOrEmpty(decision.EligibilityEvidence.Context)
            ? string.Empty
            : $", context={decision.EligibilityEvidence.Context}";
        return $"state={decision.State}, eligibility={decision.EligibilityEvidence.Code}"
            + $"[{eligibilitySubjects}]{context}, selection={decision.SelectionEvidence.Code}"
            + $"[{selectionSubjects}], implementation={decision.SelectedImplementation}.";
    }
}

public static class LauncherFeatureRemediationReviewer
{
    public static LauncherFeatureRemediationReview Review(
        string featureId,
        LauncherActivationPlan currentPlan,
        ReviewedModArtifactCandidateLease candidateLease) =>
        new(featureId, currentPlan, candidateLease);
}

public sealed record LauncherFeatureRemediationEndpoint(
    string ProviderId,
    ReviewedModArtifactCandidateAcquirer CandidateAcquirer);

public sealed class LauncherFeatureRemediationCandidates : IAsyncDisposable
{
    private readonly IReadOnlyList<LauncherFeatureRemediationEndpoint> endpoints;
    private int disposeStarted;

    public LauncherFeatureRemediationCandidates(
        IEnumerable<LauncherFeatureRemediationEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var copied = endpoints.ToArray();
        if (copied.Length == 0)
        {
            throw new ArgumentException(
                "At least one reviewed candidate endpoint is required.",
                nameof(endpoints));
        }
        if (copied.Any(endpoint =>
                string.IsNullOrWhiteSpace(endpoint.ProviderId)
                || endpoint.CandidateAcquirer is null)
            || copied.Select(endpoint => endpoint.ProviderId)
                .Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException(
                "Reviewed candidate endpoints must have unique provider identities.",
                nameof(endpoints));
        }
        this.endpoints = Array.AsReadOnly(copied);
    }

    public IReadOnlyList<LauncherFeatureRemediationEndpoint> Endpoints => endpoints;

    public Task<ReviewedCandidateRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default) =>
        endpoints[0].CandidateAcquirer.RecoverAbandonedCandidatesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }
        List<Exception>? failures = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                await endpoint.CandidateAcquirer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
        {
            throw new AggregateException(
                "One or more reviewed candidate owners could not finish exact cleanup.",
                failures);
        }
    }
}

public sealed class LauncherFeatureRemediationPreview : IAsyncDisposable
{
    internal LauncherFeatureRemediationPreview(
        LauncherProviderAtomicSwitchPreview providerSwitch,
        LauncherFeatureRemediationReview review)
    {
        ProviderSwitch = providerSwitch ?? throw new ArgumentNullException(nameof(providerSwitch));
        Review = review ?? throw new ArgumentNullException(nameof(review));
        if (!string.Equals(
                providerSwitch.Configuration.Target.ProviderId,
                review.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                providerSwitch.Configuration.Target.ReleaseChannelId,
                review.ReleaseChannelId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact feature-remediation candidate targets a different provider or release-channel transition.");
        }
    }

    public LauncherFeatureRemediationReview Review { get; }

    public string ConfirmationText =>
        $"{ProviderSwitch.Configuration.ConfirmationText} {Review.ConfirmationText}";

    internal LauncherProviderAtomicSwitchPreview ProviderSwitch { get; }

    public ValueTask DisposeAsync() => Review.DisposeAsync();
}

public sealed class LauncherFeatureRemediationCoordinator
{
    private readonly LauncherProviderAtomicSwitchCoordinator providerSwitch;
    private readonly Func<LauncherActivationPlan> currentPlan;
    private readonly Dictionary<string, ReviewedModArtifactCandidateAcquirer> candidateAcquirers;

    public LauncherFeatureRemediationCoordinator(
        LauncherProviderAtomicSwitchCoordinator providerSwitch,
        Func<LauncherActivationPlan> currentPlan,
        IEnumerable<LauncherFeatureRemediationEndpoint> endpoints)
    {
        this.providerSwitch = providerSwitch ?? throw new ArgumentNullException(nameof(providerSwitch));
        this.currentPlan = currentPlan ?? throw new ArgumentNullException(nameof(currentPlan));
        ArgumentNullException.ThrowIfNull(endpoints);
        candidateAcquirers = endpoints.ToDictionary(
            endpoint => endpoint.ProviderId,
            endpoint => endpoint.CandidateAcquirer,
            StringComparer.Ordinal);
        if (candidateAcquirers.Count == 0)
        {
            throw new ArgumentException(
                "At least one feature-remediation candidate endpoint is required.",
                nameof(endpoints));
        }
    }

    public async Task<LauncherFeatureRemediationPreview> PrepareAsync(
        string featureId,
        string targetProviderId,
        string? targetReleaseChannelId,
        string gameDirectory,
        bool isGameRunning,
        string? configurationPath,
        CancellationToken cancellationToken = default)
    {
        if (currentPlan().GetDecision(featureId).IsActive)
        {
            throw new InvalidOperationException(
                "Feature remediation can start only for a currently unavailable feature.");
        }
        var switchPreview = await providerSwitch.PreviewAsync(
            targetProviderId,
            targetReleaseChannelId,
            gameDirectory,
            isGameRunning,
            configurationPath,
            cancellationToken).ConfigureAwait(false);
        var preparation = switchPreview.Artifact
            ?? throw new InvalidOperationException(
                "Feature remediation requires a verified managed source installation and an exact target artifact.");
        if (preparation.State != ModOperationPreparationState.Ready
            || !candidateAcquirers.TryGetValue(
                switchPreview.Configuration.Target.ProviderId,
                out var acquirer))
        {
            throw new InvalidOperationException(
                "The exact target provider candidate is unavailable for feature remediation.");
        }
        if (preparation.Artifact.RuntimeManifest is null)
        {
            throw new InvalidOperationException(
                "The reviewed target release does not provide a certified runtime-manifest pair. "
                + "Release metadata remains a discovery lead and no candidate was downloaded.");
        }

        var candidateLease = await acquirer.AcquireAsync(
            preparation.Artifact,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var review = LauncherFeatureRemediationReviewer.Review(
                featureId,
                currentPlan(),
                candidateLease);
            return new(switchPreview, review);
        }
        catch
        {
            await candidateLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LauncherProviderAtomicSwitchResult> ExecuteAsync(
        LauncherFeatureRemediationPreview preview,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(confirmationText, preview.ConfirmationText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Feature remediation requires confirmation of the exact candidate evidence.");
        }
        var latestPlan = currentPlan();
        if (latestPlan.GetDecision(preview.Review.FeatureId) != preview.Review.CurrentDecision
            || latestPlan.CatalogSource != preview.Review.CurrentCatalogSource
            || latestPlan.PolicySource != preview.Review.CurrentPolicySource)
        {
            throw new InvalidOperationException(
                "Runtime capability or product-policy evidence changed after this remediation review. "
                + "Cancel it and review a new exact candidate plan.");
        }
        return await providerSwitch.ExecuteCandidateAsync(
            preview.ProviderSwitch,
            preview.Review.CandidateLease,
            preview.ProviderSwitch.Configuration.ConfirmationText,
            cancellationToken).ConfigureAwait(false);
    }
}

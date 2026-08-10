using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

internal enum FleetRuntimeProjectionDisposition
{
    None,
    Advanced,
    Duplicate,
    Stale,
}

internal sealed record FleetRuntimeSlotProjection(
    string SlotKey,
    string FleetKey,
    string State,
    string AssignmentKind,
    DateTimeOffset UpdatedAt,
    string? ShipIdentityId,
    string? ShipKeyHash,
    string? ShipType,
    long? HullSpecId,
    long? ActiveTimerRemainingMs,
    string? ActiveTimerSource);

internal sealed class FleetRuntimeProjectionSnapshot
{
    public FleetRuntimeProjectionSnapshot(
        long version,
        string source,
        string sessionId,
        string batchId,
        DateTimeOffset producedAt,
        string observationSource,
        long? observedAtMs,
        bool fleetBarTracked,
        int? selectedIndex,
        string evidenceSha256,
        IEnumerable<FleetRuntimeSlotProjection> slots)
    {
        Version = version;
        Source = source;
        SessionId = sessionId;
        BatchId = batchId;
        ProducedAt = producedAt;
        ObservationSource = observationSource;
        ObservedAtMs = observedAtMs;
        FleetBarTracked = fleetBarTracked;
        SelectedIndex = selectedIndex;
        EvidenceSha256 = evidenceSha256;
        Slots = Array.AsReadOnly(slots.ToArray());
    }

    public long Version { get; }

    public string Source { get; }

    public string SessionId { get; }

    public string BatchId { get; }

    public DateTimeOffset ProducedAt { get; }

    public string ObservationSource { get; }

    public long? ObservedAtMs { get; }

    public bool FleetBarTracked { get; }

    public int? SelectedIndex { get; }

    public string EvidenceSha256 { get; }

    public ReadOnlyCollection<FleetRuntimeSlotProjection> Slots { get; }
}

internal sealed record FleetRuntimeProjectionStatus(
    FleetRuntimeProjectionSnapshot? Current,
    FleetRuntimeProjectionDisposition LastDisposition,
    int RetainedBatchReceipts,
    long AcceptedBatches,
    long AdvancedSnapshots,
    long DuplicateBatches,
    long StaleBatches,
    long ConflictingBatches);

/// <summary>
/// Bounded, process-local owner for the current Fleet runtime projection. It
/// consumes only an already transport-validated fleet envelope, retains no raw
/// payload bytes, creates no filesystem or network resources, and never owns
/// listener activation or player policy.
/// </summary>
internal sealed class FleetRuntimeSnapshotSink : IBattleIngestSink, IAsyncDisposable
{
    private const int MaximumSnapshotBytes = 512 * 1024;
    private const int MaximumReceipts = 2048;
    private const int MaximumSlots = 64;
    private const int MaximumTextLength = 80;
    private const int MaximumShipIdentityLength = 64;

    private readonly object sync = new();
    private readonly Dictionary<BatchIdentity, BatchReceipt> receipts = [];
    private readonly Queue<BatchIdentity> receiptOrder = [];
    private FleetRuntimeProjectionSnapshot? current;
    private FleetRuntimeProjectionDisposition lastDisposition;
    private long acceptedBatches;
    private long advancedSnapshots;
    private long duplicateBatches;
    private long staleBatches;
    private long conflictingBatches;
    private bool disposed;

    public ValueTask<BattleIngestCommitResult> CommitAsync(
        BattleIngestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = ParseCandidate(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var identity = new BatchIdentity(envelope.Source, envelope.SessionId, envelope.BatchId);
            if (receipts.TryGetValue(identity, out var receipt))
            {
                if (!StringComparer.Ordinal.Equals(receipt.EnvelopeSha256, candidate.EnvelopeSha256))
                {
                    conflictingBatches++;
                    throw new InvalidDataException(
                        "A Fleet runtime batch ID was reused with different exact bytes.");
                }
                acceptedBatches++;
                duplicateBatches++;
                lastDisposition = FleetRuntimeProjectionDisposition.Duplicate;
                return ValueTask.FromResult(new BattleIngestCommitResult(1));
            }

            if (current is not null
                && (current.Source != envelope.Source || current.SessionId != envelope.SessionId))
            {
                conflictingBatches++;
                throw new InvalidDataException(
                    "The Fleet runtime sink is already bound to a different producer scope.");
            }

            var disposition = DetermineDisposition(candidate);
            if (disposition == FleetRuntimeProjectionDisposition.None)
            {
                conflictingBatches++;
                throw new InvalidDataException(
                    "Fleet runtime observations with the same timestamp have ambiguous different state.");
            }

            AddReceipt(identity, candidate.EnvelopeSha256);
            acceptedBatches++;
            lastDisposition = disposition;
            if (disposition == FleetRuntimeProjectionDisposition.Stale)
            {
                staleBatches++;
            }
            else if (disposition == FleetRuntimeProjectionDisposition.Duplicate)
            {
                duplicateBatches++;
            }
            else
            {
                advancedSnapshots++;
                current = candidate.ToSnapshot(advancedSnapshots);
            }
            return ValueTask.FromResult(new BattleIngestCommitResult(1));
        }
    }

    public FleetRuntimeProjectionStatus ReadStatus()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new(
                current,
                lastDisposition,
                receipts.Count,
                acceptedBatches,
                advancedSnapshots,
                duplicateBatches,
                staleBatches,
                conflictingBatches);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }
            disposed = true;
            current = null;
            receipts.Clear();
            receiptOrder.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private FleetRuntimeProjectionDisposition DetermineDisposition(ProjectionCandidate candidate)
    {
        if (current is null)
        {
            return FleetRuntimeProjectionDisposition.Advanced;
        }
        var comparison = candidate.ProducedAt.UtcTicks.CompareTo(current.ProducedAt.UtcTicks);
        if (comparison < 0)
        {
            return FleetRuntimeProjectionDisposition.Stale;
        }
        if (comparison > 0)
        {
            return FleetRuntimeProjectionDisposition.Advanced;
        }
        return candidate.EvidenceSha256 == current.EvidenceSha256
            ? FleetRuntimeProjectionDisposition.Duplicate
            : FleetRuntimeProjectionDisposition.None;
    }

    private void AddReceipt(BatchIdentity identity, string envelopeSha256)
    {
        while (receipts.Count >= MaximumReceipts)
        {
            receipts.Remove(receiptOrder.Dequeue());
        }
        receipts.Add(identity, new(envelopeSha256));
        receiptOrder.Enqueue(identity);
    }

    private static ProjectionCandidate ParseCandidate(BattleIngestEnvelope envelope)
    {
        if (envelope.ProtocolVersion != BattleIngestProtocol.Version
            || envelope.Kind != BattleIngestProtocol.FleetRuntimeKind
            || envelope.PayloadProtocol != BattleIngestProtocol.FleetRuntimeVersion
            || !IsBoundedIdentity(envelope.Source)
            || !IsBoundedIdentity(envelope.SessionId)
            || !IsBoundedIdentity(envelope.BatchId)
            || envelope.ExactEnvelopeBytes.Length is <= 0 or > MaximumSnapshotBytes
            || envelope.ExactEventBytes.Count != 1
            || envelope.ExactEventBytes[0].Length is <= 0 or > MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The Fleet runtime envelope is outside the accepted sink contract.");
        }

        using var document = JsonDocument.Parse(envelope.ExactEventBytes[0], new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 48,
        });
        RejectDuplicateProperties(document.RootElement);
        var payload = RequireObject(document.RootElement, "Fleet runtime payload");
        if (RequiredString(payload, "type") != BattleIngestProtocol.FleetRuntimeKind
            || RequiredString(payload, "schemaVersion") != BattleIngestProtocol.FleetRuntimeVersion)
        {
            throw new InvalidDataException("The Fleet runtime payload schema is unsupported.");
        }
        var slotsValue = RequiredProperty(payload, "slots");
        if (slotsValue.ValueKind != JsonValueKind.Array
            || slotsValue.GetArrayLength() > MaximumSlots)
        {
            throw new InvalidDataException("Fleet runtime slots must be a bounded array.");
        }

        var tracked = OptionalBoolean(payload, "fleetBarTracked") == true;
        var slots = new Dictionary<int, FleetRuntimeSlotProjection>();
        foreach (var value in slotsValue.EnumerateArray())
        {
            var slot = ProjectSlot(value, envelope.ProducedAt, tracked);
            if (slot is not null)
            {
                slots[ParseSlotIndex(slot.SlotKey)] = slot;
            }
        }

        var exactEvent = envelope.ExactEventBytes[0].Span;
        return new(
            envelope.Source,
            envelope.SessionId,
            envelope.BatchId,
            envelope.ProducedAt,
            SafeText(payload, "source") ?? "unknown",
            OptionalInt64(payload, "observedAtMs"),
            tracked,
            OptionalInt32(payload, "selectedIndex"),
            Sha256(exactEvent),
            Sha256(envelope.ExactEnvelopeBytes.Span),
            slots.OrderBy(item => item.Key).Select(item => item.Value).ToArray());
    }

    private static FleetRuntimeSlotProjection? ProjectSlot(
        JsonElement value,
        DateTimeOffset producedAt,
        bool fleetBarTracked)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A Fleet runtime slot must be an object.");
        }
        var slotIndex = OptionalInt32(value, "slotIndex");
        if (slotIndex is null || slotIndex < 0)
        {
            return null;
        }
        var present = OptionalBoolean(value, "present") == true;
        var fleetId = OptionalInt64(value, "fleetId");
        var hullSpecId = OptionalInt64(value, "hullSpecId");
        var currentState = SafeText(value, "currentStateName");
        var hullName = SafeText(value, "hullName");
        var shipIdentity = ShipIdentity(value);
        var activeTimer = ActiveTimer(value);
        var fleetHash = present && fleetId is not null
            ? Sha256(Encoding.UTF8.GetBytes($"fleet:{fleetId.Value.ToString(CultureInfo.InvariantCulture)}"))
            : null;
        return new(
            $"slot-{slotIndex.Value.ToString(CultureInfo.InvariantCulture)}",
            fleetHash is null
                ? $"fleet-slot-{slotIndex.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"fleet-{fleetHash[..16]}",
            !fleetBarTracked
                ? "unavailable"
                : !present
                    ? "empty"
                    : currentState?.ToLowerInvariant() ?? "observed",
            present ? "player_ship" : "slot",
            producedAt,
            present ? shipIdentity : null,
            present && fleetHash is not null ? fleetHash[..32] : null,
            present && hullName is not null ? $"hull:{hullName}" : null,
            present ? hullSpecId : null,
            present ? activeTimer?.RemainingMs : null,
            present ? activeTimer?.Source : null);
    }

    private static int ParseSlotIndex(string slotKey) =>
        int.Parse(slotKey.AsSpan("slot-".Length), CultureInfo.InvariantCulture);

    private static string? ShipIdentity(JsonElement slot)
    {
        if (!slot.TryGetProperty("shipIdentityProbe", out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("shipId", out var shipId)
            || shipId.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = shipId.GetString()?.Trim();
        return text is { Length: > 0 and <= MaximumShipIdentityLength }
            && text.All(character => character is >= '0' and <= '9')
                ? text
                : null;
    }

    private static TimerProjection? ActiveTimer(JsonElement slot)
    {
        if (!slot.TryGetProperty("activeTimer", out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var remaining = NonNegativeInt64(value, "remainingMs")
            ?? ScaleNonNegative(value, "remainingSeconds", 1000d)
            ?? ScaleNonNegative(value, "remainingTicks", 0.0001d);
        return remaining is null
            ? null
            : new(remaining.Value, SafeText(value, "source") ?? "unknown");
    }

    private static long? ScaleNonNegative(JsonElement parent, string name, double scale)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0
            || number > long.MaxValue / scale)
        {
            return null;
        }
        return checked((long)Math.Truncate(number * scale));
    }

    private static long? NonNegativeInt64(JsonElement parent, string name)
    {
        var value = OptionalInt64(parent, name);
        return value >= 0 ? value : null;
    }

    private static string? SafeText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString();
        if (text is null)
        {
            return null;
        }
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized[..Math.Min(normalized.Length, MaximumTextLength)];
    }

    private static int? OptionalInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? OptionalInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : null;

    private static bool? OptionalBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static JsonElement RequireObject(JsonElement value, string context) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{context} must be an object.");

    private static JsonElement RequiredProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Required Fleet runtime property '{name}' is missing.");

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException($"Fleet runtime property '{name}' must be a non-empty string.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException("Fleet runtime JSON contains a duplicate property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsBoundedIdentity(string value) =>
        value is { Length: > 0 and <= 160 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '/' or '+' or '-');

    private sealed record BatchIdentity(string Source, string SessionId, string BatchId);

    private sealed record BatchReceipt(string EnvelopeSha256);

    private sealed record TimerProjection(long RemainingMs, string Source);

    private sealed record ProjectionCandidate(
        string Source,
        string SessionId,
        string BatchId,
        DateTimeOffset ProducedAt,
        string ObservationSource,
        long? ObservedAtMs,
        bool FleetBarTracked,
        int? SelectedIndex,
        string EvidenceSha256,
        string EnvelopeSha256,
        FleetRuntimeSlotProjection[] Slots)
    {
        public FleetRuntimeProjectionSnapshot ToSnapshot(long version) =>
            new(
                version,
                Source,
                SessionId,
                BatchId,
                ProducedAt,
                ObservationSource,
                ObservedAtMs,
                FleetBarTracked,
                SelectedIndex,
                EvidenceSha256,
                Slots);
    }
}

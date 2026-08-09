using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleIngestParseStatus
{
    Complete,
    ChunkPending,
    Rejected,
}

internal sealed record BattleIngestParseResult(
    BattleIngestParseStatus Status,
    BattleIngestEnvelope? Envelope,
    BattleIngestFailureCode FailureCode,
    int ReceivedChunks = 0,
    int ChunkCount = 0,
    IDisposable? ProcessingLease = null);

internal sealed partial class BattleIngestEnvelopeProcessor
{
    private const int MaximumIdentityLength = 160;
    private readonly BattleIngestActivation activation;
    private readonly BattleIngestLimits limits;
    private readonly TimeProvider timeProvider;
    private readonly object chunkGate = new();
    private readonly Dictionary<ChunkIdentity, PendingChunkGroup> chunks = [];
    private long pendingChunkBytes;

    public BattleIngestEnvelopeProcessor(
        BattleIngestActivation activation,
        BattleIngestLimits limits,
        TimeProvider? timeProvider = null)
    {
        this.activation = activation ?? throw new ArgumentNullException(nameof(activation));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        limits.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public (int Groups, long Bytes) PendingChunks
    {
        get
        {
            lock (chunkGate)
            {
                PruneExpiredLocked(timeProvider.GetUtcNow());
                return (chunks.Count, pendingChunkBytes);
            }
        }
    }

    public void ClearPendingChunks()
    {
        lock (chunkGate)
        {
            foreach (var group in chunks.Values)
            {
                pendingChunkBytes -= group.ReceivedBytes;
            }
            chunks.Clear();
        }
    }

    public BattleIngestParseResult Parse(ReadOnlyMemory<byte> bytes) =>
        Parse(bytes, limits.MaximumRequestBytes);

    private BattleIngestParseResult Parse(ReadOnlyMemory<byte> bytes, int maximumBytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumBytes)
        {
            return Rejected(BattleIngestFailureCode.PayloadTooLarge);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 48,
            });
            RejectDuplicateProperties(document.RootElement);
            var root = RequireObject(document.RootElement, "ingest envelope");
            var protocol = RequiredString(root, "protocolVersion");
            if (protocol != BattleIngestProtocol.Version)
            {
                return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
            }

            var kind = RequiredString(root, "kind");
            if (kind == BattleIngestProtocol.TransportChunkKind)
            {
                return ParseChunk(root);
            }
            return ParseComplete(root, bytes);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidDataException
                or FormatException
                or OverflowException)
        {
            return Rejected(BattleIngestFailureCode.InvalidRequest);
        }
    }

    private BattleIngestParseResult ParseComplete(
        JsonElement root,
        ReadOnlyMemory<byte> exactBytes)
    {
        var kind = RequiredString(root, "kind");
        if (!activation.Accepts(kind))
        {
            return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
        }

        var batchId = RequiredIdentity(root, "batchId");
        var producedAt = RequiredTimestamp(root, "producedAt");
        var sessionId = RequiredIdentity(root, "sessionId");
        var source = RequiredIdentity(root, "source");
        var modVersion = RequiredIdentity(root, "modVersion");
        var payloadProtocol = RequiredIdentity(root, "payloadProtocol");
        var payload = RequiredProperty(root, "payload");
        IReadOnlyList<ReadOnlyMemory<byte>> exactEvents;

        if (kind == BattleIngestProtocol.BattleEventsKind)
        {
            if (payloadProtocol != BattleIngestProtocol.SidecarEventsVersion
                || payload.ValueKind != JsonValueKind.Array
                || payload.GetArrayLength() is <= 0
                || payload.GetArrayLength() > limits.MaximumBatchEvents)
            {
                return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
            }

            foreach (var item in payload.EnumerateArray())
            {
                ValidateBattleCapture(item);
            }
            exactEvents = ExtractPayloadSlices(exactBytes, expectArray: true);
        }
        else
        {
            if (payloadProtocol != BattleIngestProtocol.FleetRuntimeVersion)
            {
                return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
            }
            ValidateFleetRuntime(payload);
            exactEvents = ExtractPayloadSlices(exactBytes, expectArray: false);
        }

        return new(
            BattleIngestParseStatus.Complete,
            new(
                BattleIngestProtocol.Version,
                kind,
                batchId,
                producedAt,
                sessionId,
                source,
                modVersion,
                payloadProtocol,
                exactBytes,
                exactEvents),
            BattleIngestFailureCode.None);
    }

    private BattleIngestParseResult ParseChunk(JsonElement root)
    {
        if (RequiredIdentity(root, "payloadProtocol") != BattleIngestProtocol.TransportChunkVersion)
        {
            return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
        }

        _ = RequiredIdentity(root, "batchId");
        _ = RequiredTimestamp(root, "producedAt");
        var sessionId = RequiredIdentity(root, "sessionId");
        var source = RequiredIdentity(root, "source");
        _ = RequiredIdentity(root, "modVersion");
        var payload = RequireObject(RequiredProperty(root, "payload"), "transport chunk");
        if (RequiredIdentity(payload, "schemaVersion") != BattleIngestProtocol.TransportChunkVersion
            || RequiredIdentity(payload, "chunkEncoding") != "base64")
        {
            return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
        }

        var groupId = RequiredIdentity(payload, "chunkGroupId");
        var chunkIdentity = new ChunkIdentity(source, sessionId, groupId);
        var originalKind = RequiredIdentity(payload, "originalKind");
        var originalBatchId = RequiredIdentity(payload, "originalBatchId");
        if (!activation.Accepts(originalKind))
        {
            return Rejected(BattleIngestFailureCode.UnsupportedProtocol);
        }

        var index = RequiredInteger(payload, "chunkIndex", 0, limits.MaximumChunkCount - 1);
        var count = RequiredInteger(payload, "chunkCount", 1, limits.MaximumChunkCount);
        var totalBytes = RequiredInteger(payload, "totalBytes", 1, limits.MaximumReassembledBytes);
        if (index >= count)
        {
            return Rejected(BattleIngestFailureCode.InvalidRequest);
        }
        var encoded = RequiredString(payload, "chunkBase64");
        if (encoded.Length > limits.MaximumRequestBytes
            || encoded.Length % 4 != 0
            || !Base64Pattern().IsMatch(encoded))
        {
            return Rejected(BattleIngestFailureCode.InvalidRequest);
        }
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return Rejected(BattleIngestFailureCode.InvalidRequest);
        }
        if (decoded.Length == 0)
        {
            return Rejected(BattleIngestFailureCode.InvalidRequest);
        }

        byte[]? reassembled = null;
        ProcessingMemoryLease? processingLease = null;
        lock (chunkGate)
        {
            var now = timeProvider.GetUtcNow();
            PruneExpiredLocked(now);
            if (!chunks.TryGetValue(chunkIdentity, out var group))
            {
                if (chunks.Count >= limits.MaximumPendingChunkGroups
                    || pendingChunkBytes + decoded.Length > limits.MaximumPendingChunkBytes)
                {
                    return Rejected(BattleIngestFailureCode.Busy);
                }
                group = new(count, totalBytes, originalKind, originalBatchId, now);
                chunks.Add(chunkIdentity, group);
            }
            else if (group.ChunkCount != count
                || group.TotalBytes != totalBytes
                || group.OriginalKind != originalKind
                || group.OriginalBatchId != originalBatchId)
            {
                RemoveGroupLocked(chunkIdentity, group);
                return Rejected(BattleIngestFailureCode.ChunkConflict);
            }

            group.LastUpdated = now;
            var prior = group.Chunks[index];
            if (prior is not null)
            {
                if (!prior.AsSpan().SequenceEqual(decoded))
                {
                    RemoveGroupLocked(chunkIdentity, group);
                    return Rejected(BattleIngestFailureCode.ChunkConflict);
                }
            }
            else
            {
                if (group.ReceivedBytes + decoded.Length > group.TotalBytes
                    || pendingChunkBytes + decoded.Length > limits.MaximumPendingChunkBytes)
                {
                    RemoveGroupLocked(chunkIdentity, group);
                    return Rejected(BattleIngestFailureCode.PayloadTooLarge);
                }
                group.Chunks[index] = decoded;
                group.ReceivedBytes += decoded.Length;
                ++group.ReceivedChunks;
                pendingChunkBytes += decoded.Length;
            }

            if (group.ReceivedChunks != group.ChunkCount)
            {
                return new(
                    BattleIngestParseStatus.ChunkPending,
                    null,
                    BattleIngestFailureCode.None,
                    group.ReceivedChunks,
                    group.ChunkCount);
            }
            if (group.ReceivedBytes != group.TotalBytes)
            {
                RemoveGroupLocked(chunkIdentity, group);
                return Rejected(BattleIngestFailureCode.ChunkConflict);
            }
            if (pendingChunkBytes + group.TotalBytes > limits.MaximumPendingChunkBytes)
            {
                RemoveGroupLocked(chunkIdentity, group);
                return Rejected(BattleIngestFailureCode.Busy);
            }
            pendingChunkBytes += group.TotalBytes;
            processingLease = new(this, group.TotalBytes);
            try
            {
                reassembled = new byte[group.TotalBytes];
                var offset = 0;
                foreach (var chunk in group.Chunks)
                {
                    chunk!.CopyTo(reassembled, offset);
                    offset += chunk.Length;
                }
            }
            catch
            {
                processingLease.Dispose();
                throw;
            }
            RemoveGroupLocked(chunkIdentity, group);
        }

        var result = Parse(reassembled, limits.MaximumReassembledBytes);
        if (result.Status != BattleIngestParseStatus.Complete
            || result.Envelope!.Kind != originalKind
            || result.Envelope.BatchId != originalBatchId
            || result.Envelope.Source != source
            || result.Envelope.SessionId != sessionId)
        {
            processingLease.Dispose();
            return Rejected(
                result.FailureCode == BattleIngestFailureCode.None
                    ? BattleIngestFailureCode.ChunkConflict
                    : result.FailureCode);
        }
        return result with { ProcessingLease = processingLease };
    }

    private static void ValidateBattleCapture(JsonElement value)
    {
        var item = RequireObject(value, "battle event");
        if (RequiredIdentity(item, "protocolVersion") != BattleIngestProtocol.SidecarEventsVersion
            || RequiredIdentity(item, "type") != "battle.capture"
            || RequiredIdentity(item, "schemaVersion") != "stfc.battle.capture.v1")
        {
            throw new InvalidDataException("The battle event family is unsupported.");
        }
        _ = RequiredTimestamp(item, "timestamp");
        _ = RequiredIdentity(item, "journalId");
        _ = RequireObject(RequiredProperty(item, "capture"), "battle capture");
    }

    private static void ValidateFleetRuntime(JsonElement value)
    {
        var payload = RequireObject(value, "fleet runtime payload");
        if (RequiredIdentity(payload, "type") != BattleIngestProtocol.FleetRuntimeKind
            || RequiredIdentity(payload, "schemaVersion") != BattleIngestProtocol.FleetRuntimeVersion)
        {
            throw new InvalidDataException("The fleet runtime schema is unsupported.");
        }
        var slots = RequiredProperty(payload, "slots");
        if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() > 64)
        {
            throw new InvalidDataException("Fleet runtime slots must be a bounded array.");
        }
        foreach (var slot in slots.EnumerateArray())
        {
            _ = RequireObject(slot, "fleet runtime slot");
        }
    }

    private static ReadOnlyCollection<ReadOnlyMemory<byte>> ExtractPayloadSlices(
        ReadOnlyMemory<byte> envelope,
        bool expectArray)
    {
        var reader = new Utf8JsonReader(envelope.Span, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 48,
        });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName
                || reader.CurrentDepth != 1
                || !reader.ValueTextEquals("payload"u8))
            {
                continue;
            }
            if (!reader.Read())
            {
                break;
            }
            if (expectArray)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    break;
                }
                var slices = new List<ReadOnlyMemory<byte>>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var start = checked((int)reader.TokenStartIndex);
                    reader.Skip();
                    var end = checked((int)reader.BytesConsumed);
                    slices.Add(envelope.Slice(start, end - start));
                }
                return slices.AsReadOnly();
            }
            else
            {
                var start = checked((int)reader.TokenStartIndex);
                reader.Skip();
                var end = checked((int)reader.BytesConsumed);
                return Array.AsReadOnly<ReadOnlyMemory<byte>>(
                    [envelope.Slice(start, end - start)]);
            }
        }
        throw new InvalidDataException("The exact payload byte boundary could not be identified.");
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var pair in chunks.ToArray())
        {
            if (now - pair.Value.LastUpdated >= limits.PendingChunkTimeout)
            {
                RemoveGroupLocked(pair.Key, pair.Value);
            }
        }
    }

    private void RemoveGroupLocked(ChunkIdentity id, PendingChunkGroup group)
    {
        if (chunks.Remove(id))
        {
            pendingChunkBytes -= group.ReceivedBytes;
        }
    }

    private void ReleaseProcessingMemory(int bytes)
    {
        lock (chunkGate)
        {
            pendingChunkBytes -= bytes;
        }
    }

    private static BattleIngestParseResult Rejected(BattleIngestFailureCode code) =>
        new(BattleIngestParseStatus.Rejected, null, code);

    private static JsonElement RequireObject(JsonElement value, string context) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{context} must be an object.");

    private static JsonElement RequiredProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Required property '{name}' is missing.");

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException($"Property '{name}' must be a non-empty string.");
    }

    private static string RequiredIdentity(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name);
        return value.Length <= MaximumIdentityLength && IdentityPattern().IsMatch(value)
            ? value
            : throw new InvalidDataException($"Property '{name}' is not a bounded identity.");
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement parent, string name)
    {
        var text = RequiredString(parent, name);
        if (text.Length > 64
            || !TimestampOffsetPattern().IsMatch(text)
            || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new InvalidDataException($"Property '{name}' must be an offset timestamp.");
        }
        return timestamp;
    }

    private static int RequiredInteger(JsonElement parent, string name, int min, int max)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            && result >= min
            && result <= max
            ? result
            : throw new InvalidDataException($"Property '{name}' must be a bounded integer.");
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
                    throw new InvalidDataException(
                        $"JSON contains duplicate property '{property.Name}'.");
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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/+\\-]{0,159}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampOffsetPattern();

    [GeneratedRegex("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Pattern();

    private sealed class PendingChunkGroup(
        int chunkCount,
        int totalBytes,
        string originalKind,
        string originalBatchId,
        DateTimeOffset lastUpdated)
    {
        public int ChunkCount { get; } = chunkCount;
        public int TotalBytes { get; } = totalBytes;
        public string OriginalKind { get; } = originalKind;
        public string OriginalBatchId { get; } = originalBatchId;
        public DateTimeOffset LastUpdated { get; set; } = lastUpdated;
        public byte[]?[] Chunks { get; } = new byte[chunkCount][];
        public int ReceivedChunks { get; set; }
        public int ReceivedBytes { get; set; }
    }

    private sealed class ProcessingMemoryLease(
        BattleIngestEnvelopeProcessor owner,
        int bytes) : IDisposable
    {
        private BattleIngestEnvelopeProcessor? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.ReleaseProcessingMemory(bytes);
        }
    }

    private sealed record ChunkIdentity(string Source, string SessionId, string GroupId);
}

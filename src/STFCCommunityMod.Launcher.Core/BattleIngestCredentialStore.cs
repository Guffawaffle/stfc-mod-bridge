using System.Globalization;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleCredentialRotationReason { Initial, Manual, Recovery }

internal enum BattleCredentialLoadState { Absent, Readable, TooLarge, Invalid, Unavailable }

internal sealed record BattleCredentialMetadata(
    string CredentialId,
    long Generation,
    string ProtocolVersion,
    string PipeName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RotatedAtUtc,
    BattleCredentialRotationReason RotationReason,
    int ProtectedByteCount,
    string ProtectedSha256);

internal sealed record BattleCredentialLoadResult(
    BattleCredentialLoadState State,
    BattleCredentialLease? Lease,
    string Code);

internal interface IBattleCredentialProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedBytes);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiBattleCredentialProtector : IBattleCredentialProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("STFC Mod Bridge Battle ingest credential v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}

internal sealed class BattleCredentialLease : IDisposable
{
    private readonly byte[] credential;
    private bool disposed;

    internal BattleCredentialLease(BattleCredentialMetadata metadata, byte[] credential)
    {
        Metadata = metadata;
        this.credential = credential;
    }

    public BattleCredentialMetadata Metadata { get; }

    public ReadOnlySpan<byte> Credential
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return credential;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        CryptographicOperations.ZeroMemory(credential);
        disposed = true;
    }

    internal bool IsZeroedForTest() => credential.All(value => value == 0);
}

internal sealed class BattleCredentialCandidate : IDisposable
{
    private byte[] protectedBytes;
    private bool disposed;

    internal BattleCredentialCandidate(BattleCredentialLease lease, byte[] protectedBytes)
    {
        Lease = lease;
        this.protectedBytes = protectedBytes;
    }

    public BattleCredentialLease Lease { get; }

    public ReadOnlyMemory<byte> ProtectedBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return protectedBytes;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Lease.Dispose();
        CryptographicOperations.ZeroMemory(protectedBytes);
        protectedBytes = [];
        disposed = true;
    }
}

internal static class BattleIngestCredentialCodec
{
    internal const string Schema = "stfc.battle-ingest-credential.v1";
    internal const string FileName = "ingest-credential-v1.dpapi";
    internal const int MaximumProtectedBytes = 16 * 1024;
    private const int CredentialBytes = 32;

    public static BattleCredentialCandidate CreateCandidate(
        string pipeName,
        long previousGeneration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset rotatedAtUtc,
        BattleCredentialRotationReason rotationReason,
        IBattleCredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var normalizedPipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
        if (previousGeneration < 0 || previousGeneration == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(previousGeneration));
        var generation = checked(previousGeneration + 1);
        ValidateMetadata(generation, createdAtUtc, rotatedAtUtc, rotationReason);
        var credential = RandomNumberGenerator.GetBytes(CredentialBytes);
        var credentialId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var plaintext = Serialize(
            credentialId, generation, normalizedPipeName, createdAtUtc, rotatedAtUtc, rotationReason, credential);
        try
        {
            var protectedBytes = protector.Protect(plaintext)
                ?? throw new InvalidDataException("The Battle credential protector returned no result.");
            if (ReferenceEquals(protectedBytes, plaintext)) protectedBytes = protectedBytes.ToArray();
            if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                throw new InvalidDataException("The protected Battle credential is outside its size bound.");
            }
            var metadata = CreateMetadata(
                credentialId, generation, normalizedPipeName, createdAtUtc, rotatedAtUtc, rotationReason, protectedBytes);
            return new(new(metadata, credential), protectedBytes);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(credential);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static BattleCredentialLease Decode(byte[] protectedBytes, IBattleCredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        ArgumentNullException.ThrowIfNull(protector);
        if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
            throw new InvalidDataException("The protected Battle credential is outside its size bound.");
        byte[]? plaintext = null;
        byte[]? credential = null;
        try
        {
            plaintext = protector.Unprotect(protectedBytes)
                ?? throw new InvalidDataException("The Battle credential protector returned no plaintext.");
            var parsed = Parse(plaintext);
            credential = parsed.Credential;
            var canonical = Serialize(
                parsed.CredentialId, parsed.Generation, parsed.PipeName, parsed.CreatedAtUtc,
                parsed.RotatedAtUtc, parsed.RotationReason, credential);
            try
            {
                if (!plaintext.AsSpan().SequenceEqual(canonical))
                    throw new InvalidDataException("The Battle credential record is not canonical.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            var lease = new BattleCredentialLease(
                CreateMetadata(
                    parsed.CredentialId, parsed.Generation, parsed.PipeName, parsed.CreatedAtUtc,
                    parsed.RotatedAtUtc, parsed.RotationReason, protectedBytes),
                credential);
            credential = null;
            return lease;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (credential is not null) CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static BattleCredentialMetadata CreateMetadata(
        string id, long generation, string pipeName, DateTimeOffset created, DateTimeOffset rotated,
        BattleCredentialRotationReason reason, byte[] protectedBytes) =>
        new(id, generation, BattleLocalIpcProtocol.Version, pipeName, created, rotated, reason,
            protectedBytes.Length, Convert.ToHexString(SHA256.HashData(protectedBytes)).ToLowerInvariant());

    private static byte[] Serialize(
        string credentialId,
        long generation,
        string pipeName,
        DateTimeOffset createdAtUtc,
        DateTimeOffset rotatedAtUtc,
        BattleCredentialRotationReason rotationReason,
        ReadOnlySpan<byte> credential)
    {
        using var stream = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("credentialId", credentialId);
            writer.WriteNumber("generation", generation);
            writer.WriteString("protocolVersion", BattleLocalIpcProtocol.Version);
            writer.WriteString("pipeName", pipeName);
            writer.WriteString("createdAtUtc", FormatTimestamp(createdAtUtc));
            writer.WriteString("rotatedAtUtc", FormatTimestamp(rotatedAtUtc));
            writer.WriteString("rotationReason", FormatReason(rotationReason));
            writer.WriteString("credential", Base64UrlEncode(credential));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static ParsedCredential Parse(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
            var name = reader.GetString() ?? throw Invalid();
            if (!values.TryAdd(name, string.Empty) || !reader.Read()) throw Invalid();
            values[name] = name == "generation"
                ? reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number)
                    ? number : throw Invalid()
                : reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw Invalid();
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || values.Count != 9) throw Invalid();
        string[] expected =
        [
            "schema", "credentialId", "generation", "protocolVersion", "pipeName",
            "createdAtUtc", "rotatedAtUtc", "rotationReason", "credential",
        ];
        if (expected.Any(name => !values.ContainsKey(name))) throw Invalid();

        var schema = (string)values["schema"];
        var id = (string)values["credentialId"];
        var generation = (long)values["generation"];
        var protocol = (string)values["protocolVersion"];
        var pipeName = (string)values["pipeName"];
        if (schema != Schema || protocol != BattleLocalIpcProtocol.Version || !IsLowerHexId(id)) throw Invalid();
        var normalizedPipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
        var created = ParseTimestamp((string)values["createdAtUtc"]);
        var rotated = ParseTimestamp((string)values["rotatedAtUtc"]);
        var reason = ParseReason((string)values["rotationReason"]);
        ValidateMetadata(generation, created, rotated, reason);
        var credential = Base64UrlDecode((string)values["credential"]);
        if (credential.Length != CredentialBytes)
        {
            CryptographicOperations.ZeroMemory(credential);
            throw Invalid();
        }
        return new(id, generation, normalizedPipeName, created, rotated, reason, credential);
    }

    private static void ValidateMetadata(
        long generation,
        DateTimeOffset createdAtUtc,
        DateTimeOffset rotatedAtUtc,
        BattleCredentialRotationReason reason)
    {
        if (generation <= 0 || createdAtUtc.Offset != TimeSpan.Zero || rotatedAtUtc.Offset != TimeSpan.Zero
            || rotatedAtUtc < createdAtUtc || (generation == 1) != (reason == BattleCredentialRotationReason.Initial)
            || !Enum.IsDefined(reason))
            throw new ArgumentException("The Battle credential metadata is invalid.");
    }

    private static bool IsLowerHexId(string value) => value.Length == 32 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero || FormatTimestamp(parsed) != value)
            throw Invalid();
        return parsed;
    }

    private static string FormatReason(BattleCredentialRotationReason reason) => reason switch
    {
        BattleCredentialRotationReason.Initial => "initial",
        BattleCredentialRotationReason.Manual => "manual",
        BattleCredentialRotationReason.Recovery => "recovery",
        _ => throw Invalid(),
    };

    private static BattleCredentialRotationReason ParseReason(string value) => value switch
    {
        "initial" => BattleCredentialRotationReason.Initial,
        "manual" => BattleCredentialRotationReason.Manual,
        "recovery" => BattleCredentialRotationReason.Recovery,
        _ => throw Invalid(),
    };

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (value.Length != 43 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) throw Invalid();
        try
        {
            var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            if (Base64UrlEncode(decoded) != value)
            {
                CryptographicOperations.ZeroMemory(decoded);
                throw Invalid();
            }
            return decoded;
        }
        catch (FormatException exception)
        {
            throw Invalid(exception);
        }
    }

    private static InvalidDataException Invalid(Exception? inner = null) =>
        new("The Battle credential record is invalid.", inner);

    private sealed record ParsedCredential(
        string CredentialId, long Generation, string PipeName, DateTimeOffset CreatedAtUtc,
        DateTimeOffset RotatedAtUtc, BattleCredentialRotationReason RotationReason, byte[] Credential);
}

internal sealed class BattleIngestCredentialStore
{
    private readonly IBattleCredentialProtector protector;

    public BattleIngestCredentialStore(string stateRoot, IBattleCredentialProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        Path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(stateRoot), "battle", BattleIngestCredentialCodec.FileName);
    }

    public string Path { get; }

    public BattleCredentialLoadResult Load()
    {
        try
        {
            using var stream = OpenLockedReadNoFollow(Path);
            if (stream.Length is <= 0 or > BattleIngestCredentialCodec.MaximumProtectedBytes)
                return new(BattleCredentialLoadState.TooLarge, null, "credential-size-invalid");
            var protectedBytes = new byte[stream.Length];
            stream.ReadExactly(protectedBytes);
            try
            {
                return new(BattleCredentialLoadState.Readable,
                    BattleIngestCredentialCodec.Decode(protectedBytes, protector), "credential-readable");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BattleCredentialLoadState.Absent, null, "credential-absent");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return new(BattleCredentialLoadState.Absent, null, "credential-absent");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(BattleCredentialLoadState.Unavailable, null, "credential-unavailable");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or CryptographicException or ArgumentException or JsonException)
        {
            return new(BattleCredentialLoadState.Invalid, null, "credential-invalid");
        }
        catch (Win32Exception)
        {
            return new(BattleCredentialLoadState.Unavailable, null, "credential-unavailable");
        }
    }

    private static FileStream OpenLockedReadNoFollow(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.None);
        return new(CandidateFileNative.OpenRecoveryReadNoFollow(path), FileAccess.Read, 81920, isAsync: false);
    }
}

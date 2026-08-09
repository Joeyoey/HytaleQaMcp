using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Orchestrator;

public sealed record LauncherSingleplayerProofRecord(
    string Schema,
    string EvidenceId,
    string SessionId,
    string SessionNonceProtected,
    string ServerEndpoint,
    string WorldId,
    string WorldDirectory,
    string WorldManifestPath,
    string WorldManifestSha256,
    PinnedProcessEvidence Launcher,
    PinnedProcessEvidence Client,
    PinnedProcessEvidence Server,
    string ServerArtifactPath,
    string ServerArtifactSha256,
    string AssetsPath,
    string AssetsSha256,
    string ObserverSpoolDirectory,
    string LauncherLogPath,
    string LauncherLogSha256,
    string ClientLogPath,
    string ClientLogSha256,
    string WorldServerLogPath,
    string WorldServerLogSha256,
    DateTimeOffset LaunchObservedAtUtc,
    string LaunchEvidenceSha256,
    string LaunchEvidenceMac);

public sealed record LauncherProofSummary(
    string EvidenceId,
    string SessionId,
    string WorldId,
    string ServerEndpoint,
    int LauncherProcessId,
    int ClientProcessId,
    int ServerProcessId,
    string LaunchEvidenceSha256,
    DateTimeOffset ObservedAtUtc,
    int ClientProcessCount,
    bool Ready);

public interface ILauncherProcessInspector
{
    PinnedProcessEvidence Inspect(int processId);
    PinnedProcessEvidence InspectIdentity(int processId, string pinnedSha256) => Inspect(processId);
}

public sealed class WindowsLauncherProcessInspector : ILauncherProcessInspector
{
    public PinnedProcessEvidence Inspect(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Unable to read process executable path.");
        return new(process.Id, ParentProcessId(process.Id), process.StartTime.ToUniversalTime(),
            Path.GetFullPath(path), HashFile(path));
    }

    public PinnedProcessEvidence InspectIdentity(int processId, string pinnedSha256)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Unable to read process executable path.");
        return new(process.Id, ParentProcessId(process.Id), process.StartTime.ToUniversalTime(),
            Path.GetFullPath(path), pinnedSha256);
    }

    private static int ParentProcessId(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                if (entry.ProcessId == processId) return checked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            throw new InvalidOperationException("Process disappeared while its parent was inspected.");
        }
        finally { CloseHandle(snapshot); }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public int ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

public static class LauncherProofAuthentication
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HYTALE-QA-LAUNCHER-SINGLEPLAYER-v1");

    public static byte[] CanonicalBytes(LauncherSingleplayerProofRecord record)
    {
        var fields = new[]
        {
            record.Schema, record.EvidenceId, record.SessionId, record.ServerEndpoint, record.WorldId,
            Full(record.WorldDirectory), Full(record.WorldManifestPath), Hash(record.WorldManifestSha256),
            Pin(record.Launcher), Pin(record.Client), Pin(record.Server),
            Full(record.ServerArtifactPath), Hash(record.ServerArtifactSha256),
            Full(record.AssetsPath), Hash(record.AssetsSha256), Full(record.ObserverSpoolDirectory),
            Full(record.LauncherLogPath), Hash(record.LauncherLogSha256),
            Full(record.ClientLogPath), Hash(record.ClientLogSha256),
            Full(record.WorldServerLogPath), Hash(record.WorldServerLogSha256),
            record.LaunchObservedAtUtc.ToUniversalTime().ToString("O")
        };
        var builder = new StringBuilder(2048);
        foreach (var field in fields) builder.Append(field.Length).Append(':').Append(field);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static string ComputeSha256(LauncherSingleplayerProofRecord record) =>
        Convert.ToHexString(SHA256.HashData(CanonicalBytes(record)));

    public static string ComputeMac(LauncherSingleplayerProofRecord record, string nonce)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(nonce));
        return Convert.ToHexString(hmac.ComputeHash(CanonicalBytes(record)));
    }

    public static string ProtectNonce(string nonce)
    {
        var plain = Encoding.UTF8.GetBytes(nonce);
        try { return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string UnprotectNonce(string protectedNonce)
    {
        byte[] cipher;
        try { cipher = Convert.FromBase64String(protectedNonce); }
        catch (FormatException exception) { throw new InvalidDataException("Launcher nonce protection is invalid.", exception); }
        try
        {
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(cipher); }
    }

    private static string Pin(PinnedProcessEvidence pin) =>
        $"{pin.ProcessId}|{pin.ParentProcessId}|{pin.CreationTimeUtc.ToUniversalTime():O}|{Full(pin.ExecutablePath)}|{pin.Sha256.ToUpperInvariant()}";
    private static string Hash(string sha256) => sha256.ToUpperInvariant();
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}

public sealed class LauncherSingleplayerProofProvider : IWorkerOfflineProofProvider
{
    public const string Schema = "hytale-qa-launcher-singleplayer-offline-proof-v2";
    public const string ObserverBoundary = "LOOPBACK_PROCESS";
    private readonly LauncherSingleplayerProofRecord record;
    private readonly string nonce;
    private readonly ILauncherProcessInspector processes;
    private readonly IObserverSpoolClient observer;
    private readonly IReadOnlyDictionary<string, FilePin> files;
    private readonly LauncherSingleplayerEvidence evidence;
    private bool healthVerified;

    public LauncherSingleplayerProofProvider(LauncherSingleplayerProofRecord record, string nonce,
        ILauncherProcessInspector processes, IObserverSpoolClient observer)
    {
        this.record = record;
        this.nonce = nonce;
        this.processes = processes;
        this.observer = observer;
        ValidateRecordShape(record, nonce);
        ValidateProcessTree(fullHash: true);
        if (record.Client.ParentProcessId != record.Launcher.ProcessId || record.Server.ParentProcessId != record.Client.ProcessId)
            throw new InvalidDataException("Launcher evidence is not an exact launcher-to-client-to-server process tree.");
        files = new Dictionary<string, FilePin>(StringComparer.OrdinalIgnoreCase)
        {
            ["worldManifest"] = PinFile(record.WorldManifestPath, record.WorldManifestSha256, "worldManifest"),
            ["serverArtifact"] = PinFile(record.ServerArtifactPath, record.ServerArtifactSha256, "serverArtifact"),
            ["assets"] = PinFile(record.AssetsPath, record.AssetsSha256, "assets"),
            ["launcherLog"] = PinFile(record.LauncherLogPath, record.LauncherLogSha256, "launcherLog"),
            ["clientLog"] = PinFile(record.ClientLogPath, record.ClientLogSha256, "clientLog"),
            ["worldServerLog"] = PinFile(record.WorldServerLogPath, record.WorldServerLogSha256, "worldServerLog")
        };
        evidence = new(record.EvidenceId, record.WorldId, HashText(Path.GetFullPath(record.WorldDirectory)),
            files["worldManifest"].Sha256, record.Launcher, record.Client, record.Server,
            files["serverArtifact"].Sha256, files["assets"].Sha256, record.LaunchEvidenceSha256,
            true, true, false);
    }

    public OfflineProofBoundaryKind Boundary => OfflineProofBoundaryKind.LauncherOwnedSingleplayer;
    public string SessionId => record.SessionId;
    public int ClientProcessId => record.Client.ProcessId;

    public async Task<OfflineServerProof> GetFreshProofAsync(CancellationToken cancellationToken)
    {
        // Executable content was fully hashed at construction. A live Windows
        // process pins its loaded executable while PID, parent, creation time,
        // and canonical path are revalidated on every heartbeat. Rehashing the
        // retail client for every 500 ms heartbeat made otherwise valid proof
        // expire before the worker could receive it.
        ValidateProcessTree(fullHash: false);
        foreach (var pair in files) pair.Value.Revalidate(pair.Key);
        var heartbeat = await observer.VerifyHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        if (!heartbeat.Ready || !heartbeat.Offline || !heartbeat.PacketEvidenceValid)
            throw new InvalidOperationException("Observer no longer attests launcher-owned offline singleplayer.");
        if (!healthVerified)
        {
            var health = await observer.HealthAsync(cancellationToken).ConfigureAwait(false);
            if (!health.Ready || !health.Offline || !health.PacketEvidenceValid ||
                !string.Equals(health.BridgeNetworkBoundary, ObserverBoundary, StringComparison.Ordinal))
                throw new InvalidOperationException("Observer HEALTH does not attest launcher-owned singleplayer.");
            healthVerified = true;
        }
        return new("offline", true, record.ServerEndpoint, "", "", "", files["serverArtifact"].Sha256,
            nonce, heartbeat.ObservedAt, OfflineProofKind.LauncherOwnedSingleplayer,
            evidence with { ObserverBoundaryVerified = true });
    }

    public async Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken)
    {
        _ = await GetFreshProofAsync(cancellationToken).ConfigureAwait(false);
        return await observer.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRecordShape(LauncherSingleplayerProofRecord record, string nonce)
    {
        if (!string.Equals(record.Schema, Schema, StringComparison.Ordinal)) throw new InvalidDataException("Launcher proof schema is unsupported.");
        if (!Guid.TryParse(record.EvidenceId, out var evidence) || evidence == Guid.Empty ||
            !Guid.TryParse(record.SessionId, out var session) || session == Guid.Empty ||
            !Guid.TryParse(record.WorldId, out var world) || world == Guid.Empty)
            throw new InvalidDataException("Evidence, session, and world ids must be non-empty UUIDs.");
        if (!Uri.TryCreate("udp://" + record.ServerEndpoint, UriKind.Absolute, out var endpoint) ||
            !System.Net.IPAddress.TryParse(endpoint.Host, out var address) || !System.Net.IPAddress.IsLoopback(address) || endpoint.Port <= 0)
            throw new InvalidDataException("Launcher-owned server endpoint must be an explicit loopback address.");
        if (record.LaunchObservedAtUtc > DateTimeOffset.UtcNow.AddSeconds(2))
            throw new InvalidDataException("Verified launch evidence is future-dated.");
        if (nonce.Length != 64 || !nonce.All(Uri.IsHexDigit))
            throw new InvalidDataException("Launcher observer nonce must be 32-byte hexadecimal data.");
        var hashes = new[]
        {
            record.WorldManifestSha256, record.ServerArtifactSha256, record.AssetsSha256,
            record.LauncherLogSha256, record.ClientLogSha256, record.WorldServerLogSha256,
            record.LaunchEvidenceSha256, record.LaunchEvidenceMac,
            record.Launcher.Sha256, record.Client.Sha256, record.Server.Sha256
        };
        if (hashes.Any(hash => hash.Length != 64 || hash.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))))
            throw new InvalidDataException("Launcher proof hashes must be uppercase SHA-256 hexadecimal values.");
        if (record.Launcher.ProcessId <= 0 || record.Client.ProcessId <= 0 || record.Server.ProcessId <= 0 ||
            record.Launcher.ProcessId == record.Client.ProcessId || record.Client.ProcessId == record.Server.ProcessId ||
            record.Launcher.CreationTimeUtc > record.Client.CreationTimeUtc ||
            record.Client.CreationTimeUtc > record.Server.CreationTimeUtc ||
            record.Server.CreationTimeUtc > record.LaunchObservedAtUtc.AddSeconds(2))
            throw new InvalidDataException("Launch process chronology or identity is invalid.");
        var worldDirectory = Path.GetFullPath(record.WorldDirectory);
        if (!Directory.Exists(worldDirectory) || (File.GetAttributes(worldDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pinned world directory is missing or unsafe.");
        var manifest = Path.GetFullPath(record.WorldManifestPath);
        var relativeManifest = Path.GetRelativePath(worldDirectory, manifest);
        if (Path.IsPathRooted(relativeManifest) || relativeManifest.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException("World manifest is not contained by the exact pinned world directory.");
        var sha = LauncherProofAuthentication.ComputeSha256(record);
        var mac = LauncherProofAuthentication.ComputeMac(record, nonce);
        if (!Fixed(sha, record.LaunchEvidenceSha256) || !Fixed(mac, record.LaunchEvidenceMac))
            throw new InvalidDataException("Verified launch evidence digest or authentication tag is invalid.");
    }

    private static void ValidateProcess(PinnedProcessEvidence expected, PinnedProcessEvidence actual)
    {
        if (expected.ProcessId != actual.ProcessId || expected.ParentProcessId != actual.ParentProcessId ||
            expected.CreationTimeUtc != actual.CreationTimeUtc || !PathEqual(expected.ExecutablePath, actual.ExecutablePath) ||
            !Fixed(expected.Sha256, actual.Sha256))
            throw new InvalidOperationException($"Pinned process identity changed: {expected.ProcessId}.");
    }

    private void ValidateProcessTree(bool fullHash)
    {
        PinnedProcessEvidence Current(PinnedProcessEvidence expected) => fullHash
            ? processes.Inspect(expected.ProcessId)
            : processes.InspectIdentity(expected.ProcessId, expected.Sha256);
        var client = Current(record.Client);
        var server = Current(record.Server);
        ValidateProcess(record.Client, client);
        ValidateProcess(record.Server, server);
        try
        {
            ValidateProcess(record.Launcher, Current(record.Launcher));
        }
        catch (ArgumentException) when (client.ParentProcessId == record.Launcher.ProcessId)
        {
            // The official launcher may close after Play. Its authenticated
            // bootstrap pin remains admissible only while the live, pinned
            // client still reports that exact launcher PID as its OS parent.
            // If the PID is reused, Inspect succeeds and the full pin must
            // match; any other inspection failure remains fatal.
        }
    }

    private static FilePin PinFile(string path, string expectedSha256, string name)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new FileNotFoundException("Pinned evidence file is missing or unsafe.", full);
        var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
            if (!Fixed(expectedSha256, actualSha256))
                throw new InvalidDataException($"Pinned {name} SHA-256 does not match authenticated launch evidence.");
            stream.Position = 0;
            return new(full, info.Length, info.LastWriteTimeUtc, actualSha256, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool PathEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static bool Fixed(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left); var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Dispose()
    {
        foreach (var pin in files.Values) pin.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record FilePin(string Path, long Length, DateTime LastWriteUtc, string Sha256, FileStream Stream)
        : IDisposable
    {
        public void Revalidate(string name)
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length != Length || info.LastWriteTimeUtc != LastWriteUtc ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 || Stream.Length != Length)
                throw new InvalidOperationException($"Pinned {name} identity changed.");
        }

        public void Dispose() => Stream.Dispose();
    }
}

public interface ILauncherProofValidator
{
    Task<LauncherProofSummary> ValidateAsync(string evidenceFileName, CancellationToken cancellationToken);
    LauncherEvidenceSeal Seal(string evidenceFileName, string purpose, string sha256);
    void VerifySeal(LauncherEvidenceSeal seal, string purpose, string sha256);
}

public sealed record LauncherEvidenceSeal(string Schema, string Purpose, string SessionId, string EvidenceId,
    string Sha256, string SessionNonceProtected, string Mac);

public sealed class LauncherSingleplayerProofProviderFactory(
    QaPaths paths, IObserverSpoolClientFactory observerClients, ILauncherProcessInspector processes)
    : ILauncherProofValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public LauncherSingleplayerProofProvider Create(string evidenceFileName)
    {
        var path = EvidencePath(evidenceFileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 128 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Launcher proof record is missing, unsafe, or oversized.");
        var record = ReadProofRecord(path);
        if (!string.Equals(Path.GetFileNameWithoutExtension(evidenceFileName), record.SessionId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Launcher proof file name does not match its authenticated session id.");
        var nonce = LauncherProofAuthentication.UnprotectNonce(record.SessionNonceProtected);
        var spool = Path.GetFullPath(record.ObserverSpoolDirectory);
        if (!string.Equals(spool.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(paths.LauncherObserverSpoolDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Launcher proof observer spool does not match the isolated launcher control boundary.");
        if (!Directory.Exists(spool) || (File.GetAttributes(spool) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Launcher observer spool is missing or unsafe.");
        var logRoot = Path.GetFullPath(Path.Combine(paths.LauncherProofDirectory,
            record.SessionId + "-launch-evidence"));
        if (!Directory.Exists(logRoot) || (File.GetAttributes(logRoot) & FileAttributes.ReparsePoint) != 0 ||
            new[] { record.LauncherLogPath, record.ClientLogPath, record.WorldServerLogPath }.Any(log =>
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(log)), logRoot,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Launcher log snapshots do not match the exact isolated session evidence directory.");
        return new(record, nonce, processes,
            observerClients.Create(spool, nonce, LauncherSingleplayerProofProvider.ObserverBoundary));
    }

    public LauncherEvidenceSeal Seal(string evidenceFileName, string purpose, string sha256)
    {
        var record = ReadRecord(evidenceFileName);
        var nonce = LauncherProofAuthentication.UnprotectNonce(record.SessionNonceProtected);
        return new("hytale-qa-evidence-seal-v1", purpose, record.SessionId, record.EvidenceId,
            RequireSha256(sha256), record.SessionNonceProtected,
            EvidenceSealMac(nonce, purpose, record.SessionId, record.EvidenceId, sha256));
    }

    public void VerifySeal(LauncherEvidenceSeal seal, string purpose, string sha256)
    {
        if (!string.Equals(seal.Schema, "hytale-qa-evidence-seal-v1", StringComparison.Ordinal) ||
            !string.Equals(seal.Purpose, purpose, StringComparison.Ordinal) ||
            !FixedSeal(seal.Sha256, RequireSha256(sha256)) ||
            !Guid.TryParse(seal.SessionId, out var session) || session == Guid.Empty ||
            !Guid.TryParse(seal.EvidenceId, out var evidence) || evidence == Guid.Empty)
            throw new InvalidDataException("QA evidence seal identity is invalid.");
        var nonce = LauncherProofAuthentication.UnprotectNonce(seal.SessionNonceProtected);
        var expected = EvidenceSealMac(nonce, seal.Purpose, seal.SessionId, seal.EvidenceId, seal.Sha256);
        if (!FixedSeal(expected, seal.Mac)) throw new InvalidDataException("QA evidence seal MAC is invalid.");
    }

    public async Task<LauncherProofSummary> ValidateAsync(string evidenceFileName, CancellationToken cancellationToken)
    {
        using var provider = Create(evidenceFileName);
        var proof = await provider.GetFreshProofAsync(cancellationToken).ConfigureAwait(false);
        var clients = Process.GetProcessesByName("HytaleClient");
        try
        {
            if (clients.Length != 1 || clients[0].Id != proof.Launcher!.Client.ProcessId)
                throw new InvalidDataException(
                    "Launcher proof requires exactly one Hytale client and it must be the pinned client.");
        }
        finally { foreach (var client in clients) client.Dispose(); }
        return new(proof.Launcher!.EvidenceId, provider.SessionId, proof.Launcher.WorldId, proof.ServerEndpoint,
            proof.Launcher.Launcher.ProcessId, proof.Launcher.Client.ProcessId, proof.Launcher.Server.ProcessId,
            proof.Launcher.LaunchEvidenceSha256, proof.ObservedAtUtc, 1, true);
    }

    private string EvidencePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Evidence must be a plain .json file name.", nameof(fileName));
        var root = Path.GetFullPath(paths.LauncherProofDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Launcher evidence escaped its isolated directory.");
        return candidate;
    }

    private LauncherSingleplayerProofRecord ReadRecord(string evidenceFileName)
    {
        var path = EvidencePath(evidenceFileName);
        return ReadProofRecord(path);
    }

    private static LauncherSingleplayerProofRecord ReadProofRecord(string path)
    {
        var bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> payload = bytes;
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
            payload = payload[3..];
        var node = JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidDataException("Launcher proof record is empty.");
        if (node["launcher"] is JsonObject launcher)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "processId", "parentProcessId", "creationTimeUtc", "executablePath", "sha256", "schema", "mac" };
            if (launcher.Select(property => property.Key).Any(key => !allowed.Contains(key)))
                throw new InvalidDataException("Launcher process evidence contains an unknown member.");
            launcher.Remove("schema");
            launcher.Remove("mac");
        }
        return JsonSerializer.Deserialize<LauncherSingleplayerProofRecord>(node.ToJsonString(), JsonOptions)
            ?? throw new InvalidDataException("Launcher proof record is empty.");
    }

    private static string EvidenceSealMac(string nonce, string purpose, string sessionId, string evidenceId, string sha256)
    {
        var canonical = $"HYTALE-QA-EVIDENCE-SEAL/1\npurpose={purpose}\nsessionId={sessionId}\nevidenceId={evidenceId}\nsha256={RequireSha256(sha256)}\n";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(nonce));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string RequireSha256(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper.Length != 64 || upper.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new InvalidDataException("QA evidence seal SHA-256 is invalid.");
        return upper;
    }

    private static bool FixedSeal(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left); var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

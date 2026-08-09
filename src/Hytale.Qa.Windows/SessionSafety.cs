using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Windows;

public interface IProcessInspector
{
    ProcessIdentity Inspect(int processId);
    ProcessIdentity InspectIdentity(int processId, string pinnedSha256) => Inspect(processId);
    int CountByExecutableName(string executableName);
    SafetyCheck ValidateWindowIdentity(ProcessIdentity identity);
    bool IsForeground(ProcessIdentity identity);
    bool TrySetForeground(ProcessIdentity identity);
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    public ProcessIdentity Inspect(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Unable to read executable path.");
        var handle = process.MainWindowHandle;
        if (handle == nint.Zero) throw new InvalidOperationException("Process has no top-level window.");
        return new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path), ComputeSha256(path), handle);
    }

    public ProcessIdentity InspectIdentity(int processId, string pinnedSha256)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Unable to read executable path.");
        var handle = process.MainWindowHandle;
        if (handle == nint.Zero) throw new InvalidOperationException("Process has no top-level window.");
        return new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path), pinnedSha256, handle);
    }

    public int CountByExecutableName(string executableName)
    {
        var stem = Path.GetFileNameWithoutExtension(executableName);
        var processes = Process.GetProcessesByName(stem);
        try { return processes.Length; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public SafetyCheck ValidateWindowIdentity(ProcessIdentity identity)
    {
        if (!NativeMethods.IsWindow((nint)identity.WindowHandle))
            return SafetyCheck.Fail("window.invalid", "Window handle is not valid.");
        NativeMethods.GetWindowThreadProcessId((nint)identity.WindowHandle, out var owner);
        return owner == identity.ProcessId
            ? SafetyCheck.Pass()
            : SafetyCheck.Fail("window.owner", "Window is not owned by the leased process.");
    }

    public bool IsForeground(ProcessIdentity identity)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || foreground != (nint)identity.WindowHandle) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        return pid == identity.ProcessId;
    }

    public bool TrySetForeground(ProcessIdentity identity) => NativeMethods.SetForegroundWindow((nint)identity.WindowHandle);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class ClientLeaseManager
{
    private readonly IProcessInspector inspector;
    private readonly object sync = new();
    private ClientLease? current;

    public ClientLeaseManager(IProcessInspector inspector) => this.inspector = inspector;
    public ClientLease? Current { get { lock (sync) return current; } }

    public ClientLease Acquire(int processId, SessionSafetyPolicy policy)
    {
        lock (sync)
        {
            if (current is not null) throw new InvalidOperationException("A client lease is already active.");
            var policyCheck = SafetyPolicyValidator.Validate(policy);
            if (!policyCheck.Safe) throw new InvalidOperationException($"{policyCheck.Code}: {policyCheck.Message}");
            var identity = inspector.Inspect(processId);
            var identityCheck = ValidateIdentity(identity, policy);
            if (!identityCheck.Safe) throw new InvalidOperationException($"{identityCheck.Code}: {identityCheck.Message}");
            current = new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, identity, policy);
            return current;
        }
    }

    public SafetyCheck Revalidate(string leaseId, bool requireForeground)
    {
        lock (sync)
        {
            if (current is null || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(current.LeaseId),
                    System.Text.Encoding.UTF8.GetBytes(leaseId)))
                return SafetyCheck.Fail("lease.invalid", "No matching active client lease.");
            ProcessIdentity actual;
            // Acquire fully hashes the exact executable. A live Windows image
            // remains pinned by the process; hot-path watchdog checks revalidate
            // PID, creation time, canonical path, HWND, owner, and one-process
            // count without rehashing the retail client ten times per second.
            try { actual = inspector.InspectIdentity(current.Identity.ProcessId, current.Identity.Sha256); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return SafetyCheck.Fail("process.missing", "The leased process is no longer available."); }
            if (actual != current.Identity) return SafetyCheck.Fail("process.identity_changed", "PID, creation time, path, hash, or HWND changed.");
            var identityCheck = ValidateIdentity(actual, current.Policy);
            if (!identityCheck.Safe) return identityCheck;
            if (requireForeground && !inspector.IsForeground(actual))
                return SafetyCheck.Fail("window.not_foreground", "The leased client is not the foreground window.");
            return SafetyCheck.Pass();
        }
    }

    public bool TryFocus(string leaseId)
    {
        var check = Revalidate(leaseId, false);
        if (!check.Safe) return false;
        lock (sync) return current is not null && inspector.TrySetForeground(current.Identity);
    }

    public void Release(string leaseId)
    {
        lock (sync)
        {
            if (current?.LeaseId != leaseId) throw new InvalidOperationException("Lease id does not match the active lease.");
            current = null;
        }
    }

    private SafetyCheck ValidateIdentity(ProcessIdentity identity, SessionSafetyPolicy policy)
    {
        if (!string.Equals(Path.GetFileName(identity.ExecutablePath), policy.ExpectedExecutableName, StringComparison.OrdinalIgnoreCase))
            return SafetyCheck.Fail("process.name", "Executable name is not allowlisted.");
        var normalized = Path.GetFullPath(identity.ExecutablePath);
        if (!policy.AllowedExecutablePaths.Any(path => string.Equals(Path.GetFullPath(path), normalized, StringComparison.OrdinalIgnoreCase)))
            return SafetyCheck.Fail("process.path", "Executable path is not allowlisted.");
        if (!policy.AllowedSha256.Any(hash => string.Equals(hash, identity.Sha256, StringComparison.OrdinalIgnoreCase)))
            return SafetyCheck.Fail("process.hash", "Executable SHA-256 is not allowlisted.");
        var windowCheck = inspector.ValidateWindowIdentity(identity);
        if (!windowCheck.Safe) return windowCheck;
        if (policy.RequireSingleMatchingProcess && inspector.CountByExecutableName(policy.ExpectedExecutableName) != 1)
            return SafetyCheck.Fail("process.count", "Exactly one matching client process is required.");
        return SafetyCheck.Pass();
    }
}

public static class SafetyPolicyValidator
{
    public static SafetyCheck Validate(SessionSafetyPolicy policy)
    {
        if (!policy.OfflineAttested) return SafetyCheck.Fail("server.not_offline", "Offline attestation is mandatory.");
        if (string.IsNullOrWhiteSpace(policy.SessionId) || string.IsNullOrWhiteSpace(policy.Nonce))
            return SafetyCheck.Fail("session.identity", "Session id and nonce are mandatory.");
        if (!TryParseLoopbackEndpoint(policy.ServerEndpoint))
            return SafetyCheck.Fail("server.endpoint", "Server endpoint must be an explicit IP loopback address with a valid port.");
        if (!string.Equals(policy.ExpectedExecutableName, "HytaleClient.exe", StringComparison.OrdinalIgnoreCase))
            return SafetyCheck.Fail("process.expected_name", "This worker only permits HytaleClient.exe.");
        if (policy.AllowedExecutablePaths.Count == 0 || policy.AllowedSha256.Count == 0)
            return SafetyCheck.Fail("process.allowlist", "Executable path and SHA-256 allowlists are mandatory.");
        if (policy.AllowedSha256.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            return SafetyCheck.Fail("process.hash_format", "Every SHA-256 allowlist entry must be 64 hexadecimal characters.");
        return SafetyCheck.Pass();
    }

    public static SafetyCheck ValidateOfflineProof(SessionSafetyPolicy policy, OfflineServerProof proof,
        EvidenceCapabilities capabilities, ProcessIdentity? leasedClient = null)
    {
        if (!proof.Offline || !string.Equals(proof.AuthMode, "offline", StringComparison.OrdinalIgnoreCase))
            return SafetyCheck.Fail("proof.auth_mode", "Server proof must attest auth mode offline.");
        if (!string.Equals(proof.ServerEndpoint, policy.ServerEndpoint, StringComparison.OrdinalIgnoreCase))
            return SafetyCheck.Fail("proof.endpoint", "Server proof endpoint does not match the immutable session policy.");
        if (!string.Equals(proof.ObserverNonce, policy.Nonce, StringComparison.Ordinal))
            return SafetyCheck.Fail("proof.nonce", "Observer nonce does not match the session nonce.");
        if (proof.ServerSha256.Length != 64 || !proof.ServerSha256.All(Uri.IsHexDigit))
            return SafetyCheck.Fail("proof.server_hash", "Server SHA-256 must be 64 hexadecimal characters.");
        var boundary = proof.Kind switch
        {
            OfflineProofKind.DockerDedicated => ValidateDockerProof(proof),
            OfflineProofKind.LauncherOwnedSingleplayer => ValidateLauncherProof(proof, leasedClient),
            _ => SafetyCheck.Fail("proof.kind", "Offline proof kind is not supported.")
        };
        if (!boundary.Safe) return boundary;
        if (DateTimeOffset.UtcNow - proof.ObservedAtUtc > TimeSpan.FromSeconds(10) || proof.ObservedAtUtc > DateTimeOffset.UtcNow.AddSeconds(2))
            return SafetyCheck.Fail("proof.stale", "Offline proof is stale or has an invalid future timestamp.");
        if (!capabilities.PhysicalInput)
            return SafetyCheck.Fail("capability.input", "The Windows worker requires physical input capability.");
        if (policy.AssistanceMode != AssistanceMode.WhiteBox && (capabilities.Teleport || capabilities.DirectDamage || capabilities.FaultInjection))
            return SafetyCheck.Fail("capability.mode", "Mutating assistance capabilities are permitted only in white-box evidence mode.");
        if (policy.AssistanceMode == AssistanceMode.BlackBox && capabilities.CombatReflex)
            return SafetyCheck.Fail("capability.black_box", "Combat reflex cannot be enabled in black-box evidence mode.");
        return SafetyCheck.Pass();
    }

    private static SafetyCheck ValidateDockerProof(OfflineServerProof proof)
    {
        if (proof.Launcher is not null)
            return SafetyCheck.Fail("proof.boundary_mixed", "Docker proof cannot contain launcher evidence.");
        if (string.IsNullOrWhiteSpace(proof.DockerProject) || string.IsNullOrWhiteSpace(proof.DockerContainer) ||
            string.IsNullOrWhiteSpace(proof.DockerNetwork))
            return SafetyCheck.Fail("proof.docker_identity", "Docker project, container, and network identity are mandatory.");
        return SafetyCheck.Pass();
    }

    private static SafetyCheck ValidateLauncherProof(OfflineServerProof proof, ProcessIdentity? leasedClient)
    {
        if (!string.IsNullOrEmpty(proof.DockerProject) || !string.IsNullOrEmpty(proof.DockerContainer) ||
            !string.IsNullOrEmpty(proof.DockerNetwork))
            return SafetyCheck.Fail("proof.boundary_mixed", "Launcher proof cannot claim Docker identity.");
        var evidence = proof.Launcher;
        if (evidence is null) return SafetyCheck.Fail("proof.launcher_missing", "Launcher proof evidence is mandatory.");
        if (!Guid.TryParse(evidence.EvidenceId, out var evidenceId) || evidenceId == Guid.Empty ||
            !Guid.TryParse(evidence.WorldId, out var worldId) || worldId == Guid.Empty)
            return SafetyCheck.Fail("proof.launcher_identity", "Launcher evidence and world ids must be non-empty UUIDs.");
        if (!evidence.ProcessTreeVerified || !evidence.LoopbackOnlyVerified || !evidence.ObserverBoundaryVerified)
            return SafetyCheck.Fail("proof.launcher_predicate", "Process tree, loopback, and observer boundary predicates must all be verified.");
        string[] hashes = [evidence.WorldPathSha256, evidence.WorldManifestSha256,
            evidence.Launcher.Sha256, evidence.Client.Sha256, evidence.Server.Sha256,
            evidence.ServerArtifactSha256, evidence.AssetsSha256, evidence.LaunchEvidenceSha256];
        if (hashes.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            return SafetyCheck.Fail("proof.launcher_hash", "Every launcher proof hash must be 64 hexadecimal characters.");
        if (evidence.Launcher.ProcessId <= 0 || evidence.Client.ProcessId <= 0 || evidence.Server.ProcessId <= 0 ||
            evidence.Client.ParentProcessId != evidence.Launcher.ProcessId ||
            evidence.Server.ParentProcessId != evidence.Client.ProcessId ||
            evidence.Launcher.ProcessId == evidence.Client.ProcessId || evidence.Client.ProcessId == evidence.Server.ProcessId)
            return SafetyCheck.Fail("proof.launcher_tree", "Launcher, client, and server must be a pinned launcher-to-client-to-server process tree.");
        if (leasedClient is null)
            return SafetyCheck.Fail("proof.client_lease", "Launcher proof requires a pinned client lease identity.");
        if (leasedClient.ProcessId != evidence.Client.ProcessId ||
            leasedClient.CreationTimeUtc != evidence.Client.CreationTimeUtc ||
            !PathEquals(leasedClient.ExecutablePath, evidence.Client.ExecutablePath) ||
            !string.Equals(leasedClient.Sha256, evidence.Client.Sha256, StringComparison.OrdinalIgnoreCase))
            return SafetyCheck.Fail("proof.client_lease", "Launcher evidence does not match the exact leased client identity.");
        return SafetyCheck.Pass();
    }

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static bool TryParseLoopbackEndpoint(string value)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"udp://{value}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Port is > 0 and <= 65535
            && IPAddress.TryParse(uri.Host, out var address)
            && IPAddress.IsLoopback(address);
    }
}

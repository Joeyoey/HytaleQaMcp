using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Orchestrator;

public sealed class WorkerRpcException(int code, string message, object? data = null)
    : InvalidOperationException(Detail(message, data))
{
    public int Code { get; } = code;
    public object? RpcData { get; } = data;

    private static string Detail(string message, object? data) => data switch
    {
        JsonElement element when element.ValueKind != JsonValueKind.Null => $"{message}: {element.GetRawText()}",
        null => message,
        _ => $"{message}: {JsonSerializer.Serialize(data)}"
    };
}

public interface IWorkerRpcClient : IAsyncDisposable
{
    bool IsAlive { get; }
    Task<T> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken);
    Task TerminateAsync(CancellationToken cancellationToken);
}

public interface IWorkerRpcClientFactory
{
    Task<IWorkerRpcClient> StartAsync(string tracePath, string artifactRoot, CancellationToken cancellationToken);
}

public sealed class WorkerRpcProcessClientFactory(QaPaths paths) : IWorkerRpcClientFactory
{
    public Task<IWorkerRpcClient> StartAsync(string tracePath, string artifactRoot, CancellationToken cancellationToken) =>
        WorkerRpcProcessClient.StartAsync(paths.WorkerExecutablePath, tracePath, artifactRoot,
            paths.FfmpegAllowlistPath, cancellationToken);
}

public sealed class WorkerRpcProcessClient : IWorkerRpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly Process process;
    private readonly SemaphoreSlim callGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task stderrDrain;
    private readonly Queue<string> stderrTail = new();
    private readonly object stderrSync = new();
    private long nextId;
    private int terminated;

    private WorkerRpcProcessClient(Process process)
    {
        this.process = process;
        stderrDrain = Task.Run(DrainStderrAsync);
    }

    public bool IsAlive
    {
        get
        {
            try { return Volatile.Read(ref terminated) == 0 && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static Task<IWorkerRpcClient> StartAsync(string executablePath, string tracePath, string artifactRoot,
        string ffmpegAllowlistPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(executable), "Hytale.Qa.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Worker executable must be Hytale.Qa.Worker.exe.");
        if (!File.Exists(executable)) throw new FileNotFoundException("QA worker executable is missing.", executable);
        var trace = Path.GetFullPath(tracePath);
        Directory.CreateDirectory(Path.GetDirectoryName(trace)
            ?? throw new InvalidOperationException("Worker trace path has no parent directory."));

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--trace");
        start.ArgumentList.Add(trace);
        start.ArgumentList.Add("--artifact-root");
        start.ArgumentList.Add(Path.GetFullPath(artifactRoot));
        start.ArgumentList.Add("--ffmpeg-allowlist");
        start.ArgumentList.Add(Path.GetFullPath(ffmpegAllowlistPath));
        var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the QA worker.");
        return Task.FromResult<IWorkerRpcClient>(new WorkerRpcProcessClient(process));
    }

    public async Task<T> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("RPC method is required.", nameof(method));
        await callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAlive) throw new InvalidOperationException($"QA worker is not alive. {StderrSummary()}");
            var id = Interlocked.Increment(ref nextId);
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, JsonOptions);
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? responseLine;
            try { responseLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if (responseLine is null)
                throw new EndOfStreamException($"QA worker closed stdout. {StderrSummary()}");
            RpcResponse response;
            try
            {
                response = JsonSerializer.Deserialize<RpcResponse>(responseLine, JsonOptions)
                    ?? throw new InvalidDataException("QA worker returned an empty response.");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException("QA worker returned invalid JSON.", failure);
            }
            if (response.Id is not JsonElement responseId || !responseId.TryGetInt64(out var actualId) || actualId != id)
                throw new InvalidDataException("QA worker response id did not match the request.");
            if (response.Error is not null)
                throw new WorkerRpcException(response.Error.Code, response.Error.Message, response.Error.Data);
            if (response.Result is null)
            {
                if (default(T) is null) return default!;
                throw new InvalidDataException($"QA worker method {method} returned no result.");
            }
            var element = response.Result is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(response.Result, JsonOptions);
            return element.Deserialize<T>(JsonOptions)
                ?? throw new InvalidDataException($"QA worker method {method} returned an invalid result.");
        }
        finally { callGate.Release(); }
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref terminated, 1) != 0) return;
        lifetime.Cancel();
        try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        try
        {
            using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            graceful.CancelAfter(TimeSpan.FromMilliseconds(500));
            await process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }
        try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false);
                if (line is null) return;
                lock (stderrSync)
                {
                    stderrTail.Enqueue(line);
                    while (stderrTail.Count > 16) stderrTail.Dequeue();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private string StderrSummary()
    {
        lock (stderrSync) return stderrTail.Count == 0 ? "No stderr diagnostics." : string.Join(" | ", stderrTail);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        lifetime.Dispose();
        callGate.Dispose();
        process.Dispose();
        GC.SuppressFinalize(this);
    }
}

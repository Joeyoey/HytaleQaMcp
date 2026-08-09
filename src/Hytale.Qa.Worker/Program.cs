using Hytale.Qa.Windows;
using Hytale.Qa.Worker;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Hytale QA Worker requires Windows.");
    return 2;
}

var tracePath = args.SkipWhile(arg => arg != "--trace").Skip(1).FirstOrDefault();
ITraceSink trace = tracePath is null ? new NullTraceSink() : new HashChainedNdjsonTraceSink(tracePath);
IWindowCaptureBackend screenCapture = WindowsGraphicsCaptureBackend.IsSupported()
    ? new WindowsGraphicsCaptureBackend()
    : new VisibleWindowGdiCaptureBackend();
var artifactRoot = args.SkipWhile(arg => arg != "--artifact-root").Skip(1).FirstOrDefault()
    ?? throw new InvalidOperationException("Worker artifact root is required.");
IRollingVideoEncoder? rollingEncoder = null;
var ffmpegAllowlistPath = args.SkipWhile(arg => arg != "--ffmpeg-allowlist").Skip(1).FirstOrDefault();
if (!string.IsNullOrWhiteSpace(ffmpegAllowlistPath) && File.Exists(ffmpegAllowlistPath))
{
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ffmpegAllowlistPath));
    var config = document.RootElement;
    if (config.GetProperty("schema").GetString() != "hytale-qa-ffmpeg-allowlist-v1")
        throw new InvalidDataException("Trusted FFmpeg allowlist schema is invalid.");
    rollingEncoder = new AllowlistedFfmpegEncoder(new(
        config.GetProperty("executablePath").GetString() ?? "",
        config.GetProperty("sha256").GetString() ?? ""));
}
using var worker = new WorkerHost(
    new WindowsProcessInspector(),
    new SendInputBackend(),
    screenCapture,
    new ProcessLoopbackAudioCaptureBackend(),
    trace,
    artifactRoot,
    rollingEncoder);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
try
{
    await worker.RunAsync(Console.In, Console.Out, shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}

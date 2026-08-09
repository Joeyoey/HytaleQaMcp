using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Windows;

public interface ITraceSink : IDisposable
{
    TraceRecord Append(string sessionId, string category, string action, string outcome, string correlationId, object? data);
}

public sealed class HashChainedNdjsonTraceSink : ITraceSink
{
    private readonly StreamWriter writer;
    private readonly object sync = new();
    private long sequence;
    private string previousHash = new('0', 64);

    public HashChainedNdjsonTraceSink(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        { AutoFlush = true };
    }

    public TraceRecord Append(string sessionId, string category, string action, string outcome, string correlationId, object? data)
    {
        lock (sync)
        {
            var nextSequence = ++sequence;
            var timestamp = DateTimeOffset.UtcNow;
            var element = JsonSerializer.SerializeToElement(data ?? new { });
            var canonical = JsonSerializer.Serialize(new
            {
                sequence = nextSequence,
                timestampUtc = timestamp,
                sessionId,
                category,
                action,
                outcome,
                correlationId,
                data = element,
                previousHash
            });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var record = new TraceRecord(nextSequence, timestamp, sessionId, category, action, outcome, correlationId, element, previousHash, hash);
            writer.WriteLine(JsonSerializer.Serialize(record));
            previousHash = hash;
            return record;
        }
    }

    public void Dispose() => writer.Dispose();
}

public sealed class NullTraceSink : ITraceSink
{
    public TraceRecord Append(string sessionId, string category, string action, string outcome, string correlationId, object? data) =>
        new(0, DateTimeOffset.UtcNow, sessionId, category, action, outcome, correlationId,
            JsonSerializer.SerializeToElement(data ?? new { }), new('0', 64), new('0', 64));
    public void Dispose() { }
}

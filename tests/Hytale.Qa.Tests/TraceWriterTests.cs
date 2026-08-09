using System.Text.Json;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class TraceWriterTests
{
    [Fact]
    public void CreatesVerifiableHashChain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hytale-qa-{Guid.NewGuid():N}.ndjson");
        try
        {
            using (var sink = new HashChainedNdjsonTraceSink(path))
            {
                var first = sink.Append("s", "input", "key", "sent", "a", new { key = 87 });
                var second = sink.Append("s", "input", "key", "sent", "b", new { key = 87 });
                Assert.Equal(first.Hash, second.PreviousHash);
                Assert.NotEqual(first.Hash, second.Hash);
            }
            Assert.Equal(2, File.ReadLines(path).Count());
            foreach (var line in File.ReadLines(path)) Assert.NotNull(JsonDocument.Parse(line));
        }
        finally { File.Delete(path); }
    }
}

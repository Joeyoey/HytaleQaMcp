using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hytale.Qa.Runner;

public sealed record QaEvidenceItem(
    long Sequence,
    DateTimeOffset At,
    string Kind,
    string StepId,
    string PreviousSha256,
    string Sha256,
    JsonElement Payload);

public sealed class QaEvidenceChain
{
    private readonly List<QaEvidenceItem> items = [];
    private string previous = new('0', 64);

    public IReadOnlyList<QaEvidenceItem> Items => items;
    public string RootSha256 => previous;

    public QaEvidenceItem Append(string kind, string stepId, object payload, DateTimeOffset at)
    {
        var element = JsonSerializer.SerializeToElement(payload);
        var canonical = JsonSerializer.Serialize(new { sequence = items.Count + 1, at, kind, stepId, previous, payload = element });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var item = new QaEvidenceItem(items.Count + 1, at, kind, stepId, previous, hash, element);
        items.Add(item);
        previous = hash;
        return item;
    }

    public bool Verify() => Verify(items, out _);

    public static bool Verify(IReadOnlyList<QaEvidenceItem> evidence, out string rootSha256)
    {
        var prior = new string('0', 64);
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (item.Sequence != index + 1 || !IsSha256(item.PreviousSha256) || !IsSha256(item.Sha256) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(prior), Encoding.UTF8.GetBytes(item.PreviousSha256)))
            {
                rootSha256 = prior;
                return false;
            }
            var canonical = JsonSerializer.Serialize(new { sequence = item.Sequence, at = item.At, kind = item.Kind, stepId = item.StepId, previous = prior, payload = item.Payload });
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(item.Sha256)))
            {
                rootSha256 = prior;
                return false;
            }
            prior = actual;
        }
        rootSha256 = prior;
        return true;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

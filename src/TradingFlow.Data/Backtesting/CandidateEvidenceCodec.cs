using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingFlow.Data.Backtesting;

/// <summary>Exact-byte, content-addressed JSON fragments; observations from different closes never share mutable state.</summary>
public static class CandidateEvidenceCodec
{
    public const string EncodingId = "candidate-evidence-json-fragments-brotli-v1";
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const int MinimumFragmentBytes = 512;
    public sealed record Reference(string Encoding, Guid RunId, string Sha256);
    public sealed record Part(string? Literal = null, string? Sha256 = null);
    public sealed record Fragment(int ByteLength, IReadOnlyList<Part> Parts);
    private sealed class SpanNode(int start)
    {
        public int Start { get; } = start;
        public int End { get; set; }
        public List<SpanNode> Children { get; } = [];
    }

    /// <summary>Splits only complete JSON containers, retaining whitespace, numeric spelling and every original byte.</summary>
    public static string Encode(Guid runId, string evidence, Action<Guid, string, string> persist)
    {
        var bytes = Encoding.UTF8.GetBytes(evidence);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Candidate evidence exceeds the exact-byte codec limit.");
        var root = new SpanNode(0) { End = bytes.Length };
        var stack = new Stack<SpanNode>();
        stack.Push(root);
        if (bytes.Length > 0)
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    var node = new SpanNode(checked((int)reader.TokenStartIndex));
                    stack.Push(node);
                }
                else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                {
                    var node = stack.Pop();
                    node.End = checked((int)reader.BytesConsumed);
                    if (node.End - node.Start >= MinimumFragmentBytes) stack.Peek().Children.Add(node);
                }
            }
            if (stack.Count != 1) throw new InvalidDataException("Candidate evidence contains incomplete JSON.");
        }
        // A synthetic root preserves leading/trailing whitespace, but need not reference an identical real root.
        if (root.Children.Count == 1 && root.Children[0].Start == 0 && root.Children[0].End == bytes.Length)
            root = root.Children[0];
        return JsonSerializer.Serialize(new Reference(EncodingId, runId, Store(root, bytes, runId, persist)));
    }

    /// <summary>Stores children before their parent; a parent hash always identifies its fully hydrated original bytes.</summary>
    private static string Store(SpanNode node, byte[] bytes, Guid runId, Action<Guid, string, string> persist)
    {
        var parts = new List<Part>();
        var cursor = node.Start;
        foreach (var child in node.Children.Where(child => child.End - child.Start >= MinimumFragmentBytes))
        {
            if (child.Start > cursor) parts.Add(new(Encoding.UTF8.GetString(bytes, cursor, child.Start - cursor)));
            parts.Add(new(Sha256: Store(child, bytes, runId, persist)));
            cursor = child.End;
        }
        if (cursor < node.End) parts.Add(new(Encoding.UTF8.GetString(bytes, cursor, node.End - cursor)));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(node.Start, node.End - node.Start)));
        persist(runId, hash, JsonSerializer.Serialize(new Fragment(node.End - node.Start, parts)));
        return hash;
    }

    /// <summary>Requires an explicit encoding and owner; missing, corrupt, cyclic or oversized fragments fail closed.</summary>
    public static string Hydrate(Guid runId, string referenceJson, Func<Guid, string, string> load)
    {
        var reference = JsonSerializer.Deserialize<Reference>(referenceJson)
            ?? throw new InvalidDataException("Candidate evidence reference is missing.");
        if (reference.Encoding != EncodingId || reference.RunId != runId)
            throw new InvalidDataException("Candidate evidence encoding or run identity differs.");
        var fragment = ReadFragment(runId, reference.Sha256, load);
        var bytes = new byte[fragment.ByteLength];
        var visits = 0;
        Fill(runId, reference.Sha256, fragment, load, bytes, new HashSet<string>(StringComparer.Ordinal), 0, ref visits);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Validates a bounded recipe before allocating output; the only output buffer belongs to the root observation.</summary>
    private static Fragment ReadFragment(Guid runId, string hash, Func<Guid, string, string> load)
    {
        if (String.IsNullOrEmpty(hash) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Candidate evidence reference hash is invalid.");
        var recipe = load(runId, hash);
        if (recipe.Length > MaximumBytes * 6L) throw new InvalidDataException("Candidate fragment recipe exceeds bounds.");
        var fragment = JsonSerializer.Deserialize<Fragment>(recipe)
            ?? throw new InvalidDataException("Candidate evidence fragment is missing.");
        if (fragment.ByteLength < 0 || fragment.ByteLength > MaximumBytes || fragment.Parts is null)
            throw new InvalidDataException("Candidate evidence fragment length is invalid.");
        return fragment;
    }

    /// <summary>Expands into a slice of one root-sized buffer. Active-path and visit limits reject cycles before another load.</summary>
    private static int Fill(Guid runId, string hash, Fragment fragment, Func<Guid, string, string> load,
        Span<byte> output, HashSet<string> active, int depth, ref int visits)
    {
        if (depth > 65 || ++visits > 65_536 || !active.Add(hash))
            throw new InvalidDataException("Candidate evidence reference is cyclic or exceeds its complexity limit.");
        if (fragment.ByteLength > output.Length || (depth > 0 && fragment.ByteLength < MinimumFragmentBytes))
            throw new InvalidDataException("Candidate fragment expands beyond the remaining parent observation.");
        long futureLiteralBytes = 0;
        foreach (var part in fragment.Parts)
        {
            if (part is null || (part.Literal is null) == (part.Sha256 is null)) throw new InvalidDataException("Candidate fragment part is ambiguous.");
            if (part.Literal is not null) futureLiteralBytes += Encoding.UTF8.GetByteCount(part.Literal);
        }
        if (futureLiteralBytes > fragment.ByteLength) throw new InvalidDataException("Candidate fragment literals exceed its declared length.");
        var written = 0;
        foreach (var part in fragment.Parts)
        {
            if (part.Literal is not null)
            {
                var count = Encoding.UTF8.GetBytes(part.Literal, output[written..]);
                written += count;
                futureLiteralBytes -= count;
            }
            else
            {
                if (active.Contains(part.Sha256!)) throw new InvalidDataException("Candidate evidence reference is cyclic.");
                var child = ReadFragment(runId, part.Sha256!, load);
                var remaining = checked(fragment.ByteLength - written - (int)futureLiteralBytes);
                written += Fill(runId, part.Sha256!, child, load, output.Slice(written, remaining), active, depth + 1, ref visits);
            }
        }
        if (written != fragment.ByteLength || Convert.ToHexStringLower(SHA256.HashData(output[..written])) != hash)
            throw new InvalidDataException("Candidate evidence fragment does not match its original bytes.");
        active.Remove(hash);
        return written;
    }
}

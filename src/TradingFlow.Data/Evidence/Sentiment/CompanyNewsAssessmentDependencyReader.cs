using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;

namespace TradingFlow.Data.Evidence.Sentiment;

/// <summary>Discovers declarations for lease acquisition only; it never verifies assessment rows or source semantics.</summary>
public static class CompanyNewsAssessmentDependencyReader
{
    /// <summary>Hashes one bound packet and reads only dependency strings; complete replay remains mandatory under the resulting lease.</summary>
    public static ImmutableArray<CompanyNewsAssessmentDependency> ReadDeclarations(string researchRoot, string path,
        string expectedSha256)
    {
        var full = HistoricalEvidenceManifestStore.BoundPath(researchRoot,
            Path.IsPathFullyQualified(path) ? Path.GetRelativePath(researchRoot, path) : path);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!HistoricalEvidenceManifestStore.IsHash(expectedSha256) ||
            Convert.ToHexStringLower(SHA256.HashData(stream)) != expectedSha256)
            throw new InvalidDataException("Company assessment declaration packet differs from its frozen SHA256.");
        stream.Position = 0;
        var declarations = ReadDependencyTokens(stream);
        var result = ImmutableArray.CreateBuilder<CompanyNewsAssessmentDependency>();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Canonicalize every declaration now so an escaping or contradictory graph cannot acquire a lease.
        void Add(string declaredPath, string hash)
        {
            if (!HistoricalEvidenceManifestStore.IsHash(hash))
                throw new InvalidDataException("Invalid assessment dependency declaration hash.");
            var bound = HistoricalEvidenceManifestStore.BoundPath(researchRoot,
                Path.IsPathFullyQualified(declaredPath) ? Path.GetRelativePath(researchRoot, declaredPath) : declaredPath);
            if (paths.TryGetValue(bound, out var existing))
            {
                if (existing != hash) throw new InvalidDataException("Assessment dependency declarations contain conflicting hashes.");
                return;
            }
            paths.Add(bound, hash);
            result.Add(new(Path.GetRelativePath(researchRoot, bound), hash));
        }

        Add(full, expectedSha256);
        foreach (var dependency in declarations) Add(dependency.Path, dependency.Sha256);
        return result.ToImmutable();
    }

    /// <summary>Advances token by token through rows without creating row objects; partial tokens alone may grow the read buffer.</summary>
    private static ImmutableArray<CompanyNewsAssessmentDependency> ReadDependencyTokens(Stream stream)
    {
        var declarations = new DependencyDeclarations();
        var buffer = new byte[16 * 1024];
        var retained = 0;
        var state = new JsonReaderState();
        while (true)
        {
            if (retained == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
            var read = stream.Read(buffer, retained, buffer.Length - retained);
            var final = read == 0;
            var available = retained + read;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, available), final, state);
            while (reader.Read()) declarations.ReadToken(ref reader);
            var consumed = checked((int)reader.BytesConsumed);
            retained = available - consumed;
            buffer.AsSpan(consumed, retained).CopyTo(buffer);
            state = reader.CurrentState;
            if (final) break;
        }
        return declarations.Complete();
    }

    private sealed class DependencyDeclarations
    {
        private readonly ImmutableArray<CompanyNewsAssessmentDependency>.Builder dependencies = ImmutableArray.CreateBuilder<CompanyNewsAssessmentDependency>();
        private bool declarationsSeen;
        private bool readingDependencies;
        private string? rootProperty;
        private string? dependencyProperty;
        private string? dependencyPath;
        private string? dependencyHash;

        /// <summary>Locates only the root declaration array; row values never become strings or objects in memory.</summary>
        public void ReadToken(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                rootProperty = reader.GetString();
            else if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.StartArray &&
                String.Equals(rootProperty, "Dependencies", StringComparison.OrdinalIgnoreCase))
            {
                if (declarationsSeen) throw new InvalidDataException("Repeated assessment dependency declarations are not allowed.");
                declarationsSeen = readingDependencies = true;
            }
            else if (readingDependencies) ReadDependencyToken(ref reader);
        }

        /// <summary>Collects path/hash members at their exact declaration depth, rejecting incomplete dependency objects.</summary>
        private void ReadDependencyToken(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2)
                dependencyPath = dependencyHash = dependencyProperty = null;
            else if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 3)
                dependencyProperty = reader.GetString();
            else if (reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 3)
            {
                if (String.Equals(dependencyProperty, "Path", StringComparison.OrdinalIgnoreCase)) dependencyPath = reader.GetString();
                else if (String.Equals(dependencyProperty, "Sha256", StringComparison.OrdinalIgnoreCase)) dependencyHash = reader.GetString();
            }
            else if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 2)
            {
                if (String.IsNullOrWhiteSpace(dependencyPath) || dependencyHash is null)
                    throw new InvalidDataException("Incomplete assessment dependency declaration.");
                dependencies.Add(new(dependencyPath, dependencyHash));
            }
            else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
                readingDependencies = false;
        }

        /// <summary>Returns declaration metadata only after the complete JSON document has been consumed.</summary>
        public ImmutableArray<CompanyNewsAssessmentDependency> Complete()
        {
            if (!declarationsSeen || readingDependencies || dependencies.Count == 0)
                throw new InvalidDataException("Assessment packet has no complete dependency declarations.");
            return dependencies.ToImmutable();
        }
    }
}

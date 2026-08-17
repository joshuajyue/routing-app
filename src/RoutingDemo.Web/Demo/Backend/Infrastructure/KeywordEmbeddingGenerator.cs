using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend.Infrastructure;

internal sealed partial class KeywordEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 256;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = [];
        foreach (string value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings.Add(new Embedding<float>(Embed(value)));
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (Match match in WordPattern().Matches(text))
        {
            string word = Stem(match.Value.ToLowerInvariant());
            if (word.Length < 2)
            {
                continue;
            }

            int hash = StableHash(word);
            vector[(hash & int.MaxValue) % Dimensions] += 1f;
            vector[((hash * 31) & int.MaxValue) % Dimensions] += 0.5f;
        }

        float magnitude = MathF.Sqrt(vector.Sum(component => component * component));
        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }

        return vector;
    }

    private static string Stem(string word)
    {
        foreach (string suffix in (string[])["ing", "ed", "es", "s"])
        {
            if (word.Length > suffix.Length + 2 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in value)
            {
                hash = (hash ^ character) * 16777619;
            }

            return (int)hash;
        }
    }

    [GeneratedRegex(@"[a-zA-Z0-9#+']+")]
    private static partial Regex WordPattern();
}

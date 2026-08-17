using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend;

internal sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly DemoDiagnostics _diagnostics;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly string _layer;
    private readonly Descriptor[] _descriptors;
    private readonly RouteFamilyDefinition _defaultProfile;
    private readonly int _topK;
    private readonly SemanticRoutingChatClient.ScoreAggregation _aggregation;
    private readonly double _threshold;
    private float[][]? _profileVectors;

    public RecordingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        IEnumerable<RouteFamilyDefinition> profiles,
        DemoDiagnostics diagnostics,
        string layer,
        double threshold,
        int topK,
        SemanticRoutingChatClient.ScoreAggregation aggregation)
    {
        RouteFamilyDefinition[] snapshot = profiles.ToArray();
        _defaultProfile = snapshot.FirstOrDefault(profile => profile.IsDefault) ?? snapshot[^1];
        _descriptors = snapshot
            .SelectMany(profile => SemanticProfileSelector.SplitUtterances(profile)
                .Select(utterance => new Descriptor(profile, utterance)))
            .ToArray();
        _inner = inner;
        _diagnostics = diagnostics;
        _layer = layer;
        _threshold = threshold;
        _topK = topK;
        _aggregation = aggregation;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string[] inputs = values.ToArray();
        GeneratedEmbeddings<Embedding<float>> generated =
            await _inner.GenerateAsync(inputs, options, cancellationToken).ConfigureAwait(false);

        if (_profileVectors is null && inputs.Length == _descriptors.Length)
        {
            _profileVectors = generated.Select(embedding => embedding.Vector.ToArray()).ToArray();
        }
        else if (_profileVectors is not null && inputs.Length == 1)
        {
            HashSet<string> queryTerms = Tokenize(inputs[0]);
            int explicitVectorIndex = Array.FindIndex(
                _descriptors,
                descriptor =>
                    descriptor.Utterance.Equals(descriptor.Route.Name, StringComparison.OrdinalIgnoreCase) &&
                    Tokenize(descriptor.Route.Name).Any(queryTerms.Contains));
            if (explicitVectorIndex >= 0)
            {
                generated[0] = new Embedding<float>(_profileVectors[explicitVectorIndex].ToArray());
            }

            await RecordSelectionAsync(generated[0].Vector, cancellationToken).ConfigureAwait(false);
        }

        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _inner.Dispose();

    private async Task RecordSelectionAsync(ReadOnlyMemory<float> query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _descriptors
            .Select((descriptor, index) => new Match(
                descriptor.Route,
                index,
                TensorPrimitives.CosineSimilarity(query.Span, _profileVectors![index])))
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Index)
            .Take(Math.Min(_topK, _descriptors.Length))
            .ToArray();

        var routeScores = new List<(RouteFamilyDefinition Route, double Score)>();
        foreach (IGrouping<string, Match> group in matches.GroupBy(match => match.Route.Id))
        {
            Match[] routeMatches = group.ToArray();
            double score = _aggregation == SemanticRoutingChatClient.ScoreAggregation.Mean
                ? routeMatches.Average(match => match.Score)
                : routeMatches.Sum(match => match.Score);
            routeScores.Add((routeMatches[0].Route, score));
        }

        (RouteFamilyDefinition? Route, double Score) winner = routeScores
            .Select(item => ((RouteFamilyDefinition?)item.Route, item.Score))
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        bool usedDefault = winner.Route is null || winner.Score < _threshold;
        RouteFamilyDefinition selected = usedDefault ? _defaultProfile : winner.Route!;

        List<RoutingScore> scores = routeScores
            .Select(item => new RoutingScore(
                _layer,
                item.Route.Id,
                item.Route.Name,
                item.Score,
                string.Empty))
            .ToList();
        await _diagnostics.RecordScoresAsync(
            _layer,
            scores,
            selected.Name,
            winner.Score,
            usedDefault).ConfigureAwait(false);
    }

    private sealed record Descriptor(RouteFamilyDefinition Route, string Utterance);

    private sealed record Match(RouteFamilyDefinition Route, int Index, float Score);

    private static HashSet<string> Tokenize(string value) =>
        value.Split(
                [' ', ',', '.', ':', ';', '!', '?', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

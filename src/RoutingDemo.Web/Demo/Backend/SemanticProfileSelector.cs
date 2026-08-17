using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend;

internal sealed class SemanticProfileSelector : IDisposable
{
    private readonly DemoDiagnostics _diagnostics;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly string _layer;
    private readonly Profile[] _profiles;
    private readonly RouteFamilyDefinition _defaultProfile;
    private readonly int _topK;
    private readonly SemanticRoutingChatClient.ScoreAggregation _aggregation;
    private readonly double _threshold;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private float[][]? _profileVectors;

    public SemanticProfileSelector(
        IEnumerable<RouteFamilyDefinition> profiles,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        DemoDiagnostics diagnostics,
        string layer,
        double threshold,
        int topK,
        SemanticRoutingChatClient.ScoreAggregation aggregation)
    {
        RouteFamilyDefinition[] snapshot = profiles.ToArray();
        _defaultProfile = snapshot.FirstOrDefault(profile => profile.IsDefault) ?? snapshot[^1];
        _profiles = snapshot
            .Select(profile => new Profile(profile, SplitUtterances(profile)))
            .ToArray();
        _embeddings = embeddings;
        _diagnostics = diagnostics;
        _layer = layer;
        _threshold = threshold;
        _topK = topK;
        _aggregation = aggregation;
    }

    public async ValueTask<RouteFamilyDefinition> SelectAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        string query = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        HashSet<string> queryTerms = Tokenize(query);
        RouteFamilyDefinition? explicitProfile = _profiles
            .Select(profile => profile.Route)
            .FirstOrDefault(profile => Tokenize(profile.Name).Any(queryTerms.Contains));

        await EnsureProfileVectorsAsync(cancellationToken).ConfigureAwait(false);
        GeneratedEmbeddings<Embedding<float>> generated =
            await _embeddings.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<float> queryVector = generated[0].Vector.Span;

        var matches = new List<Match>();
        int vectorIndex = 0;
        foreach (Profile profile in _profiles)
        {
            foreach (string _ in profile.Utterances)
            {
                matches.Add(new Match(
                    profile.Route,
                    vectorIndex,
                    TensorPrimitives.CosineSimilarity(queryVector, _profileVectors![vectorIndex])));
                vectorIndex++;
            }
        }

        Match[] topMatches = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Index)
            .Take(Math.Min(_topK, matches.Count))
            .ToArray();
        var routeScores = new List<(RouteFamilyDefinition Route, float Score)>();
        foreach (IGrouping<string, Match> group in topMatches.GroupBy(match => match.Route.Id))
        {
            Match[] routeMatches = group.ToArray();
            float score = _aggregation == SemanticRoutingChatClient.ScoreAggregation.Mean
                ? routeMatches.Average(match => match.Score)
                : routeMatches.Sum(match => match.Score);
            routeScores.Add((routeMatches[0].Route, score));
        }

        RouteFamilyDefinition selected;
        float selectedScore;
        if (explicitProfile is not null)
        {
            selected = explicitProfile;
            selectedScore = 1;
        }
        else
        {
            (RouteFamilyDefinition Route, float Score) winner = routeScores
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            selected = winner.Route is not null && winner.Score >= _threshold
                ? winner.Route
                : _defaultProfile;
            selectedScore = winner.Score;
        }

        List<RoutingScore> scores = routeScores
            .Select(item => new RoutingScore(
                _layer,
                item.Route.Id,
                item.Route.Name,
                ReferenceEquals(item.Route, explicitProfile) ? 1 : item.Score,
                ReferenceEquals(item.Route, explicitProfile) ? $"explicit: {item.Route.Name}" : string.Empty))
            .ToList();
        await _diagnostics.RecordScoresAsync(
            _layer,
            scores,
            selected.Name,
            selectedScore,
            ReferenceEquals(selected, _defaultProfile) && explicitProfile is null).ConfigureAwait(false);

        if (explicitProfile is not null)
        {
            await _diagnostics.AddEventAsync(
                "Selection",
                "Demo keyword matched",
                $"{_layer}: '{explicitProfile.Name}' selected by explicit keyword. Configured profile order breaks ties.")
                .ConfigureAwait(false);
        }

        return selected;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _embeddings.Dispose();
    }

    internal static IReadOnlyList<string> SplitUtterances(RouteFamilyDefinition profile) =>
        [profile.Name, .. profile.ProfileExamples
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private async Task EnsureProfileVectorsAsync(CancellationToken cancellationToken)
    {
        if (_profileVectors is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profileVectors is not null)
            {
                return;
            }

            string[] utterances = _profiles.SelectMany(profile => profile.Utterances).ToArray();
            GeneratedEmbeddings<Embedding<float>> generated =
                await _embeddings.GenerateAsync(utterances, cancellationToken: cancellationToken).ConfigureAwait(false);
            _profileVectors = generated.Select(embedding => embedding.Vector.ToArray()).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HashSet<string> Tokenize(string value) =>
        value.Split(
                [' ', ',', '.', ':', ';', '!', '?', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record Profile(RouteFamilyDefinition Route, IReadOnlyList<string> Utterances);

    private sealed record Match(RouteFamilyDefinition Route, int Index, float Score);
}

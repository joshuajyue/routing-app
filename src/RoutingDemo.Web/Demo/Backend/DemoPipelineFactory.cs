using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace RoutingDemo.Web.Demo.Backend;

public sealed class DemoPipelineFactory
{
    private readonly IDistributedCache _cache;
    private readonly MockResponsesApi _responses;

    public DemoPipelineFactory(IDistributedCache cache, MockResponsesApi responses)
    {
        _cache = cache;
        _responses = responses;
    }

    public DemoPipelineRuntime Create(PipelineConfiguration configuration, string sessionId)
    {
        var diagnostics = new DemoDiagnostics();
        var builder = new PipelineBuilder(configuration, _cache, _responses, diagnostics);
        IChatClient root = builder.Build();
        return new DemoPipelineRuntime(
            root,
            configuration,
            sessionId,
            diagnostics,
            builder.StickyClient);
    }

    private sealed class PipelineBuilder
    {
        private readonly IDistributedCache _cache;
        private readonly PipelineConfiguration _configuration;
        private readonly DemoDiagnostics _diagnostics;
        private readonly MockResponsesApi _responses;

        public PipelineBuilder(
            PipelineConfiguration configuration,
            IDistributedCache cache,
            MockResponsesApi responses,
            DemoDiagnostics diagnostics)
        {
            _configuration = configuration;
            _cache = cache;
            _responses = responses;
            _diagnostics = diagnostics;
        }

        public StickySemanticRoutingChatClient? StickyClient { get; private set; }

        public IChatClient Build()
        {
            IChatClient selectedClient = _configuration.SelectionPolicy switch
            {
                "StickySemantic" => BuildStickySelector(),
                "Semantic" => BuildSemanticSelector(
                    _configuration.Families,
                    _configuration.ScoreThreshold,
                    _configuration.TopK,
                    _configuration.ScoreAggregation,
                    "outer"),
                _ => BuildFamily(_configuration.Families[0], _configuration.Families[0].Name),
            };

            if (!_configuration.GlobalFallbackEnabled)
            {
                return selectedClient;
            }

            IChatClient emergency = BuildLeaf(_configuration.GlobalFallback, "Outer fallback");
            return new OrderedFailoverChatClient([selectedClient, emergency]);
        }

        private IChatClient BuildStickySelector()
        {
            var clients = new Dictionary<string, IChatClient>(StringComparer.Ordinal);
            var definitions = new Dictionary<string, RouteFamilyDefinition>(StringComparer.Ordinal);
            foreach (RouteFamilyDefinition family in _configuration.Families)
            {
                clients[family.Name] = BuildFamily(family, family.Name);
                definitions[family.Name] = family;
            }

            var selector = new SemanticProfileSelector(
                _configuration.Families,
                new KeywordEmbeddingGenerator(),
                _diagnostics,
                "outer",
                _configuration.ScoreThreshold,
                _configuration.TopK,
                _configuration.ScoreAggregation == "Sum"
                    ? SemanticRoutingChatClient.ScoreAggregation.Sum
                    : SemanticRoutingChatClient.ScoreAggregation.Mean);
            StickyClient = new StickySemanticRoutingChatClient(
                clients,
                definitions,
                selector,
                _cache,
                _diagnostics);
            return StickyClient;
        }

        private IChatClient BuildFamily(RouteFamilyDefinition family, string layer)
        {
            if (family.SemanticRouter is { } semanticRouter)
            {
                return BuildSemanticSelector(
                    semanticRouter.Profiles,
                    semanticRouter.ScoreThreshold,
                    semanticRouter.TopK,
                    semanticRouter.ScoreAggregation,
                    family.Name);
            }

            IChatClient[] clients = family.Routes
                .Select(route => BuildLeaf(route, $"{layer} / {GetResilienceName(family.ResiliencePolicy)}"))
                .ToArray();
            return family.ResiliencePolicy switch
            {
                "Ordered" => new OrderedFailoverChatClient(clients)
                {
                    MaximumAttemptsPerRequest = family.MaximumAttempts,
                },
                "Cooldown" => new CooldownFailoverChatClient(
                    clients,
                    family.Routes,
                    TimeSpan.FromSeconds(family.CooldownSeconds),
                    family.MaximumAttempts,
                    _diagnostics),
                _ => clients[0],
            };
        }

        private IChatClient BuildSemanticSelector(
            IReadOnlyList<RouteFamilyDefinition> profiles,
            double threshold,
            int topK,
            string aggregation,
            string layer)
        {
            var profileClients = new Dictionary<IChatClient, IReadOnlyList<string>>();
            IChatClient? defaultClient = null;
            foreach (RouteFamilyDefinition profile in profiles)
            {
                IChatClient client = BuildFamily(profile, $"{layer} / {profile.Name}");
                if (profile.IsDefault)
                {
                    defaultClient = client;
                }

                profileClients[client] = SemanticProfileSelector.SplitUtterances(profile);
            }

            RouteFamilyDefinition defaultProfile =
                profiles.FirstOrDefault(profile => profile.IsDefault) ?? profiles[^1];
            defaultClient ??= BuildFamily(defaultProfile, $"{layer} / {defaultProfile.Name}");
            SemanticRoutingChatClient.ScoreAggregation scoreAggregation =
                aggregation == "Sum"
                    ? SemanticRoutingChatClient.ScoreAggregation.Sum
                    : SemanticRoutingChatClient.ScoreAggregation.Mean;
            var embeddings = new RecordingEmbeddingGenerator(
                new KeywordEmbeddingGenerator(),
                profiles,
                _diagnostics,
                layer,
                threshold,
                topK,
                scoreAggregation);
            return new SemanticRoutingChatClient(
                embeddings,
                profileClients,
                defaultClient,
                (float)threshold,
                topK,
                scoreAggregation);
        }

        private IChatClient BuildLeaf(RouteDefinition route, string layer) =>
            new MockResponsesChatClient(route, layer, _responses, _diagnostics);

        private static string GetResilienceName(string policy) =>
            policy == "None" ? "Single" : policy;
    }
}

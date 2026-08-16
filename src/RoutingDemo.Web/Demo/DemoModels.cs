namespace RoutingDemo.Web.Demo;

public sealed class PipelineConfiguration
{
    public string ScenarioId { get; set; } = "semantic-composition";

    public string ScenarioName { get; set; } = "Semantic route families";

    public string SelectionPolicy { get; set; } = "Semantic";

    public double ScoreThreshold { get; set; } = 0.35;

    public int TopK { get; set; } = 3;

    public string ScoreAggregation { get; set; } = "Mean";

    public bool GlobalFallbackEnabled { get; set; } = true;

    public RouteDefinition GlobalFallback { get; set; } = new()
    {
        Name = "global-emergency",
        Purpose = "Whole-pipeline fallback",
        ModelId = "gpt-4o-mini",
        ReasoningEffort = "low",
        Temperature = 0.2,
        Instructions = "Provide a concise response when the selected route family is unavailable.",
    };

    public bool Streaming { get; set; } = true;

    public List<RouteFamilyDefinition> Families { get; set; } = [];

    public IEnumerable<RouteDefinition> AllRoutes()
    {
        foreach (RouteFamilyDefinition family in Families)
        {
            foreach (RouteDefinition route in family.AllRoutes())
            {
                yield return route;
            }
        }

        if (GlobalFallbackEnabled)
        {
            yield return GlobalFallback;
        }
    }

    public PipelineConfiguration Clone(bool resetRuntimeState = true) =>
        new()
        {
            ScenarioId = ScenarioId,
            ScenarioName = ScenarioName,
            SelectionPolicy = SelectionPolicy,
            ScoreThreshold = ScoreThreshold,
            TopK = TopK,
            ScoreAggregation = ScoreAggregation,
            GlobalFallbackEnabled = GlobalFallbackEnabled,
            GlobalFallback = GlobalFallback.Clone(resetRuntimeState),
            Streaming = Streaming,
            Families = Families.Select(family => family.Clone(resetRuntimeState)).ToList(),
        };
}

public sealed class RouteFamilyDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "route-family";

    public string Purpose { get; set; } = "General";

    public string ProfileExamples { get; set; } = "general questions, everyday help";

    public bool IsDefault { get; set; }

    public string ResiliencePolicy { get; set; } = "None";

    public int MaximumAttempts { get; set; } = 1;

    public int CooldownSeconds { get; set; } = 30;

    public List<RouteDefinition> Routes { get; set; } = [];

    public SemanticRouterDefinition? SemanticRouter { get; set; }

    public IEnumerable<RouteDefinition> AllRoutes() =>
        SemanticRouter is null
            ? Routes
            : SemanticRouter.Profiles.SelectMany(profile => profile.AllRoutes());

    public RouteFamilyDefinition Clone(bool resetRuntimeState = true) =>
        new()
        {
            Id = Id,
            Name = Name,
            Purpose = Purpose,
            ProfileExamples = ProfileExamples,
            IsDefault = IsDefault,
            ResiliencePolicy = ResiliencePolicy,
            MaximumAttempts = MaximumAttempts,
            CooldownSeconds = CooldownSeconds,
            Routes = Routes.Select(route => route.Clone(resetRuntimeState)).ToList(),
            SemanticRouter = SemanticRouter?.Clone(resetRuntimeState),
        };
}

public sealed class SemanticRouterDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "SemanticRoutingChatClient";

    public double ScoreThreshold { get; set; } = 0.35;

    public int TopK { get; set; } = 3;

    public string ScoreAggregation { get; set; } = "Mean";

    public List<RouteFamilyDefinition> Profiles { get; set; } = [];

    public SemanticRouterDefinition Clone(bool resetRuntimeState = true) =>
        new()
        {
            Id = Id,
            Name = Name,
            ScoreThreshold = ScoreThreshold,
            TopK = TopK,
            ScoreAggregation = ScoreAggregation,
            Profiles = Profiles.Select(profile => profile.Clone(resetRuntimeState)).ToList(),
        };
}

public sealed class RouteDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "model";

    public string Purpose { get; set; } = "General";

    public string ModelId { get; set; } = "gpt-5.4";

    public string ReasoningEffort { get; set; } = "medium";

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 1200;

    public string Instructions { get; set; } = "Be concise and helpful.";

    public bool FailNext { get; set; }

    public DateTimeOffset? DownUntil { get; set; }

    public bool DownUntilRevived { get; set; }

    public DateTimeOffset? CooldownUntil { get; set; }

    public bool PolicyDisabled { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? LastFailure { get; set; }

    public RouteDefinition Clone(bool resetRuntimeState = true) =>
        new()
        {
            Id = Id,
            Name = Name,
            Purpose = Purpose,
            ModelId = ModelId,
            ReasoningEffort = ReasoningEffort,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            Instructions = Instructions,
            FailNext = resetRuntimeState ? false : FailNext,
            DownUntil = resetRuntimeState ? null : DownUntil,
            DownUntilRevived = !resetRuntimeState && DownUntilRevived,
            CooldownUntil = resetRuntimeState ? null : CooldownUntil,
            PolicyDisabled = !resetRuntimeState && PolicyDisabled,
            ConsecutiveFailures = resetRuntimeState ? 0 : ConsecutiveFailures,
            LastFailure = resetRuntimeState ? null : LastFailure,
        };
}

public sealed record ScenarioDefinition(
    string Id,
    string Eyebrow,
    string Name,
    string Description,
    string SelectionPolicy,
    string Composition);

public sealed class ChatEntry
{
    public string Role { get; set; } = "assistant";

    public string Content { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public string? ModelId { get; set; }

    public bool IsPending { get; set; }
}

public sealed record RoutingScore(
    string Layer,
    string RouteFamilyId,
    string RouteFamilyName,
    double Score,
    string MatchedTerms);

public sealed record AttemptRecord(
    int Number,
    string Layer,
    string RouteName,
    string ModelId,
    int DurationMs,
    int? TimeToFirstUpdateMs,
    bool ResponseCompleted,
    bool OutputCommitted,
    bool IsTerminal,
    string Status,
    string? Exception);

public sealed record DebugEventRecord(
    DateTimeOffset Timestamp,
    string Kind,
    string Title,
    string Detail);

public sealed class RequestDebugState
{
    public string RequestId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string SelectionPolicy { get; set; } = string.Empty;

    public string ResiliencePolicy { get; set; } = string.Empty;

    public string SelectedFamilyId { get; set; } = string.Empty;

    public string SelectedRoute { get; set; } = "Pending";

    public string SelectedInnerRoute { get; set; } = "Pending";

    public string FinalRouteId { get; set; } = string.Empty;

    public string FinalRoute { get; set; } = "Pending";

    public string ConfiguredModel { get; set; } = "Pending";

    public string ActualModel { get; set; } = "Pending";

    public string Outcome { get; set; } = "Routing";

    public string FinishReason { get; set; } = "Pending";

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public List<RoutingScore> Scores { get; } = [];

    public List<AttemptRecord> Attempts { get; } = [];

    public List<DebugEventRecord> Events { get; } = [];
}

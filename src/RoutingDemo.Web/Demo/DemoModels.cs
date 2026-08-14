namespace RoutingDemo.Web.Demo;

public sealed class PipelineConfiguration
{
    public string ScenarioId { get; set; } = "semantic-failover";

    public string ScenarioName { get; set; } = "Semantic plus failover";

    public string SelectionPolicy { get; set; } = "Semantic";

    public string ResiliencePolicy { get; set; } = "Ordered";

    public double ScoreThreshold { get; set; } = 0.35;

    public int TopK { get; set; } = 3;

    public string ScoreAggregation { get; set; } = "Mean";

    public int MaximumAttempts { get; set; } = 3;

    public int CooldownSeconds { get; set; } = 30;

    public bool Streaming { get; set; } = true;

    public List<RouteDefinition> Routes { get; set; } = [];

    public PipelineConfiguration Clone(bool resetRuntimeState = true) =>
        new()
        {
            ScenarioId = ScenarioId,
            ScenarioName = ScenarioName,
            SelectionPolicy = SelectionPolicy,
            ResiliencePolicy = ResiliencePolicy,
            ScoreThreshold = ScoreThreshold,
            TopK = TopK,
            ScoreAggregation = ScoreAggregation,
            MaximumAttempts = MaximumAttempts,
            CooldownSeconds = CooldownSeconds,
            Streaming = Streaming,
            Routes = Routes.Select(route => route.Clone(resetRuntimeState)).ToList(),
        };
}

public sealed class RouteDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "route";

    public string Purpose { get; set; } = "General";

    public string ModelId { get; set; } = "gpt-5.4";

    public string ReasoningEffort { get; set; } = "medium";

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 1200;

    public string Instructions { get; set; } = "Be concise and helpful.";

    public string ProfileExamples { get; set; } = "general questions, everyday help";

    public bool IsDefault { get; set; }

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
            ProfileExamples = ProfileExamples,
            IsDefault = IsDefault,
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
    string ResiliencePolicy);

public sealed class ChatEntry
{
    public string Role { get; set; } = "assistant";

    public string Content { get; set; } = string.Empty;

    public string? RouteName { get; set; }

    public string? ModelId { get; set; }

    public bool IsPending { get; set; }
}

public sealed record RoutingScore(
    string RouteId,
    string RouteName,
    double Score,
    string MatchedTerms);

public sealed record AttemptRecord(
    int Number,
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

    public string SelectedRoute { get; set; } = "Pending";

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

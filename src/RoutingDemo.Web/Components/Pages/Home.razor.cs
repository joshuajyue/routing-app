using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using RoutingDemo.Web.Demo;

namespace RoutingDemo.Web.Components.Pages;

public partial class Home
{
    private static readonly (string Title, string Subtitle)[] BuilderSteps =
    [
        ("Scenario", "Choose a starting point"),
        ("Policies", "Compose routing behavior"),
        ("Routes", "Configure model clients"),
        ("Review", "Build the pipeline"),
    ];

    private static readonly ScenarioDefinition[] Scenarios =
    [
        new(
            "semantic-failover",
            "Best full demo",
            "Semantic plus failover",
            "Route by intent, then move to a fallback when the selected specialist is unavailable.",
            "Semantic",
            "Ordered failover"),
        new(
            "ordered",
            "Built-in resilience",
            "Ordered failover",
            "Walk a fixed list of model clients and expose the pre-output retry boundary.",
            "Direct",
            "Ordered failover"),
        new(
            "cooldown",
            "Custom policy",
            "Cooldown failover",
            "Remember failures across requests, skip cooling routes, and probe them after recovery.",
            "Direct",
            "Cooldown"),
        new(
            "reasoning",
            "Option shaping",
            "Reasoning levels",
            "Route between low- and high-reasoning wrappers over the same underlying model.",
            "Callback",
            "Ordered failover"),
    ];

    private static readonly (string Value, string Label, string Description)[] SelectionOptions =
    [
        ("Semantic", "Semantic routing", "Match the latest user message against route examples."),
        ("Callback", "Callback routing", "Use request shape and complexity in RoutingChatClient.Create."),
        ("None", "Direct", "Start from the first configured route without classification."),
    ];

    private static readonly (string Value, string Label, string Description)[] ResilienceOptions =
    [
        ("Ordered", "Ordered failover", "Try clients in a fixed order after pre-output failure."),
        ("Cooldown", "Cooldown failover", "Remember failures and skip unhealthy clients across requests."),
        ("None", "No failover", "Invoke only the selected route and surface its result."),
    ];

    private static readonly string[] ModelOptions =
    [
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5-mini",
        "gpt-4o-mini",
    ];

    private static readonly string[] ReasoningOptions = ["none", "low", "medium", "high"];

    private static readonly string[] PromptSuggestions =
    [
        "Refactor this C# service to use dependency injection",
        "Write a short launch announcement for a developer tool",
        "Explain when streaming failover becomes terminal",
    ];

    private static readonly string[] DebugTabs = ["Summary", "Attempts", "Options"];

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "for", "how", "in", "is", "it", "me", "of", "on", "the", "this", "to", "with",
    ];

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _requestCancellation;
    private Task? _clockTask;

    private PipelineConfiguration Draft { get; set; } = new();

    private PipelineConfiguration? ActiveConfiguration { get; set; }

    private List<ChatEntry> Messages { get; } = [];

    private RequestDebugState? CurrentDebug { get; set; }

    private int CurrentStep { get; set; }

    private bool IsBuilt { get; set; }

    private bool IsSending { get; set; }

    private string Prompt { get; set; } = string.Empty;

    private string DebugTab { get; set; } = "Summary";

    private string SessionId { get; set; } = CreateSessionId();

    private bool CanAdvance => CurrentStep switch
    {
        0 => Scenarios.Any(scenario => scenario.Id == Draft.ScenarioId),
        1 => !string.IsNullOrWhiteSpace(Draft.SelectionPolicy) &&
             !string.IsNullOrWhiteSpace(Draft.ResiliencePolicy),
        2 => HasValidRoutes(),
        _ => CanBuild,
    };

    private bool CanBuild =>
        HasValidRoutes() &&
        Draft.Routes.Select(route => route.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
        Draft.Routes.Count &&
        (Draft.SelectionPolicy != "Semantic" ||
         (Draft.Routes.Count(route => route.IsDefault) == 1 &&
          Draft.Routes.Where(route => !route.IsDefault)
              .All(route => !string.IsNullOrWhiteSpace(route.ProfileExamples))));

    protected override void OnInitialized()
    {
        SelectScenario("semantic-failover");
        _clockTask = RunClockAsync(_lifetimeCancellation.Token);
    }

    private void GoHome()
    {
        if (IsBuilt)
        {
            RebuildPipeline();
            return;
        }

        CurrentStep = 0;
    }

    private void SelectScenario(string scenarioId)
    {
        Draft = scenarioId switch
        {
            "ordered" => CreateOrderedScenario(),
            "cooldown" => CreateCooldownScenario(),
            "reasoning" => CreateReasoningScenario(),
            _ => CreateSemanticFailoverScenario(),
        };
    }

    private void GoToStep(int step)
    {
        if (step >= 0 && step <= CurrentStep)
        {
            CurrentStep = step;
        }
    }

    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
        }
    }

    private void NextStep()
    {
        if (CanAdvance && CurrentStep < BuilderSteps.Length - 1)
        {
            CurrentStep++;
        }
    }

    private void AddRoute()
    {
        int routeNumber = Draft.Routes.Count + 1;
        Draft.Routes.Add(CreateRoute(
            $"route-{routeNumber}",
            "General",
            "gpt-5.4",
            "medium",
            0.3,
            "Be concise and helpful.",
            "general questions, everyday help"));
    }

    private void RemoveRoute(RouteDefinition route)
    {
        if (Draft.Routes.Count <= 2)
        {
            return;
        }

        bool removedDefault = route.IsDefault;
        Draft.Routes.Remove(route);
        if (removedDefault)
        {
            Draft.Routes[^1].IsDefault = true;
        }
    }

    private void MoveRoute(int index, int offset)
    {
        int target = index + offset;
        if (index < 0 || index >= Draft.Routes.Count || target < 0 || target >= Draft.Routes.Count)
        {
            return;
        }

        RouteDefinition route = Draft.Routes[index];
        Draft.Routes.RemoveAt(index);
        Draft.Routes.Insert(target, route);
    }

    private void MakeDefault(RouteDefinition route)
    {
        foreach (RouteDefinition candidate in Draft.Routes)
        {
            candidate.IsDefault = ReferenceEquals(candidate, route);
        }
    }

    private void BuildPipeline()
    {
        if (!CanBuild)
        {
            return;
        }

        ActiveConfiguration = Draft.Clone(resetRuntimeState: true);
        SessionId = CreateSessionId();
        Messages.Clear();
        CurrentDebug = null;
        Prompt = string.Empty;
        DebugTab = "Summary";
        IsBuilt = true;
    }

    private void RebuildPipeline()
    {
        _requestCancellation?.Cancel();
        if (ActiveConfiguration is not null)
        {
            Draft = ActiveConfiguration.Clone(resetRuntimeState: true);
        }

        IsBuilt = false;
        CurrentStep = 1;
        IsSending = false;
    }

    private void UsePrompt(string prompt) => Prompt = prompt;

    private async Task SendAsync()
    {
        if (IsSending || ActiveConfiguration is null || string.IsNullOrWhiteSpace(Prompt))
        {
            return;
        }

        CleanExpiredStates();

        string prompt = Prompt.Trim();
        Prompt = string.Empty;
        Messages.Add(new ChatEntry { Role = "user", Content = prompt });

        var debug = new RequestDebugState
        {
            RequestId = $"req_{Guid.NewGuid():N}"[..12],
            SessionId = SessionId,
            Prompt = prompt,
            SelectionPolicy = ActiveConfiguration.SelectionPolicy,
            ResiliencePolicy = ActiveConfiguration.ResiliencePolicy,
            InputTokens = EstimateTokens(prompt),
        };
        CurrentDebug = debug;
        DebugTab = "Summary";
        AddEvent(debug, "Routing", "Request started", $"{ActiveConfiguration.SelectionPolicy} selection with {ActiveConfiguration.ResiliencePolicy} resilience.");

        _requestCancellation = new CancellationTokenSource();
        IsSending = true;

        try
        {
            await RouteRequestAsync(prompt, debug, _requestCancellation.Token);
        }
        finally
        {
            IsSending = false;
            _requestCancellation.Dispose();
            _requestCancellation = null;
        }
    }

    private async Task RouteRequestAsync(
        string prompt,
        RequestDebugState debug,
        CancellationToken cancellationToken)
    {
        PipelineConfiguration configuration = ActiveConfiguration!;
        RouteDefinition selectedRoute = SelectInitialRoute(configuration, prompt, debug);
        debug.SelectedRoute = selectedRoute.Name;
        debug.ConfiguredModel = selectedRoute.ModelId;
        AddEvent(debug, "Selection", "Client selected", $"{selectedRoute.Name} ({selectedRoute.ModelId}) was selected first.");

        List<RouteDefinition> candidates = BuildCandidateList(configuration, selectedRoute);
        int attemptNumber = 0;

        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            RouteDefinition route = candidates[candidateIndex];

            if (configuration.ResiliencePolicy == "Cooldown" && IsPolicyUnavailable(route))
            {
                AddEvent(
                    debug,
                    "Policy",
                    "Candidate skipped",
                    $"{route.Name} is {GetPolicyStateLabel(route).ToLowerInvariant()}.");
                await InvokeAsync(StateHasChanged);
                continue;
            }

            if (attemptNumber >= configuration.MaximumAttempts)
            {
                break;
            }

            attemptNumber++;
            AddEvent(debug, "Attempt", $"Attempt {attemptNumber} started", $"Invoking {route.Name} with {route.ModelId}.");
            await InvokeAsync(StateHasChanged);

            var stopwatch = Stopwatch.StartNew();
            bool outputCommitted = false;
            int? timeToFirstUpdate = null;
            ChatEntry? assistantMessage = null;

            try
            {
                if (TryConsumeAvailabilityFailure(route, out string failure, out bool permanentFailure))
                {
                    await Task.Delay(160 + GetLatencyOffset(route), cancellationToken);
                    stopwatch.Stop();

                    if (configuration.ResiliencePolicy == "Cooldown")
                    {
                        ApplyCooldown(route, failure, permanentFailure);
                    }

                    bool isTerminal = !HasNextCandidate(
                        configuration,
                        candidates,
                        candidateIndex + 1,
                        attemptNumber);
                    debug.Attempts.Add(new AttemptRecord(
                        attemptNumber,
                        route.Name,
                        route.ModelId,
                        (int)stopwatch.ElapsedMilliseconds,
                        null,
                        false,
                        false,
                        isTerminal,
                        "Failed",
                        $"HttpRequestException: {failure}"));
                    AddEvent(
                        debug,
                        "Failure",
                        $"{route.Name} failed",
                        isTerminal
                            ? "The attempt is terminal."
                            : "Failure occurred before output; selecting again.");

                    await InvokeAsync(StateHasChanged);
                    if (isTerminal)
                    {
                        break;
                    }

                    continue;
                }

                int latency = 260 + GetLatencyOffset(route);
                string response = ComposeResponse(route, prompt, configuration);
                assistantMessage = new ChatEntry
                {
                    Role = "assistant",
                    RouteName = route.Name,
                    ModelId = route.ModelId,
                    IsPending = true,
                };
                Messages.Add(assistantMessage);

                if (configuration.Streaming)
                {
                    await Task.Delay(latency, cancellationToken);
                    timeToFirstUpdate = (int)stopwatch.ElapsedMilliseconds;

                    foreach (string chunk in ChunkResponse(response))
                    {
                        outputCommitted = true;
                        assistantMessage.Content += chunk;
                        assistantMessage.IsPending = false;
                        await InvokeAsync(StateHasChanged);
                        await Task.Delay(48, cancellationToken);
                    }
                }
                else
                {
                    await Task.Delay(latency + 220, cancellationToken);
                    assistantMessage.Content = response;
                    assistantMessage.IsPending = false;
                    await InvokeAsync(StateHasChanged);
                }

                stopwatch.Stop();
                route.CooldownUntil = null;
                route.PolicyDisabled = false;
                route.ConsecutiveFailures = 0;
                route.LastFailure = null;

                debug.Attempts.Add(new AttemptRecord(
                    attemptNumber,
                    route.Name,
                    route.ModelId,
                    (int)stopwatch.ElapsedMilliseconds,
                    timeToFirstUpdate,
                    true,
                    configuration.Streaming && outputCommitted,
                    true,
                    "Completed",
                    null));
                debug.FinalRoute = route.Name;
                debug.ConfiguredModel = route.ModelId;
                debug.ActualModel = route.ModelId;
                debug.Outcome = "Completed";
                debug.FinishReason = "Stop";
                debug.OutputTokens = EstimateTokens(response);
                AddEvent(debug, "Response", "Response completed", $"{route.Name} completed the request with {route.ModelId}.");
                await InvokeAsync(StateHasChanged);
                return;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                if (assistantMessage is not null)
                {
                    assistantMessage.IsPending = false;
                }

                debug.Attempts.Add(new AttemptRecord(
                    attemptNumber,
                    route.Name,
                    route.ModelId,
                    (int)stopwatch.ElapsedMilliseconds,
                    timeToFirstUpdate,
                    false,
                    outputCommitted,
                    true,
                    "Canceled",
                    "OperationCanceledException"));
                debug.FinalRoute = route.Name;
                debug.ConfiguredModel = route.ModelId;
                debug.ActualModel = outputCommitted ? route.ModelId : "None";
                debug.Outcome = "Canceled";
                debug.FinishReason = "Canceled";
                AddEvent(debug, "Canceled", "Request canceled", "Cancellation is terminal and no reselection occurs.");
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        debug.FinalRoute = "None";
        debug.ActualModel = "None";
        debug.Outcome = "Failed";
        debug.FinishReason = "Error";
        AddEvent(debug, "Failure", "All routes unavailable", "No eligible route completed the request.");
        Messages.Add(new ChatEntry
        {
            Role = "assistant",
            RouteName = "Router",
            Content = "No route could complete the request. Revive a model or rebuild the pipeline.",
        });
        await InvokeAsync(StateHasChanged);
    }

    private RouteDefinition SelectInitialRoute(
        PipelineConfiguration configuration,
        string prompt,
        RequestDebugState debug)
    {
        if (configuration.SelectionPolicy == "Semantic")
        {
            List<RoutingScore> scores = ScoreRoutes(configuration, prompt);
            debug.Scores.AddRange(scores);

            RoutingScore? winner = scores
                .Where(score => !configuration.Routes.First(route => route.Id == score.RouteId).IsDefault)
                .OrderByDescending(score => score.Score)
                .FirstOrDefault();

            if (winner is not null && winner.Score >= configuration.ScoreThreshold)
            {
                AddEvent(debug, "Selection", "Semantic threshold cleared", $"{winner.RouteName} scored {winner.Score:0.00}.");
                return configuration.Routes.First(route => route.Id == winner.RouteId);
            }

            RouteDefinition fallback = configuration.Routes.FirstOrDefault(route => route.IsDefault) ??
                                       configuration.Routes[^1];
            AddEvent(debug, "Selection", "Default route selected", $"No route cleared {configuration.ScoreThreshold:0.00}; using {fallback.Name}.");
            return fallback;
        }

        if (configuration.SelectionPolicy == "Callback")
        {
            bool complex = prompt.Length > 90 ||
                           ContainsAny(prompt, "architecture", "analyze", "complex", "debug", "reason");
            RouteDefinition selected = complex
                ? configuration.Routes.FirstOrDefault(route => route.ReasoningEffort == "high") ??
                  configuration.Routes[0]
                : configuration.Routes.FirstOrDefault(route => route.ReasoningEffort is "low" or "none") ??
                  configuration.Routes[0];
            AddEvent(debug, "Selection", "Callback evaluated", complex ? "Request classified as complex." : "Request classified as routine.");
            return selected;
        }

        return configuration.Routes[0];
    }

    private static List<RouteDefinition> BuildCandidateList(
        PipelineConfiguration configuration,
        RouteDefinition selectedRoute)
    {
        var candidates = new List<RouteDefinition> { selectedRoute };
        if (configuration.ResiliencePolicy == "None")
        {
            return candidates;
        }

        if (configuration.SelectionPolicy == "Semantic")
        {
            RouteDefinition? defaultRoute = configuration.Routes.FirstOrDefault(route => route.IsDefault);
            if (defaultRoute is not null && defaultRoute.Id != selectedRoute.Id)
            {
                candidates.Add(defaultRoute);
            }
        }

        foreach (RouteDefinition route in configuration.Routes)
        {
            if (candidates.All(candidate => candidate.Id != route.Id))
            {
                candidates.Add(route);
            }
        }

        return candidates;
    }

    private bool HasNextCandidate(
        PipelineConfiguration configuration,
        IReadOnlyList<RouteDefinition> candidates,
        int startIndex,
        int attemptsMade)
    {
        if (configuration.ResiliencePolicy == "None" ||
            attemptsMade >= configuration.MaximumAttempts)
        {
            return false;
        }

        for (int i = startIndex; i < candidates.Count; i++)
        {
            if (configuration.ResiliencePolicy != "Cooldown" || !IsPolicyUnavailable(candidates[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static List<RoutingScore> ScoreRoutes(
        PipelineConfiguration configuration,
        string prompt)
    {
        HashSet<string> queryTerms = Tokenize(prompt);
        var scores = new List<RoutingScore>();

        foreach (RouteDefinition route in configuration.Routes)
        {
            if (route.IsDefault)
            {
                scores.Add(new RoutingScore(route.Id, route.Name, 0, "Default route"));
                continue;
            }

            HashSet<string> profileTerms = Tokenize(route.ProfileExamples);
            string[] matches = queryTerms
                .Intersect(profileTerms, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            double score = matches.Length == 0
                ? 0.08
                : Math.Min(0.96, 0.18 + (matches.Length * 0.22));
            scores.Add(new RoutingScore(
                route.Id,
                route.Name,
                score,
                string.Join(", ", matches)));
        }

        return scores;
    }

    private bool TryConsumeAvailabilityFailure(
        RouteDefinition route,
        out string failure,
        out bool permanentFailure)
    {
        permanentFailure = false;

        if (route.FailNext)
        {
            route.FailNext = false;
            failure = $"{route.Name} rejected the one-shot simulated invocation (503).";
            return true;
        }

        if (route.DownUntilRevived)
        {
            permanentFailure = true;
            failure = $"{route.Name} is down until manually revived (503).";
            return true;
        }

        if (route.DownUntil is { } downUntil)
        {
            if (downUntil > DateTimeOffset.Now)
            {
                failure = $"{route.Name} is unavailable until {downUntil:HH:mm:ss} (503).";
                return true;
            }

            route.DownUntil = null;
        }

        failure = string.Empty;
        return false;
    }

    private void ApplyCooldown(RouteDefinition route, string failure, bool permanentFailure)
    {
        route.ConsecutiveFailures++;
        route.LastFailure = failure;

        if (permanentFailure)
        {
            route.PolicyDisabled = true;
            route.CooldownUntil = null;
            return;
        }

        DateTimeOffset automaticCooldown = DateTimeOffset.Now.AddSeconds(ActiveConfiguration!.CooldownSeconds);
        route.CooldownUntil = route.DownUntil is { } manualExpiry && manualExpiry > automaticCooldown
            ? manualExpiry
            : automaticCooldown;
    }

    private void FailNext(RouteDefinition route)
    {
        route.FailNext = true;
        RecordRuntimeAction("Availability", $"{route.Name} will fail its next invocation.");
    }

    private void KillFor(RouteDefinition route, int seconds)
    {
        route.DownUntil = DateTimeOffset.Now.AddSeconds(seconds);
        route.DownUntilRevived = false;
        RecordRuntimeAction("Availability", $"{route.Name} is down for {seconds} seconds.");
    }

    private void KillUntilRevived(RouteDefinition route)
    {
        route.DownUntil = null;
        route.DownUntilRevived = true;
        RecordRuntimeAction("Availability", $"{route.Name} is down until manually revived.");
    }

    private void ReviveRoute(RouteDefinition route)
    {
        route.FailNext = false;
        route.DownUntil = null;
        route.DownUntilRevived = false;
        route.CooldownUntil = null;
        route.PolicyDisabled = false;
        route.ConsecutiveFailures = 0;
        route.LastFailure = null;
        RecordRuntimeAction("Availability", $"{route.Name} was revived and is eligible.");
    }

    private void CancelRequest() => _requestCancellation?.Cancel();

    private void ClearTranscript()
    {
        if (IsSending)
        {
            return;
        }

        Messages.Clear();
        CurrentDebug = null;
        Prompt = string.Empty;
        DebugTab = "Summary";
    }

    private void ClearDebugEvents()
    {
        CurrentDebug?.Events.Clear();
    }

    private void RecordRuntimeAction(string kind, string detail)
    {
        if (CurrentDebug is not null)
        {
            AddEvent(CurrentDebug, kind, "Runtime control changed", detail);
        }
    }

    private string GetRouteStatusLabel(RouteDefinition route)
    {
        if (route.DownUntilRevived)
        {
            return "Down until revived";
        }

        if (route.DownUntil is { } downUntil && downUntil > DateTimeOffset.Now)
        {
            return $"Down {SecondsRemaining(downUntil)}s";
        }

        if (route.PolicyDisabled)
        {
            return "Policy disabled";
        }

        if (route.CooldownUntil is { } cooldownUntil && cooldownUntil > DateTimeOffset.Now)
        {
            return $"Cooling {SecondsRemaining(cooldownUntil)}s";
        }

        if (route.FailNext)
        {
            return "Fails next";
        }

        return "Healthy";
    }

    private string GetRouteStatusClass(RouteDefinition route)
    {
        if (IsRouteUnavailable(route) || route.PolicyDisabled)
        {
            return "down";
        }

        if (IsRouteCoolingDown(route))
        {
            return "cooling";
        }

        return route.FailNext ? "armed" : "healthy";
    }

    private static bool IsRouteUnavailable(RouteDefinition route) =>
        route.DownUntilRevived ||
        route.DownUntil is { } downUntil && downUntil > DateTimeOffset.Now;

    private static bool IsRouteCoolingDown(RouteDefinition route) =>
        route.PolicyDisabled ||
        route.CooldownUntil is { } cooldownUntil && cooldownUntil > DateTimeOffset.Now;

    private static bool IsPolicyUnavailable(RouteDefinition route) =>
        route.PolicyDisabled ||
        route.CooldownUntil is { } cooldownUntil && cooldownUntil > DateTimeOffset.Now;

    private static string GetPolicyStateLabel(RouteDefinition route) =>
        route.PolicyDisabled ? "Disabled by policy" : "Cooling down";

    private void CleanExpiredStates()
    {
        if (ActiveConfiguration is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (RouteDefinition route in ActiveConfiguration.Routes)
        {
            if (route.DownUntil is { } downUntil && downUntil <= now)
            {
                route.DownUntil = null;
            }

            if (route.CooldownUntil is { } cooldownUntil && cooldownUntil <= now)
            {
                route.CooldownUntil = null;
                route.LastFailure = null;
            }
        }
    }

    private async Task RunClockAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (IsBuilt)
                {
                    CleanExpiredStates();
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string ComposeResponse(
        RouteDefinition route,
        string prompt,
        PipelineConfiguration configuration)
    {
        if (ContainsAny(prompt, "code", "c#", "bug", "refactor", "function", "service"))
        {
            return $"The {route.Name} route handled this as a coding request on {route.ModelId}. " +
                   "Start by keeping selection policy separate from each configured client. " +
                   "Wrap the model with route-level options, compose it behind the router, and record each invocation so failover remains visible.";
        }

        if (ContainsAny(prompt, "write", "poem", "story", "announcement", "creative"))
        {
            return $"The {route.Name} route handled this as a creative request on {route.ModelId}. " +
                   "A clean launch note could read: Build the route, send the prompt, and watch every decision surface in real time.";
        }

        if (ContainsAny(prompt, "failover", "stream", "terminal", "cooldown"))
        {
            return $"The {route.Name} route answered with {configuration.ResiliencePolicy} resilience active. " +
                   "A failure before the first streamed update can select again. Once output is committed, the attempt is terminal. " +
                   "Cooldown adds cross-request memory so a recently failed route can be skipped.";
        }

        return $"The {route.Name} route completed this simulated request with {route.ModelId}. " +
               "Use the inspector to compare the selected route, effective options, provider model ID, and failover attempt data.";
    }

    private static IEnumerable<string> ChunkResponse(string response)
    {
        string[] words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int wordsPerChunk = 5;
        for (int index = 0; index < words.Length; index += wordsPerChunk)
        {
            int count = Math.Min(wordsPerChunk, words.Length - index);
            string chunk = string.Join(' ', words, index, count);
            yield return index + count < words.Length ? chunk + " " : chunk;
        }
    }

    private static int GetLatencyOffset(RouteDefinition route) =>
        route.Name.Sum(character => character) % 140;

    private static int SecondsRemaining(DateTimeOffset expiry) =>
        Math.Max(1, (int)Math.Ceiling((expiry - DateTimeOffset.Now).TotalSeconds));

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.35));

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9#+]+")
            .Select(match => match.Value)
            .Where(value => value.Length > 1 && !StopWords.Contains(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AddEvent(
        RequestDebugState debug,
        string kind,
        string title,
        string detail) =>
        debug.Events.Add(new DebugEventRecord(DateTimeOffset.Now, kind, title, detail));

    private static string GetScenarioMonogram(string scenarioId) => scenarioId switch
    {
        "semantic-failover" => "S+",
        "ordered" => "OF",
        "cooldown" => "CD",
        _ => "R2",
    };

    private static string ToTitle(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatBoolean(bool value) => value ? "True" : "False";

    private bool HasValidRoutes() =>
        Draft.Routes.Count >= 2 &&
        Draft.Routes.All(route =>
            !string.IsNullOrWhiteSpace(route.Name) &&
            !string.IsNullOrWhiteSpace(route.ModelId));

    private List<string> GetWarnings()
    {
        var warnings = new List<string>();

        if (Draft.MaximumAttempts > Draft.Routes.Count)
        {
            warnings.Add("Maximum attempts is higher than the number of distinct routes.");
        }

        if (Draft.Routes.Any(route => route.ReasoningEffort == "high" && route.Temperature > 0.7))
        {
            warnings.Add("A high-reasoning route also has a high temperature. Confirm that the selected model supports that combination.");
        }

        if (Draft.ResiliencePolicy == "None")
        {
            warnings.Add("No resilience layer is configured; the first invocation failure will be terminal.");
        }

        return warnings;
    }

    private static string BuildCodePreview(PipelineConfiguration configuration)
    {
        var builder = new StringBuilder();
        foreach (RouteDefinition route in configuration.Routes)
        {
            string variable = ToVariableName(route.Name);
            builder.AppendLine($"IChatClient {variable} = openAI.GetChatClient(\"{route.ModelId}\")");
            builder.AppendLine("    .AsIChatClient()");
            builder.AppendLine("    .AsBuilder()");
            builder.AppendLine($"    .ConfigureOptions(options => options.Reasoning = \"{route.ReasoningEffort}\")");
            builder.AppendLine("    .Build();");
            builder.AppendLine();
        }

        string firstRoute = ToVariableName(configuration.Routes[0].Name);
        string selectedClient = firstRoute;
        if (configuration.SelectionPolicy == "Semantic")
        {
            builder.AppendLine("using var semantic = new SemanticRoutingChatClient(");
            builder.AppendLine("    embeddings, clientProfiles, defaultClient,");
            builder.AppendLine($"    scoreThreshold: {configuration.ScoreThreshold:0.00}f, topK: {configuration.TopK});");
            builder.AppendLine();
            selectedClient = "semantic";
        }
        else if (configuration.SelectionPolicy == "Callback")
        {
            builder.AppendLine("using var callback = RoutingChatClient.Create((context, ct) =>");
            builder.AppendLine("    new(isComplex(context) ? highEffort : lowEffort));");
            builder.AppendLine();
            selectedClient = "callback";
        }

        if (configuration.ResiliencePolicy == "Ordered")
        {
            string candidates = string.Join(", ", new[] { selectedClient }
                .Concat(configuration.Routes
                    .Select(route => ToVariableName(route.Name))
                    .Where(name => name != selectedClient)));
            builder.AppendLine($"using var pipeline = new OrderedFailoverChatClient([{candidates}]);");
        }
        else if (configuration.ResiliencePolicy == "Cooldown")
        {
            string candidates = string.Join(", ", configuration.Routes.Select(route => ToVariableName(route.Name)));
            builder.AppendLine($"using var pipeline = new CooldownFailoverChatClient([{candidates}],");
            builder.AppendLine($"    cooldown: TimeSpan.FromSeconds({configuration.CooldownSeconds}));");
        }
        else
        {
            builder.AppendLine($"IChatClient pipeline = {selectedClient};");
        }

        return builder.ToString().TrimEnd();
    }

    private static string ToVariableName(string value)
    {
        string sanitized = Regex.Replace(value, "[^a-zA-Z0-9]+", " ").Trim();
        string[] parts = sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "route";
        }

        string result = char.ToLowerInvariant(parts[0][0]) + parts[0][1..];
        foreach (string part in parts.Skip(1))
        {
            result += char.ToUpperInvariant(part[0]) + part[1..];
        }

        return char.IsDigit(result[0]) ? $"route{result}" : result;
    }

    private static PipelineConfiguration CreateSemanticFailoverScenario() =>
        new()
        {
            ScenarioId = "semantic-failover",
            ScenarioName = "Semantic plus failover",
            SelectionPolicy = "Semantic",
            ResiliencePolicy = "Ordered",
            ScoreThreshold = 0.35,
            TopK = 3,
            ScoreAggregation = "Mean",
            MaximumAttempts = 3,
            Routes =
            [
                CreateRoute(
                    "code",
                    "Programming",
                    "gpt-5.5",
                    "high",
                    0.2,
                    "You are a precise programming assistant. Prefer concrete code and clear tradeoffs.",
                    "code, bug, refactor, function, csharp, dependency injection, api"),
                CreateRoute(
                    "creative",
                    "Writing",
                    "gpt-5.4",
                    "low",
                    0.8,
                    "You are a concise, vivid creative partner.",
                    "write, poem, story, brainstorm, names, announcement, creative"),
                CreateRoute(
                    "general",
                    "Default",
                    "gpt-4o-mini",
                    "low",
                    0.3,
                    "You are a helpful general-purpose assistant.",
                    "general questions, everyday help",
                    isDefault: true),
            ],
        };

    private static PipelineConfiguration CreateOrderedScenario() =>
        new()
        {
            ScenarioId = "ordered",
            ScenarioName = "Ordered failover",
            SelectionPolicy = "None",
            ResiliencePolicy = "Ordered",
            MaximumAttempts = 3,
            Routes =
            [
                CreateRoute(
                    "primary",
                    "Primary",
                    "gpt-5.5",
                    "high",
                    0.2,
                    "Handle requests with the strongest configured model.",
                    "primary"),
                CreateRoute(
                    "backup",
                    "Backup",
                    "gpt-5.4",
                    "medium",
                    0.3,
                    "Handle requests when the primary is unavailable.",
                    "backup"),
                CreateRoute(
                    "emergency",
                    "Last resort",
                    "gpt-4o-mini",
                    "low",
                    0.3,
                    "Provide a concise answer as the final fallback.",
                    "fallback",
                    isDefault: true),
            ],
        };

    private static PipelineConfiguration CreateCooldownScenario() =>
        new()
        {
            ScenarioId = "cooldown",
            ScenarioName = "Cooldown failover",
            SelectionPolicy = "None",
            ResiliencePolicy = "Cooldown",
            MaximumAttempts = 3,
            CooldownSeconds = 30,
            Routes =
            [
                CreateRoute(
                    "primary",
                    "Primary",
                    "gpt-5.5",
                    "high",
                    0.2,
                    "Handle requests while healthy.",
                    "primary"),
                CreateRoute(
                    "regional-backup",
                    "Backup",
                    "gpt-5.4",
                    "medium",
                    0.3,
                    "Handle requests while the primary is cooling down.",
                    "backup"),
                CreateRoute(
                    "last-resort",
                    "Last resort",
                    "gpt-4o-mini",
                    "low",
                    0.3,
                    "Provide a concise answer when stronger routes are unavailable.",
                    "fallback",
                    isDefault: true),
            ],
        };

    private static PipelineConfiguration CreateReasoningScenario() =>
        new()
        {
            ScenarioId = "reasoning",
            ScenarioName = "Reasoning-level router",
            SelectionPolicy = "Callback",
            ResiliencePolicy = "Ordered",
            MaximumAttempts = 3,
            Routes =
            [
                CreateRoute(
                    "fast",
                    "Routine requests",
                    "gpt-5.4",
                    "low",
                    0.2,
                    "Answer routine requests quickly and directly.",
                    "simple, routine"),
                CreateRoute(
                    "deep",
                    "Complex requests",
                    "gpt-5.4",
                    "high",
                    0.2,
                    "Analyze complex requests carefully before answering.",
                    "complex, architecture, analyze"),
                CreateRoute(
                    "fallback",
                    "Last resort",
                    "gpt-4o-mini",
                    "low",
                    0.3,
                    "Provide a concise fallback answer.",
                    "fallback",
                    isDefault: true),
            ],
        };

    private static RouteDefinition CreateRoute(
        string name,
        string purpose,
        string modelId,
        string reasoningEffort,
        double temperature,
        string instructions,
        string profileExamples,
        bool isDefault = false) =>
        new()
        {
            Name = name,
            Purpose = purpose,
            ModelId = modelId,
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
            Instructions = instructions,
            ProfileExamples = profileExamples,
            IsDefault = isDefault,
        };

    private static string CreateSessionId() => $"session_{Guid.NewGuid():N}"[..16];

    public async ValueTask DisposeAsync()
    {
        _requestCancellation?.Cancel();
        _lifetimeCancellation.Cancel();

        if (_clockTask is not null)
        {
            try
            {
                await _clockTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _requestCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
    }
}

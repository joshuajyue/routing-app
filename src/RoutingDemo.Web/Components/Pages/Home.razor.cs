using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RoutingDemo.Web.Demo;

namespace RoutingDemo.Web.Components.Pages;

public partial class Home
{
    private static readonly ScenarioDefinition[] Scenarios =
    [
        new(
            "semantic-composition",
            "Best full demo",
            "Semantic route families",
            "Semantic profiles select stable family clients; each family owns its own ordered, cooldown, or single-model target.",
            "Semantic",
            "Per-family resilience"),
        new(
            "sticky-reasoning",
            "Session routing",
            "Sticky model + reasoning",
            "Pin a broad model family for the session, then semantically choose that model's reasoning effort per turn.",
            "Sticky semantic",
            "Model pin / per-turn reasoning"),
        new(
            "cooldown",
            "Custom policy",
            "Cooldown route family",
            "One route family remembers failures across requests and skips cooling model clients.",
            "Direct",
            "Cooldown chain"),
        new(
            "reasoning",
            "Option shaping",
            "Reasoning levels",
            "Semantic routing selects low, medium, or high reasoning wrappers over the same model.",
            "Semantic",
            "Low / medium / high"),
    ];

    private static readonly (string Value, string Label, string Description)[] ResilienceOptions =
    [
        ("None", "Single", "Invoke the first configured model client only."),
        ("Ordered", "Ordered", "Try model clients in a fixed order after pre-output failure."),
        ("Cooldown", "Cooldown", "Remember failures and skip unhealthy model clients across requests."),
    ];

    private static readonly string[] ModelOptions =
    [
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-4o-mini",
    ];

    private static readonly string[] ReasoningOptions = ["none", "low", "medium", "high"];

    private static readonly string[] PromptSuggestions =
    [
        "Refactor this C# service to use dependency injection",
        "Write a short launch announcement for a developer tool",
        "Explain how semantic routing composes with cooldown failover",
    ];

    private static readonly string[] DebugTabs = ["Summary", "Attempts", "Options"];

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "for", "how", "in", "is", "it", "me", "of", "on", "the", "this", "to", "with",
    ];

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<string, string> _stickySelections = [];
    private CancellationTokenSource? _requestCancellation;
    private Task? _clockTask;
    private IJSObjectReference? _composerModule;
    private DotNetObjectReference<Home>? _dotNetReference;
    private bool _composerAttached;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    private PipelineConfiguration Draft { get; set; } = new();

    private PipelineConfiguration? ActiveConfiguration { get; set; }

    private List<ChatEntry> Messages { get; } = [];

    private RequestDebugState? CurrentDebug { get; set; }

    private bool IsBuilt { get; set; }

    private bool IsSending { get; set; }

    private string Prompt { get; set; } = string.Empty;

    private string DebugTab { get; set; } = "Summary";

    private string SessionId { get; set; } = CreateSessionId();

    private string SelectedNodeKey { get; set; } = "selector";

    private string ActiveSelectedNodeKey { get; set; } = string.Empty;

    private bool PipelineSidebarVisible { get; set; } = true;

    private bool DebugSidebarVisible { get; set; } = true;

    private RouteFamilyDefinition? SelectedFamily => ResolveSelectedFamily();

    private RouteFamilyDefinition? SelectedInnerFamily => ResolveSelectedInnerFamily();

    private SemanticRouterDefinition? SelectedSemanticRouter => ResolveSelectedSemanticRouter();

    private RouteFamilyDefinition? SelectedRouteFamily => ResolveSelectedRouteFamily();

    private RouteDefinition? SelectedRoute => ResolveSelectedRoute();

    private RouteLocation? ActiveSelectedRouteLocation =>
        ActiveConfiguration is null
            ? null
            : ResolveRouteLocation(ActiveConfiguration, ActiveSelectedNodeKey);

    private string? StickyPinnedFamilyId =>
        ActiveConfiguration is not null &&
        ActiveConfiguration.SelectionPolicy == "StickySemantic" &&
        _stickySelections.TryGetValue(SessionId, out string? familyId)
            ? familyId
            : null;

    private RouteFamilyDefinition? StickyPinnedFamily =>
        StickyPinnedFamilyId is { } familyId
            ? ActiveConfiguration?.Families.FirstOrDefault(family => family.Id == familyId)
            : null;

    private bool CanBuild =>
        HasValidTree() &&
        (!Draft.GlobalFallbackEnabled ||
         (!string.IsNullOrWhiteSpace(Draft.GlobalFallback.Name) &&
          !string.IsNullOrWhiteSpace(Draft.GlobalFallback.ModelId))) &&
        Draft.Families.Select(family => family.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == Draft.Families.Count &&
        Draft.AllRoutes().Select(route => route.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == Draft.AllRoutes().Count() &&
        (Draft.SelectionPolicy != "Direct" || Draft.Families.Count == 1) &&
        (!UsesSemanticSelection(Draft.SelectionPolicy) ||
         (Draft.Families.Count(family => family.IsDefault) == 1 &&
          Draft.Families.Where(family => !family.IsDefault)
              .All(family => !string.IsNullOrWhiteSpace(family.ProfileExamples))));

    protected override void OnInitialized()
    {
        SelectScenario("semantic-composition");
        _clockTask = RunClockAsync(_lifetimeCancellation.Token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!IsBuilt)
        {
            _composerAttached = false;
            return;
        }

        if (_composerAttached)
        {
            return;
        }

        _composerModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "./chat-composer.js");
        _dotNetReference ??= DotNetObjectReference.Create(this);
        await _composerModule.InvokeVoidAsync(
            "attachComposer",
            "chat-composer",
            _dotNetReference);
        _composerAttached = true;
    }

    private void GoHome()
    {
        if (IsBuilt)
        {
            RebuildPipeline();
            return;
        }

        SelectedNodeKey = "selector";
    }

    private void SelectScenario(string scenarioId)
    {
        Draft = scenarioId switch
        {
            "sticky-reasoning" => CreateStickyReasoningScenario(),
            "cooldown" => CreateCooldownScenario(),
            "reasoning" => CreateReasoningScenario(),
            _ => CreateSemanticCompositionScenario(),
        };
        SelectedNodeKey = "selector";
    }

    private void SetSelectionPolicy(string policy)
    {
        if (policy == "Direct" && Draft.Families.Count != 1)
        {
            return;
        }

        Draft.SelectionPolicy = policy;
        if (UsesSemanticSelection(policy) && Draft.Families.All(family => !family.IsDefault))
        {
            Draft.Families[^1].IsDefault = true;
        }
    }

    private void OnSelectionChanged(ChangeEventArgs eventArgs) =>
        SetSelectionPolicy(eventArgs.Value?.ToString() ?? "Semantic");

    private void AddFamily()
    {
        if (Draft.SelectionPolicy == "Direct")
        {
            Draft.SelectionPolicy = "Semantic";
            EnsureSemanticDefault();
        }

        int familyNumber = Draft.Families.Count + 1;
        RouteFamilyDefinition family = Draft.SelectionPolicy == "StickySemantic"
            ? CreateSemanticModelFamily(
                $"model-{familyNumber}",
                "Custom model tasks",
                "custom task, specialized request",
                "gpt-5.4")
            : CreateFamily(
                $"family-{familyNumber}",
                "Custom route family",
                "custom requests, specialized help",
                "None",
                [
                    CreateRoute(
                        $"family-{familyNumber}-primary",
                        "Primary",
                        "gpt-5.4",
                        "medium",
                        0.3,
                        "Be concise and helpful."),
                ]);
        Draft.Families.Add(family);
        SelectedNodeKey = family.SemanticRouter is null
            ? $"family:{family.Id}"
            : $"semantic:{family.Id}";
    }

    private void RemoveFamily(RouteFamilyDefinition family)
    {
        List<RouteFamilyDefinition> owner = GetFamilyOwner(family);
        if (owner.Count <= 1)
        {
            return;
        }

        bool removedDefault = family.IsDefault;
        owner.Remove(family);
        if (removedDefault)
        {
            owner[^1].IsDefault = true;
        }

        SelectedNodeKey = GetFamilyNodeKey(owner[0]);
    }

    private void MoveFamily(RouteFamilyDefinition family, int offset)
    {
        List<RouteFamilyDefinition> owner = GetFamilyOwner(family);
        int index = owner.IndexOf(family);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= owner.Count)
        {
            return;
        }

        owner.RemoveAt(index);
        owner.Insert(target, family);
    }

    private void MakeDefault(RouteFamilyDefinition family)
    {
        foreach (RouteFamilyDefinition candidate in GetFamilyOwner(family))
        {
            candidate.IsDefault = ReferenceEquals(candidate, family);
        }
    }

    private void AddSemanticProfile(RouteFamilyDefinition outerFamily)
    {
        SemanticRouterDefinition router = outerFamily.SemanticRouter!;
        int profileNumber = router.Profiles.Count + 1;
        var profile = CreateFamily(
            $"profile-{profileNumber}",
            "Reasoning profile",
            "example request",
            "None",
            [
                CreateRoute(
                    $"{outerFamily.Name}-profile-{profileNumber}",
                    "Reasoning configuration",
                    outerFamily.AllRoutes().First().ModelId,
                    "medium",
                    0.2,
                    "Handle requests matching this reasoning profile."),
            ]);
        router.Profiles.Add(profile);
        SelectedNodeKey = $"subfamily:{outerFamily.Id}:{profile.Id}";
    }

    private void SelectSemanticRouter(RouteFamilyDefinition family) =>
        SelectedNodeKey = $"semantic:{family.Id}";

    private void EnsureSemanticDefault()
    {
        if (Draft.Families.Count > 0 && Draft.Families.All(family => !family.IsDefault))
        {
            Draft.Families[0].IsDefault = true;
        }
    }

    private void SetFamilyResilience(RouteFamilyDefinition family, string policy)
    {
        if (policy == "None" && family.Routes.Count != 1)
        {
            return;
        }

        family.ResiliencePolicy = policy;
        family.MaximumAttempts = policy == "None"
            ? 1
            : Math.Max(2, Math.Min(family.Routes.Count, family.MaximumAttempts));
        SelectedNodeKey = policy == "None"
            ? $"family:{family.Id}"
            : $"policy:{family.Id}";
    }

    private void AddRoute(RouteFamilyDefinition family)
    {
        int routeNumber = family.Routes.Count + 1;
        RouteDefinition source = family.Routes[^1];
        var route = CreateRoute(
            $"{family.Name}-backup-{routeNumber - 1}",
            "Backup",
            source.ModelId == "gpt-5.5" ? "gpt-5.4" : "gpt-4o-mini",
            source.ReasoningEffort == "high" ? "medium" : source.ReasoningEffort,
            source.Temperature,
            $"Back up the {family.Name} route family.");
        family.Routes.Add(route);
        if (family.ResiliencePolicy == "None")
        {
            family.ResiliencePolicy = "Ordered";
        }

        family.MaximumAttempts = Math.Max(family.MaximumAttempts, family.Routes.Count);
        SelectedNodeKey = GetRouteNodeKey(family, route);
    }

    private void RemoveRoute(RouteFamilyDefinition family, RouteDefinition route)
    {
        if (family.Routes.Count <= 1)
        {
            return;
        }

        family.Routes.Remove(route);
        family.MaximumAttempts = Math.Min(family.MaximumAttempts, family.Routes.Count);
        if (family.Routes.Count == 1)
        {
            family.ResiliencePolicy = "None";
            family.MaximumAttempts = 1;
        }

        SelectedNodeKey = GetRouteNodeKey(family, family.Routes[0]);
    }

    private void MoveRoute(RouteFamilyDefinition family, RouteDefinition route, int offset)
    {
        int index = family.Routes.IndexOf(route);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= family.Routes.Count)
        {
            return;
        }

        family.Routes.RemoveAt(index);
        family.Routes.Insert(target, route);
    }

    private bool IsFirstFamily(RouteFamilyDefinition family) => GetFamilyOwner(family).IndexOf(family) == 0;

    private bool IsLastFamily(RouteFamilyDefinition family) =>
        GetFamilyOwner(family).IndexOf(family) == GetFamilyOwner(family).Count - 1;

    private static bool IsFirstRoute(RouteFamilyDefinition family, RouteDefinition route) =>
        family.Routes.IndexOf(route) == 0;

    private static bool IsLastRoute(RouteFamilyDefinition family, RouteDefinition route) =>
        family.Routes.IndexOf(route) == family.Routes.Count - 1;

    private List<RouteFamilyDefinition> GetFamilyOwner(RouteFamilyDefinition family)
    {
        foreach (RouteFamilyDefinition outerFamily in Draft.Families)
        {
            if (outerFamily.SemanticRouter?.Profiles.Contains(family) == true)
            {
                return outerFamily.SemanticRouter.Profiles;
            }
        }

        return Draft.Families;
    }

    private string GetFamilyNodeKey(RouteFamilyDefinition family)
    {
        foreach (RouteFamilyDefinition outerFamily in Draft.Families)
        {
            if (outerFamily.SemanticRouter?.Profiles.Contains(family) == true)
            {
                return $"subfamily:{outerFamily.Id}:{family.Id}";
            }
        }

        return $"family:{family.Id}";
    }

    private string GetRouteNodeKey(RouteFamilyDefinition family, RouteDefinition route)
    {
        RouteFamilyDefinition? outerFamily = GetOuterFamilyForInnerProfile(family);
        return outerFamily is null
            ? $"route:{family.Id}:{route.Id}"
            : $"subroute:{outerFamily.Id}:{family.Id}:{route.Id}";
    }

    private RouteFamilyDefinition? GetOuterFamilyForInnerProfile(RouteFamilyDefinition family) =>
        Draft.Families.FirstOrDefault(
            outerFamily => outerFamily.SemanticRouter?.Profiles.Contains(family) == true);

    private RouteFamilyDefinition? ResolveSelectedFamily()
    {
        string[] parts = SelectedNodeKey.Split(':');
        string? familyId = parts.Length switch
        {
            >= 2 when parts[0] is "family" or "policy" or "semantic" => parts[1],
            >= 3 when parts[0] == "route" => parts[1],
            _ => null,
        };

        return familyId is null
            ? null
            : Draft.Families.FirstOrDefault(family => family.Id == familyId);
    }

    private SemanticRouterDefinition? ResolveSelectedSemanticRouter() =>
        SelectedNodeKey.StartsWith("semantic:", StringComparison.Ordinal)
            ? ResolveSelectedFamily()?.SemanticRouter
            : null;

    private RouteFamilyDefinition? ResolveSelectedInnerFamily()
    {
        string[] parts = SelectedNodeKey.Split(':');
        if (parts.Length < 3 ||
            parts[0] is not ("subfamily" or "subpolicy") &&
            !(parts.Length >= 4 && parts[0] == "subroute"))
        {
            return null;
        }

        string outerFamilyId = parts[1];
        string innerFamilyId = parts[2];
        return Draft.Families
            .FirstOrDefault(family => family.Id == outerFamilyId)
            ?.SemanticRouter?.Profiles
            .FirstOrDefault(profile => profile.Id == innerFamilyId);
    }

    private RouteFamilyDefinition? ResolveSelectedRouteFamily() =>
        SelectedNodeKey.StartsWith("subroute:", StringComparison.Ordinal)
            ? ResolveSelectedInnerFamily()
            : SelectedNodeKey.StartsWith("route:", StringComparison.Ordinal)
                ? ResolveSelectedFamily()
                : null;

    private RouteDefinition? ResolveSelectedRoute()
    {
        string[] parts = SelectedNodeKey.Split(':');
        if (parts.Length >= 2 && parts[0] == "global")
        {
            return Draft.GlobalFallback;
        }

        if (parts.Length >= 3 && parts[0] == "route")
        {
            return Draft.Families
                .FirstOrDefault(family => family.Id == parts[1])
                ?.Routes.FirstOrDefault(route => route.Id == parts[2]);
        }

        if (parts.Length >= 4 && parts[0] == "subroute")
        {
            return Draft.Families
                .FirstOrDefault(family => family.Id == parts[1])
                ?.SemanticRouter?.Profiles
                .FirstOrDefault(profile => profile.Id == parts[2])
                ?.Routes.FirstOrDefault(route => route.Id == parts[3]);
        }

        return null;
    }

    private string GetSelectedNodeType()
    {
        if (SelectedNodeKey == "outer")
        {
            return "Outer resilience";
        }

        if (SelectedNodeKey == "selector")
        {
            return "Selection";
        }

        if (SelectedNodeKey.StartsWith("family:", StringComparison.Ordinal))
        {
            return UsesSemanticSelection(Draft.SelectionPolicy) ? "Semantic profile" : "Route family";
        }

        if (SelectedNodeKey.StartsWith("semantic:", StringComparison.Ordinal))
        {
            return "Nested selection";
        }

        if (SelectedNodeKey.StartsWith("subfamily:", StringComparison.Ordinal))
        {
            return "Reasoning profile";
        }

        if (SelectedNodeKey.StartsWith("policy:", StringComparison.Ordinal) ||
            SelectedNodeKey.StartsWith("subpolicy:", StringComparison.Ordinal))
        {
            return "Family resilience";
        }

        if (SelectedNodeKey.StartsWith("global:", StringComparison.Ordinal))
        {
            return "Outer fallback client";
        }

        return "Model client";
    }

    private string GetSelectedNodeTitle()
    {
        if (SelectedNodeKey == "outer")
        {
            return "Whole-pipeline failover";
        }

        if (SelectedNodeKey == "selector")
        {
            return GetSelectorTypeName(Draft);
        }

        if (SelectedSemanticRouter is { } semanticRouter)
        {
            return semanticRouter.Name;
        }

        RouteFamilyDefinition? selectedPolicyFamily =
            SelectedNodeKey.StartsWith("subpolicy:", StringComparison.Ordinal)
                ? SelectedInnerFamily
                : SelectedFamily;
        if ((SelectedNodeKey.StartsWith("policy:", StringComparison.Ordinal) ||
             SelectedNodeKey.StartsWith("subpolicy:", StringComparison.Ordinal)) &&
            selectedPolicyFamily is { } policyFamily)
        {
            return policyFamily.ResiliencePolicy == "Cooldown"
                ? "CooldownFailoverChatClient"
                : "OrderedFailoverChatClient";
        }

        return SelectedRoute?.Name ??
               SelectedInnerFamily?.Name ??
               SelectedFamily?.Name ??
               "Pipeline node";
    }

    private static string GetSelectorTypeName(PipelineConfiguration configuration) =>
        configuration.SelectionPolicy switch
        {
            "Semantic" => "SemanticRoutingChatClient",
            "StickySemantic" => "StickySemanticRoutingChatClient",
            _ => "Direct route selection",
        };

    private static string GetResilienceDescription(string policy) => policy switch
    {
        "Ordered" => "Try clients in order after a pre-output failure.",
        "Cooldown" => "Skip recently failed clients across requests.",
        _ => "Use one configured client.",
    };

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
        ActiveSelectedNodeKey = GetInitialActiveNodeKey(ActiveConfiguration);
        PipelineSidebarVisible = true;
        DebugSidebarVisible = true;
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
        SelectedNodeKey = "selector";
        ActiveSelectedNodeKey = string.Empty;
        IsSending = false;
    }

    private void TogglePipelineSidebar() => PipelineSidebarVisible = !PipelineSidebarVisible;

    private void ToggleDebugSidebar() => DebugSidebarVisible = !DebugSidebarVisible;

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
            InputTokens = EstimateTokens(prompt),
        };
        CurrentDebug = debug;
        DebugTab = "Summary";
        AddEvent(
            debug,
            "Routing",
            "Request started",
            $"{ActiveConfiguration.SelectionPolicy} selection over {ActiveConfiguration.Families.Count} stable route-family clients.");

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

    [JSInvokable]
    public Task SendPromptFromKeyboardAsync() => SendAsync();

    private async Task RouteRequestAsync(
        string prompt,
        RequestDebugState debug,
        CancellationToken cancellationToken)
    {
        PipelineConfiguration configuration = ActiveConfiguration!;
        RouteFamilyDefinition selectedFamily = SelectRouteFamily(configuration, prompt, debug);
        debug.SelectedFamilyId = selectedFamily.Id;
        debug.SelectedRoute = selectedFamily.Name;
        debug.ResiliencePolicy = selectedFamily.SemanticRouter is null
            ? GetResilienceDisplayName(selectedFamily.ResiliencePolicy)
            : "Nested semantic";
        debug.ConfiguredModel = selectedFamily.SemanticRouter is null
            ? selectedFamily.Routes[0].ModelId
            : selectedFamily.Name;
        AddEvent(
            debug,
            "Selection",
            "Route family selected",
            $"{selectedFamily.Name} resolved to a {GetFamilyTargetType(selectedFamily)} target.");

        var execution = new RequestExecutionContext();
        InvocationOutcome familyOutcome = await ExecuteFamilyAsync(
            configuration,
            selectedFamily,
            prompt,
            debug,
            execution,
            cancellationToken);

        if (familyOutcome.Kind is InvocationOutcomeKind.Succeeded or InvocationOutcomeKind.Canceled)
        {
            if (familyOutcome.Kind == InvocationOutcomeKind.Succeeded)
            {
                PinStickySelection(configuration, selectedFamily, debug);
            }

            return;
        }

        if (configuration.GlobalFallbackEnabled)
        {
            AddEvent(
                debug,
                "Policy",
                "Family failure propagated",
                $"{selectedFamily.Name} was terminal in its own layer; the outer OrderedFailoverChatClient selected {configuration.GlobalFallback.Name}.");
            InvocationOutcome globalOutcome = await InvokeRouteAsync(
                configuration.GlobalFallback,
                "Outer fallback",
                prompt,
                debug,
                execution,
                isTerminalInLayer: true,
                cancellationToken);
            if (globalOutcome.Kind is InvocationOutcomeKind.Succeeded or InvocationOutcomeKind.Canceled)
            {
                return;
            }
        }

        debug.FinalRoute = "None";
        debug.FinalRouteId = string.Empty;
        debug.ActualModel = "None";
        debug.Outcome = "Failed";
        debug.FinishReason = "Error";
        AddEvent(debug, "Failure", "Pipeline exhausted", "No configured leaf client completed the request.");
        Messages.Add(new ChatEntry
        {
            Role = "assistant",
            RouteName = "Router",
            Content = "No route could complete the request. Revive a model or rebuild the pipeline.",
        });
        await InvokeAsync(StateHasChanged);
    }

    private async Task<InvocationOutcome> ExecuteFamilyAsync(
        PipelineConfiguration configuration,
        RouteFamilyDefinition family,
        string prompt,
        RequestDebugState debug,
        RequestExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (family.SemanticRouter is { } semanticRouter)
        {
            RouteFamilyDefinition selectedProfile = SelectSemanticProfile(
                semanticRouter.Profiles,
                semanticRouter.ScoreThreshold,
                prompt,
                debug,
                layer: family.Name,
                useDemoKeywords: configuration.SelectionPolicy == "StickySemantic");
            debug.SelectedInnerRoute = selectedProfile.Name;
            AddEvent(
                debug,
                "Selection",
                "Inner semantic profile selected",
                $"{family.Name} selected {selectedProfile.Name} reasoning for this turn.");
            return await ExecuteFamilyAsync(
                configuration,
                selectedProfile,
                prompt,
                debug,
                execution,
                cancellationToken);
        }

        IReadOnlyList<RouteDefinition> candidates = family.ResiliencePolicy == "None"
            ? family.Routes.Take(1).ToArray()
            : family.Routes;
        int maximumAttempts = family.ResiliencePolicy == "None"
            ? 1
            : family.MaximumAttempts;
        int familyAttempts = 0;

        for (int index = 0; index < candidates.Count; index++)
        {
            RouteDefinition route = candidates[index];
            if (family.ResiliencePolicy == "Cooldown" && IsPolicyUnavailable(route))
            {
                AddEvent(
                    debug,
                    "Policy",
                    "Cooling model skipped",
                    $"{route.Name} was skipped inside {family.Name}; state is {GetPolicyStateLabel(route).ToLowerInvariant()}.");
                await InvokeAsync(StateHasChanged);
                continue;
            }

            if (familyAttempts >= maximumAttempts)
            {
                break;
            }

            familyAttempts++;
            bool hasNextCandidate = HasNextFamilyCandidate(
                family,
                candidates,
                index + 1,
                familyAttempts,
                maximumAttempts);
            InvocationOutcome outcome = await InvokeRouteAsync(
                route,
                $"{family.Name} / {GetResilienceDisplayName(family.ResiliencePolicy)}",
                prompt,
                debug,
                execution,
                isTerminalInLayer: !hasNextCandidate,
                cancellationToken);

            if (outcome.Kind == InvocationOutcomeKind.Succeeded ||
                outcome.Kind == InvocationOutcomeKind.Canceled)
            {
                return outcome;
            }

            if (family.ResiliencePolicy == "Cooldown")
            {
                ApplyCooldown(family, route, outcome.Failure!, outcome.PermanentFailure);
            }

            if (!hasNextCandidate)
            {
                break;
            }

            AddEvent(
                debug,
                "Policy",
                "Family reselected",
                $"{family.Name} will invoke its next compatible model client.");
        }

        return InvocationOutcome.Failed("The selected route family was exhausted.");
    }

    private async Task<InvocationOutcome> InvokeRouteAsync(
        RouteDefinition route,
        string layer,
        string prompt,
        RequestDebugState debug,
        RequestExecutionContext execution,
        bool isTerminalInLayer,
        CancellationToken cancellationToken)
    {
        execution.AttemptNumber++;
        int attemptNumber = execution.AttemptNumber;
        AddEvent(debug, "Attempt", $"Attempt {attemptNumber} started", $"Invoking {route.Name} in {layer}.");
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
                debug.Attempts.Add(new AttemptRecord(
                    attemptNumber,
                    layer,
                    route.Name,
                    route.ModelId,
                    (int)stopwatch.ElapsedMilliseconds,
                    null,
                    false,
                    false,
                    isTerminalInLayer,
                    "Failed",
                    $"HttpRequestException: {failure}"));
                AddEvent(
                    debug,
                    "Failure",
                    $"{route.Name} failed",
                    isTerminalInLayer
                        ? "The attempt is terminal in this failover layer."
                        : "Failure occurred before output; this layer can select again.");
                await InvokeAsync(StateHasChanged);
                return InvocationOutcome.Failed(failure, permanentFailure);
            }

            int latency = 260 + GetLatencyOffset(route);
            string response = ComposeResponse(route, layer, prompt);
            assistantMessage = new ChatEntry
            {
                Role = "assistant",
                RouteName = route.Name,
                ModelId = route.ModelId,
                IsPending = true,
            };
            Messages.Add(assistantMessage);

            if (ActiveConfiguration!.Streaming)
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
                layer,
                route.Name,
                route.ModelId,
                (int)stopwatch.ElapsedMilliseconds,
                timeToFirstUpdate,
                true,
                ActiveConfiguration.Streaming && outputCommitted,
                true,
                "Completed",
                null));
            debug.FinalRoute = route.Name;
            debug.FinalRouteId = route.Id;
            debug.ConfiguredModel = route.ModelId;
            debug.ActualModel = route.ModelId;
            debug.Outcome = "Completed";
            debug.FinishReason = "Stop";
            debug.OutputTokens = EstimateTokens(response);
            AddEvent(debug, "Response", "Response completed", $"{route.Name} completed the request with {route.ModelId}.");
            await InvokeAsync(StateHasChanged);
            return InvocationOutcome.Succeeded();
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (assistantMessage is not null)
            {
                if (!outputCommitted)
                {
                    Messages.Remove(assistantMessage);
                }
                else
                {
                    assistantMessage.IsPending = false;
                }
            }

            debug.Attempts.Add(new AttemptRecord(
                attemptNumber,
                layer,
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
            debug.FinalRouteId = route.Id;
            debug.ConfiguredModel = route.ModelId;
            debug.ActualModel = outputCommitted ? route.ModelId : "None";
            debug.Outcome = "Canceled";
            debug.FinishReason = "Canceled";
            AddEvent(debug, "Canceled", "Request canceled", "Cancellation is terminal and no layer reselects.");
            await InvokeAsync(StateHasChanged);
            return InvocationOutcome.Canceled();
        }
    }

    private RouteFamilyDefinition SelectRouteFamily(
        PipelineConfiguration configuration,
        string prompt,
        RequestDebugState debug)
    {
        if (configuration.SelectionPolicy == "StickySemantic")
        {
            if (_stickySelections.TryGetValue(SessionId, out string? pinnedFamilyId) &&
                configuration.Families.FirstOrDefault(family => family.Id == pinnedFamilyId) is { } pinnedFamily)
            {
                AddEvent(
                    debug,
                    "Selection",
                    "Sticky cache hit",
                    $"Session key {SessionId} reused the pinned {pinnedFamily.Name} family.");
                return pinnedFamily;
            }

            AddEvent(
                debug,
                "Selection",
                "Sticky cache miss",
                $"Session key {SessionId} has no pin; running first-turn semantic classification.");
        }

        if (UsesSemanticSelection(configuration.SelectionPolicy))
        {
            return SelectSemanticProfile(
                configuration.Families,
                configuration.ScoreThreshold,
                prompt,
                debug,
                layer: "outer",
                useDemoKeywords: configuration.SelectionPolicy == "StickySemantic");
        }

        return configuration.Families[0];
    }

    private void PinStickySelection(
        PipelineConfiguration configuration,
        RouteFamilyDefinition selectedFamily,
        RequestDebugState debug)
    {
        if (configuration.SelectionPolicy != "StickySemantic" ||
            _stickySelections.ContainsKey(SessionId))
        {
            return;
        }

        _stickySelections[SessionId] = selectedFamily.Id;
        AddEvent(
            debug,
            "Selection",
            "Session route pinned",
            $"Cached {selectedFamily.Name} under application session key {SessionId} after the response completed.");
    }

    private static bool HasNextFamilyCandidate(
        RouteFamilyDefinition family,
        IReadOnlyList<RouteDefinition> candidates,
        int startIndex,
        int attemptsMade,
        int maximumAttempts)
    {
        if (family.ResiliencePolicy == "None" || attemptsMade >= maximumAttempts)
        {
            return false;
        }

        for (int index = startIndex; index < candidates.Count; index++)
        {
            if (family.ResiliencePolicy != "Cooldown" || !IsPolicyUnavailable(candidates[index]))
            {
                return true;
            }
        }

        return false;
    }

    private RouteFamilyDefinition SelectSemanticProfile(
        IReadOnlyList<RouteFamilyDefinition> profiles,
        double scoreThreshold,
        string prompt,
        RequestDebugState debug,
        string layer,
        bool useDemoKeywords)
    {
        HashSet<string> queryTerms = Tokenize(prompt);
        RouteFamilyDefinition? explicitProfile = useDemoKeywords
            ? profiles.FirstOrDefault(profile =>
                Tokenize(profile.Name).Any(queryTerms.Contains))
            : null;
        List<RoutingScore> scores = ScoreFamilies(
            profiles,
            queryTerms,
            layer,
            explicitProfile?.Id);
        debug.Scores.AddRange(scores);

        if (explicitProfile is not null)
        {
            AddEvent(
                debug,
                "Selection",
                "Demo keyword matched",
                $"{layer}: '{explicitProfile.Name}' selected by explicit keyword. Configured profile order breaks ties.");
            return explicitProfile;
        }

        RoutingScore? winner = scores
            .Where(score => !profiles.First(profile => profile.Id == score.RouteFamilyId).IsDefault)
            .OrderByDescending(score => score.Score)
            .FirstOrDefault();

        if (winner is not null && winner.Score >= scoreThreshold)
        {
            AddEvent(
                debug,
                "Selection",
                "Semantic threshold cleared",
                $"{layer}: {winner.RouteFamilyName} scored {winner.Score:0.00}.");
            return profiles.First(profile => profile.Id == winner.RouteFamilyId);
        }

        RouteFamilyDefinition fallback = profiles.FirstOrDefault(profile => profile.IsDefault) ?? profiles[^1];
        AddEvent(
            debug,
            "Selection",
            "Default family selected",
            $"{layer}: no profile cleared {scoreThreshold:0.00}; using {fallback.Name}.");
        return fallback;
    }

    private static List<RoutingScore> ScoreFamilies(
        IReadOnlyList<RouteFamilyDefinition> profiles,
        HashSet<string> queryTerms,
        string layer,
        string? explicitProfileId)
    {
        var scores = new List<RoutingScore>();

        foreach (RouteFamilyDefinition family in profiles)
        {
            if (family.Id == explicitProfileId)
            {
                scores.Add(new RoutingScore(
                    layer,
                    family.Id,
                    family.Name,
                    1,
                    $"explicit: {family.Name}"));
                continue;
            }

            if (family.IsDefault)
            {
                scores.Add(new RoutingScore(layer, family.Id, family.Name, 0, "Default family"));
                continue;
            }

            HashSet<string> profileTerms = Tokenize(family.ProfileExamples);
            string[] matches = queryTerms
                .Intersect(profileTerms, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            double score = matches.Length == 0
                ? 0.08
                : Math.Min(0.96, 0.18 + (matches.Length * 0.22));
            scores.Add(new RoutingScore(
                layer,
                family.Id,
                family.Name,
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

    private static void ApplyCooldown(
        RouteFamilyDefinition family,
        RouteDefinition route,
        string failure,
        bool permanentFailure)
    {
        route.ConsecutiveFailures++;
        route.LastFailure = failure;

        if (permanentFailure)
        {
            route.PolicyDisabled = true;
            route.CooldownUntil = null;
            return;
        }

        DateTimeOffset automaticCooldown = DateTimeOffset.Now.AddSeconds(family.CooldownSeconds);
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

    private void ClearStickyPin()
    {
        _stickySelections.Remove(SessionId);
        if (CurrentDebug is not null)
        {
            AddEvent(
                CurrentDebug,
                "Selection",
                "Sticky pin cleared",
                $"Removed the cached route family for session key {SessionId}.");
        }
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

    private static string GetAvailabilityLabel(RouteDefinition route)
    {
        if (route.DownUntilRevived)
        {
            return "Down until revived";
        }

        if (route.DownUntil is { } downUntil && downUntil > DateTimeOffset.Now)
        {
            return $"Down {SecondsRemaining(downUntil)}s";
        }

        return route.FailNext ? "Fails next" : "Up";
    }

    private static string GetPolicyHealthLabel(RouteDefinition route)
    {
        if (route.PolicyDisabled)
        {
            return "Disabled";
        }

        if (route.CooldownUntil is { } cooldownUntil && cooldownUntil > DateTimeOffset.Now)
        {
            return $"Cooling {SecondsRemaining(cooldownUntil)}s";
        }

        return "Eligible";
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
        foreach (RouteDefinition route in ActiveConfiguration.AllRoutes())
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
        string layer,
        string prompt)
    {
        if (route.Purpose.EndsWith("reasoning", StringComparison.Ordinal))
        {
            return $"{route.Name} handled this request on {route.ModelId} with {route.ReasoningEffort} reasoning. " +
                   "The sticky selector chose the model family, then that family's semantic router selected reasoning effort for this turn.";
        }

        if (ContainsAny(prompt, "code", "c#", "bug", "refactor", "function", "service"))
        {
            return $"{route.Name} handled the coding request on {route.ModelId}. " +
                   $"It was invoked through {layer}. The semantic router selected a stable route-family client, " +
                   "and that family owned the model-level resilience behavior.";
        }

        if (ContainsAny(prompt, "write", "poem", "story", "announcement", "creative"))
        {
            return $"{route.Name} handled the creative request on {route.ModelId}. " +
                   "The composition keeps semantic classification outside and compatible-model failover inside the selected family.";
        }

        if (ContainsAny(prompt, "failover", "stream", "terminal", "cooldown", "semantic"))
        {
            return $"{route.Name} answered through {layer}. Semantic routing chooses the route family first. " +
                   "That family can then use ordered or cooldown failover without replacing the semantic profile client.";
        }

        return $"{route.Name} completed this simulated request with {route.ModelId} through {layer}. " +
               "Use the inspector to compare family selection, leaf attempts, and any outer emergency fallback.";
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
        "semantic-composition" => "S+",
        "sticky-reasoning" => "ST",
        "cooldown" => "CD",
        _ => "R2",
    };

    private static string ToTitle(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool UsesSemanticSelection(string policy) =>
        policy is "Semantic" or "StickySemantic";

    private static string FormatBoolean(bool value) => value ? "True" : "False";

    private bool HasValidTree() =>
        Draft.Families.Count >= 1 &&
        (Draft.SelectionPolicy != "Direct" || Draft.Families.Count == 1) &&
        Draft.Families.All(IsValidFamily);

    private static bool IsValidFamily(RouteFamilyDefinition family)
    {
        if (string.IsNullOrWhiteSpace(family.Name))
        {
            return false;
        }

        if (family.SemanticRouter is { } semanticRouter)
        {
            return semanticRouter.Profiles.Count >= 1 &&
                   semanticRouter.Profiles.Count(profile => profile.IsDefault) == 1 &&
                   semanticRouter.Profiles.All(profile =>
                       !string.IsNullOrWhiteSpace(profile.ProfileExamples) &&
                       IsValidFamily(profile));
        }

        return family.Routes.Count >= 1 &&
               (family.ResiliencePolicy != "None" || family.Routes.Count == 1) &&
               family.Routes.All(route =>
                   !string.IsNullOrWhiteSpace(route.Name) &&
                   !string.IsNullOrWhiteSpace(route.ModelId));
    }

    private List<string> GetWarnings()
    {
        var warnings = new List<string>();

        foreach (RouteFamilyDefinition family in Draft.Families.SelectMany(FlattenFamilies))
        {
            if (family.SemanticRouter is not null)
            {
                continue;
            }

            if (family.ResiliencePolicy == "None" && family.Routes.Count > 1)
            {
                warnings.Add($"{family.Name} must remove backup clients or select an ordered/cooldown resilience policy.");
            }

            if (family.MaximumAttempts > family.Routes.Count)
            {
                warnings.Add($"{family.Name} allows more attempts than its number of model clients.");
            }
        }

        if (Draft.AllRoutes().Any(route => route.ReasoningEffort == "high" && route.Temperature > 0.7))
        {
            warnings.Add("A high-reasoning client also has a high temperature. Confirm that its model supports that combination.");
        }

        if (!Draft.GlobalFallbackEnabled)
        {
            warnings.Add("No outer emergency fallback is configured; an exhausted selected family ends the request.");
        }
        else if (string.IsNullOrWhiteSpace(Draft.GlobalFallback.Name) ||
                 string.IsNullOrWhiteSpace(Draft.GlobalFallback.ModelId))
        {
            warnings.Add("The outer emergency fallback needs both a client name and model ID.");
        }

        if (Draft.SelectionPolicy == "Direct" && Draft.Families.Count != 1)
        {
            warnings.Add("Direct selection requires exactly one route family.");
        }

        return warnings;
    }

    private static string GetCompositionLabel(PipelineConfiguration configuration)
    {
        string inner = configuration.SelectionPolicy switch
        {
            "StickySemantic" => "Sticky semantic families",
            "Semantic" => "Semantic families",
            _ => "Direct family",
        };
        return configuration.GlobalFallbackEnabled ? $"Outer failover / {inner}" : inner;
    }

    private static string GetFamilyTargetType(RouteFamilyDefinition family) => family.ResiliencePolicy switch
    {
        _ when family.SemanticRouter is not null => "SemanticRoutingChatClient",
        "Ordered" => "OrderedFailoverChatClient",
        "Cooldown" => "CooldownFailoverChatClient",
        _ => "configured IChatClient",
    };

    private static IEnumerable<RouteFamilyDefinition> FlattenFamilies(RouteFamilyDefinition family)
    {
        yield return family;
        if (family.SemanticRouter is not null)
        {
            foreach (RouteFamilyDefinition profile in family.SemanticRouter.Profiles)
            {
                foreach (RouteFamilyDefinition descendant in FlattenFamilies(profile))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static string GetResilienceDisplayName(string policy) =>
        policy == "None" ? "Single" : policy;

    private static string GetInitialActiveNodeKey(PipelineConfiguration configuration)
    {
        RouteFamilyDefinition family = configuration.Families[0];
        if (family.SemanticRouter is { Profiles.Count: > 0 } semanticRouter)
        {
            RouteFamilyDefinition profile = semanticRouter.Profiles[0];
            return $"subroute:{family.Id}:{profile.Id}:{profile.Routes[0].Id}";
        }

        return $"route:{family.Id}:{family.Routes[0].Id}";
    }

    private static RouteLocation? ResolveRouteLocation(
        PipelineConfiguration configuration,
        string nodeKey)
    {
        string[] parts = nodeKey.Split(':');
        if (parts.Length >= 2 && parts[0] == "global" &&
            configuration.GlobalFallbackEnabled &&
            configuration.GlobalFallback.Id == parts[1])
        {
            return new RouteLocation(
                string.Empty,
                "Outer fallback",
                configuration.GlobalFallback,
                IsGlobalFallback: true);
        }

        if (parts.Length >= 3 && parts[0] == "route")
        {
            RouteFamilyDefinition? family =
                configuration.Families.FirstOrDefault(candidate => candidate.Id == parts[1]);
            RouteDefinition? route =
                family?.Routes.FirstOrDefault(candidate => candidate.Id == parts[2]);
            if (family is not null && route is not null)
            {
                return new RouteLocation(family.Id, family.Name, route, IsGlobalFallback: false);
            }
        }

        if (parts.Length >= 4 && parts[0] == "subroute")
        {
            RouteFamilyDefinition? outerFamily =
                configuration.Families.FirstOrDefault(candidate => candidate.Id == parts[1]);
            RouteFamilyDefinition? profile =
                outerFamily?.SemanticRouter?.Profiles.FirstOrDefault(candidate => candidate.Id == parts[2]);
            RouteDefinition? route =
                profile?.Routes.FirstOrDefault(candidate => candidate.Id == parts[3]);
            if (outerFamily is not null && profile is not null && route is not null)
            {
                return new RouteLocation(
                    profile.Id,
                    $"{outerFamily.Name} / {profile.Name}",
                    route,
                    IsGlobalFallback: false);
            }
        }

        return null;
    }

    private static RouteDefinition? FindRouteById(
        PipelineConfiguration configuration,
        string routeId) =>
        configuration.AllRoutes().FirstOrDefault(route => route.Id == routeId);

    private static string BuildCodePreview(PipelineConfiguration configuration)
    {
        var builder = new StringBuilder();
        foreach (RouteDefinition route in configuration.AllRoutes())
        {
            string variable = ToVariableName(route.Name);
            builder.AppendLine($"IChatClient {variable} = openAI.GetChatClient(\"{route.ModelId}\")");
            builder.AppendLine("    .AsIChatClient()");
            builder.AppendLine("    .AsBuilder()");
            builder.AppendLine("    .ConfigureOptions(options =>");
            builder.AppendLine($"        options.Reasoning = new() {{ Effort = ReasoningEffort.{ToTitle(route.ReasoningEffort)} }})");
            builder.AppendLine("    .Build();");
            builder.AppendLine();
        }

        foreach (RouteFamilyDefinition family in configuration.Families)
        {
            AppendFamilyClientCode(builder, family, parentName: null);
        }

        string selectorVariable;
        if (UsesSemanticSelection(configuration.SelectionPolicy))
        {
            builder.AppendLine(configuration.SelectionPolicy == "StickySemantic"
                ? "using var selector = new StickySemanticRoutingChatClient("
                : "using var selector = new SemanticRoutingChatClient(");
            builder.AppendLine("    embeddings,");
            builder.AppendLine("    new Dictionary<IChatClient, IReadOnlyList<string>>");
            builder.AppendLine("    {");
            foreach (RouteFamilyDefinition family in configuration.Families.Where(family => !family.IsDefault))
            {
                builder.AppendLine($"        [{GetFamilyVariableName(family, null)}] = [/* {family.ProfileExamples} */],");
            }

            builder.AppendLine("    },");
            RouteFamilyDefinition defaultFamily =
                configuration.Families.FirstOrDefault(family => family.IsDefault) ??
                configuration.Families[^1];
            builder.AppendLine($"    defaultClient: {GetFamilyVariableName(defaultFamily, null)},");
            builder.AppendLine($"    scoreThreshold: {configuration.ScoreThreshold:0.00}f,");
            if (configuration.SelectionPolicy == "StickySemantic")
            {
                builder.AppendLine($"    topK: {configuration.TopK},");
                builder.AppendLine("    cache,");
                builder.AppendLine("    sessionIdPropertyName: \"routing-session-id\");");
            }
            else
            {
                builder.AppendLine($"    topK: {configuration.TopK});");
            }

            selectorVariable = "selector";
        }
        else
        {
            selectorVariable = GetFamilyVariableName(configuration.Families[0], null);
        }

        if (configuration.GlobalFallbackEnabled)
        {
            builder.AppendLine();
            builder.AppendLine($"using var pipeline = new OrderedFailoverChatClient(");
            builder.AppendLine($"    [{selectorVariable}, {ToVariableName(configuration.GlobalFallback.Name)}]);");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine($"IChatClient pipeline = {selectorVariable};");
        }

        return builder.ToString().TrimEnd();
    }

    private static string AppendFamilyClientCode(
        StringBuilder builder,
        RouteFamilyDefinition family,
        string? parentName)
    {
        string familyVariable = GetFamilyVariableName(family, parentName);
        if (family.SemanticRouter is { } semanticRouter)
        {
            foreach (RouteFamilyDefinition profile in semanticRouter.Profiles)
            {
                AppendFamilyClientCode(builder, profile, family.Name);
            }

            builder.AppendLine($"using IChatClient {familyVariable} = new SemanticRoutingChatClient(");
            builder.AppendLine("    embeddings,");
            builder.AppendLine("    new Dictionary<IChatClient, IReadOnlyList<string>>");
            builder.AppendLine("    {");
            foreach (RouteFamilyDefinition profile in semanticRouter.Profiles.Where(profile => !profile.IsDefault))
            {
                builder.AppendLine(
                    $"        [{GetFamilyVariableName(profile, family.Name)}] = [/* {profile.ProfileExamples} */],");
            }

            RouteFamilyDefinition defaultProfile =
                semanticRouter.Profiles.FirstOrDefault(profile => profile.IsDefault) ??
                semanticRouter.Profiles[^1];
            builder.AppendLine("    },");
            builder.AppendLine($"    defaultClient: {GetFamilyVariableName(defaultProfile, family.Name)},");
            builder.AppendLine($"    scoreThreshold: {semanticRouter.ScoreThreshold:0.00}f,");
            builder.AppendLine($"    topK: {semanticRouter.TopK});");
            builder.AppendLine();
            return familyVariable;
        }

        string candidates = string.Join(", ", family.Routes.Select(route => ToVariableName(route.Name)));
        switch (family.ResiliencePolicy)
        {
            case "Ordered":
                builder.AppendLine($"using IChatClient {familyVariable} =");
                builder.AppendLine($"    new OrderedFailoverChatClient([{candidates}]);");
                break;
            case "Cooldown":
                builder.AppendLine($"using IChatClient {familyVariable} =");
                builder.AppendLine($"    new CooldownFailoverChatClient([{candidates}],");
                builder.AppendLine($"        TimeSpan.FromSeconds({family.CooldownSeconds}));");
                break;
            default:
                builder.AppendLine($"IChatClient {familyVariable} = {ToVariableName(family.Routes[0].Name)};");
                break;
        }

        builder.AppendLine();
        return familyVariable;
    }

    private static string GetFamilyVariableName(RouteFamilyDefinition family, string? parentName) =>
        $"{ToVariableName(parentName is null ? family.Name : $"{parentName} {family.Name}")}Route";

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

    private static PipelineConfiguration CreateSemanticCompositionScenario() =>
        new()
        {
            ScenarioId = "semantic-composition",
            ScenarioName = "Semantic route families",
            SelectionPolicy = "Semantic",
            ScoreThreshold = 0.35,
            TopK = 3,
            ScoreAggregation = "Mean",
            GlobalFallbackEnabled = true,
            Families =
            [
                CreateFamily(
                    "coding",
                    "Programming",
                    "code, bug, refactor, function, csharp, dependency injection, api",
                    "Ordered",
                    [
                        CreateRoute(
                            "coding-primary",
                            "Primary coding model",
                            "gpt-5.5",
                            "high",
                            0.2,
                            "You are a precise programming assistant. Prefer concrete code and clear tradeoffs."),
                        CreateRoute(
                            "coding-backup",
                            "Backup coding model",
                            "gpt-5.4",
                            "medium",
                            0.2,
                            "You are a concise programming assistant and coding fallback."),
                    ]),
                CreateFamily(
                    "creative",
                    "Writing",
                    "write, poem, story, brainstorm, names, announcement, creative",
                    "Cooldown",
                    [
                        CreateRoute(
                            "creative-primary",
                            "Primary creative model",
                            "gpt-5.4",
                            "low",
                            0.8,
                            "You are a concise, vivid creative partner."),
                        CreateRoute(
                            "creative-backup",
                            "Backup creative model",
                            "gpt-5.4-mini",
                            "low",
                            0.7,
                            "You are a quick creative fallback."),
                    ],
                    cooldownSeconds: 30),
                CreateFamily(
                    "general",
                    "Default",
                    "general questions, everyday help",
                    "None",
                    [
                        CreateRoute(
                            "general",
                            "General model",
                            "gpt-4o-mini",
                            "low",
                            0.3,
                            "You are a helpful general-purpose assistant."),
                    ],
                    isDefault: true),
            ],
            GlobalFallback = CreateRoute(
                "global-emergency",
                "Whole-pipeline fallback",
                "gpt-4o-mini",
                "low",
                0.2,
                "Provide a concise response when the selected route family is unavailable."),
        };

    private static PipelineConfiguration CreateStickyReasoningScenario() =>
        new()
        {
            ScenarioId = "sticky-reasoning",
            ScenarioName = "Sticky model and reasoning routing",
            SelectionPolicy = "StickySemantic",
            ScoreThreshold = 0.35,
            TopK = 3,
            ScoreAggregation = "Mean",
            GlobalFallbackEnabled = false,
            Families =
            [
                CreateSemanticModelFamily(
                    "simple",
                    "Simple tasks",
                    "simple task, quick answer, short summary, rewrite, routine request, straightforward question",
                    "gpt-5.4-mini"),
                CreateSemanticModelFamily(
                    "balanced",
                    "Balanced tasks",
                    "balanced task, moderate task, explain a concept, compare options, make a plan, general analysis",
                    "gpt-5.4",
                    isDefault: true),
                CreateSemanticModelFamily(
                    "complex",
                    "Complex tasks",
                    "complex task, difficult debugging, architecture design, deep analysis, multi-step problem, hard problem",
                    "gpt-5.5"),
            ],
        };

    private static RouteFamilyDefinition CreateSemanticModelFamily(
        string name,
        string purpose,
        string profileExamples,
        string modelId,
        bool isDefault = false) =>
        new()
        {
            Name = name,
            Purpose = purpose,
            ProfileExamples = profileExamples,
            IsDefault = isDefault,
            SemanticRouter = new SemanticRouterDefinition
            {
                Name = $"{modelId} SemanticRoutingChatClient",
                ScoreThreshold = 0.35,
                TopK = 3,
                ScoreAggregation = "Mean",
                Profiles =
                [
                    CreateFamily(
                        "low",
                        "Low reasoning",
                        "low reasoning, brief answer, answer directly, minimal analysis, concise response, quick response",
                        "None",
                        [
                            CreateRoute(
                                $"{modelId}-low",
                                $"{modelId} low reasoning",
                                modelId,
                                "low",
                                0.2,
                                "Answer directly with minimal reasoning overhead."),
                        ]),
                    CreateFamily(
                        "medium",
                        "Medium reasoning",
                        "medium reasoning, explain reasoning, compare tradeoffs, step by step, balanced analysis, make a plan",
                        "None",
                        [
                            CreateRoute(
                                $"{modelId}-medium",
                                $"{modelId} medium reasoning",
                                modelId,
                                "medium",
                                0.2,
                                "Use balanced reasoning and explain the important steps."),
                        ],
                        isDefault: true),
                    CreateFamily(
                        "high",
                        "High reasoning",
                        "high reasoning, reason deeply, verify assumptions, exhaustive analysis, difficult proof, careful multi-step reasoning",
                        "None",
                        [
                            CreateRoute(
                                $"{modelId}-high",
                                $"{modelId} high reasoning",
                                modelId,
                                "high",
                                0.2,
                                "Reason deeply, verify assumptions, and check the result."),
                        ]),
                ],
            },
        };

    private static PipelineConfiguration CreateCooldownScenario() =>
        new()
        {
            ScenarioId = "cooldown",
            ScenarioName = "Cooldown route family",
            SelectionPolicy = "Direct",
            GlobalFallbackEnabled = false,
            Families =
            [
                CreateFamily(
                    "request",
                    "All requests",
                    "all requests",
                    "Cooldown",
                    [
                        CreateRoute("primary", "Primary", "gpt-5.5", "high", 0.2, "Handle requests while healthy."),
                        CreateRoute("regional-backup", "Backup", "gpt-5.4", "medium", 0.3, "Handle requests while the primary cools."),
                        CreateRoute("last-resort", "Last resort", "gpt-4o-mini", "low", 0.3, "Provide a concise fallback."),
                    ],
                    cooldownSeconds: 30),
            ],
        };

    private static PipelineConfiguration CreateReasoningScenario() =>
        new()
        {
            ScenarioId = "reasoning",
            ScenarioName = "Reasoning levels",
            SelectionPolicy = "Semantic",
            ScoreThreshold = 0.35,
            TopK = 3,
            ScoreAggregation = "Mean",
            GlobalFallbackEnabled = false,
            Families =
            [
                CreateFamily(
                    "low",
                    "Low reasoning",
                    "quick answer, simple question, short summary, rewrite, routine request",
                    "None",
                    [
                        CreateRoute("low-reasoning", "Low reasoning wrapper", "gpt-5.4", "low", 0.2, "Answer routine requests quickly and directly."),
                    ]),
                CreateFamily(
                    "medium",
                    "Medium reasoning",
                    "explain a concept, compare options, make a plan, general analysis",
                    "None",
                    [
                        CreateRoute("medium-reasoning", "Medium reasoning wrapper", "gpt-5.4", "medium", 0.2, "Use balanced reasoning for general analysis."),
                    ],
                    isDefault: true),
                CreateFamily(
                    "high",
                    "High reasoning",
                    "complex architecture, difficult debugging, deep analysis, multi-step reasoning, hard problem",
                    "None",
                    [
                        CreateRoute("high-reasoning", "High reasoning wrapper", "gpt-5.4", "high", 0.2, "Reason carefully through difficult multi-step requests."),
                    ]),
            ],
        };

    private static RouteFamilyDefinition CreateFamily(
        string name,
        string purpose,
        string profileExamples,
        string resiliencePolicy,
        List<RouteDefinition> routes,
        bool isDefault = false,
        int cooldownSeconds = 30) =>
        new()
        {
            Name = name,
            Purpose = purpose,
            ProfileExamples = profileExamples,
            ResiliencePolicy = resiliencePolicy,
            MaximumAttempts = resiliencePolicy == "None" ? 1 : routes.Count,
            CooldownSeconds = cooldownSeconds,
            Routes = routes,
            IsDefault = isDefault,
        };

    private static RouteDefinition CreateRoute(
        string name,
        string purpose,
        string modelId,
        string reasoningEffort,
        double temperature,
        string instructions) =>
        new()
        {
            Name = name,
            Purpose = purpose,
            ModelId = modelId,
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
            Instructions = instructions,
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
        if (_composerModule is not null)
        {
            try
            {
                await _composerModule.InvokeVoidAsync("detachComposer", "chat-composer");
                await _composerModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _dotNetReference?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed class RequestExecutionContext
    {
        public int AttemptNumber { get; set; }
    }

    private enum InvocationOutcomeKind
    {
        Succeeded,
        Failed,
        Canceled,
    }

    private sealed record InvocationOutcome(
        InvocationOutcomeKind Kind,
        string? Failure = null,
        bool PermanentFailure = false)
    {
        public static InvocationOutcome Succeeded() => new(InvocationOutcomeKind.Succeeded);

        public static InvocationOutcome Failed(string failure, bool permanentFailure = false) =>
            new(InvocationOutcomeKind.Failed, failure, permanentFailure);

        public static InvocationOutcome Canceled() => new(InvocationOutcomeKind.Canceled);
    }

    private sealed record RouteLocation(
        string FamilyId,
        string FamilyName,
        RouteDefinition Route,
        bool IsGlobalFallback);
}

namespace RoutingDemo.Web.Demo.Backend.Infrastructure;

internal sealed class DemoDiagnostics
{
    private RequestScope? _current;

    public RequestDebugState? Current => _current?.Debug;

    public IDisposable BeginRequest(RequestDebugState debug, Func<ValueTask> onChanged)
    {
        RequestScope? previous = _current;
        _current = new RequestScope(debug, onChanged);
        return new ScopeLease(() => _current = previous);
    }

    public async ValueTask AddEventAsync(string kind, string title, string detail)
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.Events.Add(new DebugEventRecord(DateTimeOffset.Now, kind, title, detail));
        await scope.OnChanged().ConfigureAwait(false);
    }

    public int BeginAttempt(string layer, RouteDefinition route)
    {
        if (_current is not { } scope)
        {
            return 0;
        }

        int attemptNumber = scope.Debug.Attempts.Count + 1;
        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Attempt",
            $"Attempt {attemptNumber} started",
            $"Invoking {route.Name} in {layer}."));
        return attemptNumber;
    }

    public async ValueTask CompleteAttemptAsync(AttemptRecord attempt)
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.Attempts.Add(attempt);
        await scope.OnChanged().ConfigureAwait(false);
    }

    public async ValueTask RecordScoresAsync(
        string layer,
        IReadOnlyList<RoutingScore> scores,
        string selectedRoute,
        double selectedScore,
        bool usedDefault)
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.Scores.AddRange(scores);
        RoutingScore? selectedScoreRecord =
            scores.FirstOrDefault(score => score.RouteFamilyName == selectedRoute);
        if (layer == "outer")
        {
            scope.Debug.SelectedRoute = selectedRoute;
            scope.Debug.SelectedFamilyId = selectedScoreRecord?.RouteFamilyId ?? string.Empty;
        }
        else
        {
            scope.Debug.SelectedInnerRoute = selectedRoute;
        }

        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Selection",
            usedDefault ? "Default family selected" : "Semantic threshold cleared",
            usedDefault
                ? $"{layer}: no profile cleared the threshold; using {selectedRoute}."
                : $"{layer}: {selectedRoute} scored {selectedScore:0.00}."));
        await scope.OnChanged().ConfigureAwait(false);
    }

    public async ValueTask SetSelectedRouteAsync(string routeId, string routeName, string detail)
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.SelectedFamilyId = routeId;
        scope.Debug.SelectedRoute = routeName;
        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Selection",
            "Route family selected",
            detail));
        await scope.OnChanged().ConfigureAwait(false);
    }

    public async ValueTask MarkCompletedAsync(RouteDefinition route, string layer)
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.FinalRoute = route.Name;
        scope.Debug.FinalRouteId = route.Id;
        scope.Debug.ConfiguredModel = route.ModelId;
        scope.Debug.ActualModel = route.ModelId;
        scope.Debug.Outcome = "Completed";
        scope.Debug.FinishReason = "Stop";
        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Response",
            "Response completed",
            $"{route.Name} completed through {layer} with {route.ModelId}."));
        await scope.OnChanged().ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(Exception exception)
    {
        if (_current is not { } scope)
        {
            return;
        }

        if (scope.Debug.Attempts.Count > 0)
        {
            int index = scope.Debug.Attempts.Count - 1;
            scope.Debug.Attempts[index] = scope.Debug.Attempts[index] with { IsTerminal = true };
        }

        scope.Debug.FinalRoute = "None";
        scope.Debug.FinalRouteId = string.Empty;
        scope.Debug.ActualModel = "None";
        scope.Debug.Outcome = "Failed";
        scope.Debug.FinishReason = "Error";
        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Failure",
            "Pipeline exhausted",
            exception.Message));
        await scope.OnChanged().ConfigureAwait(false);
    }

    public async ValueTask MarkCanceledAsync()
    {
        if (_current is not { } scope)
        {
            return;
        }

        scope.Debug.Outcome = "Canceled";
        scope.Debug.FinishReason = "Canceled";
        scope.Debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Canceled",
            "Request canceled",
            "Cancellation is terminal and no routing layer reselects."));
        await scope.OnChanged().ConfigureAwait(false);
    }

    private sealed record RequestScope(RequestDebugState Debug, Func<ValueTask> OnChanged);

    private sealed class ScopeLease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

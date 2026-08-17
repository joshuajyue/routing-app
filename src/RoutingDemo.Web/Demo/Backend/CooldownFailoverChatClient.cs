using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend;

/// <summary>
/// Tries clients in order while skipping clients that were placed into a cross-request cooldown
/// after a pre-output failure.
/// </summary>
internal sealed class CooldownFailoverChatClient : FailoverChatClient
{
    private readonly TimeSpan _cooldown;
    private readonly IChatClient[] _clients;
    private readonly DemoDiagnostics _diagnostics;
    private readonly RouteDefinition[] _routes;
    private readonly ConcurrentDictionary<RoutingContext, CooldownState> _states = new();
    private bool _disposed;

    public CooldownFailoverChatClient(
        IReadOnlyList<IChatClient> clients,
        IReadOnlyList<RouteDefinition> routes,
        TimeSpan cooldown,
        int maximumAttempts,
        DemoDiagnostics diagnostics)
    {
        _clients = clients.ToArray();
        _routes = routes.ToArray();
        _cooldown = cooldown;
        _diagnostics = diagnostics;
        MaximumAttemptsPerRequest = maximumAttempts;
    }

    protected override ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        CooldownState state = _states.GetOrAdd(context, static _ => new CooldownState());
        DateTimeOffset now = DateTimeOffset.Now;
        while (state.NextIndex < _clients.Length && !IsEligible(_routes[state.NextIndex], now))
        {
            RouteDefinition skipped = _routes[state.NextIndex++];
            _ = _diagnostics.AddEventAsync(
                "Policy",
                "Cooling model skipped",
                $"{skipped.Name} is not eligible for this request.");
        }

        if (state.NextIndex >= _clients.Length)
        {
            _states.TryRemove(context, out _);
            throw new InvalidOperationException("No cooldown route is currently eligible.");
        }

        state.SelectedIndex = state.NextIndex;
        return new ValueTask<IChatClient>(_clients[state.NextIndex]);
    }

    protected override async ValueTask OnRoutingUpdateAsync(
        RoutingContext context,
        FailoverChatClientAttempt attempt,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        CooldownState state = _states[context];
        RouteDefinition route = _routes[state.SelectedIndex];

        if (attempt.ResponseCompleted)
        {
            _states.TryRemove(context, out _);
            route.CooldownUntil = null;
            route.PolicyDisabled = false;
            route.ConsecutiveFailures = 0;
            route.LastFailure = null;
            return;
        }

        if (attempt.Exception is not null && !attempt.OutputCommitted)
        {
            route.ConsecutiveFailures++;
            route.LastFailure = attempt.Exception.Message;
            if (route.DownUntilRevived)
            {
                route.PolicyDisabled = true;
            }
            else
            {
                route.CooldownUntil = DateTimeOffset.Now.Add(_cooldown);
            }
        }

        if (!isTerminal)
        {
            state.NextIndex = state.SelectedIndex + 1;
            return;
        }

        _states.TryRemove(context, out _);
        await ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            foreach (IChatClient client in _clients)
            {
                client.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static bool IsEligible(RouteDefinition route, DateTimeOffset now) =>
        !route.PolicyDisabled &&
        (route.CooldownUntil is not { } cooldownUntil || cooldownUntil <= now);

    private sealed class CooldownState
    {
        public int NextIndex { get; set; }

        public int SelectedIndex { get; set; }
    }
}

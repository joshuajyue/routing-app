using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace RoutingDemo.Web.Demo.Backend;

/// <summary>
/// Selects a route semantically for an unpinned application session, then caches that route
/// only after the selected client completes successfully.
/// </summary>
internal sealed class StickySemanticRoutingChatClient : FailoverChatClient
{
    private const string SessionIdPropertyName = "routing-session-id";
    private readonly IDistributedCache _cache;
    private readonly DemoDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<RoutingContext, string> _pending = new();
    private readonly IReadOnlyDictionary<string, IChatClient> _routes;
    private readonly IReadOnlyDictionary<string, RouteFamilyDefinition> _routeDefinitions;
    private readonly SemanticProfileSelector _selector;
    private bool _disposed;

    public StickySemanticRoutingChatClient(
        IReadOnlyDictionary<string, IChatClient> routes,
        IReadOnlyDictionary<string, RouteFamilyDefinition> routeDefinitions,
        SemanticProfileSelector selector,
        IDistributedCache cache,
        DemoDiagnostics diagnostics)
    {
        _routes = routes;
        _routeDefinitions = routeDefinitions;
        _selector = selector;
        _cache = cache;
        _diagnostics = diagnostics;
        MaximumAttemptsPerRequest = 1;
    }

    public string? PinnedRouteId { get; private set; }

    public async Task ClearPinAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(CacheKey(sessionId), cancellationToken).ConfigureAwait(false);
        PinnedRouteId = null;
    }

    protected override async ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        string sessionId = GetSessionId(context);
        string? routeName =
            await _cache.GetStringAsync(CacheKey(sessionId), cancellationToken).ConfigureAwait(false);

        if (routeName is not null && _routes.ContainsKey(routeName))
        {
            RouteFamilyDefinition route = _routeDefinitions[routeName];
            PinnedRouteId = route.Id;
            await _diagnostics.SetSelectedRouteAsync(
                route.Id,
                route.Name,
                $"Sticky cache hit for application session {sessionId}.").ConfigureAwait(false);
            await _diagnostics.AddEventAsync(
                "Selection",
                "Sticky cache hit",
                $"Session key {sessionId} reused the pinned {route.Name} route.").ConfigureAwait(false);
        }
        else
        {
            await _diagnostics.AddEventAsync(
                "Selection",
                "Sticky cache miss",
                $"Session key {sessionId} has no pin; running semantic classification.").ConfigureAwait(false);
            RouteFamilyDefinition selected =
                await _selector.SelectAsync(context.Messages, cancellationToken).ConfigureAwait(false);
            routeName = selected.Name;
            await _diagnostics.SetSelectedRouteAsync(
                selected.Id,
                selected.Name,
                $"{selected.Name} was selected for the unpinned session.").ConfigureAwait(false);
        }

        _pending[context] = routeName;
        return _routes[routeName];
    }

    protected override async ValueTask OnRoutingUpdateAsync(
        RoutingContext context,
        FailoverChatClientAttempt attempt,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(context, out string? routeName) || !attempt.ResponseCompleted)
        {
            return;
        }

        string sessionId = GetSessionId(context);
        await _cache.SetStringAsync(
            CacheKey(sessionId),
            routeName,
            cancellationToken).ConfigureAwait(false);
        RouteFamilyDefinition route = _routeDefinitions[routeName];
        PinnedRouteId = route.Id;
        await _diagnostics.AddEventAsync(
            "Selection",
            "Session route pinned",
            $"Cached {route.Name} under application session key {sessionId} after the response completed.")
            .ConfigureAwait(false);
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
            foreach (IChatClient client in _routes.Values)
            {
                client.Dispose();
            }

            _selector.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string GetSessionId(RoutingContext context)
    {
        if (context.ChatOptions?.AdditionalProperties?.TryGetValue(
                SessionIdPropertyName,
                out object? value) == true &&
            value is string sessionId &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId;
        }

        throw new InvalidOperationException("A routing session ID is required.");
    }

    private static string CacheKey(string sessionId) => $"chat-route:{sessionId}";
}

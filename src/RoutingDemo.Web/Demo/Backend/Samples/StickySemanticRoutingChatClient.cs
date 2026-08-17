using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace RoutingDemo.Web.Demo.Backend.Samples;

/// <summary>
/// Selects a route for an unpinned application session and caches that route only after the
/// selected client completes successfully.
/// </summary>
public sealed class StickySemanticRoutingChatClient : FailoverChatClient
{
    private readonly IDistributedCache _cache;
    private readonly string _cacheKeyPrefix;
    private readonly bool _leaveOpen;
    private readonly ConcurrentDictionary<RoutingContext, string> _pending = new();
    private readonly Func<RoutingContext, CancellationToken, ValueTask<string>> _routeSelector;
    private readonly IReadOnlyDictionary<string, IChatClient> _routes;
    private readonly string _sessionIdPropertyName;
    private bool _disposed;

    public StickySemanticRoutingChatClient(
        IReadOnlyDictionary<string, IChatClient> routes,
        Func<RoutingContext, CancellationToken, ValueTask<string>> routeSelector,
        IDistributedCache cache,
        string sessionIdPropertyName = "routing-session-id",
        string cacheKeyPrefix = "chat-route:",
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(routeSelector);
        ArgumentNullException.ThrowIfNull(cache);
        if (routes.Count == 0 || routes.Any(route => string.IsNullOrWhiteSpace(route.Key) || route.Value is null))
        {
            throw new ArgumentException("At least one named, non-null route is required.", nameof(routes));
        }

        _routes = new Dictionary<string, IChatClient>(routes, StringComparer.Ordinal);
        _routeSelector = routeSelector;
        _cache = cache;
        _sessionIdPropertyName = sessionIdPropertyName;
        _cacheKeyPrefix = cacheKeyPrefix;
        _leaveOpen = leaveOpen;
        MaximumAttemptsPerRequest = 1;
    }

    public Task<string?> GetPinnedRouteAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _cache.GetStringAsync(CacheKey(sessionId), cancellationToken);

    public Task ClearPinnedRouteAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(CacheKey(sessionId), cancellationToken);

    protected override async ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        string sessionId = GetSessionId(context);
        string? routeName =
            await _cache.GetStringAsync(CacheKey(sessionId), cancellationToken).ConfigureAwait(false);
        routeName ??= await _routeSelector(context, cancellationToken).ConfigureAwait(false);

        if (!_routes.TryGetValue(routeName, out IChatClient? client))
        {
            throw new InvalidOperationException($"The route selector returned unknown route '{routeName}'.");
        }

        _pending[context] = routeName;
        return client;
    }

    protected override async ValueTask OnRoutingUpdateAsync(
        RoutingContext context,
        FailoverChatClientAttempt attempt,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        if (_pending.TryRemove(context, out string? routeName) && attempt.ResponseCompleted)
        {
            await _cache.SetStringAsync(
                CacheKey(GetSessionId(context)),
                routeName,
                cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing && !_leaveOpen)
        {
            foreach (IChatClient client in _routes.Values)
            {
                client.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private string GetSessionId(RoutingContext context)
    {
        if (context.ChatOptions?.AdditionalProperties?.TryGetValue(
                _sessionIdPropertyName,
                out object? value) == true &&
            value is string sessionId &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId;
        }

        throw new InvalidOperationException(
            $"A string '{_sessionIdPropertyName}' additional property is required.");
    }

    private string CacheKey(string sessionId) => $"{_cacheKeyPrefix}{sessionId}";
}

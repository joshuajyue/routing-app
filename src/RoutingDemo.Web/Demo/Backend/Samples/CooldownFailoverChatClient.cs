using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend.Samples;

/// <summary>
/// Tries clients in order and temporarily skips a client after an uncanceled pre-output failure.
/// </summary>
public sealed class CooldownFailoverChatClient : FailoverChatClient
{
    private readonly ConcurrentDictionary<IChatClient, DateTimeOffset> _cooldowns =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeSpan _cooldownDuration;
    private readonly IChatClient[] _clients;
    private readonly bool _leaveOpen;
    private readonly ConcurrentDictionary<RoutingContext, RequestState> _requests = new();
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public CooldownFailoverChatClient(
        IReadOnlyList<IChatClient> clients,
        TimeSpan cooldownDuration,
        int? maximumAttemptsPerRequest = null,
        TimeProvider? timeProvider = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (clients.Count == 0 || clients.Any(client => client is null))
        {
            throw new ArgumentException("At least one non-null client is required.", nameof(clients));
        }

        if (cooldownDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldownDuration));
        }

        _clients = clients.ToArray();
        _cooldownDuration = cooldownDuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaveOpen = leaveOpen;
        MaximumAttemptsPerRequest = maximumAttemptsPerRequest;
    }

    public DateTimeOffset? GetCooldownUntil(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_cooldowns.TryGetValue(client, out DateTimeOffset expiresAt))
        {
            return null;
        }

        if (expiresAt > _timeProvider.GetUtcNow())
        {
            return expiresAt;
        }

        _cooldowns.TryRemove(client, out _);
        return null;
    }

    public void Reset(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _cooldowns.TryRemove(client, out _);
    }

    protected override ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        RequestState state = _requests.GetOrAdd(context, static _ => new RequestState());
        int selectedIndex = FindNextEligibleIndex(state.NextIndex);
        if (selectedIndex < 0)
        {
            _requests.TryRemove(context, out _);
            throw new InvalidOperationException("No client is currently eligible.");
        }

        state.SelectedIndex = selectedIndex;
        return new ValueTask<IChatClient>(_clients[selectedIndex]);
    }

    protected override ValueTask OnRoutingUpdateAsync(
        RoutingContext context,
        FailoverChatClientAttempt attempt,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        RequestState state = _requests[context];

        if (attempt.ResponseCompleted)
        {
            _cooldowns.TryRemove(attempt.Client, out _);
            _requests.TryRemove(context, out _);
            return default;
        }

        if (attempt.Exception is not null && !attempt.OutputCommitted)
        {
            _cooldowns[attempt.Client] = _timeProvider.GetUtcNow().Add(_cooldownDuration);
        }

        if (isTerminal)
        {
            _requests.TryRemove(context, out _);
            return default;
        }

        int nextIndex = FindNextEligibleIndex(state.SelectedIndex + 1);
        if (nextIndex < 0)
        {
            _requests.TryRemove(context, out _);
            ExceptionDispatchInfo.Capture(attempt.Exception!).Throw();
        }

        state.NextIndex = nextIndex;
        return default;
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
            foreach (IChatClient client in _clients)
            {
                client.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private int FindNextEligibleIndex(int startIndex)
    {
        for (int index = startIndex; index < _clients.Length; index++)
        {
            if (GetCooldownUntil(_clients[index]) is null)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class RequestState
    {
        public int NextIndex { get; set; }

        public int SelectedIndex { get; set; }
    }
}

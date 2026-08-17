using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using RoutingDemo.Web.Demo.Backend.Samples;

namespace RoutingDemo.Web.Demo.Backend.Infrastructure;

public sealed class DemoPipelineRuntime : IDisposable
{
    private readonly PipelineConfiguration _configuration;
    private readonly IReadOnlyList<CooldownBinding> _cooldownBindings;
    private readonly DemoDiagnostics _diagnostics;
    private readonly List<ChatMessage> _history = [];
    private readonly IChatClient _root;
    private readonly string _sessionId;
    private readonly StickySemanticRoutingChatClient? _stickyClient;
    private readonly IReadOnlyDictionary<string, RouteFamilyDefinition> _stickyRouteDefinitions;
    private readonly IReadOnlyList<IDisposable> _ownedResources;
    private string? _pinnedRouteId;
    private bool _disposed;

    internal DemoPipelineRuntime(
        IChatClient root,
        PipelineConfiguration configuration,
        string sessionId,
        DemoDiagnostics diagnostics,
        StickySemanticRoutingChatClient? stickyClient,
        IReadOnlyDictionary<string, RouteFamilyDefinition> stickyRouteDefinitions,
        IReadOnlyList<CooldownBinding> cooldownBindings,
        IReadOnlyList<IDisposable> ownedResources)
    {
        _root = root;
        _configuration = configuration;
        _sessionId = sessionId;
        _diagnostics = diagnostics;
        _stickyClient = stickyClient;
        _stickyRouteDefinitions = stickyRouteDefinitions;
        _cooldownBindings = cooldownBindings;
        _ownedResources = ownedResources;
    }

    public string? PinnedRouteId => _pinnedRouteId;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        string prompt,
        RequestDebugState debug,
        Func<ValueTask> onChanged,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var userMessage = new ChatMessage(ChatRole.User, prompt);
        _history.Add(userMessage);
        debug.SelectionPolicy = _configuration.SelectionPolicy;
        debug.InputTokens = EstimateTokens(prompt);
        debug.Events.Add(new DebugEventRecord(
            DateTimeOffset.Now,
            "Routing",
            "Request started",
            $"Invoking the Microsoft.Extensions.AI pipeline for session {_sessionId}."));

        using IDisposable scope = _diagnostics.BeginRequest(debug, onChanged);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["routing-session-id"] = _sessionId,
            },
        };
        var responseText = new System.Text.StringBuilder();
        bool completed = false;
        string? pinnedRouteBeforeRequest = null;

        try
        {
            if (_stickyClient is not null)
            {
                pinnedRouteBeforeRequest =
                    await _stickyClient.GetPinnedRouteAsync(_sessionId, cancellationToken).ConfigureAwait(false);
                if (pinnedRouteBeforeRequest is not null &&
                    _stickyRouteDefinitions.TryGetValue(
                        pinnedRouteBeforeRequest,
                        out RouteFamilyDefinition? pinnedDefinition))
                {
                    _pinnedRouteId = pinnedDefinition.Id;
                    await _diagnostics.SetSelectedRouteAsync(
                        pinnedDefinition.Id,
                        pinnedDefinition.Name,
                        $"Sticky cache hit for application session {_sessionId}.").ConfigureAwait(false);
                    await _diagnostics.AddEventAsync(
                        "Selection",
                        "Sticky cache hit",
                        $"Session key {_sessionId} reused the pinned {pinnedDefinition.Name} route.")
                        .ConfigureAwait(false);
                }
                else
                {
                    await _diagnostics.AddEventAsync(
                        "Selection",
                        "Sticky cache miss",
                        $"Session key {_sessionId} has no pin; running semantic classification.")
                        .ConfigureAwait(false);
                }
            }

            if (_configuration.Streaming)
            {
                IAsyncEnumerator<ChatResponseUpdate> enumerator = _root
                    .GetStreamingResponseAsync(_history, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await HandleCancellationAsync(userMessage).ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception exception)
                        {
                            await HandleFailureAsync(userMessage, exception).ConfigureAwait(false);
                            throw;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        ChatResponseUpdate update = enumerator.Current;
                        responseText.Append(update.Text);
                        yield return update;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                ChatResponse response;
                try
                {
                    response =
                        await _root.GetResponseAsync(_history, options, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await HandleCancellationAsync(userMessage).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    await HandleFailureAsync(userMessage, exception).ConfigureAwait(false);
                    throw;
                }

                foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    responseText.Append(update.Text);
                    yield return update;
                }
            }

            _history.Add(new ChatMessage(ChatRole.Assistant, responseText.ToString()));
            debug.OutputTokens = EstimateTokens(responseText.ToString());
            if (_stickyClient is not null)
            {
                string? pinnedRouteAfterRequest =
                    await _stickyClient.GetPinnedRouteAsync(_sessionId, cancellationToken).ConfigureAwait(false);
                if (pinnedRouteAfterRequest is not null &&
                    _stickyRouteDefinitions.TryGetValue(
                        pinnedRouteAfterRequest,
                        out RouteFamilyDefinition? pinnedDefinition))
                {
                    _pinnedRouteId = pinnedDefinition.Id;
                    if (pinnedRouteBeforeRequest is null)
                    {
                        await _diagnostics.AddEventAsync(
                            "Selection",
                            "Session route pinned",
                            $"Cached {pinnedDefinition.Name} under application session key {_sessionId}.")
                            .ConfigureAwait(false);
                    }
                }
            }

            SynchronizePolicyState();
            completed = true;
        }
        finally
        {
            if (!completed && _history.Contains(userMessage))
            {
                _history.Remove(userMessage);
            }
        }
    }

    public void ClearConversation() => _history.Clear();

    public async Task ClearStickyPinAsync(CancellationToken cancellationToken = default)
    {
        if (_stickyClient is not null)
        {
            await _stickyClient.ClearPinnedRouteAsync(_sessionId, cancellationToken).ConfigureAwait(false);
            _pinnedRouteId = null;
        }
    }

    public void SynchronizePolicyState()
    {
        foreach (CooldownBinding binding in _cooldownBindings)
        {
            binding.Route.CooldownUntil = binding.Policy.GetCooldownUntil(binding.Client);
        }
    }

    public void ResetPolicyState(string routeId)
    {
        foreach (CooldownBinding binding in _cooldownBindings.Where(
            binding => binding.Route.Id == routeId))
        {
            binding.Policy.Reset(binding.Client);
            binding.Route.CooldownUntil = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _root.Dispose();
        foreach (IDisposable resource in _ownedResources)
        {
            resource.Dispose();
        }
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling(
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.35));

    private async Task HandleCancellationAsync(ChatMessage userMessage)
    {
        _history.Remove(userMessage);
        await _diagnostics.MarkCanceledAsync().ConfigureAwait(false);
    }

    private async Task HandleFailureAsync(ChatMessage userMessage, Exception exception)
    {
        _history.Remove(userMessage);
        await _diagnostics.MarkFailedAsync(exception).ConfigureAwait(false);
    }
}

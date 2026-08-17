using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend;

public sealed class DemoPipelineRuntime : IDisposable
{
    private readonly PipelineConfiguration _configuration;
    private readonly DemoDiagnostics _diagnostics;
    private readonly List<ChatMessage> _history = [];
    private readonly IChatClient _root;
    private readonly string _sessionId;
    private readonly StickySemanticRoutingChatClient? _stickyClient;
    private bool _disposed;

    internal DemoPipelineRuntime(
        IChatClient root,
        PipelineConfiguration configuration,
        string sessionId,
        DemoDiagnostics diagnostics,
        StickySemanticRoutingChatClient? stickyClient)
    {
        _root = root;
        _configuration = configuration;
        _sessionId = sessionId;
        _diagnostics = diagnostics;
        _stickyClient = stickyClient;
    }

    public string? PinnedRouteId => _stickyClient?.PinnedRouteId;

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

        try
        {
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

    public Task ClearStickyPinAsync(CancellationToken cancellationToken = default) =>
        _stickyClient is null
            ? Task.CompletedTask
            : _stickyClient.ClearPinAsync(_sessionId, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _root.Dispose();
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

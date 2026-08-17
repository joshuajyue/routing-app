using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RoutingDemo.Web.Demo.Backend;

internal sealed class MockResponsesChatClient : IChatClient
{
    private readonly DemoDiagnostics _diagnostics;
    private readonly string _layer;
    private readonly MockResponsesApi _responses;
    private readonly RouteDefinition _route;

    public MockResponsesChatClient(
        RouteDefinition route,
        string layer,
        MockResponsesApi responses,
        DemoDiagnostics diagnostics)
    {
        _route = route;
        _layer = layer;
        _responses = responses;
        _diagnostics = diagnostics;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new System.Text.StringBuilder();
        string? modelId = null;
        await foreach (ChatResponseUpdate update in
            GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            text.Append(update.Text);
            modelId ??= update.ModelId;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            ModelId = modelId ?? _route.ModelId,
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int attemptNumber = _diagnostics.BeginAttempt(_layer, _route);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? timeToFirstUpdate = null;
        bool outputCommitted = false;
        bool responseCompleted = false;
        Exception? failure = null;

        try
        {
            try
            {
                ThrowIfUnavailable();
                await Task.Delay(180 + LatencyOffset(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }

            string prompt = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            IReadOnlyList<string> chunks = _responses.Chunk(_responses.CreateResponse(_route, _layer, prompt));
            for (int index = 0; index < chunks.Count; index++)
            {
                try
                {
                    await Task.Delay(42, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                timeToFirstUpdate ??= stopwatch.Elapsed;
                outputCommitted = true;
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunks[index])
                {
                    ModelId = _route.ModelId,
                    FinishReason = index == chunks.Count - 1 ? ChatFinishReason.Stop : null,
                };
            }

            responseCompleted = true;
            try
            {
                await _diagnostics.MarkCompletedAsync(_route, _layer).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
        }
        finally
        {
            stopwatch.Stop();
            string status =
                responseCompleted ? "Completed" :
                failure is OperationCanceledException ? "Canceled" :
                failure is not null ? "Failed" :
                "Abandoned";
            await _diagnostics.CompleteAttemptAsync(new AttemptRecord(
                attemptNumber,
                _layer,
                _route.Name,
                _route.ModelId,
                (int)stopwatch.ElapsedMilliseconds,
                timeToFirstUpdate is { } firstUpdate ? (int)firstUpdate.TotalMilliseconds : null,
                responseCompleted,
                outputCommitted,
                responseCompleted || outputCommitted || failure is OperationCanceledException,
                status,
                failure is null ? null : $"{failure.GetType().Name}: {failure.Message}")).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private void ThrowIfUnavailable()
    {
        if (_route.FailNext)
        {
            _route.FailNext = false;
            throw new HttpRequestException($"{_route.Name} rejected the one-shot mock response (503).");
        }

        if (_route.DownUntilRevived)
        {
            throw new HttpRequestException($"{_route.Name} is down until manually revived (503).");
        }

        if (_route.DownUntil is { } downUntil && downUntil > DateTimeOffset.Now)
        {
            throw new HttpRequestException($"{_route.Name} is unavailable until {downUntil:HH:mm:ss} (503).");
        }
    }

    private int LatencyOffset() => _route.Name.Sum(character => character) % 120;
}

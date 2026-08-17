namespace RoutingDemo.Web.Demo.Backend.Infrastructure;

public sealed class MockResponsesApi
{
    public string CreateResponse(RouteDefinition route, string layer, string prompt)
    {
        if (route.Purpose.EndsWith("reasoning", StringComparison.Ordinal))
        {
            return $"{route.Name} handled this request on {route.ModelId} with {route.ReasoningEffort} reasoning. " +
                   "The outer router selected the model route, and the inner semantic router selected reasoning effort.";
        }

        if (ContainsAny(prompt, "code", "c#", "bug", "refactor", "function", "service"))
        {
            return $"{route.Name} handled the coding request on {route.ModelId} through {layer}. " +
                   "The response came from a mock Responses API behind a real Microsoft.Extensions.AI routing pipeline.";
        }

        if (ContainsAny(prompt, "write", "poem", "story", "announcement", "creative"))
        {
            return $"{route.Name} handled the creative request on {route.ModelId}. " +
                   "Selection and failover ran through Microsoft.Extensions.AI; only the generated response is mocked.";
        }

        return $"{route.Name} completed this mock response with {route.ModelId} through {layer}. " +
               "The routing, failover, sticky state, options, and streaming path are all running on Microsoft.Extensions.AI.";
    }

    public IReadOnlyList<string> Chunk(string response)
    {
        string[] words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        const int wordsPerChunk = 5;
        for (int index = 0; index < words.Length; index += wordsPerChunk)
        {
            int count = Math.Min(wordsPerChunk, words.Length - index);
            string chunk = string.Join(' ', words, index, count);
            chunks.Add(index + count < words.Length ? chunk + " " : chunk);
        }

        return chunks;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

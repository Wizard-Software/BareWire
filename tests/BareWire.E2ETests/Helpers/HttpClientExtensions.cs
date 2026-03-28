using System.Net.Http.Json;
using System.Text.Json;

namespace BareWire.E2ETests.Helpers;

internal static class HttpClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Polls <paramref name="requestUri"/> with GET until <paramref name="predicate"/> returns true
    /// or <paramref name="timeout"/> expires.
    /// </summary>
    internal static async Task<T> PollUntilAsync<T>(
        this HttpClient client,
        string requestUri,
        Func<T, bool> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        pollInterval ??= TimeSpan.FromMilliseconds(500);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var response = await client.GetAsync(requestUri, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cts.Token).ConfigureAwait(false);
            if (result is not null && predicate(result))
            {
                return result;
            }

            await Task.Delay(pollInterval.Value, cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Polling {requestUri} did not satisfy predicate within {timeout}");
    }

    /// <summary>
    /// POST with JSON body and return deserialized response.
    /// </summary>
    internal static async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        this HttpClient client,
        string requestUri,
        TRequest body,
        CancellationToken ct = default)
    {
        var response = await client.PostAsJsonAsync(requestUri, body, JsonOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct).ConfigureAwait(false);
    }
}

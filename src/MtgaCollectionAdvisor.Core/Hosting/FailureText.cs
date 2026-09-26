using System.Text.Json;

namespace MtgaCollectionAdvisor.Core.Hosting;

/// <summary>
/// A network failure the app handles by itself, in a few words for the log file (#74): the HTTP
/// status when there is one, a timeout, or an unreadable answer. The status bar already says that
/// something failed; the log says why, so a report can tell a 429 from a site being down.
/// </summary>
public static class FailureText
{
    public static string Describe(Exception exception)
    {
        // The app wraps failures in its own exceptions; the cause is the innermost one it knows.
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case HttpRequestException { StatusCode: { } status }:
                    return $"HTTP {(int)status} {status}";
                case HttpRequestException http:
                    return $"no connection ({http.HttpRequestError})";
                case TaskCanceledException or TimeoutException:
                    return "timed out";
                case JsonException:
                    return "unreadable answer (not the JSON expected)";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }
}

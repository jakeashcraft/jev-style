using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JevStyle.Decisions;

/// <summary>Turns non-success HTTP responses into <see cref="DecisionServerException"/> with the server's own message.</summary>
internal static class HttpErrors
{
    private const int _maxBodyLength = 2_000;

    public static async Task<DecisionServerException> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Keep the status code; the body is optional.
        }

        if (body.Length > _maxBodyLength)
        {
            body = body[.._maxBodyLength];
        }

        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                detail = ErrorText(error);
            }
        }
        catch (JsonException)
        {
            detail = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        }

        int code = (int)response.StatusCode;
        string message = $"HTTP {code} ({response.ReasonPhrase ?? response.StatusCode.ToString()})" + (detail is null ? string.Empty : $": {detail}");
        return new DecisionServerException(response.StatusCode, message, body, RetryAfter(response, DateTimeOffset.UtcNow));
    }

    /// <summary>Reads <c>retry-after-ms</c> first, then <c>Retry-After</c> (seconds or an HTTP date), like the TypeSafe SDK.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out IEnumerable<string>? values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
            && double.IsFinite(ms)
            && ms >= 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta >= TimeSpan.Zero ? delta : null;
        }

        return retry?.Date is { } date ? (date > now ? date - now : TimeSpan.Zero) : null;
    }

    /// <summary>Accepts both <c>{"error": "text"}</c> (LM Studio) and <c>{"error": {"message": "text"}}</c> (OpenAI, OpenRouter).</summary>
    public static string ErrorText(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.String => error.GetString() ?? string.Empty,
        JsonValueKind.Object when error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String => message.GetString() ?? string.Empty,
        _ => error.GetRawText(),
    };
}

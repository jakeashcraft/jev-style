using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JevStyle.Decisions;

/// <summary>
/// Reads next-token log-probabilities from <c>/v1/chat/completions</c> with <c>max_tokens: 1</c>,
/// <c>temperature: 0</c> and <c>top_logprobs</c>. Works with LM Studio and llama-server; the released
/// GGUF ships a pass-through chat template, so the prompt reaches the model verbatim. LM Studio does not
/// return log-probabilities from <c>/v1/completions</c> or its native <c>/api/v0</c> endpoints, which is why
/// the chat endpoint is used.
/// </summary>
public sealed class OpenAiChatLogprobSource : ILogprobSource
{
    private readonly HttpClient _httpClient;
    private readonly JevStyleClientOptions _options;
    private readonly Uri _endpoint;

    /// <summary>Creates the source for use with dependency injection.</summary>
    /// <param name="httpClient">Client from <c>IHttpClientFactory</c>.</param>
    /// <param name="options">Client settings.</param>
    [ActivatorUtilitiesConstructor]
    public OpenAiChatLogprobSource(HttpClient httpClient, IOptions<JevStyleClientOptions> options)
        : this(httpClient, options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Creates the source without dependency injection.</summary>
    /// <param name="httpClient">A long-lived client. Its timeout should be infinite or longer than your per-request cancellation.</param>
    /// <param name="options">Client settings.</param>
    public OpenAiChatLogprobSource(HttpClient httpClient, JevStyleClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _endpoint = _options.ChatCompletionsUri;
    }

    /// <inheritdoc />
    public int MaxCandidates => _options.TopLogprobs;

    /// <inheritdoc />
    public async Task<NextTokenLogprobs> GetNextTokenLogprobsAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var body = new ChatCompletionRequest(
            _options.Model, [new ChatMessage("user", prompt)], MaxTokens: 1, Temperature: 0, Logprobs: true, TopLogprobs: _options.TopLogprobs, Stream: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, DecisionJsonContext.Default.ChatCompletionRequest),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await HttpErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        ChatCompletionResponse? payload;
        try
        {
            payload = await response.Content
                .ReadFromJsonAsync(DecisionJsonContext.Default.ChatCompletionResponse, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new DecisionFormatException($"The server returned invalid JSON: {exception.Message}");
        }

        return ToLogprobs(payload);
    }

    internal static NextTokenLogprobs ToLogprobs(ChatCompletionResponse? payload)
    {
        if (payload?.Error is { } error && error.ValueKind != JsonValueKind.Null)
        {
            throw new DecisionFormatException($"The server returned an error: {HttpErrors.ErrorText(error)}");
        }

        ChatTokenLogprob? first = payload?.Choices is [{ Logprobs.Content: [var token, ..] }, ..] ? token : null;
        if (first?.TopLogprobs is not { Count: > 0 } top)
        {
            throw new DecisionFormatException(
                "The server returned no top_logprobs for the first token. Use an endpoint that supports logprobs on /v1/chat/completions (LM Studio 0.3.x or later, llama-server).");
        }

        var candidates = new TokenLogprob[top.Count];
        for (int i = 0; i < top.Count; i++)
        {
            candidates[i] = new TokenLogprob(top[i].Token ?? string.Empty, top[i].Logprob);
        }

        TokenUsage? usage = payload!.Usage is { } u ? new TokenUsage(u.PromptTokens, u.CompletionTokens) : null;
        return new NextTokenLogprobs(candidates, usage, payload.Model, payload.Id);
    }
}

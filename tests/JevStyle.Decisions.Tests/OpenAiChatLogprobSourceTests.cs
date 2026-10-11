using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace JevStyle.Decisions.Tests;

public sealed class OpenAiChatLogprobSourceTests
{
    private static readonly JevStyleClientOptions Options = new() { BaseAddress = new Uri("http://lmstudio.test:1234"), Model = "jev-style-qwen3.5-2b-decision" };

    [Fact]
    public async Task Sends_one_token_greedy_request_with_top_logprobs_and_decodes_the_answer()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(FakeHandler.ChatCompletion((" B", -0.2), (" A", -1.8))));
        var client = new DecisionClient(new OpenAiChatLogprobSource(new HttpClient(handler), Options));

        BoolDecision answer = await client.DecideBoolAsync("Premise: A man plays guitar.\nHypothesis: Someone makes music.", "Does the premise entail the hypothesis?");

        (HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://lmstudio.test:1234/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("jev-style-qwen3.5-2b-decision", root.GetProperty("model").GetString());
        Assert.Equal(1, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        Assert.True(root.GetProperty("logprobs").GetBoolean());
        Assert.Equal(20, root.GetProperty("top_logprobs").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.EndsWith("[Options]\nA. yes\nB. no\n\nAnswer:", message.GetProperty("content").GetString());

        Assert.Equal(1 / (1 + Math.Exp(1.6)), answer.Probability, 12);
        Assert.False(answer.Value);
        Assert.Equal(80, answer.Decision.Diagnostics.Usage?.PromptTokens);
        Assert.Equal("jev-style-qwen3.5-2b-decision", answer.Decision.Diagnostics.ServedModel);
    }

    [Fact]
    public async Task Sends_the_api_key_as_a_bearer_token()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(FakeHandler.ChatCompletion((" A", -0.1), (" B", -2.5))));
        var options = new JevStyleClientOptions { BaseAddress = Options.BaseAddress, ApiKey = "secret-token" };
        await new DecisionClient(new OpenAiChatLogprobSource(new HttpClient(handler), options)).DecideAsync("s", "q", ["a", "b"]);
        Assert.Equal("Bearer secret-token", handler.Requests[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Http_errors_carry_the_status_and_the_server_message()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"error":"'top_logprobs' must be an integer between 0 and 20"}""", HttpStatusCode.BadRequest));
        var source = new OpenAiChatLogprobSource(new HttpClient(handler), Options);
        DecisionServerException exception = await Assert.ThrowsAsync<DecisionServerException>(() => source.GetNextTokenLogprobsAsync("prompt"));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("'top_logprobs' must be an integer between 0 and 20", exception.Message);
    }

    [Fact]
    public async Task Openai_style_error_objects_are_unwrapped()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"error":{"message":"model not found","type":"invalid_request_error"}}""", HttpStatusCode.NotFound));
        var source = new OpenAiChatLogprobSource(new HttpClient(handler), Options);
        DecisionServerException exception = await Assert.ThrowsAsync<DecisionServerException>(() => source.GetNextTokenLogprobsAsync("prompt"));
        Assert.Equal("HTTP 404 (Not Found): model not found", exception.Message);
    }

    [Fact]
    public async Task Missing_logprobs_explains_what_the_server_must_support()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"choices":[{"index":0,"message":{"content":" A"},"logprobs":null}]}"""));
        var source = new OpenAiChatLogprobSource(new HttpClient(handler), Options);
        DecisionFormatException exception = await Assert.ThrowsAsync<DecisionFormatException>(() => source.GetNextTokenLogprobsAsync("prompt"));
        Assert.Contains("top_logprobs", exception.Message);
    }

    [Theory]
    [InlineData("http://host:1234", "http://host:1234/v1/chat/completions")]
    [InlineData("http://host:1234/", "http://host:1234/v1/chat/completions")]
    [InlineData("http://host:1234/v1", "http://host:1234/v1/chat/completions")]
    [InlineData("https://proxy.local/lmstudio/v1/", "https://proxy.local/lmstudio/v1/chat/completions")]
    public void Endpoint_accepts_server_roots_with_or_without_v1(string baseAddress, string expected) =>
        Assert.Equal(expected, new JevStyleClientOptions { BaseAddress = new Uri(baseAddress) }.ChatCompletionsUri.ToString());

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiChatLogprobSource(new HttpClient(), new JevStyleClientOptions { Model = " " }));
        Assert.Throws<ArgumentException>(() => new OpenAiChatLogprobSource(new HttpClient(), new JevStyleClientOptions { TopLogprobs = 0 }));
    }

    [Fact]
    public async Task Choices_beyond_the_candidate_count_are_rejected_before_any_request()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("must not be called"));
        var client = new DecisionClient(new OpenAiChatLogprobSource(new HttpClient(handler), new JevStyleClientOptions { TopLogprobs = 3 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.DecideAsync("s", "q", ["a", "b", "c", "d"]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Score_returns_the_expected_level()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(FakeHandler.ChatCompletion((" C", Math.Log(0.5)), (" D", Math.Log(0.5)))));
        var client = new DecisionClient(new OpenAiChatLogprobSource(new HttpClient(handler), Options));
        ScoreDecision score = await client.DecideScoreAsync("s", "Rate it", ["very negative", "negative", "neutral", "positive", "very positive"]);
        // Levels A, B and E are missing, so each gets the floor weight e^-5 relative to C and D.
        double w = Math.Exp(-5);
        Assert.Equal((1 * w + 2 + 3 + 4 * w) / (2 + 3 * w), score.ExpectedLevel, 12);
    }

    [Fact]
    public async Task Dependency_injection_registers_a_working_client()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(FakeHandler.ChatCompletion((" A", -0.05), (" B", -3.0))));
        var services = new ServiceCollection();
        services.AddJevStyleDecisions(o => o.BaseAddress = new Uri("http://injected.test:1234"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Decision decision = await provider.GetRequiredService<IDecisionClient>().DecideAsync("s", "q", ["first", "second"]);

        Assert.Equal("first", decision.Selected.Option);
        Assert.Equal("http://injected.test:1234/v1/chat/completions", handler.Requests[0].Request.RequestUri!.ToString());
    }
}

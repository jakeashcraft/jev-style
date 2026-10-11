using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace JevStyle.Decisions.Tests;

public sealed class DecisionsApiClientTests
{
    private const string NoulResponse = """
        {"model":"microsoft/microsoft-decision-1-20261009","answers":{"expression":{"type":"noul","noul":0.0008040891827022661}},
         "usage":{"input_tokens":233,"output_tokens":1,"cost":0.000009786},"id":"gen-dec-1","provider":"Azure"}
        """;

    private static readonly DecisionsApiOptions Options = new() { Model = "microsoft/microsoft-decision-1", ApiKey = "test-key" };

    [Fact]
    public async Task Sends_the_documented_request_to_openrouter()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(NoulResponse));
        var client = new DecisionsApiClient(new HttpClient(handler), Options);
        var request = new DecisionsApiRequest("{\"A\":true}", new Dictionary<string, DecisionQuestion>
        {
            ["urgent"] = new NoulQuestion("Is it urgent?", "Explicitly time-sensitive", "No urgency expressed"),
            ["team"] = new ChoiceQuestion("Which team?", [new("billing", "Payments"), new("technical", "Bugs")]),
            ["mood"] = new ScoreQuestion("How frustrated?", ["Calm", "Frustrated", "Very angry"]),
        });

        await client.DecideAsync(request);

        (HttpRequestMessage message, string body) = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", message.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", message.Headers.Authorization?.ToString());
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("microsoft/microsoft-decision-1", root.GetProperty("model").GetString());
        Assert.Equal("{\"A\":true}", root.GetProperty("state").GetString());
        JsonElement questions = root.GetProperty("questions");
        Assert.Equal("noul", questions.GetProperty("urgent").GetProperty("type").GetString());
        Assert.Equal("No urgency expressed", questions.GetProperty("urgent").GetProperty("criteria").GetProperty("false").GetString());
        Assert.Equal(["billing", "technical"], questions.GetProperty("team").GetProperty("criteria").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Calm", "Frustrated", "Very angry"], questions.GetProperty("mood").GetProperty("criteria").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("\"(@A > 1)\"", DecisionsApiClient.FormatRequest("m", new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["q"] = new NoulQuestion("(@A > 1)", "t", "f") })));
    }

    [Fact]
    public async Task Sends_an_object_state_and_batched_noul_questions_without_criteria()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""
            {"model":"typesafe/jev-1.13-20260917","answers":{"r2":{"type":"noul","noul":0.03},"r4":{"type":"noul","noul":0.96}},"usage":{"input_tokens":475,"output_tokens":38,"cost":0.00001995}}
            """));
        var client = new DecisionsApiClient(new HttpClient(handler), Options);
        var state = new JsonObject { ["Supply_Valve"] = true, ["Return_Temp"] = 110, ["Flow"] = 79.5 };
        var request = new DecisionsApiRequest(state, new Dictionary<string, DecisionQuestion>
        {
            ["r2"] = new NoulQuestion("Expression: Supply_Valve == true && Return_Temp > 150"),
            ["r4"] = new NoulQuestion("Expression: Flow > 78"),
        });

        DecisionsApiResponse response = await client.DecideAsync(request);

        Assert.Equal(
            """{"model":"microsoft/microsoft-decision-1","state":{"Supply_Valve":true,"Return_Temp":110,"Flow":79.5},"questions":{"r2":{"type":"noul","instructions":"Expression: Supply_Valve == true && Return_Temp > 150"},"r4":{"type":"noul","instructions":"Expression: Flow > 78"}}}""",
            handler.Requests[0].Body);
        Assert.Equal(0.96, response.Answer<NoulAnswer>("r4").Probability);
        Assert.Null(state.Parent);
        Assert.Contains("\"criteria\":{\"true\":\"yes\"}", DecisionsApiClient.FormatRequest("m", new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["q"] = new NoulQuestion("q", WhenTrue: "yes") })));
    }

    [Theory]
    [InlineData("retry-after-ms", "250", 250)]
    [InlineData("Retry-After", "2", 2000)]
    public async Task Rate_limit_errors_carry_the_server_retry_delay(string header, string value, double expectedMs)
    {
        var handler = new FakeHandler((_, _) =>
        {
            HttpResponseMessage response = FakeHandler.Json("""{"error":{"message":"Rate limit exceeded"}}""", HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation(header, value);
            return response;
        });
        var client = new DecisionsApiClient(new HttpClient(handler), Options);
        DecisionServerException exception = await Assert.ThrowsAsync<DecisionServerException>(() =>
            client.DecideAsync(new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["q"] = new NoulQuestion("q") })));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), exception.RetryAfter);
    }

    [Fact]
    public async Task Parses_noul_answers_usage_cost_and_snapshot_model()
    {
        var client = new DecisionsApiClient(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json(NoulResponse))), Options);
        DecisionsApiResponse response = await client.DecideAsync(new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["expression"] = new NoulQuestion("q", "t", "f") }));
        Assert.Equal(0.0008040891827022661, response.Answer<NoulAnswer>("expression").Probability);
        Assert.False(response.Answer<NoulAnswer>("expression").Value);
        Assert.Equal("microsoft/microsoft-decision-1-20261009", response.Model);
        Assert.Equal("Azure", response.Provider);
        Assert.Equal(new DecisionsApiUsage(233, 1, 0.000009786m), response.Usage);
        Assert.Throws<DecisionFormatException>(() => response.Answer<NoulAnswer>("missing"));
        Assert.Throws<DecisionFormatException>(() => response.Answer<ChoiceAnswer>("expression"));
    }

    [Fact]
    public void Parses_choice_and_score_answers_in_level_order()
    {
        using JsonDocument json = JsonDocument.Parse("""
            {"answers":{
              "team":{"type":"choice","choice":"billing","confidence":0.91,"probabilities":{"billing":0.93,"technical":0.05,"sales":0.02}},
              "mood":{"type":"score","score":1.4,"confidence":0.6,"probabilities":{"2":0.5,"0":0.1,"1":0.4},"legend":{"0":"Calm","1":"Frustrated","2":"Very angry"}},
              "future":{"type":"ranking","items":[]}}}
            """);
        DecisionsApiResponse response = DecisionsApiClient.Parse(json.RootElement);
        ChoiceAnswer team = response.Answer<ChoiceAnswer>("team");
        Assert.Equal("billing", team.Choice);
        Assert.Equal(0.93, team.Probabilities["billing"]);
        ScoreAnswer mood = response.Answer<ScoreAnswer>("mood");
        Assert.Equal([0.1, 0.4, 0.5], mood.Probabilities);
        Assert.Equal(1.4, mood.Score);
        Assert.IsType<UnknownAnswer>(response.Answers["future"]);
    }

    [Fact]
    public async Task Openrouter_errors_keep_their_status_and_message()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"error":{"code":401,"message":"No auth credentials found"}}""", HttpStatusCode.Unauthorized));
        var client = new DecisionsApiClient(new HttpClient(handler), Options);
        DecisionServerException exception = await Assert.ThrowsAsync<DecisionServerException>(() =>
            client.DecideAsync(new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["q"] = new NoulQuestion("q", "t", "f") })));
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("HTTP 401 (Unauthorized): No auth credentials found", exception.Message);
    }

    [Fact]
    public async Task Lists_model_ids_and_canonical_slugs_from_the_catalog()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""
            {"data":[{"id":"microsoft/microsoft-decision-1","canonical_slug":"microsoft/microsoft-decision-1-20261009"},{"id":"typesafe/jev-1.13","canonical_slug":"typesafe/jev-1.13-20260917"}]}
            """));
        IReadOnlyList<string> models = await new DecisionsApiClient(new HttpClient(handler), Options).ListModelsAsync();
        Assert.Equal("https://openrouter.ai/api/v1/models?output_modalities=decisions", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(["microsoft/microsoft-decision-1", "microsoft/microsoft-decision-1-20261009", "typesafe/jev-1.13", "typesafe/jev-1.13-20260917"], models);
    }

    [Fact]
    public async Task Hosted_client_answers_through_the_same_IDecisionClient_interface()
    {
        var handler = new FakeHandler((_, body) =>
        {
            using JsonDocument request = JsonDocument.Parse(body);
            JsonElement question = request.RootElement.GetProperty("questions").GetProperty("decision");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            return FakeHandler.Json("""
                {"model":"typesafe/jev-1.13-20260917","id":"gen-dec-2","answers":{"decision":{"type":"choice","choice":"yes","confidence":0.8,"probabilities":{"yes":0.9,"no":0.1}}},"usage":{"input_tokens":50,"output_tokens":1,"cost":0.0000021}}
                """);
        });
        IDecisionClient client = new HostedDecisionClient(new DecisionsApiClient(new HttpClient(handler), Options));
        BoolDecision answer = await client.DecideBoolAsync("Premise: a man plays guitar. Hypothesis: someone makes music.", "Does the premise entail the hypothesis?");
        Assert.Equal(0.9, answer.Probability, 12);
        Assert.Equal("typesafe/jev-1.13-20260917", answer.Decision.Diagnostics.ServedModel);
        Assert.Equal(50, answer.Decision.Diagnostics.Usage?.PromptTokens);
    }

    [Fact]
    public async Task Dependency_injection_registers_the_decisions_api()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(NoulResponse));
        var services = new ServiceCollection();
        services.AddDecisionsApi(o => { o.Model = "typesafe/jev-1.13"; o.ApiKey = "k"; }).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDecisionsApi>().DecideAsync(new DecisionsApiRequest("s", new Dictionary<string, DecisionQuestion> { ["expression"] = new NoulQuestion("q", "t", "f") }));
        Assert.Contains("\"model\":\"typesafe/jev-1.13\"", handler.Requests[0].Body);
        Assert.NotNull(provider.GetRequiredService<HostedDecisionClient>());
    }
}

# JevStyle.Decisions

Typed decisions with calibrated probabilities from decision models:

- Jev-style models (for example `chaoliangUNSW/Jev-Style-Qwen3.5-2B-Decision-GGUF`) served by LM Studio,
  llama-server or any OpenAI-compatible endpoint that returns `top_logprobs` on `/v1/chat/completions`.
- Hosted decision models (Jev, Microsoft-Decision-1 and others) through OpenRouter's decisions API.

Both sit behind `IDecisionClient`, so the same code can run against a local model or a hosted one.

```csharp
var http = new HttpClient();
var source = new OpenAiChatLogprobSource(http, new JevStyleClientOptions
{
    BaseAddress = new Uri("http://localhost:1234"),
    Model = "jev-style-qwen3.5-2b-decision",
});
IDecisionClient client = new DecisionClient(source);

Decision news = await client.DecideAsync(
    "Shares of the chipmaker jumped 8% after it raised its revenue forecast.",
    "Which news section does this article belong to?",
    ["World", "Sports", "Business", "Science/Technology"]);

BoolDecision entails = await client.DecideBoolAsync(
    "Premise: A man is playing a guitar on stage.\nHypothesis: Someone is performing music.",
    "Does the premise entail the hypothesis?");
```

With dependency injection:

```csharp
services.AddJevStyleDecisions(o => o.BaseAddress = new Uri("http://localhost:1234"))
        .AddStandardResilienceHandler(); // optional, from Microsoft.Extensions.Http.Resilience
```

## Hosted decision models

`DecisionsApiClient` calls OpenRouter's decisions API (`POST https://openrouter.ai/api/alpha/decisions`, the same
shape as TypeSafe's `/v1/systemone`). One call asks any number of typed questions (noul, choice, score) about one
state, which can be a JSON object of named values or plain text.

```csharp
var api = new DecisionsApiClient(new HttpClient(), new DecisionsApiOptions
{
    Model = "typesafe/jev-1.13", // default: microsoft/microsoft-decision-1
    ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
});

DecisionsApiResponse response = await api.DecideAsync(new DecisionsApiRequest(
    new JsonObject { ["Supply_Pump"] = true, ["Return_Temp"] = 151 },
    new Dictionary<string, DecisionQuestion>
    {
        ["circulating"] = new NoulQuestion("Expression: Supply_Pump == true && Return_Temp > 150"),
        ["hot"] = new NoulQuestion("Is the return temperature above 150?", WhenTrue: "It is above 150.", WhenFalse: "It is 150 or below."),
    }));

double probability = response.Answer<NoulAnswer>("circulating").Probability; // probability of true
decimal? cost = response.Usage?.Cost;                                        // USD, as reported by OpenRouter
```

A 429 or 503 throws `DecisionServerException` carrying the server's `RetryAfter`. `HostedDecisionClient` adapts any
`IDecisionsApi` to `IDecisionClient`: each `DecideAsync` call becomes one choice question whose options are the
declared options.

```csharp
services.AddDecisionsApi(o =>
{
    o.Model = "microsoft/microsoft-decision-1";
    o.ApiKey = configuration["OpenRouter:ApiKey"];
});
// Inject IDecisionsApi for the full API, or HostedDecisionClient where you would use IDecisionClient.
```

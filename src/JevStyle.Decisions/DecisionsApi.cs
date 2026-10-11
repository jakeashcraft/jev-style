using System.Text.Json.Nodes;

namespace JevStyle.Decisions;

/// <summary>
/// A question for a hosted decisions API: OpenRouter's <c>/api/alpha/decisions</c> (Jev, Microsoft-Decision-1),
/// and gateways that copy its shape. The hierarchy is closed: noul, choice or score.
/// </summary>
/// <param name="Instructions">What to decide, in plain language.</param>
public abstract record DecisionQuestion(string Instructions)
{
    internal abstract JsonObject ToJson();
}

/// <summary>A yes/no question. The answer is the probability of <c>true</c>.</summary>
/// <param name="Instructions">What to decide.</param>
/// <param name="WhenTrue">What <c>true</c> means. Optional; omitted from the request when null.</param>
/// <param name="WhenFalse">What <c>false</c> means. Optional; omitted from the request when null.</param>
public sealed record NoulQuestion(string Instructions, string? WhenTrue = null, string? WhenFalse = null) : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = "noul", ["instructions"] = Instructions };
        if (WhenTrue is null && WhenFalse is null) return json;
        var criteria = new JsonObject();
        if (WhenTrue is not null)
        {
            criteria["true"] = WhenTrue;
        }

        if (WhenFalse is not null)
        {
            criteria["false"] = WhenFalse;
        }

        json["criteria"] = criteria;

        return json;
    }
}

/// <summary>Pick one of several named options.</summary>
/// <param name="Instructions">What to decide.</param>
/// <param name="Criteria">Option name and description, in order.</param>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyList<KeyValuePair<string, string>> Criteria) : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var criteria = new JsonObject();
        foreach (KeyValuePair<string, string> option in Criteria)
        {
            criteria.Add(option.Key, option.Value);
        }

        return new JsonObject { ["type"] = "choice", ["instructions"] = Instructions, ["criteria"] = criteria };
    }
}

/// <summary>A distribution over ordered levels.</summary>
/// <param name="Instructions">What to rate.</param>
/// <param name="Levels">Ordered levels, lowest first.</param>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : DecisionQuestion(Instructions)
{
    internal override JsonObject ToJson()
    {
        var levels = new JsonArray();
        foreach (string level in Levels)
        {
            levels.Add((JsonNode?)JsonValue.Create(level));
        }

        return new JsonObject { ["type"] = "score", ["instructions"] = Instructions, ["criteria"] = levels };
    }
}

/// <summary>One answer from a hosted decisions API.</summary>
/// <param name="Type">The question type the server reported.</param>
public abstract record DecisionAnswer(string Type);

/// <summary>Answer to a <see cref="NoulQuestion"/>.</summary>
/// <param name="Probability">Probability of <c>true</c>.</param>
public sealed record NoulAnswer(double Probability) : DecisionAnswer("noul")
{
    /// <summary><see langword="true"/> when <see cref="Probability"/> is at least 0.5.</summary>
    public bool Value => Probability >= 0.5;
}

/// <summary>Answer to a <see cref="ChoiceQuestion"/>.</summary>
/// <param name="Choice">The selected option name.</param>
/// <param name="Confidence">How concentrated the distribution is, when reported.</param>
/// <param name="Probabilities">Probability per option name.</param>
public sealed record ChoiceAnswer(string Choice, double? Confidence, IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer("choice");

/// <summary>Answer to a <see cref="ScoreQuestion"/>.</summary>
/// <param name="Score">Probability-weighted position on the scale (0 is the first level).</param>
/// <param name="Confidence">How concentrated the distribution is, when reported.</param>
/// <param name="Probabilities">Probability per level, in level order.</param>
public sealed record ScoreAnswer(double Score, double? Confidence, IReadOnlyList<double> Probabilities) : DecisionAnswer("score");

/// <summary>An answer type this client does not know yet. The raw JSON is kept.</summary>
/// <param name="Type">The type the server reported.</param>
/// <param name="RawJson">The answer as JSON.</param>
public sealed record UnknownAnswer(string Type, string RawJson) : DecisionAnswer(Type);

/// <summary>A request: one state and any number of independent questions about it.</summary>
/// <param name="State">
/// What the model reads: a <see cref="JsonObject"/> of named values (sent as a JSON object, the way the TypeSafe
/// SDK sends tag snapshots) or text (sent as a JSON string). The node is copied when the request is serialized.
/// </param>
/// <param name="Questions">Questions keyed by a name of your choosing; the name is not shown to the model.</param>
public sealed record DecisionsApiRequest(JsonNode State, IReadOnlyDictionary<string, DecisionQuestion> Questions)
{
    /// <summary>Creates a request whose state is text: a document, a message, or JSON you have already serialized.</summary>
    /// <param name="state">What the model reads.</param>
    /// <param name="questions">Questions keyed by a name of your choosing.</param>
    public DecisionsApiRequest(string state, IReadOnlyDictionary<string, DecisionQuestion> questions)
        : this(JsonValue.Create(state ?? throw new ArgumentNullException(nameof(state)))!, questions)
    {
    }
}

/// <summary>Usage the server reported.</summary>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="Cost">Cost in USD.</param>
public sealed record DecisionsApiUsage(int? InputTokens, int? OutputTokens, decimal? Cost);

/// <summary>A response from a hosted decisions API.</summary>
/// <param name="Id">Response id.</param>
/// <param name="Model">The model that answered. OpenRouter returns a dated snapshot, for example <c>typesafe/jev-1.13-20260917</c>.</param>
/// <param name="Provider">The provider that served the request.</param>
/// <param name="Answers">Answers keyed by question name.</param>
/// <param name="Usage">Token usage and cost.</param>
public sealed record DecisionsApiResponse(string? Id, string? Model, string? Provider, IReadOnlyDictionary<string, DecisionAnswer> Answers, DecisionsApiUsage? Usage)
{
    /// <summary>Returns the typed answer for a question.</summary>
    /// <typeparam name="T">The expected answer type.</typeparam>
    /// <param name="question">The question name used in the request.</param>
    /// <exception cref="DecisionFormatException">The answer is missing or has another type.</exception>
    public T Answer<T>(string question)
        where T : DecisionAnswer =>
        Answers.TryGetValue(question, out DecisionAnswer? answer)
            ? answer as T ?? throw new DecisionFormatException($"Answer '{question}' is a {answer.Type} answer, not {typeof(T).Name}.")
            : throw new DecisionFormatException($"The response has no answer for '{question}'.");
}

/// <summary>A hosted decisions API. Implemented by <see cref="DecisionsApiClient"/>.</summary>
public interface IDecisionsApi
{
    /// <summary>Asks every question in the request about its state, in one call.</summary>
    /// <param name="request">State and questions.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<DecisionsApiResponse> DecideAsync(DecisionsApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Settings for a hosted decisions API. Defaults point at OpenRouter.</summary>
public sealed class DecisionsApiOptions
{
    /// <summary>OpenRouter's decisions API root.</summary>
    public static readonly Uri OpenRouterBaseAddress = new("https://openrouter.ai/api/alpha/");

    /// <summary>The default model.</summary>
    public const string DefaultModel = "microsoft/microsoft-decision-1";

    /// <summary>API root. Requests go to <c>{BaseAddress}/decisions</c>.</summary>
    public Uri BaseAddress { get; set; } = OpenRouterBaseAddress;

    /// <summary>Model id, for example <c>microsoft/microsoft-decision-1</c> or <c>typesafe/jev-1.13</c>.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Bearer token. Required by OpenRouter.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model catalog address. Defaults to OpenRouter's list of decision models on the same host.</summary>
    public Uri? ModelsAddress { get; set; }

    /// <summary>The decisions endpoint.</summary>
    public Uri DecisionsUri => new(BaseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/decisions", UriKind.Absolute);

    /// <summary>The model catalog endpoint.</summary>
    public Uri ModelsUri => ModelsAddress ?? new Uri(BaseAddress, "/api/v1/models?output_modalities=decisions");

    /// <summary>Checks the settings.</summary>
    /// <param name="error">Why the settings are invalid.</param>
    /// <returns><see langword="true"/> when usable.</returns>
    public bool TryValidate(out string? error)
    {
        error = BaseAddress is null || !BaseAddress.IsAbsoluteUri || (BaseAddress.Scheme != Uri.UriSchemeHttp && BaseAddress.Scheme != Uri.UriSchemeHttps)
            ? "BaseAddress must be an absolute http or https URL."
            : string.IsNullOrWhiteSpace(Model)
                ? "Model cannot be empty."
                : null;
        return error is null;
    }

    internal void Validate()
    {
        if (!TryValidate(out string? error))
        {
            throw new ArgumentException(error);
        }
    }
}

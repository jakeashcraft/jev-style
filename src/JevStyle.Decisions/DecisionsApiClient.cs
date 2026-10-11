using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JevStyle.Decisions;

/// <summary>
/// Client for OpenRouter's decisions API (<c>POST /api/alpha/decisions</c>) and compatible gateways. One call
/// asks several typed questions about one state and returns calibrated probabilities, without text generation.
/// </summary>
public sealed class DecisionsApiClient : IDecisionsApi
{
    private static readonly JsonSerializerOptions _writeOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly HttpClient _httpClient;
    private readonly DecisionsApiOptions _options;

    /// <summary>Creates the client for use with dependency injection.</summary>
    /// <param name="httpClient">Client from <c>IHttpClientFactory</c>.</param>
    /// <param name="options">API settings.</param>
    [ActivatorUtilitiesConstructor]
    public DecisionsApiClient(HttpClient httpClient, IOptions<DecisionsApiOptions> options)
        : this(httpClient, options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Creates the client without dependency injection.</summary>
    /// <param name="httpClient">A long-lived client.</param>
    /// <param name="options">API settings.</param>
    public DecisionsApiClient(HttpClient httpClient, DecisionsApiOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<DecisionsApiResponse> DecideAsync(DecisionsApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.Questions is not { Count: > 0 })
        {
            throw new ArgumentException("A request needs at least one question.", nameof(request));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.DecisionsUri);
        message.Content = new StringContent(FormatRequest(_options.Model, request), Encoding.UTF8, "application/json");
        Authorize(message);
        using HttpResponseMessage response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await HttpErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new DecisionFormatException($"The server returned invalid JSON: {exception.Message}");
        }
    }

    /// <summary>Lists the decision models the server offers (ids and canonical slugs).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, _options.ModelsUri);
        Authorize(message);
        using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await HttpErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var ids = new List<string>();
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Array) return ids;
        foreach (JsonElement model in data.EnumerateArray())
        {
            foreach (string name in (string[])["id", "canonical_slug"])
            {
                if (model.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The JSON body this client sends for a request. Useful for logs and reports; contains no credentials.</summary>
    /// <param name="model">Model id.</param>
    /// <param name="request">State and questions.</param>
    public static string FormatRequest(string model, DecisionsApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var questions = new JsonObject();
        foreach (KeyValuePair<string, DecisionQuestion> question in request.Questions)
        {
            questions.Add(question.Key, question.Value.ToJson());
        }

        return new JsonObject { ["model"] = model, ["state"] = request.State.DeepClone(), ["questions"] = questions }.ToJsonString(_writeOptions);
    }

    internal static DecisionsApiResponse Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionFormatException("The response is not a JSON object.");
        }

        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new DecisionFormatException($"The server returned an error: {HttpErrors.ErrorText(error)}");
        }

        if (!root.TryGetProperty("answers", out JsonElement answers) || answers.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionFormatException("The response has no answers object.");
        }

        var parsed = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (JsonProperty answer in answers.EnumerateObject())
        {
            parsed[answer.Name] = ParseAnswer(answer.Value);
        }

        DecisionsApiUsage? usage = null;
        if (root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new DecisionsApiUsage(
                Int(u, "input_tokens") ?? Int(u, "inputTokens") ?? Int(u, "prompt_tokens"),
                Int(u, "output_tokens") ?? Int(u, "outputTokens") ?? Int(u, "completion_tokens"),
                u.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null);
        }

        return new DecisionsApiResponse(Text(root, "id"), Text(root, "model"), Text(root, "provider"), parsed, usage);
    }

    private static DecisionAnswer ParseAnswer(JsonElement answer)
    {
        string type = Text(answer, "type") ?? "unknown";
        switch (type)
        {
            case "noul":
            case "boolean":
                double? probability = Number(answer, "noul") ?? Number(answer, "probability");
                return probability is { } p ? new NoulAnswer(p) : throw new DecisionFormatException($"A {type} answer has no probability.");
            case "choice":
                return new ChoiceAnswer(Text(answer, "choice") ?? string.Empty, Number(answer, "confidence"), Probabilities(answer));
            case "score":
                IReadOnlyDictionary<string, double> levels = Probabilities(answer);
                double[] ordered = [.. levels
                    .Select(l => (Index: int.TryParse(l.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : int.MaxValue, l.Value))
                    .OrderBy(l => l.Index)
                    .Select(l => l.Value)];
                return new ScoreAnswer(Number(answer, "score") ?? double.NaN, Number(answer, "confidence"), ordered);
            default:
                return new UnknownAnswer(type, answer.GetRawText());
        }
    }

    private static Dictionary<string, double> Probabilities(JsonElement answer)
    {
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!answer.TryGetProperty("probabilities", out JsonElement values)) return probabilities;
        switch (values.ValueKind)
        {
            case JsonValueKind.Object:
            {
                foreach (var value in values.EnumerateObject().Where(value => value.Value.ValueKind == JsonValueKind.Number))
                {
                    probabilities[value.Name] = value.Value.GetDouble();
                }

                break;
            }
            case JsonValueKind.Array:
            {
                int index = 0;
                foreach (JsonElement value in values.EnumerateArray())
                {
                    probabilities[(index++).ToString(CultureInfo.InvariantCulture)] = value.GetDouble();
                }

                break;
            }
        }

        return probabilities;
    }

    private void Authorize(HttpRequestMessage message)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
}

/// <summary>
/// <see cref="IDecisionClient"/> backed by a hosted decisions API, so code written against the local client can
/// switch to Jev or Microsoft-Decision-1 without changes. Each <see cref="DecisionRequest"/> becomes one choice
/// question whose option names are the declared options.
/// </summary>
/// <param name="api">The hosted API.</param>
public sealed class HostedDecisionClient(IDecisionsApi api) : IDecisionClient
{
    private const string QuestionName = "decision";
    private readonly IDecisionsApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public async Task<Decision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DecisionPrompt.ValidateOptions(request.Options, DecisionPrompt.MaxOptions);
        if (request.Options.Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
        {
            throw new ArgumentException("Options must be unique for a hosted decisions API.", nameof(request));
        }

        var question = new ChoiceQuestion(request.Question, [.. request.Options.Select(o => new KeyValuePair<string, string>(o, o))]);
        DecisionsApiResponse response = await _api
            .DecideAsync(new DecisionsApiRequest(request.State, new Dictionary<string, DecisionQuestion> { [QuestionName] = question }), cancellationToken)
            .ConfigureAwait(false);
        ChoiceAnswer answer = response.Answer<ChoiceAnswer>(QuestionName);

        double[] raw = [.. request.Options.Select(o => answer.Probabilities.TryGetValue(o, out double p) ? p : 0)];
        double total = raw.Sum();
        if (total <= 0)
        {
            throw new DecisionFormatException("The response has no probability for any declared option.");
        }

        var options = new OptionProbability[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            options[i] = new OptionProbability(i, DecisionPrompt.Letters[i], request.Options[i], raw[i] / total, null);
        }

        DecisionsApiUsage? usage = response.Usage;
        var diagnostics = new DecisionDiagnostics(
            Math.Min(1.0, total),
            request.Options.Count(o => !answer.Probabilities.ContainsKey(o)),
            double.NaN,
            usage is null ? null : new TokenUsage(usage.InputTokens, usage.OutputTokens),
            response.Model,
            response.Id,
            []);
        return new Decision(options, diagnostics);
    }
}

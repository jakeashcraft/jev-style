using System.Text.Json.Nodes;
using JevStyle.Benchmark.Dataset;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Running;

/// <summary>What a server said about one request.</summary>
/// <param name="Probabilities">P(true) per row id.</param>
/// <param name="InputTokens">Input tokens, when reported.</param>
/// <param name="OutputTokens">Output tokens, when reported.</param>
/// <param name="Cost">Cost in USD, when reported (OpenRouter reports it per request).</param>
/// <param name="ServedModel">The model the server says answered.</param>
/// <param name="Provider">The provider behind a gateway, when reported.</param>
/// <param name="ResponseId">Server response id.</param>
/// <param name="OptionMass">Local model only: probability on the option letters before renormalising.</param>
internal sealed record SenderReply(
    IReadOnlyDictionary<string, double> Probabilities,
    int? InputTokens = null,
    int? OutputTokens = null,
    double? Cost = null,
    string? ServedModel = null,
    string? Provider = null,
    string? ResponseId = null,
    double? OptionMass = null);

/// <summary>Strategy for answering one request. The sweep runner only sees this.</summary>
internal interface IRequestSender
{
    /// <summary>Most rows one request can carry.</summary>
    int MaxBatch { get; }

    /// <summary>One line describing the request format, for the console and the results file.</summary>
    string Describe();

    /// <summary>The exact body or prompt the server receives for a request. Contains no credentials.</summary>
    string Example(BenchRequest request);

    Task<SenderReply> SendAsync(BenchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A hosted decisions API (OpenRouter's <c>/api/alpha/decisions</c>, the same shape as TypeSafe's
/// <c>/v1/systemone</c>). Sends bench.mts's payload unchanged: the state as a JSON object and one noul question
/// per row, keyed by row id, with the rules and the expression as instructions.
/// </summary>
internal sealed class DecisionsApiSender(IDecisionsApi api, DecisionsApiOptions options) : IRequestSender
{
    public int MaxBatch => int.MaxValue;

    public static DecisionsApiRequest Build(BenchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);
        foreach (ExpressionRow row in request.Rows)
        {
            questions[row.Id] = new NoulQuestion(ExpressionDataset.Question(row));
        }

        // Parsing the JavaScript-formatted JSON keeps every number exactly as bench.mts writes it.
        return new DecisionsApiRequest(JsonNode.Parse(request.State.ToJson())!, questions);
    }

    public string Describe() => $"{options.Model} via {options.DecisionsUri}, one noul question per expression with bench.mts's rules, state limited to the request's tags";

    public string Example(BenchRequest request) => DecisionsApiClient.FormatRequest(options.Model, Build(request));

    public async Task<SenderReply> SendAsync(BenchRequest request, CancellationToken cancellationToken)
    {
        DecisionsApiResponse response = await api.DecideAsync(Build(request), cancellationToken).ConfigureAwait(false);
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ExpressionRow row in request.Rows)
        {
            double probability = response.Answer<NoulAnswer>(row.Id).Probability;
            if (!double.IsFinite(probability) || probability is < 0 or > 1)
            {
                throw new DecisionFormatException($"Answer '{row.Id}' has an invalid probability ({probability}).");
            }

            probabilities[row.Id] = probability;
        }

        DecisionsApiUsage? usage = response.Usage;
        return new SenderReply(
            probabilities,
            usage?.InputTokens,
            usage?.OutputTokens,
            usage?.Cost is { } cost ? (double)cost : null,
            response.Model,
            response.Provider,
            response.Id);
    }
}

/// <summary>Which option gets letter A in the local prompt.</summary>
internal enum OptionOrder
{
    TrueFirst,
    FalseFirst,
}

/// <summary>
/// A local Jev-style model through next-token log-probabilities (LM Studio). The model reads one decision per
/// prompt, so a request carries one row: the state is that row's tags, the question is bench.mts's rules and
/// expression followed by a yes/no proposition, and P(yes) is the answer.
/// </summary>
internal sealed class LogprobSender(IDecisionClient client, OptionOrder order = OptionOrder.TrueFirst) : IRequestSender
{
    public const string Proposition = "Does the expression evaluate to true?";

    private readonly string[] _options = order == OptionOrder.TrueFirst ? ["yes", "no"] : ["no", "yes"];
    private readonly int _trueIndex = order == OptionOrder.TrueFirst ? 0 : 1;

    public int MaxBatch => 1;

    public DecisionRequest Build(BenchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Rows.Count != 1
            ? throw new ArgumentException("The local model answers one expression per prompt.", nameof(request))
            : new DecisionRequest(request.State.ToJson(),
                $"{ExpressionDataset.Question(request.Rows[0])}\n{Proposition}", _options);
    }

    public string Describe() =>
        $"Jev-style prompt, one expression per request, options {string.Join(" / ", _options.Select((o, i) => $"{DecisionPrompt.LetterFor(i)}. {o}"))}";

    public string Example(BenchRequest request)
    {
        DecisionRequest decision = Build(request);
        return DecisionPrompt.Build(decision.State, decision.Question, decision.Options);
    }

    public async Task<SenderReply> SendAsync(BenchRequest request, CancellationToken cancellationToken)
    {
        Decision decision = await client.DecideAsync(Build(request), cancellationToken).ConfigureAwait(false);
        DecisionDiagnostics diagnostics = decision.Diagnostics;
        return new SenderReply(
            new Dictionary<string, double>(StringComparer.Ordinal) { [request.Rows[0].Id] = decision.ProbabilityAt(_trueIndex) },
            diagnostics.Usage?.PromptTokens,
            diagnostics.Usage?.CompletionTokens,
            Cost: null,
            diagnostics.ServedModel,
            Provider: null,
            diagnostics.ResponseId,
            diagnostics.OptionMass);
    }
}

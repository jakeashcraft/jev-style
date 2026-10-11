namespace JevStyle.Decisions;

/// <summary>One candidate for the next token and its log-probability.</summary>
/// <param name="Token">Token text as the server reports it, for example <c>" A"</c>.</param>
/// <param name="Logprob">Natural-log probability of the token.</param>
public readonly record struct TokenLogprob(string Token, double Logprob);

/// <summary>Token counts reported by the server, when it reports them.</summary>
/// <param name="PromptTokens">Prompt (input) tokens.</param>
/// <param name="CompletionTokens">Generated (output) tokens.</param>
public sealed record TokenUsage(int? PromptTokens, int? CompletionTokens)
{
    /// <summary>Prompt plus completion tokens, when both are known.</summary>
    public int? TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>The candidates a server returned for the first generated position.</summary>
/// <param name="Candidates">Top candidates with their log-probabilities.</param>
/// <param name="Usage">Token usage, when reported.</param>
/// <param name="ServedModel">The model the server says it used. LM Studio answers with the loaded model even when the requested id is unknown, so compare this with the requested id.</param>
/// <param name="ResponseId">Server response id, when reported.</param>
public sealed record NextTokenLogprobs(
    IReadOnlyList<TokenLogprob> Candidates,
    TokenUsage? Usage = null,
    string? ServedModel = null,
    string? ResponseId = null);

/// <summary>
/// Strategy that turns a prompt into next-token log-probabilities. Implement it to support another
/// transport (llama.cpp native API, an in-process runtime, a test double).
/// </summary>
public interface ILogprobSource
{
    /// <summary>The most candidates this source returns per position. It caps the number of options.</summary>
    int MaxCandidates { get; }

    /// <summary>Returns the top candidates for the first token the model would generate after the prompt.</summary>
    /// <param name="prompt">The complete prompt, sent verbatim.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<NextTokenLogprobs> GetNextTokenLogprobsAsync(string prompt, CancellationToken cancellationToken = default);
}

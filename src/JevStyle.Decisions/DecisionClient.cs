namespace JevStyle.Decisions;

/// <summary>Makes typed decisions with a Jev-style decision model.</summary>
public interface IDecisionClient
{
    /// <summary>Makes one decision: one probability per declared option.</summary>
    /// <param name="request">State, question and declared options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Decision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDecisionClient"/>: builds the prompt, asks an <see cref="ILogprobSource"/> for the
/// next-token log-probabilities and renormalises them over the option letters. Stateless and thread-safe.
/// </summary>
/// <param name="source">Where log-probabilities come from, for example <see cref="OpenAiChatLogprobSource"/>.</param>
public sealed class DecisionClient(ILogprobSource source) : IDecisionClient
{
    private readonly ILogprobSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public async Task<Decision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DecisionPrompt.ValidateOptions(request.Options, _source.MaxCandidates);
        string prompt = DecisionPrompt.Build(request.State, request.Question, request.Options);
        NextTokenLogprobs next = await _source.GetNextTokenLogprobsAsync(prompt, cancellationToken).ConfigureAwait(false);
        return LogprobDecoder.Decode(request.Options, next);
    }
}

/// <summary>Convenience overloads for Choice, Bool and Score decisions.</summary>
public static class DecisionClientExtensions
{
    /// <param name="client">The decision client.</param>
    extension(IDecisionClient client)
    {
        /// <summary>Choice: picks one of <paramref name="options"/>.</summary>
        /// <param name="state">What the model reads.</param>
        /// <param name="question">The question about the state.</param>
        /// <param name="options">Declared options.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public Task<Decision> DecideAsync(string state, string question, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.DecideAsync(new DecisionRequest(state, question, options), cancellationToken);
        }

        /// <summary>Bool: the probability that <paramref name="proposition"/> is true, asked as yes/no.</summary>
        /// <param name="state">What the model reads.</param>
        /// <param name="proposition">A yes/no question about the state.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<BoolDecision> DecideBoolAsync(string state, string proposition, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            Decision decision = await client.DecideAsync(new DecisionRequest(state, proposition, ["yes", "no"]), cancellationToken).ConfigureAwait(false);
            return new BoolDecision(decision.ProbabilityAt(0), decision);
        }

        /// <summary>Score: a distribution over ordered levels and its expected level.</summary>
        /// <param name="state">What the model reads.</param>
        /// <param name="question">The question about the state.</param>
        /// <param name="levels">Ordered levels, lowest first.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<ScoreDecision> DecideScoreAsync(string state, string question, IReadOnlyList<string> levels, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            Decision decision = await client.DecideAsync(new DecisionRequest(state, question, levels), cancellationToken).ConfigureAwait(false);
            double expected = decision.Options.Sum(level => level.Index * level.Probability);

            return new ScoreDecision(expected, decision);
        }
    }
}

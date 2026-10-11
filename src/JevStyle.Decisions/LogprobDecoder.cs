namespace JevStyle.Decisions;

/// <summary>
/// Turns next-token log-probabilities into a decision by renormalising over the declared option letters.
/// This follows the reference client published with the model: tokens are matched after trimming
/// whitespace (<c>" A"</c> and <c>"A"</c> both count), the best match per letter wins, and a letter missing
/// from the candidates gets the lowest candidate log-probability minus 5.
/// </summary>
public static class LogprobDecoder
{
    /// <summary>How far below the lowest returned candidate a missing option letter is placed, in nats.</summary>
    public const double MissingOptionPenalty = 5.0;

    /// <summary>Decodes a decision over <paramref name="options"/> from next-token log-probabilities.</summary>
    /// <param name="options">The declared options, in the order used in the prompt.</param>
    /// <param name="next">The candidates for the first generated position.</param>
    /// <exception cref="DecisionFormatException">The server returned no usable candidates.</exception>
    public static Decision Decode(IReadOnlyList<string> options, NextTokenLogprobs next)
    {
        DecisionPrompt.ValidateOptions(options, DecisionPrompt.MaxOptions);
        ArgumentNullException.ThrowIfNull(next);

        double lowest = double.PositiveInfinity;
        foreach (TokenLogprob candidate in next.Candidates)
        {
            if (double.IsFinite(candidate.Logprob) && candidate.Logprob < lowest)
            {
                lowest = candidate.Logprob;
            }
        }

        if (double.IsPositiveInfinity(lowest))
        {
            throw new DecisionFormatException("The server returned no candidate tokens with finite log-probabilities.");
        }

        double floor = lowest - MissingOptionPenalty;
        int count = options.Count;
        double?[] matched = new double?[count];
        double[] logits = new double[count];
        double optionMass = 0;
        int missing = 0;
        for (int i = 0; i < count; i++)
        {
            char letter = DecisionPrompt.Letters[i];
            double? best = null;
            foreach (TokenLogprob candidate in next.Candidates)
            {
                if (double.IsFinite(candidate.Logprob) && IsLetter(candidate.Token, letter) && (best is null || candidate.Logprob > best))
                {
                    best = candidate.Logprob;
                }
            }

            matched[i] = best;
            logits[i] = best ?? floor;
            if (best is { } value)
            {
                optionMass += Math.Exp(value);
            }
            else
            {
                missing++;
            }
        }

        double max = logits.Max();
        double sum = 0;
        double[] weights = new double[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = Math.Exp(logits[i] - max);
            sum += weights[i];
        }

        var probabilities = new OptionProbability[count];
        for (int i = 0; i < count; i++)
        {
            probabilities[i] = new OptionProbability(i, DecisionPrompt.Letters[i], options[i], weights[i] / sum, matched[i]);
        }

        var diagnostics = new DecisionDiagnostics(
            Math.Min(1.0, optionMass), missing, floor, next.Usage, next.ServedModel, next.ResponseId, next.Candidates);
        return new Decision(probabilities, diagnostics);
    }

    private static bool IsLetter(string? token, char letter)
    {
        if (token is null)
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = token.AsSpan().Trim();
        return trimmed.Length == 1 && trimmed[0] == letter;
    }
}

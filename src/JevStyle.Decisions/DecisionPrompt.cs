using System.Text;

namespace JevStyle.Decisions;

/// <summary>
/// Builds the exact prompt that Jev-style decision models are trained on. The model answers with the
/// letter of one option, so the next-token distribution over the option letters is the decision.
/// </summary>
/// <remarks>
/// Line endings are always <c>\n</c>, independent of the operating system, so the prompt is byte-identical
/// on Windows, Linux and macOS.
/// </remarks>
public static class DecisionPrompt
{
    /// <summary>The fixed instruction line that starts every prompt.</summary>
    public const string Header = "You are a decision function. Read the state, then answer the question by choosing exactly one option.";

    /// <summary>Option letters in declaration order.</summary>
    public const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The smallest number of options a decision can have.</summary>
    public const int MinOptions = 2;

    /// <summary>The largest number of options the prompt format supports.</summary>
    public const int MaxOptions = 26;

    /// <summary>Returns the option letter for a zero-based option index.</summary>
    /// <param name="index">Zero-based option index.</param>
    public static char LetterFor(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxOptions);
        return Letters[index];
    }

    /// <summary>Builds the prompt text for one decision.</summary>
    /// <param name="state">What the model should read: a document, record or JSON snapshot.</param>
    /// <param name="question">The question to answer about the state.</param>
    /// <param name="options">The declared options, in order. Between 2 and 26.</param>
    /// <returns>The prompt, ending with <c>Answer:</c>.</returns>
    public static string Build(string state, string question, IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(question);
        ValidateOptions(options, MaxOptions);

        int capacity = Header.Length + state.Length + question.Length + 48 + options.Sum(option => option.Length + 4);

        var builder = new StringBuilder(capacity)
            .Append(Header)
            .Append("\n\n[State]\n").Append(state)
            .Append("\n\n[Question]\n").Append(question)
            .Append("\n\n[Options]\n");

        for (int i = 0; i < options.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Letters[i]).Append(". ").Append(options[i]);
        }

        return builder.Append("\n\nAnswer:").ToString();
    }

    internal static void ValidateOptions(IReadOnlyList<string> options, int maxOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        int limit = Math.Min(maxOptions, MaxOptions);
        if (options.Count < MinOptions || options.Count > limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Count, $"A decision needs between {MinOptions} and {limit} options.");
        }

        for (int i = 0; i < options.Count; i++)
        {
            if (options[i] is null)
            {
                throw new ArgumentException($"Option {i} is null.", nameof(options));
            }
        }
    }
}

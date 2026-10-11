namespace JevStyle.Decisions;

/// <summary>A request for one typed decision.</summary>
/// <param name="State">What the model reads.</param>
/// <param name="Question">The question about the state.</param>
/// <param name="Options">Declared options in order. The model cannot answer outside them.</param>
public sealed record DecisionRequest(string State, string Question, IReadOnlyList<string> Options);

/// <summary>The probability of one declared option.</summary>
/// <param name="Index">Zero-based position in the declared options.</param>
/// <param name="Letter">The letter the model answers with.</param>
/// <param name="Option">The option text.</param>
/// <param name="Probability">Probability after renormalising over the declared options.</param>
/// <param name="Logprob">Raw log-probability of the best matching token, or <see langword="null"/> when the letter was not among the returned candidates.</param>
public readonly record struct OptionProbability(int Index, char Letter, string Option, double Probability, double? Logprob);

/// <summary>Details that help judge whether a decision can be trusted.</summary>
/// <param name="OptionMass">Probability the model put on the declared option letters before renormalising (0 to 1). Low values mean the model wanted to write something other than an option letter, which usually points to a prompt format problem.</param>
/// <param name="MissingOptions">Declared letters that were not among the returned candidates. They receive the floor log-probability.</param>
/// <param name="FloorLogprob">Log-probability assigned to missing letters: the lowest returned candidate minus <see cref="LogprobDecoder.MissingOptionPenalty"/>.</param>
/// <param name="Usage">Token usage, when reported.</param>
/// <param name="ServedModel">The model the server says it used.</param>
/// <param name="ResponseId">Server response id.</param>
/// <param name="Candidates">All candidates the server returned for the position.</param>
public sealed record DecisionDiagnostics(
    double OptionMass,
    int MissingOptions,
    double FloorLogprob,
    TokenUsage? Usage,
    string? ServedModel,
    string? ResponseId,
    IReadOnlyList<TokenLogprob> Candidates);

/// <summary>A decision: one probability per declared option.</summary>
public sealed class Decision
{
    private OptionProbability[]? _ranked;

    /// <summary>Creates a decision from probabilities in declared order.</summary>
    /// <param name="options">One entry per declared option, in declared order.</param>
    /// <param name="diagnostics">Diagnostics for the decision.</param>
    public Decision(IReadOnlyList<OptionProbability> options, DecisionDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (options.Count == 0)
        {
            throw new ArgumentException("A decision needs at least one option.", nameof(options));
        }

        Options = options;
        Diagnostics = diagnostics;
        OptionProbability selected = options[0];
        foreach (OptionProbability option in options)
        {
            if (option.Probability > selected.Probability)
            {
                selected = option;
            }
        }

        Selected = selected;
    }

    /// <summary>Probabilities in declared order.</summary>
    public IReadOnlyList<OptionProbability> Options { get; }

    /// <summary>The most likely option. Ties go to the earliest declared option.</summary>
    public OptionProbability Selected { get; }

    /// <summary>Options from most to least likely.</summary>
    public IReadOnlyList<OptionProbability> Ranked => _ranked ??= [.. Options.OrderByDescending(o => o.Probability).ThenBy(o => o.Index)];

    /// <summary>Diagnostics for the decision.</summary>
    public DecisionDiagnostics Diagnostics { get; }

    /// <summary>Probability of the option at a declared position.</summary>
    /// <param name="index">Zero-based option index.</param>
    public double ProbabilityAt(int index) => Options[index].Probability;

    /// <summary>Probability of the first option whose text matches exactly (ordinal).</summary>
    /// <param name="option">Option text.</param>
    /// <exception cref="KeyNotFoundException">The option was not declared.</exception>
    public double ProbabilityOf(string option)
    {
        foreach (OptionProbability candidate in Options)
        {
            if (string.Equals(candidate.Option, option, StringComparison.Ordinal))
            {
                return candidate.Probability;
            }
        }

        throw new KeyNotFoundException($"'{option}' is not one of the declared options.");
    }
}

/// <summary>A Bool decision: the probability that a proposition is true.</summary>
/// <param name="Probability">Probability of <c>yes</c>.</param>
/// <param name="Decision">The underlying yes/no decision.</param>
public sealed record BoolDecision(double Probability, Decision Decision)
{
    /// <summary><see langword="true"/> when <see cref="Probability"/> is at least 0.5.</summary>
    public bool Value => Probability >= 0.5;
}

/// <summary>A Score decision over ordered levels.</summary>
/// <param name="ExpectedLevel">Expected zero-based level: the sum of index times probability.</param>
/// <param name="Decision">The underlying decision over the levels, lowest first.</param>
public sealed record ScoreDecision(double ExpectedLevel, Decision Decision);

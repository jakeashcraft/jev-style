using JevStyle.Benchmark.Dataset;
using JevStyle.Benchmark.Running;

namespace JevStyle.Benchmark.Reporting;

/// <summary>One answered row: P(true) and whether thresholding it at 0.5 matches the expected answer.</summary>
internal sealed record ScoredAnswer(ExpressionRow Row, double Probability, bool Correct);

/// <summary>
/// bench.mts's per-config summary (same definitions: wall time of the timed pass, latency percentiles over
/// successful requests, accuracy at P(yes) &gt;= 0.5), plus Brier score, output tokens, reported cost, served
/// models and the local model's option mass.
/// </summary>
internal sealed class PassSummary
{
    private PassSummary(PassResult pass)
    {
        Pass = pass;
    }

    public PassResult Pass { get; }

    public required IReadOnlyList<ScoredAnswer> Scored { get; init; }

    public double ExpressionsPerSecond => Scored.Count / (Pass.WallMs / 1000);

    public double? P50Ms { get; private init; }

    public double? P95Ms { get; private init; }

    public double? MaxMs { get; private init; }

    public int Retries { get; private init; }

    /// <summary>Requests still failing after every retry.</summary>
    public int Failed { get; private init; }

    /// <summary>Percent of answered rows that match the evaluator.</summary>
    public double? Accuracy { get; private init; }

    public double? AccuracyTrue { get; private init; }

    public double? AccuracyFalse { get; private init; }

    public double? Brier { get; private init; }

    public long InputTokens { get; private init; }

    public long OutputTokens { get; private init; }

    /// <summary>Sum of the cost the server reported, or null when it reported none.</summary>
    public double? Cost { get; private init; }

    public int RequestsWithCost { get; private init; }

    public double? MeanOptionMass { get; private init; }

    public double? MinOptionMass { get; private init; }

    /// <summary>Error messages and how often each occurred, in order of first occurrence.</summary>
    public required IReadOnlyList<KeyValuePair<string, int>> Failures { get; init; }

    public required IReadOnlyList<KeyValuePair<string, int>> ServedModels { get; init; }

    public required IReadOnlyList<KeyValuePair<string, int>> Providers { get; init; }

    public static PassSummary From(PassResult pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var scored = new List<ScoredAnswer>();
        for (int i = 0; i < pass.Outcomes.Count; i++)
        {
            if (pass.Outcomes[i].Reply is not { } reply)
            {
                continue;
            }

            scored.AddRange(from row in pass.Requests[i].Rows
                let probability = reply.Probabilities[row.Id]
                select new ScoredAnswer(row, probability, probability >= 0.5 == row.Expected));
        }

        double[] latencies = [.. pass.Outcomes.Where(o => o.Reply is not null).Select(o => o.Ms).Order()];
        SenderReply[] replies = [.. pass.Outcomes.Select(o => o.Reply).OfType<SenderReply>()];
        double[] costs = [.. replies.Select(r => r.Cost).OfType<double>()];
        double[] mass = [.. replies.Select(r => r.OptionMass).OfType<double>()];
        return new PassSummary(pass)
        {
            Scored = scored,
            P50Ms = Percentile(latencies, 0.5),
            P95Ms = Percentile(latencies, 0.95),
            MaxMs = latencies.Length == 0 ? null : latencies[^1],
            Retries = pass.Outcomes.Sum(o => o.Retries),
            Failed = pass.Outcomes.Count - latencies.Length,
            Accuracy = PercentCorrect(scored),
            AccuracyTrue = PercentCorrect([.. scored.Where(s => s.Row.Expected)]),
            AccuracyFalse = PercentCorrect([.. scored.Where(s => !s.Row.Expected)]),
            Brier = scored.Count == 0 ? null : scored.Average(s => Math.Pow(s.Probability - (s.Row.Expected ? 1 : 0), 2)),
            InputTokens = replies.Sum(r => (long)(r.InputTokens ?? 0)),
            OutputTokens = replies.Sum(r => (long)(r.OutputTokens ?? 0)),
            Cost = costs.Length == 0 ? null : costs.Sum(),
            RequestsWithCost = costs.Length,
            MeanOptionMass = mass.Length == 0 ? null : mass.Average(),
            MinOptionMass = mass.Length == 0 ? null : mass.Min(),
            Failures = Count(pass.Outcomes.Select(o => o.Error).OfType<string>()),
            ServedModels = Count(replies.Select(r => r.ServedModel).OfType<string>()),
            Providers = Count(replies.Select(r => r.Provider).OfType<string>()),
        };
    }

    /// <summary>bench.mts's percentile: <c>sorted[min(n - 1, floor(p * n))]</c>, not nearest rank.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double fraction) =>
        sorted.Count == 0 ? null : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(fraction * sorted.Count))];

    /// <summary>bench.mts's round: <c>Number(value.toFixed(digits))</c>. Null for NaN and infinity, as JSON.stringify writes them.</summary>
    public static double? Round(double? value, int digits = 0) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v, digits, MidpointRounding.AwayFromZero) : null;

    private static double? PercentCorrect(IReadOnlyCollection<ScoredAnswer> subset) =>
        subset.Count == 0 ? null : 100.0 * subset.Count(s => s.Correct) / subset.Count;

    private static List<KeyValuePair<string, int>> Count(IEnumerable<string> values)
    {
        var counts = new List<KeyValuePair<string, int>>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (positions.TryGetValue(value, out int position))
            {
                counts[position] = new KeyValuePair<string, int>(value, counts[position].Value + 1);
            }
            else
            {
                positions[value] = counts.Count;
                counts.Add(new KeyValuePair<string, int>(value, 1));
            }
        }

        return counts;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using JevStyle.Benchmark.Dataset;
using static JevStyle.Benchmark.Reporting.Format;

namespace JevStyle.Benchmark.Reporting;

/// <summary>
/// Compares runs from bench.mts and jev-style-bench on the rows every run answered. Expected answers come from
/// the dataset, so files only need their probabilities.
/// </summary>
internal static partial class RunComparison
{
    private const int _maxMissedRows = 40;
    private const string _space = JsText.WhiteSpaceClass;

    private static readonly string[] _shapes =
    [
        "One tag compared with true or false",
        "One tag compared with a number",
        "One tag, compound (ranges, OR lists)",
        "Two or more tags",
        "No tags",
    ];

    public static string Render(ExpressionDataset dataset, IReadOnlyList<RecordedRun> runs)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(runs);
        var rows = dataset.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        string[] labels = [.. runs.Select((r, i) => $"{i + 1}. {r.Model ?? "unknown model"}")];
        ExpressionRow[] common = [.. dataset.Rows.Where(row => runs.All(run => run.Probabilities.ContainsKey(row.Id)))];
        var lines = new List<string>
        {
            "# Run comparison",
            string.Empty,
            $"{common.Length:N0} rows answered by every run, out of {dataset.Rows.Count:N0} in the dataset. Expected answers come from the dataset's own evaluator; a probability of 0.5 or more counts as true.",
            string.Empty,
        };

        foreach (RecordedRun run in runs)
        {
            int unknown = run.Probabilities.Keys.Count(id => !rows.ContainsKey(id));
            if (unknown > 0)
            {
                lines.Add($"> {run.Source} has {unknown} row ids that are not in this dataset. Check that --data points at the dataset the run used.");
            }

            if (run.Rules is { } rules && rules != ExpressionDataset.Rules)
            {
                lines.Add($"> {run.Source} was run with different rules text from today's bench.mts, so its prompts differ slightly: \"{Md(rules)}\".");
            }
        }

        if (lines[^1].StartsWith('>'))
        {
            lines.Add(string.Empty);
        }

        lines.Add("| Run | File | Provider | Batch x conc | Answered | Accuracy | True / false recall | Brier | Wall s | Expr/s | p50 ms | Input tokens | Cost |");
        lines.Add("| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < runs.Count; i++)
        {
            RecordedRun run = runs[i];
            Answer[] answered = [.. Answers(run, dataset.Rows.Where(r => run.Probabilities.ContainsKey(r.Id)))];
            lines.Add(
                $"| {Md(labels[i])} | {Md(run.Source)} | {Md(run.Provider ?? "n/a")} | {run.Batch?.ToString(CultureInfo.InvariantCulture) ?? "?"} x {run.Concurrency?.ToString(CultureInfo.InvariantCulture) ?? "?"} | " +
                $"{answered.Length:N0} | {Percent(Accuracy(answered))} | {Percent(Recall(answered, true))} / {Percent(Recall(answered, false))} | {Number(Brier(answered), 4)} | " +
                $"{Number(run.WallSeconds, 3)} | {Number(run.ExpressionsPerSecond, 0)} | {Number(run.P50Ms, 0)} | {Number(run.Tokens)} | {(run.Cost is { } c ? "$" + Number(c, 5) : "n/a")} |");
        }

        lines.Add(string.Empty);
        lines.Add("bench.mts files price input tokens at Jev's $0.042 per million; jev-style-bench files sum the cost the server reported. Wall time is one timed pass, so batch and concurrency change it more than the model does.");
        if (common.Length == 0)
        {
            return string.Join('\n', lines) + "\n";
        }

        lines.AddRange([string.Empty, "## Accuracy by expression shape (rows every run answered)", string.Empty, Header("Shape", labels), Divider(labels.Length)]);
        lines.AddRange(from shape in _shapes
            let subset = (ExpressionRow[])[.. common.Where(r => Shape(r) == shape)]
            where subset.Length > 0
            select $"| {shape} | {subset.Length:N0} | " +
                   string.Join(" | ", runs.Select(run => Percent(Accuracy([.. Answers(run, subset)])))) + " |");

        var outcomes = common.ToDictionary(r => r.Id, r => ExpressionAnalysis.Outcomes(r, dataset.Literals));
        ExpressionRow[] constant = [.. common.Where(r => outcomes[r.Id] is { } o && o.CanBeTrue != o.CanBeFalse)];
        if (constant.Length > 0)
        {
            ExpressionRow[] variable = [.. common.Where(r => outcomes[r.Id] is { CanBeTrue: true, CanBeFalse: true })];
            lines.AddRange([
                string.Empty,
                "## Rows that can never change value",
                string.Empty,
                "A row \"can't change\" when it gives the same answer for every combination of its tags' candidate values (each compared constant and its neighbours, or true/false), for example `STEP != 13 || STEP != 14`.",
                string.Empty,
                Header("Group", labels),
                Divider(labels.Length),
                $"| Can't change: always true | {constant.Count(r => outcomes[r.Id]!.Value.CanBeTrue):N0} | " + string.Join(" | ", runs.Select(run => Percent(Accuracy([.. Answers(run, constant.Where(r => outcomes[r.Id]!.Value.CanBeTrue))]))) ) + " |",
                $"| Can't change: always false | {constant.Count(r => outcomes[r.Id]!.Value.CanBeFalse):N0} | " + string.Join(" | ", runs.Select(run => Percent(Accuracy([.. Answers(run, constant.Where(r => outcomes[r.Id]!.Value.CanBeFalse))]))) ) + " |",
                $"| Can change | {variable.Length:N0} | " + string.Join(" | ", runs.Select(run => Percent(Accuracy([.. Answers(run, variable)])))) + " |",
            ]);
        }

        if (runs.Count is >= 2 and <= 12)
        {
            lines.AddRange([
                string.Empty, "## Same prediction (rows every run answered)", string.Empty,
                "| | " + string.Join(" | ", labels.Select(Md)) + " |",
                "| --- |" + string.Concat(labels.Select(_ => " ---: |"))
            ]);
            lines.AddRange(runs
                .Select(t => runs.Select(other =>
                    Percent(
                        (double)common.Count(r => t.Probabilities[r.Id] >= 0.5 == other.Probabilities[r.Id] >= 0.5) /
                        common.Length))).Select((cells, i) => $"| {Md(labels[i])} | {string.Join(" | ", cells)} |"));
        }

        var missed = common
            .Select(row => (Row: row, Misses: runs.Count(run => run.Probabilities[row.Id] >= 0.5 != row.Expected)))
            .Where(m => m.Misses > 0)
            .OrderByDescending(m => m.Misses)
            .ThenBy(m => m.Row.Line)
            .ToArray();
        lines.AddRange([string.Empty, $"## Rows missed by at least one run ({missed.Length:N0})", string.Empty]);
        if (missed.Length == 0)
        {
            lines.Add("None.");
        }
        else
        {
            lines.Add("P(true) per run; bold is wrong.");
            lines.Add(string.Empty);
            lines.Add("| Row | Name | Expected | " + string.Join(" | ", runs.Select((_, i) => (i + 1).ToString(CultureInfo.InvariantCulture))) + " | Expression |");
            lines.Add("| --- | --- | --- |" + string.Concat(runs.Select(_ => " ---: |")) + " --- |");
            foreach ((ExpressionRow row, _) in missed.Take(_maxMissedRows))
            {
                IEnumerable<string> cells = runs.Select(run =>
                {
                    double p = run.Probabilities[row.Id];
                    string value = p.ToString("0.00", CultureInfo.InvariantCulture);
                    return p >= 0.5 != row.Expected ? $"**{value}**" : value;
                });
                string text = row.Text.Length > 90 ? row.Text[..90] + "..." : row.Text;
                lines.Add($"| {row.Id} | {Md(row.Name)} | {Bool(row.Expected)} | {string.Join(" | ", cells)} | `{Md(text)}` |");
            }

            if (missed.Length > _maxMissedRows)
            {
                lines.Add(string.Empty);
                lines.Add($"{missed.Length - _maxMissedRows} more not shown.");
            }
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    internal static string Shape(ExpressionRow row)
    {
        switch (row.Tags.Count)
        {
            case 0:
                return _shapes[4];
            case > 1:
                return _shapes[3];
            default:
            {
                Match match = SingleComparison().Match(row.Text);
                return !match.Success ? _shapes[2] : match.Groups["literal"].Value.ToLowerInvariant() is "true" or "false" ? _shapes[0] : _shapes[1];
            }
        }
    }

    private readonly record struct Answer(ExpressionRow Row, double Probability)
    {
        public bool Correct => Probability >= 0.5 == Row.Expected;
    }

    private static IEnumerable<Answer> Answers(RecordedRun run, IEnumerable<ExpressionRow> rows) =>
        rows.Where(r => run.Probabilities.ContainsKey(r.Id)).Select(r => new Answer(r, run.Probabilities[r.Id]));

    private static double? Accuracy(IReadOnlyCollection<Answer> answers) => answers.Count == 0 ? null : (double)answers.Count(a => a.Correct) / answers.Count;

    private static double? Recall(IEnumerable<Answer> answers, bool expected)
    {
        Answer[] subset = [.. answers.Where(a => a.Row.Expected == expected)];
        return subset.Length == 0 ? null : (double)subset.Count(a => a.Correct) / subset.Length;
    }

    private static double? Brier(IReadOnlyCollection<Answer> answers) =>
        answers.Count == 0 ? null : answers.Average(a => Math.Pow(a.Probability - (a.Row.Expected ? 1 : 0), 2));

    private static string Header(string first, IEnumerable<string> labels) => $"| {first} | Rows | {string.Join(" | ", labels.Select(Md))} |";

    private static string Divider(int runs) => "| --- | ---: |" + string.Concat(Enumerable.Repeat(" ---: |", runs));

    // `TAG op literal`, optionally wrapped in one pair of parentheses.
    [GeneratedRegex("^\\(?[" + _space + "]*[A-Za-z_][A-Za-z0-9_]*[" + _space + "]*(?:==|!=|>=|<=|>|<)[" + _space + "]*(?<literal>true|false|-?[0-9]+(?:\\.[0-9]+)?)[" + _space + "]*\\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleComparison();
}

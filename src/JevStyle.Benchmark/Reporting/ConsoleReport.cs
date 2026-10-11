using System.Text;
using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Reporting;

/// <summary>Console output in bench.mts's words: a line per pass while it runs, then one table.</summary>
internal static class ConsoleReport
{
    private static readonly string[] _columns = ["batch", "conc", "reqs", "wall s", "expr/s", "p50 ms", "p95 ms", "max ms", "retries", "failed", "acc %", "tokens", "cost $"];

    public static string Header(ExpressionDataset dataset, IReadOnlyList<ExpressionRow> selected)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(selected);
        int expectedTrue = selected.Count(r => r.Expected);
        return $"{selected.Count} expressions, {dataset.Literals.Tags.Count} tags, expected {expectedTrue} true / {selected.Count - expectedTrue} false\n" +
            $"local evaluation of all {dataset.Rows.Count} rows: {dataset.LocalMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} ms";
    }

    /// <summary>bench.mts's line per config, plus up to three failure messages.</summary>
    public static string PassLine(PassSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var builder = new StringBuilder()
            .Append($"batch {s.Pass.Batch} x conc {s.Pass.Concurrency}: {Js(PassSummary.Round(s.Pass.WallMs / 1000, 3))} s, {Js(PassSummary.Round(s.ExpressionsPerSecond))} expr/s, ")
            .Append($"{Js(PassSummary.Round(s.Accuracy, 2))}% correct, {s.Retries} retries, {s.Failed} failed");
        if (s.Pass.Interrupted)
        {
            builder.Append(" (interrupted)");
        }

        foreach (KeyValuePair<string, int> failure in s.Failures.Take(3))
        {
            string message = failure.Key.Length > 200 ? failure.Key[..200] : failure.Key;
            builder.Append($"\n  {failure.Value}x {message}");
        }

        return builder.ToString();
    }

    public static string Table(IReadOnlyList<PassSummary> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        string[][] rows = [.. passes.Select(s => new[]
        {
            Js(s.Pass.Batch), Js(s.Pass.Concurrency), Js(s.Pass.Requests.Count), Js(PassSummary.Round(s.Pass.WallMs / 1000, 3)),
            Js(PassSummary.Round(s.ExpressionsPerSecond)), Js(PassSummary.Round(s.P50Ms)), Js(PassSummary.Round(s.P95Ms)), Js(PassSummary.Round(s.MaxMs)),
            Js(s.Retries), Js(s.Failed), Js(PassSummary.Round(s.Accuracy, 2)), Js(s.InputTokens), Js(PassSummary.Round(s.Cost, 5)),
        })];
        int[] widths = [.. _columns.Select((c, i) => Math.Max(c.Length, rows.Length == 0 ? 0 : rows.Max(r => r[i].Length)))];
        var builder = new StringBuilder();
        builder.AppendLine(string.Join("  ", _columns.Select((c, i) => c.PadLeft(widths[i]))));
        foreach (string[] row in rows)
        {
            builder.AppendLine(string.Join("  ", row.Select((v, i) => v.PadLeft(widths[i]))));
        }

        return builder.ToString().TrimEnd();
    }

    private static string Js(double? value) => value is { } v ? JsNumber.Format(v) : "n/a";

    private static string Js(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

using System.Text.Encodings.Web;
using System.Text.Json;
using JevStyle.Benchmark.Dataset;
using JevStyle.Benchmark.Running;

namespace JevStyle.Benchmark.Reporting;

/// <summary>Everything about a run that is not a measurement.</summary>
internal sealed record RunInfo(
    string ToolVersion,
    DateTimeOffset CreatedAt,
    string Runtime,
    string Provider,
    string Url,
    string Model,
    string Request,
    string Example,
    ServerModelInfo? Server,
    IReadOnlyList<string> Notes,
    string? StopReason);

/// <summary>
/// Writes one results file per invocation in bench.mts's shape (<c>model, rules, expressions, expectedTrue,
/// localMs, runs[].summary/failures/requests/mismatches/probabilities</c>, same keys and rounding), so the two
/// tools' files can be read the same way. Extra fields only add information.
/// </summary>
internal static class ResultsWriter
{
    private static readonly JsonWriterOptions _options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Write(Stream stream, RunInfo info, ExpressionDataset dataset, IReadOnlyList<ExpressionRow> selected, IReadOnlyList<PassSummary> passes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(passes);
        using var writer = new Utf8JsonWriter(stream, _options);
        writer.WriteStartObject();
        writer.WriteString("model", info.Model);
        writer.WriteString("rules", ExpressionDataset.Rules);
        writer.WriteNumber("expressions", selected.Count);
        writer.WriteNumber("expectedTrue", selected.Count(r => r.Expected));
        writer.WriteNumber("localMs", dataset.LocalMs);
        writer.WriteString("tool", "jev-style-bench");
        writer.WriteString("toolVersion", info.ToolVersion);
        writer.WriteString("createdAt", info.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("runtime", info.Runtime);
        writer.WriteString("provider", info.Provider);
        writer.WriteString("url", info.Url);
        writer.WriteStartObject("request");
        writer.WriteString("description", info.Request);
        writer.WriteString("example", info.Example);
        writer.WriteEndObject();
        if (info.Server is { } server)
        {
            writer.WriteStartObject("server");
            String(writer, "type", server.Type);
            String(writer, "publisher", server.Publisher);
            String(writer, "arch", server.Arch);
            String(writer, "format", server.Format);
            String(writer, "quantization", server.Quantization);
            String(writer, "state", server.State);
            Number(writer, "maxContextLength", server.MaxContextLength);
            Number(writer, "loadedContextLength", server.LoadedContextLength);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("data");
        String(writer, "directory", dataset.Directory);
        String(writer, "csvSha256", dataset.CsvSha256);
        String(writer, "tagsSha256", dataset.TagsSha256);
        writer.WriteNumber("rows", dataset.Rows.Count);
        writer.WriteNumber("tags", dataset.Literals.Tags.Count);
        writer.WriteBoolean("snapshotGenerated", dataset.SnapshotGenerated);
        writer.WriteStartArray("skipped");
        foreach (SkippedRow skipped in dataset.Skipped)
        {
            writer.WriteStringValue(skipped.ToString());
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartArray("notes");
        foreach (string note in info.Notes)
        {
            writer.WriteStringValue(note);
        }

        writer.WriteEndArray();
        String(writer, "stopReason", info.StopReason);
        writer.WriteStartArray("runs");
        foreach (PassSummary pass in passes)
        {
            WriteRun(writer, pass, dataset.Snapshot);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRun(Utf8JsonWriter writer, PassSummary s, TagSnapshot snapshot)
    {
        PassResult pass = s.Pass;
        writer.WriteStartObject();
        writer.WriteStartObject("summary");
        writer.WriteNumber("batch", pass.Batch);
        writer.WriteNumber("conc", pass.Concurrency);
        writer.WriteNumber("reqs", pass.Requests.Count);
        Number(writer, "build ms", PassSummary.Round(pass.BuildMs, 2));
        Number(writer, "warmup ms", PassSummary.Round(pass.WarmupMs));
        Number(writer, "wall s", PassSummary.Round(pass.WallMs / 1000, 3));
        Number(writer, "expr/s", PassSummary.Round(s.ExpressionsPerSecond));
        Number(writer, "p50 ms", PassSummary.Round(s.P50Ms));
        Number(writer, "p95 ms", PassSummary.Round(s.P95Ms));
        Number(writer, "max ms", PassSummary.Round(s.MaxMs));
        writer.WriteNull("server p50 ms");
        writer.WriteNumber("retries", s.Retries);
        writer.WriteNumber("failed", s.Failed);
        Number(writer, "acc %", PassSummary.Round(s.Accuracy, 2));
        Number(writer, "acc true %", PassSummary.Round(s.AccuracyTrue, 2));
        Number(writer, "acc false %", PassSummary.Round(s.AccuracyFalse, 2));
        writer.WriteNumber("tokens", s.InputTokens);
        Number(writer, "cost $", PassSummary.Round(s.Cost, 5));
        writer.WriteNumber("output tokens", s.OutputTokens);
        writer.WriteNumber("answered", s.Scored.Count);
        Number(writer, "brier", PassSummary.Round(s.Brier, 4));
        writer.WriteNumber("requests with cost", s.RequestsWithCost);
        Number(writer, "option mass mean", PassSummary.Round(s.MeanOptionMass, 4));
        Number(writer, "option mass min", PassSummary.Round(s.MinOptionMass, 4));
        writer.WriteBoolean("interrupted", pass.Interrupted);
        writer.WriteEndObject();

        Counts(writer, "failures", s.Failures);
        Counts(writer, "servedModels", s.ServedModels);
        Counts(writer, "providers", s.Providers);

        writer.WriteStartArray("requests");
        foreach (RequestOutcome outcome in pass.Outcomes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("expressions", outcome.Expressions);
            Number(writer, "ms", PassSummary.Round(outcome.Ms));
            writer.WriteNumber("retries", outcome.Retries);
            writer.WriteNumber("tokens", outcome.Reply?.InputTokens ?? 0);
            if (outcome.Error is { } error)
            {
                writer.WriteString("error", error);
            }

            if (outcome.StatusCode is { } status)
            {
                writer.WriteNumber("status", status);
            }

            if (outcome.Reply is { } reply)
            {
                Number(writer, "outputTokens", reply.OutputTokens);
                Number(writer, "cost", reply.Cost);
                String(writer, "servedModel", reply.ServedModel);
                String(writer, "provider", reply.Provider);
                String(writer, "responseId", reply.ResponseId);
                Number(writer, "optionMass", reply.OptionMass);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("mismatches");
        foreach (ScoredAnswer miss in s.Scored.Where(a => !a.Correct))
        {
            writer.WriteStartObject();
            writer.WriteString("id", miss.Row.Id);
            writer.WriteString("name", miss.Row.Name);
            writer.WriteString("expr", miss.Row.Text);
            writer.WriteStartObject("state");
            foreach (string tag in miss.Row.Tags)
            {
                Value(writer, tag, snapshot[tag]);
            }

            writer.WriteEndObject();
            writer.WriteBoolean("expected", miss.Row.Expected);
            writer.WriteNumber("probability", miss.Probability);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("probabilities");
        foreach (ScoredAnswer answer in s.Scored)
        {
            writer.WriteNumber(answer.Row.Id, answer.Probability);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void Value(Utf8JsonWriter writer, string name, TagValue value)
    {
        if (value.IsBoolean)
        {
            writer.WriteBoolean(name, value.Truthy);
        }
        else
        {
            writer.WriteNumber(name, value.Number);
        }
    }

    private static void Counts(Utf8JsonWriter writer, string name, IReadOnlyList<KeyValuePair<string, int>> counts)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, int> count in counts)
        {
            writer.WriteNumber(count.Key, count.Value);
        }

        writer.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            writer.WriteNumber(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void Number(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } v)
        {
            writer.WriteNumber(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}

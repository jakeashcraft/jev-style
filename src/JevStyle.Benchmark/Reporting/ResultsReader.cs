using System.Globalization;
using System.Text.Json;

namespace JevStyle.Benchmark.Reporting;

/// <summary>One timed pass read back from a results file: bench.mts's or this tool's.</summary>
internal sealed record RecordedRun(
    string Path,
    int Number,
    string Tool,
    string? Model,
    string? Provider,
    string? Rules,
    int? Batch,
    int? Concurrency,
    double? WallSeconds,
    double? ExpressionsPerSecond,
    double? P50Ms,
    long? Tokens,
    double? Cost,
    int? Failed,
    IReadOnlyDictionary<string, double> Probabilities)
{
    public string Source => $"{System.IO.Path.GetFileName(Path)}#{Number}";
}

/// <summary>A results file that cannot be read.</summary>
internal sealed class ResultsFileException(string message) : Exception(message);

/// <summary>
/// Reads results files. bench.mts files have no tool or provider field; they come from the TypeSafe API, and
/// their <c>cost $</c> is computed at Jev's price rather than reported.
/// </summary>
internal static class ResultsReader
{
    /// <summary>Reads every run in a file, or one run with <c>file.json#2</c> (1-based).</summary>
    public static IReadOnlyList<RecordedRun> Read(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int? pick = null;
        string path = spec;
        int hash = spec.LastIndexOf('#');
        if (hash > 0 && int.TryParse(spec[(hash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            pick = number;
            path = spec[..hash];
        }

        if (!File.Exists(path))
        {
            throw new ResultsFileException($"{path} not found");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
            {
                throw new ResultsFileException($"{path} is not a results file from bench.mts or jev-style-bench (no runs array)");
            }

            string tool = Text(root, "tool") ?? "bench.mts";
            string? provider = Text(root, "provider") ?? (tool == "bench.mts" ? "typesafe" : null);
            var result = new List<RecordedRun>();
            int index = 0;
            foreach (JsonElement run in runs.EnumerateArray())
            {
                index++;
                if (pick is { } wanted && wanted != index)
                {
                    continue;
                }

                JsonElement summary = run.TryGetProperty("summary", out JsonElement s) ? s : default;
                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                if (run.TryGetProperty("probabilities", out JsonElement values) && values.ValueKind == JsonValueKind.Object)
                {
                    foreach (var value in values.EnumerateObject().Where(value => value.Value.ValueKind == JsonValueKind.Number))
                    {
                        probabilities[value.Name] = value.Value.GetDouble();
                    }
                }

                result.Add(new RecordedRun(
                    path,
                    index,
                    tool,
                    Text(root, "model"),
                    provider,
                    Text(root, "rules"),
                    (int?)Number(summary, "batch"),
                    (int?)Number(summary, "conc"),
                    Number(summary, "wall s"),
                    Number(summary, "expr/s"),
                    Number(summary, "p50 ms"),
                    (long?)Number(summary, "tokens"),
                    Number(summary, "cost $"),
                    (int?)Number(summary, "failed"),
                    probabilities));
            }

            if (pick is { } missing && result.Count == 0)
            {
                throw new ResultsFileException($"{path} has no run #{missing} ({index} runs)");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new ResultsFileException($"{path} is not valid JSON: {exception.Message}");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

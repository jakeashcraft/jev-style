using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JevStyle.Benchmark.Dataset;

/// <summary>A problem with the dataset folder: missing files, invalid tags.json, tags without values.</summary>
internal sealed class DatasetException(string message) : Exception(message);

/// <summary>A CSV row bench.mts would skip, with its 1-based line number.</summary>
internal sealed record SkippedRow(int Line, string Reason)
{
    public override string ToString() => $"line {Line}: {Reason}";
}

/// <summary>One expression: id <c>r{line}</c> as bench.mts names it, the activity name, and the answer for the snapshot.</summary>
internal sealed record ExpressionRow(string Id, int Line, string Name, Expression Expression, bool Expected = false)
{
    public string Text => Expression.Text;

    public IReadOnlyList<string> Tags => Expression.Tags;
}

/// <summary>
/// The workload of jev-expression-benchmark: <c>data/expressions.csv</c> (one <c>Name,ExpressionScript</c> pair per
/// line) evaluated against one simulated PLC snapshot, <c>data/tags.json</c>. Reads both exactly the way bench.mts
/// does, so the same rows get the same ids, states and expected answers.
/// </summary>
internal sealed partial record ExpressionDataset
{
    /// <summary>The rules bench.mts puts in front of every expression.</summary>
    public const string Rules =
        "Evaluate the Boolean expression against the tag values in the state; tag names are state keys. " +
        "&& is AND, || is OR, and && binds tighter than ||. true equals 1 and false equals 0.";

    public required IReadOnlyList<ExpressionRow> Rows { get; init; }

    public required IReadOnlyList<SkippedRow> Skipped { get; init; }

    public required TagLiterals Literals { get; init; }

    public required TagSnapshot Snapshot { get; init; }

    /// <summary>True when tags.json was missing and the snapshot was generated.</summary>
    public bool SnapshotGenerated { get; init; }

    /// <summary>Time to evaluate every row against the snapshot, for scale.</summary>
    public double LocalMs { get; init; }

    public string? Directory { get; init; }

    public string? CsvSha256 { get; init; }

    public string? TagsSha256 { get; init; }

    public int ExpectedTrue => Rows.Count(row => row.Expected);

    /// <summary>The instructions bench.mts sends for a row: the rules, then the expression.</summary>
    public static string Question(ExpressionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{Rules}\nExpression: {row.Text}";
    }

    /// <summary>Splits the CSV like bench.mts: one row per line, name before the first comma, no quoting.</summary>
    public static (List<ExpressionRow> Rows, List<SkippedRow> Skipped) ParseRows(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = new List<ExpressionRow>();
        var skipped = new List<SkippedRow>();
        string[] lines = LineBreak().Split(csv);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (index == 0 || JsText.Trim(line).Length == 0)
            {
                continue;
            }

            int comma = line.IndexOf(',', StringComparison.Ordinal);
            string text = JsText.Trim(line[(comma + 1)..]);
            string name = comma >= 0 ? line[..comma] : line[..^1]; // JavaScript: slice(0, -1) when there is no comma
            try
            {
                rows.Add(new ExpressionRow($"r{index + 1}", index + 1, name, Expression.Parse(text)));
            }
            catch (ExpressionSyntaxException exception)
            {
                skipped.Add(new SkippedRow(index + 1, exception.Message));
            }
        }

        return (rows, skipped);
    }

    /// <summary>Builds the dataset from file contents. A null <paramref name="tagsJson"/> generates the snapshot.</summary>
    public static ExpressionDataset Create(string csv, string? tagsJson)
    {
        (List<ExpressionRow> rows, List<SkippedRow> skipped) = ParseRows(csv);
        var literals = TagLiterals.Collect(rows);
        TagSnapshot snapshot = tagsJson is null ? SnapshotGenerator.Generate(rows, literals) : TagSnapshot.Parse(tagsJson);
        string[] missing = [.. literals.Tags.Where(tag => !snapshot.Contains(tag))];
        if (missing.Length > 0)
        {
            throw new DatasetException($"data/tags.json is missing {missing.Length} tags (e.g. {missing[0]}) - delete it to regenerate");
        }

        long started = Stopwatch.GetTimestamp();
        ExpressionRow[] evaluated = [.. rows.Select(row => row with { Expected = row.Expression.Evaluate(snapshot) })];
        double localMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new ExpressionDataset
        {
            Rows = evaluated,
            Skipped = skipped,
            Literals = literals,
            Snapshot = snapshot,
            SnapshotGenerated = tagsJson is null,
            LocalMs = localMs,
            CsvSha256 = Sha256(csv),
            TagsSha256 = tagsJson is null ? null : Sha256(tagsJson),
        };
    }

    /// <summary>
    /// Reads <c>expressions.csv</c> and <c>tags.json</c> from a folder. When tags.json is missing it is generated
    /// and, unless <paramref name="writeGeneratedSnapshot"/> is false, written there as bench.mts does, so both
    /// tools keep using the same snapshot.
    /// </summary>
    public static ExpressionDataset Load(string directory, bool writeGeneratedSnapshot = true)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string csvPath = Path.Combine(directory, "expressions.csv");
        string tagsPath = Path.Combine(directory, "tags.json");
        if (!File.Exists(csvPath))
        {
            throw new DatasetException($"{csvPath} not found. Point --data at a folder with expressions.csv and tags.json, such as jev-expression-benchmark's data folder.");
        }

        string csv = File.ReadAllText(csvPath, Encoding.UTF8);
        string? tags = File.Exists(tagsPath) ? File.ReadAllText(tagsPath, Encoding.UTF8) : null;
        ExpressionDataset dataset = Create(csv, tags) with { Directory = Path.GetFullPath(directory) };

        if (tags is not null || !writeGeneratedSnapshot) return dataset;
        string generated = dataset.Snapshot.ToIndentedJson();
        File.WriteAllText(tagsPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        dataset = dataset with { TagsSha256 = Sha256(generated) };

        return dataset;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex("\r?\n")]
    private static partial Regex LineBreak();
}

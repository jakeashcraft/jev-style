using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Running;

/// <summary>
/// One request, built the way bench.mts builds them: a chunk of rows in CSV order (which keeps related
/// equipment together) and a state holding only the tags those rows use.
/// </summary>
internal sealed record BenchRequest(IReadOnlyList<ExpressionRow> Rows, TagSnapshot State)
{
    /// <summary>
    /// bench.mts's warm-up payload. The timed pass never sends it, so no cache anywhere can flatter the numbers.
    /// </summary>
    public static BenchRequest Warmup { get; } = new(
        [new ExpressionRow("warmup", 0, "warmup", Expression.Parse("WARMUP_TAG == true"), Expected: true)],
        new TagSnapshot(new List<KeyValuePair<string, TagValue>> { new("WARMUP_TAG", TagValue.True) }.AsReadOnly()));

    public static IReadOnlyList<BenchRequest> Build(IReadOnlyList<ExpressionRow> rows, TagSnapshot snapshot, int batch)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        var requests = new List<BenchRequest>((rows.Count + batch - 1) / batch);
        for (int start = 0; start < rows.Count; start += batch)
        {
            ExpressionRow[] chunk = [.. rows.Skip(start).Take(batch)];
            requests.Add(new BenchRequest(chunk, snapshot.Subset(chunk.SelectMany(row => row.Tags))));
        }

        return requests;
    }
}

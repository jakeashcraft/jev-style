using System.Text.Json;
using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Tests;

/// <summary>
/// The C# port against bench.mts itself. Fixtures/reference.json comes from running bench.mts's own loading,
/// compiling, literal and snapshot code (generate-reference.mjs) on the published dataset and on a set of edge
/// cases, so these tests fail if any row, tag, domain, snapshot value or answer differs.
/// </summary>
public sealed class ParityTests
{
    public static TheoryData<string> Scenarios => ["published", "edge"];

    [Fact]
    public void Rules_text_is_bench_mts_constant()
    {
        string? reference = TestData.ReferenceRoot.GetProperty("rules").GetString();
        Assert.True(reference == ExpressionDataset.Rules, $"bench.mts rules changed: {reference}");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Rows_ids_names_tags_and_skipped_lines_match(string scenario)
    {
        JsonElement reference = TestData.Scenario(scenario);
        ExpressionDataset dataset = Create(reference);

        JsonElement[] expected = [.. reference.GetProperty("rows").EnumerateArray()];
        Assert.Equal(expected.Length, dataset.Rows.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            ExpressionRow row = dataset.Rows[i];
            Assert.Equal(expected[i].GetProperty("id").GetString(), row.Id);
            Assert.Equal(expected[i].GetProperty("name").GetString(), row.Name);
            Assert.Equal(expected[i].GetProperty("expr").GetString(), row.Text);
            Assert.Equal(expected[i].GetProperty("tags").EnumerateArray().Select(t => t.GetString()), row.Tags);
            Assert.True(expected[i].GetProperty("expected").GetBoolean() == row.Expected, $"{row.Id} {row.Text}");
        }

        Assert.Equal(reference.GetProperty("skipped").EnumerateArray().Select(s => s.GetInt32()), dataset.Skipped.Select(s => s.Line));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Literals_and_domains_match(string scenario)
    {
        JsonElement reference = TestData.Scenario(scenario);
        ExpressionDataset dataset = Create(reference);

        JsonElement[] literals = [.. reference.GetProperty("literals").EnumerateArray()];
        Assert.Equal(literals.Select(l => l[0].GetString()), dataset.Literals.Tags);
        foreach (JsonElement entry in literals)
        {
            Assert.Equal(entry[1].EnumerateArray().Select(l => l.GetString()), dataset.Literals[entry[0].GetString()!]);
        }

        foreach (JsonElement entry in reference.GetProperty("domains").EnumerateArray())
        {
            Assert.Equal(entry[1].EnumerateArray().Select(TestData.Value), SnapshotGenerator.DomainOf(dataset.Literals[entry[0].GetString()!]));
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Snapshot_and_generated_snapshot_match_in_order(string scenario)
    {
        JsonElement reference = TestData.Scenario(scenario);
        ExpressionDataset dataset = Create(reference);

        Assert.Equal(TestData.Entries(reference.GetProperty("snapshot")), dataset.Snapshot);
        TagSnapshot generated = SnapshotGenerator.Generate(dataset.Rows, dataset.Literals);
        Assert.Equal(TestData.Entries(reference.GetProperty("generated")), generated);
        Assert.Equal(reference.GetProperty("snapshotText").GetString(), dataset.Snapshot.ToIndentedJson());
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_row_matches_for_every_random_state(string scenario)
    {
        JsonElement reference = TestData.Scenario(scenario);
        ExpressionDataset dataset = Create(reference);
        int checkedStates = 0;
        foreach (JsonElement state in reference.GetProperty("states").EnumerateArray())
        {
            var snapshot = new TagSnapshot(TestData.Entries(state.GetProperty("state")));
            string truth = state.GetProperty("truth").GetString()!;
            string actual = string.Concat(dataset.Rows.Select(row => row.Expression.Evaluate(snapshot) ? '1' : '0'));
            Assert.Equal(truth, actual);
            checkedStates++;
        }

        Assert.True(checkedStates >= 20);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Request_states_match_bench_mts_chunks(string scenario)
    {
        JsonElement reference = TestData.Scenario(scenario);
        ExpressionDataset dataset = Create(reference);
        JsonElement[] batches = [.. reference.GetProperty("batchStates").EnumerateArray()];
        int[] sizes = [1, 50];
        for (int b = 0; b < sizes.Length; b++)
        {
            IReadOnlyList<Running.BenchRequest> requests = Running.BenchRequest.Build(dataset.Rows, dataset.Snapshot, sizes[b]);
            string[] expected = [.. batches[b].EnumerateArray().Select(s => s.GetString()!)];
            Assert.Equal(expected, requests.Take(expected.Length).Select(r => r.State.ToJson()));
        }
    }

    [Fact]
    public void Published_snapshot_regenerates_from_the_csv_alone()
    {
        // The jev-expression-benchmark README says regenerating tags.json gives the same value for every tag.
        JsonElement reference = TestData.Scenario("published");
        var generated = ExpressionDataset.Create(reference.GetProperty("csv").GetString()!, tagsJson: null);
        ExpressionDataset shipped = Create(reference);
        Assert.True(generated.SnapshotGenerated);
        Assert.Equal(shipped.Snapshot.OrderBy(e => e.Key, StringComparer.Ordinal), generated.Snapshot.OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Equal(shipped.Rows.Select(r => r.Expected), generated.Rows.Select(r => r.Expected));
    }

    [Fact]
    public void Vendored_data_folder_is_the_published_dataset()
    {
        // data/ is a copy of jev-expression-benchmark's data folder; the test project copies it next to the tests.
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        JsonElement reference = TestData.Scenario("published");
        Assert.Equal(reference.GetProperty("csv").GetString(), File.ReadAllText(Path.Combine(data, "expressions.csv")));
        Assert.Equal(reference.GetProperty("tags").GetString(), File.ReadAllText(Path.Combine(data, "tags.json")));
    }

    private static ExpressionDataset Create(JsonElement reference)
    {
        JsonElement tags = reference.GetProperty("tags");
        return ExpressionDataset.Create(reference.GetProperty("csv").GetString()!, tags.ValueKind == JsonValueKind.Null ? null : tags.GetString());
    }
}

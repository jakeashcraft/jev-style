using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Tests;

public sealed class ExpressionTests
{
    [Theory]
    [InlineData("A == true || B == true && C == true", "A=false B=true C=false", false)] // && binds tighter than ||
    [InlineData("!A == B", "A=true B=false", true)]                                       // (!A) == B
    [InlineData("A == T > 150", "A=true T=151", true)]                                   // A == (T > 150)
    [InlineData("A == T > 150", "A=true T=150", false)]
    [InlineData("T < U < 2", "T=5 U=3", true)]                                           // (5 < 3) < 2, false is 0
    [InlineData("A == 1", "A=true", true)]                                               // loose equality
    [InlineData("A != 0", "A=false", false)]
    [InlineData("A > 0", "A=true", true)]
    [InlineData("A == TRUE || B == False", "A=false B=false", true)]
    [InlineData("(T || A) == 151", "T=151 A=false", true)]                               // || returns an operand
    [InlineData("(T || A) == 151", "T=0 A=true", false)]
    [InlineData("A && T", "A=true T=0", false)]
    [InlineData("T -1 > 149", "T=151", true)]                                            // A -1 is A - 1
    [InlineData("T -1 > 149", "T=150", false)]
    [InlineData("N > 1-1", "N=1", true)]
    [InlineData("N > -1 && N <= 3", "N=0", true)]
    [InlineData("F >= 1.5 && F < 2.25", "F=2.25", false)]
    [InlineData("S <= 13 && S >= 14", "S=13", false)]
    [InlineData("S != 13 || S != 14", "S=13", true)]
    [InlineData("!!A && !(B || C)", "A=1 B=0 C=false", true)]
    [InlineData("1 == 1", "", true)]
    public void Evaluates_with_javascript_semantics(string text, string state, bool expected)
    {
        var expression = Expression.Parse(text);
        Assert.Equal(expected, expression.Evaluate(Snapshot(state)));
    }

    [Theory]
    [InlineData("A === B")]
    [InlineData("A = 1")]
    [InlineData("foo(A)")]
    [InlineData("(A)(B)")]
    [InlineData("@A == true")]
    [InlineData("")]
    [InlineData("A &&")]
    [InlineData("(A")]
    [InlineData("A)")]
    [InlineData("A B")]
    [InlineData("1 2")]
    [InlineData("if == true")]
    [InlineData("A == null")]
    [InlineData("A == 'x'")]
    [InlineData("A & B")]
    [InlineData("A == 010")]   // JavaScript would read a legacy octal literal
    [InlineData("A > 1e5")]    // or an exponent
    [InlineData("A == 0x1F")]  // or hex digits
    public void Rejects_rows_bench_mts_skips_or_the_grammar_excludes(string text)
    {
        Assert.Throws<ExpressionSyntaxException>(() => Expression.Parse(text));
    }

    [Fact]
    public void Tags_are_in_order_of_first_appearance_without_literals()
    {
        var expression = Expression.Parse("B == true && A == 1 && B == False && TRUE");
        Assert.Equal(["B", "A"], expression.Tags);
        Assert.Throws<ArgumentException>(() => expression.Evaluate([TagValue.True]));
    }

    [Fact]
    public void Tags_json_keeps_order_and_rejects_other_types()
    {
        var snapshot = TagSnapshot.Parse("""{"B": 2, "A": true, "B": 3.5}""");
        Assert.Equal(["B", "A"], snapshot.Tags);
        Assert.Equal(TagValue.Of(3.5), snapshot["B"]);
        Assert.Equal("""{"B":3.5,"A":true}""", snapshot.ToJson());
        Assert.Equal("{\n  \"B\": 3.5,\n  \"A\": true\n}", snapshot.ToIndentedJson());
        Assert.Throws<DatasetException>(() => TagSnapshot.Parse("""{"A": "on"}"""));
        Assert.Throws<DatasetException>(() => TagSnapshot.Parse("[true]"));
        Assert.Throws<DatasetException>(() => TagSnapshot.Parse("{"));
    }

    [Fact]
    public void Missing_tag_values_stop_the_run()
    {
        DatasetException exception = Assert.Throws<DatasetException>(() =>
            ExpressionDataset.Create(TestData.SmallCsv, """{"PUMP_1":true,"VALVE_1":false,"TT_RECIRC_TEMP":151}"""));
        Assert.Equal("data/tags.json is missing 1 tags (e.g. STEP_NO) - delete it to regenerate", exception.Message);
    }

    [Fact]
    public void Load_generates_tags_json_when_it_is_missing()
    {
        string folder = TestData.DatasetFolder(TestData.SmallCsv, tagsJson: null);
        var readOnly = ExpressionDataset.Load(folder, writeGeneratedSnapshot: false);
        Assert.True(readOnly.SnapshotGenerated);
        Assert.False(File.Exists(Path.Combine(folder, "tags.json")));

        var dataset = ExpressionDataset.Load(folder);
        Assert.Equal(dataset.Snapshot.ToIndentedJson(), File.ReadAllText(Path.Combine(folder, "tags.json")));
        Assert.False(ExpressionDataset.Load(folder).SnapshotGenerated);
    }

    [Fact]
    public void Question_is_the_rules_then_the_expression()
    {
        var dataset = ExpressionDataset.Create(TestData.SmallCsv, TestData.SmallTags);
        Assert.Equal(
            ExpressionDataset.Rules + "\nExpression: STEP_NO == 15",
            ExpressionDataset.Question(dataset.Rows[2]));
        Assert.Equal(["r2", "r3", "r4", "r5"], dataset.Rows.Select(r => r.Id));
        Assert.Equal([true, false, false, true], dataset.Rows.Select(r => r.Expected));
    }

    private static TagSnapshot Snapshot(string state)
    {
        var snapshot = new TagSnapshot();
        foreach (string pair in state.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=');
            snapshot.Set(parts[0], parts[1] switch
            {
                "true" => TagValue.True,
                "false" => TagValue.False,
                string number => TagValue.Of(double.Parse(number, System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        return snapshot;
    }
}

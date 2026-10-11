using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JevStyle.Benchmark.Cli;
using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Tests;

/// <summary>End to end through the command line, against a fake LM Studio and a fake OpenRouter.</summary>
public sealed class CliTests
{
    private static readonly ExpressionDataset _dataset = ExpressionDataset.Create(TestData.SmallCsv, TestData.SmallTags);

    [Fact]
    public async Task Local_model_run_writes_a_bench_mts_shaped_results_file()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => FakeHandler.Json("""{"data":[{"id":"jev-style-qwen3.5-2b-decision","object":"model"}]}"""),
            "/v1/chat/completions" => FakeHandler.Json(ChatCompletion(yes: 0.9, no: 0.1)),
            _ => FakeHandler.Json("""{"error":"not found"}""", HttpStatusCode.NotFound),
        });
        string results = ResultsPath();

        (int exit, string output, string error) = await RunAsync(handler, "--data", Folder(), "--url", "http://lmstudio.test:1234", "--concurrency", "1,2", "--output", results);

        Assert.True(exit == 0, error);
        Assert.Contains("4 expressions, 4 tags, expected 2 true / 2 false", output);
        Assert.Contains("batch 1 x conc 1:", output);
        Assert.Contains("batch 1 x conc 2:", output);
        using var json = JsonDocument.Parse(File.ReadAllText(results));
        JsonElement root = json.RootElement;
        Assert.Equal("jev-style-qwen3.5-2b-decision", root.GetProperty("model").GetString());
        Assert.Equal(ExpressionDataset.Rules, root.GetProperty("rules").GetString());
        Assert.Equal(4, root.GetProperty("expressions").GetInt32());
        Assert.Equal(2, root.GetProperty("expectedTrue").GetInt32());
        Assert.Equal("logprobs", root.GetProperty("provider").GetString());
        JsonElement run = root.GetProperty("runs")[1];
        JsonElement summary = run.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("conc").GetInt32());
        Assert.Equal(4, summary.GetProperty("reqs").GetInt32());
        Assert.Equal(50, summary.GetProperty("acc %").GetDouble());
        Assert.Equal(100, summary.GetProperty("acc true %").GetDouble());
        Assert.Equal(0, summary.GetProperty("acc false %").GetDouble());
        Assert.Equal(320, summary.GetProperty("tokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("cost $").ValueKind);
        Assert.Equal(0.9, run.GetProperty("probabilities").GetProperty("r4").GetDouble(), 6);
        Assert.Equal(["r3", "r4"], run.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("""{"STEP_NO":14}""", JsonSerializer.Serialize(run.GetProperty("mismatches")[1].GetProperty("state")));

        string prompt = handler.Requests
            .Where(r => r.Request.RequestUri!.AbsolutePath == "/v1/chat/completions")
            .Select(r => JsonNode.Parse(r.Body)!["messages"]![0]!["content"]!.GetValue<string>())
            .First(p => p.Contains("PUMP_1 == true", StringComparison.Ordinal));
        Assert.Contains("[State]\n{\"PUMP_1\":true,\"VALVE_1\":false,\"TT_RECIRC_TEMP\":151}\n", prompt);
        Assert.Contains(ExpressionDataset.Rules + "\nExpression: PUMP_1 == true && VALVE_1 == false && TT_RECIRC_TEMP > 150\nDoes the expression evaluate to true?", prompt);
        Assert.Contains("[Options]\nA. yes\nB. no\n\nAnswer:", prompt);
    }

    [Fact]
    public async Task Hosted_run_batches_questions_like_bench_mts_and_sums_the_reported_cost()
    {
        FakeHandler handler = OpenRouter();
        string results = ResultsPath();

        (int exit, _, string error) = await RunAsync(handler, "--provider", "decisions", "--model", "typesafe/jev-1.13", "--api-key", "test-key", "--data", Folder(), "--batch", "1,2", "--concurrency", "4", "--output", results);

        Assert.True(exit == 0, error);
        using var json = JsonDocument.Parse(File.ReadAllText(results));
        JsonElement[] runs = [.. json.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal([(1, 4), (2, 2)], runs.Select(r => (r.GetProperty("summary").GetProperty("batch").GetInt32(), r.GetProperty("summary").GetProperty("conc").GetInt32())));
        JsonElement batched = runs[1].GetProperty("summary");
        Assert.Equal(100, batched.GetProperty("acc %").GetDouble());
        Assert.Equal(0.00002, batched.GetProperty("cost $").GetDouble());
        Assert.Equal(2, runs[1].GetProperty("servedModels").GetProperty("typesafe/jev-1.13-20260917").GetInt32());

        (HttpRequestMessage request, string body) = handler.Requests.First(r => r.Body.Contains("\"r3\"", StringComparison.Ordinal) && r.Body.Contains("\"r2\"", StringComparison.Ordinal));
        Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
        Assert.Equal(
            "{\"model\":\"typesafe/jev-1.13\",\"state\":{\"PUMP_1\":true,\"VALVE_1\":false,\"TT_RECIRC_TEMP\":151}," +
            "\"questions\":{\"r2\":{\"type\":\"noul\",\"instructions\":\"" + Escape(ExpressionDataset.Rules) + "\\nExpression: PUMP_1 == true && VALVE_1 == false && TT_RECIRC_TEMP > 150\"}," +
            "\"r3\":{\"type\":\"noul\",\"instructions\":\"" + Escape(ExpressionDataset.Rules) + "\\nExpression: PUMP_1 == false || TT_RECIRC_TEMP < 110\"}}}",
            body);
    }

    [Theory]
    [InlineData("The local model reads one expression per prompt", "--batch", "2")]
    [InlineData("--batch expects positive integers", "--batch", "1,0")]
    [InlineData("--option-order only applies to the local model", "--provider", "decisions", "--api-key", "k", "--option-order", "false-first")]
    [InlineData("--limit must be a positive integer", "--limit", "0")]
    public async Task Invalid_combinations_are_rejected_before_any_request(string message, params string[] args)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));
        (int exit, _, string error) = await RunAsync(handler, [.. args, "--data", Folder()]);
        Assert.Equal(1, exit);
        Assert.Contains(message, error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Dry_run_prints_the_first_request_and_sends_nothing()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));
        (int exit, string output, _) = await RunAsync(handler, "--provider", "decisions", "--dry-run", "--batch", "2,4", "--data", Folder());
        Assert.Equal(0, exit);
        Assert.Contains("batch 2: 2 requests", output);
        Assert.Contains("batch 4: 1 requests", output);
        Assert.Contains("\"questions\":{\"r2\":", output);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_authentication_failure_ends_the_sweep_with_exit_code_1()
    {
        var handler = new FakeHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHandler.Json("""{"data":[{"id":"microsoft/microsoft-decision-1"}]}""")
            : FakeHandler.Json("""{"error":{"code":401,"message":"No auth credentials found"}}""", HttpStatusCode.Unauthorized));
        string results = ResultsPath();

        (int exit, _, string error) = await RunAsync(handler, "--provider", "decisions", "--api-key", "bad", "--data", Folder(), "--concurrency", "2,4", "--output", results);

        Assert.Equal(1, exit);
        Assert.Contains("every warm-up request failed: HTTP 401 (Unauthorized): No auth credentials found", error);
        using var json = JsonDocument.Parse(File.ReadAllText(results));
        Assert.Equal(0, json.RootElement.GetProperty("runs").GetArrayLength());
        Assert.Equal("not sent: an earlier request failed with HTTP 401", json.RootElement.GetProperty("stopReason").GetString());
        Assert.Equal(2, handler.Requests.Count(r => r.Request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Compare_reads_bench_mts_and_jev_style_files_against_the_dataset()
    {
        string ours = ResultsPath();
        (int exit, _, string error) = await RunAsync(OpenRouter(), "--provider", "decisions", "--model", "typesafe/jev-1.13", "--api-key", "k", "--data", Folder(), "--concurrency", "4", "--output", ours);
        Assert.True(exit == 0, error);
        string bench = ResultsPath();
        File.WriteAllText(bench, new JsonObject
        {
            ["model"] = "jev-latest",
            ["rules"] = ExpressionDataset.Rules,
            ["expressions"] = 4,
            ["expectedTrue"] = 2,
            ["localMs"] = 0.1,
            ["runs"] = new JsonArray(new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["batch"] = 1, ["conc"] = 16, ["reqs"] = 4, ["wall s"] = 0.5, ["expr/s"] = 8, ["p50 ms"] = 100, ["failed"] = 0, ["acc %"] = 75, ["tokens"] = 400, ["cost $"] = 0.00002,
                },
                ["probabilities"] = new JsonObject { ["r2"] = 0.98, ["r3"] = 0.03, ["r4"] = 0.6, ["r5"] = 0.97 },
            }),
        }.ToJsonString());
        string markdown = Path.ChangeExtension(ResultsPath(), ".md");

        (exit, string output, error) = await RunAsync(OpenRouter(), "compare", bench, ours + "#1", "--data", Folder(), "--output", markdown);

        Assert.True(exit == 0, error);
        Assert.Equal(output, File.ReadAllText(markdown));
        Assert.Contains("| 1. jev-latest | ", output);
        Assert.Contains("| typesafe | 1 x 16 | 4 | 75% |", output);
        Assert.Contains("| 2. typesafe/jev-1.13 | ", output);
        Assert.Contains("| Can't change: always true | 1 |", output);
        Assert.Contains("## Rows missed by at least one run (1)", output);
        Assert.Contains("| r4 | Drain | false | **0.60** | 0.03 |", output);
    }

    private static FakeHandler OpenRouter() => new((request, body) =>
    {
        if (request.Method == HttpMethod.Get)
        {
            return FakeHandler.Json("""{"data":[{"id":"typesafe/jev-1.13","canonical_slug":"typesafe/jev-1.13-20260917"}]}""");
        }

        var answers = new JsonObject();
        foreach ((string id, _) in JsonNode.Parse(body)!["questions"]!.AsObject())
        {
            bool expected = id == "warmup" || _dataset.Rows.Single(r => r.Id == id).Expected;
            answers[id] = new JsonObject { ["type"] = "noul", ["noul"] = expected ? 0.97 : 0.03 };
        }

        var response = new JsonObject
        {
            ["model"] = "typesafe/jev-1.13-20260917",
            ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 200, ["output_tokens"] = 20, ["cost"] = 0.00001 },
            ["id"] = "gen-dec-test",
            ["provider"] = "TypeSafe",
        };
        return FakeHandler.Json(response.ToJsonString());
    });

    private static string ChatCompletion(double yes, double no) => new JsonObject
    {
        ["id"] = "chatcmpl-test",
        ["object"] = "chat.completion",
        ["model"] = "jev-style-qwen3.5-2b-decision",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = " A" },
            ["logprobs"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["token"] = " A",
                    ["logprob"] = Math.Log(yes),
                    ["top_logprobs"] = new JsonArray(
                        new JsonObject { ["token"] = " A", ["logprob"] = Math.Log(yes), ["bytes"] = new JsonArray() },
                        new JsonObject { ["token"] = " B", ["logprob"] = Math.Log(no), ["bytes"] = new JsonArray() }),
                }),
            },
            ["finish_reason"] = "length",
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 80, ["completion_tokens"] = 1, ["total_tokens"] = 81 },
    }.ToJsonString();

    private static string Escape(string text) => JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];

    private static string Folder() => TestData.DatasetFolder(TestData.SmallCsv, TestData.SmallTags);

    private static string ResultsPath() => Path.Combine(Path.GetTempPath(), "jev-style-tests", Guid.NewGuid().ToString("N") + ".json");

    private static async Task<(int Exit, string Output, string Error)> RunAsync(FakeHandler handler, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = await BenchmarkCli.RunAsync(args, output, error, () => handler);
        return (exit, output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), error.ToString());
    }
}

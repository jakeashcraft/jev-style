using System.Collections.Concurrent;
using System.Net;
using JevStyle.Benchmark.Dataset;
using JevStyle.Benchmark.Reporting;
using JevStyle.Benchmark.Running;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Tests;

public sealed class SweepRunnerTests
{
    private static readonly ExpressionDataset _dataset = ExpressionDataset.Create(TestData.SmallCsv, TestData.SmallTags);

    [Fact]
    public async Task Sweeps_batches_and_distinct_capped_concurrencies_with_one_warmup_per_worker()
    {
        var sender = new FakeSender((request, _) => Task.FromResult(Correct(request)));
        var runner = new SweepRunner(sender, new SweepOptions([1, 2], [16, 3, 64]));

        IReadOnlyList<PassResult> passes = await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None);

        Assert.Equal([(1, 4), (1, 3), (2, 2)], passes.Select(p => (p.Batch, p.Concurrency)));
        Assert.Equal(4 + 3 + 2, sender.Calls.Count(c => c.Rows[0].Id == "warmup"));
        Assert.Equal(4 + 4 + 2, sender.Calls.Count(c => c.Rows[0].Id != "warmup"));
        BenchRequest pair = passes[2].Requests[0];
        Assert.Equal(["r2", "r3"], pair.Rows.Select(r => r.Id));
        Assert.Equal("""{"PUMP_1":true,"VALVE_1":false,"TT_RECIRC_TEMP":151}""", pair.State.ToJson());
        Assert.All(passes, p => Assert.Equal(100, PassSummary.From(p).Accuracy));
    }

    [Fact]
    public async Task Retries_retryable_errors_with_bench_mts_delays_and_keeps_the_time_on_the_clock()
    {
        int attempts = 0;
        var waits = new ConcurrentBag<TimeSpan>();
        var sender = new FakeSender((request, _) =>
        {
            if (request.Rows[0].Id == "r2" && Interlocked.Increment(ref attempts) <= 2)
            {
                throw attempts == 1
                    ? new DecisionServerException(HttpStatusCode.TooManyRequests, "HTTP 429")
                    : new DecisionServerException(HttpStatusCode.ServiceUnavailable, "HTTP 503", retryAfter: TimeSpan.FromSeconds(2));
            }

            return Task.FromResult(Correct(request));
        });
        var runner = new SweepRunner(sender, new SweepOptions([1], [1], Retries: 2), random: () => 0, delay: (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        PassResult pass = Assert.Single(await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None));

        Assert.Equal(2, pass.Outcomes[0].Retries);
        Assert.Null(pass.Outcomes[0].Error);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2)], waits.Order());
        Assert.Equal(2, PassSummary.From(pass).Retries);
    }

    [Fact]
    public async Task Does_not_retry_client_errors_or_bad_answers()
    {
        var sender = new FakeSender((request, _) => request.Rows[0].Id switch
        {
            "r2" => throw new DecisionServerException(HttpStatusCode.BadRequest, "HTTP 400 (Bad Request): max_tokens_exceeded"),
            "r3" => throw new DecisionFormatException("The response has no answer for 'r3'."),
            _ => Task.FromResult(Correct(request)),
        });
        var runner = new SweepRunner(sender, new SweepOptions([1], [1], Retries: 2), delay: (_, _) => Task.CompletedTask);

        PassResult pass = Assert.Single(await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None));
        var summary = PassSummary.From(pass);

        Assert.Equal(2, summary.Failed);
        Assert.Equal(0, summary.Retries);
        Assert.Equal(400, pass.Outcomes[0].StatusCode);
        Assert.Equal(2, summary.Scored.Count);
        Assert.Equal(["HTTP 400 (Bad Request): max_tokens_exceeded", "The response has no answer for 'r3'."], summary.Failures.Select(f => f.Key));
    }

    [Fact]
    public async Task Times_out_each_attempt_and_retries_it()
    {
        var sender = new FakeSender(async (request, cancellationToken) =>
        {
            if (request.Rows[0].Id == "r4")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Correct(request);
        });
        var runner = new SweepRunner(sender, new SweepOptions([1], [2], Retries: 1, TimeoutMs: 50), delay: (_, _) => Task.CompletedTask);

        PassResult pass = Assert.Single(await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None));

        RequestOutcome timedOut = pass.Outcomes[2];
        Assert.Equal("timeout after 50 ms", timedOut.Error);
        Assert.Equal(1, timedOut.Retries);
        Assert.True(timedOut.Ms >= 100);
        Assert.Equal(2, sender.Calls.Count(c => c.Rows[0].Id == "r4"));
    }

    [Fact]
    public async Task An_authentication_failure_stops_the_queued_requests_and_later_configs()
    {
        var sender = new FakeSender((request, _) => request.Rows[0].Id == "r3"
            ? throw new DecisionServerException(HttpStatusCode.Unauthorized, "HTTP 401 (Unauthorized): No auth credentials found")
            : Task.FromResult(Correct(request)));
        var runner = new SweepRunner(sender, new SweepOptions([1], [1, 2]));

        PassResult pass = Assert.Single(await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None));

        Assert.Null(pass.Outcomes[0].Error);
        Assert.Equal(401, pass.Outcomes[1].StatusCode);
        Assert.All(pass.Outcomes.Skip(2), o => Assert.Equal("not sent: an earlier request failed with HTTP 401", o.Error));
        Assert.Equal("not sent: an earlier request failed with HTTP 401", runner.StopReason);
        Assert.DoesNotContain(sender.Calls, c => c.Rows[0].Id == "r4");
    }

    [Fact]
    public async Task Skips_a_config_whose_warmup_fails_and_keeps_going()
    {
        var notices = new List<string>();
        var sender = new FakeSender((request, _) => request.Rows[0].Id == "warmup"
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(Correct(request)));
        var runner = new SweepRunner(sender, new SweepOptions([1], [1], Retries: 0));

        IReadOnlyList<PassResult> passes = await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, CancellationToken.None, onNotice: notices.Add);

        Assert.Empty(passes);
        Assert.Equal("batch 1 x conc 1: skipped, every warm-up request failed: connection refused", Assert.Single(notices));
    }

    [Fact]
    public async Task Interrupting_marks_the_rest_of_the_pass_as_not_sent()
    {
        using var interrupt = new CancellationTokenSource();
        var sender = new FakeSender((request, _) =>
        {
            if (request.Rows[0].Id == "r3")
            {
                interrupt.Cancel();
            }

            return Task.FromResult(Correct(request));
        });
        var runner = new SweepRunner(sender, new SweepOptions([1, 2], [1]));

        PassResult pass = Assert.Single(await runner.RunAsync(_dataset.Rows, _dataset.Snapshot, interrupt.Token));

        Assert.True(pass.Interrupted);
        Assert.Null(pass.Outcomes[1].Error);
        Assert.All(pass.Outcomes.Skip(2), o => Assert.Equal("not sent: run interrupted", o.Error));
    }

    [Fact]
    public void Percentiles_use_bench_mts_index_not_nearest_rank()
    {
        double[] sorted = [10, 20, 30, 40];
        Assert.Equal(30, PassSummary.Percentile(sorted, 0.5));
        Assert.Equal(40, PassSummary.Percentile(sorted, 0.95));
        Assert.Null(PassSummary.Percentile([], 0.5));
        Assert.Equal(3, PassSummary.Round(2.5));       // (2.5).toFixed(0) is "3"
        Assert.Equal(1.3, PassSummary.Round(1.25, 1)); // (1.25).toFixed(1) is "1.3"
        Assert.Null(PassSummary.Round(double.NaN));
    }

    private static SenderReply Correct(BenchRequest request) =>
        new(request.Rows.ToDictionary(r => r.Id, r => r.Expected ? 0.97 : 0.03), InputTokens: 100, OutputTokens: 1, Cost: 0.000004, ServedModel: "fake-1", Provider: "Fake");

    private sealed class FakeSender(Func<BenchRequest, CancellationToken, Task<SenderReply>> answer) : IRequestSender
    {
        public ConcurrentQueue<BenchRequest> Calls { get; } = new();

        public int MaxBatch => int.MaxValue;

        public string Describe() => "fake";

        public string Example(BenchRequest request) => request.State.ToJson();

        public Task<SenderReply> SendAsync(BenchRequest request, CancellationToken cancellationToken)
        {
            Calls.Enqueue(request);
            return answer(request, cancellationToken);
        }
    }
}

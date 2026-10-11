using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using JevStyle.Benchmark.Dataset;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Running;

/// <summary>Sweep settings. Each value in <see cref="Batches"/> runs once per distinct concurrency.</summary>
/// <param name="Batches">Expressions per request. Repeat a value to repeat a config.</param>
/// <param name="Concurrencies">Requests in flight. Capped at the number of requests.</param>
/// <param name="Retries">Extra attempts per request for retryable errors.</param>
/// <param name="TimeoutMs">Deadline per attempt.</param>
internal sealed record SweepOptions(IReadOnlyList<int> Batches, IReadOnlyList<int> Concurrencies, int Retries = 2, int TimeoutMs = 60_000);

/// <summary>How one request went. <see cref="Ms"/> covers every attempt and retry delay: how long the caller waited.</summary>
internal sealed record RequestOutcome(int Expressions, double Ms, int Retries, SenderReply? Reply, string? Error = null, int? StatusCode = null);

/// <summary>One timed pass: every request of one batch size at one concurrency.</summary>
internal sealed record PassResult(
    int Batch,
    int Concurrency,
    IReadOnlyList<BenchRequest> Requests,
    IReadOnlyList<RequestOutcome> Outcomes,
    double BuildMs,
    double WarmupMs,
    double WallMs,
    bool Interrupted);

/// <summary>
/// The measurement loop of bench.mts: for each batch size and concurrency, warm up one keep-alive connection per
/// worker with a throwaway payload, then send every request from a worker pool while the clock runs. Retryable
/// errors (connection failures, timeouts, 408, 429, 5xx) are retried by the same worker after 50 to 150 ms, then
/// 100 to 300 ms (or the server's Retry-After), and that time stays on the clock.
/// </summary>
internal sealed class SweepRunner(IRequestSender sender, SweepOptions options, Func<double>? random = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private string? _stopReason;

    /// <summary>Why the sweep stopped early (an authentication failure), if it did.</summary>
    public string? StopReason => Volatile.Read(ref _stopReason);

    public async Task<IReadOnlyList<PassResult>> RunAsync(
        IReadOnlyList<ExpressionRow> rows,
        TagSnapshot snapshot,
        CancellationToken cancellationToken,
        Action<PassResult>? onPass = null,
        Action<string>? onNotice = null,
        Action<int, int>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(snapshot);
        var passes = new List<PassResult>();

        foreach (int batch in options.Batches)
        {
            long buildStarted = Stopwatch.GetTimestamp();
            IReadOnlyList<BenchRequest> requests = BenchRequest.Build(rows, snapshot, batch);
            double buildMs = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
            var seen = new HashSet<int>();
            foreach (int requested in options.Concurrencies)
            {
                int concurrency = Math.Min(requested, requests.Count);
                if (!seen.Add(concurrency))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested || StopReason is not null)
                {
                    return passes;
                }

                // Untimed: one keep-alive connection per worker, so TLS handshakes stay out of the measurement.
                long warmupStarted = Stopwatch.GetTimestamp();
                RequestOutcome[] warmup = await SendAllAsync([.. Enumerable.Repeat(BenchRequest.Warmup, concurrency)], concurrency, cancellationToken, null).ConfigureAwait(false);
                double warmupMs = Stopwatch.GetElapsedTime(warmupStarted).TotalMilliseconds;
                if (warmup.All(outcome => outcome.Error is not null))
                {
                    // Skip, don't stop: the configs that already ran keep their results.
                    onNotice?.Invoke($"batch {batch} x conc {concurrency}: skipped, every warm-up request failed: {warmup[0].Error}");
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                RequestOutcome[] outcomes = await SendAllAsync(requests, concurrency, cancellationToken, onProgress).ConfigureAwait(false);
                double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var pass = new PassResult(batch, concurrency, requests, outcomes, buildMs, warmupMs, wallMs, cancellationToken.IsCancellationRequested);
                passes.Add(pass);
                onPass?.Invoke(pass);
                if (pass.Interrupted)
                {
                    return passes;
                }
            }
        }

        return passes;
    }

    private async Task<RequestOutcome[]> SendAllAsync(IReadOnlyList<BenchRequest> requests, int concurrency, CancellationToken cancellationToken, Action<int, int>? onProgress)
    {
        var outcomes = new RequestOutcome[requests.Count];
        int next = -1;
        int completed = 0;

        await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, requests.Count)).Select(_ => Task.Run(Worker, CancellationToken.None))).ConfigureAwait(false);
        return outcomes;

        async Task Worker()
        {
            while (true)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= requests.Count)
                {
                    return;
                }

                outcomes[index] = await SendAsync(requests[index], cancellationToken).ConfigureAwait(false);
                onProgress?.Invoke(Interlocked.Increment(ref completed), requests.Count);
            }
        }
    }

    private async Task<RequestOutcome> SendAsync(BenchRequest request, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int retries = 0; ; retries++)
        {
            if (StopReason is { } stop)
            {
                return new RequestOutcome(request.Rows.Count, Elapsed(started), retries, null, stop);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new RequestOutcome(request.Rows.Count, Elapsed(started), retries, null, "not sent: run interrupted");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.TimeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                SenderReply reply = await sender.SendAsync(request, linked.Token).ConfigureAwait(false);
                return new RequestOutcome(request.Rows.Count, Elapsed(started), retries, reply);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                bool interrupted = cancellationToken.IsCancellationRequested;
                bool timedOut = timeout.IsCancellationRequested && !interrupted;
                int? status = StatusCodeOf(exception);
                if (status is 401 or 403)
                {
                    Interlocked.CompareExchange(ref _stopReason, $"not sent: an earlier request failed with HTTP {status}", null);
                }

                if (!interrupted && retries < options.Retries && (timedOut || IsRetryable(exception, status)))
                {
                    TimeSpan wait = (exception as DecisionServerException)?.RetryAfter ?? TimeSpan.FromMilliseconds((retries + 1) * (50 + (_random() * 100)));
                    try
                    {
                        await _delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        interrupted = true;
                    }
                }

                string error = interrupted ? "cancelled: run interrupted" : timedOut ? $"timeout after {options.TimeoutMs} ms" : exception.Message;
                return new RequestOutcome(request.Rows.Count, Elapsed(started), retries, null, error, status);
            }
        }
    }

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static int? StatusCodeOf(Exception exception) => exception switch
    {
        DecisionServerException server => (int)server.StatusCode,
        HttpRequestException { StatusCode: { } status } => (int)status,
        _ => null,
    };

    /// <summary>The errors the TypeSafe SDK retries: connection failures, 408, 429 and 5xx.</summary>
    private static bool IsRetryable(Exception exception, int? status) => status switch
    {
        (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests => true,
        >= 500 and < 600 => true,
        not null => false,
        null => exception is HttpRequestException or IOException or SocketException,
    };
}

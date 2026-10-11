using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using JevStyle.Benchmark.Dataset;
using JevStyle.Benchmark.Reporting;
using JevStyle.Benchmark.Running;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Cli;

/// <summary>Runs one sweep end to end: load the data, check the server, run every config, report.</summary>
internal sealed class BenchmarkApp(TextWriter output, TextWriter error, Func<HttpMessageHandler>? handlerFactory = null)
{
    public async Task<int> RunAsync(BenchmarkSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ExpressionDataset dataset;
        try
        {
            dataset = ExpressionDataset.Load(settings.Data);
        }
        catch (Exception exception) when (exception is DatasetException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Benchmark failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        if (dataset.Skipped.Count > 0)
        {
            await error.WriteLineAsync($"skipped {dataset.Skipped.Count} rows:\n  {string.Join("\n  ", dataset.Skipped)}").ConfigureAwait(false);
        }

        if (dataset.SnapshotGenerated)
        {
            await output.WriteLineAsync($"{Path.Combine(settings.Data, "tags.json")} was missing, so it was generated from the expressions.");
        }

        ExpressionRow[] selected = [.. dataset.Rows.Take(settings.Limit ?? int.MaxValue)];
        if (selected.Length == 0)
        {
            await error.WriteLineAsync("Benchmark failed: the CSV has no valid rows.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(ConsoleReport.Header(dataset, selected));
        await output.WriteLineAsync();

        using HttpClient http = CreateHttpClient(settings);
        IRequestSender sender;
        string endpoint;
        ServerModelInfo? server = null;
        if (settings.Provider == Provider.Decisions)
        {
            var options = settings.ToDecisionsApiOptions();
            var api = new DecisionsApiClient(http, options);
            if (settings is { DryRun: false, SkipModelCheck: false } && await CheckDecisionModelAsync(api, options, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                await error.WriteLineAsync($"Benchmark failed: {problem}").ConfigureAwait(false);
                return 1;
            }

            sender = new DecisionsApiSender(api, options);
            endpoint = options.DecisionsUri.ToString();
        }
        else
        {
            var options = settings.ToClientOptions();
            if (settings is { DryRun: false, SkipModelCheck: false } && await CheckModelAsync(http, options, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                await error.WriteLineAsync($"Benchmark failed: {problem}").ConfigureAwait(false);
                return 1;
            }

            if (!settings.DryRun)
            {
                server = await ServerProbe.GetModelInfoAsync(http, options, cancellationToken).ConfigureAwait(false);
            }

            sender = new LogprobSender(new DecisionClient(new OpenAiChatLogprobSource(http, options)), settings.Order);
            endpoint = options.ChatCompletionsUri.ToString();
        }

        await output.WriteLineAsync($"model {settings.Model} at {endpoint}" + (server is null
            ? string.Empty
            : $" (LM Studio: {server.Quantization ?? "?"}, {server.Arch ?? "?"}, context {server.LoadedContextLength?.ToString(CultureInfo.InvariantCulture) ?? "?"})"));
        await output.WriteLineAsync($"request: {sender.Describe()}");
        string example = sender.Example(BenchRequest.Build(selected, dataset.Snapshot, settings.Batches[0])[0]);
        if (settings.DryRun)
        {
            foreach (int batch in settings.Batches.Distinct())
            {
                await output.WriteLineAsync($"batch {batch}: {BenchRequest.Build(selected, dataset.Snapshot, batch).Count} requests");
            }

            await output.WriteLineAsync();
            await output.WriteLineAsync("first request:");
            await output.WriteLineAsync(example);
            return 0;
        }

        await output.WriteLineAsync();
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var summaries = new List<PassSummary>();
        int skippedConfigs = 0;
        var progress = new ProgressPrinter(error);
        var runner = new SweepRunner(sender, new SweepOptions(settings.Batches, settings.Concurrencies, settings.Retries, settings.TimeoutMs));
        Console.CancelKeyPress += OnCancel;
        try
        {
            await runner.RunAsync(
                selected,
                dataset.Snapshot,
                interrupt.Token,
                onPass: pass =>
                {
                    PassSummary summary = PassSummary.From(pass);
                    summaries.Add(summary);
                    progress.Reset();
                    output.WriteLine(ConsoleReport.PassLine(summary));
                },
                onNotice: notice =>
                {
                    skippedConfigs++;
                    error.WriteLine(notice);
                },
                onProgress: progress.Report).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        var notes = new List<string>();
        foreach (string served in summaries.SelectMany(s => s.ServedModels.Select(m => m.Key)).Distinct().Where(m => !ModelNames.Matches(settings.Model, m)))
        {
            notes.Add($"The server reported model '{served}' although '{settings.Model}' was requested.");
        }

        if (summaries.Any(s => s.MinOptionMass < 0.5))
        {
            notes.Add("Some prompts put less than half of the next-token probability on the option letters. Check the prompt format and that the server passes the prompt through verbatim.");
        }

        foreach (string note in notes)
        {
            await error.WriteLineAsync($"note: {note}").ConfigureAwait(false);
        }

        if (summaries.Count > 0)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync(ConsoleReport.Table(summaries));
        }

        var info = new RunInfo(
            ToolVersion(),
            DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            settings.Provider == Provider.Decisions ? "decisions" : "logprobs",
            endpoint,
            settings.Model,
            sender.Describe(),
            example,
            server,
            notes,
            runner.StopReason);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.Output)!);
        await using (FileStream stream = File.Create(settings.Output))
        {
            ResultsWriter.Write(stream, info, dataset, selected, summaries);
        }

        await output.WriteLineAsync($"details: {Path.GetRelativePath(Environment.CurrentDirectory, settings.Output)}");
        if (interrupt.IsCancellationRequested)
        {
            return 130;
        }

        return summaries.Count == 0 || skippedConfigs > 0 || runner.StopReason is not null || summaries.Any(s => s.Failed > 0) ? 1 : 0;

        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            if (interrupt.IsCancellationRequested)
            {
                return; // A second Ctrl+C ends the process.
            }

            args.Cancel = true;
            error.WriteLine("Stopping requests and saving the results so far...");
            interrupt.Cancel();
        }
    }

    private HttpClient CreateHttpClient(BenchmarkSettings settings)
    {
        HttpMessageHandler handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(2, settings.Concurrencies.Max() + 1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<string?> CheckModelAsync(HttpClient http, JevStyleClientOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            IReadOnlyList<string> models = await ServerModels.ListAsync(http, options, timeout.Token).ConfigureAwait(false);
            return models.Contains(options.Model, StringComparer.Ordinal)
                ? null
                : $"model '{options.Model}' is not listed by {options.ModelsUri}. Available: {(models.Count == 0 ? "none" : string.Join(", ", models))}. LM Studio would answer with whichever model is loaded. Use --model with a listed id, or --skip-model-check.";
        }
        catch (Exception exception) when (exception is HttpRequestException or DecisionServerException or JsonException or OperationCanceledException or NotSupportedException)
        {
            return $"cannot list models at {options.ModelsUri}: {exception.Message}. Check --url and that the server is running, or use --skip-model-check.";
        }
    }

    private static async Task<string?> CheckDecisionModelAsync(DecisionsApiClient api, DecisionsApiOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            IReadOnlyList<string> models = await api.ListModelsAsync(timeout.Token).ConfigureAwait(false);
            return models.Contains(options.Model, StringComparer.Ordinal)
                ? null
                : $"model '{options.Model}' is not in the decision model catalog at {options.ModelsUri}. Available: {(models.Count == 0 ? "none" : string.Join(", ", models))}. Use --model with a listed id, or --skip-model-check.";
        }
        catch (Exception exception) when (exception is HttpRequestException or DecisionServerException or JsonException or OperationCanceledException or NotSupportedException)
        {
            return $"cannot list decision models at {options.ModelsUri}: {exception.Message}. Check --url and the API key, or use --skip-model-check.";
        }
    }

    private static string ToolVersion()
    {
        string? version = typeof(BenchmarkApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version?.Split('+')[0] ?? "0.0.0";
    }

    /// <summary>Prints "[done/total]" at most once a second while a pass runs, so a long pass shows signs of life.</summary>
    private sealed class ProgressPrinter(TextWriter writer)
    {
        private readonly Lock _lock = new();
        private long _last = Stopwatch.GetTimestamp();

        public void Report(int completed, int total)
        {
            lock (_lock)
            {
                if (completed >= total || !(Stopwatch.GetElapsedTime(_last).TotalSeconds >= 1)) return;
                writer.WriteLine($"[{completed}/{total}]");
                _last = Stopwatch.GetTimestamp();
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _last = Stopwatch.GetTimestamp();
            }
        }
    }
}

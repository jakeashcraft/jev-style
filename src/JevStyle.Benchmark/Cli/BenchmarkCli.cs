using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JevStyle.Benchmark.Dataset;
using JevStyle.Benchmark.Reporting;
using JevStyle.Benchmark.Running;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Cli;

/// <summary>Command-line surface. Option names and defaults follow bench.mts.</summary>
internal static partial class BenchmarkCli
{
    public const string DataVariable = "JEVSTYLE_DATA";
    public const string UrlVariable = "JEVSTYLE_URL";
    public const string ModelVariable = "JEVSTYLE_MODEL";
    public const string ApiKeyVariable = "JEVSTYLE_API_KEY";
    public const string OpenRouterKeyVariable = "OPENROUTER_API_KEY";
    public const string LocalDefaultUrl = "http://localhost:1234";

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<HttpMessageHandler>? handlerFactory = null)
    {
        RootCommand root = Create((settings, cancellationToken) => new BenchmarkApp(output, error, handlerFactory).RunAsync(settings, cancellationToken), output, error);
        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error, ProcessTerminationTimeout = null });
    }

    internal static RootCommand Create(Func<BenchmarkSettings, CancellationToken, Task<int>> run, TextWriter standardOutput, TextWriter standardError)
    {
        Option<string?> data = DataOption();
        Option<string> provider = new("--provider") { Description = "logprobs: a local OpenAI-compatible server (LM Studio). decisions: a hosted decisions API (OpenRouter: Jev, Microsoft-Decision-1)", DefaultValueFactory = _ => "logprobs" };
        provider.AcceptOnlyFromAmong("logprobs", "decisions");
        Option<string?> url = new("--url") { Description = $"Server root. Default {LocalDefaultUrl} (env {UrlVariable}) for logprobs, {DecisionsApiOptions.OpenRouterBaseAddress} for decisions", HelpName = "url" };
        Option<string?> model = new("--model") { Description = $"Model id. Default {JevStyleClientOptions.DefaultModel} (env {ModelVariable}) for logprobs, {DecisionsApiOptions.DefaultModel} for decisions", HelpName = "id" };
        Option<string?> apiKey = new("--api-key") { Description = $"Bearer token. Defaults to env {ApiKeyVariable} for logprobs and {OpenRouterKeyVariable} for decisions (a Windows user variable also works)", HelpName = "key" };
        Option<string> batch = new("--batch") { Description = "Expressions per request. A comma list sweeps; repeat a value to repeat a config", HelpName = "n[,n...]", DefaultValueFactory = _ => "1" };
        Option<string> concurrency = new("--concurrency") { Description = "Requests in flight. A comma list sweeps. Capped at the number of requests", HelpName = "n[,n...]", DefaultValueFactory = _ => "16" };
        Option<int?> limit = new("--limit") { Description = "Only the first n rows, for cheap smoke runs", HelpName = "n" };
        Option<int> retries = new("--retries") { Description = "Extra attempts per request for retryable errors (connection failures, timeouts, 408, 429, 5xx). 0 shows the raw failure rate", HelpName = "n", DefaultValueFactory = _ => 2 };
        Option<int> timeoutMs = new("--timeout-ms") { Description = "Deadline per attempt", HelpName = "n", DefaultValueFactory = _ => 60_000 };
        Option<string> order = new("--option-order") { Description = "Local model only: which option gets letter A", DefaultValueFactory = _ => "true-first" };
        order.AcceptOnlyFromAmong("true-first", "false-first");
        Option<int> topLogprobs = new("--top-logprobs") { Description = "Local model only: candidates per position (LM Studio allows at most 20)", HelpName = "n", DefaultValueFactory = _ => JevStyleClientOptions.DefaultTopLogprobs };
        Option<bool> skipModelCheck = new("--skip-model-check") { Description = "Do not check the model id against the server's model list before the run" };
        Option<bool> dryRun = new("--dry-run") { Description = "Load the data, build the requests and print the first one; make no model calls" };
        Option<string?> output = new("--output") { Description = "Results file (default results/<timestamp>-<model>.json)", HelpName = "path" };

        var root = new RootCommand(
            "Runs jev-expression-benchmark's expressions through a local Jev-style model (LM Studio) or a hosted decision model (OpenRouter), times it and checks every answer against the local evaluator.")
        {
            data, provider, url, model, apiKey, batch, concurrency, limit, retries, timeoutMs, order, topLogprobs, skipModelCheck, dryRun, output,
        };

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var errors = new List<string>();
            bool decisions = parseResult.GetValue(provider) == "decisions";
            IReadOnlyList<int> batches = Integers(errors, "batch", parseResult.GetValue(batch) ?? "1");
            IReadOnlyList<int> concurrencies = Integers(errors, "concurrency", parseResult.GetValue(concurrency) ?? "16");
            int? limitValue = parseResult.GetValue(limit);
            if (limitValue is < 1)
            {
                errors.Add("--limit must be a positive integer");
            }

            int retriesValue = parseResult.GetValue(retries);
            if (retriesValue is < 0 or > 100)
            {
                errors.Add("--retries must be an integer between 0 and 100");
            }

            int timeoutValue = parseResult.GetValue(timeoutMs);
            if (timeoutValue < 1)
            {
                errors.Add("--timeout-ms must be a positive integer");
            }

            int topValue = parseResult.GetValue(topLogprobs);
            if (topValue is < 2 or > DecisionPrompt.MaxOptions)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"--top-logprobs must be an integer between 2 and {DecisionPrompt.MaxOptions}"));
            }

            switch (decisions)
            {
                case false when batches.Any(b => b > 1):
                    errors.Add("The local model reads one expression per prompt, so --batch must be 1 with --provider logprobs.");
                    break;
                case true when parseResult.GetResult(order) is { Implicit: false }:
                    errors.Add("--option-order only applies to the local model (--provider logprobs).");
                    break;
            }

            if (decisions && parseResult.GetResult(topLogprobs) is { Implicit: false })
            {
                errors.Add("--top-logprobs only applies to the local model (--provider logprobs).");
            }

            string modelValue = (parseResult.GetValue(model)
                ?? (decisions ? DecisionsApiOptions.DefaultModel : Environment.GetEnvironmentVariable(ModelVariable) ?? JevStyleClientOptions.DefaultModel)).Trim();
            if (modelValue.Length == 0)
            {
                errors.Add("--model cannot be empty");
            }

            bool dry = parseResult.GetValue(dryRun);
            string? keyValue = parseResult.GetValue(apiKey) ?? ReadVariable(decisions ? OpenRouterKeyVariable : ApiKeyVariable);
            if (decisions && string.IsNullOrWhiteSpace(keyValue) && !dry)
            {
                errors.Add($"{OpenRouterKeyVariable} is missing. Set it as an environment variable (a Windows user variable works) or pass --api-key. --dry-run works without a key.");
            }

            string urlText = parseResult.GetValue(url)
                ?? (decisions ? DecisionsApiOptions.OpenRouterBaseAddress.ToString() : Environment.GetEnvironmentVariable(UrlVariable) ?? LocalDefaultUrl);
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? urlValue) || (urlValue.Scheme != Uri.UriSchemeHttp && urlValue.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("--url must be an absolute http or https URL, for example http://localhost:1234");
            }

            if (errors.Count > 0)
            {
                foreach (string message in errors)
                {
                    await standardError.WriteLineAsync(message).ConfigureAwait(false);
                }

                return 1;
            }

            var settings = new BenchmarkSettings
            {
                Data = DataPath(parseResult.GetValue(data)),
                Provider = decisions ? Provider.Decisions : Provider.Logprobs,
                Url = urlValue!,
                Model = modelValue,
                ApiKey = keyValue,
                Batches = batches,
                Concurrencies = concurrencies,
                Limit = limitValue,
                Retries = retriesValue,
                TimeoutMs = timeoutValue,
                Order = parseResult.GetValue(order) == "false-first" ? OptionOrder.FalseFirst : OptionOrder.TrueFirst,
                TopLogprobs = topValue,
                SkipModelCheck = parseResult.GetValue(skipModelCheck),
                DryRun = dry,
                Output = Path.GetFullPath(parseResult.GetValue(output) ?? DefaultOutput(modelValue)),
            };
            return await run(settings, cancellationToken).ConfigureAwait(false);
        });

        root.Subcommands.Add(CreateCompare(standardOutput, standardError));
        return root;
    }

    private static Option<string?> DataOption() =>
        new("--data") { Description = $"Folder with expressions.csv and tags.json, for example jev-expression-benchmark's data folder (default env {DataVariable}, else ./data)", HelpName = "path" };

    private static Command CreateCompare(TextWriter output, TextWriter error)
    {
        Argument<string[]> files = new("results") { Description = "Results files from bench.mts or jev-style-bench. Append #n to use only run n of a file", Arity = ArgumentArity.OneOrMore };
        Option<string?> data = DataOption();
        Option<string?> file = new("--output") { Description = "Also write the comparison to this Markdown file", HelpName = "path" };
        var compare = new Command("compare", "Compare runs on the rows they all answered. Expected answers come from the dataset.") { files, data, file };
        compare.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var dataset = ExpressionDataset.Load(DataPath(parseResult.GetValue(data)), writeGeneratedSnapshot: false);
                RecordedRun[] runs = [.. (parseResult.GetValue(files) ?? []).SelectMany(ResultsReader.Read)];
                if (runs.Length == 0)
                {
                    await error.WriteLineAsync("Compare failed: the files contain no runs.").ConfigureAwait(false);
                    return 1;
                }

                string markdown = RunComparison.Render(dataset, runs);
                await output.WriteAsync(markdown).ConfigureAwait(false);
                if (parseResult.GetValue(file) is { } target)
                {
                    await File.WriteAllTextAsync(target, markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }
            catch (Exception exception) when (exception is DatasetException or ResultsFileException or IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Compare failed: {exception.Message}").ConfigureAwait(false);
                return 1;
            }
        });
        return compare;
    }

    /// <summary>Process environment first; on Windows also the user and machine variables, so a key set after this shell started still works.</summary>
    internal static string? ReadVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string DataPath(string? value) => value ?? ReadVariable(DataVariable) ?? "data";

    /// <summary>bench.mts's comma lists: positive integers.</summary>
    private static List<int> Integers(List<string> errors, string name, string value)
    {
        var list = new List<int>();
        foreach (string part in value.Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number > 0)
            {
                list.Add(number);
            }
            else
            {
                errors.Add($"--{name} expects positive integers, got \"{value}\"");
                return [];
            }
        }

        return list;
    }

    private static string DefaultOutput(string model) =>
        Path.Combine("results", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture) + "-" + Slug().Replace(model, "-") + ".json");

    [GeneratedRegex("[^A-Za-z0-9.]+")]
    private static partial Regex Slug();
}

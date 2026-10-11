using JevStyle.Benchmark.Running;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Cli;

/// <summary>Which transport answers the expressions.</summary>
internal enum Provider
{
    /// <summary>Next-token log-probabilities from an OpenAI-compatible server (LM Studio, llama-server).</summary>
    Logprobs,

    /// <summary>A hosted decisions API (OpenRouter): Jev, Microsoft-Decision-1 and others.</summary>
    Decisions,
}

/// <summary>Validated run settings. Defaults match bench.mts where the option exists there.</summary>
internal sealed record BenchmarkSettings
{
    public string Data { get; init; } = "data";

    public Provider Provider { get; init; } = Provider.Logprobs;

    public Uri Url { get; init; } = JevStyleClientOptions.DefaultBaseAddress;

    public string Model { get; init; } = JevStyleClientOptions.DefaultModel;

    public string? ApiKey { get; init; }

    public IReadOnlyList<int> Batches { get; init; } = [1];

    public IReadOnlyList<int> Concurrencies { get; init; } = [16];

    public int? Limit { get; init; }

    public int Retries { get; init; } = 2;

    public int TimeoutMs { get; init; } = 60_000;

    public OptionOrder Order { get; init; } = OptionOrder.TrueFirst;

    public int TopLogprobs { get; init; } = JevStyleClientOptions.DefaultTopLogprobs;

    public bool SkipModelCheck { get; init; }

    public bool DryRun { get; init; }

    /// <summary>The results file to write.</summary>
    public string Output { get; init; } = string.Empty;

    public DecisionsApiOptions ToDecisionsApiOptions() => new()
    {
        BaseAddress = Url,
        Model = Model,
        ApiKey = ApiKey,
    };

    public JevStyleClientOptions ToClientOptions() => new()
    {
        BaseAddress = Url,
        Model = Model,
        ApiKey = ApiKey,
        TopLogprobs = TopLogprobs,
    };
}

internal static class ModelNames
{
    /// <summary>
    /// True when the served model is the requested one. OpenRouter answers with a dated snapshot
    /// (<c>typesafe/jev-1.13-20260917</c> for <c>typesafe/jev-1.13</c>), which counts as the same model.
    /// </summary>
    public static bool Matches(string requested, string? served) =>
        served is not null && (string.Equals(requested, served, StringComparison.Ordinal) || served.StartsWith(requested + "-", StringComparison.Ordinal));
}

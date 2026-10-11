namespace JevStyle.Decisions;

/// <summary>Settings for talking to an OpenAI-compatible server such as LM Studio or llama-server.</summary>
public sealed class JevStyleClientOptions
{
    /// <summary>The model id LM Studio uses for the published v1 GGUF.</summary>
    public const string DefaultModel = "jev-style-qwen3.5-2b-decision";

    /// <summary>LM Studio's default server address.</summary>
    public static readonly Uri DefaultBaseAddress = new("http://localhost:1234/");

    /// <summary>LM Studio rejects <c>top_logprobs</c> above 20.</summary>
    public const int DefaultTopLogprobs = 20;

    /// <summary>Server root, for example <c>http://localhost:1234</c>. A trailing <c>/v1</c> is accepted.</summary>
    public Uri BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>Model id as the server lists it under <c>/v1/models</c>.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Optional bearer token (LM Studio "Require authentication", llama-server <c>--api-key</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Candidates requested per position. Also the maximum number of options. 1 to 26.</summary>
    public int TopLogprobs { get; set; } = DefaultTopLogprobs;

    /// <summary>The chat completions endpoint derived from <see cref="BaseAddress"/>.</summary>
    public Uri ChatCompletionsUri => OpenAiEndpoints.Resolve(BaseAddress, "v1/chat/completions");

    /// <summary>The model list endpoint derived from <see cref="BaseAddress"/>.</summary>
    public Uri ModelsUri => OpenAiEndpoints.Resolve(BaseAddress, "v1/models");

    /// <summary>Checks the settings.</summary>
    /// <param name="error">Why the settings are invalid.</param>
    /// <returns><see langword="true"/> when the settings are usable.</returns>
    public bool TryValidate(out string? error)
    {
        error = BaseAddress is null || !BaseAddress.IsAbsoluteUri || (BaseAddress.Scheme != Uri.UriSchemeHttp && BaseAddress.Scheme != Uri.UriSchemeHttps)
            ? "BaseAddress must be an absolute http or https URL."
            : string.IsNullOrWhiteSpace(Model)
                ? "Model cannot be empty."
                : TopLogprobs is < 1 or > DecisionPrompt.MaxOptions
                    ? $"TopLogprobs must be between 1 and {DecisionPrompt.MaxOptions}."
                    : null;
        return error is null;
    }

    internal void Validate()
    {
        if (!TryValidate(out string? error))
        {
            throw new ArgumentException(error);
        }
    }
}

/// <summary>Resolves OpenAI-compatible endpoint paths against a server root.</summary>
public static class OpenAiEndpoints
{
    /// <summary>Combines a server root with an API path such as <c>v1/chat/completions</c>.</summary>
    /// <param name="baseAddress">Server root. A trailing <c>/v1</c> is removed so it is not doubled.</param>
    /// <param name="path">Relative API path starting with <c>v1/</c>.</param>
    public static Uri Resolve(Uri baseAddress, string path)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(path);
        string root = baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^3];
        }

        return new Uri(root + "/" + path.TrimStart('/'), UriKind.Absolute);
    }
}

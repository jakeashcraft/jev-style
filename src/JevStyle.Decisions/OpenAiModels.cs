using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevStyle.Decisions;

internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("logprobs")] bool Logprobs,
    [property: JsonPropertyName("top_logprobs")] int TopLogprobs,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public ChatUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; set; }
}

internal sealed class ChatChoice
{
    [JsonPropertyName("logprobs")]
    public ChatLogprobs? Logprobs { get; set; }
}

internal sealed class ChatLogprobs
{
    [JsonPropertyName("content")]
    public List<ChatTokenLogprob>? Content { get; set; }
}

internal sealed class ChatTokenLogprob
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    [JsonPropertyName("top_logprobs")]
    public List<ChatTopLogprob>? TopLogprobs { get; set; }
}

internal sealed class ChatTopLogprob
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }
}

internal sealed class ChatUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }
}

internal sealed class ModelListResponse
{
    [JsonPropertyName("data")]
    public List<ModelEntry>? Data { get; set; }
}

internal sealed class ModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ModelListResponse))]
internal sealed partial class DecisionJsonContext : JsonSerializerContext;

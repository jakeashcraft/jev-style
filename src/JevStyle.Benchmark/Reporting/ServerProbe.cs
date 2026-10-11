using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using JevStyle.Decisions;

namespace JevStyle.Benchmark.Reporting;

/// <summary>Model details LM Studio reports for the model under test.</summary>
internal sealed record ServerModelInfo(string? Type, string? Publisher, string? Arch, string? Format, string? Quantization, string? State, int? MaxContextLength, int? LoadedContextLength);

/// <summary>
/// Optional LM Studio details through its native REST API (<c>/api/v0/models/{id}</c>). They describe the
/// server but are never needed for the benchmark, so every failure is reported as missing data.
/// </summary>
internal static class ServerProbe
{
    public static async Task<ServerModelInfo?> GetModelInfoAsync(HttpClient http, JevStyleClientOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string path = "api/v0/models/" + string.Join('/', options.Model.Split('/').Select(Uri.EscapeDataString));
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiEndpoints.Resolve(options.BaseAddress, path));
            if (!string.IsNullOrEmpty(options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            JsonNode? node = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token).ConfigureAwait(false);
            return node is JsonObject model
                ? new ServerModelInfo(
                    Text(model, "type"), Text(model, "publisher"), Text(model, "arch"), Text(model, "compatibility_type"),
                    Text(model, "quantization"), Text(model, "state"), Int(model, "max_context_length"), Int(model, "loaded_context_length"))
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int? Int(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue(out int number) ? number : null;
}

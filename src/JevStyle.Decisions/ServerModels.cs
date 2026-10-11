using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace JevStyle.Decisions;

/// <summary>Reads the model ids an OpenAI-compatible server offers.</summary>
public static class ServerModels
{
    /// <summary>
    /// Lists model ids from <c>/v1/models</c>. Check the result before a long run: LM Studio answers a
    /// request for an unknown model id with whatever model is loaded.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="options">Server settings.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient httpClient, JevStyleClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Get, options.ModelsUri);
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DecisionServerException(response.StatusCode, $"HTTP {(int)response.StatusCode} from {options.ModelsUri}");
        }

        ModelListResponse? models = await response.Content
            .ReadFromJsonAsync(DecisionJsonContext.Default.ModelListResponse, cancellationToken)
            .ConfigureAwait(false);
        return models?.Data?.Select(m => m.Id).OfType<string>().ToArray() ?? [];
    }
}

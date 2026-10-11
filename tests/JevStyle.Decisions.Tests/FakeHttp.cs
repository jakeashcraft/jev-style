using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace JevStyle.Decisions.Tests;

/// <summary>Records requests and answers them from a delegate.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A chat completion in LM Studio's shape with the given candidates.</summary>
    public static string ChatCompletion(params (string Token, double Logprob)[] candidates)
    {
        var top = new JsonArray([.. candidates.Select(c => (JsonNode)new JsonObject { ["token"] = c.Token, ["logprob"] = c.Logprob, ["bytes"] = new JsonArray() })]);
        var response = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion",
            ["model"] = "jev-style-qwen3.5-2b-decision",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = candidates[0].Token },
                ["logprobs"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["token"] = candidates[0].Token, ["logprob"] = candidates[0].Logprob, ["top_logprobs"] = top }),
                },
                ["finish_reason"] = "length",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 80, ["completion_tokens"] = 1, ["total_tokens"] = 81 },
            ["stats"] = new JsonObject(),
        };
        return response.ToJsonString();
    }
}

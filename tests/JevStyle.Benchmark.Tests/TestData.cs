using System.Net;
using System.Text;
using System.Text.Json;
using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Tests;

/// <summary>Fixture access, a fake HTTP handler and a throwaway dataset folder.</summary>
internal static class TestData
{
    private static readonly Lazy<JsonDocument> Reference = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference.json"))));

    public static JsonElement ReferenceRoot => Reference.Value.RootElement;

    public static JsonElement Scenario(string name) =>
        ReferenceRoot.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    public static TagValue Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => TagValue.True,
        JsonValueKind.False => TagValue.False,
        JsonValueKind.Number => TagValue.Of(value.GetDouble()),
        _ => throw new InvalidDataException($"Unexpected tag value {value}"),
    };

    public static List<KeyValuePair<string, TagValue>> Entries(JsonElement json) =>
        [.. json.EnumerateObject().Select(p => new KeyValuePair<string, TagValue>(p.Name, Value(p.Value)))];

    /// <summary>A dataset folder with the given CSV lines and tag values (tags.json omitted when null).</summary>
    public static string DatasetFolder(string csv, string? tagsJson)
    {
        string folder = Path.Combine(Path.GetTempPath(), "jev-style-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "expressions.csv"), csv);
        if (tagsJson is not null)
        {
            File.WriteAllText(Path.Combine(folder, "tags.json"), tagsJson);
        }

        return folder;
    }

    public const string SmallCsv =
        "Name,ExpressionScript\n" +
        "Caustic Circulation,PUMP_1 == true && VALVE_1 == false && TT_RECIRC_TEMP > 150\n" +
        "Caustic Circulation Stop,PUMP_1 == false || TT_RECIRC_TEMP < 110\n" +
        "Drain,STEP_NO == 15\n" +
        "Rinse,STEP_NO != 13 || STEP_NO != 14\n";

    public const string SmallTags = """{"PUMP_1":true,"VALVE_1":false,"TT_RECIRC_TEMP":151,"STEP_NO":14}""";
}

/// <summary>Records requests and answers them from a delegate.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly Lock _lock = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_lock)
        {
            Requests.Add((request, body));
        }

        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

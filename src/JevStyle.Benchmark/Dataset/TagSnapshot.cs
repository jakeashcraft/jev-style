using System.Collections;
using System.Text;
using System.Text.Json;

namespace JevStyle.Benchmark.Dataset;

/// <summary>
/// Tag values in insertion order, like the JavaScript objects bench.mts uses: data/tags.json, or the state sent
/// with one request. Replacing a value keeps the tag's position, as assigning to an existing key does in JavaScript.
/// </summary>
internal sealed class TagSnapshot : IReadOnlyList<KeyValuePair<string, TagValue>>
{
    private readonly List<KeyValuePair<string, TagValue>> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public TagSnapshot()
    {
    }

    public TagSnapshot(IEnumerable<KeyValuePair<string, TagValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (KeyValuePair<string, TagValue> entry in entries)
        {
            Set(entry.Key, entry.Value);
        }
    }

    public int Count => _entries.Count;

    public IEnumerable<string> Tags => _entries.Select(e => e.Key);

    public KeyValuePair<string, TagValue> this[int index] => _entries[index];

    public TagValue this[string tag] =>
        _index.TryGetValue(tag, out int index) ? _entries[index].Value : throw new KeyNotFoundException($"The snapshot has no value for tag '{tag}'.");

    public bool Contains(string tag) => _index.ContainsKey(tag);

    /// <summary>Adds a tag at the end, or replaces its value where it is.</summary>
    public void Set(string tag, TagValue value)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (_index.TryGetValue(tag, out int index))
        {
            _entries[index] = new KeyValuePair<string, TagValue>(tag, value);
        }
        else
        {
            _index[tag] = _entries.Count;
            _entries.Add(new KeyValuePair<string, TagValue>(tag, value));
        }
    }

    /// <summary>
    /// The listed tags with their values, in order of first mention: what bench.mts sends as a request's state
    /// (<c>Object.fromEntries(chunk.flatMap(row =&gt; row.tags).map(tag =&gt; [tag, snapshot[tag]]))</c>).
    /// </summary>
    public TagSnapshot Subset(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var subset = new TagSnapshot();
        foreach (string tag in tags)
        {
            if (!subset.Contains(tag))
            {
                subset.Set(tag, this[tag]);
            }
        }

        return subset;
    }

    /// <summary>JSON.stringify(snapshot).</summary>
    public string ToJson()
    {
        var builder = new StringBuilder(Count * 32).Append('{');
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(JsJson.Quote(_entries[i].Key)).Append(':').Append(_entries[i].Value.ToJson());
        }

        return builder.Append('}').ToString();
    }

    /// <summary>JSON.stringify(snapshot, null, 2): how bench.mts writes data/tags.json.</summary>
    public string ToIndentedJson()
    {
        if (_entries.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder(Count * 40).Append('{');
        for (int i = 0; i < _entries.Count; i++)
        {
            builder.Append(i == 0 ? "\n  " : ",\n  ").Append(JsJson.Quote(_entries[i].Key)).Append(": ").Append(_entries[i].Value.ToJson());
        }

        return builder.Append("\n}").ToString();
    }

    /// <summary>Reads data/tags.json: a JSON object whose values are Booleans or finite numbers.</summary>
    /// <exception cref="DatasetException">The JSON is invalid or a value has another type.</exception>
    public static TagSnapshot Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DatasetException("data/tags.json must be a JSON object that maps tag names to values");
            }

            var snapshot = new TagSnapshot();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                TagValue value = property.Value.ValueKind switch
                {
                    JsonValueKind.True => TagValue.True,
                    JsonValueKind.False => TagValue.False,
                    JsonValueKind.Number when property.Value.TryGetDouble(out double number) && double.IsFinite(number) => TagValue.Of(number),
                    _ => throw new DatasetException("data/tags.json values must be Booleans or numbers"),
                };
                snapshot.Set(property.Name, value);
            }

            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new DatasetException($"data/tags.json is not valid JSON: {exception.Message}");
        }
    }

    public IEnumerator<KeyValuePair<string, TagValue>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

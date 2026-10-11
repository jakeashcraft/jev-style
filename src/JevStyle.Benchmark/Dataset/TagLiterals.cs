using System.Text.RegularExpressions;

namespace JevStyle.Benchmark.Dataset;

/// <summary>
/// The literals each tag is compared against, collected the way bench.mts does it: tags in order of first use,
/// literals lowercased in order of first appearance. They decide each tag's candidate values.
/// </summary>
internal sealed partial class TagLiterals
{
    private const string _space = JsText.WhiteSpaceClass;
    private readonly Dictionary<string, List<string>> _literals = new(StringComparer.Ordinal);
    private readonly List<string> _tags = [];

    /// <summary>Every tag the rows use, in order of first use.</summary>
    public IReadOnlyList<string> Tags => _tags;

    public IReadOnlyList<string> this[string tag] => _literals[tag];

    public static TagLiterals Collect(IEnumerable<ExpressionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new TagLiterals();
        foreach (ExpressionRow row in rows)
        {
            foreach (string tag in row.Tags)
            {
                if (result._literals.ContainsKey(tag)) continue;
                result._literals[tag] = [];
                result._tags.Add(tag);
            }

            foreach (Match match in Comparison().Matches(row.Text))
            {
                if (!result._literals.TryGetValue(match.Groups[1].Value, out List<string>? literals)) continue;
                string literal = match.Groups[2].Value.ToLowerInvariant();
                if (!literals.Contains(literal))
                {
                    literals.Add(literal);
                }
            }
        }

        return result;
    }

    // COMPARISON from bench.mts (flags gi): `tag op literal`, literal true, false or a number.
    [GeneratedRegex("([A-Za-z_][A-Za-z0-9_]*)[" + _space + "]*(?:[=!]=|[<>]=?)[" + _space + "]*(true|false|-?[0-9]+(?:\\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Comparison();
}

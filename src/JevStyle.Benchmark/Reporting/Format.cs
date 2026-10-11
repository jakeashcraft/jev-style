using System.Globalization;
using System.Text.RegularExpressions;

namespace JevStyle.Benchmark.Reporting;

/// <summary>Number and Markdown formatting that matches the Node report (en-US grouping, trimmed decimals).</summary>
internal static partial class Format
{
    private static readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    public static string Number(double? value, int digits = 2) =>
        value is null ? "n/a" : value.Value.ToString(digits == 0 ? "#,##0" : "#,##0." + new string('#', digits), _culture);

    public static string Number(long? value) => value is null ? "n/a" : value.Value.ToString("#,##0", _culture);

    public static string Percent(double? value) => value is null ? "n/a" : Number(value * 100) + "%";

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Md(string? value) =>
        NewLines().Replace(value ?? string.Empty, " ").Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex NewLines();
}

using System.Globalization;
using System.Text;

namespace JevStyle.Benchmark.Dataset;

/// <summary>
/// The JavaScript behavior bench.mts relies on, so both tools read the same rows and send the same JSON:
/// whitespace rules (<c>\s</c> and <c>trim</c>), Number-to-string conversion and JSON.stringify quoting.
/// </summary>
internal static class JsText
{
    /// <summary>The characters JavaScript's <c>\s</c> matches and <c>String.prototype.trim</c> removes.</summary>
    public const string WhiteSpaceClass = "\t\n\x000B\f\r \x00A0\x1680\x2000-\x200A\x2028\x2029\x202F\x205F\x3000\xFEFF";

    public static bool IsWhiteSpace(char c) => c switch
    {
        '\t' or '\n' or '\x000B' or '\f' or '\r' or ' ' or '\x00A0' or '\x1680' or '\x2028' or '\x2029' or '\x202F' or '\x205F' or '\x3000' or '\xFEFF' => true,
        >= '\x2000' and <= '\x200A' => true,
        _ => false,
    };

    public static string Trim(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }
}

/// <summary>ECMAScript Number::toString and Number.isInteger/isSafeInteger.</summary>
internal static class JsNumber
{
    public const double MaxSafeInteger = 9007199254740991;

    public static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    public static bool IsSafeInteger(double value) => IsInteger(value) && Math.Abs(value) <= MaxSafeInteger;

    public static string Format(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == 0)
        {
            return "0";
        }

        // .NET's "R" format is the shortest round-trippable representation, the same digits JavaScript
        // chooses. Only the layout (exponent thresholds and notation) differs, so re-layout per ECMA-262.
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        int exponent = 0;
        int e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        int dot = text.IndexOf('.', StringComparison.Ordinal);
        string integerPart = dot >= 0 ? text[..dot] : text;
        string fractionPart = dot >= 0 ? text[(dot + 1)..] : string.Empty;
        string all = integerPart + fractionPart;
        string trimmedStart = all.TrimStart('0');
        int leadingZeros = all.Length - trimmedStart.Length;
        string digits = trimmedStart.TrimEnd('0');
        int k = digits.Length;
        int n = integerPart.Length - leadingZeros + exponent;

        string result;
        if (k <= n && n <= 21)
            result = digits + new string('0', n - k);
        else
        {
            switch (n)
            {
                case > 0 and <= 21:
                    result = digits[..n] + "." + digits[n..];
                    break;
                case > -6 and <= 0:
                    result = "0." + new string('0', -n) + digits;
                    break;
                default:
                {
                    int power = n - 1;
                    string mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
                    result = mantissa + "e" + (power >= 0 ? "+" : "-") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        return negative ? "-" + result : result;
    }
}

/// <summary>JSON.stringify for the value types the benchmark uses.</summary>
internal static class JsJson
{
    public static string Number(double value) => double.IsFinite(value) ? JsNumber.Format(value) : "null";

    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20 || (char.IsLowSurrogate(c)) || (char.IsHighSurrogate(c) && (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        builder.Append(c).Append(value[++i]);
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}

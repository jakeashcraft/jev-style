using System.Globalization;
using System.Text.RegularExpressions;

namespace JevStyle.Benchmark.Dataset;

/// <summary>Why a CSV row was skipped: its text is outside the grammar or does not compile.</summary>
internal sealed class ExpressionSyntaxException(string message) : Exception(message);

/// <summary>
/// One expression in the grammar of jev-expression-benchmark: tag names, <c>true</c>/<c>false</c> in any case,
/// numbers, <c>== != &gt; &gt;= &lt; &lt;=</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c> and parentheses. bench.mts
/// compiles each row with <c>new Function</c>, so this evaluator follows JavaScript: loose equality
/// (<c>true == 1</c>), relational operators bind tighter than equality, <c>&amp;&amp;</c> and <c>||</c> return an
/// operand, and a number written right after an operand (<c>A -1</c>) is a subtraction.
/// </summary>
/// <remarks>
/// Rows bench.mts accepts only by accident of JavaScript (numbers with leading zeros, exponents or hex digits,
/// such as <c>010</c>, <c>1e5</c> or <c>0x1F</c>) are rejected here instead of being read as octal or exponent
/// literals. None of them are in the published dataset.
/// </remarks>
internal sealed partial class Expression
{
    private const string _space = JsText.WhiteSpaceClass;

    // JavaScript words that cannot be parameter names, so bench.mts fails to compile a row that uses one as a tag.
    private static readonly HashSet<string> _reservedWords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum",
        "export", "extends", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return",
        "super", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with",
    };

    private readonly Node _root;

    private Expression(string text, Node root, string[] tags)
    {
        Text = text;
        _root = root;
        Tags = tags;
    }

    private enum Kind
    {
        Or,
        And,
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Not,
        Open,
        Close,
        Number,
        Name,
        Minus,
        End,
    }

    /// <summary>The expression as written in the CSV, trimmed.</summary>
    public string Text { get; }

    /// <summary>Tag names in order of first appearance. <see cref="Evaluate(ReadOnlySpan{TagValue})"/> takes values in this order.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>bench.mts's whitelist: every character belongs to a token, and nothing looks like a function call.</summary>
    public static bool IsSupported(string text) =>
        text.Length > 0 && Tokens().Replace(text, string.Empty).Length == 0 && !CallLike().IsMatch(text);

    /// <summary>Parses one row's expression.</summary>
    /// <exception cref="ExpressionSyntaxException">bench.mts would skip the row.</exception>
    public static Expression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsSupported(text))
        {
            throw new ExpressionSyntaxException("unsupported syntax");
        }

        var tags = new List<string>();
        Node root = new Parser(Tokenize(text), tags).ParseAll();
        return new Expression(text, root, [.. tags]);
    }

    /// <summary>Evaluates with one value per tag, in <see cref="Tags"/> order.</summary>
    public bool Evaluate(ReadOnlySpan<TagValue> values)
    {
        return values.Length != Tags.Count
            ? throw new ArgumentException($"Expected {Tags.Count} values, got {values.Length}.", nameof(values))
            : _root.Evaluate(values).Truthy;
    }

    /// <summary>Evaluates against a snapshot that holds every tag the expression uses.</summary>
    public bool Evaluate(TagSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Span<TagValue> values = Tags.Count <= 64 ? stackalloc TagValue[Tags.Count] : new TagValue[Tags.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = snapshot[Tags[i]];
        }

        return _root.Evaluate(values).Truthy;
    }

    public override string ToString() => Text;

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (Match match in Tokens().Matches(text))
        {
            string value = match.Value;
            int start = match.Index;
            int end = start + value.Length;
            if (JsText.IsWhiteSpace(value[0]))
            {
                continue;
            }

            Kind kind = value switch
            {
                "||" => Kind.Or,
                "&&" => Kind.And,
                "==" => Kind.Equal,
                "!=" => Kind.NotEqual,
                "<" => Kind.Less,
                "<=" => Kind.LessOrEqual,
                ">" => Kind.Greater,
                ">=" => Kind.GreaterOrEqual,
                "!" => Kind.Not,
                "(" => Kind.Open,
                ")" => Kind.Close,
                _ => value[0] == '-' || char.IsAsciiDigit(value[0]) ? Kind.Number : Kind.Name,
            };

            Token? previous = tokens.Count > 0 ? tokens[^1] : null;
            switch (kind)
            {
                case Kind.Name when previous is { Kind: Kind.Number } number && number.End == start:
                    // JavaScript reads 1e5 or 0x1F as one number and rejects 1abc; the grammar has neither.
                    throw new ExpressionSyntaxException($"unsupported number '{number.Text}{value}'");
                case Kind.Number:
                {
                    if (value[0] == '-' && previous is { Kind: Kind.Number or Kind.Name or Kind.Close })
                    {
                        // After an operand JavaScript reads "-1" as minus 1 (A -1 is A - 1).
                        tokens.Add(new Token(Kind.Minus, "-", start, start + 1));
                        value = value[1..];
                        start++;
                    }

                    string digits = value.TrimStart('-');
                    if (digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1]))
                    {
                        throw new ExpressionSyntaxException($"unsupported number '{value}'");
                    }

                    break;
                }
            }

            tokens.Add(new Token(kind, value, start, end));
        }

        tokens.Add(new Token(Kind.End, string.Empty, text.Length, text.Length));
        return tokens;
    }

    // TOKENS from bench.mts, with JavaScript's ASCII \d and \w and its full \s.
    [GeneratedRegex("[" + _space + @"]+|&&|\|\||[=!]=|[<>]=?|[()!]|-?[0-9]+(?:\.[0-9]+)?|[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    // /[\w)]\s*\(/ from bench.mts: rejects anything that looks like a call.
    [GeneratedRegex("[A-Za-z0-9_)][" + _space + "]*\\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallLike();

    private readonly record struct Token(Kind Kind, string Text, int Start, int End);

    /// <summary>Recursive descent with JavaScript precedence: || &lt; &amp;&amp; &lt; == != &lt; relational &lt; minus &lt; !.</summary>
    private sealed class Parser(List<Token> tokens, List<string> tags)
    {
        private int _position;

        private Token Current => tokens[_position];

        public Node ParseAll()
        {
            Node node = ParseOr();
            return Current.Kind == Kind.End ? node : throw Unexpected();
        }

        private Node ParseOr()
        {
            Node left = ParseAnd();
            while (Current.Kind == Kind.Or)
            {
                _position++;
                left = new OrNode(left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseEquality();
            while (Current.Kind == Kind.And)
            {
                _position++;
                left = new AndNode(left, ParseEquality());
            }

            return left;
        }

        private Node ParseEquality()
        {
            Node left = ParseRelational();
            while (Current.Kind is Kind.Equal or Kind.NotEqual)
            {
                Kind op = Current.Kind;
                _position++;
                left = new CompareNode(op, left, ParseRelational());
            }

            return left;
        }

        private Node ParseRelational()
        {
            Node left = ParseSubtraction();
            while (Current.Kind is Kind.Less or Kind.LessOrEqual or Kind.Greater or Kind.GreaterOrEqual)
            {
                Kind op = Current.Kind;
                _position++;
                left = new CompareNode(op, left, ParseSubtraction());
            }

            return left;
        }

        private Node ParseSubtraction()
        {
            Node left = ParseUnary();
            while (Current.Kind == Kind.Minus)
            {
                _position++;
                left = new SubtractNode(left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Kind != Kind.Not)
            {
                return ParsePrimary();
            }

            _position++;
            return new NotNode(ParseUnary());
        }

        private Node ParsePrimary()
        {
            Token token = Current;
            switch (token.Kind)
            {
                case Kind.Number:
                    _position++;
                    return new ConstantNode(TagValue.Of(double.Parse(token.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)));
                case Kind.Name when token.Text.Equals("true", StringComparison.OrdinalIgnoreCase):
                    _position++;
                    return new ConstantNode(TagValue.True);
                case Kind.Name when token.Text.Equals("false", StringComparison.OrdinalIgnoreCase):
                    _position++;
                    return new ConstantNode(TagValue.False);
                case Kind.Name when _reservedWords.Contains(token.Text):
                    throw new ExpressionSyntaxException($"Unexpected token '{token.Text}'");
                case Kind.Name:
                    _position++;
                    int slot = tags.IndexOf(token.Text);
                    if (slot >= 0) return new TagNode(slot);
                    slot = tags.Count;
                    tags.Add(token.Text);

                    return new TagNode(slot);
                case Kind.Open:
                    _position++;
                    Node inner = ParseOr();
                    if (Current.Kind != Kind.Close)
                    {
                        throw Unexpected();
                    }

                    _position++;
                    return inner;
                default:
                    throw Unexpected();
            }
        }

        private ExpressionSyntaxException Unexpected() =>
            new(Current.Kind == Kind.End ? "Unexpected end of input" : $"Unexpected token '{Current.Text}'");
    }

    private abstract class Node
    {
        public abstract TagValue Evaluate(ReadOnlySpan<TagValue> values);
    }

    private sealed class ConstantNode(TagValue value) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values) => value;
    }

    private sealed class TagNode(int slot) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values) => values[slot];
    }

    private sealed class NotNode(Node operand) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values) => TagValue.Of(!operand.Evaluate(values).Truthy);
    }

    private sealed class AndNode(Node left, Node right) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values)
        {
            TagValue value = left.Evaluate(values);
            return value.Truthy ? right.Evaluate(values) : value;
        }
    }

    private sealed class OrNode(Node left, Node right) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values)
        {
            TagValue value = left.Evaluate(values);
            return value.Truthy ? value : right.Evaluate(values);
        }
    }

    private sealed class SubtractNode(Node left, Node right) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values) =>
            TagValue.Of(left.Evaluate(values).Number - right.Evaluate(values).Number);
    }

    /// <summary>Booleans and numbers only, so loose equality and the relational operators both compare as numbers.</summary>
    private sealed class CompareNode(Kind op, Node left, Node right) : Node
    {
        public override TagValue Evaluate(ReadOnlySpan<TagValue> values)
        {
            double a = left.Evaluate(values).Number;
            double b = right.Evaluate(values).Number;
            return TagValue.Of(op switch
            {
                Kind.Equal => a == b,
                Kind.NotEqual => a != b,
                Kind.Less => a < b,
                Kind.LessOrEqual => a <= b,
                Kind.Greater => a > b,
                _ => a >= b,
            });
        }
    }
}

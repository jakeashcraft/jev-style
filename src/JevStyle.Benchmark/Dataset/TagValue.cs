namespace JevStyle.Benchmark.Dataset;

/// <summary>
/// A tag value: a Boolean or a number. Conversions follow JavaScript, which is what bench.mts uses for ground
/// truth: <c>true</c> is 1 and <c>false</c> is 0 wherever a number is needed, so <c>true == 1</c>.
/// </summary>
internal readonly record struct TagValue
{
    private TagValue(bool isBoolean, double number)
    {
        IsBoolean = isBoolean;
        Number = number;
    }

    public static TagValue True { get; } = new(true, 1);

    public static TagValue False { get; } = new(true, 0);

    public bool IsBoolean { get; }

    /// <summary>JavaScript ToNumber: the number itself, or 1 and 0 for <c>true</c> and <c>false</c>.</summary>
    public double Number { get; }

    /// <summary>JavaScript ToBoolean: everything except 0, NaN and <c>false</c> is truthy.</summary>
    public bool Truthy => Number != 0 && !double.IsNaN(Number);

    public static TagValue Of(bool value) => value ? True : False;

    public static TagValue Of(double value) => new(false, value);

    /// <summary>JSON.stringify of the value.</summary>
    public string ToJson() => IsBoolean ? (Number != 0 ? "true" : "false") : JsJson.Number(Number);

    public override string ToString() => ToJson();
}

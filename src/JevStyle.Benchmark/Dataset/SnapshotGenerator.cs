using System.Globalization;

namespace JevStyle.Benchmark.Dataset;

/// <summary>
/// Port of bench.mts's generateSnapshot, which writes data/tags.json when it is missing. Boolean tags get true or
/// false; numeric tags get each constant they are compared against and its two neighbours (negatives dropped).
/// Of 5,000 draws from mulberry32 seeded with 42, the one closest to an even true/false split wins.
/// </summary>
internal static class SnapshotGenerator
{
    public const uint Seed = 42;
    public const int Draws = 5_000;

    /// <summary>bench.mts's domainOf.</summary>
    public static IReadOnlyList<TagValue> DomainOf(IReadOnlyList<string> compared)
    {
        ArgumentNullException.ThrowIfNull(compared);
        double[] numbers = [.. compared
            .Where(literal => literal is not "true" and not "false")
            .Select(literal => double.Parse(literal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))];
        bool isBoolean = numbers.All(n => n is 0 or 1) && (numbers.Length < compared.Count || numbers.Length == 0);
        if (isBoolean)
        {
            return [TagValue.True, TagValue.False];
        }

        // new Set(...) keeps the first of equal values (SameValueZero: 0 and -0 are one value).
        var domain = new List<double>();
        foreach (double n in numbers)
        {
            double[] neighbours = [n - 1, n, n + 1];
            foreach (double candidate in neighbours)
            {
                if (!domain.Exists(existing => existing == candidate || (double.IsNaN(existing) && double.IsNaN(candidate))))
                {
                    domain.Add(candidate);
                }
            }
        }

        return [.. domain.Where(n => n >= 0).Select(TagValue.Of)];
    }

    /// <summary>bench.mts's generateSnapshot over every valid row.</summary>
    public static TagSnapshot Generate(IReadOnlyList<ExpressionRow> rows, TagLiterals literals)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(literals);
        IReadOnlyList<string> tags = literals.Tags;
        TagValue[][] domains = [.. tags.Select(tag => DomainOf(literals[tag]).ToArray())];
        for (int t = 0; t < tags.Count; t++)
        {
            if (domains[t].Length == 0)
            {
                throw new DatasetException($"Cannot generate data/tags.json: {tags[t]} is only compared with negative numbers, so it has no candidate value. Write its value into data/tags.json by hand.");
            }
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int t = 0; t < tags.Count; t++)
        {
            index[tags[t]] = t;
        }

        int[][] slots = [.. rows.Select(row => row.Tags.Select(tag => index[tag]).ToArray())];
        var buffer = new TagValue[slots.Length == 0 ? 0 : slots.Max(s => s.Length)];
        var candidate = new TagValue[tags.Count];
        TagValue[]? best = null;
        double bestGap = double.PositiveInfinity;
        var random = new SeededRandom(Seed);
        for (int draw = 0; draw < Draws; draw++)
        {
            for (int t = 0; t < tags.Count; t++)
            {
                TagValue[] domain = domains[t];
                candidate[t] = domain[(int)Math.Floor(random.NextDouble() * domain.Length)];
            }

            int trueCount = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                int[] rowSlots = slots[r];
                for (int k = 0; k < rowSlots.Length; k++)
                {
                    buffer[k] = candidate[rowSlots[k]];
                }

                if (rows[r].Expression.Evaluate(buffer.AsSpan(0, rowSlots.Length)))
                {
                    trueCount++;
                }
            }

            double gap = Math.Abs(((double)trueCount / rows.Count) - 0.5);
            if (!(gap < bestGap)) continue;
            best = (TagValue[])candidate.Clone();
            bestGap = gap;
        }

        var snapshot = new TagSnapshot();
        if (best is null) return snapshot;
        {
            for (int t = 0; t < tags.Count; t++)
            {
                snapshot.Set(tags[t], best[t]);
            }
        }

        return snapshot;
    }
}

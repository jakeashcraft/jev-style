using JevStyle.Benchmark.Dataset;

namespace JevStyle.Benchmark.Reporting;

/// <summary>Which answers a row can give at all, over every combination of its tags' candidate values.</summary>
internal static class ExpressionAnalysis
{
    /// <summary>
    /// Evaluates the row for every combination of each tag's candidate values (the same domains bench.mts draws
    /// tags.json from: each compared constant and its neighbours, or true/false). Returns null when there are
    /// more than <paramref name="limit"/> combinations.
    /// </summary>
    public static (bool CanBeTrue, bool CanBeFalse)? Outcomes(ExpressionRow row, TagLiterals literals, long limit = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(literals);
        TagValue[][] domains = [.. row.Tags.Select(tag => SnapshotGenerator.DomainOf(literals[tag]).ToArray())];
        long combinations = 1;
        foreach (TagValue[] domain in domains)
        {
            combinations *= domain.Length;
            if (combinations == 0 || combinations > limit)
            {
                return null;
            }
        }

        int[] position = new int[domains.Length];
        var values = new TagValue[domains.Length];
        bool canBeTrue = false;
        bool canBeFalse = false;
        while (true)
        {
            for (int i = 0; i < domains.Length; i++)
            {
                values[i] = domains[i][position[i]];
            }

            if (row.Expression.Evaluate(values))
            {
                canBeTrue = true;
            }
            else
            {
                canBeFalse = true;
            }

            if (canBeTrue && canBeFalse)
            {
                return (true, true);
            }

            int digit = domains.Length - 1;
            while (digit >= 0 && ++position[digit] == domains[digit].Length)
            {
                position[digit--] = 0;
            }

            if (digit < 0)
            {
                return (canBeTrue, canBeFalse);
            }
        }
    }
}

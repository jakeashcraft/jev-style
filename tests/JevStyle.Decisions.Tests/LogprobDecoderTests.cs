namespace JevStyle.Decisions.Tests;

public sealed class LogprobDecoderTests
{
    // Real LM Studio candidates for the model card's news example. Expected values were computed with the
    // reference client's formula (jev_style_client.py).
    private static readonly TokenLogprob[] NewsCandidates =
    [
        new(" C", -0.401868999004364), new(" D", -1.203893780708313), new(" A", -5.232253074645996),
        new(string.Empty, -5.694238662719727), new(" B", -6.188604354858398), new(" CD", -6.634555339813232),
    ];

    [Fact]
    public void Renormalises_over_the_declared_letters_like_the_reference_client()
    {
        Decision decision = LogprobDecoder.Decode(["World", "Sports", "Business", "Science/Technology"], new NextTokenLogprobs(NewsCandidates));
        Assert.Equal(0.005470099467846394, decision.ProbabilityOf("World"), 12);
        Assert.Equal(0.0021021182363084485, decision.ProbabilityOf("Sports"), 12);
        Assert.Equal(0.6851795194793939, decision.ProbabilityOf("Business"), 12);
        Assert.Equal(0.30724826281645123, decision.ProbabilityOf("Science/Technology"), 12);
        Assert.Equal("Business", decision.Selected.Option);
        Assert.Equal(['C', 'D', 'A', 'B'], decision.Ranked.Select(o => o.Letter));
        Assert.Equal(0.9764862631664551, decision.Diagnostics.OptionMass, 12);
        Assert.Equal(0, decision.Diagnostics.MissingOptions);
    }

    [Fact]
    public void Missing_letters_get_the_lowest_candidate_minus_five()
    {
        Decision decision = LogprobDecoder.Decode(["a", "b", "c"], new NextTokenLogprobs([new(" A", -0.1), new(" X", -3.0)]));
        Assert.Equal(-8.0, decision.Diagnostics.FloorLogprob, 12);
        Assert.Equal(2, decision.Diagnostics.MissingOptions);
        Assert.Equal(0.999259062314803, decision.ProbabilityAt(0), 12);
        Assert.Equal(0.0003704688425984187, decision.ProbabilityAt(1), 12);
        Assert.Null(decision.Options[1].Logprob);
        Assert.Equal(1.0, decision.Options.Sum(o => o.Probability), 12);
    }

    [Fact]
    public void Leading_space_and_bare_letters_both_count_and_the_best_one_wins()
    {
        Decision decision = LogprobDecoder.Decode(["yes", "no"], new NextTokenLogprobs([new("A", -2.0), new(" A", -0.5), new(" B", -1.0), new("AB", -0.1)]));
        Assert.Equal(-0.5, decision.Options[0].Logprob);
        Assert.Equal(1 / (1 + Math.Exp(-0.5)), decision.ProbabilityAt(0), 12);
    }

    [Fact]
    public void No_candidates_is_a_format_error() =>
        Assert.Throws<DecisionFormatException>(() => LogprobDecoder.Decode(["a", "b"], new NextTokenLogprobs([])));
}

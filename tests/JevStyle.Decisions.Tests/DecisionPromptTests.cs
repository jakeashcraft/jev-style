namespace JevStyle.Decisions.Tests;

public sealed class DecisionPromptTests
{
    [Fact]
    public void Prompt_matches_the_model_card_format_byte_for_byte()
    {
        string prompt = DecisionPrompt.Build("The film was excellent.", "What is the sentiment of this review?", ["negative", "positive"]);
        const string expected =
            "You are a decision function. Read the state, then answer the question by choosing exactly one option.\n\n" +
            "[State]\nThe film was excellent.\n\n" +
            "[Question]\nWhat is the sentiment of this review?\n\n" +
            "[Options]\nA. negative\nB. positive\n\n" +
            "Answer:";
        Assert.Equal(expected, prompt);
        Assert.DoesNotContain('\r', prompt);
    }

    [Fact]
    public void Options_are_lettered_in_order()
    {
        string prompt = DecisionPrompt.Build("s", "q", ["World", "Sports", "Business", "Science/Technology"]);
        Assert.EndsWith("[Options]\nA. World\nB. Sports\nC. Business\nD. Science/Technology\n\nAnswer:", prompt);
        Assert.Equal('Z', DecisionPrompt.LetterFor(25));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(27)]
    public void Rejects_option_counts_outside_2_to_26(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DecisionPrompt.Build("s", "q", [.. Enumerable.Range(0, count).Select(i => $"o{i}")]));

    [Fact]
    public void Rejects_null_options() =>
        Assert.Throws<ArgumentException>(() => DecisionPrompt.Build("s", "q", ["a", null!]));
}

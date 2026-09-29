using PCBoost.Core.Common;

namespace PCBoost.Core.Tests.Domain;

public sealed class TextRefTests
{
    [Fact]
    public void Of_keeps_key_and_arguments()
    {
        var text = TextRef.Of("Opt_Freed", 12, "Go");

        Assert.Equal("Opt_Freed", text.Key);
        Assert.Equal(new object[] { 12, "Go" }, text.Args);
        Assert.False(text.IsLiteral);
    }

    [Fact]
    public void Literal_uses_the_reserved_key_and_keeps_text_as_first_argument()
    {
        var text = TextRef.Literal("setup{0}.exe");

        Assert.True(text.IsLiteral);
        Assert.Equal(TextRef.LiteralKey, text.Key);
        Assert.Equal("setup{0}.exe", Assert.Single(text.Args));
    }

    [Fact]
    public void ToString_is_a_diagnostic_representation()
    {
        Assert.Equal("Key", TextRef.Of("Key").ToString());
        Assert.Equal("Key(1, a)", TextRef.Of("Key", 1, "a").ToString());
    }

    [Fact]
    public void No_arguments_gives_an_empty_array()
        => Assert.Empty(TextRef.Of("Key").Args);
}

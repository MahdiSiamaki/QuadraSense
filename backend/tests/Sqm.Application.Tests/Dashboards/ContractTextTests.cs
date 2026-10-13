using Sqm.Application.Dashboards;

namespace Sqm.Application.Tests.Dashboards;

/// <summary>The text checks that must agree with JavaScript and with PostgreSQL, not only with .NET.</summary>
public sealed class ContractTextTests
{
    [Theory]
    [InlineData("35000000", true)]
    [InlineData("35000000\n", false)]
    [InlineData("\u0663\u0665\u0660\u0660\u0660\u0660\u0660\u0660", false)]
    [InlineData("3500000", false)]
    public void A_contract_pattern_is_read_with_ASCII_digits_and_a_true_end(string value, bool matches) =>
        Assert.Equal(matches, ContractText.Matches(value, "^\\d{8}$"));

    [Fact]
    public void Text_is_storable_without_NUL_or_an_unpaired_surrogate()
    {
        // Built in code: an attribute's string is stored as UTF-8, which cannot hold a lone surrogate.
        Assert.True(ContractText.Storable("Ops"));
        Assert.True(ContractText.Storable("Ops \U0001F4CA"));
        Assert.False(ContractText.Storable("Ops\u0000"));
        Assert.False(ContractText.Storable("half " + (char)0xD83D));
        Assert.False(ContractText.Storable((char)0xDCCA + " alone"));
    }

    [Fact]
    public void Truncating_never_ends_inside_a_surrogate_pair()
    {
        var name = new string('a', 99) + "\U0001F4CA";
        var cut = ContractText.Truncate(name, 100);

        Assert.Equal(99, cut.Length);
        Assert.True(ContractText.Storable(cut));
    }
}

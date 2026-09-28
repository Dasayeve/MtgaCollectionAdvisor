using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>Why a deck the wallet can't cover is out of reach, shown in the deck list.</summary>
public sealed class WildcardShortfallTests
{
    [Fact]
    public void Affordable_need_has_no_shortfall()
    {
        var need = new WildcardNeed(3, 2, 1, 0);

        var shortfall = need.ShortfallAgainst(new WildcardInventory(10, 10, 10, 10));

        Assert.Equal(WildcardNeed.Zero, shortfall);
        Assert.Null(shortfall.DescribeShortfall());
    }

    [Fact]
    public void Shortfall_is_per_rarity_and_never_negative()
    {
        // Spare commons can't pay for rares: each rarity is short on its own.
        var need = new WildcardNeed(2, 1, 11, 0);

        var shortfall = need.ShortfallAgainst(new WildcardInventory(121, 65, 7, 10));

        Assert.Equal(new WildcardNeed(0, 0, 4, 0), shortfall);
    }

    [Fact]
    public void Description_names_the_rarest_first_and_pluralises()
    {
        Assert.Equal("4 rares short", new WildcardNeed(0, 0, 4, 0).DescribeShortfall());
        Assert.Equal("1 rare short", new WildcardNeed(0, 0, 1, 0).DescribeShortfall());
        Assert.Equal("1 mythic, 2 rares, 3 commons short", new WildcardNeed(3, 0, 2, 1).DescribeShortfall());
    }
}

using Clocktail.Domain.Rails;

namespace Clocktail.Tests.Domain;

public class RailEditsTests
{
    /// <summary>Stock MSVDD on the 5090 (drill 17): REL is already −50 mV.</summary>
    private static readonly RailOffsets StockMsvdd = new(-50_000, 0, 0, 0);

    [Fact]
    public void An_absolute_minus_25_on_stock_MSVDD_is_a_25_mV_raise()
    {
        var edits = new[] { new RailEdit(RailSlot.Rel, -25_000, Relative: false) };
        var after = RailEdits.Apply(StockMsvdd, edits);
        Assert.Equal(new RailOffsets(-25_000, 0, 0, 0), after);
        Assert.Equal([RailSlot.Rel], RailEdits.Raised(StockMsvdd, after));
        Assert.Equal(["rel -50 -> -25 mV (delta +25)"], RailEdits.Describe(StockMsvdd, after, edits));
    }

    [Fact]
    public void A_relative_minus_25_lowers_from_where_the_rail_is()
    {
        var edits = new[] { new RailEdit(RailSlot.Rel, -25_000, Relative: true) };
        var after = RailEdits.Apply(StockMsvdd, edits);
        Assert.Equal(new RailOffsets(-75_000, 0, 0, 0), after);
        Assert.Empty(RailEdits.Raised(StockMsvdd, after));
        Assert.Equal("rel=-75000", RailEdits.Encode(after, edits));
        Assert.Equal(["rel -50 -> -75 mV (delta -25)"], RailEdits.Describe(StockMsvdd, after, edits));
    }

    [Fact]
    public void Only_named_slots_are_encoded_and_unchanged_ones_are_not_raises()
    {
        var edits = new[] { new RailEdit(RailSlot.Vmin, 0, false), new RailEdit(RailSlot.Ov, -5_000, true) };
        var after = RailEdits.Apply(StockMsvdd, edits);
        Assert.Equal("vmin=0,ov=-5000", RailEdits.Encode(after, edits));
        Assert.Empty(RailEdits.Raised(StockMsvdd, after));
        Assert.Equal(["vmin 0 -> 0 mV (delta 0)", "ov 0 -> -5 mV (delta -5)"], RailEdits.Describe(StockMsvdd, after, edits));
    }
}

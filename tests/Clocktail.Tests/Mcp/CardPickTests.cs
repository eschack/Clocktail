using Clocktail.Core.Gpu;
using Clocktail.Mcp;
using Clocktail.Hw;

namespace Clocktail.Tests.Mcp;

public class CardPickTests
{
    private static GpuDevice Gpu(int i, string name, bool verified = false) => new()
    {
        Identity = new GpuIdentity(i, name, "", 0, 0, 0, 0, (uint)i, 0), NvapiHandle = i + 1,
        Target = verified ? new GpuTarget($"0000:{i:x2}:00.0", name, 0x2b8510de, 0, 12, 0, "PnP", 1, 1) : null,
    };

    [Fact]
    public void Only_one_already_identified_card_is_accepted_without_model_or_ordinal_fallback()
    {
        Assert.Null(Card.Pick([Gpu(0, "NVIDIA GeForce RTX 5090")]));
        Assert.Null(Card.Pick([Gpu(0, "NVIDIA GeForce RTX 4090")]));
        Assert.Null(Card.Pick([Gpu(0, "RTX 5090", true), Gpu(1, "RTX 5090", true)]));
        Assert.Equal("RTX PRO Blackwell", Card.Pick([Gpu(7, "RTX PRO Blackwell", true)])!.Identity.Name);
        Assert.Null(Card.Pick([]));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    public void Rtx_50_by_name(string name, bool rtx50) => Assert.Equal(rtx50, Card.IsRtx50(name));
}

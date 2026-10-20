using Clocktail.Core.Gpu;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class CardPickTests
{
    private static GpuDevice Gpu(int i, string name) => new() { Identity = new GpuIdentity(i, name, "", 0, 0, 0, 0, (uint)i, 0), NvapiHandle = i + 1 };

    [Fact]
    public void An_rtx_50_card_is_picked_wherever_it_sits()
    {
        var gpus = new[] { Gpu(0, "NVIDIA GeForce RTX 3060"), Gpu(1, "NVIDIA GeForce RTX 5090") };
        Assert.Equal("NVIDIA GeForce RTX 5090", Card.Pick(gpus)!.Identity.Name);
        Assert.Equal("NVIDIA GeForce RTX 4090", Card.Pick([Gpu(0, "NVIDIA GeForce RTX 4090")])!.Identity.Name);   // else the first
        Assert.Null(Card.Pick([]));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    public void Rtx_50_by_name(string name, bool rtx50) => Assert.Equal(rtx50, Card.IsRtx50(name));
}

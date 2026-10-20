using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class NvgwlsTests
{
    [Fact]
    public void Exit_code_2_with_a_driver_reset_is_a_hang_the_card_failed()
    {
        Assert.StartsWith("hang: the driver reset", Nvgwls.Verdict(2, 1));
        Assert.True(Nvgwls.Failed(2, 1));
    }

    [Fact]
    public void Exit_code_2_without_a_reset_is_a_run_that_could_not_start()
    {
        Assert.Equal(Nvgwls.Verdict(2), Nvgwls.Verdict(2, 0));
        Assert.False(Nvgwls.Failed(2, 0));
    }

    [Fact]
    public void An_unreadable_log_leaves_exit_code_2_open_and_the_others_as_they_were()
    {
        Assert.Contains("could not be read", Nvgwls.Verdict(2, null));
        Assert.False(Nvgwls.Failed(2, null));
        Assert.Equal("pass", Nvgwls.Verdict(0, null));
        Assert.True(Nvgwls.Failed(6, null));
    }

    [Fact]
    public void A_reset_fails_even_a_run_that_exited_0()
    {
        Assert.True(Nvgwls.Failed(0, 2));
        Assert.Contains("2 reset events", Nvgwls.Verdict(0, 2));
    }
}

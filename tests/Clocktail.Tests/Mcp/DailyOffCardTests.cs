using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report's "daily profile kept but not on the card": built from card_settings as the reference card
/// answered it on the founders daily profile (2026-10-20), then with one setting at a time taken off it.</summary>
public class DailyOffCardTests
{
    private const string OnCard = """
        {"power":{"limit_w":600,"daily_w":600},
         "clock_offsets":{"core":{"offset_mhz":0},"memory":{"offset_mhz":358,"daily_mhz":358}},
         "vf_curve":{"points_off_daily":0},
         "rails":{"msvdd":{"offsets_mv":{"rel":0,"alt":0,"ov":0,"vmin":0},"daily_offsets_mv":{"rel":0,"alt":0,"ov":0,"vmin":0}}},
         "clock_domains":{"xbar":{"offset_mhz":300,"vdemand_mv":20,"daily_offset_mhz":300,"daily_vdemand_mv":20}},
         "fans":{"curves":[{"index":0,"curve":"50:20:1100,79:60:1995,86:100:2875","daily_curve":"50:20:1100,79:60:1995,86:100:2875"},
                           {"index":1,"curve":"50:20:1100,79:60:1995,86:100:2875","daily_curve":"50:20:1100,79:60:1995,86:100:2875"}]},
         "daily_profile":{"suspended":false,"keeps":["power/fe","fancurve/0","fancurve/1","pstates20/memory","rails/msvdd","vdemand/xbar","clockdomain/xbar","vf/core"]},
         "trial_in_force":false}
        """;

    private static List<string>? Off(string json) => SettingsTools.DailyOffCard(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_card_on_its_daily_profile_differs_in_nothing() => Assert.Empty(Off(OnCard)!);

    [Fact]
    public void A_card_back_at_stock_after_a_boot_lists_what_is_missing()
    {
        string stock = OnCard.Replace("\"limit_w\":600", "\"limit_w\":575").Replace("\"offset_mhz\":358", "\"offset_mhz\":0")
            .Replace("\"points_off_daily\":0", "\"points_off_daily\":70").Replace("\"offset_mhz\":300,\"vdemand_mv\":20", "\"offset_mhz\":0,\"vdemand_mv\":0")
            .Replace("\"offsets_mv\":{\"rel\":0", "\"offsets_mv\":{\"rel\":-50");
        var off = Off(stock)!;
        Assert.Contains("power limit 575 W (daily 600)", off);
        Assert.Contains("memory offset 0 MHz (daily +358)", off);
        Assert.Contains("V/F curve (70 points off it)", off);
        Assert.Contains("XBAR offset 0 MHz (daily +300)", off);
        Assert.Contains("XBAR voltage demand 0 mV (daily +20)", off);
        Assert.Contains("MSVDD REL offset -50 mV (daily 0)", off);
        Assert.Equal(6, off.Count);
    }

    [Fact]
    public void A_fan_curve_or_a_section_that_cannot_be_read_counts_too()
    {
        var off = Off(OnCard.Replace("\"curve\":\"50:20:1100,79:60:1995,86:100:2875\",\"daily_curve\"", "\"curve\":\"50:20:1100,83:50:1775,88:100:2875\",\"daily_curve\"")
                            .Replace("\"vf_curve\":{\"points_off_daily\":0}", "\"vf_curve\":{\"error\":\"unreadable\"}"))!;
        Assert.Equal(3, off.Count);   // both fan curves and the curve
        Assert.Contains("V/F curve unreadable", off);
        Assert.StartsWith("fan curve 0 (50 °C 20 % · 83 °C 50 %", off[0].Replace("fan curve 1", "fan curve 0"));
    }

    [Fact]
    public void Nothing_to_compare_while_suspended_in_a_trial_or_without_a_profile()
    {
        Assert.Null(Off(OnCard.Replace("\"suspended\":false", "\"suspended\":true")));
        Assert.Null(Off(OnCard.Replace("\"trial_in_force\":false", "\"trial_in_force\":true")));
        Assert.Null(Off("""{"power":{"limit_w":575},"trial_in_force":false}"""));
        Assert.Null(Off("""{"daily_profile":{"error":"the Clocktail service does not answer"}}"""));
    }
}

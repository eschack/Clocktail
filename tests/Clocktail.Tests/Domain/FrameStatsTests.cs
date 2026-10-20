using System.Globalization;
using Clocktail.Domain.Fps;

namespace Clocktail.Tests.Domain;

/// <summary>PresentMon 2.6.0's CSV as it came on the reference machine (header as captured 2026-10-20), with a game added.</summary>
public class FrameStatsTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay";

    private static string Row(string app, double ms, double shown) => string.Create(CultureInfo.InvariantCulture,
        $"{app},1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,NA,{ms},{shown},0.1,1,1,0,{ms},1,1,1,1,1,1,0,0,0");

    private static List<string> Capture()
    {
        var lines = new List<string> { Header };
        // The game: 990 frames at 7 ms, 9 at 20 ms, 1 at 50 ms; the compositor at 60 Hz; one untimed first frame.
        lines.Add(Row("Game.exe", 0, 0));
        for (int i = 0; i < 990; i++) lines.Add(Row("Game.exe", 7, 7));
        for (int i = 0; i < 9; i++) lines.Add(Row("Game.exe", 20, 20));
        lines.Add(Row("Game.exe", 50, 50));
        for (int i = 0; i < 1200; i++) lines.Add(Row("dwm.exe", 16.67, 16.67));
        return lines;
    }

    [Fact]
    public void The_game_is_the_busiest_app_that_is_not_the_compositor()
    {
        var apps = FrameStats.Parse(Capture());
        Assert.Equal("dwm.exe", apps[0].Name);   // more frames, never the game
        var g = FrameStats.Game(apps, null)!;
        Assert.Equal("Game.exe", g.Name);
        Assert.Equal(1000, g.Frames);             // the untimed first frame is not one
        Assert.Equal(139.7, g.AvgFps);            // 1000 frames in 7.16 s
        Assert.Equal(43.5, g.Low1Fps);            // the slowest 10 frames: 9 x 20 ms and 50 ms, 23 ms on average
        Assert.Equal(20, g.Low01Fps);             // the slowest one: 50 ms
        Assert.Equal(7, g.P99Ms);                 // the 99th percentile sits at the stutter's edge: why the lows average the slowest frames
        Assert.Equal(7, g.P50Ms);
        Assert.Equal(139.7, g.DisplayedFps);
        Assert.Equal("Game.exe", FrameStats.Game(apps, "game")!.Name);   // by name, with or without .exe
        Assert.Null(FrameStats.Game(apps, "other"));
    }

    [Fact]
    public void Rows_read_in_windows_keep_the_header()
    {
        var all = Capture();
        var total = new Frames();
        foreach (var l in all.Take(500)) total.Add(l);
        var window = total.Next();
        foreach (var l in all.Skip(500).Take(500)) { total.Add(l); window.Add(l); }
        Assert.Equal(500, FrameStats.Game(window.Apps(), null)!.Frames);
        Assert.Equal(998, FrameStats.Game(total.Apps(), null)!.Frames);   // the first 500 lines held the header, an untimed frame and 498
    }

    [Fact]
    public void A_header_behind_a_byte_order_mark_is_still_the_header()
    {
        // PresentMon 2.6.0's file starts with one (seen on the reference machine).
        var apps = FrameStats.Parse([(char)0xFEFF + Header, Row("Game.exe", 10, 10)]);
        Assert.Equal("Game.exe", apps.Single().Name);
    }

    [Fact]
    public void Lines_before_the_header_and_broken_rows_are_skipped()
    {
        var apps = FrameStats.Parse(["warning: something", Header, "Game.exe,1,2", Row("Game.exe", 10, 0)]);
        Assert.Equal(1, apps.Single().Frames);
        Assert.Null(apps.Single().DisplayedFps);
    }
}

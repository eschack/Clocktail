namespace Clocktail.Hw;

/// <summary>
/// Whether the driver a long-running process opened is still there. Two signs say it is not: an NVAPI session latched a
/// handle loss (-8/-10: the handles were invalidated, as a device restart or a driver update does), or NVML answered
/// that the driver is gone (<see cref="NvmlApi.MeansDriverGone"/>). Nothing this process holds can be rebound safely
/// after that (NVML's state belongs to the driver instance that went away), so the answer is a fresh process.
/// </summary>
public static class DriverLoss
{
    /// <summary>
    /// Why the driver this process opened is gone, or null while it is not. A session's handle loss also latches NVML as
    /// lost: the same restart took NVML's state, and the next telemetry read would otherwise go through it.
    /// </summary>
    public static string? Why(NvmlApi? nvml, IEnumerable<GpuSession> sessions)
    {
        foreach (var s in sessions)
        {
            if (!s.Faulted) continue;
            nvml?.MarkLost();
            return $"{s.Label}: NVAPI handle lost (status {s.FaultStatus})";
        }
        return nvml is { Lost: true } n ? $"NVML: {NvmlApi.Describe(n.LostCode)} (status {n.LostCode})" : null;
    }
}

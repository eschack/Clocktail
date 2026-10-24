using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

/// <summary>
/// When each GPU stopped answering in the hang a reset ends (<see cref="FlightRecorder.Hang"/>), for whose crash it is
/// ("crashed"). One reset can be logged as bursts seconds apart (<see cref="ResetBursts"/>: the device restart of 2026-10-19
/// logged events over 18 s), and each burst is answered: a later one whose flight window shows no hang of its own continues
/// the one before when it comes within <see cref="SameResetWithin"/>, and keeps its times, so the crash is not recorded a
/// second time against a trial written in between. With no hang to go on, both times are
/// <see cref="FlightRecorder.HangDetectAtLeast"/> before the reset was seen: the driver logs a reset only after a hang at
/// least that long.
/// </summary>
public sealed class ResetHangs
{
    public static readonly TimeSpan SameResetWithin = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (DateTimeOffset Seen, (DateTimeOffset Answered, DateTimeOffset Stopped) Hang)> _last = new(StringComparer.OrdinalIgnoreCase);

    public (DateTimeOffset Answered, DateTimeOffset Stopped) For(string gpuId, IReadOnlyList<FlightSample> window, DateTimeOffset seen)
    {
        lock (_last)
        {
            var hang = FlightRecorder.Hang(window, seen)
                ?? (_last.TryGetValue(gpuId, out var before) && seen - before.Seen <= SameResetWithin
                    ? before.Hang
                    : (seen - FlightRecorder.HangDetectAtLeast, seen - FlightRecorder.HangDetectAtLeast));
            _last[gpuId] = (seen, hang);
            return hang;
        }
    }
}

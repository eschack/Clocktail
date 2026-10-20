using System.Globalization;

namespace Clocktail.Domain.Platform;

/// <summary>
/// Keeps a worker process running: starts it, waits for it to end, and starts it again unless it ended because it was
/// asked to (exit code 0). The broker and the tray run under one each, because a process that has NVML loaded can die
/// of an access violation that .NET cannot catch: on 2026-10-19 21:48:29Z a device restart tore the driver down under a
/// call blocked in NVML, and the broker, the tray and the MCP server all died within 15 ms. Nothing restarted the broker
/// (the task's RestartOnFailure only covers a start that fails, not a process that started and later died), so the card
/// stayed at stock until someone ran the task by hand. The supervisor itself never loads a driver library.
/// <para>
/// A worker that finds the driver it opened gone (<see cref="DriverLost"/>) leaves on purpose so that a fresh process can
/// open the driver that came back; it is started again like a crash. Restarts back off while the worker keeps dying
/// quickly (the driver may still be coming back, and a broken install must not spin), and the backoff starts over once a
/// worker has run for <see cref="Healthy"/>.
/// </para>
/// </summary>
public sealed class Supervisor(
    Func<int, int> runWorker,
    Action<string> log,
    Func<DateTimeOffset> now,
    Action<TimeSpan> sleep,
    int? giveUpAfter = null)
{
    /// <summary>The exit code of a worker whose driver went away under it (EX_TEMPFAIL): start a new one.</summary>
    public const int DriverLost = 75;

    /// <summary>A worker that ran this long was not crash-looping: the next restart waits the shortest time again.</summary>
    public static readonly TimeSpan Healthy = TimeSpan.FromMinutes(5);

    /// <summary>Waits before restarting after 1, 2, 3, ... quick exits in a row; the last one repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    public static TimeSpan Delay(int quickExits) => Backoff[Math.Clamp(quickExits - 1, 0, Backoff.Count - 1)];

    /// <summary>
    /// Runs workers until one exits 0, and returns 0; with <paramref name="giveUpAfter"/>, also stops (returning the
    /// last exit code) once that many quick exits other than <see cref="DriverLost"/> came in a row. The argument
    /// <c>runWorker</c> gets is the number of starts before this one (0 for the first), and it returns the exit code.
    /// </summary>
    public int Run()
    {
        int quick = 0, crashes = 0;
        for (int attempt = 0; ; attempt++)
        {
            var started = now();
            int code;
            // Whatever goes wrong starting one is a failed start like any other: keeping a worker running is all this does.
            try { code = runWorker(attempt); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"could not run a worker: {e.Message}");
                code = 1;
            }
            var ran = now() - started;
            if (code == 0)
            {
                log($"worker stopped cleanly after {Span(ran)}; the supervisor stops too");
                return 0;
            }
            bool healthy = ran >= Healthy;
            quick = healthy ? 1 : quick + 1;
            crashes = code == DriverLost ? 0 : healthy ? 1 : crashes + 1;
            if (giveUpAfter is { } limit && crashes >= limit)
            {
                log($"worker exited with {Describe(code)} after {Span(ran)}, {crashes} quick exits in a row; giving up");
                return code;
            }
            var wait = Delay(quick);
            log($"worker exited with {Describe(code)} after {Span(ran)}; starting a new one in {Span(wait)}");
            sleep(wait);
        }
    }

    /// <summary>An exit code as a person reads it: NTSTATUS crash codes in hex with their name.</summary>
    public static string Describe(int code) => code switch
    {
        DriverLost => $"{DriverLost} (the driver it opened went away)",
        unchecked((int)0xC0000005) => "0xC0000005 (access violation)",
        unchecked((int)0xC0000409) => "0xC0000409 (fail-fast)",
        unchecked((int)0xE0434352) => "0xE0434352 (unhandled .NET exception)",
        < 0 => $"0x{code:X8}",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };

    private static string Span(TimeSpan t) => t.TotalSeconds < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.#} s")
        : t.TotalMinutes < 120 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalMinutes:0} min")
        : string.Create(CultureInfo.InvariantCulture, $"{t.TotalHours:0.#} h");
}

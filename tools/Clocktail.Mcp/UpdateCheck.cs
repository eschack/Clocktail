using System.Reflection;
using System.Text.Json;
using Clocktail.Domain.Platform;

namespace Clocktail.Mcp;

/// <summary>
/// Whether a newer Clocktail is on npm, for card_report. The agents run the installed server, not npx, so nothing
/// updates by itself: this is how a person hears of a new release. One small read of the npm registry
/// (registry.npmjs.org/clocktail/latest: the version only, nothing about this computer or card is sent), at most every
/// <see cref="Every"/>, never blocking a report for more than <see cref="Wait"/>. CLOCKTAIL_NO_UPDATE_CHECK=1 turns it off.
/// </summary>
public static class UpdateCheck
{
    public const string Url = "https://registry.npmjs.org/clocktail/latest";
    public const string UpdateCommand = "npx -y clocktail@latest setup";
    public static readonly TimeSpan Every = TimeSpan.FromHours(6), Wait = TimeSpan.FromSeconds(2), Timeout = TimeSpan.FromSeconds(5);

    /// <summary>This server's version ("0.1.4"), from the build stamp.</summary>
    public static string Current { get; } = typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        is { } v ? (v.IndexOf('+') is var i and >= 0 ? v[..i] : v) : "unknown";

    public static bool Off => Environment.GetEnvironmentVariable("CLOCKTAIL_NO_UPDATE_CHECK") is "1" or "true";

    private static readonly Lock Gate = new();
    private static Task<string?>? _fetch;
    private static DateTimeOffset _fetchedAt;

    /// <summary>The latest version on npm, or null when it is not known (off, offline, or still on its way).</summary>
    public static string? Latest()
    {
        if (Off) return null;
        Task<string?> t;
        lock (Gate)
        {
            if (_fetch == null || (_fetch.IsCompleted && DateTimeOffset.UtcNow - _fetchedAt > (_fetch.Result == null ? TimeSpan.FromMinutes(10) : Every)))
            {
                _fetchedAt = DateTimeOffset.UtcNow;
                _fetch = Task.Run(Fetch);
            }
            t = _fetch;
        }
        return t.Wait(Wait) ? t.Result : null;
    }

    private static async Task<string?> Fetch()
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"clocktail-mcp/{Current}");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Url));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    /// <summary>The report row's text: what runs now, and what to do when a newer release is out.</summary>
    public static (string Now, bool Newer, string Note) Describe(string? latest) =>
        latest == null
            ? (Current, false, Off ? "update check off (CLOCKTAIL_NO_UPDATE_CHECK)" : "could not check npm for a newer version")
            : ReleaseVersion.IsNewer(latest, Current)
                ? ($"{Current} → {latest} available", true, $"run {UpdateCommand}, then restart your agents; the card and its profiles stay as they are")
                : ($"{Current} (latest)", false, "up to date");
}

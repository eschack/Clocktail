namespace Clocktail.Broker;

/// <summary>
/// Where the broker keeps its state. It runs as the user (elevated), so its <c>%LocalAppData%</c> is the user's:
/// the journal, the apply sentinel and StabilityLab's judge.lock all live under the same per-user root, which is
/// why the broker and the user's own tools see one another's locks without anything moving to a machine-wide
/// location. The one tool that must be reachable without elevation lives under Program Files instead, so a
/// user-level process cannot replace the binary that runs elevated at logon.
/// </summary>
internal static class BrokerPaths
{
    public static string StateRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail");

    public static string GpuStateDir(string stableId) => Path.Combine(StateRoot(), stableId);

    public static string JudgeLockPath() => Path.Combine(StateRoot(), "judge.lock");

    public static string InstallDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Clocktail");

    public const string TaskName = "ClocktailBroker";
}

namespace Clocktail.Hw;

/// <summary>NVAPI status codes this layer interprets. Everything else is passed through as a number.</summary>
public static class NvStatus
{
    public const int Ok = 0;
    public const int Error = -1;
    public const int InvalidHandle = -8;
    public const int HandleInvalidated = -10;
    public const int NotSupported = -104;
    /// <summary>Private setters refuse a non-administrator process with this (seen live on 617.42 for SetPstates20).</summary>
    public const int InvalidUserPrivilege = -137;

    /// <summary>The refusal code for anything blocked locally (null pointer, whitelist, broken guards).</summary>
    public const int Local = -1000;

    /// <summary>A GPU handle that stopped being valid (driver reset, sleep, surprise removal): status -8 or
    /// -10.</summary>
    public static bool IsHandleLoss(int status) => status is InvalidHandle or HandleInvalidated;
}

public enum NvOutcome
{
    /// <summary>The driver accepted the call and the buffer holds its reply.</summary>
    Ok,
    /// <summary>The driver returned a non-zero status; <see cref="NvResult.Status"/> carries it.</summary>
    Nvapi,
    /// <summary>The driver does not expose this interface. A capability gap, not an error.</summary>
    Missing,
    /// <summary>The session latched a handle loss earlier; the driver was not touched.</summary>
    Poisoned,
    /// <summary>The request failed local validation (shape, version word, write policy); the driver was not touched.</summary>
    Rejected,
    /// <summary>The driver wrote outside the declared structure; the reply was discarded.</summary>
    GuardBroken,
}

/// <summary>Result of one driver call. A value, never an exception: callers decide what each outcome means.</summary>
public readonly record struct NvResult(NvOutcome Outcome, int Status, Slot Slot, string? Detail)
{
    public bool IsOk => Outcome == NvOutcome.Ok;

    public static NvResult Ok(Slot slot) => new(NvOutcome.Ok, NvStatus.Ok, slot, null);
    public static NvResult FromStatus(Slot slot, int status) =>
        status == NvStatus.Ok ? Ok(slot) : new(NvOutcome.Nvapi, status, slot, null);
    public static NvResult Missing(Slot slot) => new(NvOutcome.Missing, NvStatus.Local, slot, "not exposed by this driver");
    public static NvResult Poisoned(Slot slot, int faultStatus) => new(NvOutcome.Poisoned, faultStatus, slot, "session latched a handle loss; reconnect first");
    public static NvResult Rejected(Slot slot, string why) => new(NvOutcome.Rejected, NvStatus.Local, slot, why);
    public static NvResult GuardBroken(Slot slot, string where) => new(NvOutcome.GuardBroken, NvStatus.Local, slot, $"driver wrote outside the structure ({where})");

    public override string ToString() => Outcome switch
    {
        NvOutcome.Ok => $"{Slot}: ok",
        NvOutcome.Nvapi => Status switch
        {
            NvStatus.InvalidUserPrivilege => $"{Slot}: NVAPI status -137 (invalid user privilege: this process is not running as administrator)",
            NvStatus.NotSupported => $"{Slot}: NVAPI status -104 (not supported)",
            _ => $"{Slot}: NVAPI status {Status}",
        },
        _ => $"{Slot}: {Outcome} ({Detail})",
    };
}

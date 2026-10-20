namespace Clocktail.Hw;

/// <summary>
/// Resolves interfaces to function pointers. The real implementation asks nvapi64.dll; tests hand out
/// pointers to managed stand-ins. Everything above this line is testable without a GPU.
/// </summary>
public interface INvApi
{
    /// <summary>The function pointer for a slot, or 0 when this driver does not expose it.</summary>
    nint Resolve(Slot slot);
}

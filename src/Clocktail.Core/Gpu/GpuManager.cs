using Clocktail.Core.Native;
using Clocktail.Hw;

namespace Clocktail.Core.Gpu;

public sealed record GpuIdentity(
    int Index,
    string Name,
    string Vbios,
    uint DeviceId,
    uint SubSystemId,
    uint RevisionId,
    uint ExtDeviceId,
    uint Bus,
    uint Slot)
{
    /// <summary>Stable ID that survives index reordering, built from the PCI identity.
    /// Note: it includes bus/slot, so moving the card to another PCIe slot changes the ID.</summary>
    public string StableId => $"pci-{DeviceId:x8}-{SubSystemId:x8}-{ExtDeviceId:x8}-b{Bus:x2}-s{Slot:x2}";

    /// <summary>Profile file stem: "&lt;vbios&gt;-&lt;stable id&gt;".</summary>
    public string ProfileStem => $"{Vbios}-{StableId}";
}

public sealed class GpuDevice
{
    public required GpuIdentity Identity { get; init; }
    public required nint NvapiHandle { get; init; }
    public nint NvmlHandle { get; init; }
    public bool HasNvml => NvmlHandle != 0;
}

public sealed class GpuManager : IDisposable
{
    public NvApi NvApi { get; }
    public NvmlApi? Nvml { get; }
    public IReadOnlyList<GpuDevice> Gpus { get; }
    public (uint Version, string Branch) Driver { get; }

    private GpuManager(NvApi nvapi, NvmlApi? nvml, IReadOnlyList<GpuDevice> gpus, (uint, string) driver)
    {
        NvApi = nvapi; Nvml = nvml; Gpus = gpus; Driver = driver;
    }

    public static GpuManager Open()
    {
        var nvapi = NvApi.Load();
        NvmlApi? nvml = null;
        try { nvml = NvmlApi.Load(); } catch (Exception e) when (e is DllNotFoundException or NvmlException or EntryPointNotFoundException) { }

        var handles = nvapi.EnumPhysicalGpus();
        if (handles.Length == 0) throw new InvalidOperationException("No NVIDIA physical GPU was returned.");

        // Index NVML devices by PCI bus so NVAPI and NVML refer to the same card.
        var nvmlByBus = new Dictionary<uint, nint>();
        if (nvml != null)
        {
            uint n = nvml.DeviceCount();
            for (uint i = 0; i < n; i++)
            {
                nint h = nvml.HandleByIndex(i);
                nvmlByBus[nvml.Pci(h).Bus] = h;
            }
        }

        var list = new List<GpuDevice>();
        for (int i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            var pci = nvapi.GpuPciIdentifiers(h);
            uint bus = nvapi.GpuBusId(h);
            var id = new GpuIdentity(i, nvapi.GpuFullName(h), SafeVbios(nvapi, h),
                pci.DeviceId, pci.SubSystemId, pci.RevisionId, pci.ExtDeviceId, bus, SafeSlot(nvapi, h));
            list.Add(new GpuDevice { Identity = id, NvapiHandle = h, NvmlHandle = nvmlByBus.GetValueOrDefault(bus) });
        }

        return new GpuManager(nvapi, nvml, list, nvapi.DriverAndBranch());
    }

    private static string SafeVbios(NvApi api, nint h) { try { return api.GpuVbiosVersion(h); } catch (NvApiException) { return ""; } }
    private static uint SafeSlot(NvApi api, nint h) { try { return api.GpuBusSlotId(h); } catch (NvApiException) { return 0; } }

    public GpuDevice Select(int? index, string? stableId)
    {
        if (stableId != null)
            return Gpus.FirstOrDefault(g => string.Equals(g.Identity.StableId, stableId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No GPU has stable id '{stableId}'.");
        int i = index ?? 0;
        if (i < 0 || i >= Gpus.Count) throw new ArgumentException($"GPU index {i} is out of range (0..{Gpus.Count - 1}).");
        return Gpus[i];
    }

    public void Dispose()
    {
        Nvml?.Dispose();
        NvApi.Dispose();
    }
}

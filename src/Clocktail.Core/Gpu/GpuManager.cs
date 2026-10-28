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
    public GpuTarget? Target { get; init; }
}

public sealed class GpuManager : IDisposable
{
    public NvApi NvApi { get; }
    public NvmlApi? Nvml { get; }
    public IReadOnlyList<GpuDevice> Gpus { get; }
    public IReadOnlyList<string> OtherGpus { get; }
    public (uint Version, string Branch) Driver { get; }

    private GpuManager(NvApi nvapi, NvmlApi? nvml, IReadOnlyList<GpuDevice> gpus, (uint, string) driver, IReadOnlyList<string> otherGpus)
    {
        NvApi = nvapi; Nvml = nvml; Gpus = gpus; Driver = driver; OtherGpus = otherGpus;
    }

    public static GpuManager Open()
    {
        var present = DisplayInventory.Read(); // before a vendor library is loaded
        var nvml = NvmlApi.Load();
        NvApi? nvapi = null;
        try
        {
            var candidates = new List<(GpuTarget Target, nint Handle)>();
            var inventory = new List<(string Pci, string Name)>();
            for (uint i = 0; i < nvml.DeviceCount(); i++)
            {
                nint h = nvml.HandleByIndex(i);
                var pci = nvml.Pci(h);
                string name = nvml.Name(h);
                inventory.Add((pci.BusId, name));
                var cc = nvml.ComputeCapability(h);
                if (cc != (12, 0)) continue;
                var pnp = GpuTarget.Match(present, pci.BusId, pci.DeviceId, pci.SubSystemId);
                var target = new GpuTarget(PciAddress.Parse(pci.BusId)!.Value.ToString(), name, pci.DeviceId, pci.SubSystemId,
                    cc.Major, cc.Minor, pnp.InstanceId, present.Count(p => p.Nvidia), present.Count);
                target.Validate();
                candidates.Add((target, h));
            }
            var chosen = Choose(candidates.Select(c => c.Target).ToList(), Environment.GetEnvironmentVariable("CLOCKTAIL_GPU_PCI"));
            var device = candidates.Single(c => c.Target == chosen);
            var address = PciAddress.Parse(chosen.Pci)!.Value;
            nvapi = NvApi.Load();
            var matches = nvapi.EnumPhysicalGpus().Where(h => nvapi.GpuBusId(h) == address.Bus && nvapi.GpuBusSlotId(h) == address.Device).ToList();
            if (matches.Count != 1) throw new NotSupportedException("NVAPI does not identify exactly one handle at the selected PCI address");
            nint handle = matches[0];
            var ids = nvapi.GpuPciIdentifiers(handle);
            if (ids.DeviceId != chosen.DeviceId || ids.SubSystemId != chosen.SubSystemId)
                throw new NotSupportedException("NVAPI, NVML and PnP disagree about the selected GPU");
            var id = new GpuIdentity(0, chosen.Name, SafeVbios(nvapi, handle), ids.DeviceId, ids.SubSystemId,
                ids.RevisionId, ids.ExtDeviceId, address.Bus, address.Device);
            return new GpuManager(nvapi, nvml, [new GpuDevice { Identity = id, NvapiHandle = handle, NvmlHandle = device.Handle, Target = chosen }], nvapi.DriverAndBranch(), inventory.Where(g => PciAddress.Parse(g.Pci) != address).Select(g => g.Name).ToList());
        }
        catch { nvapi?.Dispose(); nvml.Dispose(); throw; }
    }

    private static string SafeVbios(NvApi api, nint h) { try { return api.GpuVbiosVersion(h); } catch (NvApiException) { return ""; } }
    public static GpuTarget Choose(IReadOnlyList<GpuTarget> targets, string? requested)
    {
        foreach (var target in targets) target.Validate();
        if (requested != null)
        {
            var pci = PciAddress.Parse(requested) ?? throw new NotSupportedException("CLOCKTAIL_GPU_PCI is not a supported PCI address");
            return targets.SingleOrDefault(t => PciAddress.Parse(t.Pci) == pci) ?? throw new NotSupportedException("the selected GPU is absent or unsupported; no fallback");
        }
        return targets.Count == 1 ? targets[0] : throw new NotSupportedException("select exactly one supported GPU by CLOCKTAIL_GPU_PCI; enumeration order is never a target");
    }

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

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Clocktail.Lab.Game;

/// <summary>One Vulkan physical device that can ray trace (VK_KHR_ray_query or VK_KHR_ray_tracing_pipeline), with its PCI
/// address when the driver says it (VK_EXT_pci_bus_info).</summary>
public sealed record RtDevice(string Name, uint Vendor, uint Device, string? Pci);

/// <summary>
/// Which GPUs Quake II RTX could pick. The stock game has no device selector: it takes the first Vulkan device with a
/// ray-tracing extension. So the test runs only when exactly one such device exists and it is the card under test, by its
/// PCI address; a model name or an ordinal is never trusted. Inventory only, in a child process of its own (clocktail-lab
/// vk-devices): an instance and its device list, no logical device, surface or work.
/// </summary>
public static unsafe class VulkanDevices
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ApplicationInfo { public uint Type; public void* Next; public byte* Name; public uint Version; public byte* Engine; public uint EngineVersion, ApiVersion; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceInfo { public uint Type; public void* Next; public uint Flags; public ApplicationInfo* App; public uint LayerCount; public void* Layers; public uint ExtensionCount; public void* Extensions; }
    /// <summary>VkPhysicalDeviceProperties2: the header, then VkPhysicalDeviceProperties (vendor +24, device +28, name +36).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 840)]
    private struct Properties { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public PciInfo* Next; [FieldOffset(24)] public uint Vendor; [FieldOffset(28)] public uint Device; [FieldOffset(36)] public fixed byte Name[256]; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo { public uint Type; public void* Next; public uint Domain, Bus, Device, Function; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Create(InstanceInfo* info, void* allocator, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Destroy(nint instance, void* allocator);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Enumerate(nint instance, ref uint count, nint* devices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Extensions(nint device, byte* layer, ref uint count, byte* properties);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetProperties(nint device, Properties* properties);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint GetProc(nint instance, [MarshalAs(UnmanagedType.LPStr)] string name);

    public static IReadOnlyList<RtDevice> List()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("the game test needs 64-bit Windows");
        var dll = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));
        nint instance = 0;
        Destroy? destroy = null;
        try
        {
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll, name));
            var get = Export<GetProc>("vkGetInstanceProcAddr");
            T Entry<T>(string name) where T : Delegate =>
                get(instance, name) is var fn && fn != 0 ? Marshal.GetDelegateForFunctionPointer<T>(fn) : throw new NotSupportedException("Vulkan is missing " + name);
            var app = new ApplicationInfo { Type = 0, ApiVersion = (1u << 22) | (1u << 12) };
            var info = new InstanceInfo { Type = 1, App = &app };
            Check(Export<Create>("vkCreateInstance")(&info, null, out instance), "vkCreateInstance");
            destroy = Entry<Destroy>("vkDestroyInstance");
            var enumerate = Entry<Enumerate>("vkEnumeratePhysicalDevices");
            var extensions = Entry<Extensions>("vkEnumerateDeviceExtensionProperties");
            var properties = Entry<GetProperties>("vkGetPhysicalDeviceProperties2");
            uint count = 0;
            Check(enumerate(instance, ref count, null), "the Vulkan device count");
            if (count > 64) throw new NotSupportedException("an implausible Vulkan device count");
            var devices = new nint[count];
            if (count > 0) fixed (nint* p = devices) Check(enumerate(instance, ref count, p), "the Vulkan device list");
            var list = new List<RtDevice>();
            for (int i = 0; i < count; i++)
            {
                uint n = 0;
                Check(extensions(devices[i], null, ref n, null), "a Vulkan extension count");
                if (n > 4096) throw new NotSupportedException("an implausible Vulkan extension count");
                var raw = new byte[checked((int)n * 260)];   // VkExtensionProperties: char[256], uint32_t
                fixed (byte* p = raw) Check(extensions(devices[i], null, ref n, p), "a Vulkan extension list");
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int e = 0; e < n; e++)
                {
                    var bytes = raw.AsSpan(e * 260, 256);
                    int end = bytes.IndexOf((byte)0);
                    names.Add(Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]));
                }
                if (!names.Contains("VK_KHR_ray_query") && !names.Contains("VK_KHR_ray_tracing_pipeline")) continue;
                var pci = new PciInfo { Type = 1000212000 };
                var props = new Properties { Type = 1000059001, Next = names.Contains("VK_EXT_pci_bus_info") ? &pci : null };
                properties(devices[i], &props);
                var nameBytes = new ReadOnlySpan<byte>(props.Name, 256);
                int nul = nameBytes.IndexOf((byte)0);
                string name = Encoding.UTF8.GetString(nul < 0 ? nameBytes : nameBytes[..nul]);
                list.Add(new RtDevice(name, props.Vendor, props.Device, props.Next == null ? null : Pci(pci.Domain, pci.Bus, pci.Device, pci.Function)));
            }
            return list;
        }
        finally
        {
            if (instance != 0) destroy?.Invoke(instance, null);
            NativeLibrary.Free(dll);
        }
    }

    public static string Pci(uint domain, uint bus, uint device, uint function) => $"{domain:x4}:{bus:x2}:{device:x2}.{function:x}";

    public static (uint Domain, uint Bus, uint Device, uint Function)? Address(string? pci)
    {
        var p = pci?.Trim().Split(':', '.');
        return p is { Length: 4 }
            && uint.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var domain)
            && uint.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) && bus <= 255
            && uint.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var device) && device <= 31
            && uint.TryParse(p[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var function) && function <= 7
                ? (domain, bus, device, function) : null;
    }

    /// <summary>Why the game could render elsewhere than <paramref name="pci"/>, or null when it can only pick that card.</summary>
    public static string? Refusal(IReadOnlyList<RtDevice> devices, string pci)
    {
        var want = Address(pci);
        if (want == null) return "the target PCI address is invalid: no device selection can be proved";
        if (devices.Count == 0) return "no Vulkan device can ray trace (Quake II RTX needs one)";
        if (devices.Count > 1)
            return $"{devices.Count} Vulkan devices can ray trace ({string.Join(", ", devices.Select(d => $"{d.Name} at {d.Pci ?? "an unknown address"}"))}) and Quake II RTX has no device selector: it would take the first, which may not be the card under test";
        var d = devices[0];
        if (d.Pci == null) return $"the driver does not give {d.Name}'s PCI address to Vulkan, so the card the game would use cannot be confirmed";
        if (d.Vendor != 0x10de || Address(d.Pci) != want) return $"the one ray-tracing Vulkan device is {d.Name} at {d.Pci}, not the card under test at {pci}";
        return null;
    }

    private static void Check(int result, string what)
    {
        if (result != 0) throw new NotSupportedException($"{what} failed (Vulkan result {result})");
    }
}

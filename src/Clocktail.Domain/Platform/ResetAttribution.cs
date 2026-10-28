using System.Xml.Linq;
using Clocktail.Hw;

namespace Clocktail.Domain.Platform;

public enum ResetOwner { Other, Target, Ambiguous }
public sealed record TargetResetCount(int Target, int Ambiguous);

public static class ResetAttribution
{
    /// <summary>Recovery only gives back settings, so it acts unless an event is provably another vendor's driver: a doubtful
    /// event is treated as this card's, as the service always did. An unparsable event is doubtful, never ignored.</summary>
    public static bool ShouldRecover(string xml, GpuTarget target, IReadOnlyList<DisplayAdapter> present) =>
        Match(xml, target, present) != ResetOwner.Other;

    /// <summary>Never infer an adapter from enumeration order or broadcast a vendor-wide event to every GPU.</summary>
    public static ResetOwner Match(string xml, GpuTarget target, IReadOnlyList<DisplayAdapter> present)
    {
        XElement e;
        try { e = XElement.Parse(xml); } catch (System.Xml.XmlException) { return ResetOwner.Ambiguous; }
        XNamespace ns = e.Name.Namespace;
        var system = e.Element(ns + "System");
        string provider = (string?)system?.Element(ns + "Provider")?.Attribute("Name") ?? "";
        string id = (string?)system?.Element(ns + "EventID") ?? "";
        if (!((provider == "nvlddmkm" && id == "153") || (provider == "Display" && id == "4101"))) return ResetOwner.Other;
        var data = e.Element(ns + "EventData")?.Elements(ns + "Data").Select(n => n.Value).ToList() ?? [];
        if (provider == "Display")
        {
            if (data.Count > 0 && !string.IsNullOrWhiteSpace(data[0]) && !data[0].Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase))
                return ResetOwner.Other;
            if ((data.Count == 0 || string.IsNullOrWhiteSpace(data[0])) && present.Count != 1) return ResetOwner.Ambiguous;
        }
        var nvidia = present.Where(p => p.Nvidia).ToList();
        return nvidia.Count == 1 && nvidia[0].Pci == PciAddress.Parse(target.Pci) && nvidia[0].InstanceId.Equals(target.InstanceId, StringComparison.OrdinalIgnoreCase)
            ? ResetOwner.Target : ResetOwner.Ambiguous;
    }
}

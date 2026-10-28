using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    private readonly ConcurrentDictionary<string, (AppliedSettings Value, long At)> _appliedPages = new();
    private const int RowPageBytes = 96 * 1024;
    private static (AppliedRow[] Rows, int End) RowPage(AppliedSettings value, int start, int budget)
    {
        var rows = new List<AppliedRow>(); int bytes = 0, end = start;
        while (end < value.Rows.Count)
        {
            int size = JsonSerializer.SerializeToUtf8Bytes(value.Rows[end]).Length;
            if (rows.Count > 0 && bytes + size > budget) break;
            if (size > RowPageBytes) throw new InvalidDataException("One applied row exceeds the response-page bound.");
            rows.Add(value.Rows[end++]); bytes += size;
        }
        return (rows.ToArray(), end);
    }
    private BrokerResponse AppliedPage(BrokerRequest request)
    {
        if (request.Value != "3" || request.GpuId == null || request.Key == null) return Fail("Row pages require version 3, stable GPU id and cursor", BrokerCode.BadRequest);
        var parts = request.Key.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int offset) || !_appliedPages.TryGetValue(parts[0], out var stored)
            || stored.Value.GpuId != request.GpuId || stored.Value.Generation != Volatile.Read(ref _statusEpoch)
            || StatusClock.GetElapsedTime(stored.At) > TimeSpan.FromSeconds(30) || offset < 0 || offset >= stored.Value.Rows.Count)
            return Fail("Row page expired or its generation/device changed; refresh status again", BrokerCode.BadRequest);
        var page = RowPage(stored.Value, offset, RowPageBytes);
        return Ok(stored.Value with { Rows = page.Rows, TotalRows = stored.Value.Rows.Count, NextCursor = page.End < stored.Value.Rows.Count ? parts[0] + ":" + page.End : null });
    }
    private BrokerResponse AppliedResponse(TrayStatus status)
    {
        // Keep the legacy profile projection small; typed rows carry the complete bounded saved values.
        string Safe(string text, int max) => text.Length <= max ? text : $"[value exceeds {max}-character legacy projection; see typed setting row]";
        DailyProfile? Profile(DailyProfile? p) => p == null ? null : p with
        {
            Writes = p.Writes.Select(w => new ProfileWrite(Safe(w.Kind, 64), Safe(w.Key, 128), Safe(w.Value, 256))).ToArray(),
            Adopted = p.Adopted?.Select(w => new ProfileWrite(Safe(w.Kind, 64), Safe(w.Key, 128), Safe(w.Value, 256))).ToArray(), History = null
        };
        int budget = Math.Min(RowPageBytes, 600 * 1024 / Math.Max(1, status.Devices.Count));
        var devices = status.Devices.Select(device =>
        {
            var value = device.Applied!; var page = RowPage(value, 0, budget); string? cursor = null;
            if (page.End < value.Rows.Count)
            {
                if (_appliedPages.Count >= 64)
                    foreach (var old in _appliedPages.OrderBy(p => p.Value.At).Take(_appliedPages.Count - 63)) _appliedPages.TryRemove(old.Key, out _);
                string token = Guid.NewGuid().ToString("N"); _appliedPages[token] = (value, StatusClock.GetTimestamp()); cursor = token + ":" + page.End;
            }
            return device with { SavedDaily = Profile(device.SavedDaily), Applied = value with { Rows = page.Rows, TotalRows = value.Rows.Count, NextCursor = cursor } };
        }).ToArray();
        var bounded = status with { Devices = devices };
        if (JsonSerializer.SerializeToUtf8Bytes(bounded).Length > BrokerTransport.MaxMessageBytes - 1024)
            return Fail("Status metadata exceeds the bounded response; request a single stable GPU id", BrokerCode.BadRequest);
        return Ok(bounded);
    }
    private static string BoundedIdentity(string identity) => identity.Length <= 256 ? identity : "oversized/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
}

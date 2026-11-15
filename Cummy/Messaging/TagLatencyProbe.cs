#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cummy.Ipc;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace Cummy.Messaging;

/// Debug builds only: measures how long sync services (Lightless, Mare) take to deliver a SimpleHeels tag change, for
/// the sync-tag-transport go/no-go. Each client publishes `CummyProbe = <n>|<unix ms>|<echo>`: pressing Send bumps n;
/// a client that sees another's n change logs the one-way delay (clock-dependent) and echoes that n back, so the
/// sender also gets a round trip on its own clock. A jump of more than 1 in n means updates were merged.
public sealed class TagLatencyProbe : IDisposable
{
    public const string Key = "CummyProbe";

    /// Set by Plugin in debug builds, for the Debug tab.
    public static TagLatencyProbe? Instance { get; set; }

    private readonly SimpleHeelsIpc heels;
    private int n;
    private int echo;
    private readonly Dictionary<int, DateTime> pending = new();
    private readonly Dictionary<string, (int N, int Echo)> seen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextScan = DateTime.MinValue;

    public bool Enabled { get; private set; }
    public List<string> Log { get; } = new();
    public List<double> OneWayMs { get; } = new();
    public List<double> RoundTripMs { get; } = new();

    public TagLatencyProbe(SimpleHeelsIpc heels)
    {
        this.heels = heels;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Enabled) heels.Write(Key, null);
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (on) Write();
        else heels.Write(Key, null);
        seen.Clear();
    }

    public void Send()
    {
        n++;
        pending[n] = DateTime.UtcNow;
        Write();
        Add($"sent #{n}");
    }

    /// Two writes 300 ms apart, to see whether the sync service delivers both or merges them.
    public void SendBurst()
    {
        Send();
        Plugin.Framework.RunOnTick(Send, TimeSpan.FromMilliseconds(300));
    }

    public void Clear()
    {
        Log.Clear();
        OneWayMs.Clear();
        RoundTripMs.Clear();
    }

    public static string Summary(List<double> values)
    {
        if (values.Count == 0) return "no samples";
        var sorted = values.OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];
        return $"{values.Count} samples, median {median:0} ms, max {sorted[^1]:0} ms";
    }

    private void Write()
    {
        var ms = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        heels.Write(Key, $"{n}|{ms}|{echo}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Enabled || DateTime.UtcNow < nextScan || Plugin.ObjectTable.LocalPlayer is not { } me) return;
        nextScan = DateTime.UtcNow + TimeSpan.FromMilliseconds(50);
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId) continue;
            if (heels.Read(pc.ObjectIndex, Key) is not { } value) continue;
            var parts = value.Split('|');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var theirN)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var theirMs)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var theirEcho)) continue;
            var who = pc.Name.TextValue;
            var first = !seen.TryGetValue(who, out var last);
            seen[who] = (theirN, theirEcho);
            if (first)
            {
                Add($"{who}: baseline #{theirN}");
                continue;
            }
            if (theirN != last.N)
            {
                var oneWay = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() - theirMs;
                OneWayMs.Add(oneWay);
                var merged = theirN - last.N > 1 ? $" (MERGED: skipped {theirN - last.N - 1})" : "";
                Add($"{who}: got #{theirN}, one-way {oneWay} ms{merged}");
                echo = theirN;
                Write();
            }
            if (theirEcho != last.Echo && pending.Remove(theirEcho, out var at))
            {
                var rtt = (DateTime.UtcNow - at).TotalMilliseconds;
                RoundTripMs.Add(rtt);
                Add($"{who}: echoed #{theirEcho}, round trip {rtt:0} ms");
            }
        }
    }

    private void Add(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
        if (Log.Count > 200) Log.RemoveAt(0);
        Plugin.Log.Information($"[probe] {line}");
    }
}
#endif

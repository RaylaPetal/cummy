using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Cummy.State;

/// Dripping (opt-in): layered parts drip downhill onto the parts below them, one stage per drip, never past the highest
/// stage among a part's sources. Each receiving part has its own countdown, kept in memory only, so nothing drips while
/// logged out and countdowns start afresh on login and plugin load. Blocked parts are never dripped on (dams).
/// Framework thread.
public sealed class DripService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// Source -> the parts it drips onto. Hands and feet are sinks; Full Body takes no part.
    private static readonly (BodyPart From, BodyPart[] To)[] Flow =
    [
        (BodyPart.Face, [BodyPart.Chest]),
        (BodyPart.Chest, [BodyPart.Stomach, BodyPart.ArmLeft, BodyPart.ArmRight]),
        (BodyPart.ArmLeft, [BodyPart.HandLeft]),
        (BodyPart.ArmRight, [BodyPart.HandRight]),
        (BodyPart.Stomach, [BodyPart.Groin, BodyPart.LegLeft, BodyPart.LegRight]),
        (BodyPart.Groin, [BodyPart.LegLeft, BodyPart.LegRight]),
        (BodyPart.LegLeft, [BodyPart.FeetLeft]),
        (BodyPart.LegRight, [BodyPart.FeetRight]),
        (BodyPart.Back, [BodyPart.Butt]),
        (BodyPart.Butt, [BodyPart.LegLeftBack, BodyPart.LegRightBack]),
        (BodyPart.LegLeftBack, [BodyPart.FeetLeft]),
        (BodyPart.LegRightBack, [BodyPart.FeetRight]),
    ];

    /// Receiving part -> the parts that drip onto it.
    private static readonly Dictionary<BodyPart, BodyPart[]> Sources = Flow
        .SelectMany(f => f.To.Select(to => (To: to, f.From)))
        .GroupBy(e => e.To)
        .ToDictionary(g => g.Key, g => g.Select(e => e.From).ToArray());

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly Dictionary<BodyPart, DateTime> dueAt = new();
    private DateTime nextCheck = DateTime.MinValue;

    public DripService(Configuration config, LayerState layers)
    {
        this.config = config;
        this.layers = layers;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.Login += Reset;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.Login -= Reset;
        Plugin.ClientState.Logout -= OnLogout;
    }

    /// The parts with a drip coming, soonest first, and how long until it lands.
    public IReadOnlyList<(BodyPart Part, TimeSpan In)> Pending()
    {
        var now = DateTime.UtcNow;
        return dueAt.OrderBy(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Value > now ? kv.Value - now : TimeSpan.Zero))
                    .ToList();
    }

#if DEBUG
    /// Debug builds only: every pending drip lands now.
    public void DripNow()
    {
        foreach (var part in dueAt.Keys.ToList()) dueAt[part] = DateTime.MinValue;
        Check(DateTime.UtcNow);
    }
#endif

    private void Reset() => dueAt.Clear();

    private void OnLogout(int type, int code) => Reset();

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextCheck) return;
        nextCheck = now + CheckInterval;
        Check(now);
    }

    private void Check(DateTime now)
    {
        if (!config.DripEnabled || layers.Current is not { } data)
        {
            dueAt.Clear();
            return;
        }

        var interval = config.DripInterval;
        // Caps come from the stages at the start of the check, so a drip never runs on down a chain within one check
        // and a part getting wetter never disturbs the countdowns below it.
        var stages = Sources.Keys.Concat(Sources.Values.SelectMany(s => s)).Distinct().ToDictionary(p => p, layers.Stage);
        foreach (var (part, sources) in Sources)
        {
            var cap = Cap(part, sources, data.BlockMask, stages);
            if (cap == 0)
            {
                dueAt.Remove(part);
                continue;
            }
            if (!dueAt.TryGetValue(part, out var due))
            {
                dueAt[part] = now + interval;
                continue;
            }
            if (now < due) continue;

            layers.Drip(part, cap);
            if (cap > layers.Stage(part)) dueAt[part] = now + interval;
            else dueAt.Remove(part);
        }
    }

    /// The stage <paramref name="part"/> can still be dripped up to, or 0 when it isn't eligible: blocked, or no source
    /// above its own stage.
    private static int Cap(BodyPart part, BodyPart[] sources, int blockMask, Dictionary<BodyPart, int> stages)
    {
        if (part.IsBlockedBy(blockMask)) return 0;
        var cap = Math.Min(sources.Max(s => stages[s]), part.StageCount());
        return cap > stages[part] ? cap : 0;
    }
}

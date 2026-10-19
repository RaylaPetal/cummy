using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cummy;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int MinDryMinutes = 5;
    public const int MinDripMinutes = 2;
    public const int MaxDripMinutes = 30;

    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;

    /// Layered parts clean themselves this long after being layered.
    public bool DryOffEnabled { get; set; } = false;
    public int DryOffMinutes { get; set; } = 30;

    /// Dripping (opt-in): layered parts drip one stage onto the parts below them every DripMinutes (DripService).
    public bool DripEnabled { get; set; } = false;
    public int DripMinutes { get; set; } = 10;

    /// <see cref="DripMinutes"/> within its range.
    public TimeSpan DripInterval => TimeSpan.FromMinutes(Math.Clamp(DripMinutes, MinDripMinutes, MaxDripMinutes));

    /// Keyed by the local character's content id: pairings, blocks and layers belong to one character.
    public Dictionary<ulong, CharacterData> Characters { get; set; } = new();

    /// The wearer's own overlay art, for every character on this install: a full-body image (Bibo layout) split into
    /// the body parts by region, and per-part overrides keyed by BodyPart id (Face's in the face-paint layout). The
    /// images live in the config folder (OverlaySources).
    public CustomOverlayRecord CustomFullBody { get; set; } = new();

    /// Before 5: the full-face image, split into Forehead and Mouth. Migrated into Face's override; always empty after.
    public CustomOverlayRecord CustomFullFace { get; set; } = new();

    /// With the Proteus plugin loaded, body layers normally go through it as an overlay pack; this keeps Cummy's own
    /// compositor instead.
    public bool UseOwnCompositorWithProteus { get; set; } = false;
    public Dictionary<string, CustomOverlayRecord> CustomOverlays { get; set; } = new();

    /// The on-screen quick-layer button (shown while a paired partner is nearby), and whether it can be dragged.
    public bool ShowQuickLayerButton { get; set; } = true;
    public bool LockQuickLayerButton { get; set; } = false;

    /// Washing off (both opt-in): layers rinse off one stage at a time while swimming, diving or in a bath ("Clean in
    /// water"), and one stage per RainWashMinutes outdoors in the rain.
    public bool WashInWater { get; set; } = false;
    public bool WashInRain { get; set; } = false;
    public int RainWashMinutes { get; set; } = 2;

    /// Cleaning yourself by hand (buttons, self menu, /cummy clean) only while nothing cleans you on its own: with
    /// drying off or either washing-off option on, only time, water and a partner's lick clean you.
    public bool ManualCleanAllowed => !DryOffEnabled && !WashInWater && !WashInRain;

    /// How the wearer gets clean while <see cref="ManualCleanAllowed"/> is false, e.g. "Dries off over time, or washes
    /// off in water." Null while manual cleaning is allowed.
    public string? HowYouGetClean()
    {
        if (ManualCleanAllowed) return null;
        var water = (WashInWater, WashInRain) switch
        {
            (true, true) => "washes off in water or the rain",
            (true, false) => "washes off in water",
            (false, true) => "washes off in the rain",
            _ => null,
        };
        return (DryOffEnabled, water) switch
        {
            (true, null) => "Dries off over time.",
            (true, _) => $"Dries off over time, or {water}.",
            _ => $"{char.ToUpperInvariant(water![0])}{water[1..]}.",
        };
    }

    /// Moodles (opt-in): apply the wearer's saved moodle MoodleId whenever a partner's layer lands. MoodleTitle is
    /// only for naming the choice in settings when Moodles is missing or the moodle was deleted.
    public bool ApplyMoodle { get; set; } = false;
    public Guid MoodleId { get; set; } = Guid.Empty;
    public string MoodleTitle { get; set; } = string.Empty;

    /// How layers look on the wearer's body (colour, opacity, shine, skin tone), in both compositing modes.
    public LayerLook Look { get; set; } = new();

    /// The look <paramref name="part"/> is drawn with. One global look for now; per-part overrides would slot in here.
    public LayerLook ResolveLook(BodyPart part) => (Look ?? new LayerLook()).Clamped();

    public CharacterData ForCharacter(ulong contentId)
    {
        if (!Characters.TryGetValue(contentId, out var data))
            Characters[contentId] = data = new CharacterData();
        return data;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    /// Brings a loaded configuration up to date; true when it changed and needs saving. Runs on every load, not only
    /// below <see cref="CurrentVersion"/>, so state written by an older build after a downgrade is fixed up too.
    public bool Migrate()
    {
        var changed = Version < CurrentVersion;
        Version = Math.Max(Version, CurrentVersion);
        // 4: Full Body stopped being a part. A Full Body layer becomes stage 1 on the clean regional parts it would
        // have layered (keeping its time); Full Body blocks are dropped (blocking parts limits Full Body now).
        var fullBody = BodyPart.FullBody.Id();
        foreach (var data in Characters.Values)
        {
            if (data.LayeredAt.Remove(fullBody, out var at))
            {
                foreach (var part in BodyParts.Regional)
                {
                    if (part.IsBlockedBy(data.BlockMask) || data.LayeredAt.ContainsKey(part.Id())) continue;
                    data.LayeredAt[part.Id()] = at;
                    data.Stages[part.Id()] = 1;
                }
                changed = true;
            }
            changed |= data.Stages.Remove(fullBody);
            if (BodyPart.FullBody.IsBlockedBy(data.BlockMask))
            {
                data.BlockMask &= ~BodyPart.FullBody.Bit();
                changed = true;
            }
            changed |= MigrateFace(data);
        }
        changed |= MigrateFaceOverlays();
        return changed;
    }

    /// 5: Forehead and Mouth became one staged Face (Mouth's id). Mouth alone loads as stage 1, Forehead (with or
    /// without Mouth) as stage 2, at the later of their times; a Forehead block becomes a Face block. A Mouth without a
    /// stage entry can only come from an older version (they never staged it), so this is safe on every load.
    private static bool MigrateFace(CharacterData data)
    {
        var face = BodyPart.Face.Id();
        var forehead = BodyPart.Forehead.Id();
        var changed = data.Stages.Remove(forehead);
        var hadForehead = data.LayeredAt.Remove(forehead, out var foreheadAt);
        var hadFace = data.LayeredAt.TryGetValue(face, out var faceAt);
        if (hadForehead || (hadFace && !data.Stages.ContainsKey(face)))
        {
            data.LayeredAt[face] = hadFace && (!hadForehead || faceAt > foreheadAt) ? faceAt : foreheadAt;
            var stage = hadForehead ? 2 : 1;
            data.Stages[face] = Math.Max(stage, data.Stages.GetValueOrDefault(face));
            changed = true;
        }
        if (BodyPart.Forehead.IsBlockedBy(data.BlockMask))
        {
            data.BlockMask = (data.BlockMask | BodyPart.Face.Bit()) & ~BodyPart.Forehead.Bit();
            changed = true;
        }
        return changed;
    }

    /// 5: the full-face image and the Forehead override are gone; the first of full-face image, Mouth override and
    /// Forehead override becomes Face's override (its stored files moved to Face's folder), and the others are deleted.
    private bool MigrateFaceOverlays()
    {
        var face = BodyPart.Face.Id();
        var forehead = BodyPart.Forehead.Id();
        if (!CustomFullFace.HasDiffuse && !CustomFullFace.HasNormal && !CustomOverlays.ContainsKey(forehead)) return false;

        var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "overlays");
        (string Key, CustomOverlayRecord? Record)[] candidates =
        [
            ("face", CustomFullFace),
            (face, CustomOverlays.GetValueOrDefault(face)),
            (forehead, CustomOverlays.GetValueOrDefault(forehead)),
        ];
        var chosen = candidates.FirstOrDefault(c => c.Record is { HasDiffuse: true });
        try
        {
            if (chosen.Record != null && chosen.Key != face)
            {
                var target = Path.Combine(dir, face);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                var from = Path.Combine(dir, chosen.Key);
                if (Directory.Exists(from)) Directory.Move(from, target);
                CustomOverlays[face] = chosen.Record;
            }
            foreach (var key in new[] { "face", forehead })
            {
                var stale = Path.Combine(dir, key);
                if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't move the custom face images; set the Face override again if it's missing.");
        }
        CustomOverlays.Remove(forehead);
        CustomFullFace = new CustomOverlayRecord();
        return true;
    }
}

[Serializable]
public class CharacterData
{
    public List<PeerRecord> Peers { get; set; } = new();

    /// Parts this character refuses to be layered on.
    public int BlockMask { get; set; }

    /// When each layered part (by BodyPart id) was last layered or advanced a stage, UTC. Absent = clean.
    public Dictionary<string, DateTime> LayeredAt { get; set; } = new();

    /// The stage of each layered staged part (by BodyPart id). A layered part without an entry is at its last stage:
    /// that is how layers saved before stages existed load, and older versions ignore this.
    public Dictionary<string, int> Stages { get; set; } = new();

    /// The moodle Cummy put on this character and has not removed yet; Empty when none. Only this one is ever removed.
    public Guid AppliedMoodleId { get; set; } = Guid.Empty;

    public PeerRecord? FindPeer(string name, string world)
        => Peers.Find(p => p.Matches(name, world));
}

[Serializable]
public class CustomOverlayRecord
{
    public bool HasDiffuse { get; set; }
    public bool HasNormal { get; set; }

    /// File name the colour image was chosen from, for display.
    public string OriginalName { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
}

/// The wearer's layer look. The defaults reproduce the look from before the setting existed.
[Serializable]
public class LayerLook
{
    public const string White = "#FFFFFF";

    /// Multiplied into the layer art, "#RRGGBB" (the Proteus sidecar's colour format).
    public string Tint { get; set; } = White;

    /// −100…100: negative fades layers, positive makes partly covered pixels solid (Proteus' row opacity).
    public int Opacity { get; set; } = 0;

    /// 0–100 %: strength of the wet gloss.
    public int Shine { get; set; } = 100;

    /// 0–100 %: how much the wearer's skin tone shows through the layers.
    public int SkinTone { get; set; } = 0;

    [Newtonsoft.Json.JsonIgnore]
    public bool IsDefault => TintRgb() == (255, 255, 255) && Opacity == 0 && Shine == 100 && SkinTone == 0;

    /// The opacity compositing uses: the slider when negative, 0.4 × it when positive, so +100 thickens thin art
    /// without turning it into solid paint.
    [Newtonsoft.Json.JsonIgnore]
    public int EffectiveOpacity => Opacity < 0 ? Opacity : Opacity * 2 / 5;

    [Newtonsoft.Json.JsonIgnore]
    public bool IsWhite => TintRgb() == (255, 255, 255);

    /// Compact identity for composite signatures and output names.
    [Newtonsoft.Json.JsonIgnore]
    public string Key
    {
        get
        {
            var (r, g, b) = TintRgb();
            return $"{r:X2}{g:X2}{b:X2}/{Opacity}/{Shine}/{SkinTone}";
        }
    }

    /// The tint as bytes; anything unparseable counts as white.
    public (byte R, byte G, byte B) TintRgb()
    {
        var hex = (Tint ?? "").Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return (255, 255, 255);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public LayerLook Clamped()
    {
        var (r, g, b) = TintRgb();
        return new LayerLook
        {
            Tint = $"#{r:X2}{g:X2}{b:X2}",
            Opacity = Math.Clamp(Opacity, -100, 100),
            Shine = Math.Clamp(Shine, 0, 100),
            SkinTone = Math.Clamp(SkinTone, 0, 100),
        };
    }
}

[Serializable]
public class PeerRecord
{
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public DateTime PairedAtUtc { get; set; }

    /// The peer's latest announced block list, so the picker can grey those parts out.
    public int BlockMask { get; set; }

    public string Key => $"{Name}@{World}";

    public bool Matches(string name, string world)
        => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(World, world, StringComparison.OrdinalIgnoreCase);
}

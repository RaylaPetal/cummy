using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cummy.Ipc;
using Cummy.State;
using Dalamud.Game.ClientState.Objects.Enums;

namespace Cummy.Compositor;

/// The creampie drip in Cummy's mod, only while a drip runs (Dripping Pussy VFX by Tirda Frye, shipped in
/// Assets/drip). The drip ring (the Emperor's New Ring, accessory 53, variant 1) gets an IMC VFX id on the slot
/// <see cref="DripRings.Assignment"/> picks: VfxId 1 → va0001 (pussy.avfx), 2 → va0002 (ass.avfx), or 3 → va0003
/// (both.avfx, built by tools/build-drip-both.py) when both holes drip. While any drips, the ring model and the wearer's
/// own race's attach-point file (with the drip's points added to what Penumbra resolves for it) ride along.
/// <see cref="DripRings"/> puts the ring on. Framework thread.
public sealed class DripVfx : IDisposable
{
    private const string AccessoryRoot = "chara/accessory/a0053";
    private const string RingModel = AccessoryRoot + "/model/c0101a0053_rir.mdl";
    private const string Folder = "drip";

    private readonly ManagedMod mod;
    private readonly DripRings rings;
    private readonly PenumbraIpc penumbra;
    private readonly string assetsDir;
    private bool assetsCopied;
    private string? patchedRace;

    public DripVfx(ManagedMod mod, DripRings rings, PenumbraIpc penumbra)
    {
        this.mod = mod;
        this.rings = rings;
        this.penumbra = penumbra;
        assetsDir = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory!.FullName, "Assets", Folder);
        rings.AssignmentChanged += Update;
        penumbra.Ready += Update;
        Plugin.Framework.RunOnTick(Update, TimeSpan.FromSeconds(3));   // plugin reload while dripping
    }

    public void Dispose()
    {
        rings.AssignmentChanged -= Update;
        penumbra.Ready -= Update;
        // Nothing of the drip stays in the mod while the plugin is off.
        mod.SetDrip(new Dictionary<string, string>(), [], out _);
    }

    private static string Vfx(int id) => $"{AccessoryRoot}/vfx/eff/va{id:D4}.avfx";

    /// Puts exactly the assigned holes' drip into the mod (nothing when none runs).
    public void Update()
    {
        if (!mod.Ensure()) return;   // Penumbra not ready: penumbra.Ready runs this again
        var active = rings.Assignment;
        if (active.Count == 0)
        {
            if (mod.SetDrip(new Dictionary<string, string>(), [], out var removed) && removed) penumbra.RedrawPlayer();
            return;
        }
        var dir = Path.Combine(mod.Dir!, Folder);
        if (!CopyAssets(dir)) return;

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RingModel] = $"{Folder}/ring.mdl",
        };
        var manipulations = new List<object>();
        // One ring per slot (in practice one ring): its VFX id picks pussy (1), ass (2) or both at once (3).
        foreach (var group in active.GroupBy(kv => kv.Value))
        {
            var holes = group.Select(kv => kv.Key).ToHashSet();
            var (vfxId, file) = holes.Count == 2 ? (3, "both")
                              : holes.Contains(Hole.Pussy) ? (1, "pussy")
                              : (2, "ass");
            files[Vfx(vfxId)] = $"{Folder}/{file}.avfx";
            manipulations.Add(Imc(group.Key.ToString(), vfxId));
        }
        if (PatchEid(dir) is { } eid) files[eid.GamePath] = eid.File;

        // A redraw makes the ring (if worn) pick up the new VFX; only when the drip actually changed.
        if (mod.SetDrip(files, manipulations, out var changed) && changed) penumbra.RedrawPlayer();
    }

    /// The Emperor's New Ring's IMC entry for one slot, as the original mod sets it, with that slot's VFX id.
    private static object Imc(string slot, int vfxId) => new Dictionary<string, object>
    {
        ["Type"] = "Imc",
        ["Manipulation"] = new Dictionary<string, object>
        {
            ["ObjectType"] = "Accessory",
            ["PrimaryId"] = 53,
            ["Variant"] = 1,
            ["EquipSlot"] = slot,
            ["Entry"] = new Dictionary<string, object>
            {
                ["MaterialId"] = 1,
                ["DecalId"] = 0,
                ["VfxId"] = vfxId,
                ["MaterialAnimationId"] = 0,
                ["AttributeMask"] = 1023,
                ["SoundId"] = 0,
            },
        },
    };

    private bool CopyAssets(string dir)
    {
        if (assetsCopied) return true;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "pussy.avfx", "ass.avfx", "both.avfx", "ring.mdl" })
                File.Copy(Path.Combine(assetsDir, name), Path.Combine(dir, name), overwrite: true);
            assetsCopied = true;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not copy the creampie drip files into the Cummy mod.");
            return false;
        }
    }

    /// The local player's race's attach-point file with the drip's points added, built on what Penumbra resolves for it
    /// (other mods' points included; game data when that is Cummy's own copy). Null when it can't be made: the drip
    /// then has nothing to attach to, but nothing else breaks.
    private (string GamePath, string File)? PatchEid(string dir)
    {
        if (RaceCode() is not { } race) return null;
        var gamePath = $"chara/human/{race}/skeleton/base/b0001/eid_{race}b0001.eid";
        var relative = $"{Folder}/eid_{race}.eid";
        var output = Path.Combine(mod.Dir!, relative);
        if (patchedRace == race && File.Exists(output)) return (gamePath, relative);
        try
        {
            var resolved = penumbra.ResolvePlayer(gamePath);
            var own = resolved != null && Path.GetFullPath(resolved).StartsWith(Path.GetFullPath(mod.Dir!), StringComparison.OrdinalIgnoreCase);
            var bytes = resolved != null && !own && File.Exists(resolved)
                ? File.ReadAllBytes(resolved)
                : Plugin.DataManager.GetFile(gamePath)?.Data;
            if (bytes == null || EidFile.AddPoints(bytes, EidFile.DripPoints) is not { } patched)
            {
                Plugin.Log.Warning($"Could not read {gamePath}; the creampie drip has nothing to attach to.");
                return null;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(output, patched);
            patchedRace = race;
            Plugin.Log.Information($"Creampie drip attach points added to {race}'s ({(own || resolved == null ? "game" : resolved)}).");
            return (gamePath, relative);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not patch {gamePath}.");
            return null;
        }
    }

    /// The local player's model race code (c0101-c1801), from the tribe and sex in their customize data.
    private static string? RaceCode()
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) return null;
        var customize = me.Customize;
        var tribe = customize[(int)CustomizeIndex.Tribe];
        var female = customize[(int)CustomizeIndex.Gender] == 1;
        int? baseCode = tribe switch
        {
            1 => 1,          // Hyur Midlander
            2 => 3,          // Hyur Highlander
            3 or 4 => 5,     // Elezen
            7 or 8 => 7,     // Miqo'te
            9 or 10 => 9,    // Roegadyn
            5 or 6 => 11,    // Lalafell
            11 or 12 => 13,  // Au Ra
            13 or 14 => 15,  // Hrothgar
            15 or 16 => 17,  // Viera
            _ => null,
        };
        return baseCode is { } b ? $"c{b + (female ? 1 : 0):D2}01" : null;
    }
}

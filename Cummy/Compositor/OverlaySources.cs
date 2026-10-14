using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StbImageSharp;

namespace Cummy.Compositor;

public enum OverlayOrigin { Shipped, FullBody, Part }

/// What a custom image belongs to: one part's override (Face's in the face-paint layout), or the full-body image
/// (Bibo layout, split into the body parts).
public readonly record struct OverlayTarget(BodyPart? Part)
{
    public static readonly OverlayTarget FullBody = new(null);

    public static OverlayTarget Of(BodyPart part) => new(part);

    public string Key => Part?.Id() ?? "full";

    public string DisplayName => Part?.DisplayName() ?? "Full body";
}

/// Where one part's art comes from for a composite. <see cref="Normal"/> null = the part adds no relief.
/// <see cref="Stamp"/> identifies the files' content ("shipped" for the plugin's own art), so cached crops and
/// published outputs follow the art. A staged part's art is limited to the shipped coverage of <see cref="Stage"/>,
/// read from <see cref="StageMap"/> (tools/build-assets.py); null for single-stage parts.
public sealed record OverlaySource(OverlayOrigin Origin, string Diffuse, string? Normal, string Stamp,
                                   string? StageMap = null, int Stage = 0)
{
    /// Whether the art is cut by the stage map. Shipped art at its last stage is the stage map's whole coverage
    /// already; custom art is cut at every stage, so it never shows where the shipped art has no strokes.
    public bool IsStageCut => StageMap != null && Stage > 0 && (Stage < BodyParts.MaxStages || Origin != OverlayOrigin.Shipped);

    /// Identifies the art and its stage cut, for composite and pack signatures.
    public string Key => IsStageCut ? $"{Stamp}@{Stage}" : Stamp;
}

public enum CustomState { None, Custom, Missing }

/// The wearer's own overlay art: a full-body image, split into the body parts by a shipped region map, and per-part
/// overrides. Picks a part's source (override → full-body split for body parts → shipped), validates and stores
/// imported images in the config folder, and resets them. Framework thread only, except the import work it starts itself.
public sealed class OverlaySources
{
    public const int MaxSide = 8192;
    public const string DiffuseFile = "diffuse.png";
    public const string NormalFile = "normal.png";
    public const string StagesFile = "stages.png";
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    private readonly Configuration config;
    private readonly string assetsDir;
    private readonly string customDir;
    private readonly HashSet<string> busy = new();
    private readonly Dictionary<string, string> errors = new();

    /// Raised after a source is imported or reset.
    public event Action<OverlayTarget>? Changed;

    public OverlaySources(Configuration config)
    {
        this.config = config;
        assetsDir = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory!.FullName, "Assets");
        customDir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "overlays");
    }

    /// Which part each pixel of the Bibo atlas belongs to (tools/build-regions.py).
    public string RegionMapPath => Path.Combine(assetsDir, "regions.png");

    private string Dir(OverlayTarget target) => Path.Combine(customDir, target.Key);

    public CustomOverlayRecord? Record(OverlayTarget target)
        => target.Part is { } p ? config.CustomOverlays.GetValueOrDefault(p.Id())
         : config.CustomFullBody;

    public CustomState State(OverlayTarget part)
    {
        if (Record(part) is not { HasDiffuse: true }) return CustomState.None;
        return Stored(part, "diffuse") != null ? CustomState.Custom : CustomState.Missing;
    }

    public bool HasNormal(OverlayTarget part)
        => Record(part) is { HasNormal: true } && Stored(part, "normal") != null;

    public bool IsBusy(OverlayTarget part) => busy.Contains(part.Key);

    public string? Error(OverlayTarget part) => errors.GetValueOrDefault(part.Key);

    /// The art <paramref name="part"/> uses right now at <paramref name="stage"/> (1 to its stage count).
    public OverlaySource Resolve(BodyPart part, int stage)
    {
        var dir = Path.Combine(assetsDir, part.Id());
        var source = Custom(OverlayTarget.Of(part))
                     // Body parts split the full-body image (Bibo layout); Face has only its override (face-paint layout).
                     ?? (part.IsFacial() ? null : Custom(OverlayTarget.FullBody))
                     ?? new OverlaySource(OverlayOrigin.Shipped, Path.Combine(dir, DiffuseFile), Path.Combine(dir, NormalFile), "shipped");
        if (part.StageCount() == 1) return source;
        return source with { StageMap = Path.Combine(dir, StagesFile), Stage = Math.Clamp(stage, 1, part.StageCount()) };
    }

    private OverlaySource? Custom(OverlayTarget part)
    {
        if (Record(part) is not { HasDiffuse: true } record || Stored(part, "diffuse") is not { } diffuse) return null;
        // Colour and relief always come from the same source: no normal here means no relief, never another one's.
        var normal = record.HasNormal ? Stored(part, "normal") : null;
        var stamp = $"{part.Key}:{Stamp(diffuse)}";
        if (normal != null) stamp += $"+{Stamp(normal)}";
        if (part.Part != null)
            return new OverlaySource(OverlayOrigin.Part, diffuse, normal, stamp);
        // A full image's split depends on its region map too.
        stamp += $"+regions:{Stamp(RegionMapPath)}";
        return new OverlaySource(OverlayOrigin.FullBody, diffuse, normal, stamp);
    }

    private static string Stamp(string path)
        => File.Exists(path) ? $"{new FileInfo(path).Length}.{File.GetLastWriteTimeUtc(path).Ticks}" : "none";

    private string? Stored(OverlayTarget part, string kind)
    {
        var dir = Dir(part);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, kind + ".*")
                        .FirstOrDefault(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
    }

    /// Validates and stores <paramref name="sourcePath"/> as <paramref name="part"/>'s colour or normal image in the
    /// background; on success updates the config and raises <see cref="Changed"/>, otherwise records a reason.
    public void Import(OverlayTarget part, bool normal, string sourcePath)
    {
        var key = part.Key;
        if (!busy.Add(key)) return;
        errors.Remove(key);

        Task.Run(() => Store(part, normal, sourcePath)).ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            busy.Remove(key);
            var error = t.IsFaulted ? $"Couldn't import that image: {t.Exception!.InnerException?.Message}" : t.Result;
            var what = $"{part.DisplayName} {(normal ? "normal map" : "image")}";
            if (error != null)
            {
                errors[key] = error;
                Notifier.Error($"{what} not changed: {error}");
                return;
            }

            CustomOverlayRecord record;
            if (part.Part is { } p)
            {
                if (!config.CustomOverlays.TryGetValue(p.Id(), out record!))
                    config.CustomOverlays[p.Id()] = record = new CustomOverlayRecord();
            }
            else
            {
                record = config.CustomFullBody;
            }
            if (normal)
            {
                record.HasNormal = true;
            }
            else
            {
                record.HasDiffuse = true;
                record.OriginalName = Path.GetFileName(sourcePath);
            }
            record.ImportedUtc = DateTime.UtcNow;
            config.Save();
            Notifier.Info($"Custom {what.ToLowerInvariant()} set.");
            Changed?.Invoke(part);
        }));
    }

    /// Null on success, else a short reason for the wearer.
    private string? Store(OverlayTarget part, bool normal, string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (!Extensions.Contains(ext)) return "choose a PNG, JPEG, TGA or BMP image.";

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception ex)
        {
            return $"couldn't read {Path.GetFileName(source)} ({ex.Message}).";
        }

        if (ImageInfo.FromStream(new MemoryStream(bytes)) is not { } info) return "that file isn't an image Cummy can read.";
        if (info.Width > MaxSide || info.Height > MaxSide)
            return $"it is {info.Width}×{info.Height}; the limit is {MaxSide}×{MaxSide}.";

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception)
        {
            return "that file isn't an image Cummy can read.";
        }
        if (!normal && !HasCoverage(image.Data))
            return "it has no visible coverage (its alpha is zero everywhere).";

        var dir = Dir(part);
        Directory.CreateDirectory(dir);
        var kind = normal ? "normal" : "diffuse";
        var temp = Path.Combine(dir, $".import-{kind}{ext}");
        File.WriteAllBytes(temp, bytes);
        foreach (var old in Directory.EnumerateFiles(dir, kind + ".*").ToList())
            File.Delete(old);
        File.Move(temp, Path.Combine(dir, kind + ext), overwrite: true);
        return null;
    }

    private static bool HasCoverage(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0) return true;
        return false;
    }

    /// Back to the next source in line: deletes the stored images and forgets the record.
    public void Reset(OverlayTarget part)
    {
        var dir = Dir(part);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't delete {dir}");
        }
        if (part.Part is { } p) config.CustomOverlays.Remove(p.Id());
        else config.CustomFullBody = new CustomOverlayRecord();
        errors.Remove(part.Key);
        config.Save();
        Changed?.Invoke(part);
    }
}

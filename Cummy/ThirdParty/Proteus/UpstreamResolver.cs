// The upstream-resolution pattern of Proteus's CompositorService.ResolveUpstreamCore
// (https://github.com/solona-m/proteus, AGPL-3.0, by Solona), adapted for Cummy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Cummy.Ipc;

namespace Cummy.ThirdParty.Proteus;

/// What a skin texture resolves to *underneath* our own output: Penumbra's live answer when it is somebody
/// else's file, else the last such answer we saw. Compositing onto our own output would apply layers twice.
/// Another compositor (the Proteus plugin's mod) is fine underneath us - its output is the wearer's skin with their
/// tattoos, our right base. Only when it is stacked *above* us does it build on our output; then its file already
/// contains our layers and using it as our base would loop, stacking layers on every round, so it is treated like
/// our own output (<see cref="StackedAbove"/>).
public sealed class UpstreamResolver
{
    /// Penumbra mod folders of other compositors that sit above ours and build on our output.
    private static readonly string[] StackedModNames = ["Proteus"];

    private readonly PenumbraIpc penumbra;
    private readonly string ownDir;
    private readonly string[] stackedDirs;
    private readonly Dictionary<string, string> lastUpstream = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? memoryFile;

    /// <paramref name="memoryFile"/> keeps the last real upstreams across restarts: with a stacked compositor on top,
    /// Penumbra never shows us the skin underneath again after a restart.
    public UpstreamResolver(PenumbraIpc penumbra, string ownDir, string? memoryFile = null)
    {
        this.penumbra = penumbra;
        this.ownDir = AsDir(ownDir);
        this.memoryFile = memoryFile;
        var root = Path.GetDirectoryName(Path.GetFullPath(ownDir).TrimEnd(Path.DirectorySeparatorChar))!;
        stackedDirs = Array.ConvertAll(StackedModNames, name => AsDir(Path.Combine(root, name)));
        try
        {
            if (memoryFile != null && File.Exists(memoryFile)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(memoryFile)) is { } saved)
                foreach (var (path, disk) in saved) lastUpstream[path] = disk;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't read {memoryFile}");
        }
    }

    private void Remember(string gamePath, string? disk)
    {
        var changed = disk == null ? lastUpstream.Remove(gamePath)
                    : !lastUpstream.TryGetValue(gamePath, out var old) || old != disk;
        if (disk != null) lastUpstream[gamePath] = disk;
        if (!changed || memoryFile == null) return;
        try
        {
            File.WriteAllText(memoryFile, JsonSerializer.Serialize(lastUpstream));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't write {memoryFile}");
        }
    }

    private static string AsDir(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// Whether the other compositors currently sit above our mod (set by the caller per plan, from priorities).
    public bool StackedAbove { get; set; }

    /// Set when a stacked compositor's file had to be skipped and nothing real was known underneath it: the base
    /// fell back to game data. Reset by the caller per plan.
    public bool HiddenByStacked { get; set; }

    /// Set by <see cref="Resolve"/> when Penumbra still answered with our own output and a remembered upstream
    /// had to stand in. Reset by the caller per plan.
    public bool UsedFallback { get; set; }

    public bool IsOwnOutput(string? disk)
        => disk != null && Path.GetFullPath(disk).StartsWith(ownDir, StringComparison.OrdinalIgnoreCase);

    /// A file published by another compositor while it is stacked above us (then it builds on our output).
    public bool IsStackedOutput(string? disk)
    {
        if (disk == null || !StackedAbove) return false;
        var full = Path.GetFullPath(disk);
        return Array.Exists(stackedDirs, dir => full.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
    }

    /// The disk file to use as the base for <paramref name="gamePath"/>, or null for vanilla game data.
    public string? Resolve(string gamePath)
    {
        var disk = penumbra.ResolvePlayer(gamePath);
        if (disk != null && !IsOwnOutput(disk) && !IsStackedOutput(disk))
        {
            Remember(gamePath, disk);
            return disk;
        }

        // Unmodded: game data is the upstream, and any remembered mod file is stale.
        if (disk == null)
        {
            Remember(gamePath, null);
            return null;
        }

        var stacked = IsStackedOutput(disk);
        if (!stacked) UsedFallback = true;   // our own output: Penumbra may still be rebuilding (probe waits on this)
        // A remembered file from the stacked compositor (seen while it was below us) may since have been rebuilt on
        // top of our output, so it is not a safe base while that compositor is above us.
        if (lastUpstream.TryGetValue(gamePath, out var prev) && File.Exists(prev) && !IsStackedOutput(prev))
        {
            Plugin.Log.Debug($"Resolve for {gamePath} points at {(stacked ? "a stacked compositor" : "our output")} - using upstream {prev}");
            return prev;
        }

        if (stacked) HiddenByStacked = true;
        Plugin.Log.Warning($"{gamePath} resolves to {(stacked ? "a stacked compositor's" : "our own")} output and no upstream is known - falling back to game data");
        return null;
    }
}

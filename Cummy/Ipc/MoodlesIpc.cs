using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// One of the wearer's saved moodles, as Moodles lists it.
public sealed record MoodleEntry(Guid Id, uint IconId, string Path, string Title);

/// The Moodles plugin's IPC (github.com/kawaii/Moodles, IPCProcessor.cs; EzIPC names every endpoint
/// "Moodles.<Method>"): list the saved moodles, apply or remove one on the local player, and read which are on.
/// Safe when Moodles is missing: every call is guarded and a failure counts as unavailable. Framework thread.
public sealed class MoodlesIpc : IDisposable
{
    private const int MinVersion = 4;

    // MoodlesStatusInfo with Moodles' enums (StatusType, ChainTrigger) as int; Dalamud converts across plugins.
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<List<(Guid, uint, string, string)>> registeredMoodles;
    private readonly ICallGateSubscriber<List<(int, Guid, int, string, string, string, long, int, int, int, uint, Guid, int, string, string, bool)>> clientStatusInfo;
    private readonly ICallGateSubscriber<Guid, nint, object> addOrUpdateByPtr;
    private readonly ICallGateSubscriber<List<Guid>, nint, object> removeByPtr;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<object> unloading;
    private readonly ICallGateSubscriber<Guid, bool, object> statusUpdated;

    private readonly IDalamudPluginInterface pi;
    private bool? lastAvailable;
    private List<MoodleEntry> moodles = [];
    private bool moodlesStale = true;

    /// Raised when Moodles appears or goes away.
    public event Action? AvailabilityChanged;

    public MoodlesIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        version = pi.GetIpcSubscriber<int>("Moodles.Version");
        registeredMoodles = pi.GetIpcSubscriber<List<(Guid, uint, string, string)>>("Moodles.GetRegisteredMoodlesV2");
        clientStatusInfo = pi.GetIpcSubscriber<List<(int, Guid, int, string, string, string, long, int, int, int, uint, Guid, int, string, string, bool)>>(
            "Moodles.GetClientStatusManagerInfoV2");
        addOrUpdateByPtr = pi.GetIpcSubscriber<Guid, nint, object>("Moodles.AddOrUpdateMoodleByPtrV2");
        removeByPtr = pi.GetIpcSubscriber<List<Guid>, nint, object>("Moodles.RemoveMoodlesByPtrV2");
        ready = pi.GetIpcSubscriber<object>("Moodles.Ready");
        unloading = pi.GetIpcSubscriber<object>("Moodles.Unloading");
        statusUpdated = pi.GetIpcSubscriber<Guid, bool, object>("Moodles.StatusUpdated");

        ready.Subscribe(OnReady);
        unloading.Subscribe(OnUnloading);
        statusUpdated.Subscribe(OnStatusUpdated);
        pi.ActivePluginsChanged += OnPluginsChanged;
        Refresh();
    }

    public void Dispose()
    {
        pi.ActivePluginsChanged -= OnPluginsChanged;
        ready.Unsubscribe(OnReady);
        unloading.Unsubscribe(OnUnloading);
        statusUpdated.Unsubscribe(OnStatusUpdated);
    }

    /// Moodles is loaded and speaks a supported IPC version.
    public bool IsAvailable => lastAvailable == true;

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args) => Refresh();
    private void OnReady() => Plugin.Framework.RunOnFrameworkThread(() => Refresh());
    private void OnUnloading() => Plugin.Framework.RunOnFrameworkThread(() => SetAvailable(false, "Moodles unloaded."));
    private void OnStatusUpdated(Guid id, bool removed) => moodlesStale = true;

    /// Re-asks Moodles for its version; raises <see cref="AvailabilityChanged"/> when the answer changed.
    public bool Refresh()
    {
        try
        {
            var v = version.InvokeFunc();
            return SetAvailable(v >= MinVersion, v >= MinVersion
                ? $"Moodles IPC {v} available."
                : $"Moodles IPC {v} is not supported (need {MinVersion}+) - no moodles are applied.");
        }
        catch (Exception)
        {
            return SetAvailable(false, null);   // not loaded (no provider registered)
        }
    }

    private bool SetAvailable(bool available, string? message)
    {
        if (lastAvailable == available) return available;
        if (message != null) Plugin.Log.Information(message);
        moodlesStale = true;
        var first = lastAvailable == null;
        lastAvailable = available;
        if (!first) AvailabilityChanged?.Invoke();
        return available;
    }

    /// The wearer's saved moodles, sorted by folder path; empty while Moodles is unavailable. Re-read after Moodles
    /// reports a change, or when <paramref name="reload"/>.
    public IReadOnlyList<MoodleEntry> Moodles(bool reload = false)
    {
        if (!IsAvailable) return [];
        if (!moodlesStale && !reload) return moodles;
        moodlesStale = false;
        try
        {
            moodles = registeredMoodles.InvokeFunc()
                .Select(m => new MoodleEntry(m.Item1, m.Item2, m.Item3 ?? "", m.Item4 ?? ""))
                .OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not list moodles.");
            moodles = [];
        }
        return moodles;
    }

    /// Whether the wearer still has a saved moodle <paramref name="id"/>.
    public bool Exists(Guid id) => Moodles().Any(m => m.Id == id);

    /// Applies (or re-applies) saved moodle <paramref name="id"/> to the local player. False when it could not be sent.
    public bool Apply(Guid id)
    {
        if (!IsAvailable || Plugin.ObjectTable.LocalPlayer is not { } me) return false;
        try
        {
            addOrUpdateByPtr.InvokeAction(id, me.Address);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not apply moodle {id}.");
            return false;
        }
    }

    /// Removes moodle <paramref name="id"/> from the local player. False when it could not be sent.
    public bool Remove(Guid id)
    {
        if (!IsAvailable || Plugin.ObjectTable.LocalPlayer is not { } me) return false;
        try
        {
            removeByPtr.InvokeAction([id], me.Address);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not remove moodle {id}.");
            return false;
        }
    }

    /// The moodles currently on the local player; null when that could not be read.
    public IReadOnlySet<Guid>? ActiveIds()
    {
        if (!IsAvailable) return null;
        try
        {
            return clientStatusInfo.InvokeFunc().Select(s => s.Item2).ToHashSet();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read the active moodles.");
            return null;
        }
    }
}

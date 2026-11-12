using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// SimpleHeels' tag IPC (API 2.x), as Corruption uses it: tags on object 0 (the local player) are reported to sync
/// services such as Lightless and Mare, which deliver them to players they sync with; other players' tags are read
/// locally. Missing SimpleHeels reads as unavailable. Framework thread.
public sealed class SimpleHeelsIpc : IDisposable
{
    private const int SupportedMajorVersion = 2;
    private const int LocalPlayer = 0;

    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<int, string, string, object?> setTag;
    private readonly ICallGateSubscriber<int, string, string?> getTag;
    private readonly ICallGateSubscriber<int, string, object?> removeTag;

    public bool IsAvailable { get; private set; }

    /// SimpleHeels appeared (or came back), so tags have to be written again.
    public event Action? BecameAvailable;

    public SimpleHeelsIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        setTag = pi.GetIpcSubscriber<int, string, string, object?>("SimpleHeels.SetTag");
        getTag = pi.GetIpcSubscriber<int, string, string?>("SimpleHeels.GetTag");
        removeTag = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RemoveTag");
        pi.ActivePluginsChanged += OnPluginsChanged;
        Refresh();
    }

    public void Dispose() => pi.ActivePluginsChanged -= OnPluginsChanged;

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args) => Plugin.Framework.RunOnFrameworkThread(Refresh);

    private void Refresh()
    {
        var was = IsAvailable;
        try
        {
            var (major, minor) = apiVersion.InvokeFunc();
            IsAvailable = major == SupportedMajorVersion;
            if (!was) Plugin.Log.Information(IsAvailable
                ? $"SimpleHeels IPC available (v{major}.{minor}) - Cummy users nearby can find each other."
                : $"SimpleHeels IPC {major}.{minor} is not supported (need {SupportedMajorVersion}.x).");
        }
        catch
        {
            IsAvailable = false;
        }
        if (IsAvailable && !was) BecameAvailable?.Invoke();
    }

    /// Writes the local player's <paramref name="key"/> tag, or removes it when <paramref name="value"/> is null. False
    /// when it couldn't be written.
    public bool Write(string key, string? value)
    {
        if (!IsAvailable) return false;
        try
        {
            if (value is null) removeTag.InvokeAction(LocalPlayer, key);
            else setTag.InvokeAction(LocalPlayer, key, value);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"SimpleHeels tag write failed: {ex.Message}");
            return false;
        }
    }

    /// Another player's (or your own, at index 0) <paramref name="key"/> tag, or null when they have none.
    public string? Read(int objectIndex, string key)
    {
        if (!IsAvailable) return null;
        try
        {
            return getTag.InvokeFunc(objectIndex, key);
        }
        catch
        {
            return null;
        }
    }
}

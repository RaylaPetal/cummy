using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Cummy.Proteus;

/// The Proteus plugin's IPC (github.com/solona-m/proteus, Interop/IpcProvider.cs): its API version, and asking it
/// to recomposite. Safe when Proteus is missing. Framework thread.
public sealed class ProteusIpc : IDisposable
{
    private const int SupportedMajor = 1;

    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<object> recomposite;
    private bool? lastAvailable;

    /// Raised when Proteus appears or goes away (re-checked whenever Dalamud's plugin list changes).
    public event Action? AvailabilityChanged;

    public ProteusIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("Proteus.ApiVersion");
        recomposite = pi.GetIpcSubscriber<object>("Proteus.Recomposite");
        pi.ActivePluginsChanged += OnPluginsChanged;
        Refresh();
    }

    public void Dispose() => pi.ActivePluginsChanged -= OnPluginsChanged;

    /// Proteus is loaded and speaks a supported API version.
    public bool IsAvailable => lastAvailable == true;

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args) => Refresh();

    /// Re-asks Proteus for its version; raises <see cref="AvailabilityChanged"/> when the answer changed.
    public bool Refresh()
    {
        bool available;
        try
        {
            var (major, minor) = apiVersion.InvokeFunc();
            available = major == SupportedMajor;
            if (lastAvailable != available)
                Plugin.Log.Information(available
                    ? $"Proteus API {major}.{minor} available - body layers go through Proteus."
                    : $"Proteus API {major}.{minor} is not supported (need {SupportedMajor}.x) - Cummy composites the body itself.");
        }
        catch (Exception)
        {
            available = false;   // not loaded (no provider registered)
        }
        if (lastAvailable == available) return available;
        var first = lastAvailable == null;
        lastAvailable = available;
        if (!first) AvailabilityChanged?.Invoke();
        return available;
    }

    /// Asks Proteus to recomposite (it re-reads overlay packs and their selected options).
    public void Recomposite()
    {
        if (!IsAvailable) return;
        try
        {
            recomposite.InvokeAction();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Proteus.Recomposite failed");
            Refresh();
        }
    }
}

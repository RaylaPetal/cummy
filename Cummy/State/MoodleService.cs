using System;
using Cummy.Ipc;
using Cummy.Pairing;

namespace Cummy.State;

/// Puts the wearer's chosen moodle on them when a partner's layer lands, and takes it off once they are clean. Only
/// the moodle Cummy applied (CharacterData.AppliedMoodleId) is ever removed. Logging in never applies anything; it
/// only removes a leftover once the wearer turns out to be clean.
public sealed class MoodleService : IDisposable
{
    private static readonly TimeSpan ApplyCheckDelay = TimeSpan.FromSeconds(2);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly PairingService pairing;
    private readonly MoodlesIpc moodles;

    public MoodleService(Configuration config, LayerState layers, PairingService pairing, MoodlesIpc moodles)
    {
        this.config = config;
        this.layers = layers;
        this.pairing = pairing;
        this.moodles = moodles;
        pairing.LayerAccepted += OnLayerAccepted;
        layers.Changed += RemoveIfClean;
        moodles.AvailabilityChanged += RemoveIfClean;

        // Plugin reload while logged in: LayerState only re-raises Changed on login.
        if (Plugin.ClientState.IsLoggedIn)
            Plugin.Framework.RunOnTick(RemoveIfClean, TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        pairing.LayerAccepted -= OnLayerAccepted;
        layers.Changed -= RemoveIfClean;
        moodles.AvailabilityChanged -= RemoveIfClean;
    }

    /// Set when the last apply did not show up on the wearer, which is what Moodles does while its "Allow remote
    /// apply" setting is off. Cleared by the next apply that works.
    public bool LastApplyIgnored { get; private set; }

    private void OnLayerAccepted(BodyPart part)
    {
        var id = config.MoodleId;
        if (!config.ApplyMoodle || id == Guid.Empty || !moodles.IsAvailable || !moodles.Exists(id)) return;
        if (layers.Current is not { } data) return;

        // The choice changed since the last apply: take the old one off so it is not left behind untracked.
        if (data.AppliedMoodleId != Guid.Empty && data.AppliedMoodleId != id)
            moodles.Remove(data.AppliedMoodleId);

        if (!moodles.Apply(id)) return;
        data.AppliedMoodleId = id;
        config.Save();
        Plugin.Framework.RunOnTick(() => CheckApplied(id), ApplyCheckDelay);
    }

    private void CheckApplied(Guid id)
    {
        if (moodles.ActiveIds() is not { } active) return;
        LastApplyIgnored = !active.Contains(id);
        if (!LastApplyIgnored) return;

        Plugin.Log.Information("Moodles ignored the moodle Cummy applied; its \"Allow remote apply\" setting is probably off.");
        if (layers.Current is { } data && data.AppliedMoodleId == id)
        {
            data.AppliedMoodleId = Guid.Empty;
            config.Save();
        }
    }

    /// Takes off the moodle Cummy applied once nothing is layered. While Moodles is unavailable the moodle stays
    /// recorded and this runs again when Moodles comes back.
    private void RemoveIfClean()
    {
        if (layers.Current is not { } data || data.AppliedMoodleId == Guid.Empty || !layers.IsEmpty) return;
        if (!moodles.Remove(data.AppliedMoodleId)) return;
        data.AppliedMoodleId = Guid.Empty;
        config.Save();
    }
}

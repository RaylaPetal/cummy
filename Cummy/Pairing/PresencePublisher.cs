using System;
using Cummy.Ipc;
using Cummy.State;

namespace Cummy.Pairing;

/// Publishes the logged-in character's <see cref="PresenceTag"/> through SimpleHeels, so synced Cummy users nearby see
/// this player in their droplet (and know its mode and blocks) without any tell. Written only when the value changes
/// (login, mode, blacklist/partners, blocks, SimpleHeels appearing); removed on logout and when Cummy unloads.
/// Framework thread.
public sealed class PresencePublisher : IDisposable
{
    private readonly LayerState layers;
    private readonly PairingService pairing;
    private readonly SimpleHeelsIpc heels;
    private string? written;

    public PresencePublisher(LayerState layers, PairingService pairing, SimpleHeelsIpc heels)
    {
        this.layers = layers;
        this.pairing = pairing;
        this.heels = heels;
        layers.CharacterChanged += OnCharacterChanged;
        pairing.AccessChanged += Publish;
        pairing.BlocksChanged += Publish;
        heels.BecameAvailable += Republish;
        if (Plugin.ClientState.IsLoggedIn) Plugin.Framework.RunOnTick(Publish, TimeSpan.FromSeconds(3));   // plugin reload
    }

    public void Dispose()
    {
        layers.CharacterChanged -= OnCharacterChanged;
        pairing.AccessChanged -= Publish;
        pairing.BlocksChanged -= Publish;
        heels.BecameAvailable -= Republish;
        Remove();
    }

    /// The tag this character publishes now, or null while logged out.
    public string? Current
        => layers.Current is { } me ? new PresenceTag(me.PublicLayering, me.BlockMask).Format() : null;

    private void OnCharacterChanged(bool loggedIn)
    {
        if (loggedIn) Publish();
        else Remove();
    }

    private void Republish()
    {
        written = null;
        Publish();
    }

    private void Publish()
    {
        if (Current is not { } value || value == written) return;
        if (!heels.Write(PresenceTag.Key, value)) return;
        written = value;
        Plugin.Log.Debug($"Presence tag: {value}");
    }

    private void Remove()
    {
        if (written == null) return;
        heels.Write(PresenceTag.Key, null);
        written = null;
    }
}

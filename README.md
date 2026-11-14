# Cummy

A Dalamud plugin for FINAL FANTASY XIV that brings Second Life INM-style cum layering to paired partners, or anyone nearby in Public mode. **18+.**

- **Pair** with someone nearby: click the droplet button, choose them and click *Pair*. They accept the invite and you're paired; either of you can *Unpair* at any time from `/cummy`.
- **Layer** or **lick** a partner nearby from the same droplet button: choose them and click a body part. Under the parts, **Creampie** their pussy or ass: it drips for a while (the Emperor's New Ring is put on through Glamourer to carry the drip effect, which needs an IVCS skeleton) and layers their groin or butt. Washing in water or a lick there ends it; block either in settings.
- **Who's nearby:** the droplet lists your partners, plus other Cummy users near you. Cummy finds them through a small SimpleHeels tag that sync services (Lightless, Mare...) carry, so nobody without Cummy ever gets a message. You need SimpleHeels and to be synced with them; without SimpleHeels only partners are listed. The tag shares your Pairing/Public mode and blocks with players you're synced with.
- **Pairing or Public** (in `/cummy settings`): in Pairing mode, the default, only partners can layer you. In Public mode anyone with Cummy nearby can layer or lick you without pairing, and you can layer anyone nearby. Your blocks and range still apply either way.
- **Blacklist** someone (hold Ctrl in the droplet dropdown, or type `Name@World` in settings) and they can never layer, lick or invite you until you remove them. Blacklisting a partner unpairs them.
- Layers are applied **on the wearer's own client**, on top of the skin they are actually wearing, and published through one Cummy mod in Penumbra, listed as `Cummy [Name Surname @ World]` after the logged-in character, so sync plugins show them to everyone. With the Proteus plugin, the same mod carries a "Cummy layers" option group that Proteus composites your body layers from. Older versions' separate "Cummy Layers" mod is removed automatically.
- **Clean** yourself in `/cummy`, part by part or everything, or with `/cummy clean`. Optional drying off (off by default) cleans layered parts after a set time.
- **Custom overlays** (in `/cummy settings`): choose one full-body image painted on the Bibo UV layout (transparent except the art, optionally with a normal map) and Cummy splits it into the body parts for you; per-part overrides replace single parts. They change how layers look on *your* body (which everyone sees); they aren't sent to partners.
- `/cummy` opens the main window: your layers (clean one part or everything) and your partners (layer, or hold Ctrl to unpair). `/cummy clean` cleans everything, `/cummy clean chest` one part.
- Block body parts you don't want layered in `/cummy settings`, along with drying off and custom overlays.

Requirements: Penumbra, and a **Bibo** body (other bodies are refused for now). Optional: Moodles, to have one of your moodles applied when you're layered.

## Installing

In game, open `/xlsettings` → *Experimental*, add this URL under *Custom Plugin Repositories*, save, then install **Cummy** from `/xlplugins`:

```
https://raw.githubusercontent.com/RaylaPetal/cummy/main/repo.json
```

## Releasing

Bump `<Version>` in `Cummy/Cummy.csproj` (4 parts, e.g. `1.0.1.0`), commit it to `main`, then push a matching tag (`v1.0.1.0`). The release workflow builds it, publishes a GitHub release with `Cummy-<version>.zip`, points `repo.json` on `main` at it and prunes old releases. A tag ending in `-testing` publishes a testing build only.

## How it talks to your partner

Commands travel as short `/tell`s prefixed `cmy1`, sent only when you click something. Incoming ones are hidden from your chat log, and only accepted from characters you have paired with. There is no server.

## Building

```
git clone --recursive <this repo>
dotnet build Cummy.slnx
```

Requires the .NET 10 SDK and Dalamud installed at least once. Shipped overlays are generated from `Textures/Finished` with `tools/build-assets.py` (see that file; it needs Python with Pillow, numpy and scipy), including each staged part's stage map, which it builds from the part's own art. The body-part region map that splits a wearer's full-body overlay (`Cummy/Assets/regions.png`) is built from those overlays with `tools/build-regions.py`: `generate`, correct `Textures/Regions/regions.png` by hand, then `check` (see that file). Rerun `check` whenever the shipped overlays change. The staged Face overlay (`Cummy/Assets/mo`) is built from Coomface v1.0's face paint 9 (its Chin, Chin+Forehead and Face options are stages 1-3) with `tools/build-face-assets.py`: `import "<Coomface v1.0>/options (face paint 9)"` once to decode the options into `Textures/Finished/Face`, then run it without arguments (see that file). The creampie drip that covers both holes (`Cummy/Assets/drip/both.avfx`) is built from the shipped `pussy.avfx` and `ass.avfx` with `tools/build-drip-both.py` (see that file).

## Plugin API

Other plugins can build on Cummy through Dalamud IPC: read the wearer's layers and settings, react to every change, layer, lick and clean under the wearer's own rules, message the same plugin on a partner's client, and paint their own overlays through Cummy's compositor. The API is at version 6.

**Client.** `Cummy.Api/CummyIpc.cs` is a single file to copy into your plugin. It has typed wrappers for every call gate below, events, and the outcome and cause strings as constants (`CummyStrings`). Cummy's own debug build drives its settings debug panel through that file, so it always matches the gates.

**Lifecycle.** Subscribe to `Cummy.Ready` and `Cummy.Disposing` once. `Ready` fires whenever Cummy (re)loads, so re-set anything you placed on Cummy (overlays) then. Event subscriptions survive Cummy reloads.

**Threads.** Call everything on the framework thread (`Framework.Update`, `RunOnFrameworkThread`, or your UI's `Draw`). From another thread, outcome calls return `wrong-thread` and reads return their empty value. Only `Cummy.ApiVersion` answers from any thread. No exception crosses into your plugin: an internal failure returns `error` (or an empty value) and is logged by Cummy.

**Consent.** Nothing in the API changes the wearer's blocks, Pairing/Public mode, blacklist, partners, cleaning settings or plugin toggles. Every action goes through the same checks as Cummy's own UI.

Part IDs are the wire IDs in `Cummy/BodyPart.cs` (`GetParts` lists them): `mo` Face, `ch` Chest, `bk` Back, `st` Stomach, `bt` Butt, `gr` Groin, `al`/`ar` arms, `hl`/`hr` hands, `ll`/`lr` legs, `bl`/`br` backs of the legs, `fl`/`fr` feet, and `fb` Full Body. Full Body is a target, not a stored part: it layers every unblocked part one stage.

### Lifecycle and reads

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.ApiVersion` | `() -> int` | The API version, currently 6. Any thread |
| `Cummy.Ready` | event `()` | Cummy's endpoints are registered (on every load) |
| `Cummy.Disposing` | event `()` | Cummy is about to unregister its endpoints |
| `Cummy.GetParts` | `() -> List<(string id, string name, int stages, bool facial, bool isTarget)>` | v5. Every layer target in display order, Full Body last (`isTarget`, 0 stages) |
| `Cummy.GetStages` | `() -> Dictionary<string, int>` | v5. Each layered part's stage by part ID. Clean parts are left out; empty when logged out |
| `Cummy.GetStage` | `(string partId) -> int` | v5. One part's stage: 0 when clean, -1 for an ID that isn't a stored part (`fb` included) |
| `Cummy.GetLayerCount` | `() -> int` | v2. Stages worn, summed over layered parts |
| `Cummy.GetBlockedParts` | `() -> List<string>` | v5. The wearer's blocked part IDs |
| `Cummy.GetAccessMode` | `() -> string` | v5. `pairing` or `public` |
| `Cummy.IsBlacklisted` | `(string name, string world) -> bool` | v5. Whether that player is on the wearer's blacklist (the list itself isn't exposed) |
| `Cummy.GetPartners` | `() -> List<(string name, string world, List<string> blocked)>` | v5. Paired partners and the parts each is last known to block |
| `Cummy.GetCleaning` | `() -> (bool dryOff, int dryMinutes, bool washWater, bool washRain, bool drip, bool manualClean)` | v5. Cleaning settings, and whether cleaning by hand (and `CleanSelf`) is allowed right now |
| `Cummy.GetNearby` | `() -> List<(string name, string world, bool partner)>` | v5. The players the droplet lists within layer range: partners, and other Cummy users found through their SimpleHeels presence tag (only partners without SimpleHeels); never blacklisted ones. `CanLayer` says whether one can be layered |
| `Cummy.CanLayer` | `(string name, string world, string partId) -> string` | v5. What `SendLayer` would answer, with `ok` in place of `sent`. Sends nothing |

### Events

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.OnPartChanged` | event `(string partId, int oldStage, int newStage, string cause, string sourceName, string sourceWorld)` | v5. One per part whose stage changed. Causes: `layer`, `full-body`, `drip`, `rinse`, `lick`, `kiss`, `dry`, `clean` (by hand), `plugin` (`CleanSelf`/`SetStage`). The source is the player responsible, or empty |
| `Cummy.OnLayersChanged` | event `(int count)` | v2. The new `GetLayerCount` after any change, and on login |
| `Cummy.OnLayerReceived` | event `(string partId, string sourceName, string sourceWorld)` | A partner's layer (or `LayerSelf`) changed a stage. Not raised for blocked or full parts, or for drips |
| `Cummy.OnCharacterChanged` | event `(bool loggedIn)` | v5. After login once the saved state is loaded, and on logout |
| `Cummy.OnBlocksChanged` | event `(List<string> blocked)` | v5. The wearer's blocks changed |
| `Cummy.OnAccessChanged` | event `()` | v5. A partner was added or removed, a partner's known blocks changed, the mode changed, or the blacklist changed |
| `Cummy.OnInviteReceived` | event `(string name, string world)` | v5. A pairing invite arrived (not raised for blacklisted senders) |
| `Cummy.OnLickResult` | event `(string name, string world, string partId, string result)` | v5. The answer to a lick the wearer sent: `licked`, or `nothing` once it timed out |

### Actions

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.SendLayer` | `(string name, string world, string partId) -> string` | Layers a paired partner with the normal layer tell. In Public mode (v4) it also layers any player in range who isn't paired; their blocks are unknown, so `blocked` only applies to partners. Returns `sent`, `not-paired`, `blacklisted`, `out-of-range`, `blocked`, `queue-full` or `unknown-part`, and sends nothing unless `sent`. Their Cummy still applies its own rules when the tell arrives |
| `Cummy.LayerSelf` | `(string partId, string sourceName, string sourceWorld) -> string` | v3. Layers the wearer as if that player had, with no pairing and no tell, under the wearer's own blocks and stages. For plugins that decide the layer themselves, such as Corruption on a scene partner's climax. Returns `layered`, `advanced`, `full`, `blocked`, `blacklisted`, `unknown-part` or `no-character` |
| `Cummy.SendLick` | `(string name, string world, string partId) -> string` | v5. Licks a part off a partner (or a stranger in Public mode) within lick range. Returns `sent`, `not-paired`, `blacklisted`, `out-of-range`, `queue-full` or `unknown-part` (Full Body included). The result arrives as `OnLickResult` |
| `Cummy.Invite` | `(string name, string world) -> string` | v5. Sends a pairing invite. Returns `sent`, `already-paired`, `pending`, `blacklisted`, `queue-full` or `no-character`. Accepting, unpairing and blacklisting stay with the wearer |
| `Cummy.CleanSelf` | `(string partId) -> string` | v5. Cleans one part, or every part for `all` (v6: `all` also ends both creampies; `pussy`/`ass` end one). Returns `cleaned`, `already-clean`, `locked` (drying off or washing off is on), `unknown-part` or `no-character` |
| `Cummy.RinseSelf` | `() -> string` | v5. Every layered part one stage down, as washing off does. Allowed when cleaning by hand is, or while washing in water is on (your plugin is the water). Returns `rinsed`, `already-clean`, `locked` or `no-character` |
| `Cummy.SetStage` | `(string partId, int stage, string sourceName, string sourceWorld) -> string` | v5. Sets a part to a stage from 0 to its stage count. Raising follows layering rules (`blocked`, `blacklisted` for a blacklisted source); lowering follows cleaning rules (`locked`). Returns `set`, `unchanged`, `blocked`, `blacklisted`, `locked`, `invalid-stage`, `unknown-part` or `no-character` |

### Creampies

A creampie makes the wearer's pussy or ass drip for a while (the wearer's setting, 10 minutes by default; another one restarts it) and layers Groin or Butt one step. Washing in water, a partner licking that part, or cleaning ends it. Holes are `pussy` and `ass`.

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.SendCreampie` | `(string name, string world, string hole) -> string` | v6. Creampies a partner (or, in Public mode, anyone in range) with the normal tell; their Cummy applies its own rules. Returns `sent`, `not-paired`, `blacklisted`, `out-of-range`, `blocked`, `queue-full` or `unknown-hole` |
| `Cummy.CreampieSelf` | `(string hole, string sourceName, string sourceWorld) -> string` | v6. Creampies the wearer as if that player had, with no pairing, tell or range, under the wearer's blocks and blacklist. Returns `creampied`, `refreshed`, `blocked`, `blacklisted`, `unknown-hole` or `no-character` |
| `Cummy.GetCreampies` | `() -> Dictionary<string, int>` | v6. Seconds left per dripping hole |
| `Cummy.OnCreampieChanged` | event `(string hole, bool active, int secondsLeft, string cause, string sourceName, string sourceWorld)` | v6. A drip started or restarted (`creampie`) or ended (`timer`, `wash`, `lick`, `clean`, `plugin`). The accompanying Groin/Butt step shows in `OnPartChanged` with cause `creampie` |

### Messaging

Plugins can talk to the same plugin on a paired partner's game. Messages travel as hidden `cmy1 ext <channel> <payload>` tells, so older Cummy versions hide them too and ignore them.

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.SendMessage` | `(string name, string world, string channel, string payload) -> string` | v5. Queues one message to a paired partner. The channel is 1-16 characters of `a-z0-9` (pick one per plugin). The payload is space-separated `a-z0-9` tokens, at most 200 characters (encode anything else, e.g. as base32). Only partners: Public mode doesn't apply. Messages share Cummy's 1.1 s tell spacing and never take more than 2 places in the send queue. Returns `sent`, `not-paired`, `blacklisted`, `invalid`, `disabled` (the wearer turned the relay off), `queue-full` or `no-character` |
| `Cummy.OnMessage` | event `(string channel, string payload, string senderName, string senderWorld)` | v5. A paired, not-blacklisted partner's plugin sent a message. The sender is the one the game reports, never one read from the text. Filter on your channel |

### Overlays

Plugins can show their own art on the wearer (mud, paint, marks...) through Cummy's compositor, so they never fight it for the skin textures. Overlays are composited above Cummy's own layers in the order they were first set, untinted by the wearer's layer look, and also on parts Cummy hasn't layered. Body overlays work both with Cummy's own compositor and in Proteus mode (as extra options of the Cummy mod's "Cummy layers" group); Face overlays always go through Cummy's own compositor. Cleaning, rinsing and drying off never touch them.

Images use the same layouts as Cummy's custom overlays: a body image is a whole Bibo body texture (`full` is split into the body parts by Cummy's region map; a part target uses its image whole), and a Face image is in the face-paint layout. PNG, JPEG, TGA or BMP, at most 8192×8192.

Overlays are kept in memory only. They go away when the plugin that owns them unloads, when the character logs out, and when Cummy unloads, so set them again on `Cummy.Ready`. At most 16 show at once. The wearer sees the list in settings and can remove any of them or turn them all off.

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.SetOverlay` | `(string owner, string key, string target, string diffusePath, string normalPath, int opacity) -> string` | v5. Shows or replaces overlay `key` (1-32 characters) of plugin `owner` (your plugin's internal name; it must be loaded). `target` is a part ID or `full`. The paths are absolute; `normalPath` may be `""` for no relief. Opacity is 1-100 and applies to colour, relief and gloss. Returns `accepted`, `invalid`, `unknown-part`, `too-many` or `disabled`; the images are checked in the background and a problem arrives as `OnOverlayFailed`. Rewriting the file and calling again shows the new art |
| `Cummy.RemoveOverlay` | `(string owner, string key) -> string` | v5. Returns `removed` or `unchanged` |
| `Cummy.ClearOverlays` | `(string owner) -> string` | v5. Removes all of `owner`'s overlays. Returns `removed` or `unchanged` |
| `Cummy.GetOverlays` | `() -> List<(string owner, string key, string target)>` | v5. The overlays showing, bottom first |
| `Cummy.OnOverlayFailed` | event `(string owner, string key, string reason)` | v5. The images couldn't be used (unreadable, too large, or no visible coverage); the overlay was dropped |
| `Cummy.OnOverlayRemoved` | event `(string owner, string key)` | v5. Removed by the wearer, by a call, or because its owner unloaded or the character logged out |

Debug builds also register `Cummy.DebugLayerSelf` `(string partId) -> string`, which layers you as if `Debug Partner@Debug` had sent the tell, for testing against.

### Changes in version 6

- Sends to strangers use their presence tag: `SendLayer`, `SendLick`, `SendCreampie` and `CanLayer` answer `not-paired` for a stranger in Pairing mode, and `blocked` for what their tag blocks. `GetNearby` lists strangers only when they carry the tag.

- Creampies: `SendCreampie`, `CreampieSelf`, `GetCreampies`, `OnCreampieChanged`; `CleanSelf` takes `pussy`/`ass`, and `all` ends creampies too; `OnPartChanged` has the cause `creampie`.

### Changes in version 5

- `LayerSelf` accepts `fb` (Full Body). It used to return `unknown-part` for it.
- Every call returns `wrong-thread` off the framework thread and `error` on an internal failure.

## Help and support

- **Bugs and questions:** [open an issue on GitHub](https://github.com/RaylaPetal/cummy/issues), or message `raylapetal` on Discord.
- **Support me:** [Ko-fi](https://ko-fi.com/raylapetal) · [Patreon](https://www.patreon.com/cw/RaylaPetal)

In game, the **?** button in the main window's title bar has the issue and Discord links, and the Ko-fi and Patreon buttons sit at the top right of the window.

## Credits and licence

Cummy is licensed under the **GNU AGPL v3** (see `LICENSE.md`).

- The overlay compositor is ported from [Proteus](https://github.com/solona-m/proteus) by Solona (AGPL-3.0): upstream skin resolution, `.mtrl`/`.tex` handling and overlay blending. Ported files live in `Cummy/ThirdParty/Proteus/` and keep their attribution.
- The tell-based pairing and messaging pattern follows Oathbound.
- The default facial overlays are converted from [HLF] Cum Blush by HighLowFlow.
- The creampie dripping VFX and ring model are from Dripping Pussy VFX by Tirda Frye.
- Scaffolded from [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin).
- Penumbra IPC via [Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api).

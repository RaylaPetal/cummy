# Cummy

A Dalamud plugin for FINAL FANTASY XIV that brings Second Life INM-style cum layering to paired partners. **18+.**

- **Pair** with someone by right-clicking them and choosing *Pair*. They accept the invite and you're paired; either of you can *Unpair* at any time.
- **Layer** a paired partner by right-clicking them, choosing *Layer...* and clicking a body part. Each part is either layered or clean.
- Layers are applied **on the wearer's own client**, on top of the skin they are actually wearing, and published through a "Cummy" mod in Penumbra (visible in its mod list), so sync plugins show them to everyone.
- **Clean** yourself by right-clicking your own character, part by part or everything. Optional drying off (off by default) cleans layered parts after a set time.
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

Requires the .NET 10 SDK and Dalamud installed at least once. Shipped overlays are generated from `Textures/Finished` with `tools/build-assets.py` (see that file), including each staged part's stage map from `Textures/Finished/Staged Parts`. The body-part region map that splits a wearer's full-body overlay (`Cummy/Assets/regions.png`) is built from those overlays with `tools/build-regions.py`: `generate`, correct `Textures/Regions/regions.png` by hand, then `check` (see that file). Rerun `check` whenever the shipped overlays change. The staged Face overlay (`Cummy/Assets/mo`) is built from Coomface v1.0's face paint 9 (its Chin, Chin+Forehead and Face options are stages 1-3) with `tools/build-face-assets.py`: `import "<Coomface v1.0>/options (face paint 9)"` once to decode the options into `Textures/Finished/Face`, then run it without arguments (see that file).

## Plugin API

Other plugins can use Cummy through Dalamud IPC, on the framework thread:

| IPC | Signature | Purpose |
| --- | --- | --- |
| `Cummy.ApiVersion` | `() -> int` | The API version, currently 2 |
| `Cummy.SendLayer` | `(string name, string world, string partId) -> string` | Layers a paired partner with the normal layer tell. Returns `sent`, `not-paired`, `out-of-range`, `blocked`, `queue-full` or `unknown-part`, and sends nothing unless `sent` |
| `Cummy.OnLayerReceived` | event `(string partId, string sourceName, string sourceWorld)` | A partner's layer changed a stage on you. Not raised for blocked or full parts, or for drips |
| `Cummy.GetLayerCount` | `() -> int` | Version 2. The layer stages you're wearing, summed over layered parts. 0 when clean or logged out |
| `Cummy.OnLayersChanged` | event `(int count)` | Version 2. Raised with the new count whenever layers are added, drip, rinse, are cleaned or dry off, and on login |

Part IDs are the wire IDs in `Cummy/BodyPart.cs`, such as `ch` for Chest and `fb` for Full Body. `sent` means the tell was queued. The partner's Cummy still applies its own pairing, block and range rules when it arrives.

## Credits and licence

Cummy is licensed under the **GNU AGPL v3** (see `LICENSE.md`).

- The overlay compositor is ported from [Proteus](https://github.com/solona-m/proteus) by Solona (AGPL-3.0): upstream skin resolution, `.mtrl`/`.tex` handling and overlay blending. Ported files live in `Cummy/ThirdParty/Proteus/` and keep their attribution.
- The tell-based pairing and messaging pattern follows Oathbound.
- The default facial overlays are converted from [HLF] Cum Blush by HighLowFlow.
- Scaffolded from [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin).
- Penumbra IPC via [Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api).

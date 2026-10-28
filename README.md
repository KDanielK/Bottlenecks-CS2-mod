# Bottlenecks

A Cities: Skylines II code mod that finds deadlocked trains, lets you decide what happens to them, flags overloaded cargo stations, moves bulk cargo onto your cargo lines, and can get a jammed supply chain and post service running again.

- **Paradox Mods:** https://mods.paradoxplaza.com/mods/163289/Windows
- **Feedback and discussion:** [Paradox forum thread](https://forum.paradoxplaza.com/forum/threads/bottlenecks-a-cs2-mod-for-jammed-rail-and-cargo-networks-fixes-issues-with-post-services.1945897/)

![Held train deadlock](Bottlenecks/Properties/Screenshots/01-train-deadlock.png)

## Features

**Trains** (passenger and cargo trains, trams, subways):
- 🔴 **Deadlock:** trains blocking each other are found the moment the game would despawn them. Hold them on the track while you fix the layout, pause the game, or let the game despawn them and mark the spot.
- 🟠 **Blocked:** a train held at a signal, junction or crossing for a long time.
- 🟢 **Queue:** a train waiting a while for a busy station platform.
- 🔔 **Notification cards** that move the camera to the problem.

Some deadlocks are left to the game, as in vanilla: a train blocked by a parked train (the game removes the parked train), and empty trains that get stuck right after leaving their depot or yard.

**Cargo stations, harbours and cargo airports:**
- 🔵 **Not selling:** the station's truck queue has been too long for a while, so companies buy elsewhere. Not critical.
- 🟠 **Overloaded:** the station's trucks are falling behind.
- 🔴 **Backlogged:** a runaway backlog that won't clear by itself. It also stops mail to and from the outside, which takes the post service down.
- **Bulk freight optimisation** (Options, off by default): cargo between two stations that one of your cargo lines connects goes by that line (train, ship or airplane) instead of by truck, which frees up the stations' trucks for companies, warehouses and container yards.
- **Reset cargo transfers** (Options): clears a runaway backlog. Fix the cause first and save before using it, because goods in queued trips are lost.

Hover any icon in the building's info panel for an explanation.

## Does it change the game? Is it safe to remove?

Bottlenecks does not change how the game plans routes: pathfinding, route costs and signals stay vanilla. Most of the mod only observes and shows icons. Only these parts change what happens in the city:
- **Bulk freight optimisation** (off by default): after the game has planned a storage transfer route, the truck stretch between two cargo stations that a cargo line connects is replaced by that line's segments, the same way the game writes a train ride. The game then books the transfer with its own code (`StorageTransferSystem`).
- **Hold deadlocked trains** (on by default): keeps trains the game would despawn.
- **Reset cargo transfers**: only when you press the button.

Safe to remove: apart from its warning icons the mod stores nothing in the save. Transfers booked while it ran are normal game transfers and continue; held trains become ordinary trains again.

## Reporting bugs

Please post in the forum thread or open an issue. Describe what you saw and, if possible, attach the mod's log file:

```
%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\Bottlenecks.Mod.log
```

For crashes, `Player.log` in the folder above helps as well.

## Building from source

Requirements:
- Cities: Skylines II (developed against game version 1.6.2)
- The Cities: Skylines II modding toolchain, installed from the game's options. It sets the `CSII_*` environment variables the project uses.
- .NET SDK

```
dotnet build Bottlenecks/Bottlenecks.csproj -c Release
```

The build deploys the mod to your local Mods folder (`...\Cities Skylines II\Mods\Bottlenecks`). Close the game before building. If you also subscribe to the published version, disable it, or both copies load.

Project layout:
- `Bottlenecks/Mod.cs`: entry point and system registration
- `Bottlenecks/Setting.cs`: options page and English texts
- `Bottlenecks/Systems/`: the simulation systems (deadlock holding, train and cargo icons, despawn markers, cargo reset, bulk freight optimisation, optional diagnostics)

Diagnostics are off in release builds. Set `Mod.EnableDiagnostics` to `true` in `Mod.cs` and rebuild to log `[Diag]` and `[Rail]` lines about post facilities, cargo stations, cargo lines and storage transfer routes, for example to re-check cargo routing after a game update.
- `Bottlenecks/Icons/`: icon sources (SVG) and the embedded PNGs

## License

The code and the icon artwork are released under the [MIT License](LICENSE).

This is a fan-made mod, not affiliated with or endorsed by Paradox Interactive or Colossal Order. Cities: Skylines II and the game content shown in the screenshots belong to their respective owners.

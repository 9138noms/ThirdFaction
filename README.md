# ThirdFaction

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Nuclear Option** that adds a fully configurable third faction to the game.

Designed as a **mission-maker tool** — the mod provides the framework, you create the content.

## Features

- **Third faction** appears in the mission editor faction dropdown
- **Configurable** name, tag, color, logo, and starting balance via BepInEx config
- **AI deployment** — PMC aircraft and vehicles deploy automatically from assigned airbases
- **Full integration** — map icons, leaderboard, radar tracking, and faction coloring all work
- **Uses existing assets** — no new units required; BDF/PALA aircraft and vehicles are available to the third faction
- **Custom logo support** — place a PNG file as `BepInEx/plugins/pmc_logo.png` or specify a path in config

## Requirements

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx) for Unity (IL2CPP or Mono, whichever your game version uses)

## Installation

1. Install BepInEx if you haven't already
2. Download `ThirdFaction.dll` from [Releases](https://github.com/9138noms/ThirdFaction/releases)
3. Place it in `BepInEx/plugins/`
4. Launch the game — a config file will be generated at `BepInEx/config/com.noms.thirdfaction.cfg`

## Configuration

Edit `BepInEx/config/com.noms.thirdfaction.cfg`:

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable/disable the mod |
| `FactionName` | `PMC` | Display name of the third faction |
| `FactionTag` | `PMC` | Short tag shown on MFD |
| `StartingBalance` | `5000` | Starting funds for the faction |
| `FactionColor` | `0.2,0.8,0.2,1` | RGBA color (comma-separated floats) |
| `LogoPath` | *(empty)* | Path to a custom PNG logo (auto-generates a colored circle if empty) |

## For Mission Makers

1. Open the mission editor
2. The third faction now appears in the faction dropdown
3. Place units and assign them to the third faction
4. Set airbases, supply counts, and AI aircraft limits as usual
5. The faction's AI will handle deployment and combat autonomously

## Limitations

- **Singleplayer only** — the mod bypasses network sync (SyncVar/SyncList) to function. Multiplayer is not supported.
- **No alliance system** — the third faction is hostile to both BDF and PALA by default (game treats unknown factions as hostile).

## Building from Source

Requires .NET Framework 4.7.2 and references to Nuclear Option's `Assembly-CSharp.dll` and Unity/BepInEx assemblies. See `ThirdFaction.csproj` for reference paths.

## License

MIT

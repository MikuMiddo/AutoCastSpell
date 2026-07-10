# AutoCastSpell

Auto-caster mod for [Orb of Creation](https://store.steampowered.com/app/2804360/Orb_of_Creation/). Automatically casts your spells so you can focus on strategy.

## Installation

1. Install **BepInEx 5.x** ([download](https://github.com/BepInEx/BepInEx/releases)) — extract to the game root folder
2. Download `AutoCastSpell.dll` from [Releases](https://github.com/IngoHHacks/AutoCastSpell/releases/latest)
3. Place the DLL in `BepInEx/plugins/`
4. Launch the game

**Linux / Steam Deck:** add launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`

## Quick Start

After loading into a game, you'll see the startup hint at the bottom of the screen:

```
AutoCastSpell loaded! LAlt+]/LAlt+[ to cycle | LAlt+X to exclude | F2 to toggle
```

Press **`LAlt + ]`** to start auto-casting. The hint fades out after 10 seconds.

## Keybinds

| Key | Action |
|---|---|
| `F2` | Toggle auto-casting on/off globally |
| `LAlt + ]` | Next mode |
| `LAlt + [` | Previous mode |
| `LAlt + X` | Toggle exclusion for the spell under the mouse cursor |

All keybinds can be customized in the main menu settings panel or in `BepInEx/config/`.

## Modes

| # | Mode | Behavior |
|---|---|---|
| 0 | **Disabled** | No auto-casting |
| 1 | **Cast all** | Cast all ready spells in slot order |
| 2 | **Cheapest cost first** | Prioritize lowest resource cost first |
| 3 | **Shortest CD first** | Prioritize shortest cooldown first |
| 4 | **Buff Sync** | Synchronized buff management (see below) |

## Buff Sync Mode

Only **toggled** buff spells (Aura / Channel types that can be manually turned on/off) participate in Buff Sync. Other spells — including one-shot duration spells — are treated as regular damage spells and cast normally.

### Buff spells (toggled Aura / Channel)

- **All ready** → released together in batch to maximize overlap
- **Only 1 missing** (others active) → the missing one is cast to fill the gap
- **Multiple missing** → wait for synchronization

### Non-buff spells (damage, one-shot durations, etc.)

- **All buffs active** → released immediately
- **Some buffs missing** → compares time until all buffs return vs. the spell's own cooldown; if the wait is longer than one cooldown cycle, the spell is cast anyway
- **Channeled spell active** → all auto-casting is paused until the channel ends

## Spell Exclusion

Hover your mouse over any spell icon and press `LAlt + X` to toggle whether it's included in auto-casting.

| Indicator | Meaning |
|---|---|
| Green `[A]` | Auto-cast enabled |
| Yellow `[A]` | Hovering — press LAlt+X to toggle |
| Grey `[×]` | Excluded from auto-casting |

Exclusions are saved and persist across sessions.

## Channeled Spells

Channeled spells are cast **last** — after all other ready spells. While a channeled spell is active, no other spells will be auto-cast. If multiple channeled spells are available, the first one in slot order is used.

## Troubleshooting

- **Log file:** `BepInEx/LogOutput.log`
- **Console:** set `Enabled = true` under `[Logging.Console]` in `BepInEx/config/BepInEx.cfg`
- **Config reset:** delete `BepInEx/config/IngoH.OrbOfCreation.AutoCastSpell.cfg` to restore defaults

## Building from Source

1. Clone the repo
2. Copy `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `Unity.TextMeshPro.dll`, `UnityEngine.UI.dll`, and `Newtonsoft.Json.dll` from `Orb Of Creation_Data/Managed/` into `lib/`
3. Publicize `Assembly-CSharp.dll` → `lib/Assembly-CSharp_public.dll` (use [BepInEx.AssemblyPublicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer))
4. `dotnet build`

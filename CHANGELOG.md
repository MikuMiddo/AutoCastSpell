## 1.2.0

- **Priority system**: each spell has a numeric priority (default 1). Displayed as `[1]`, `[2]`, etc. on spell icons.
  - Priority `0` shows `[A]` — always cast, bypasses all restrictions.
  - **Mouse wheel** over a spell icon adjusts its priority up/down.
- **Simplified modes**: removed "Cheapest cost first" and "Shortest CD first". Now only two modes:
  - **Cast all** — release ready spells in priority order (high number first).
  - **Buff Sync** — only release a spell if all higher-priority buffs are currently active. `[A]` spells bypass this check.
- **Redesigned Buff Sync**: no more batch queue or cooldown estimation. Purely priority-driven — "if higher-priority buffs are on, release. Otherwise wait."

## 1.1.2

- **Code restructure**: rewrote AutoCaster.cs with clear architecture — `RefreshState` / `Spell Queries` / `TickNormal` / `TickBuffSync` / `OnGUI`
- **Removed batch queue**: Buff Sync now uses `_syncing` flag for per-tick direct decision instead of pre-built release queue
- Simplified naming: `CanCastBuff`, `IsBuffActive`, `IsManagedBuff`
- Merged `DecideBuffSyncAction` into inline decision tree in `TickBuffSync`

## 1.1.1

- **Draggable settings window**: main menu panel now uses `GUI.Window` — drag by the title bar
- Fixed spacing between labels and buttons to match original layout
- Merged `IsBuffReadyForSync` / `IsBuffActuallyActive` simplifications (dead code removal)
- Cleaned up debug logging and build artifacts

## 1.1.0

- **Buff Sync overhaul**: only toggled (Aura/Channel) spells are managed as buffs; one-shot duration spells treated as regular damage
- **Precise buff state tracking**: distinguishes "buff active" vs "buff on cooldown" using `IsCasting()` and cooldown start time detection
- **Damage waiting logic**: compares time until all buffs return against each spell's own cooldown
- **Channeled spells**: cast last in priority order; active channeling pauses all other auto-casting
- **Spell exclusion**: hover over icon + LAlt+X to toggle per-spell; visual indicators on spell buttons
- **Improved OnGUI**: fade-out startup hint with keybind tips; right-side mode display with outline
- **Configurable keybinds** via main menu settings panel
- Fixed toggled spell cancellation on re-cast
- Stale record cleanup when spells are swapped out of loadout

## 1.0.0

- Initial release
- Auto-cast with 3 modes: Cast all, Cheapest cost first, Shortest CD first
- Spell exclusion system
- Global enable/disable

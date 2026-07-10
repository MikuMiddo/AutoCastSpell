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

# GatherRatePlusUI

Gather rates for Rust servers running Oxide/uMod, with an in-game editor.

**Gather Rates Plus** decides how much players get when they gather: trees, ore, corpses, wild
plants and crops, quarries, the excavator, survey charges, and helicopter and Bradley wreckage.
Rates can differ:

- **by player**, through permissions: VIPs on 3x, everyone else on 2x;
- **by area**, through ZoneManager: a 5x zone around a monument;
- **by source**: "3x high quality metal, but only from downed helicopters".

It can also speed up quarries and the excavator, change how often worms and grubs drop, and import
the settings of the GatherManager plugin.

**Gather Rates Plus UI** is an optional full-screen editor for all of that: type `/grpui` and change
rates with the mouse instead of console commands. Changes save immediately, and several admins can
edit at once without overwriting each other.

Players need nothing installed. They can type `/gather` to see the rates that apply to them.

| File | What it is | Guide |
|---|---|---|
| `GatherRatesPlus.cs` 0.3.0 | The plugin. Works on its own, from the console and RCON | [README.GatherRatesPlus.md](README.GatherRatesPlus.md) |
| `GatherRatesPlusUI.cs` 0.1.0 | The `/grpui` editor. Needs Gather Rates Plus | [README.GatherRatesPlusUI.md](README.GatherRatesPlusUI.md) |

## Install

1. **Unload any other gather plugin first** (GatherManager, UGather and so on). Two gather plugins
   multiply each other's rates: 3x and 2x becomes 6x.
2. Copy `GatherRatesPlus.cs`, and `GatherRatesPlusUI.cs` if you want the editor, into the server's
   `oxide/plugins` folder. They load within a few seconds.
3. Give admins `gatherratesplus.admin` (server owners and moderators have it automatically):
   ```
   oxide.grant user "Some Admin" gatherratesplus.admin
   ```
4. Out of the box everyone gathers at vanilla rates. Set the base rate in `/grpui`, or with
   `grp.defaultrate default 2`.

For different rates for different players, create a ruleset (`grp.ruleset.add vip 3`, or **New
ruleset** in the editor) and grant its permission, `gatherratesplus.ruleset.vip`. Zones need the
**ZoneManager** plugin; everything else works without it.

Gather Rates Plus uses Harmony, which comes with Oxide. Tested on Oxide only; Carbon is untested.

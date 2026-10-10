# Gather Rates Plus UI — user guide

`/grpui` opens a full-screen editor for **Gather Rates Plus**. Everything the `grp.*` console
commands can change is on screen here: rulesets, per-item rates, per-source rates, bonus rolls,
gather zones, quarry and excavator speed, and who holds which ruleset. Version 0.1.0.

The editor is optional. It changes nothing by itself: every setting lives in Gather Rates Plus, and
the editor reads and writes it through that plugin. What each setting means is explained in
[README.GatherRatesPlus.md](README.GatherRatesPlus.md); this guide covers using the screen.

1. [Installing](#1-installing)
2. [Opening and closing](#2-opening-and-closing)
3. [Conventions used on every tab](#3-conventions-used-on-every-tab)
4. [The tabs](#4-the-tabs)
5. [Several admins at once](#5-several-admins-at-once)
6. [Permissions](#6-permissions)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Installing

1. Install **Gather Rates Plus** first (`GatherRatesPlus.cs`). The editor does nothing without it.
2. Copy `GatherRatesPlusUI.cs` into `oxide/plugins/` and make it readable (`chmod 666`).
3. Oxide loads it within a few seconds. Check the console for `Loaded plugin Gather Rates Plus UI`.

The editor has no config or data file of its own.

---

## 2. Opening and closing

- Type **`/grpui`** in chat. The editor opens after a short pause (the game needs a moment after a
  chat command before it will show a screen).
- Close it with the **X** at the top right.
- Six tabs run across the top: **Rulesets, Rates, Dispensers, Zones, Speed, Players**. The editor
  remembers which tab and ruleset you were on until you disconnect.

**Every change saves the moment you make it.** There is no Apply or Save button.

---

## 3. Conventions used on every tab

- **Typing a rate.** Click a box, type, press Enter:

  | You type | Meaning |
  |---|---|
  | `2` | twice the vanilla amount |
  | `=5` | always exactly 5 |
  | `=5-10` | a random 5 to 10 each time |

- **Dimmed values are inherited**, not set on this screen. A **bright value on a darker box** is one
  you set, and has a **Reset** or **Clear** button beside it.
- **Amber means "not the final word"**: something more specific overrides the number you are looking
  at. An amber **"N dispenser overrides"** button lists those sources; clicking one jumps to it, and
  Cancel brings you back.
- **Destructive buttons need two clicks.** The first click arms the button and turns it red
  ("Click again to confirm"); if you don't click again within a few seconds it disarms.
- **Search boxes** filter as you type. Long lists are paged.

---

## 4. The tabs

### Rulesets

Every ruleset with its base rate, how many overrides it has, and the permission that gives it to
players.

- **Edit rates** opens that ruleset on the Rates tab.
- Change a ruleset's **base rate** in place.
- **New ruleset**: type a name and a base rate. This also creates its permission,
  `gatherratesplus.ruleset.<name>`.
- **Reset** clears a ruleset's overrides; **Delete** removes it (not possible for `default`).
- **Reset everything** clears every ruleset, zone and speed setting. Gather Rates Plus backs the
  config up first.

### Rates — per item, from every source

One row per item that can be gathered, with the sources it comes from ("Gathered from…").

- Type a rate in the **Rate** column, or **Clear** it to fall back to the base rate.
- **Chance** is for the bonus items (worm and grub): the base drop chance everywhere.
- The **Gatherable / All items** button switches the list between items that can be gathered and
  every item in the game (useful for items the server has not seen gathered yet).
- **Set all N matching**: the **1–10** buttons apply that rate to *every item matching your search*,
  not just the visible page. Search first, then click.

### Dispensers — per source

Pick a source on the left (a tree, an ore node, a plant, a corpse, a quarry, wreckage…). On the
right is a row for every item it yields, and the **Source** column says where each number comes
from: **override** (set here), **item rule** (from the Rates tab) or **base rate**.

- A rate set here beats the Rates tab for this source only: this is how you make "3x high quality
  metal, but only from downed helicopters".
- Bonus items (shown in green) also have a **Chance** and an **Amount**, and the **one only** tick
  box (the vanilla "award at most one bonus item" flag, which applies to the whole source).
- Some sources are filled at run time and show "No confirmed output yet" until someone gathers from
  them. If you already know an item's short name, type it under **Add item**.

### Zones — per area (needs ZoneManager)

The gather zones, each with its ruleset, radius, **Enabled**, **Overrides perm** and **Needs perm**
switches, and **Delete**.

- **Add zone at your position**: type `<ruleset> <radius>`, for example `vip 120`, and press Enter.
  The zone is centred where you stand.
- Without ZoneManager the tab says "ZoneManager plugin missing". Existing zones are kept and work
  again once ZoneManager loads.

### Speed — how fast, not how much

Mining quarry tick rate, excavator tick rate, excavator belt speed and excavator time to full output,
each shown against its vanilla value with a **Reset**.

Halving a tick rate doubles output per hour but also doubles fuel burn; raising the rate on the Rates
tab instead makes each unit of fuel go further.

The **Round multiplied amounts instead of truncating** switch is here too (on by default, and
recommended): with it off, 1.5x on a 1-unit yield gives 1.

### Players — who gets which ruleset

Connected players, searchable, with one button per ruleset. Click a button to grant or revoke that
ruleset's permission (the same as `oxide.grant` / `oxide.revoke`). If a player holds several, the one
listed last on the Rulesets tab applies.

---

## 5. Several admins at once

Editing alongside other admins is safe. The title bar shows how many other editors are open. Any
change, whether from another admin's editor, the console or RCON, refreshes every open editor
straight away with the note "Settings changed elsewhere - reloaded", so nobody overwrites a change
with an out-of-date screen.

---

## 6. Permissions

| Permission | What it allows |
|---|---|
| `gatherratesplus.admin` | Opening `/grpui` (and every `grp.*` command in Gather Rates Plus) |

Players with auth level 1 or 2 (`moderatorid` / `ownerid`) count as admins without the permission.

```
oxide.grant user "Some Admin" gatherratesplus.admin
```

---

## 7. Troubleshooting

| Problem | What to check |
|---|---|
| "You don't have permission to use the Gather Rates editor." | Grant `gatherratesplus.admin` |
| "GatherRatesPlus is not loaded." | Install or reload `GatherRatesPlus.cs`; check the console for a compile error |
| Nothing appears after `/grpui` | Wait a second; the editor opens after a short pause |
| A source shows no items | It is filled at run time: gather from it once, or add the item by short name |
| Zones tab says ZoneManager is missing | Install and load ZoneManager, then reopen the tab |
| Rates are much higher than set | Another gather plugin is running too and the rates multiply; see the Gather Rates Plus guide |

# Gather Rates Plus

Controls how much players get when they gather: trees, ore nodes, corpses, wild plants,
planted crops, quarries, the excavator, survey charges, and helicopter/Bradley wreckage.

Rates can differ **per player** (through permissions), **per area** (through ZoneManager)
and **per source** — so "3x high quality metal, but only from downed helicopters" is a
single setting rather than something you have to work around.

It ships as two plugins:

| File | What it is |
|---|---|
| `GatherRatesPlus.cs` | the plugin itself — everything works without the UI |
| `GatherRatesPlusUI.cs` | an in-game editor (`/grpui`). Optional |

---

## 1. Installing

Copy both `.cs` files into your server's plugin folder:

```
<server>/oxide/plugins/
```

Oxide compiles them automatically. You should see:

```
GatherRatesPlus was compiled successfully
Loaded plugin Gather Rates Plus
```

**Make sure the files are world-readable** (`chmod 666`). A plugin file the server user
cannot read does not merely fail to load — on some setups it wedges Oxide's compiler until
the server is restarted.

### Where the settings live

| File | Contents | Safe to hand-edit? |
|---|---|---|
| `oxide/config/GatherRatesPlus.json` | every rate, ruleset, zone and speed setting | Yes — reload the plugin afterwards |
| `oxide/data/GatherRatesPlus.json` | who started which quarry, who threw which survey charge, learned item sources | No — managed automatically |

The config file is created on first load. If you edit it by hand, run
`oxide.reload GatherRatesPlus` to apply your changes.

### Replacing another gather plugin

**Unload any other gather plugin first.** Gather Rates Plus, GatherManager and UGather all
hook the same events, so running two together multiplies their rates together — a 3x and a
2x becomes 6x. Renaming the file to `.cs.disabled` and running `oxide.unload <name>` is
enough.

If you are coming from GatherManager, `grp.import` will convert its settings for you — see
§5.

---

## 2. How it works

### Rulesets and the base rate

A **ruleset** is one complete set of rates. Every server has one automatically:

- **`default`** — applies to everyone. It has no permission attached and cannot be deleted.

You can add more, and each one creates a permission. Players holding that permission use
that ruleset instead of `default`:

```
grp.ruleset.add vip 3
→ creates the permission gatherratesplus.ruleset.vip
```

Every ruleset has a **base rate** — the multiplier used for anything you have not given a
specific setting. A base rate of `3` means "three times vanilla for everything".

If a player holds several ruleset permissions, the **last one listed** in the config wins.
They do not add up.

### The Rates tab — rates per item

Sets a rate for one item, from every source at once. "Wood at 2x" means wood from every
tree, everywhere.

Each row shows where that item can actually be gathered from, and warns you when something
more specific is overriding the number you are looking at.

You can enter three kinds of value:

| You type | Meaning |
|---|---|
| `2` | twice the vanilla amount |
| `=5` | always exactly 5, regardless of tool or node |
| `=5-10` | a random 5 to 10 each time |

> **Note on fixed amounts.** For wild plants, quarries and the excavator these behave
> exactly as you would expect. On trees and ore nodes they have a side effect: a better tool
> takes bigger bites out of the node, so it breaks in fewer hits — and with a fixed amount
> per hit, fewer hits means *less total*. A rock can out-farm a jackhammer. Multipliers do
> not have this problem.

The **Chance** column is only used by bonus items (see below) and stays blank for everything
else.

### The Dispensers tab — rates per source

The same item can be worth a different amount depending on where it came from. Select a
source on the left and you get a row for every item it yields:

```
Sunflower       sunflower        5            base rate
Sunflower Seed  seed.sunflower   5            base rate
Grub            grub             5    5%      base rate   [x] one only
Worm            worm             5    20%     base rate   [x] one only
```

The **Source** column tells you where the displayed number comes from:

- **override** — set here, for this source only
- **item rule** — inherited from the Rates tab
- **base rate** — inherited from the ruleset

A value set here beats the Rates tab, which beats the base rate. The first match wins;
they do not multiply together.

**Bonus items** (worm and grub, shown in green) are not part of what a source contains —
they are a separate random roll that happens when you pick a plant. They have two extra
controls:

- **Chance** — how often it drops. Vanilla varies by source: 20% from planted crops, 10%
  from wild plants, 5% from mushrooms and coconuts.
- **[x] one only** — the vanilla "award one item at most" flag. It belongs to the whole
  table, so ticking it on any row affects every bonus item on that source.

Rates only change the bonus **amount**, never how often it drops. To change frequency, set
a Chance — either per source here, or as a base chance on the Rates tab.

### The Zones tab — rates per area

Needs the **ZoneManager** plugin. Without it the tab shows *"ZoneManager plugin missing"*
and zones stay switched off; any zones already in your config are kept and start working
again as soon as ZoneManager is loaded.

A zone applies a chosen ruleset to anyone gathering inside it. Each has:

- **Radius** and a position (taken from where you stood when you created it)
- **On** — switch it off without deleting it
- **Overrides perm** — the zone's ruleset wins even over a player's own permission
- **Needs perm** — the player must also hold that ruleset's permission for the zone to
  apply to them
- Optional messages shown on entering and leaving

### The Speed tab — how fast, rather than how much

Four settings that change *throughput* instead of payout:

| Setting | Vanilla |
|---|---|
| Mining quarry tick rate | 5s |
| Excavator tick rate | 3s |
| Excavator belt speed | 0.1 |
| Excavator time to full output | 120s |

**Speed and rates are not the same thing.** Doubling the *rate* means each fuel charge
produces twice as much. Halving the *tick rate* means twice as much per hour, but it also
burns fuel twice as fast — you have bought speed, not value.

Anything left at its vanilla value is not touched at all.

This tab also holds **Round multiplied amounts instead of truncating** (on by default).
With it off, a 1.5x rate on a 1-unit yield produces 1 — the multiplier silently does
nothing on low-yield sources.

### The Players tab — who gets what

Lists connected players and the rulesets they hold, with a button per ruleset to grant or
revoke. Equivalent to `oxide.grant` / `oxide.revoke`, without leaving the game.

---

## 3. Permissions

| Permission | Who needs it | Grants |
|---|---|---|
| *(none)* | everyone | `/gather` — see your own rates |
| `gatherratesplus.admin` | admins | every `grp.*` command and the `/grpui` editor |
| `gatherratesplus.ruleset.<name>` | players | use that ruleset instead of `default` |

Players with **auth level 1 or 2** (server `ownerid` / `moderatorid`) are treated as admins
without needing `gatherratesplus.admin`.

```
oxide.grant user <player> gatherratesplus.admin
oxide.grant group vip gatherratesplus.ruleset.vip
```

### 3.1 Custom rulesets in practice

Rulesets exist to give different players different rates. A typical tiered setup:

```
grp.ruleset.add vip 3
grp.ruleset.add elite 5
oxide.grant group vip   gatherratesplus.ruleset.vip
oxide.grant group elite gatherratesplus.ruleset.elite
```

Everyone else stays on `default`.

Things worth knowing:

- **Order matters.** If a player somehow holds both `vip` and `elite`, the one listed last
  in the config wins. They never stack.
- **Rulesets are complete sets, not modifiers.** `vip` does not inherit anything from
  `default` — it has its own base rate and its own item and per-source settings. Setting
  something in `default` does not appear in `vip`.
- **Deleting a ruleset** leaves its permission registered until the plugin reloads. Players
  still holding it fall back to `default`, so nothing breaks.
- **Zones can carry a ruleset too.** "Needs perm" decides whether a zone applies to
  everyone inside it or only to players who already hold that ruleset's permission.
- **Bonus chance and amount set explicitly cannot vary per ruleset.** The underlying table
  is shared by the whole server. Bonus *amounts* driven by a rate do follow the player's
  ruleset; explicit chances and amounts are global.

---

## 4. Commands for players

| Command | Does |
|---|---|
| `/gather` | shows the rates that apply to you — base rate, any item-specific rates, and whether you are standing in a gather zone |

That is the whole player-facing surface. Everything else is admin-only.

---

## 5. Commands for admins

All of these work in the F1 console and over RCON. `<ruleset>` is `default` unless you have
created others.

### Rulesets

```
grp.rulesets                             list rulesets, rates and permissions
grp.ruleset.add <name> <rate>            create one
grp.ruleset.remove <name>                delete one
grp.defaultrate <ruleset> <rate>         set a ruleset's base rate
```

### Rates

Values are `2` (multiply), `=5` (fixed), `=5-10` (random range) or `remove`.

```
grp.rate <ruleset> <item> <value>                    per item, everywhere
grp.rate <ruleset> * <value>                         same as setting the base rate
grp.dispenserrate <ruleset> <source> <item> <value>  per item, from one source only
grp.clearoverrides <ruleset> [filter]                remove per-source settings
grp.clearoverrides <ruleset> [filter] confirm        ...and actually do it
```

### Finding names

```
grp.listitems [filter]        item short names (metal.refined, not "High Quality Metal")
grp.listdispensers [filter]   source names, labelled by type
grp.sources <item>            which sources yield an item
```

### Bonus rolls (worm and grub)

```
grp.bonus [filter] [page]                            show the bonus tables
grp.bonus summary                                    grouped view
grp.bonuschance <item> <percent|remove>              base chance everywhere
grp.bonusset <source> <item> amount <n>              fixed amount, one source
grp.bonusset <source> <item> chance <percent>        chance, one source
grp.bonusset <source> <item> reset                   back to vanilla
grp.bonusset <source> onlyawardone <true|false|reset>
```

### Speed

```
grp.speed                        show all four against vanilla
grp.quarrytickrate <seconds>
grp.excavatortickrate <seconds>
grp.excavatorbelt <speed>
grp.excavatorfulltime <seconds>
```

### Zones (needs ZoneManager)

```
grp.zone list
grp.zone add <ruleset> <radius>            uses your position; in game only
grp.zone remove <zoneId>
grp.zone set <zoneId> <field> <value>      ruleset | radius | enabled |
                                           override | requireperm | enter | leave
```

### Importing from GatherManager

```
grp.import                       preview the conversion, change nothing
grp.import apply                 convert into the default ruleset
grp.import apply <ruleset>       convert into a named ruleset
```

Reads `oxide/config/GatherManager.json`. It maps GatherManager's five separate rate tables
onto the sources each one covered, converts display names to short names, and reports any
entries that never did anything (for example a rate on an item that source cannot produce).

Preview first — `apply` replaces the target ruleset's rates entirely. It writes a backup to
`oxide/config/GatherRatesPlus.pre-import-<timestamp>.json` first and stops if that fails.

### Starting over

```
grp.reset <ruleset>              preview
grp.reset <ruleset> confirm      clear one ruleset
grp.resetall                     preview
grp.resetall confirm             clear everything, including zones and speed
```

Both back up the config first.

---

## 6. The editor — `/grpui`

The full guide to the editor is [README.GatherRatesPlusUI.md](README.GatherRatesPlusUI.md).

Typed in chat, by an admin. It opens after a short delay; if nothing appears immediately,
give it a second.

Six tabs across the top: **Rulesets, Rates, Dispensers, Zones, Speed, Players** — each
covered in §2. Close with the **X** at the top right.

Everything saves the moment you change it. There is no apply button.

**Editing alongside other admins is safe.** The title bar shows how many other editors are
open, and any change — from another admin's editor, the console, or RCON — refreshes every
open screen immediately with a note saying settings changed elsewhere. Two admins cannot
overwrite each other with a stale view.

Some conventions used throughout:

- A **dimmed** value is inherited, not set here. A **bright** value on a darker box is
  something you set, and has a **Reset** or **Clear** button next to it.
- **Amber** means "the number you are looking at is not the final word" — something more
  specific is overriding it.
- An amber **"N dispenser overrides"** button is clickable: it lists the sources that
  override that item, and clicking one takes you straight there. Cancel returns you.
- Destructive buttons need **two clicks**. The first arms it and turns it red; the arming
  expires after a few seconds.
- Search boxes filter as you type. On the Rates tab, the **1–10** buttons apply a rate to
  *every item matching your current search*, not just the visible page.

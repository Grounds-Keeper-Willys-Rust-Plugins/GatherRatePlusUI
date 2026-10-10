using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Gather Rates Plus UI", "Lucien Murray-Pitts", "0.1.0")]
    [Description("Full-screen admin UI for editing Gather Rates Plus: rates, dispensers, zones, speed and permissions")]
    internal class GatherRatesPlusUI : RustPlugin
    {
        [PluginReference] private Plugin GatherRatesPlus, ServerMenuSuite;

        #region Constants

        private const string PermissionAdmin = "gatherratesplus.admin";

        private const string UiRoot = "GRPUI.Root";

        private const string ColWindow = "0.115 0.125 0.135 0.985";
        private const string ColBar = "0.165 0.180 0.195 1";
        private const string ColPanel = "0.145 0.155 0.168 1";
        private const string ColCard = "0.205 0.220 0.235 1";
        private const string ColCardAlt = "0.245 0.262 0.280 1";
        private const string ColField = "0.085 0.092 0.100 1";
        private const string ColAccent = "0.278 0.529 0.757 1";
        private const string ColGreen = "0.353 0.545 0.278 1";
        private const string ColRed = "0.686 0.278 0.220 1";
        private const string ColAmber = "0.760 0.560 0.200 1";
        private const string ColNeutral = "0.310 0.330 0.350 1";
        private const string ColText = "0.870 0.880 0.890 1";
        private const string ColTextDim = "0.560 0.585 0.610 1";
        private const string ColClear = "0 0 0 0";
        private const string ColShade = "0 0 0 0.72";

        private const int PageSize = 8;

        // Shared column geometry for the Dispensers pane, so normal and bonus rows align exactly.
        private const float ColItemX = 8f,    ColItemW = 164f;
        private const float ColShortX = 176f, ColShortW = 116f;
        private const float ColRateX = 296f,  ColRateW = 60f;
        private const float ColChanceX = 362f, ColChanceW = 54f;
        private const float ColSrcX = 422f,   ColSrcW = 84f;
        private const float ColOnlyX = 510f,  ColOnlyW = 86f;
        private const float ColResetX = 600f, ColResetW = 42f;

        // Rates tab columns (row origin is content x=14).
        private const float RItemX = 8f,    RItemW = 220f;
        private const float RShortX = 232f, RShortW = 150f;
        private const float RRateX = 386f,  RRateW = 66f;
        private const float RChanceX = 458f, RChanceW = 60f;
        private const float RSrcX = 524f,   RSrcW = 200f;
        private const float RTagX = 730f,   RTagW = 140f;
        private const float RClearX = 876f, RClearW = 90f;

        private static readonly string[] Tabs = { "Rulesets", "Rates", "Dispensers", "Zones", "Speed", "Players" };

        #endregion

        #region State

        private class UiState
        {
            public string Tab = "Rulesets";
            public string Ruleset = "default";
            public string SelectedDispenser;
            public string ItemSearch = "";
            public string DispenserSearch = "";
            public string PlayerSearch = "";
            public bool ShowAllItems;
            public int DispPage;
            public int DispItemPage;
            public string PendingReset;     // ruleset name, or "*" for everything
            public float PendingUntil;
            public string ModalItem;        // item whose overriding dispensers are being listed
            public int Page;
            public int SeenVersion = -1;
            public string Notice;
            public float NoticeUntil;
        }

        private readonly Dictionary<ulong, UiState> _states = new Dictionary<ulong, UiState>();
        private int _nameCounter;

        // Snapshot cache, refreshed whenever the main plugin reports a change.
        private JObject _snapshot;
        private List<string> _dispensers = new List<string>();
        private Dictionary<string, string> _items = new Dictionary<string, string>();

        // Only the items that can actually come out of a gather, with the dispensers they come from.
        private Dictionary<string, List<string>> _gatherable = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _dispenserItems = new Dictionary<string, List<string>>();

        // Vanilla bonus tables (worm/grub), keyed by dispenser prefab.
        private JObject _bonusTables = new JObject();

        #endregion

        #region Lifecycle

        private void Init()
        {
            // GatherRatesPlus owns this permission; only register it if that plugin has not loaded
            // yet, so the two do not both claim it and log a duplicate warning.
            if (!permission.PermissionExists(PermissionAdmin))
                permission.RegisterPermission(PermissionAdmin, this);
        }

        private void OnServerInitialized()
        {
            RefreshCaches();
        }

        private void Unload()
        {
            foreach (var userId in _states.Keys.ToList())
            {
                var player = BasePlayer.FindByID(userId);
                if (player != null)
                    CuiHelper.DestroyUi(player, UiRoot);
            }

            _states.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason) => _states.Remove(player.userID);

        // Fired by GatherRatesPlus after any mutation, from any source: this UI, another admin's UI,
        // a console command or RCON. Every open editor re-reads and redraws, so two admins editing at
        // once converge instead of overwriting each other with stale screens.
        private void OnGatherRatesPlusChanged(int version)
        {
            RefreshCaches();

            foreach (var entry in _states.ToList())
            {
                var player = BasePlayer.FindByID(entry.Key);
                if (player == null || !player.IsConnected)
                {
                    _states.Remove(entry.Key);
                    continue;
                }

                var state = entry.Value;

                // Anyone whose screen predates this change gets told where it came from.
                if (state.SeenVersion >= 0 && state.SeenVersion != version)
                    Notify(state, "Settings changed elsewhere - reloaded");

                Render(player, state);
            }
        }

        private void RefreshCaches()
        {
            _snapshot = null;

            if (!PluginReady)
                return;

            var raw = GatherRatesPlus.Call("API_GetSnapshot") as string;
            if (string.IsNullOrEmpty(raw))
                return;

            try
            {
                _snapshot = JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                PrintError($"Could not parse the GatherRatesPlus snapshot: {ex.Message}");
                return;
            }

            var dispensersRaw = GatherRatesPlus.Call("API_ListDispensers") as string;
            if (!string.IsNullOrEmpty(dispensersRaw))
                _dispensers = JArray.Parse(dispensersRaw).Select(x => x.ToString()).ToList();

            var itemsRaw = GatherRatesPlus.Call("API_ListItems") as string;
            if (!string.IsNullOrEmpty(itemsRaw))
                _items = JObject.Parse(itemsRaw).Properties().ToDictionary(x => x.Name, x => x.Value.ToString());

            var gatherableRaw = GatherRatesPlus.Call("API_ListGatherableItems") as string;
            if (!string.IsNullOrEmpty(gatherableRaw))
            {
                _gatherable = JObject.Parse(gatherableRaw).Properties().ToDictionary(
                    x => x.Name,
                    x => ((JArray)x.Value["sources"]).Select(v => v.ToString()).ToList());
            }

            var bonusRaw = GatherRatesPlus.Call("API_ListBonusTables") as string;
            if (!string.IsNullOrEmpty(bonusRaw))
                _bonusTables = JObject.Parse(bonusRaw);

            var dispItemsRaw = GatherRatesPlus.Call("API_ListDispenserItems") as string;
            if (!string.IsNullOrEmpty(dispItemsRaw))
            {
                _dispenserItems = JObject.Parse(dispItemsRaw).Properties().ToDictionary(
                    x => x.Name,
                    x => ((JArray)x.Value).Select(v => v.ToString()).ToList());
            }
        }

        private bool PluginReady => GatherRatesPlus != null && GatherRatesPlus.IsLoaded;

        private bool ZonesAvailable => _snapshot != null && _snapshot.Value<bool>("zonesAvailable");

        private int SnapshotVersion => _snapshot?.Value<int>("version") ?? -1;

        #endregion

        #region Snapshot Helpers

        private JObject GetRuleset(string name)
        {
            if (_snapshot == null)
                return null;

            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                return _snapshot["default"] as JObject;

            return (_snapshot["rulesets"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals(x.Value<string>("Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> RulesetNames()
        {
            var names = new List<string> { "default" };
            var rulesets = _snapshot?["rulesets"] as JArray;

            if (rulesets != null)
                names.AddRange(rulesets.OfType<JObject>().Select(x => x.Value<string>("Name")).Where(x => !string.IsNullOrEmpty(x)));

            return names;
        }

        private float RateForItem(JObject ruleset, string shortName)
        {
            var overrides = ruleset?["ItemRateOverrides"] as JObject;
            var token = overrides?[shortName];
            return token?.Value<float>() ?? ruleset?.Value<float>("DefaultRate") ?? 1f;
        }

        private bool HasItemOverride(JObject ruleset, string shortName)
            => (ruleset?["ItemRateOverrides"] as JObject)?[shortName] != null;

        private JObject DispenserOverrides(JObject ruleset, string prefab)
            => (ruleset?["DispenserRateOverrides"] as JObject)?[prefab] as JObject;

        // Renders a stored rate the same way the console accepts it, so the input field round-trips:
        // a plain number is a multiplier, "=5" a fixed amount, "=5-10" an inclusive random range.
        private static string SpecInputText(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
            {
                var mode = token.Value<string>("Mode");
                if (mode == "Fixed")
                    return "=" + token.Value<int>("Amount");
                if (mode == "Range")
                    return $"={token.Value<int>("Min")}-{token.Value<int>("Max")}";
            }

            return token.Value<float>().ToString(CultureInfo.InvariantCulture);
        }

        private static string SpecMode(JToken token)
        {
            if (token != null && token.Type == JTokenType.Object)
            {
                var mode = token.Value<string>("Mode");
                if (mode == "Fixed") return "fixed";
                if (mode == "Range") return "range";
            }

            return "multiply";
        }

        // How many dispenser-level overrides shadow this item in the current ruleset. Without this the
        // Rates tab shows the base rate while a per-dispenser entry is what actually applies -- which
        // reads as "I set 5 but I get 2".
        private int ShadowCount(JObject ruleset, string shortName)
        {
            var dispensers = ruleset?["DispenserRateOverrides"] as JObject;
            if (dispensers == null)
                return 0;

            var count = 0;
            foreach (var property in dispensers.Properties())
            {
                var items = property.Value as JObject;
                if (items?[shortName] != null)
                    count++;
            }

            return count;
        }

        private JObject BonusTable(string prefab) => _bonusTables?[prefab] as JObject;

        // Items that appear in any RandomItemDispenser table, so the Rates tab knows which rows
        // get a Chance box and which leave that column blank.
        private HashSet<string> BonusItemNames()
        {
            var names = new HashSet<string>();
            if (_bonusTables == null)
                return names;

            foreach (var property in _bonusTables.Properties())
            {
                var entries = (property.Value as JObject)?["entries"] as JArray;
                if (entries == null)
                    continue;

                foreach (var entry in entries.OfType<JObject>())
                    names.Add(entry.Value<string>("item"));
            }

            return names;
        }

        // Vanilla chance for a bonus item, across every prefab that rolls it. Worm is 20% on planted
        // crops, 10% wild and 5% on mushrooms, so a single number would be a lie - show the range.
        private string VanillaChanceText(string item)
        {
            var values = new List<float>();

            if (_bonusTables != null)
            {
                foreach (var property in _bonusTables.Properties())
                {
                    var entries = (property.Value as JObject)?["entries"] as JArray;
                    if (entries == null)
                        continue;

                    foreach (var entry in entries.OfType<JObject>())
                    {
                        if (entry.Value<string>("item") == item)
                            values.Add(entry.Value<float>("chance") * 100f);
                    }
                }
            }

            if (values.Count == 0)
                return "";

            var min = values.Min();
            var max = values.Max();

            return Mathf.Approximately(min, max)
                ? min.ToString("0.#", CultureInfo.InvariantCulture)
                : $"{min.ToString("0.#", CultureInfo.InvariantCulture)}-{max.ToString("0.#", CultureInfo.InvariantCulture)}";
        }

        // Does any single dispenser carry its own chance for this item? If so the item-level value
        // shown here is not the whole story.
        private int BonusChanceOverrideCount(string item)
        {
            var overrides = _snapshot?["bonusOverrides"] as JObject;
            if (overrides == null)
                return 0;

            var count = 0;
            foreach (var property in overrides.Properties())
            {
                var items = (property.Value as JObject)?["Items"] as JObject;
                if ((items?[item] as JObject)?["ChancePercent"] != null)
                    count++;
            }

            return count;
        }

        private float? BonusItemChance(string item)
        {
            var token = (_snapshot?["bonusItemChances"] as JObject)?[item];
            return token == null ? (float?)null : token.Value<float>();
        }

        private JObject BonusOverrideFor(string prefab)
            => (_snapshot?["bonusOverrides"] as JObject)?[prefab] as JObject;

        private JObject BonusItemOverride(string prefab, string item)
            => (BonusOverrideFor(prefab)?["Items"] as JObject)?[item] as JObject;

        // Effective OnlyAwardOne: the override if one is set, otherwise vanilla.
        private bool BonusOnlyAwardOne(string prefab, out bool overridden)
        {
            var over = BonusOverrideFor(prefab)?["OnlyAwardOne"];
            overridden = over != null && over.Type != JTokenType.Null;
            return overridden ? over.Value<bool>() : BonusTable(prefab)?.Value<bool>("onlyAwardOne") ?? true;
        }

        private string DisplayName(string shortName)
        {
            string display;
            return _items.TryGetValue(shortName, out display) ? display : shortName;
        }

        #endregion

        #region Open / Close

        [ChatCommand("grpui")]
        private void ChatOpen(BasePlayer player, string command, string[] args)
        {
            if (!HasAdmin(player))
            {
                SendReply(player, "You don't have permission to use the Gather Rates editor.");
                return;
            }

            if (!PluginReady)
            {
                SendReply(player, "GatherRatesPlus is not loaded.");
                return;
            }

            // Opening from chat needs a beat before CUI will take, same as the other suite editors.
            timer.Once(0.3f, () =>
            {
                if (player == null || !player.IsConnected)
                    return;

                RefreshCaches();
                var state = GetState(player);
                Render(player, state);
            });
        }

        [ConsoleCommand("grpui.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            _states.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiRoot);
        }

        private bool HasAdmin(BasePlayer player)
            => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin));

        private UiState GetState(BasePlayer player)
        {
            UiState state;
            if (!_states.TryGetValue(player.userID, out state))
            {
                state = new UiState();
                _states[player.userID] = state;
            }

            return state;
        }

        private void Notify(UiState state, string message)
        {
            state.Notice = message;
            state.NoticeUntil = Time.realtimeSinceStartup + 4f;
        }

        // Re-renders only the caller. Every mutation path ends here; cross-editor updates arrive
        // separately through OnGatherRatesPlusChanged.
        private void Redraw(ConsoleSystem.Arg arg, Action<BasePlayer, UiState> mutate = null)
        {
            var player = arg.Player();
            if (player == null || !HasAdmin(player))
                return;

            var state = GetState(player);
            mutate?.Invoke(player, state);
            Render(player, state);
        }

        #endregion

        #region Render

        private string NextName() => "GRPUI." + (++_nameCounter);

        private static CuiRectTransformComponent Rect(float x, float y, float w, float h)
            => new CuiRectTransformComponent
            {
                AnchorMin = "0 0",
                AnchorMax = "0 0",
                OffsetMin = $"{x} {y}",
                OffsetMax = $"{x + w} {y + h}",
            };

        private void Render(BasePlayer player, UiState state)
        {
            state.SeenVersion = SnapshotVersion;

            var c = new CuiElementContainer();

            var win = c.Add(new CuiPanel
            {
                Image = { Color = ColWindow },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-520 -300", OffsetMax = "520 300" },
                CursorEnabled = true,
            }, "Overlay", UiRoot);

            DrawTitleBar(c, win, state);
            DrawTabs(c, win, state);

            var content = AddPanel(c, win, 12, 12, 1016, 476, ColPanel);

            if (_snapshot == null)
            {
                AddText(c, content, "GatherRatesPlus is not loaded, or its snapshot could not be read.",
                    0, 220, 1016, 30, 14, ColRed, TextAnchor.MiddleCenter);
            }
            else
            {
                switch (state.Tab)
                {
                    case "Rulesets": DrawRulesetsTab(c, content, state); break;
                    case "Rates": DrawRatesTab(c, content, state); break;
                    case "Dispensers": DrawDispensersTab(c, content, state); break;
                    case "Zones": DrawZonesTab(c, content, state); break;
                    case "Speed": DrawSpeedTab(c, content, state); break;
                    case "Players": DrawPlayersTab(c, content, state); break;
                }
            }

            if (state.ModalItem != null)
                DrawShadowModal(c, win, state);

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.AddUi(player, c);
        }

        // Lists the dispensers that override one item, so the admin can jump straight to whichever
        // one is actually deciding the rate rather than hunting for it in a 229-entry list.
        private void DrawShadowModal(CuiElementContainer c, string win, UiState state)
        {
            var ruleset = GetRuleset(state.Ruleset);
            var item = state.ModalItem;

            // Full-window shade, and it is a button so a click anywhere outside dismisses.
            AddButton(c, win, "", "grpui.modalclose", 0, 0, 1040, 600, ColShade, ColClear, 1);

            var panel = AddPanel(c, win, 300, 130, 440, 340, ColWindow);
            AddPanel(c, panel, 0, 300, 440, 40, ColBar);
            AddText(c, panel, $"Dispensers overriding {DisplayName(item)}", 12, 300, 416, 40, 13, ColText);

            var overriding = new List<KeyValuePair<string, JToken>>();
            var dispensers = ruleset?["DispenserRateOverrides"] as JObject;

            if (dispensers != null)
            {
                foreach (var property in dispensers.Properties().OrderBy(x => x.Name))
                {
                    var items = property.Value as JObject;
                    var token = items?[item];
                    if (token != null)
                        overriding.Add(new KeyValuePair<string, JToken>(property.Name, token));
                }
            }

            AddText(c, panel, "Click one to open it on the Dispensers tab.", 12, 272, 416, 22, 10, ColTextDim);

            var y = 238f;
            foreach (var entry in overriding.Take(8))
            {
                AddButton(c, panel, entry.Key, $"grpui.gotodisp {entry.Key} {item}", 12, y, 320, 26,
                    ColCard, ColText, 10);
                AddText(c, panel, SpecInputText(entry.Value), 340, y, 88, 26, 11, ColAmber,
                    TextAnchor.MiddleCenter);
                y -= 30;
            }

            if (overriding.Count > 8)
                AddText(c, panel, $"+{overriding.Count - 8} more - narrow it on the Dispensers tab",
                    12, y, 416, 22, 10, ColTextDim);

            AddButton(c, panel, "Cancel", "grpui.modalclose", 340, 12, 88, 26, ColNeutral, ColText, 11);
        }

        private void DrawTitleBar(CuiElementContainer c, string win, UiState state)
        {
            var bar = AddPanel(c, win, 0, 556, 1040, 44, ColBar);
            AddText(c, bar, "Gather Rates Plus", 16, 0, 400, 44, 17, ColText);

            // Anyone else with the editor open right now, so you know you are not alone in here.
            var others = _states.Count - 1;
            if (others > 0)
                AddText(c, bar, $"{others} other editor{(others == 1 ? "" : "s")} open", 380, 0, 260, 44, 11, ColAmber);

            if (state.Notice != null && Time.realtimeSinceStartup < state.NoticeUntil)
                AddText(c, bar, state.Notice, 600, 0, 340, 44, 11, ColAccent, TextAnchor.MiddleRight);

            AddButton(c, bar, "X", "grpui.close", 996, 8, 28, 28, ColRed);
        }

        private void DrawTabs(CuiElementContainer c, string win, UiState state)
        {
            var x = 12f;
            foreach (var tab in Tabs)
            {
                var active = state.Tab == tab;
                var label = tab;

                if (tab == "Zones" && !ZonesAvailable)
                    label = "Zones (!)";

                AddButton(c, win, label, $"grpui.tab {tab}", x, 500, 150, 34,
                    active ? ColAccent : ColCard, active ? ColText : ColTextDim);
                x += 158;
            }
        }

        private void DrawRulesetHeader(CuiElementContainer c, string content, UiState state, float y)
        {
            AddText(c, content, "Ruleset", 14, y, 80, 26, 12, ColTextDim);

            var x = 90f;
            foreach (var name in RulesetNames())
            {
                var active = string.Equals(name, state.Ruleset, StringComparison.OrdinalIgnoreCase);
                AddButton(c, content, name, $"grpui.selectruleset {name}", x, y, 110, 26,
                    active ? ColAccent : ColCard, active ? ColText : ColTextDim, 11);
                x += 116;

                if (x > 900)
                    break;
            }
        }

        private void DrawRulesetsTab(CuiElementContainer c, string content, UiState state)
        {
            AddText(c, content, "Rulesets are matched by permission. The last one in the list that a player holds wins; "
                                + "everyone else falls back to 'default'.", 14, 440, 990, 26, 12, ColTextDim);

            AddText(c, content, "Name", 20, 408, 200, 24, 11, ColTextDim);
            AddText(c, content, "Base rate", 240, 408, 100, 24, 11, ColTextDim);
            AddText(c, content, "Overrides", 360, 408, 160, 24, 11, ColTextDim);
            AddText(c, content, "Permission", 540, 408, 320, 24, 11, ColTextDim);

            var y = 378f;
            var index = 0;

            foreach (var name in RulesetNames())
            {
                var ruleset = GetRuleset(name);
                if (ruleset == null)
                    continue;

                var row = AddPanel(c, content, 14, y, 990, 30, index++ % 2 == 0 ? ColCard : ColCardAlt);
                AddText(c, row, name, 8, 0, 220, 30, 12, ColText);

                AddInput(c, row, ruleset.Value<float>("DefaultRate").ToString(CultureInfo.InvariantCulture),
                    $"grpui.setbaserate {name}", 226, 3, 90, 24);

                var itemCount = (ruleset["ItemRateOverrides"] as JObject)?.Count ?? 0;
                var dispCount = (ruleset["DispenserRateOverrides"] as JObject)?.Count ?? 0;
                AddText(c, row, $"{itemCount} item / {dispCount} dispenser", 346, 0, 200, 30, 11, ColTextDim);

                var isDefault = string.Equals(name, "default", StringComparison.OrdinalIgnoreCase);
                AddText(c, row, isDefault ? "(applies to everyone)" : "gatherratesplus.ruleset." + name,
                    526, 0, 340, 30, 11, isDefault ? ColTextDim : ColAccent);

                AddButton(c, row, "Edit rates", $"grpui.editrates {name}", 700, 3, 84, 24, ColNeutral, ColText, 10);

                var rowArmed = string.Equals(state.PendingReset, name, StringComparison.OrdinalIgnoreCase)
                               && Time.realtimeSinceStartup < state.PendingUntil;

                AddButton(c, row, rowArmed ? "Confirm?" : "Reset", $"grpui.resetruleset {name}",
                    790, 3, 84, 24, rowArmed ? ColRed : ColNeutral, ColText, 10);

                if (!isDefault)
                    AddButton(c, row, "Delete", $"grpui.delruleset {name}", 882, 3, 88, 24, ColRed, ColText, 10);

                y -= 34;

                if (y < 90)
                    break;
            }

            AddText(c, content, "New ruleset", 14, 50, 100, 26, 11, ColTextDim);
            AddInput(c, content, "", "grpui.addruleset", 120, 50, 240, 26);
            AddText(c, content, "Type a name then press enter. Creates it at x1 - set the rate above.",
                    374, 50, 440, 26, 11, ColTextDim);

            // Destructive, so it takes two clicks: the first arms it for a few seconds.
            var armed = state.PendingReset == "*" && Time.realtimeSinceStartup < state.PendingUntil;
            AddButton(c, content, armed ? "Click again to confirm" : "Reset everything",
                "grpui.resetall", 818, 50, 186, 26, armed ? ColRed : ColNeutral, ColText, armed ? 10 : 11);

            AddText(c, content, armed
                    ? "Clears every rate, ruleset, zone and speed setting. A backup is written first."
                    : "Clears all rates, rulesets, zones and speed settings back to vanilla.",
                14, 20, 990, 22, 10, armed ? ColAmber : ColTextDim);
        }

        private void DrawRatesTab(CuiElementContainer c, string content, UiState state)
        {
            DrawRulesetHeader(c, content, state, 440);

            var ruleset = GetRuleset(state.Ruleset);
            if (ruleset == null)
            {
                AddText(c, content, "Select a ruleset.", 14, 300, 400, 30, 13, ColTextDim);
                return;
            }

            AddText(c, content, "Base rate (everything without an override)", 14, 404, 320, 26, 11, ColTextDim);
            AddInput(c, content, ruleset.Value<float>("DefaultRate").ToString(CultureInfo.InvariantCulture),
                $"grpui.setbaserate {state.Ruleset}", 340, 404, 90, 26);

            AddText(c, content, "Search", 452, 404, 50, 26, 11, ColTextDim);
            AddInput(c, content, state.ItemSearch, "grpui.itemsearch", 504, 404, 210, 26);
            AddButton(c, content, "Clear", "grpui.itemsearch", 720, 404, 56, 26, ColNeutral, ColText, 10);
            AddButton(c, content, "Overrides", "grpui.itemsearch @overrides", 782, 404, 72, 26, ColNeutral, ColText, 10);
            AddButton(c, content, state.ShowAllItems ? "All items" : "Gatherable", "grpui.toggleallitems",
                860, 404, 144, 26, state.ShowAllItems ? ColAmber : ColGreen, ColText, 10);

            var matches = FilterItems(state, ruleset);
            DrawPager(c, content, state, matches.Count);

            // Quick-set: applies to every item currently matching the search, not just this page.
            AddText(c, content, "Type  2  to multiply,  =5  for a fixed amount,  =5-10  for a random range.",
                14, 316, 620, 20, 10, ColTextDim);

            AddText(c, content, $"Set all {matches.Count} matching:", 14, 370, 150, 22, 10, ColTextDim);

            var px = 168f;
            for (var preset = 1; preset <= 10; preset++)
            {
                AddButton(c, content, preset.ToString(), $"grpui.bulkrate {state.Ruleset} {preset}",
                    px, 370, 30, 22, ColCard, ColText, 10);
                px += 34;
            }

            AddButton(c, content, "reset", $"grpui.bulkclear {state.Ruleset}", px, 370, 48, 22, ColNeutral, ColText, 9);

            AddText(c, content, "Item", 14 + RItemX, 292, RItemW, 20, 10, ColTextDim);
            AddText(c, content, "Short name", 14 + RShortX, 292, RShortW, 20, 10, ColTextDim);
            AddText(c, content, "Rate", 14 + RRateX, 292, RRateW, 20, 10, ColTextDim);
            AddText(c, content, "Chance", 14 + RChanceX, 292, RChanceW, 20, 10, ColTextDim);
            AddText(c, content, "Gathered from Dispenser(s)...", 14 + RSrcX, 292, RSrcW, 20, 10, ColTextDim);

            var bonusNames = BonusItemNames();

            var y = 264f;
            var index = 0;

            foreach (var shortName in matches.Skip(state.Page * PageSize).Take(PageSize))
            {
                var row = AddPanel(c, content, 14, y, 990, 26, index++ % 2 == 0 ? ColCard : ColCardAlt);
                var overridden = HasItemOverride(ruleset, shortName);

                AddText(c, row, DisplayName(shortName), RItemX, 0, RItemW, 26, 11, ColText);
                AddText(c, row, shortName, RShortX, 0, RShortW, 26, 10, ColTextDim);

                var token = (ruleset?["ItemRateOverrides"] as JObject)?[shortName];
                var shown = SpecInputText(token)
                            ?? (ruleset?.Value<float>("DefaultRate") ?? 1f).ToString(CultureInfo.InvariantCulture);

                AddInput(c, row, shown, $"grpui.setitemrate {state.Ruleset} {shortName}",
                    RRateX, 2, RRateW, 22, bg: overridden ? ColField : ColPanel);

                // Chance only means anything for bonus-roll items; every other row leaves it blank.
                if (bonusNames.Contains(shortName))
                {
                    var itemChance = BonusItemChance(shortName);
                    var perDispenser = BonusChanceOverrideCount(shortName);

                    // Mirrors the Rate box: the inherited value is shown dimmed so you can see what
                    // applies without it looking like something you set. Amber when a dispenser
                    // carries its own chance and this value is not the final word.
                    var text = itemChance?.ToString("0.#", CultureInfo.InvariantCulture)
                               ?? VanillaChanceText(shortName);

                    var colour = itemChance != null
                        ? (perDispenser > 0 ? ColAmber : ColText)
                        : (perDispenser > 0 ? ColAmber : ColTextDim);

                    AddInput(c, row, text, $"grpui.bonusitemchance {shortName}",
                        RChanceX, 2, RChanceW, 22,
                        bg: itemChance != null ? ColField : ColPanel, textColor: colour);
                }

                AddText(c, row, SourceSummary(shortName), RSrcX, 0, RSrcW, 26, 10, ColTextDim);

                var mode = SpecMode(token);
                var shadows = ShadowCount(ruleset, shortName);

                // A chance override alone still needs a way to be cleared.
                var clearable = overridden || BonusItemChance(shortName) != null;

                string tag;
                string tagColor;

                if (shadows > 0)
                {
                    // A dispenser entry beats whatever is shown here, so say so instead of implying
                    // the displayed number is what players will get - and let the admin click
                    // through to the dispensers responsible.
                    AddButton(c, row, $"{shadows} dispenser override{(shadows == 1 ? "" : "s")}",
                        $"grpui.shadowlist {shortName}", RTagX, 2, RTagW, 22, ColCardAlt, ColAmber, 10);

                    if (clearable)
                        AddButton(c, row, "Clear", $"grpui.clearitemrate {state.Ruleset} {shortName}",
                            RClearX, 2, RClearW, 22, ColNeutral, ColText, 10);

                    y -= 30;
                    continue;
                }

                if (overridden)
                {
                    tag = mode;
                    tagColor = mode == "multiply" ? ColGreen : ColAmber;
                }
                else
                {
                    tag = "inherits base";
                    tagColor = ColTextDim;
                }

                AddText(c, row, tag, RTagX, 0, RTagW, 26, 10, tagColor);

                if (clearable)
                    AddButton(c, row, "Clear", $"grpui.clearitemrate {state.Ruleset} {shortName}",
                        RClearX, 2, RClearW, 22, ColNeutral, ColText, 10);

                y -= 30;
            }
        }

        private List<string> FilterItems(UiState state, JObject ruleset)
        {
            if (state.ItemSearch == "@overrides")
            {
                var overrides = ruleset?["ItemRateOverrides"] as JObject;
                return overrides?.Properties().Select(x => x.Name).OrderBy(x => x).ToList() ?? new List<string>();
            }

            var search = state.ItemSearch ?? "";

            // ItemManager.itemList is every item in the game -- cars, rifles, doors. Unless the admin
            // explicitly asks for all of them, only offer things a dispenser can actually produce.
            var source = state.ShowAllItems
                ? (IEnumerable<KeyValuePair<string, string>>)_items
                : _gatherable.Select(x => new KeyValuePair<string, string>(x.Key, DisplayName(x.Key)));

            return source
                .Where(x => search.Length == 0
                            || x.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                            || (x.Value ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Key)
                .Select(x => x.Key)
                .ToList();
        }

        // "where the item is" -- how many dispensers yield it, and the first couple by name.
        private string SourceSummary(string shortName)
        {
            List<string> sources;
            if (!_gatherable.TryGetValue(shortName, out sources) || sources.Count == 0)
                return "not seen gathering";

            var names = string.Join(", ", sources.Take(2).ToArray());
            return sources.Count <= 2 ? names : $"{names} +{sources.Count - 2}";
        }

        private void DrawDispensersTab(CuiElementContainer c, string content, UiState state)
        {
            DrawRulesetHeader(c, content, state, 440);

            var ruleset = GetRuleset(state.Ruleset);

            AddText(c, content, "Per-entity rates beat per-item rates. This is how you scale one source "
                                + "(heli wreckage, a specific node) without touching the rest of the map.",
                    14, 408, 990, 24, 11, ColTextDim);

            AddText(c, content, "Search", 14, 378, 60, 26, 11, ColTextDim);
            AddInput(c, content, state.DispenserSearch, "grpui.dispsearch", 78, 378, 260, 26);

            var matches = _dispensers
                .Where(x => string.IsNullOrEmpty(state.DispenserSearch)
                            || x.IndexOf(state.DispenserSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x)
                .ToList();

            AddText(c, content, $"{matches.Count} dispenser(s)", 348, 378, 160, 26, 11, ColTextDim);

            // Left: dispenser list. Right: the overrides for whichever one is selected.
            const int dispPerPage = 10;
            var dispPages = Mathf.Max(1, Mathf.CeilToInt(matches.Count / (float)dispPerPage));
            state.DispPage = Mathf.Clamp(state.DispPage, 0, dispPages - 1);

            var y = 344f;
            foreach (var prefab in matches.Skip(state.DispPage * dispPerPage).Take(dispPerPage))
            {
                var selected = prefab == state.SelectedDispenser;
                AddButton(c, content, prefab, $"grpui.seldisp {prefab}", 14, y, 330, 26,
                    selected ? ColAccent : ColCard, selected ? ColText : ColTextDim, 10);
                y -= 30;
            }

            AddButton(c, content, "<", "grpui.disppage -1", 14, 14, 40, 24, ColNeutral, ColText, 11);
            AddText(c, content, $"{state.DispPage + 1} / {dispPages}", 58, 14, 242, 24, 10, ColTextDim,
                TextAnchor.MiddleCenter);
            AddButton(c, content, ">", "grpui.disppage 1", 304, 14, 40, 24, ColNeutral, ColText, 11);

            if (string.IsNullOrEmpty(state.SelectedDispenser))
            {
                AddText(c, content, "Select a dispenser on the left.", 360, 300, 400, 30, 12, ColTextDim);
                return;
            }

            AddText(c, content, state.SelectedDispenser, 360, 344, 500, 26, 13, ColAccent);

            var overrides = DispenserOverrides(ruleset, state.SelectedDispenser);

            List<string> known;
            _dispenserItems.TryGetValue(state.SelectedDispenser, out known);

            if (known == null || known.Count == 0)
            {
                // Runtime-filled dispensers (heli and Bradley wreckage) declare nothing on the prefab,
                // so there is no list to show until someone has gathered from one.
                AddText(c, content, "No confirmed output yet - this dispenser is filled at runtime "
                                    + "(heli and Bradley wreckage do this).\nGather from one once and its "
                                    + "items appear here, or add a short name below if you already know it.",
                    360, 250, 644, 60, 11, ColAmber);

                AddText(c, content, "Add item", 360, 200, 70, 26, 11, ColTextDim);
                AddInput(c, content, "", $"grpui.adddisprate {state.Ruleset} {state.SelectedDispenser}",
                    436, 200, 200, 26);
                return;
            }

            AddText(c, content, "Item", 360 + ColItemX, 316, ColItemW, 20, 10, ColTextDim);
            AddText(c, content, "Short name", 360 + ColShortX, 316, ColShortW, 20, 10, ColTextDim);
            AddText(c, content, "Rate / Amt", 360 + ColRateX, 316, ColRateW, 20, 10, ColTextDim);
            AddText(c, content, "Chance", 360 + ColChanceX, 316, ColChanceW, 20, 10, ColTextDim);
            AddText(c, content, "Source", 360 + ColSrcX, 316, ColSrcW, 20, 10, ColTextDim);

            const int itemsPerPage = 8;

            // Include anything already overridden for this dispenser even if it is not in the known
            // output list, otherwise a stale or mistaken entry (wood on a mushroom) sits in the config
            // invisible and unfixable.
            var ordered = known.ToList();
            if (overrides != null)
            {
                foreach (var extra in overrides.Properties().Select(x => x.Name))
                {
                    if (!ordered.Contains(extra))
                        ordered.Add(extra);
                }
            }

            // Bonus-roll items (worm, grub) come from the RandomItemDispenser table rather than the
            // dispenser's contents, so they are grouped after the normal items rather than sorted in
            // among them. Both groups sort by display name, which is what reads naturally:
            // Sunflower, Sunflower Seed, then Grub, Worm.
            var bonusTable = BonusTable(state.SelectedDispenser);
            var bonusItems = (bonusTable?["entries"] as JArray)?.OfType<JObject>().ToList()
                             ?? new List<JObject>();

            var bonusNames = new HashSet<string>(bonusItems.Select(x => x.Value<string>("item")));

            ordered = ordered
                .Where(x => !bonusNames.Contains(x))
                .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
                .Concat(bonusItems
                    .Select(x => x.Value<string>("item"))
                    .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var itemPages = Mathf.Max(1, Mathf.CeilToInt(ordered.Count / (float)itemsPerPage));
            state.DispItemPage = Mathf.Clamp(state.DispItemPage, 0, itemPages - 1);

            var rowY = 290f;
            var index = 0;

            foreach (var item in ordered.Skip(state.DispItemPage * itemsPerPage).Take(itemsPerPage))
            {
                var row = AddPanel(c, content, 360, rowY, 644, 26, index++ % 2 == 0 ? ColCard : ColCardAlt);

                // Three places a rate can come from, most specific first - same order the plugin
                // itself resolves them in, so what is shown is what will actually apply.
                var dispToken = overrides?[item];
                var hasDispOverride = dispToken != null;
                var hasItemRule = !hasDispOverride && HasItemOverride(ruleset, item);

                var bonusEntry = bonusItems.FirstOrDefault(x => x.Value<string>("item") == item);
                if (bonusEntry != null)
                {
                    DrawBonusRow(c, content, state, ruleset, item, bonusEntry,
                        rowY, index++ % 2 == 0 ? ColCard : ColCardAlt);
                    rowY -= 30;
                    continue;
                }

                var yielded = known.Contains(item);

                string shown;
                string tag;
                string tagColor;

                if (hasDispOverride)
                {
                    shown = SpecInputText(dispToken);
                    tag = SpecMode(dispToken) == "multiply" ? "override" : SpecMode(dispToken);
                    tagColor = SpecMode(dispToken) == "multiply" ? ColGreen : ColAmber;
                }
                else if (hasItemRule)
                {
                    shown = SpecInputText((ruleset?["ItemRateOverrides"] as JObject)?[item]);
                    tag = "item rule";
                    tagColor = ColAccent;
                }
                else
                {
                    shown = (ruleset?.Value<float>("DefaultRate") ?? 1f).ToString(CultureInfo.InvariantCulture);
                    tag = "base rate";
                    tagColor = ColTextDim;
                }

                // Overridden but never actually produced here - dead config, flag it plainly.
                if (!yielded)
                {
                    tag = "never yielded";
                    tagColor = ColRed;
                }

                AddText(c, row, DisplayName(item), ColItemX, 0, ColItemW, 26, 11, yielded ? ColText : ColRed);
                AddText(c, row, item, ColShortX, 0, ColShortW, 26, 10, ColTextDim);

                AddInput(c, row, shown,
                    $"grpui.setdisprate {state.Ruleset} {state.SelectedDispenser} {item}",
                    ColRateX, 2, ColRateW, 22, bg: hasDispOverride ? ColField : ColPanel);

                AddText(c, row, tag, ColSrcX, 0, ColSrcW, 26, 10, tagColor);

                if (hasDispOverride)
                    AddButton(c, row, "Reset",
                        $"grpui.cleardisprate {state.Ruleset} {state.SelectedDispenser} {item}",
                        susp: 0, x: ColResetX, y: 2, w: ColResetW, h: 22);

                rowY -= 30;
            }

            if (itemPages > 1)
            {
                AddButton(c, content, "<", "grpui.dispitempage -1", 360, 46, 40, 22, ColNeutral, ColText, 11);
                AddText(c, content, $"{state.DispItemPage + 1} / {itemPages}   ({known.Count} items)",
                    404, 46, 200, 22, 10, ColTextDim, TextAnchor.MiddleCenter);
                AddButton(c, content, ">", "grpui.dispitempage 1", 608, 46, 40, 22, ColNeutral, ColText, 11);
            }

            AddText(c, content, "Rates here beat the item rules on the Rates tab, for this dispenser only. "
                                + "Use  2  /  =5  /  =5-10.", 360, 14, 644, 22, 10, ColTextDim);
        }

        // A bonus row shows the effective Amount and Chance, plus the shared OnlyAwardOne flag.
        // Rates only ever change Amount; Chance is vanilla unless an explicit percentage is set.
        private void DrawBonusRow(CuiElementContainer c, string content, UiState state, JObject ruleset,
            string item, JObject vanilla, float rowY, string rowColor)
        {
            var prefab = state.SelectedDispenser;
            var row = AddPanel(c, content, 360, rowY, 644, 26, rowColor);

            var over = BonusItemOverride(prefab, item);
            var amountOverride = over?["Amount"];
            var chanceOverride = over?["ChancePercent"];

            var vanillaAmount = vanilla.Value<int>("amount");
            var vanillaChance = vanilla.Value<float>("chance") * 100f;

            string amountText;
            string tag;
            string tagColor;

            if (amountOverride != null)
            {
                amountText = amountOverride.Value<int>().ToString(CultureInfo.InvariantCulture);
                tag = "fixed";
                tagColor = ColAmber;
            }
            else
            {
                // Same resolution the plugin uses at award time.
                var token = (ruleset?["DispenserRateOverrides"] as JObject)?[prefab] is JObject dispObj
                    ? dispObj[item]
                    : null;
                token = token ?? (ruleset?["ItemRateOverrides"] as JObject)?[item];

                var rate = token != null ? token.Type == JTokenType.Object ? 1f : token.Value<float>()
                    : ruleset?.Value<float>("DefaultRate") ?? 1f;

                amountText = Mathf.Max(1, Mathf.RoundToInt(vanillaAmount * rate))
                    .ToString(CultureInfo.InvariantCulture);
                tag = token != null ? "item rule" : "base rate";
                tagColor = token != null ? ColAccent : ColTextDim;
            }

            var chanceText = (chanceOverride?.Value<float>() ?? vanillaChance)
                .ToString("0.#", CultureInfo.InvariantCulture) + "%";

            AddText(c, row, DisplayName(item), ColItemX, 0, ColItemW, 26, 11, ColGreen);
            AddText(c, row, item, ColShortX, 0, ColShortW, 26, 10, ColTextDim);

            AddInput(c, row, amountText, $"grpui.bonusamount {prefab} {item}",
                ColRateX, 2, ColRateW, 22, bg: amountOverride != null ? ColField : ColPanel);

            AddInput(c, row, chanceText.TrimEnd('%'), $"grpui.bonuschance {prefab} {item}",
                ColChanceX, 2, ColChanceW, 22, bg: chanceOverride != null ? ColField : ColPanel);

            AddText(c, row, chanceOverride != null ? "set" : tag, ColSrcX, 0, ColSrcW, 26, 10,
                chanceOverride != null ? ColAmber : tagColor);

            bool onlyOverridden;
            var onlyAwardOne = BonusOnlyAwardOne(prefab, out onlyOverridden);

            // Table-level flag: shown on every bonus row, and toggling any one flips them all.
            AddButton(c, row, onlyAwardOne ? "[x] one only" : "[  ] one only",
                $"grpui.bonusonly {prefab} {(!onlyAwardOne).ToString().ToLowerInvariant()}",
                ColOnlyX, 2, ColOnlyW, 22, onlyOverridden ? ColAmber : ColNeutral, ColText, 9);

            if (amountOverride != null || chanceOverride != null)
                AddButton(c, row, "Reset", $"grpui.bonusreset {prefab} {item}",
                    susp: 0, x: ColResetX, y: 2, w: ColResetW, h: 22);
        }

        private void DrawZonesTab(CuiElementContainer c, string content, UiState state)
        {
            if (!ZonesAvailable)
            {
                AddPanel(c, content, 200, 240, 616, 90, ColCard);
                AddText(c, content, "ZoneManager plugin missing", 200, 290, 616, 30, 16, ColAmber, TextAnchor.MiddleCenter);
                AddText(c, content, "Gather zones are disabled. Install and load ZoneManager, then reopen this tab -\n"
                                    + "existing zone definitions are kept in the config and will re-register automatically.",
                    200, 240, 616, 50, 11, ColTextDim, TextAnchor.MiddleCenter);
                return;
            }

            var zones = _snapshot["zones"] as JArray ?? new JArray();

            AddText(c, content, $"{zones.Count} zone(s). A zone applies its ruleset to anyone gathering inside it.",
                14, 440, 700, 24, 11, ColTextDim);

            AddText(c, content, "Zone", 20, 412, 220, 22, 11, ColTextDim);
            AddText(c, content, "Ruleset", 250, 412, 120, 22, 11, ColTextDim);
            AddText(c, content, "Radius", 380, 412, 80, 22, 11, ColTextDim);
            AddText(c, content, "On", 470, 412, 60, 22, 11, ColTextDim);
            AddText(c, content, "Overrides perm", 530, 412, 110, 22, 11, ColTextDim);
            AddText(c, content, "Needs perm", 650, 412, 110, 22, 11, ColTextDim);

            var y = 382f;
            var index = 0;

            foreach (var zone in zones.OfType<JObject>())
            {
                var id = zone.Value<string>("ZoneId");
                var row = AddPanel(c, content, 14, y, 990, 28, index++ % 2 == 0 ? ColCard : ColCardAlt);

                AddText(c, row, id, 8, 0, 230, 28, 10, ColText);
                AddText(c, row, zone.Value<string>("RulesetName"), 236, 0, 120, 28, 11, ColAccent);

                AddInput(c, row, zone.Value<float>("Radius").ToString(CultureInfo.InvariantCulture),
                    $"grpui.setzone {id} radius", 366, 3, 70, 22);

                AddToggle(c, row, zone.Value<bool>("Enabled"), $"grpui.setzone {id} enabled", 456, 3);
                AddToggle(c, row, zone.Value<bool>("OverridePermission"), $"grpui.setzone {id} override", 546, 3);
                AddToggle(c, row, zone.Value<bool>("RequirePermission"), $"grpui.setzone {id} requireperm", 666, 3);

                AddButton(c, row, "Delete", $"grpui.delzone {id}", 890, 3, 80, 22, ColRed, ColText, 10);

                y -= 32;

                if (y < 110)
                    break;
            }

            AddText(c, content, "Add zone at your position:", 14, 60, 180, 26, 11, ColTextDim);
            AddText(c, content, "ruleset", 200, 60, 50, 26, 10, ColTextDim);
            AddInput(c, content, "", "grpui.addzone", 252, 60, 160, 26);
            AddText(c, content, "type '<ruleset> <radius>' and press enter, e.g. 'vip 120'",
                    422, 60, 560, 26, 10, ColTextDim);
        }

        private void DrawSpeedTab(CuiElementContainer c, string content, UiState state)
        {
            var speed = _snapshot["speed"] as JObject;
            if (speed == null)
                return;

            AddText(c, content, "Speed changes throughput without changing what a single payout is worth. "
                                + "Halving a tick rate doubles output per hour but also doubles fuel burn; "
                                + "doubling the rate multiplier instead makes each unit of fuel go twice as far.",
                14, 424, 990, 40, 11, ColTextDim);

            DrawSpeedRow(c, content, 380, "Mining quarry tick rate", "MiningQuarryTickRate",
                speed.Value<float>("MiningQuarryTickRate"), 5f, "seconds between payouts");

            DrawSpeedRow(c, content, 336, "Excavator tick rate", "ExcavatorTickRate",
                speed.Value<float>("ExcavatorTickRate"), 3f, "seconds between payouts");

            DrawSpeedRow(c, content, 292, "Excavator belt speed", "ExcavatorBeltSpeedMax",
                speed.Value<float>("ExcavatorBeltSpeedMax"), 0.1f, "belt animation / output feel");

            DrawSpeedRow(c, content, 248, "Excavator time to full output", "ExcavatorTimeForFullResources",
                speed.Value<float>("ExcavatorTimeForFullResources"), 120f, "warm-up before full rate");

            var round = _snapshot.Value<bool>("round");
            var row = AddPanel(c, content, 14, 170, 990, 34, ColCard);
            AddText(c, row, "Round multiplied amounts instead of truncating", 10, 0, 420, 34, 12, ColText);
            AddToggle(c, row, round, "grpui.setrounding", 440, 5);
            AddText(c, row, round
                    ? "1.5x on a 1-unit yield gives 2 (recommended)"
                    : "1.5x on a 1-unit yield gives 1 - low yields ignore the multiplier",
                480, 0, 500, 34, 10, round ? ColTextDim : ColAmber);

            AddText(c, content, "Values equal to vanilla are left alone entirely - the plugin only re-arms a timer "
                                + "when you have actually changed it.", 14, 120, 990, 24, 10, ColTextDim);
        }

        private void DrawSpeedRow(CuiElementContainer c, string content, float y, string label,
            string key, float value, float vanilla, string hint)
        {
            var row = AddPanel(c, content, 14, y, 990, 38, ColCard);
            AddText(c, row, label, 10, 0, 300, 38, 12, ColText);
            AddInput(c, row, value.ToString(CultureInfo.InvariantCulture), $"grpui.setspeed {key}", 316, 6, 90, 26);

            var changed = Math.Abs(value - vanilla) > 0.0001f;
            AddText(c, row, changed ? $"vanilla {vanilla}" : "vanilla", 414, 0, 110, 38, 10,
                changed ? ColAmber : ColTextDim);

            if (changed)
                AddButton(c, row, "Reset", $"grpui.setspeed {key} {vanilla.ToString(CultureInfo.InvariantCulture)}",
                    528, 6, 70, 26, ColNeutral, ColText, 10);

            AddText(c, row, hint, 608, 0, 370, 38, 10, ColTextDim);
        }

        private void DrawPlayersTab(CuiElementContainer c, string content, UiState state)
        {
            AddText(c, content, "Grant a ruleset's permission to a player. The last ruleset in the list that a "
                                + "player holds is the one that applies to them.", 14, 440, 800, 24, 11, ColTextDim);

            AddText(c, content, "Search players", 14, 410, 100, 26, 11, ColTextDim);
            AddInput(c, content, state.PlayerSearch, "grpui.playersearch", 120, 410, 260, 26);

            var rulesetNames = RulesetNames().Where(x => !string.Equals(x, "default", StringComparison.OrdinalIgnoreCase)).ToList();

            if (rulesetNames.Count == 0)
            {
                AddText(c, content, "No permission rulesets defined yet - create one on the Rulesets tab.",
                    14, 300, 700, 30, 13, ColAmber);
                return;
            }

            var search = state.PlayerSearch ?? "";
            var players = covalence.Players.Connected
                .Where(x => search.Length == 0 || x.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Name)
                .Take(PageSize)
                .ToList();

            if (players.Count == 0)
            {
                AddText(c, content, "No connected players matched.", 14, 340, 500, 30, 12, ColTextDim);
                return;
            }

            AddText(c, content, "Player", 20, 378, 260, 22, 11, ColTextDim);
            AddText(c, content, "Rulesets held (click to toggle)", 300, 378, 400, 22, 11, ColTextDim);

            var y = 348f;
            var index = 0;

            foreach (var target in players)
            {
                var row = AddPanel(c, content, 14, y, 990, 28, index++ % 2 == 0 ? ColCard : ColCardAlt);
                AddText(c, row, target.Name, 8, 0, 280, 28, 11, ColText);

                var x = 292f;
                foreach (var name in rulesetNames.Take(6))
                {
                    var held = permission.UserHasPermission(target.Id, "gatherratesplus.ruleset." + name);
                    AddButton(c, row, name, $"grpui.toggleperm {target.Id} {name}", x, 3, 106, 22,
                        held ? ColGreen : ColNeutral, ColText, 10);
                    x += 112;
                }

                y -= 32;
            }
        }

        private void DrawPager(CuiElementContainer c, string content, UiState state, int total)
        {
            var pages = Mathf.Max(1, Mathf.CeilToInt(total / (float)PageSize));
            state.Page = Mathf.Clamp(state.Page, 0, pages - 1);

            AddButton(c, content, "<", "grpui.page -1", 820, 292, 34, 20, ColNeutral, ColText, 11);
            AddText(c, content, $"{state.Page + 1} / {pages}   ({total})", 858, 292, 112, 20, 10, ColTextDim, TextAnchor.MiddleCenter);
            AddButton(c, content, ">", "grpui.page 1", 970, 292, 34, 20, ColNeutral, ColText, 11);
        }

        #endregion

        #region Commands

        private static string ArgString(ConsoleSystem.Arg arg, int index) => arg.GetString(index, "") ?? "";

        // Input fields append what the admin typed as the final argument, which may contain spaces.
        private static string ArgTail(ConsoleSystem.Arg arg, int fromIndex)
        {
            var parts = new List<string>();
            for (var i = fromIndex; i < fromIndex + 16; i++)
            {
                var piece = ArgString(arg, i);
                if (string.IsNullOrEmpty(piece))
                    break;

                parts.Add(piece);
            }

            return string.Join(" ", parts.ToArray());
        }

        private static bool TryFloat(string raw, out float value)
            => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        [ConsoleCommand("grpui.tab")]
        private void CmdTab(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var tab = ArgString(arg, 0);
            if (Tabs.Contains(tab))
            {
                state.Tab = tab;
                state.Page = 0;
            }
        });

        [ConsoleCommand("grpui.selectruleset")]
        private void CmdSelectRuleset(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.Ruleset = ArgString(arg, 0);
            state.Page = 0;
        });

        [ConsoleCommand("grpui.editrates")]
        private void CmdEditRates(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.Ruleset = ArgString(arg, 0);
            state.Tab = "Rates";
            state.Page = 0;
        });

        [ConsoleCommand("grpui.itemsearch")]
        private void CmdItemSearch(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.ItemSearch = ArgTail(arg, 0);
            state.Page = 0;
        });

        [ConsoleCommand("grpui.dispsearch")]
        private void CmdDispSearch(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.DispenserSearch = ArgTail(arg, 0);
        });

        [ConsoleCommand("grpui.playersearch")]
        private void CmdPlayerSearch(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.PlayerSearch = ArgTail(arg, 0);
        });

        [ConsoleCommand("grpui.toggleallitems")]
        private void CmdToggleAllItems(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.ShowAllItems = !state.ShowAllItems;
            state.Page = 0;
            Notify(state, state.ShowAllItems
                ? "Showing every item in the game - most cannot be gathered"
                : "Showing gatherable items only");
        });

        [ConsoleCommand("grpui.page")]
        private void CmdPage(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            int delta;
            if (int.TryParse(ArgString(arg, 0), out delta))
                state.Page = Mathf.Max(0, state.Page + delta);
        });

        [ConsoleCommand("grpui.shadowlist")]
        private void CmdShadowList(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var item = ArgString(arg, 0);
            if (!string.IsNullOrEmpty(item))
                state.ModalItem = item;
        });

        [ConsoleCommand("grpui.modalclose")]
        private void CmdModalClose(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.ModalItem = null;
        });

        [ConsoleCommand("grpui.gotodisp")]
        private void CmdGoToDispenser(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var prefab = ArgString(arg, 0);
            if (string.IsNullOrEmpty(prefab))
                return;

            state.ModalItem = null;
            state.Tab = "Dispensers";
            state.SelectedDispenser = prefab;
            state.DispItemPage = 0;

            // Filter the left-hand list to this prefab so it is actually on screen rather than
            // buried somewhere in the paging.
            state.DispenserSearch = prefab;
            state.DispPage = 0;
        });

        [ConsoleCommand("grpui.seldisp")]
        private void CmdSelectDispenser(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            state.SelectedDispenser = ArgString(arg, 0);
            state.DispItemPage = 0;
        });

        [ConsoleCommand("grpui.resetruleset")]
        private void CmdResetRuleset(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var name = ArgString(arg, 0);
            if (string.IsNullOrEmpty(name))
                return;

            if (!string.Equals(state.PendingReset, name, StringComparison.OrdinalIgnoreCase)
                || Time.realtimeSinceStartup >= state.PendingUntil)
            {
                state.PendingReset = name;
                state.PendingUntil = Time.realtimeSinceStartup + 6f;
                Notify(state, $"Click Reset again to clear '{name}'");
                return;
            }

            state.PendingReset = null;
            var ok = GatherRatesPlus.Call("API_ResetRuleset", name) as bool? ?? false;
            Notify(state, ok ? $"'{name}' reset to vanilla (backup written)" : "Could not reset that ruleset");
        });

        [ConsoleCommand("grpui.resetall")]
        private void CmdResetAll(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            if (state.PendingReset != "*" || Time.realtimeSinceStartup >= state.PendingUntil)
            {
                state.PendingReset = "*";
                state.PendingUntil = Time.realtimeSinceStartup + 6f;
                Notify(state, "Click again to clear EVERYTHING");
                return;
            }

            state.PendingReset = null;
            GatherRatesPlus.Call("API_ResetEverything");
            state.Ruleset = "default";
            state.Page = 0;
            Notify(state, "Everything reset to vanilla (backup written)");
        });

        [ConsoleCommand("grpui.addruleset")]
        private void CmdAddRuleset(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var name = ArgTail(arg, 0).Replace(" ", "");
            if (name.Length == 0)
                return;

            var ok = GatherRatesPlus.Call("API_AddRuleset", name, 1f) as bool? ?? false;
            Notify(state, ok ? $"Created ruleset '{name}'" : $"'{name}' already exists");
        });

        [ConsoleCommand("grpui.delruleset")]
        private void CmdDelRuleset(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var name = ArgString(arg, 0);
            var ok = GatherRatesPlus.Call("API_RemoveRuleset", name) as bool? ?? false;

            if (ok && string.Equals(state.Ruleset, name, StringComparison.OrdinalIgnoreCase))
                state.Ruleset = "default";

            Notify(state, ok ? $"Deleted ruleset '{name}'" : "Could not delete that ruleset");
        });

        [ConsoleCommand("grpui.setbaserate")]
        private void CmdSetBaseRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            float rate;
            var ruleset = ArgString(arg, 0);
            if (!TryFloat(ArgString(arg, 1), out rate) || rate < 0f)
            {
                Notify(state, "Enter a number of 0 or more");
                return;
            }

            GatherRatesPlus.Call("API_SetDefaultRate", ruleset, rate);
        });

        [ConsoleCommand("grpui.setitemrate")]
        private void CmdSetItemRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var ruleset = ArgString(arg, 0);
            var item = ArgString(arg, 1);

            var raw = ArgTail(arg, 2);
            var ok = GatherRatesPlus.Call("API_SetItemRate", ruleset, item, raw) as bool? ?? false;
            if (!ok)
                Notify(state, "Use 2 to multiply, =5 for a fixed amount, =5-10 for a range");
        });

        [ConsoleCommand("grpui.clearitemrate")]
        private void CmdClearItemRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var ruleset = ArgString(arg, 0);
            var item = ArgString(arg, 1);

            // The row's Clear button clears everything the row owns - the rate override AND the
            // item-level bonus chance. Per-dispenser overrides are deliberately left alone: they
            // belong to a dispenser, and the override counter links through to them.
            GatherRatesPlus.Call("API_ClearItemRate", ruleset, item);

            if (BonusItemChance(item) != null)
            {
                GatherRatesPlus.Call("API_ClearBonusItemChance", item);
                Notify(state, $"{item}: rate and base chance cleared");
            }

            var perDispenser = BonusChanceOverrideCount(item);
            if (perDispenser > 0)
                Notify(state, $"{perDispenser} dispenser(s) still set their own chance for {item}");
        });

        [ConsoleCommand("grpui.setdisprate")]
        private void CmdSetDispRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var ruleset = ArgString(arg, 0);
            var prefab = ArgString(arg, 1);
            var item = ArgString(arg, 2);

            var raw = ArgTail(arg, 3);
            var ok = GatherRatesPlus.Call("API_SetDispenserRate", ruleset, prefab, item, raw) as bool? ?? false;

            if (!ok)
            {
                Notify(state, "Use 2 to multiply, =5 for a fixed amount, =5-10 for a range");
                return;
            }

            // Fixed and ranged amounts ignore the tool's gather damage, but the node still depletes at
            // the tool's rate - so on a struck dispenser a better tool ends up yielding less overall.
            var toolGathered = GatherRatesPlus.Call("API_IsToolGathered", prefab) as bool? ?? false;
            if (toolGathered && raw.TrimStart().StartsWith("="))
                Notify(state, "Fixed amounts invert tool progression on struck nodes - see /gatherrates");
        });

        [ConsoleCommand("grpui.cleardisprate")]
        private void CmdClearDispRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            GatherRatesPlus.Call("API_ClearDispenserRate", ArgString(arg, 0), ArgString(arg, 1), ArgString(arg, 2));
        });

        [ConsoleCommand("grpui.adddisprate")]
        private void CmdAddDispRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var ruleset = ArgString(arg, 0);
            var prefab = ArgString(arg, 1);
            var item = ArgTail(arg, 2).Replace(" ", "");

            if (item.Length == 0)
                return;

            if (ItemManager.FindItemDefinition(item) == null)
            {
                Notify(state, $"'{item}' is not an item short name");
                return;
            }

            // If we know what this dispenser yields, refuse anything else outright. A multiplier on an
            // item the dispenser never drops silently does nothing, which is worse than an error.
            List<string> knownItems;
            if (_dispenserItems.TryGetValue(prefab, out knownItems) && knownItems.Count > 0
                && !knownItems.Contains(item))
            {
                Notify(state, $"{prefab} never yields {item} - a rate on it would do nothing");
                return;
            }

            // Seeded at the ruleset's base rate so the row appears and can then be edited in place.
            var ruleObj = GetRuleset(ruleset);
            var seed = (ruleObj?.Value<float>("DefaultRate") ?? 1f).ToString(CultureInfo.InvariantCulture);
            GatherRatesPlus.Call("API_SetDispenserRate", ruleset, prefab, item, seed);

            // Reached only when the dispenser has no known output yet (runtime-filled wreckage), so
            // this is a best-guess entry rather than a verified one.
            if (knownItems == null || knownItems.Count == 0)
                Notify(state, $"Added {item} - {prefab} has no confirmed output list yet");
        });

        [ConsoleCommand("grpui.disppage")]
        private void CmdDispPage(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            int delta;
            if (int.TryParse(ArgString(arg, 0), out delta))
                state.DispPage = Mathf.Max(0, state.DispPage + delta);
        });

        [ConsoleCommand("grpui.dispitempage")]
        private void CmdDispItemPage(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            int delta;
            if (int.TryParse(ArgString(arg, 0), out delta))
                state.DispItemPage = Mathf.Max(0, state.DispItemPage + delta);
        });

        [ConsoleCommand("grpui.bulkrate")]
        private void CmdBulkRate(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var rulesetName = ArgString(arg, 0);
            var raw = ArgString(arg, 1);

            var ruleset = GetRuleset(rulesetName);
            var matches = FilterItems(state, ruleset);

            if (matches.Count == 0)
            {
                Notify(state, "Nothing matches the current search");
                return;
            }

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(matches);
            var changed = GatherRatesPlus.Call("API_SetItemRatesBulk", rulesetName, json, raw) as int? ?? 0;
            Notify(state, $"Set {changed} item(s) to {raw}");
        });

        [ConsoleCommand("grpui.bulkclear")]
        private void CmdBulkClear(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var rulesetName = ArgString(arg, 0);
            var ruleset = GetRuleset(rulesetName);
            var matches = FilterItems(state, ruleset);

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(matches);
            var changed = GatherRatesPlus.Call("API_ClearItemRatesBulk", rulesetName, json) as int? ?? 0;
            Notify(state, $"Cleared {changed} override(s) - back to the base rate");
        });

        [ConsoleCommand("grpui.bonusitemchance")]
        private void CmdBonusItemChance(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var item = ArgString(arg, 0);
            var raw = ArgTail(arg, 1).TrimEnd('%').Trim();

            if (raw.Length == 0)
            {
                GatherRatesPlus.Call("API_ClearBonusItemChance", item);
                Notify(state, $"{item}: back to each prefab's own chance");
                return;
            }

            float percent;
            if (!TryFloat(raw, out percent) || percent < 0f || percent > 100f)
            {
                Notify(state, "Enter a percentage between 0 and 100, or clear the box to reset");
                return;
            }

            GatherRatesPlus.Call("API_SetBonusItemChance", item, percent);
            Notify(state, $"{item}: {percent}% everywhere it rolls");
        });

        [ConsoleCommand("grpui.bonusamount")]
        private void CmdBonusAmount(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            float value;
            var prefab = ArgString(arg, 0);
            var item = ArgString(arg, 1);

            if (!TryFloat(ArgString(arg, 2), out value) || value < 0f)
            {
                Notify(state, "Enter a whole number of 0 or more");
                return;
            }

            GatherRatesPlus.Call("API_SetBonusAmount", prefab, item, Mathf.RoundToInt(value));
            Notify(state, $"{item} amount fixed at {Mathf.RoundToInt(value)} - the rate no longer applies");
        });

        [ConsoleCommand("grpui.bonuschance")]
        private void CmdBonusChance(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            float value;
            var prefab = ArgString(arg, 0);
            var item = ArgString(arg, 1);

            if (!TryFloat(ArgString(arg, 2).TrimEnd('%'), out value) || value < 0f || value > 100f)
            {
                Notify(state, "Enter a percentage between 0 and 100");
                return;
            }

            GatherRatesPlus.Call("API_SetBonusChance", prefab, item, value);
        });

        [ConsoleCommand("grpui.bonusreset")]
        private void CmdBonusReset(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            GatherRatesPlus.Call("API_ClearBonus", ArgString(arg, 0), ArgString(arg, 1));
        });

        [ConsoleCommand("grpui.bonusonly")]
        private void CmdBonusOnly(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            bool value;
            var prefab = ArgString(arg, 0);
            if (!bool.TryParse(ArgString(arg, 1), out value))
                return;

            // Table-level, so this flips every bonus item on this dispenser at once.
            GatherRatesPlus.Call("API_SetBonusOnlyAwardOne", prefab, value);
            Notify(state, value
                ? "Only one bonus item can drop per gather"
                : "Every bonus item rolls independently");
        });

        [ConsoleCommand("grpui.setspeed")]
        private void CmdSetSpeed(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            float value;
            var key = ArgString(arg, 0);

            if (!TryFloat(ArgString(arg, 1), out value) || value <= 0f)
            {
                Notify(state, "Enter a number greater than 0");
                return;
            }

            GatherRatesPlus.Call("API_SetSpeed", key, value);
        });

        [ConsoleCommand("grpui.setrounding")]
        private void CmdSetRounding(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            bool value;
            if (bool.TryParse(ArgString(arg, 0), out value))
                GatherRatesPlus.Call("API_SetRounding", value);
        });

        [ConsoleCommand("grpui.addzone")]
        private void CmdAddZone(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var ruleset = ArgString(arg, 0);
            float radius;

            if (ruleset.Length == 0 || !TryFloat(ArgString(arg, 1), out radius) || radius <= 0f)
            {
                Notify(state, "Type '<ruleset> <radius>', e.g. 'vip 120'");
                return;
            }

            var pos = player.transform.position;
            var zoneId = GatherRatesPlus.Call("API_AddZone", ruleset, pos.x, pos.y, pos.z, radius) as string;
            Notify(state, zoneId == null ? "Could not create the zone" : $"Created {zoneId}");
        });

        [ConsoleCommand("grpui.delzone")]
        private void CmdDelZone(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            GatherRatesPlus.Call("API_RemoveZone", ArgString(arg, 0));
        });

        [ConsoleCommand("grpui.setzone")]
        private void CmdSetZone(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var zoneId = ArgString(arg, 0);
            var field = ArgString(arg, 1);
            var value = ArgTail(arg, 2);

            var ok = GatherRatesPlus.Call("API_SetZoneField", zoneId, field, value) as bool? ?? false;
            if (!ok)
                Notify(state, $"Could not set {field}");
        });

        [ConsoleCommand("grpui.toggleperm")]
        private void CmdTogglePerm(ConsoleSystem.Arg arg) => Redraw(arg, (player, state) =>
        {
            var userId = ArgString(arg, 0);
            var ruleset = ArgString(arg, 1);
            var perm = "gatherratesplus.ruleset." + ruleset;

            if (!permission.PermissionExists(perm))
            {
                Notify(state, $"Permission {perm} does not exist");
                return;
            }

            if (permission.UserHasPermission(userId, perm))
            {
                permission.RevokeUserPermission(userId, perm);
                Notify(state, $"Revoked {ruleset}");
            }
            else
            {
                permission.GrantUserPermission(userId, perm, this);
                Notify(state, $"Granted {ruleset}");
            }
        });

        #endregion

        #region CUI Builders

        private string AddPanel(CuiElementContainer c, string parent, float x, float y, float w, float h, string color)
        {
            var id = NextName();
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = id,
                Components = { new CuiImageComponent { Color = color }, Rect(x, y, w, h) },
            });

            return id;
        }

        private void AddText(CuiElementContainer c, string parent, string text, float x, float y, float w, float h,
            int size, string color, TextAnchor align = TextAnchor.MiddleLeft)
        {
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = NextName(),
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text ?? "",
                        FontSize = size,
                        Color = color,
                        Align = align,
                        VerticalOverflow = VerticalWrapMode.Overflow,
                        BlocksRaycast = false,
                    },
                    Rect(x, y, w, h),
                },
            });
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string command,
            float x, float y, float w, float h, string color, string textColor = ColText, int size = 12)
        {
            var id = NextName();
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = id,
                Components = { new CuiButtonComponent { Color = color, Command = command }, Rect(x, y, w, h) },
            });

            AddText(c, id, text, 0, 0, w, h, size, textColor, TextAnchor.MiddleCenter);
        }

        // Overload used by the dispenser rows, which need a plain neutral clear button.
        private void AddButton(CuiElementContainer c, string parent, string text, string command,
            int susp, float x, float y, float w, float h)
            => AddButton(c, parent, text, command, x, y, w, h, ColNeutral, ColText, 10);

        private void AddToggle(CuiElementContainer c, string parent, bool value, string command, float x, float y)
            => AddButton(c, parent, value ? "ON" : "OFF", $"{command} {(!value).ToString().ToLowerInvariant()}",
                x, y, 60, 22, value ? ColGreen : ColNeutral, ColText, 10);

        private void AddInput(CuiElementContainer c, string parent, string text, string command,
            float x, float y, float w, float h, int size = 11, int charsLimit = 64, string bg = ColField,
            string textColor = ColText)
        {
            var id = AddPanel(c, parent, x, y, w, h, bg);
            c.Add(new CuiElement
            {
                Parent = id,
                Name = NextName(),
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = text ?? "",
                        FontSize = size,
                        Color = textColor,
                        Align = TextAnchor.MiddleLeft,
                        CharsLimit = charsLimit,
                        Command = command,
                        NeedsKeyboard = true,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "6 0",
                        OffsetMax = "-6 0",
                    },
                },
            });
        }

        #endregion
    }
}

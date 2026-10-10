using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Gather Rates Plus", "WhiteThunder / Lucien Murray-Pitts", "0.3.0")]
    [Description("Permission, zone and entity aware gather rates, with quarry and excavator speed control")]
    internal class GatherRatesPlus : RustPlugin
    {
        [PluginReference] private Plugin ZoneManager;

        #region Constants

        private const string PermissionAdmin = "gatherratesplus.admin";
        private const string RulesetPermissionPrefix = "gatherratesplus.ruleset.";

        // Vanilla defaults, taken from the decompiled server source. GatherManager 2.2.78 restored
        // excavators using the QUARRY default (5s instead of 3s), silently slowing every excavator
        // by 40% on each reload; keeping these separate is what stops us repeating that.
        private const float VanillaQuarryTickRate = 5f;
        private const float VanillaExcavatorTickRate = 3f;
        private const float VanillaExcavatorBeltSpeedMax = 0.1f;
        private const float VanillaExcavatorTimeForFullResources = 120f;

        private const string ProcessResources = "ProcessResources";

        private static readonly object False = false;

        #endregion

        #region Fields

        private Configuration _config;
        private StoredData _data;

        // Prefab short names of every entity that can act as a dispenser on this build.
        // Populated once at startup by walking the prefab manifest.
        private HashSet<string> _validDispensers = new HashSet<string>();

        // Item short name -> display name, for command output and the UI.
        private readonly Dictionary<string, string> _itemNames = new Dictionary<string, string>();

        // Which dispensers can actually produce a given item, and vice versa. Built once from the
        // prefabs so the editor only ever offers items that can really come out of a gather --
        // ItemManager.itemList contains every item in the game, cars and rifles included.
        private readonly Dictionary<string, HashSet<string>> _itemSources
            = new Dictionary<string, HashSet<string>>();

        private readonly Dictionary<string, HashSet<string>> _dispenserItems
            = new Dictionary<string, HashSet<string>>();

        // Quarries, pumpjacks and survey charges pull from a ResourceDeposit rather than an authored
        // item list, so their output set is the ore table MiningQuarry seeds deposits with.
        private static readonly string[] DepositItems =
            { "stones", "metal.ore", "sulfur.ore", "hq.metal.ore", "crude.oil" };

        private bool _speedApplied;

        private static GatherRatesPlus Instance;
        private Harmony _harmony;
        private const string HarmonyId = "com.lucienmp.gatherratesplus";

        // Vanilla bonus tables captured at startup, keyed by prefab short name, plus a prefabID
        // lookup so the Harmony patch can find the right one from the shared attribute.
        private readonly Dictionary<string, BonusTable> _bonusTables = new Dictionary<string, BonusTable>();
        private readonly Dictionary<uint, string> _bonusPrefabById = new Dictionary<uint, string>();

        #endregion

        #region Hooks

        private void Init()
        {
            Instance = this;
            permission.RegisterPermission(PermissionAdmin, this);
            _data = StoredData.Load();
        }

        private void OnServerInitialized()
        {
            _validDispensers = BuildDispenserIndex();
            ApplyLearnedSources();
            CacheItemNames();
            _config.Init(this, _validDispensers);
            ApplySpeedSettings();
            SyncZonesToZoneManager();

            // Patched here rather than in Init(): Harmony's PatchAll touches types whose static
            // constructors run before server.identity is set, which kills the server outright.
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        private void Unload()
        {
            _harmony?.UnpatchAll(HarmonyId);
            RestoreSpeedSettings();
            _data?.Save();
            Instance = null;
        }

        private void OnServerSave() => _data.Save();

        private void OnNewSave() => _data = StoredData.Clear();

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name != nameof(ZoneManager))
                return;

            // ZoneManager came back after we started without it; re-register our zones.
            timer.Once(2f, () =>
            {
                SyncZonesToZoneManager();
                BroadcastChanged();
            });
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == nameof(ZoneManager))
                BroadcastChanged();
        }

        // Declared as BaseEntity rather than BasePlayer: on this build the hook passes a BaseEntity
        // (it also fires for horses eating), so a BasePlayer signature silently misses those calls.
        private object OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity?.ToPlayer();
            if (player == null || dispenser?.baseEntity == null || item?.info == null)
                return null;

            ProcessGather(dispenser.baseEntity, item, player.UserIDString, player.transform.position);
            return item.amount < 1 ? False : null;
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity?.ToPlayer();
            if (player == null || dispenser?.baseEntity == null || item?.info == null)
                return;

            ProcessGather(dispenser.baseEntity, item, player.UserIDString, player.transform.position);
            RemoveIfEmpty(item);
        }

        private void OnGrowableGathered(GrowableEntity growable, Item item, BasePlayer player)
        {
            if (growable == null || item?.info == null || player == null)
                return;

            ProcessGather(growable, item, player.UserIDString, player.transform.position);
            RemoveIfEmpty(item);
        }

        // Deliberately a non-cancelling hook that only rewrites the pending amounts.
        //
        // Upstream GatherRates cancels vanilla pickup and reimplements it, which lets it also scale
        // the RandomItemDispenser bonus roll -- but that copy has drifted from the game: it drops the
        // IgnoreInTutorial check, SetItemOwnership, Analytics and GiveItemOptions.BackpackOverflow.
        // Mutating itemList in place keeps all of vanilla's behaviour at the cost of leaving bonus
        // rolls at 1x, which is what GatherManager and UGather do too.
        // VERIFIED LIVE 2026-09-15 with a probe plugin: this build fires the TWO-argument form.
        // The three-argument (…, bool eat) shape that upstream GatherRates declares never fires here,
        // which is why nothing was being multiplied at all.
        private void OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (collectible?.itemList == null || player == null)
                return;

            var ruleset = GetRuleset(player.UserIDString, player.transform.position);
            if (ruleset == null)
                return;

            foreach (var itemAmount in collectible.itemList)
            {
                if (itemAmount?.itemDef == null)
                    continue;

                LearnSource(collectible.ShortPrefabName, itemAmount.itemDef.shortname);

                var spec = ruleset.GetRateSpec(itemAmount.itemDef, collectible.ShortPrefabName);
                if (spec.IsNoop)
                    continue;

                if (MultiplyWasBlocked(collectible, itemAmount.itemDef, player.UserIDString))
                    continue;

                itemAmount.amount = spec.Apply((int)itemAmount.amount, _config.RoundInsteadOfTruncate);
            }
        }

        private object OnQuarryGather(MiningQuarry quarry, Item item)
        {
            if (quarry == null || item?.info == null)
                return null;

            ProcessGather(quarry, item, ResolveQuarryUser(quarry), quarry.transform.position);
            return item.amount < 1 ? False : null;
        }

        private object OnExcavatorGather(ExcavatorArm excavator, Item item)
        {
            if (excavator == null || item?.info == null)
                return null;

            string userId;
            _data.ExcavatorStarters.TryGetValue(excavator.net.ID.Value, out userId);

            ProcessGather(excavator, item, userId, excavator.transform.position);
            if (item.amount < 1)
            {
                item.Remove();
                return False;
            }

            return null;
        }

        private void OnQuarryToggled(MiningQuarry quarry, BasePlayer player)
        {
            if (quarry == null || player == null || !quarry.IsOn())
                return;

            _data.QuarryStarters[quarry.net.ID.Value] = player.UserIDString;
        }

        private void OnExcavatorResourceSet(ExcavatorArm arm, string resourceName, BasePlayer player)
        {
            // Only claim attribution while the arm is stopped, so nobody can steal a running dig
            // by flipping the resource selector mid-run.
            if (arm == null || player == null || arm.IsOn())
                return;

            _data.ExcavatorStarters[arm.net.ID.Value] = player.UserIDString;
        }

        // Survey charges report no thrower, so remember who threw each one.
        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon instance)
        {
            var survey = entity as SurveyCharge;
            if (survey == null || player == null)
                return;

            _data.SurveyThrowers[survey.net.ID.Value] = player.UserIDString;
        }

        private void OnSurveyGather(SurveyCharge survey, Item item)
        {
            if (survey == null || item?.info == null)
                return;

            string userId;
            _data.SurveyThrowers.TryGetValue(survey.net.ID.Value, out userId);

            ProcessGather(survey, item, userId, survey.transform.position);
            RemoveIfEmpty(item);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            // Keep the attribution tables from growing across a wipe.
            var id = entity?.net?.ID.Value;
            if (id == null)
                return;

            if (entity is SurveyCharge)
                _data.SurveyThrowers.Remove(id.Value);
            else if (entity is MiningQuarry)
                _data.QuarryStarters.Remove(id.Value);
            else if (entity is ExcavatorArm)
                _data.ExcavatorStarters.Remove(id.Value);
        }

        private void OnMiningQuarryEnabled(MiningQuarry quarry)
        {
            if (quarry == null || !_config.Speed.QuarryTickRateChanged)
                return;

            quarry.CancelInvoke(ProcessResources);
            quarry.InvokeRepeating(ProcessResources, _config.Speed.MiningQuarryTickRate, _config.Speed.MiningQuarryTickRate);
        }

        private void OnEnterZone(string zoneId, BasePlayer player)
        {
            var zone = _config.FindZone(zoneId);
            if (zone == null || player == null || string.IsNullOrEmpty(zone.EnterMessage))
                return;

            if (zone.RequirePermission && !HasRulesetPermission(player.UserIDString, zone.RulesetName))
                return;

            SendReply(player, zone.EnterMessage);
        }

        private void OnExitZone(string zoneId, BasePlayer player)
        {
            var zone = _config.FindZone(zoneId);
            if (zone == null || player == null || string.IsNullOrEmpty(zone.LeaveMessage))
                return;

            if (zone.RequirePermission && !HasRulesetPermission(player.UserIDString, zone.RulesetName))
                return;

            SendReply(player, zone.LeaveMessage);
        }

        #endregion

        #region Rate Resolution

        private void ProcessGather(BaseEntity entity, Item item, string userId, Vector3 position)
        {
            LearnSource(entity.ShortPrefabName, item.info.shortname);

            var ruleset = GetRuleset(userId, position);
            if (ruleset == null)
                return;

            var spec = ruleset.GetRateSpec(item.info, entity.ShortPrefabName);
            if (spec.IsNoop)
                return;

            if (MultiplyWasBlocked(entity, item.info, userId))
                return;

            item.amount = spec.Apply(item.amount, _config.RoundInsteadOfTruncate);
            item.MarkDirty();
        }

        // Quarries a player deployed use the owner's rates; monument quarries use whoever last
        // started them. Unlike upstream GatherRates we fall through to the default ruleset when
        // there is no record -- otherwise every monument quarry pays vanilla rates until someone
        // happens to re-toggle it, which is silently wrong on a flat-rate server.
        private string ResolveQuarryUser(MiningQuarry quarry)
        {
            if (quarry.OwnerID != 0)
                return quarry.OwnerID.ToString();

            string userId;
            return _data.QuarryStarters.TryGetValue(quarry.net.ID.Value, out userId) ? userId : null;
        }

        private GatherRateRuleset GetRuleset(string userId, Vector3 position)
        {
            var zone = FindMatchingZone(userId, position);

            if (zone != null && zone.OverridePermission)
                return _config.FindRuleset(zone.RulesetName) ?? _config.DefaultRuleset;

            if (!string.IsNullOrEmpty(userId))
            {
                var rulesets = _config.GatherRateRulesets;
                if (rulesets != null)
                {
                    for (var i = rulesets.Count - 1; i >= 0; i--)
                    {
                        var ruleset = rulesets[i];
                        if (string.IsNullOrEmpty(ruleset.Name))
                            continue;

                        if (permission.UserHasPermission(userId, GetRulesetPermission(ruleset.Name)))
                            return ruleset;
                    }
                }
            }

            if (zone != null)
                return _config.FindRuleset(zone.RulesetName) ?? _config.DefaultRuleset;

            return _config.DefaultRuleset;
        }

        // Zone membership is resolved from our own stored centre/radius rather than by asking
        // ZoneManager, because quarries, excavators and survey charges gather with no player to ask
        // about. ZoneManager still owns the real zone (so other plugins see it) and drives messages.
        private ZoneDefinition FindMatchingZone(string userId, Vector3 position)
        {
            if (!ZonesAvailable || _config.Zones == null || _config.Zones.Count == 0)
                return null;

            ZoneDefinition best = null;

            foreach (var zone in _config.Zones)
            {
                if (zone == null || !zone.Enabled || string.IsNullOrEmpty(zone.RulesetName))
                    continue;

                if (position == Vector3.zero || !zone.Contains(position))
                    continue;

                if (zone.RequirePermission && (userId == null || !HasRulesetPermission(userId, zone.RulesetName)))
                    continue;

                if (zone.OverridePermission)
                    return zone;

                if (best == null)
                    best = zone;
            }

            return best;
        }

        private bool HasRulesetPermission(string userId, string rulesetName)
        {
            return !string.IsNullOrEmpty(rulesetName)
                   && permission.UserHasPermission(userId, GetRulesetPermission(rulesetName));
        }

        private bool MultiplyWasBlocked(BaseEntity entity, ItemDefinition itemDefinition, string userId)
        {
            var result = Interface.CallHook("OnGatherRateMultiply", entity, itemDefinition, userId);
            return result is bool && !(bool)result;
        }

        // Upstream truncates with a plain (int) cast, so 1.5x on a 1-unit yield produces 1 and the
        // multiplier silently does nothing. Rounding (with a floor of 1) is what UGather does and is
        // the behaviour people expect. NOTE: this is a deliberate divergence from stock GatherRates
        // -- if low-yield nodes ever look too generous, this is the knob to look at first.
        private int ApplyMultiplier(int amount, float multiplier)
        {
            if (amount <= 0 || float.IsNaN(multiplier) || multiplier < 0f)
                return amount;

            if (Mathf.Approximately(multiplier, 1f))
                return amount;

            var scaled = amount * multiplier;
            if (float.IsNaN(scaled))
                return amount;

            if (float.IsPositiveInfinity(scaled) || scaled >= int.MaxValue)
                return int.MaxValue;

            var result = _config.RoundInsteadOfTruncate
                ? Mathf.RoundToInt(scaled)
                : Mathf.FloorToInt(scaled);

            // A rate below 1x is allowed to reach zero so callers can drop the item; above 1x it
            // must never round down to nothing.
            return multiplier >= 1f ? Mathf.Max(1, result) : Mathf.Max(0, result);
        }

        private static void RemoveIfEmpty(Item item)
        {
            if (item.amount >= 1)
                return;

            item.RemoveFromContainer();
            item.Remove();
        }

        #endregion

        #region Speed Settings

        private void ApplySpeedSettings()
        {
            var speed = _config.Speed;

            if (speed.QuarryTickRateChanged)
            {
                foreach (var quarry in UnityEngine.Object.FindObjectsOfType<MiningQuarry>())
                {
                    if (quarry == null || !quarry.IsOn())
                        continue;

                    quarry.CancelInvoke(ProcessResources);
                    quarry.InvokeRepeating(ProcessResources, speed.MiningQuarryTickRate, speed.MiningQuarryTickRate);
                }
            }

            foreach (var excavator in UnityEngine.Object.FindObjectsOfType<ExcavatorArm>())
            {
                if (excavator == null)
                    continue;

                if (speed.ExcavatorTickRateChanged)
                {
                    excavator.CancelInvoke(ProcessResources);
                    excavator.InvokeRepeating(ProcessResources, speed.ExcavatorTickRate, speed.ExcavatorTickRate);
                }

                if (speed.ExcavatorBeltSpeedChanged)
                    excavator.beltSpeedMax = speed.ExcavatorBeltSpeedMax;

                if (speed.ExcavatorTimeForFullResourcesChanged)
                    excavator.timeForFullResources = speed.ExcavatorTimeForFullResources;
            }

            _speedApplied = true;
        }

        private void RestoreSpeedSettings()
        {
            if (!_speedApplied)
                return;

            var speed = _config.Speed;

            if (speed.QuarryTickRateChanged)
            {
                foreach (var quarry in UnityEngine.Object.FindObjectsOfType<MiningQuarry>())
                {
                    if (quarry == null || !quarry.IsOn())
                        continue;

                    quarry.CancelInvoke(ProcessResources);
                    quarry.InvokeRepeating(ProcessResources, VanillaQuarryTickRate, VanillaQuarryTickRate);
                }
            }

            foreach (var excavator in UnityEngine.Object.FindObjectsOfType<ExcavatorArm>())
            {
                if (excavator == null)
                    continue;

                if (speed.ExcavatorTickRateChanged)
                {
                    excavator.CancelInvoke(ProcessResources);
                    // Excavator default, not the quarry's -- this is the GatherManager bug, fixed.
                    excavator.InvokeRepeating(ProcessResources, VanillaExcavatorTickRate, VanillaExcavatorTickRate);
                }

                if (speed.ExcavatorBeltSpeedChanged)
                    excavator.beltSpeedMax = VanillaExcavatorBeltSpeedMax;

                if (speed.ExcavatorTimeForFullResourcesChanged)
                    excavator.timeForFullResources = VanillaExcavatorTimeForFullResources;
            }

            _speedApplied = false;
        }

        private void ReapplySpeedSettings()
        {
            RestoreSpeedSettings();
            ApplySpeedSettings();
        }

        #endregion

        #region Zones

        private bool ZonesAvailable => ZoneManager != null && ZoneManager.IsLoaded;

        private void SyncZonesToZoneManager()
        {
            if (!ZonesAvailable || _config.Zones == null)
                return;

            foreach (var zone in _config.Zones)
            {
                if (zone == null || !zone.Enabled)
                    continue;

                CreateOrUpdateZone(zone);
            }
        }

        private bool CreateOrUpdateZone(ZoneDefinition zone)
        {
            if (!ZonesAvailable)
                return false;

            var args = new[] { "radius", zone.Radius.ToString(CultureInfo.InvariantCulture) };
            var result = ZoneManager.CallHook("CreateOrUpdateZone", zone.ZoneId, args, zone.Position);
            return result is bool && (bool)result;
        }

        private bool EraseZone(string zoneId)
        {
            if (!ZonesAvailable || string.IsNullOrEmpty(zoneId))
                return false;

            var result = ZoneManager.CallHook("EraseZone", zoneId);
            return result is bool && (bool)result;
        }

        #endregion

        #region Prefab & Item Discovery

        // Walks the prefab manifest once, recording every entity that can dispense and exactly which
        // items it yields. Prefab-authored component data (containedItems, itemList, resourcesToMine)
        // is only readable this way -- it is not in the decompiled source or the asset bundles.
        private HashSet<string> BuildDispenserIndex()
        {
            var validDispensers = new HashSet<string>();

            foreach (var prefabPath in GameManifest.Current.entities)
            {
                var entity = GameManager.server.FindPrefab(prefabPath.ToLower())?.GetComponent<BaseEntity>();
                if (entity == null || string.IsNullOrEmpty(entity.ShortPrefabName))
                    continue;

                var shortName = entity.ShortPrefabName;
                var produced = new HashSet<string>();

                var collectible = entity as CollectibleEntity;
                if (collectible != null)
                {
                    _collectiblePrefabs.Add(shortName);

                    if (collectible.itemList != null)
                    {
                        foreach (var itemAmount in collectible.itemList)
                            AddProduced(produced, itemAmount?.itemDef);
                    }
                }

                var growable = entity as GrowableEntity;
                if (growable != null)
                {
                    _growablePrefabs.Add(shortName);

                    if (growable.Properties != null)
                    {
                        AddProduced(produced, growable.Properties.pickupItem);
                        AddProduced(produced, growable.Properties.SeedItem);
                    }
                }

                var excavator = entity as ExcavatorArm;
                if (excavator != null)
                {
                    _excavatorPrefabs.Add(shortName);

                    if (excavator.resourcesToMine != null)
                    {
                        foreach (var itemAmount in excavator.resourcesToMine)
                            AddProduced(produced, itemAmount?.itemDef);
                    }
                }

                if (entity is MiningQuarry)
                    _quarryPrefabs.Add(shortName);

                if (entity is SurveyCharge)
                    _surveyPrefabs.Add(shortName);

                if (entity is MiningQuarry || entity is SurveyCharge)
                {
                    foreach (var shortname in DepositItems)
                        AddProduced(produced, ItemManager.FindItemDefinition(shortname));
                }

                var dispenser = entity.GetComponent<ResourceDispenser>();
                if (dispenser != null)
                {
                    // Struck with a tool, so its payout scales with the tool's gather damage.
                    _toolGathered.Add(shortName);

                    if (dispenser.containedItems != null)
                    {
                        foreach (var itemAmount in dispenser.containedItems)
                            AddProduced(produced, itemAmount?.itemDef);
                    }
                }

                // Bonus-roll table, if this prefab carries one. Its items are folded into the index
                // so worm and grub appear on the Rates tab like anything else you can gather.
                var bonus = PrefabAttribute.server.Find<RandomItemDispenser>(entity.prefabID);
                if (bonus?.Chances != null && bonus.Chances.Length > 0)
                {
                    var table = new BonusTable { OnlyAwardOne = bonus.OnlyAwardOne };

                    foreach (var chance in bonus.Chances)
                    {
                        if (chance.Item == null)
                            continue;

                        table.Entries.Add(new BonusEntry
                        {
                            Item = chance.Item.shortname,
                            Amount = chance.Amount,
                            Chance = chance.Chance,
                        });

                        AddProduced(produced, chance.Item);
                    }

                    if (table.Entries.Count > 0)
                    {
                        _bonusTables[shortName] = table;
                        _bonusPrefabById[entity.prefabID] = shortName;
                    }
                }

                var isDispenser = dispenser != null || collectible != null || growable != null
                                  || excavator != null || entity is MiningQuarry || entity is SurveyCharge;

                if (!isDispenser && produced.Count == 0)
                    continue;

                validDispensers.Add(shortName);

                HashSet<string> existing;
                if (!_dispenserItems.TryGetValue(shortName, out existing))
                {
                    existing = new HashSet<string>();
                    _dispenserItems[shortName] = existing;
                }

                foreach (var item in produced)
                {
                    existing.Add(item);

                    HashSet<string> sources;
                    if (!_itemSources.TryGetValue(item, out sources))
                    {
                        sources = new HashSet<string>();
                        _itemSources[item] = sources;
                    }

                    sources.Add(shortName);
                }
            }

            return validDispensers;
        }

        // Some dispensers have no authored contents -- the servergibs_* wreckage from a downed heli
        // or Bradley is filled in at runtime when the vehicle dies, so the prefab scan sees nothing.
        // Recording what actually drops fills those gaps in, and the pairs persist across restarts.
        private void LearnSource(string dispenserShortName, string itemShortName)
        {
            if (string.IsNullOrEmpty(dispenserShortName) || string.IsNullOrEmpty(itemShortName))
                return;

            HashSet<string> items;
            if (!_dispenserItems.TryGetValue(dispenserShortName, out items))
            {
                items = new HashSet<string>();
                _dispenserItems[dispenserShortName] = items;
            }

            if (!items.Add(itemShortName))
                return;

            HashSet<string> sources;
            if (!_itemSources.TryGetValue(itemShortName, out sources))
            {
                sources = new HashSet<string>();
                _itemSources[itemShortName] = sources;
            }

            sources.Add(dispenserShortName);
            _validDispensers.Add(dispenserShortName);

            List<string> stored;
            if (!_data.LearnedSources.TryGetValue(dispenserShortName, out stored))
            {
                stored = new List<string>();
                _data.LearnedSources[dispenserShortName] = stored;
            }

            if (!stored.Contains(itemShortName))
            {
                stored.Add(itemShortName);
                _data.Dirty = true;
            }

            // A newly discovered pair changes what the editor can offer, so redraw open editors.
            BroadcastChanged();
        }

        private void ApplyLearnedSources()
        {
            foreach (var entry in _data.LearnedSources)
            {
                foreach (var itemShortName in entry.Value)
                {
                    HashSet<string> items;
                    if (!_dispenserItems.TryGetValue(entry.Key, out items))
                    {
                        items = new HashSet<string>();
                        _dispenserItems[entry.Key] = items;
                    }

                    items.Add(itemShortName);

                    HashSet<string> sources;
                    if (!_itemSources.TryGetValue(itemShortName, out sources))
                    {
                        sources = new HashSet<string>();
                        _itemSources[itemShortName] = sources;
                    }

                    sources.Add(entry.Key);
                    _validDispensers.Add(entry.Key);
                }
            }
        }

        private static void AddProduced(HashSet<string> set, ItemDefinition definition)
        {
            if (definition != null && !string.IsNullOrEmpty(definition.shortname))
                set.Add(definition.shortname);
        }

        private void CacheItemNames()
        {
            foreach (var definition in ItemManager.itemList)
            {
                if (definition == null || string.IsNullOrEmpty(definition.shortname))
                    continue;

                _itemNames[definition.shortname] = definition.displayName?.english ?? definition.shortname;
            }
        }

        private static string GetRulesetPermission(string rulesetName) => RulesetPermissionPrefix + rulesetName;

        #endregion

        #region Commands

        private bool HasAdmin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return true; // server console / RCON

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin))
                return true;

            arg.ReplyWith(Lang("NoPermission"));
            return false;
        }

        private static bool TryFloat(string raw, out float value)
            => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        [ConsoleCommand("grp.rulesets")]
        private void CmdRulesets(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var sb = new StringBuilder();
            sb.AppendLine($"default : x{_config.DefaultRuleset.DefaultRate} (no permission, applies to everyone)");

            foreach (var ruleset in _config.GatherRateRulesets)
            {
                sb.AppendLine($"{ruleset.Name} : x{ruleset.DefaultRate} " +
                              $"({ruleset.ItemRateOverrides.Count} item, {ruleset.DispenserRateOverrides.Count} dispenser overrides) " +
                              $"-> {GetRulesetPermission(ruleset.Name)}");
            }

            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("grp.ruleset.add")]
        private void CmdRulesetAdd(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var name = arg.GetString(0);
            float rate;
            if (string.IsNullOrEmpty(name) || !TryFloat(arg.GetString(1), out rate))
            {
                arg.ReplyWith("Usage: grp.ruleset.add <name> <multiplier>");
                return;
            }

            arg.ReplyWith(AddRuleset(name, rate)
                ? $"Created ruleset '{name}' at x{rate}. Grant it with: oxide.grant user <player> {GetRulesetPermission(name)}"
                : $"A ruleset named '{name}' already exists.");
        }

        [ConsoleCommand("grp.ruleset.remove")]
        private void CmdRulesetRemove(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var name = arg.GetString(0);
            if (string.IsNullOrEmpty(name))
            {
                arg.ReplyWith("Usage: grp.ruleset.remove <name>");
                return;
            }

            arg.ReplyWith(RemoveRuleset(name)
                ? $"Removed ruleset '{name}'."
                : $"No ruleset named '{name}'.");
        }

        [ConsoleCommand("grp.defaultrate")]
        private void CmdDefaultRate(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            float rate;
            var rulesetName = arg.GetString(0);
            if (string.IsNullOrEmpty(rulesetName) || !TryFloat(arg.GetString(1), out rate))
            {
                arg.ReplyWith("Usage: grp.defaultrate <ruleset|default> <multiplier>");
                return;
            }

            arg.ReplyWith(SetDefaultRate(rulesetName, rate)
                ? $"'{rulesetName}' base rate set to x{rate}."
                : $"No ruleset named '{rulesetName}'.");
        }

        [ConsoleCommand("grp.rate")]
        private void CmdRate(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var rulesetName = arg.GetString(0);
            var itemName = arg.GetString(1);
            var rawRate = arg.GetString(2);

            if (string.IsNullOrEmpty(rulesetName) || string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(rawRate))
            {
                arg.ReplyWith("Usage: grp.rate <ruleset|default> <item shortname|*> <value|remove>\n" +
                              "  2      multiply vanilla by 2\n" +
                              "  =5     always give exactly 5\n" +
                              "  =5-10  give a random 5 to 10 (inclusive)\n" +
                              "'*' sets the ruleset base multiplier. Use grp.listitems to find short names.");
                return;
            }

            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
            {
                arg.ReplyWith($"No ruleset named '{rulesetName}'.");
                return;
            }

            if (itemName == "*")
            {
                float baseRate;
                if (!TryFloat(rawRate, out baseRate))
                {
                    arg.ReplyWith("Invalid multiplier.");
                    return;
                }

                SetDefaultRate(rulesetName, baseRate);
                arg.ReplyWith($"'{rulesetName}' base rate set to x{baseRate}.");
                return;
            }

            if (ItemManager.FindItemDefinition(itemName) == null)
            {
                arg.ReplyWith($"'{itemName}' is not a valid item short name. Try grp.listitems {itemName}");
                return;
            }

            if (rawRate.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                SetItemRate(rulesetName, itemName, null);
                arg.ReplyWith($"Removed the '{itemName}' override from '{rulesetName}'.");
                return;
            }

            var spec = RateSpec.Parse(rawRate);
            if (spec == null)
            {
                arg.ReplyWith("Invalid value. Use 2, =5 or =5-10.");
                return;
            }

            SetItemRate(rulesetName, itemName, spec);
            arg.ReplyWith($"'{rulesetName}': {itemName} set to {spec}." + FixedModeWarning(spec, null));
        }

        [ConsoleCommand("grp.dispenserrate")]
        private void CmdDispenserRate(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var rulesetName = arg.GetString(0);
            var prefab = arg.GetString(1);
            var itemName = arg.GetString(2);
            var rawRate = arg.GetString(3);

            if (string.IsNullOrEmpty(rulesetName) || string.IsNullOrEmpty(prefab)
                || string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(rawRate))
            {
                arg.ReplyWith("Usage: grp.dispenserrate <ruleset|default> <dispenser shortprefabname> <item shortname> <multiplier|remove>\n" +
                              "Use grp.listdispensers to find dispenser names.");
                return;
            }

            if (_config.FindRuleset(rulesetName) == null)
            {
                arg.ReplyWith($"No ruleset named '{rulesetName}'.");
                return;
            }

            if (_validDispensers.Count > 0 && !_validDispensers.Contains(prefab))
            {
                arg.ReplyWith($"'{prefab}' is not a known dispenser. Try grp.listdispensers {prefab}");
                return;
            }

            if (ItemManager.FindItemDefinition(itemName) == null)
            {
                arg.ReplyWith($"'{itemName}' is not a valid item short name.");
                return;
            }

            if (rawRate.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                SetDispenserRate(rulesetName, prefab, itemName, null);
                arg.ReplyWith($"Removed '{itemName}' from '{prefab}' in ruleset '{rulesetName}'.");
                return;
            }

            var spec = RateSpec.Parse(rawRate);
            if (spec == null)
            {
                arg.ReplyWith("Invalid value. Use 2, =5 or =5-10.");
                return;
            }

            SetDispenserRate(rulesetName, prefab, itemName, spec);
            arg.ReplyWith($"'{rulesetName}': {itemName} from {prefab} set to {spec}."
                          + FixedModeWarning(spec, prefab));
        }

        // Shared by import and the reset commands: never destroy a config without leaving a copy.
        private string BackupConfig(string reason)
        {
            var backup = Path.Combine(Interface.Oxide.ConfigDirectory,
                $"GatherRatesPlus.{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            File.Copy(Path.Combine(Interface.Oxide.ConfigDirectory, "GatherRatesPlus.json"), backup, true);
            return Path.GetFileName(backup);
        }

        [ConsoleCommand("grp.reset")]
        private void CmdReset(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var rulesetName = arg.GetString(0);
            if (string.IsNullOrEmpty(rulesetName))
            {
                arg.ReplyWith("Usage: grp.reset <ruleset|default> [confirm]\n" +
                              "Clears that ruleset's base rate, item rules and per-dispenser overrides.\n" +
                              "Use grp.resetall to also clear rulesets, zones and speed settings.");
                return;
            }

            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
            {
                arg.ReplyWith($"No ruleset named '{rulesetName}'.");
                return;
            }

            var items = ruleset.ItemRateOverrides.Count;
            var dispensers = ruleset.DispenserRateOverrides.Sum(x => x.Value.Count);

            if (!string.Equals(arg.GetString(1), "confirm", StringComparison.OrdinalIgnoreCase))
            {
                arg.ReplyWith($"'{rulesetName}' would be reset: base x{ruleset.DefaultRate} -> x1, "
                              + $"{items} item rule(s) and {dispensers} per-dispenser override(s) removed.\n"
                              + $"Run  grp.reset {rulesetName} confirm  to do it.");
                return;
            }

            string backup;
            try
            {
                backup = BackupConfig("pre-reset");
            }
            catch (Exception ex)
            {
                arg.ReplyWith($"Aborted - could not back up the current config: {ex.Message}");
                return;
            }

            ResetRuleset(ruleset);
            CommitChange();

            arg.ReplyWith($"Reset '{rulesetName}' to vanilla. Backup: {backup}");
        }

        [ConsoleCommand("grp.resetall")]
        private void CmdResetAll(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var rulesets = _config.GatherRateRulesets.Count;
            var zones = _config.Zones.Count;
            var items = _config.DefaultRuleset.ItemRateOverrides.Count;
            var dispensers = _config.DefaultRuleset.DispenserRateOverrides.Sum(x => x.Value.Count);

            if (!string.Equals(arg.GetString(0), "confirm", StringComparison.OrdinalIgnoreCase))
            {
                arg.ReplyWith("This clears EVERYTHING back to vanilla:\n"
                              + $"  default ruleset : x{_config.DefaultRuleset.DefaultRate} -> x1, "
                              + $"{items} item rule(s), {dispensers} dispenser override(s)\n"
                              + $"  rulesets        : {rulesets} removed (their permissions stay registered until reload)\n"
                              + $"  zones           : {zones} removed from config and ZoneManager\n"
                              + "  speed           : quarry, excavator, belt and time-to-full back to vanilla\n"
                              + "Run  grp.resetall confirm  to do it.");
                return;
            }

            string backup;
            try
            {
                backup = BackupConfig("pre-resetall");
            }
            catch (Exception ex)
            {
                arg.ReplyWith($"Aborted - could not back up the current config: {ex.Message}");
                return;
            }

            ResetEverything();
            arg.ReplyWith($"Everything reset to vanilla. Backup: {backup}");
        }

        private static void ResetRuleset(GatherRateRuleset ruleset)
        {
            ruleset.DefaultRate = 1f;
            ruleset.ItemRateOverrides = new Dictionary<string, RateSpec>();
            ruleset.DispenserRateOverrides = new Dictionary<string, Dictionary<string, RateSpec>>();
        }

        private void ResetEverything()
        {
            ResetRuleset(_config.DefaultRuleset);

            foreach (var ruleset in _config.GatherRateRulesets.ToList())
                ResetRuleset(ruleset);

            _config.GatherRateRulesets.Clear();

            foreach (var zone in _config.Zones.ToList())
                EraseZone(zone.ZoneId);

            _config.Zones.Clear();

            // Put the live entities back before forgetting what we changed them from.
            RestoreSpeedSettings();
            _config.Speed = new SpeedSettings();
            _speedApplied = true;

            CommitChange();
        }

        private bool API_ResetRuleset(string rulesetName)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
                return false;

            try { BackupConfig("pre-reset"); } catch { }

            ResetRuleset(ruleset);
            CommitChange();
            return true;
        }

        private bool API_ResetEverything()
        {
            try { BackupConfig("pre-resetall"); } catch { }

            ResetEverything();
            return true;
        }

        [ConsoleCommand("grp.clearoverrides")]
        private void CmdClearOverrides(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var rulesetName = arg.GetString(0);
            if (string.IsNullOrEmpty(rulesetName))
            {
                arg.ReplyWith("Usage: grp.clearoverrides <ruleset|default> [item or prefab filter] [confirm]\n" +
                              "Removes per-dispenser overrides so those items fall back to the item rule or base rate.\n" +
                              "Without 'confirm' this only reports what would be removed.");
                return;
            }

            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
            {
                arg.ReplyWith($"No ruleset named '{rulesetName}'.");
                return;
            }

            var filter = arg.GetString(1);
            var confirm = string.Equals(filter, "confirm", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(arg.GetString(2), "confirm", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(filter, "confirm", StringComparison.OrdinalIgnoreCase))
                filter = null;

            var matched = new List<string>();

            foreach (var prefabEntry in ruleset.DispenserRateOverrides.ToList())
            {
                foreach (var itemEntry in prefabEntry.Value.ToList())
                {
                    var hit = string.IsNullOrEmpty(filter)
                              || prefabEntry.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                              || itemEntry.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!hit)
                        continue;

                    matched.Add($"{prefabEntry.Key} / {itemEntry.Key} ({itemEntry.Value})");

                    if (confirm)
                        prefabEntry.Value.Remove(itemEntry.Key);
                }

                if (confirm && prefabEntry.Value.Count == 0)
                    ruleset.DispenserRateOverrides.Remove(prefabEntry.Key);
            }

            if (matched.Count == 0)
            {
                arg.ReplyWith("Nothing matched.");
                return;
            }

            if (!confirm)
            {
                arg.ReplyWith($"Would remove {matched.Count} override(s):\n"
                              + string.Join("\n", matched.Take(25).ToArray())
                              + (matched.Count > 25 ? $"\n... and {matched.Count - 25} more" : "")
                              + "\n\nAdd 'confirm' to actually remove them.");
                return;
            }

            CommitChange();
            arg.ReplyWith($"Removed {matched.Count} per-dispenser override(s) from '{rulesetName}'.");
        }

        [ConsoleCommand("grp.listdispensers")]
        private void CmdListDispensers(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var filter = arg.GetString(0);
            var matches = _validDispensers
                .Where(x => string.IsNullOrEmpty(filter) || x.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x)
                .ToList();

            arg.ReplyWith(matches.Count == 0
                ? "No dispensers matched."
                : $"{matches.Count} dispenser(s):\n"
                  + string.Join("\n", matches.Select(x => $"{x,-34} {DescribeDispenser(x)}").ToArray()));
        }

        // Which gather path a prefab belongs to. The same resource often exists as both a wild
        // collectible and a planted growable, and they are rated through different hooks.
        private string DescribeDispenser(string prefab)
        {
            if (_collectiblePrefabs.Contains(prefab))
                return "collectible  (wild, press E to pick)";
            if (_growablePrefabs.Contains(prefab))
                return "growable     (planted, grown from a seed)";
            if (_quarryPrefabs.Contains(prefab))
                return "quarry       (deposit driven, per tick)";
            if (_excavatorPrefabs.Contains(prefab))
                return "excavator    (deposit driven, per tick)";
            if (_surveyPrefabs.Contains(prefab))
                return "survey charge";
            if (_toolGathered.Contains(prefab))
                return "struck       (tool gathered, finite pool)";

            return "";
        }

        // Bonus-roll tables. RandomItemDispenser is a PrefabAttribute, so this is the only way to
        // read it: walk the manifest and ask for each prefab's attribute the way vanilla does in
        // CollectibleEntity.DoPickup and GrowableEntity.PickFruit.
        [ConsoleCommand("grp.bonus")]
        private void CmdBonus(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var first = arg.GetString(0) ?? "";
            var summary = string.Equals(first, "summary", StringComparison.OrdinalIgnoreCase);

            var filter = "";
            var page = 1;
            int parsed;

            if (!summary)
            {
                // A bare number is a page, not a filter - "grp.bonus 2" is what the footer tells you
                // to type, so it has to mean page 2 rather than "prefabs containing 2".
                if (int.TryParse(first, out parsed))
                {
                    page = parsed;
                }
                else
                {
                    filter = first;
                    if (int.TryParse(arg.GetString(1), out parsed))
                        page = parsed;
                }
            }

            if (page < 1)
                page = 1;

            var tables = CollectBonusTables(filter);

            if (tables.Count == 0)
            {
                arg.ReplyWith("No bonus tables matched.");
                return;
            }

            var sb = new StringBuilder();

            if (summary)
            {
                // Nearly every table is the same worm/grub pair at one of a few rate tiers, so group
                // identical tables together rather than printing 31 near-identical blocks.
                foreach (var group in tables.GroupBy(x => x.Value).OrderByDescending(x => x.Count()))
                {
                    sb.AppendLine(group.Key);
                    sb.AppendLine($"    {group.Count()} prefab(s): "
                                  + string.Join(", ", group.Select(x => x.Key).OrderBy(x => x).ToArray()));
                    sb.AppendLine();
                }

                sb.AppendLine($"{tables.Count} prefab(s) carry a bonus table, "
                              + $"{tables.GroupBy(x => x.Value).Count()} distinct table(s).");
                arg.ReplyWith(sb.ToString());
                return;
            }

            const int perPage = 10;
            var pages = Mathf.Max(1, Mathf.CeilToInt(tables.Count / (float)perPage));
            page = Mathf.Min(page, pages);

            foreach (var entry in tables.Skip((page - 1) * perPage).Take(perPage))
            {
                sb.AppendLine(entry.Key);
                sb.AppendLine(entry.Value);
            }

            sb.AppendLine($"page {page}/{pages}   ({tables.Count} prefab(s) with a bonus table)");

            if (pages > 1)
                sb.AppendLine($"grp.bonus {(string.IsNullOrEmpty(filter) ? "" : filter + " ")}{page + 1}"
                              + "  for the next page, or  grp.bonus summary  for the grouped view.");

            arg.ReplyWith(sb.ToString());
        }

        // prefab short name -> formatted table body, ordered by prefab name.
        private List<KeyValuePair<string, string>> CollectBonusTables(string filter)
        {
            var result = new List<KeyValuePair<string, string>>();

            foreach (var prefabPath in GameManifest.Current.entities)
            {
                var entity = GameManager.server.FindPrefab(prefabPath.ToLower())?.GetComponent<BaseEntity>();
                if (entity == null || string.IsNullOrEmpty(entity.ShortPrefabName))
                    continue;

                var dispenser = PrefabAttribute.server.Find<RandomItemDispenser>(entity.prefabID);
                if (dispenser?.Chances == null || dispenser.Chances.Length == 0)
                    continue;

                var matches = string.IsNullOrEmpty(filter)
                              || entity.ShortPrefabName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                              || dispenser.Chances.Any(x => x.Item != null
                                   && x.Item.shortname.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!matches)
                    continue;

                var body = new StringBuilder();
                body.AppendLine($"    OnlyAwardOne={dispenser.OnlyAwardOne}");

                foreach (var chance in dispenser.Chances)
                {
                    if (chance.Item == null)
                        continue;

                    body.AppendLine($"    {chance.Item.shortname,-22} x{chance.Amount,-4} "
                                    + $"{chance.Chance * 100f:0.#}%"
                                    + (chance.IgnoreInTutorial ? "   (not in tutorial)" : ""));
                }

                result.Add(new KeyValuePair<string, string>(entity.ShortPrefabName, body.ToString().TrimEnd()));
            }

            return result.OrderBy(x => x.Key).ToList();
        }

        [ConsoleCommand("grp.bonuschance")]
        private void CmdBonusItemChance(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var item = arg.GetString(0);
            var raw = arg.GetString(1);

            if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(raw))
            {
                arg.ReplyWith("Usage: grp.bonuschance <item> <percent|remove>\n" +
                              "Sets the base drop chance for a bonus item everywhere it rolls.\n" +
                              "Per-dispenser values set with grp.bonusset still win.");
                return;
            }

            if (raw.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                arg.ReplyWith(SetBonusItemChance(item, null)
                    ? $"{item}: base chance removed, back to each prefab's own value."
                    : $"'{item}' is not a bonus item.");
                return;
            }

            float percent;
            if (!TryFloat(raw.TrimEnd('%'), out percent) || percent < 0f || percent > 100f)
            {
                arg.ReplyWith("Chance must be a percentage between 0 and 100.");
                return;
            }

            arg.ReplyWith(SetBonusItemChance(item, percent)
                ? $"{item}: base chance set to {percent}% on every dispenser that rolls it."
                : $"'{item}' is not a bonus item. Try grp.bonus to see the tables.");
        }

        [ConsoleCommand("grp.bonusset")]
        private void CmdBonusSet(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var prefab = arg.GetString(0);
            var second = arg.GetString(1);

            if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(second))
            {
                arg.ReplyWith("Usage:\n" +
                              "  grp.bonusset <prefab> <item> amount <n>\n" +
                              "  grp.bonusset <prefab> <item> chance <percent>\n" +
                              "  grp.bonusset <prefab> <item> reset\n" +
                              "  grp.bonusset <prefab> onlyawardone <true|false|reset>\n" +
                              "Use grp.bonus to find prefabs and their tables.");
                return;
            }

            if (!_bonusTables.ContainsKey(prefab))
            {
                arg.ReplyWith($"'{prefab}' has no bonus table. Try grp.bonus {prefab}");
                return;
            }

            if (string.Equals(second, "onlyawardone", StringComparison.OrdinalIgnoreCase))
            {
                var raw = arg.GetString(2);
                if (string.Equals(raw, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    SetBonusOnlyAwardOne(prefab, null);
                    arg.ReplyWith($"{prefab}: OnlyAwardOne back to vanilla.");
                    return;
                }

                bool flag;
                if (!bool.TryParse(raw, out flag))
                {
                    arg.ReplyWith("Usage: grp.bonusset <prefab> onlyawardone <true|false|reset>");
                    return;
                }

                SetBonusOnlyAwardOne(prefab, flag);
                arg.ReplyWith($"{prefab}: OnlyAwardOne = {flag}.");
                return;
            }

            var item = second;
            var field = arg.GetString(2);

            if (string.Equals(field, "reset", StringComparison.OrdinalIgnoreCase))
            {
                arg.ReplyWith(SetBonusOverride(prefab, item, null, null, true)
                    ? $"{prefab}: {item} back to vanilla."
                    : $"{prefab}: {item} had no override.");
                return;
            }

            float value;
            if (!TryFloat(arg.GetString(3), out value))
            {
                arg.ReplyWith("Usage: grp.bonusset <prefab> <item> <amount|chance> <value>");
                return;
            }

            if (string.Equals(field, "amount", StringComparison.OrdinalIgnoreCase))
            {
                SetBonusOverride(prefab, item, Mathf.RoundToInt(value), null, false);
                arg.ReplyWith($"{prefab}: {item} amount fixed at {Mathf.RoundToInt(value)} (rate no longer applies).");
                return;
            }

            if (string.Equals(field, "chance", StringComparison.OrdinalIgnoreCase))
            {
                SetBonusOverride(prefab, item, null, value, false);
                arg.ReplyWith($"{prefab}: {item} chance set to {Mathf.Clamp(value, 0f, 100f)}%.");
                return;
            }

            arg.ReplyWith("Field must be 'amount', 'chance' or 'reset'.");
        }

        [ConsoleCommand("grp.sources")]
        private void CmdSources(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var filter = arg.GetString(0);
            if (string.IsNullOrEmpty(filter))
            {
                arg.ReplyWith($"Usage: grp.sources <item shortname or partial name>\n" +
                              $"{_itemSources.Count} gatherable item(s) indexed across {_dispenserItems.Count} dispenser(s).");
                return;
            }

            var matches = _itemSources
                .Where(x => x.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || DisplayName(x.Key).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Key)
                .Take(15)
                .ToList();

            if (matches.Count == 0)
            {
                arg.ReplyWith($"No gatherable item matched '{filter}'.");
                return;
            }

            var sb = new StringBuilder();
            foreach (var entry in matches)
            {
                sb.AppendLine($"{entry.Key} ({DisplayName(entry.Key)}) <- {entry.Value.Count} source(s):");
                sb.AppendLine("   " + string.Join(", ", entry.Value.OrderBy(x => x).Take(12).ToArray()));
            }

            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("grp.listitems")]
        private void CmdListItems(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var filter = arg.GetString(0);
            var matches = _itemNames
                .Where(x => string.IsNullOrEmpty(filter)
                            || x.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || x.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}  ({x.Value})")
                .ToList();

            if (matches.Count == 0)
            {
                arg.ReplyWith("No items matched.");
                return;
            }

            if (matches.Count > 120 && string.IsNullOrEmpty(filter))
            {
                arg.ReplyWith($"{matches.Count} items. Pass a filter, e.g. grp.listitems metal");
                return;
            }

            arg.ReplyWith($"{matches.Count} item(s):\n" + string.Join("\n", matches.ToArray()));
        }

        [ConsoleCommand("grp.quarrytickrate")]
        private void CmdQuarryTickRate(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            float value;
            if (!TryFloat(arg.GetString(0), out value) || value < 1f)
            {
                arg.ReplyWith($"Usage: grp.quarrytickrate <seconds, min 1> (vanilla {VanillaQuarryTickRate})");
                return;
            }

            SetSpeed("MiningQuarryTickRate", value);
            arg.ReplyWith($"Mining quarries now tick every {value}s.");
        }

        [ConsoleCommand("grp.excavatortickrate")]
        private void CmdExcavatorTickRate(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            float value;
            if (!TryFloat(arg.GetString(0), out value) || value < 1f)
            {
                arg.ReplyWith($"Usage: grp.excavatortickrate <seconds, min 1> (vanilla {VanillaExcavatorTickRate})");
                return;
            }

            SetSpeed("ExcavatorTickRate", value);
            arg.ReplyWith($"Excavator now ticks every {value}s.");
        }

        [ConsoleCommand("grp.excavatorbelt")]
        private void CmdExcavatorBelt(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            float value;
            if (!TryFloat(arg.GetString(0), out value) || value <= 0f)
            {
                arg.ReplyWith($"Usage: grp.excavatorbelt <speed> (vanilla {VanillaExcavatorBeltSpeedMax})");
                return;
            }

            SetSpeed("ExcavatorBeltSpeedMax", value);
            arg.ReplyWith($"Excavator belt speed set to {value}.");
        }

        [ConsoleCommand("grp.excavatorfulltime")]
        private void CmdExcavatorFullTime(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            float value;
            if (!TryFloat(arg.GetString(0), out value) || value < 1f)
            {
                arg.ReplyWith($"Usage: grp.excavatorfulltime <seconds> (vanilla {VanillaExcavatorTimeForFullResources})");
                return;
            }

            SetSpeed("ExcavatorTimeForFullResources", value);
            arg.ReplyWith($"Excavator reaches full output after {value}s.");
        }

        [ConsoleCommand("grp.import")]
        private void CmdImport(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var apply = string.Equals(arg.GetString(0), "apply", StringComparison.OrdinalIgnoreCase);
            var rulesetName = arg.GetString(apply ? 1 : 0);
            if (string.IsNullOrEmpty(rulesetName))
                rulesetName = "default";

            var target = _config.FindRuleset(rulesetName);
            if (target == null)
            {
                arg.ReplyWith($"No ruleset named '{rulesetName}'. Create it first with grp.ruleset.add.");
                return;
            }

            var path = Path.Combine(Interface.Oxide.ConfigDirectory, "GatherManager.json");
            if (!File.Exists(path))
            {
                arg.ReplyWith($"No GatherManager.json found at {path}");
                return;
            }

            JObject options;
            try
            {
                options = JObject.Parse(File.ReadAllText(path))["Options"] as JObject;
            }
            catch (Exception ex)
            {
                arg.ReplyWith($"Could not read GatherManager.json: {ex.Message}");
                return;
            }

            if (options == null)
            {
                arg.ReplyWith("GatherManager.json has no 'Options' section.");
                return;
            }

            var report = new StringBuilder();
            var plan = BuildImportPlan(options, report);

            if (!apply)
            {
                report.AppendLine();
                report.AppendLine("This was a PREVIEW - nothing has changed.");
                report.AppendLine($"Run  grp.import apply {(rulesetName == "default" ? "" : rulesetName)} to commit.");
                arg.ReplyWith(report.ToString());
                return;
            }

            // The current config is overwritten wholesale, so keep a copy next to it first.
            var backup = Path.Combine(Interface.Oxide.ConfigDirectory,
                $"GatherRatesPlus.pre-import-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(Path.Combine(Interface.Oxide.ConfigDirectory, "GatherRatesPlus.json"), backup, true);
                report.AppendLine($"Backed up current config to {Path.GetFileName(backup)}");
            }
            catch (Exception ex)
            {
                arg.ReplyWith($"Aborted - could not back up the current config: {ex.Message}");
                return;
            }

            target.DefaultRate = plan.DefaultRate;
            target.ItemRateOverrides = plan.ItemRates;
            target.DispenserRateOverrides = plan.DispenserRates;

            if (plan.Speed != null)
            {
                _config.Speed = plan.Speed;
                ReapplySpeedSettings();
            }

            CommitChange();

            report.AppendLine();
            report.AppendLine($"Imported into ruleset '{rulesetName}'.");
            arg.ReplyWith(report.ToString());
        }

        private class ImportPlan
        {
            public float DefaultRate = 1f;
            public Dictionary<string, RateSpec> ItemRates = new Dictionary<string, RateSpec>();
            public Dictionary<string, Dictionary<string, RateSpec>> DispenserRates
                = new Dictionary<string, Dictionary<string, RateSpec>>();
            public SpeedSettings Speed;
        }

        // GatherManager keys everything by English display name and splits rates across five
        // per-category tables. We key by short name and resolve per dispenser prefab, so the
        // conversion maps each category onto the prefabs that category actually covers.
        private ImportPlan BuildImportPlan(JObject options, StringBuilder report)
        {
            var plan = new ImportPlan();
            var unmapped = new List<string>();

            var dispenserTable = ReadTable(options, "GatherResourceModifiers", unmapped);
            var pickupTable = ReadTable(options, "PickupResourceModifiers", unmapped);
            var quarryTable = ReadTable(options, "QuarryResourceModifiers", unmapped);

            // GatherManager defaults ExcavatorResourceModifiers to the QUARRY table when the key is
            // missing entirely (LoadConfigValues passes quarryResourceModifiers as the default), so a
            // config without that key runs the excavator on quarry rates.
            var excavatorInherits = options["ExcavatorResourceModifiers"] == null;
            var excavatorTable = excavatorInherits
                ? new Dictionary<string, float>(quarryTable)
                : ReadTable(options, "ExcavatorResourceModifiers", unmapped);

            if (excavatorInherits)
                report.AppendLine("Note                 : no ExcavatorResourceModifiers key - "
                                  + "GatherManager falls back to the quarry table, so we do too");
            var surveyTable = ReadTable(options, "SurveyResourceModifiers", unmapped);

            // GatherManager's "*" is the fallback for DISPENSER gathering only - quarries, the
            // excavator, pickups and surveys stayed at 1x unless named. Our DefaultRate is global,
            // so adopting "*" means explicitly pinning the other categories back to 1x.
            float wildcard;
            var hasWildcard = dispenserTable.TryGetValue("*", out wildcard);
            dispenserTable.Remove("*");

            plan.DefaultRate = hasWildcard ? wildcard : 1f;

            foreach (var entry in dispenserTable)
                plan.ItemRates[entry.Key] = RateSpec.Multiply(entry.Value);

            report.AppendLine($"Base rate            : x{plan.DefaultRate}"
                              + (hasWildcard ? "   (from the dispenser '*' entry)" : ""));
            report.AppendLine($"Item overrides       : {plan.ItemRates.Count}  (from GatherResourceModifiers)");

            ApplyCategory(plan, _collectiblePrefabs, pickupTable, hasWildcard, "pickups", report);
            ApplyCategory(plan, _quarryPrefabs, quarryTable, hasWildcard, "quarries", report);
            ApplyCategory(plan, _excavatorPrefabs, excavatorTable, hasWildcard, "excavator", report);
            ApplyCategory(plan, _surveyPrefabs, surveyTable, hasWildcard, "survey charges", report);

            var speed = ReadSpeed(options, report);
            plan.Speed = speed;

            var dispenserModifiers = options["GatherDispenserModifiers"] as JObject;
            if (dispenserModifiers != null && dispenserModifiers.Count > 0)
                report.AppendLine($"SKIPPED              : GatherDispenserModifiers ({dispenserModifiers.Count}) "
                                  + "- pool scaling has no equivalent here");

            if (unmapped.Count > 0)
                report.AppendLine($"UNMAPPED NAMES       : {string.Join(", ", unmapped.Distinct().ToArray())}");

            var totalDispenserEntries = plan.DispenserRates.Sum(x => x.Value.Count);
            report.AppendLine($"Per-dispenser entries: {totalDispenserEntries} across {plan.DispenserRates.Count} prefabs");

            return plan;
        }

        // A category's rates become per-prefab overrides on the prefabs that category covers. Where
        // the category was silent but our new global base rate is not 1x, the items that category
        // yields are pinned back to 1x so behaviour matches GatherManager exactly.
        private void ApplyCategory(ImportPlan plan, HashSet<string> prefabs, Dictionary<string, float> table,
            bool hasWildcard, string label, StringBuilder report)
        {
            if (prefabs.Count == 0)
                return;

            var named = 0;
            var pinned = 0;
            var viaWildcard = 0;
            var matchedItems = new HashSet<string>();

            // Every GatherManager table carries its own "*" fallback - they are independent, not one
            // shared global. A category wildcard covers that category's unnamed items entirely.
            float categoryWildcard;
            var hasCategoryWildcard = table.TryGetValue("*", out categoryWildcard);

            foreach (var prefab in prefabs)
            {
                HashSet<string> yields;
                if (!_dispenserItems.TryGetValue(prefab, out yields) || yields.Count == 0)
                    continue;

                foreach (var item in yields)
                {
                    float rate;
                    var explicitlyNamed = table.TryGetValue(item, out rate);

                    if (explicitlyNamed)
                    {
                        matchedItems.Add(item);
                    }
                    else if (hasCategoryWildcard)
                    {
                        rate = categoryWildcard;
                        viaWildcard++;
                    }
                    else
                    {
                        // Silent in GatherManager means vanilla. Only pin when the item would
                        // otherwise resolve to something other than 1x.
                        if (hasWildcard && !Mathf.Approximately(EffectiveRate(plan, item), 1f))
                        {
                            SetPlanRate(plan, prefab, item, 1f);
                            pinned++;
                        }

                        continue;
                    }

                    // Compare against what this item ALREADY resolves to - its global item rule if it
                    // has one, otherwise the base rate. Comparing against the base rate alone writes
                    // a per-dispenser entry that merely repeats the item rule, which is noise and
                    // makes the Rates tab report overrides that change nothing.
                    if (Mathf.Approximately(rate, EffectiveRate(plan, item)))
                        continue;

                    SetPlanRate(plan, prefab, item, rate);

                    if (explicitlyNamed)
                        named++;
                }
            }

            report.AppendLine($"  {label,-16}: {named} rate(s)"
                              + (hasCategoryWildcard ? $", {viaWildcard} from this category's own '*' x{categoryWildcard}" : "")
                              + $", {pinned} pinned to 1x across {prefabs.Count} prefabs");

            // Entries naming an item this category cannot produce were already doing nothing in
            // GatherManager. Say so rather than dropping them silently.
            var inert = table.Keys.Where(x => x != "*" && !matchedItems.Contains(x)).OrderBy(x => x).ToList();
            if (inert.Count > 0)
                report.AppendLine($"  {"",-16}  dropped {inert.Count} entry(s) this category never yields: "
                                  + string.Join(", ", inert.Take(8).ToArray()));
        }

        // What an item resolves to with no per-dispenser entry: its global item rule, else the base.
        private static float EffectiveRate(ImportPlan plan, string item)
        {
            RateSpec spec;
            return plan.ItemRates.TryGetValue(item, out spec) ? spec.Value : plan.DefaultRate;
        }

        private static void SetPlanRate(ImportPlan plan, string prefab, string item, float rate)
        {
            Dictionary<string, RateSpec> items;
            if (!plan.DispenserRates.TryGetValue(prefab, out items))
            {
                items = new Dictionary<string, RateSpec>();
                plan.DispenserRates[prefab] = items;
            }

            items[item] = RateSpec.Multiply(rate);
        }

        // Display name -> short name, because GatherManager keys by the English display name.
        private Dictionary<string, float> ReadTable(JObject options, string name, List<string> unmapped)
        {
            var result = new Dictionary<string, float>();
            var table = options[name] as JObject;
            if (table == null)
                return result;

            foreach (var property in table.Properties())
            {
                if (property.Name == "*")
                {
                    result["*"] = property.Value.Value<float>();
                    continue;
                }

                var shortName = ResolveShortName(property.Name);
                if (shortName == null)
                {
                    unmapped.Add(property.Name);
                    continue;
                }

                result[shortName] = property.Value.Value<float>();
            }

            return result;
        }

        private string ResolveShortName(string displayName)
        {
            foreach (var entry in _itemNames)
            {
                if (string.Equals(entry.Value, displayName, StringComparison.OrdinalIgnoreCase))
                    return entry.Key;
            }

            // Fall back to treating it as a short name already.
            return ItemManager.FindItemDefinition(displayName) != null ? displayName : null;
        }

        private SpeedSettings ReadSpeed(JObject options, StringBuilder report)
        {
            var speed = new SpeedSettings
            {
                MiningQuarryTickRate = options.Value<float?>("MiningQuarryResourceTickRate") ?? VanillaQuarryTickRate,
                ExcavatorTickRate = options.Value<float?>("ExcavatorResourceTickRate") ?? VanillaExcavatorTickRate,
                ExcavatorBeltSpeedMax = options.Value<float?>("ExcavatorBeltSpeedMax") ?? VanillaExcavatorBeltSpeedMax,
                ExcavatorTimeForFullResources = options.Value<float?>("ExcavatorTimeForFullResources")
                                                ?? VanillaExcavatorTimeForFullResources,
            };

            var changed = speed.QuarryTickRateChanged || speed.ExcavatorTickRateChanged
                          || speed.ExcavatorBeltSpeedChanged || speed.ExcavatorTimeForFullResourcesChanged;

            report.AppendLine(changed
                ? $"Speed                : quarry {speed.MiningQuarryTickRate}s, excavator {speed.ExcavatorTickRate}s, "
                  + $"belt {speed.ExcavatorBeltSpeedMax}, time-to-full {speed.ExcavatorTimeForFullResources}s"
                : "Speed                : all vanilla, nothing to change");

            return speed;
        }

        [ConsoleCommand("grp.speed")]
        private void CmdSpeed(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            var s = _config.Speed;
            arg.ReplyWith(
                $"Quarry tick rate      : {s.MiningQuarryTickRate}s (vanilla {VanillaQuarryTickRate})\n" +
                $"Excavator tick rate   : {s.ExcavatorTickRate}s (vanilla {VanillaExcavatorTickRate})\n" +
                $"Excavator belt speed  : {s.ExcavatorBeltSpeedMax} (vanilla {VanillaExcavatorBeltSpeedMax})\n" +
                $"Excavator time-to-full: {s.ExcavatorTimeForFullResources}s (vanilla {VanillaExcavatorTimeForFullResources})");
        }

        [ConsoleCommand("grp.zone")]
        private void CmdZone(ConsoleSystem.Arg arg)
        {
            if (!HasAdmin(arg)) return;

            if (!ZonesAvailable)
            {
                arg.ReplyWith(Lang("ZonesUnavailable"));
                return;
            }

            var sub = arg.GetString(0)?.ToLowerInvariant();

            switch (sub)
            {
                case "list":
                {
                    if (_config.Zones.Count == 0)
                    {
                        arg.ReplyWith("No gather zones defined.");
                        return;
                    }

                    var sb = new StringBuilder();
                    for (var i = 0; i < _config.Zones.Count; i++)
                    {
                        var z = _config.Zones[i];
                        sb.AppendLine($"[{i + 1}] {z.ZoneId} ruleset='{z.RulesetName}' r={z.Radius} " +
                                      $"enabled={z.Enabled} override={z.OverridePermission} requireperm={z.RequirePermission}");
                    }

                    arg.ReplyWith(sb.ToString());
                    return;
                }

                case "add":
                {
                    var player = arg.Player();
                    if (player == null)
                    {
                        arg.ReplyWith("grp.zone add must be run in game (it uses your position).");
                        return;
                    }

                    var rulesetName = arg.GetString(1);
                    float radius;
                    if (string.IsNullOrEmpty(rulesetName) || !TryFloat(arg.GetString(2), out radius) || radius <= 0f)
                    {
                        arg.ReplyWith("Usage: grp.zone add <ruleset> <radius>");
                        return;
                    }

                    if (_config.FindRuleset(rulesetName) == null)
                    {
                        arg.ReplyWith($"No ruleset named '{rulesetName}'.");
                        return;
                    }

                    var zoneId = AddZone(rulesetName, player.transform.position, radius);
                    arg.ReplyWith(zoneId == null
                        ? "ZoneManager refused to create the zone."
                        : $"Created zone {zoneId} (ruleset '{rulesetName}', radius {radius}).");
                    return;
                }

                case "remove":
                {
                    var zoneId = arg.GetString(1);
                    if (string.IsNullOrEmpty(zoneId))
                    {
                        arg.ReplyWith("Usage: grp.zone remove <zoneId>");
                        return;
                    }

                    arg.ReplyWith(RemoveZone(zoneId) ? $"Removed zone {zoneId}." : $"No zone {zoneId}.");
                    return;
                }

                case "set":
                {
                    var zoneId = arg.GetString(1);
                    var field = arg.GetString(2);
                    var value = arg.GetString(3);

                    if (string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(field))
                    {
                        arg.ReplyWith("Usage: grp.zone set <zoneId> <ruleset|radius|enabled|override|requireperm|enter|leave> <value>");
                        return;
                    }

                    arg.ReplyWith(SetZoneField(zoneId, field, value)
                        ? $"Updated {field} on {zoneId}."
                        : $"Could not set '{field}' on '{zoneId}'.");
                    return;
                }

                default:
                    arg.ReplyWith("grp.zone list | add <ruleset> <radius> | remove <zoneId> | set <zoneId> <field> <value>");
                    return;
            }
        }

        [ChatCommand("gather")]
        private void ChatGather(BasePlayer player, string command, string[] args)
        {
            var ruleset = GetRuleset(player.UserIDString, player.transform.position);
            var sb = new StringBuilder();

            sb.AppendLine(Lang("RatesHeader", player.UserIDString));
            sb.AppendLine($"  {Lang("BaseRate", player.UserIDString)}: x{ruleset.DefaultRate}");

            if (ruleset.ItemRateOverrides.Count > 0)
            {
                foreach (var entry in ruleset.ItemRateOverrides.OrderBy(x => x.Key))
                    sb.AppendLine($"  {DisplayName(entry.Key)}: x{entry.Value}");
            }

            var zone = FindMatchingZone(player.UserIDString, player.transform.position);
            if (zone != null)
                sb.AppendLine(Lang("InZone", player.UserIDString).Replace("{ruleset}", zone.RulesetName));

            if (_config.Speed.QuarryTickRateChanged)
                sb.AppendLine($"  Quarry tick: {_config.Speed.MiningQuarryTickRate}s");

            SendReply(player, sb.ToString());

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin))
                SendReply(player, Lang("AdminHint", player.UserIDString));
        }

        [ChatCommand("gatherrates")]
        private void ChatGatherRates(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                SendReply(player, Lang("NoPermission", player.UserIDString));
                return;
            }

            SendReply(player,
                "<color=#8ac6f2>Gather Rates Plus</color>\n" +
                "grp.rulesets / grp.ruleset.add / grp.ruleset.remove\n" +
                "grp.rate <ruleset> <item|*> <mult|remove>\n" +
                "grp.dispenserrate <ruleset> <prefab> <item> <mult|remove>\n" +
                "grp.listitems [filter] / grp.listdispensers [filter]\n" +
                "grp.speed / grp.quarrytickrate / grp.excavatortickrate / grp.excavatorbelt / grp.excavatorfulltime\n" +
                "grp.zone list|add|remove|set\n" +
                "/grpui opens the editor UI.");
        }

        private void SendHelpText(BasePlayer player) => SendReply(player, Lang("HelpText", player.UserIDString));

        // A fixed or ranged amount ignores how hard the tool hit, but the NODE still drains at the
        // tool's rate -- so a better tool empties it in fewer swings and therefore yields less in
        // total. Worth saying out loud, because it inverts tool progression on nodes and trees.
        private string FixedModeWarning(RateSpec spec, string prefab)
        {
            if (spec == null || spec.Mode == RateMode.Multiply)
                return "";

            // Collectibles, quarries, excavators and survey charges have no tool swing involved,
            // so a fixed amount behaves exactly as you would expect there.
            if (prefab != null && !IsToolGathered(prefab))
                return "";

            return "\nNote: fixed and ranged amounts ignore the tool's gather damage, but the node still "
                   + "depletes at the tool's rate - so a jackhammer empties it in fewer hits and yields LESS "
                   + "in total than a rock. Safe on collectibles, quarries and excavators; on nodes and trees "
                   + "it inverts tool progression.";
        }

        private bool IsToolGathered(string prefab)
        {
            var entity = _toolGathered.Contains(prefab);
            return entity;
        }

        private readonly HashSet<string> _toolGathered = new HashSet<string>();

        // Prefab sets by gather path, so GatherManager's five per-category tables can be mapped onto
        // the prefabs each category actually covers.
        private readonly HashSet<string> _collectiblePrefabs = new HashSet<string>();
        private readonly HashSet<string> _growablePrefabs = new HashSet<string>();
        private readonly HashSet<string> _quarryPrefabs = new HashSet<string>();
        private readonly HashSet<string> _excavatorPrefabs = new HashSet<string>();
        private readonly HashSet<string> _surveyPrefabs = new HashSet<string>();

        private string DisplayName(string shortName)
        {
            string display;
            return _itemNames.TryGetValue(shortName, out display) ? display : shortName;
        }

        #endregion

        #region Mutation + API

        // Every mutation funnels through here so the config is saved once and every open editor is
        // told to re-read. _configVersion lets a UI notice it is looking at stale values.
        private int _configVersion;

        private void CommitChange()
        {
            _configVersion++;
            SaveConfig();
            BroadcastChanged();
        }

        private void BroadcastChanged() => Interface.CallHook("OnGatherRatesPlusChanged", _configVersion);

        private bool AddRuleset(string name, float rate)
        {
            if (string.IsNullOrWhiteSpace(name) || _config.FindRuleset(name) != null)
                return false;

            var ruleset = new GatherRateRuleset { Name = name, DefaultRate = rate };
            ruleset.Init(this, _validDispensers, isDefault: false);
            _config.GatherRateRulesets.Add(ruleset);
            CommitChange();
            return true;
        }

        private bool RemoveRuleset(string name)
        {
            var ruleset = _config.GatherRateRulesets
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (ruleset == null)
                return false;

            _config.GatherRateRulesets.Remove(ruleset);

            foreach (var zone in _config.Zones.Where(z =>
                         string.Equals(z.RulesetName, name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                zone.Enabled = false;
            }

            CommitChange();
            return true;
        }

        private bool SetDefaultRate(string rulesetName, float rate)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null || rate < 0f)
                return false;

            ruleset.DefaultRate = rate;
            CommitChange();
            return true;
        }

        private bool SetItemRate(string rulesetName, string itemShortName, RateSpec spec)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
                return false;

            if (spec == null)
                ruleset.ItemRateOverrides.Remove(itemShortName);
            else
                ruleset.ItemRateOverrides[itemShortName] = spec;

            CommitChange();
            return true;
        }

        private bool SetDispenserRate(string rulesetName, string prefab, string itemShortName, RateSpec spec)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null)
                return false;

            Dictionary<string, RateSpec> itemRates;
            if (!ruleset.DispenserRateOverrides.TryGetValue(prefab, out itemRates) || itemRates == null)
            {
                if (spec == null)
                    return false;

                itemRates = new Dictionary<string, RateSpec>();
                ruleset.DispenserRateOverrides[prefab] = itemRates;
            }

            if (spec == null)
            {
                itemRates.Remove(itemShortName);
                if (itemRates.Count == 0)
                    ruleset.DispenserRateOverrides.Remove(prefab);
            }
            else
            {
                itemRates[itemShortName] = spec;
            }

            CommitChange();
            return true;
        }

        private bool SetSpeed(string key, float value)
        {
            switch (key)
            {
                case "MiningQuarryTickRate": _config.Speed.MiningQuarryTickRate = value; break;
                case "ExcavatorTickRate": _config.Speed.ExcavatorTickRate = value; break;
                case "ExcavatorBeltSpeedMax": _config.Speed.ExcavatorBeltSpeedMax = value; break;
                case "ExcavatorTimeForFullResources": _config.Speed.ExcavatorTimeForFullResources = value; break;
                default: return false;
            }

            ReapplySpeedSettings();
            CommitChange();
            return true;
        }

        private string AddZone(string rulesetName, Vector3 position, float radius)
        {
            if (!ZonesAvailable || _config.FindRuleset(rulesetName) == null)
                return null;

            var zone = new ZoneDefinition
            {
                ZoneId = $"GRP_{rulesetName}_{DateTime.UtcNow.Ticks}",
                RulesetName = rulesetName,
                Radius = radius,
                X = position.x,
                Y = position.y,
                Z = position.z,
            };

            if (!CreateOrUpdateZone(zone))
            {
                EraseZone(zone.ZoneId);
                return null;
            }

            _config.Zones.Add(zone);
            CommitChange();
            return zone.ZoneId;
        }

        private bool RemoveZone(string zoneId)
        {
            var zone = _config.FindZone(zoneId);
            if (zone == null)
                return false;

            EraseZone(zoneId);
            _config.Zones.Remove(zone);
            CommitChange();
            return true;
        }

        private bool SetZoneField(string zoneId, string field, string value)
        {
            var zone = _config.FindZone(zoneId);
            if (zone == null || field == null)
                return false;

            float number;
            bool flag;

            switch (field.ToLowerInvariant())
            {
                case "ruleset":
                    if (_config.FindRuleset(value) == null) return false;
                    zone.RulesetName = value;
                    break;

                case "radius":
                    if (!TryFloat(value, out number) || number <= 0f) return false;
                    zone.Radius = number;
                    CreateOrUpdateZone(zone);
                    break;

                case "enabled":
                    if (!bool.TryParse(value, out flag)) return false;
                    zone.Enabled = flag;
                    if (flag) CreateOrUpdateZone(zone); else EraseZone(zone.ZoneId);
                    break;

                case "override":
                    if (!bool.TryParse(value, out flag)) return false;
                    zone.OverridePermission = flag;
                    break;

                case "requireperm":
                    if (!bool.TryParse(value, out flag)) return false;
                    zone.RequirePermission = flag;
                    break;

                case "enter":
                    zone.EnterMessage = value ?? "";
                    break;

                case "leave":
                    zone.LeaveMessage = value ?? "";
                    break;

                default:
                    return false;
            }

            CommitChange();
            return true;
        }

        // ---- Called by GatherRatesPlusUI. Data crosses the plugin boundary as JSON so the two
        // ---- plugins never have to agree on compiled types.

        private string API_GetSnapshot()
        {
            var payload = new JObject
            {
                ["version"] = _configVersion,
                ["zonesAvailable"] = ZonesAvailable,
                ["round"] = _config.RoundInsteadOfTruncate,
                ["speed"] = JObject.FromObject(_config.Speed),
                ["default"] = JObject.FromObject(_config.DefaultRuleset),
                ["rulesets"] = JArray.FromObject(_config.GatherRateRulesets),
                ["zones"] = JArray.FromObject(_config.Zones),
                ["bonusOverrides"] = JObject.FromObject(_config.BonusOverrides),
                ["bonusItemChances"] = JObject.FromObject(_config.BonusItemChances),
            };

            return payload.ToString(Formatting.None);
        }

        private string API_ListDispensers() => JsonConvert.SerializeObject(_validDispensers.OrderBy(x => x));

        // item shortname -> { display, sources[] }. The editor uses this instead of the full item
        // list so it never offers something that cannot be gathered.
        private string API_ListGatherableItems()
        {
            var payload = new JObject();

            foreach (var entry in _itemSources.OrderBy(x => x.Key))
            {
                payload[entry.Key] = new JObject
                {
                    ["display"] = DisplayName(entry.Key),
                    ["sources"] = new JArray(entry.Value.OrderBy(x => x).Cast<object>().ToArray()),
                };
            }

            return payload.ToString(Formatting.None);
        }

        private string API_ListDispenserItems()
            => JsonConvert.SerializeObject(
                _dispenserItems.ToDictionary(x => x.Key, x => x.Value.OrderBy(y => y).ToList()));

        private string API_ListItems()
            => JsonConvert.SerializeObject(_itemNames.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value));

        private bool API_AddRuleset(string name, float rate) => AddRuleset(name, rate);

        private bool API_RemoveRuleset(string name) => RemoveRuleset(name);

        private bool API_SetDefaultRate(string ruleset, float rate) => SetDefaultRate(ruleset, rate);

        // Takes the same text the console does: "2", "=5", "=5-10".
        private bool API_SetItemRate(string ruleset, string item, string raw)
        {
            var spec = RateSpec.Parse(raw);
            return spec != null && SetItemRate(ruleset, item, spec);
        }

        private bool API_ClearItemRate(string ruleset, string item) => SetItemRate(ruleset, item, null);

        private bool API_SetDispenserRate(string ruleset, string prefab, string item, string raw)
        {
            var spec = RateSpec.Parse(raw);
            return spec != null && SetDispenserRate(ruleset, prefab, item, spec);
        }

        // Tells the editor whether a dispenser is struck with a tool, so it can warn about fixed
        // amounts inverting tool progression there.
        private bool API_IsToolGathered(string prefab) => _toolGathered.Contains(prefab);

        private bool API_ClearDispenserRate(string ruleset, string prefab, string item)
            => SetDispenserRate(ruleset, prefab, item, null);

        // Bulk apply, so setting 200 filtered items to 5x is one save and one redraw rather than 200.
        private int API_SetItemRatesBulk(string rulesetName, string itemsJson, string raw)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            var spec = RateSpec.Parse(raw);
            if (ruleset == null || spec == null || string.IsNullOrEmpty(itemsJson))
                return 0;

            List<string> items;
            try
            {
                items = JsonConvert.DeserializeObject<List<string>>(itemsJson);
            }
            catch
            {
                return 0;
            }

            if (items == null || items.Count == 0)
                return 0;

            var changed = 0;
            foreach (var shortName in items)
            {
                if (ItemManager.FindItemDefinition(shortName) == null)
                    continue;

                ruleset.ItemRateOverrides[shortName] = spec;
                changed++;
            }

            if (changed > 0)
                CommitChange();

            return changed;
        }

        private int API_ClearItemRatesBulk(string rulesetName, string itemsJson)
        {
            var ruleset = _config.FindRuleset(rulesetName);
            if (ruleset == null || string.IsNullOrEmpty(itemsJson))
                return 0;

            List<string> items;
            try
            {
                items = JsonConvert.DeserializeObject<List<string>>(itemsJson);
            }
            catch
            {
                return 0;
            }

            var changed = items?.Count(x => ruleset.ItemRateOverrides.Remove(x)) ?? 0;
            if (changed > 0)
                CommitChange();

            return changed;
        }

        // Vanilla bonus tables: prefab -> { onlyAwardOne, entries[] }. The editor pairs these with
        // the overrides from the snapshot to show the effective values.
        private string API_ListBonusTables()
        {
            var payload = new JObject();

            foreach (var entry in _bonusTables.OrderBy(x => x.Key))
            {
                var entries = new JArray();
                foreach (var bonus in entry.Value.Entries)
                {
                    entries.Add(new JObject
                    {
                        ["item"] = bonus.Item,
                        ["amount"] = bonus.Amount,
                        ["chance"] = bonus.Chance,
                    });
                }

                payload[entry.Key] = new JObject
                {
                    ["onlyAwardOne"] = entry.Value.OnlyAwardOne,
                    ["entries"] = entries,
                };
            }

            return payload.ToString(Formatting.None);
        }

        private bool API_SetBonusAmount(string prefab, string item, int amount)
            => SetBonusOverride(prefab, item, amount, null, false);

        private bool API_SetBonusChance(string prefab, string item, float percent)
            => SetBonusOverride(prefab, item, null, percent, false);

        private bool API_ClearBonus(string prefab, string item)
            => SetBonusOverride(prefab, item, null, null, true);

        private bool API_SetBonusItemChance(string item, float percent)
            => SetBonusItemChance(item, percent);

        private bool API_ClearBonusItemChance(string item)
            => SetBonusItemChance(item, null);

        private bool API_SetBonusOnlyAwardOne(string prefab, bool value)
            => SetBonusOnlyAwardOne(prefab, value);

        private bool API_ClearBonusOnlyAwardOne(string prefab)
            => SetBonusOnlyAwardOne(prefab, null);

        private bool API_SetSpeed(string key, float value) => SetSpeed(key, value);

        private bool API_SetRounding(bool round)
        {
            _config.RoundInsteadOfTruncate = round;
            CommitChange();
            return true;
        }

        private string API_AddZone(string ruleset, float x, float y, float z, float radius)
            => AddZone(ruleset, new Vector3(x, y, z), radius);

        private bool API_RemoveZone(string zoneId) => RemoveZone(zoneId);

        private bool API_SetZoneField(string zoneId, string field, string value) => SetZoneField(zoneId, field, value);

        private bool API_ZonesAvailable() => ZonesAvailable;

        #endregion

        #region Localization

        private string Lang(string key, string userId = null) => lang.GetMessage(key, this, userId);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this command.",
                ["RatesHeader"] = "<color=#8ac6f2>Gather rates that apply to you:</color>",
                ["BaseRate"] = "Everything else",
                ["InZone"] = "  You are inside a gather zone using ruleset '{ruleset}'.",
                ["AdminHint"] = "Admin: /gatherrates for commands, /grpui for the editor.",
                ["HelpText"] = "/gather - shows the gather rates that apply to you.",
                ["ZonesUnavailable"] = "ZoneManager is not loaded - gather zones are disabled.",
            }, this);
        }

        #endregion

        #region Bonus Rolls

        // Vanilla snapshot of one prefab's RandomItemDispenser table.
        private class BonusTable
        {
            public bool OnlyAwardOne;
            public List<BonusEntry> Entries = new List<BonusEntry>();
        }

        private class BonusEntry
        {
            public string Item;
            public int Amount;
            public float Chance;   // 0-1, as the game stores it
        }

        private class BonusOverride
        {
            [JsonProperty("OnlyAwardOne", NullValueHandling = NullValueHandling.Ignore)]
            public bool? OnlyAwardOne;

            [JsonProperty("Items")]
            public Dictionary<string, BonusItemOverride> Items = new Dictionary<string, BonusItemOverride>();
        }

        private class BonusItemOverride
        {
            [JsonProperty("Amount", NullValueHandling = NullValueHandling.Ignore)]
            public int? Amount;

            // Stored as a percentage because that is how it is edited and displayed.
            [JsonProperty("ChancePercent", NullValueHandling = NullValueHandling.Ignore)]
            public float? ChancePercent;
        }

        // RandomItemDispenser is a single shared object per prefab, so it cannot hold per-player
        // values. Instead of reimplementing the award loop (which is where upstream GatherRates
        // drifted from vanilla), swap in this player's numbers, let the real method run, then put
        // the originals back in a finalizer so it restores even if the original throws.
        [HarmonyPatch(typeof(RandomItemDispenser), nameof(RandomItemDispenser.DistributeItems))]
        internal static class DistributeItemsPatch
        {
            private static RandomItemDispenser.RandomItemChance[] _savedChances;
            private static bool _savedOnlyAwardOne;
            private static bool _applied;

            private static void Prefix(RandomItemDispenser __instance, BasePlayer forPlayer)
            {
                _applied = false;

                var instance = Instance;
                if (instance == null || __instance?.Chances == null)
                    return;

                _applied = instance.ApplyBonusAdjustments(__instance, forPlayer,
                    out _savedChances, out _savedOnlyAwardOne);
            }

            private static void Finalizer(RandomItemDispenser __instance)
            {
                if (!_applied || __instance == null)
                    return;

                __instance.Chances = _savedChances;
                __instance.OnlyAwardOne = _savedOnlyAwardOne;
                _applied = false;
            }
        }

        // Rates only ever change the AMOUNT. The chance is vanilla unless an explicit percentage has
        // been set for that dispenser, so a blanket base rate never inflates how often bait drops.
        private bool ApplyBonusAdjustments(RandomItemDispenser dispenser, BasePlayer player,
            out RandomItemDispenser.RandomItemChance[] savedChances, out bool savedOnlyAwardOne)
        {
            savedChances = dispenser.Chances;
            savedOnlyAwardOne = dispenser.OnlyAwardOne;

            string prefab;
            if (!_bonusPrefabById.TryGetValue(dispenser.prefabID, out prefab))
                return false;

            BonusOverride over;
            _config.BonusOverrides.TryGetValue(prefab, out over);

            var ruleset = GetRuleset(player?.UserIDString,
                player == null ? Vector3.zero : player.transform.position);

            var adjusted = (RandomItemDispenser.RandomItemChance[])savedChances.Clone();
            var changed = false;

            for (var i = 0; i < adjusted.Length; i++)
            {
                var definition = adjusted[i].Item;
                if (definition == null)
                    continue;

                BonusItemOverride itemOverride = null;
                if (over?.Items != null)
                    over.Items.TryGetValue(definition.shortname, out itemOverride);

                if (itemOverride?.Amount != null)
                {
                    adjusted[i].Amount = Mathf.Max(0, itemOverride.Amount.Value);
                    changed = true;
                }
                else if (ruleset != null)
                {
                    var spec = ruleset.GetRateSpec(definition, prefab);
                    if (!spec.IsNoop)
                    {
                        adjusted[i].Amount = spec.Apply(savedChances[i].Amount, _config.RoundInsteadOfTruncate);
                        changed = true;
                    }
                }

                // Chance precedence mirrors the rate chain: this dispenser's explicit value, then
                // the item's base chance, then whatever the prefab shipped with.
                float itemChance;
                if (itemOverride?.ChancePercent != null)
                {
                    adjusted[i].Chance = Mathf.Clamp01(itemOverride.ChancePercent.Value / 100f);
                    changed = true;
                }
                else if (_config.BonusItemChances.TryGetValue(definition.shortname, out itemChance))
                {
                    adjusted[i].Chance = Mathf.Clamp01(itemChance / 100f);
                    changed = true;
                }
            }

            if (over?.OnlyAwardOne != null && over.OnlyAwardOne.Value != savedOnlyAwardOne)
            {
                dispenser.OnlyAwardOne = over.OnlyAwardOne.Value;
                changed = true;
            }

            if (!changed)
                return false;

            dispenser.Chances = adjusted;
            return true;
        }

        private bool SetBonusOverride(string prefab, string item, int? amount, float? chancePercent, bool clear)
        {
            if (!_bonusTables.ContainsKey(prefab))
                return false;

            BonusOverride over;
            if (!_config.BonusOverrides.TryGetValue(prefab, out over) || over == null)
            {
                if (clear)
                    return false;

                over = new BonusOverride();
                _config.BonusOverrides[prefab] = over;
            }

            if (clear)
            {
                over.Items.Remove(item);
                if (over.Items.Count == 0 && over.OnlyAwardOne == null)
                    _config.BonusOverrides.Remove(prefab);

                CommitChange();
                return true;
            }

            BonusItemOverride itemOverride;
            if (!over.Items.TryGetValue(item, out itemOverride) || itemOverride == null)
            {
                itemOverride = new BonusItemOverride();
                over.Items[item] = itemOverride;
            }

            if (amount != null)
                itemOverride.Amount = Mathf.Max(0, amount.Value);

            if (chancePercent != null)
                itemOverride.ChancePercent = Mathf.Clamp(chancePercent.Value, 0f, 100f);

            CommitChange();
            return true;
        }

        // Global base chance for one bonus item across every dispenser that rolls it.
        private bool SetBonusItemChance(string item, float? percent)
        {
            if (string.IsNullOrEmpty(item) || !_bonusTables.Values.Any(t => t.Entries.Any(e => e.Item == item)))
                return false;

            if (percent == null)
                _config.BonusItemChances.Remove(item);
            else
                _config.BonusItemChances[item] = Mathf.Clamp(percent.Value, 0f, 100f);

            CommitChange();
            return true;
        }

        private bool SetBonusOnlyAwardOne(string prefab, bool? value)
        {
            if (!_bonusTables.ContainsKey(prefab))
                return false;

            BonusOverride over;
            if (!_config.BonusOverrides.TryGetValue(prefab, out over) || over == null)
            {
                if (value == null)
                    return false;

                over = new BonusOverride();
                _config.BonusOverrides[prefab] = over;
            }

            over.OnlyAwardOne = value;

            if (value == null && over.Items.Count == 0)
                _config.BonusOverrides.Remove(prefab);

            CommitChange();
            return true;
        }

        #endregion

        #region Rate Specs

        private enum RateMode { Multiply, Fixed, Range }

        // A rate entry is either a plain multiplier (serialised as a bare number, so every existing
        // config keeps working untouched) or an object describing a fixed amount or a random range.
        [JsonConverter(typeof(RateSpecConverter))]
        private class RateSpec
        {
            public RateMode Mode = RateMode.Multiply;
            public float Value = 1f;
            public int Min = 1;
            public int Max = 1;

            public static RateSpec Multiply(float value) => new RateSpec { Mode = RateMode.Multiply, Value = value };

            public static RateSpec FixedAmount(int amount)
                => new RateSpec { Mode = RateMode.Fixed, Value = amount, Min = amount, Max = amount };

            public static RateSpec RangeAmount(int min, int max)
                => new RateSpec { Mode = RateMode.Range, Min = Mathf.Min(min, max), Max = Mathf.Max(min, max) };

            public bool IsNoop => Mode == RateMode.Multiply && Mathf.Approximately(Value, 1f);

            public int Apply(int vanillaAmount, bool round)
            {
                switch (Mode)
                {
                    case RateMode.Fixed:
                        return Mathf.Max(0, Mathf.RoundToInt(Value));

                    case RateMode.Range:
                        // Max is inclusive: "5-10" can roll a 10.
                        return Mathf.Max(0, UnityEngine.Random.Range(Min, Max + 1));

                    default:
                        if (vanillaAmount <= 0 || float.IsNaN(Value) || Value < 0f)
                            return vanillaAmount;

                        var scaled = vanillaAmount * Value;
                        if (float.IsNaN(scaled))
                            return vanillaAmount;

                        if (float.IsPositiveInfinity(scaled) || scaled >= int.MaxValue)
                            return int.MaxValue;

                        var result = round ? Mathf.RoundToInt(scaled) : Mathf.FloorToInt(scaled);
                        return Value >= 1f ? Mathf.Max(1, result) : Mathf.Max(0, result);
                }
            }

            public override string ToString()
            {
                switch (Mode)
                {
                    case RateMode.Fixed: return "=" + Mathf.RoundToInt(Value);
                    case RateMode.Range: return $"={Min}-{Max}";
                    default: return "x" + Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Accepts "2" / "2.5" (multiply), "=5" (fixed), "=5-10" (range).
            public static RateSpec Parse(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return null;

                raw = raw.Trim();

                if (raw.StartsWith("="))
                {
                    var body = raw.Substring(1).Trim();
                    var dash = body.IndexOf('-');

                    if (dash > 0)
                    {
                        int min, max;
                        if (int.TryParse(body.Substring(0, dash).Trim(), out min)
                            && int.TryParse(body.Substring(dash + 1).Trim(), out max)
                            && min >= 0 && max >= 0)
                            return RangeAmount(min, max);

                        return null;
                    }

                    int amount;
                    return int.TryParse(body, out amount) && amount >= 0 ? FixedAmount(amount) : null;
                }

                float multiplier;
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier)
                    && multiplier >= 0f)
                    return Multiply(multiplier);

                return null;
            }
        }

        private class RateSpecConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(RateSpec);

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var token = JToken.Load(reader);

                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                    return RateSpec.Multiply(token.Value<float>());

                var obj = token as JObject;
                if (obj == null)
                    return RateSpec.Multiply(1f);

                var mode = RateMode.Multiply;
                var modeRaw = obj.Value<string>("Mode");
                if (!string.IsNullOrEmpty(modeRaw))
                    Enum.TryParse(modeRaw, true, out mode);

                switch (mode)
                {
                    case RateMode.Fixed:
                        return RateSpec.FixedAmount(obj.Value<int?>("Amount") ?? 1);
                    case RateMode.Range:
                        return RateSpec.RangeAmount(obj.Value<int?>("Min") ?? 1, obj.Value<int?>("Max") ?? 1);
                    default:
                        return RateSpec.Multiply(obj.Value<float?>("Rate") ?? 1f);
                }
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var spec = (RateSpec)value;

                // Plain multipliers stay bare numbers so the config reads the way it always did.
                if (spec.Mode == RateMode.Multiply)
                {
                    writer.WriteValue(spec.Value);
                    return;
                }

                writer.WriteStartObject();
                writer.WritePropertyName("Mode");
                writer.WriteValue(spec.Mode.ToString());

                if (spec.Mode == RateMode.Fixed)
                {
                    writer.WritePropertyName("Amount");
                    writer.WriteValue(Mathf.RoundToInt(spec.Value));
                }
                else
                {
                    writer.WritePropertyName("Min");
                    writer.WriteValue(spec.Min);
                    writer.WritePropertyName("Max");
                    writer.WriteValue(spec.Max);
                }

                writer.WriteEndObject();
            }
        }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("DefaultRuleset")]
            public GatherRateRuleset DefaultRuleset = new GatherRateRuleset();

            [JsonProperty("GatherRateRulesets")]
            public List<GatherRateRuleset> GatherRateRulesets = new List<GatherRateRuleset>();

            [JsonProperty("Zones")]
            public List<ZoneDefinition> Zones = new List<ZoneDefinition>();

            [JsonProperty("Speed")]
            public SpeedSettings Speed = new SpeedSettings();

            [JsonProperty("RoundInsteadOfTruncate")]
            public bool RoundInsteadOfTruncate = true;

            // Absolute overrides for bonus-roll tables, keyed by dispenser short prefab name.
            // Global by nature: the underlying table is shared, so these cannot vary per ruleset.
            [JsonProperty("BonusOverrides")]
            public Dictionary<string, BonusOverride> BonusOverrides = new Dictionary<string, BonusOverride>();

            // Base chance per bonus item, as a percentage, applied to every dispenser that rolls it
            // unless that dispenser has its own explicit chance. Flattens vanilla's per-prefab tiers.
            [JsonProperty("BonusItemChances")]
            public Dictionary<string, float> BonusItemChances = new Dictionary<string, float>();

            public void Init(GatherRatesPlus plugin, HashSet<string> validDispensers)
            {
                DefaultRuleset.Init(plugin, validDispensers, isDefault: true);

                if (GatherRateRulesets == null)
                    GatherRateRulesets = new List<GatherRateRuleset>();

                foreach (var ruleset in GatherRateRulesets)
                    ruleset.Init(plugin, validDispensers, isDefault: false);

                if (Zones == null)
                    Zones = new List<ZoneDefinition>();

                if (BonusOverrides == null)
                    BonusOverrides = new Dictionary<string, BonusOverride>();

                if (BonusItemChances == null)
                    BonusItemChances = new Dictionary<string, float>();
            }

            public GatherRateRuleset FindRuleset(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return null;

                if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                    return DefaultRuleset;

                return GatherRateRulesets?.FirstOrDefault(
                    x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            public ZoneDefinition FindZone(string zoneId)
            {
                return Zones?.FirstOrDefault(x => string.Equals(x?.ZoneId, zoneId, StringComparison.Ordinal));
            }
        }

        private class GatherRateRuleset
        {
            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

            [JsonProperty("DefaultRate")]
            public float DefaultRate = 1f;

            [JsonProperty("ItemRateOverrides", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Dictionary<string, RateSpec> ItemRateOverrides = new Dictionary<string, RateSpec>();

            [JsonProperty("DispenserRateOverrides", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Dictionary<string, Dictionary<string, RateSpec>> DispenserRateOverrides
                = new Dictionary<string, Dictionary<string, RateSpec>>();

            public void Init(GatherRatesPlus plugin, HashSet<string> validDispensers, bool isDefault)
            {
                if (!isDefault && !string.IsNullOrWhiteSpace(Name))
                    plugin.permission.RegisterPermission(GetRulesetPermission(Name), plugin);

                if (ItemRateOverrides == null)
                    ItemRateOverrides = new Dictionary<string, RateSpec>();

                if (DispenserRateOverrides == null)
                    DispenserRateOverrides = new Dictionary<string, Dictionary<string, RateSpec>>();

                foreach (var shortName in ItemRateOverrides.Keys.ToList())
                {
                    if (ItemManager.FindItemDefinition(shortName) == null)
                        plugin.PrintWarning($"[{Name ?? "Default"}] Unknown item short name in config: '{shortName}'");
                }

                foreach (var entry in DispenserRateOverrides)
                {
                    if (validDispensers.Count > 0 && !validDispensers.Contains(entry.Key))
                        plugin.PrintWarning($"[{Name ?? "Default"}] Unknown dispenser short prefab name in config: '{entry.Key}'");

                    if (entry.Value == null)
                        continue;

                    foreach (var shortName in entry.Value.Keys)
                    {
                        if (ItemManager.FindItemDefinition(shortName) == null)
                            plugin.PrintWarning($"[{Name ?? "Default"}] Unknown item short name in config: '{shortName}'");
                    }
                }
            }

            // Most specific first: this dispenser + this item, then this item anywhere, then the
            // ruleset's base multiplier. First match wins - they never combine.
            public RateSpec GetRateSpec(ItemDefinition itemDefinition, string shortPrefabName)
            {
                Dictionary<string, RateSpec> itemRates;
                RateSpec spec;

                if (shortPrefabName != null
                    && DispenserRateOverrides != null
                    && DispenserRateOverrides.TryGetValue(shortPrefabName, out itemRates)
                    && itemRates != null
                    && itemRates.TryGetValue(itemDefinition.shortname, out spec)
                    && spec != null)
                    return spec;

                if (ItemRateOverrides != null
                    && ItemRateOverrides.TryGetValue(itemDefinition.shortname, out spec)
                    && spec != null)
                    return spec;

                return RateSpec.Multiply(DefaultRate);
            }
        }

        private class ZoneDefinition
        {
            [JsonProperty("ZoneId")]
            public string ZoneId;

            [JsonProperty("RulesetName")]
            public string RulesetName;

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Radius")]
            public float Radius = 50f;

            [JsonProperty("X")] public float X;
            [JsonProperty("Y")] public float Y;
            [JsonProperty("Z")] public float Z;

            [JsonProperty("EnterMessage")]
            public string EnterMessage = "";

            [JsonProperty("LeaveMessage")]
            public string LeaveMessage = "";

            // When true this zone's ruleset beats the player's permission ruleset outright.
            [JsonProperty("OverridePermission")]
            public bool OverridePermission;

            // When true the player must also hold the zone ruleset's permission for it to apply.
            [JsonProperty("RequirePermission")]
            public bool RequirePermission;

            [JsonIgnore]
            public Vector3 Position => new Vector3(X, Y, Z);

            public bool Contains(Vector3 point)
            {
                // Compared on the horizontal plane: zone height is not meaningful for gathering and
                // ZoneManager spheres are authored by radius anyway.
                var dx = point.x - X;
                var dz = point.z - Z;
                return dx * dx + dz * dz <= Radius * Radius;
            }
        }

        private class SpeedSettings
        {
            [JsonProperty("MiningQuarryTickRate")]
            public float MiningQuarryTickRate = VanillaQuarryTickRate;

            [JsonProperty("ExcavatorTickRate")]
            public float ExcavatorTickRate = VanillaExcavatorTickRate;

            [JsonProperty("ExcavatorBeltSpeedMax")]
            public float ExcavatorBeltSpeedMax = VanillaExcavatorBeltSpeedMax;

            [JsonProperty("ExcavatorTimeForFullResources")]
            public float ExcavatorTimeForFullResources = VanillaExcavatorTimeForFullResources;

            [JsonIgnore]
            public bool QuarryTickRateChanged => !Mathf.Approximately(MiningQuarryTickRate, VanillaQuarryTickRate);

            [JsonIgnore]
            public bool ExcavatorTickRateChanged => !Mathf.Approximately(ExcavatorTickRate, VanillaExcavatorTickRate);

            [JsonIgnore]
            public bool ExcavatorBeltSpeedChanged => !Mathf.Approximately(ExcavatorBeltSpeedMax, VanillaExcavatorBeltSpeedMax);

            [JsonIgnore]
            public bool ExcavatorTimeForFullResourcesChanged
                => !Mathf.Approximately(ExcavatorTimeForFullResources, VanillaExcavatorTimeForFullResources);
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch (Exception ex)
            {
                PrintError($"Configuration file is invalid, using defaults: {ex.Message}");
                _config = new Configuration();
            }

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty("QuarryStarters")]
            public Dictionary<ulong, string> QuarryStarters = new Dictionary<ulong, string>();

            [JsonProperty("ExcavatorStarters")]
            public Dictionary<ulong, string> ExcavatorStarters = new Dictionary<ulong, string>();

            [JsonProperty("SurveyThrowers")]
            public Dictionary<ulong, string> SurveyThrowers = new Dictionary<ulong, string>();

            // Dispenser short prefab name -> item short names actually seen dropping from it.
            [JsonProperty("LearnedSources")]
            public Dictionary<string, List<string>> LearnedSources = new Dictionary<string, List<string>>();

            [JsonIgnore]
            public bool Dirty;

            public static StoredData Load() =>
                Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(GatherRatesPlus)) ?? new StoredData();

            public static StoredData Clear() => new StoredData().Save();

            public StoredData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(nameof(GatherRatesPlus), this);
                return this;
            }
        }

        #endregion
    }
}

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ItemDataManager;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BlacksmithingExpanded
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BlacksmithingExpanded : BaseUnityPlugin
    {
        internal const string ModName = "Blacksmithing Expanded";
        internal const string ModVersion = "1.0.0";
        internal const string ModGUID = "org.bepinex.plugins.blacksmithingexpanded";

        private Harmony harmony;

        private static readonly ConfigSync configSync = new(ModGUID)
        {
            DisplayName = ModName,
            CurrentVersion = ModVersion,
            MinimumRequiredVersion = ModVersion,
            ModRequired = true
        };

        internal static Skill blacksmithSkill;
        private static readonly Dictionary<string, float> baseArmorLookup = new();
        private static readonly Dictionary<string, HitData.DamageTypes> baseDamageLookup = new();
        private static readonly Dictionary<string, float> baseDurabilityLookup = new();

        // Config entries
        internal static ConfigEntry<float> cfg_SkillGainFactor;
        internal static ConfigEntry<float> cfg_SkillEffectFactor;
        internal static ConfigEntry<int> cfg_TierInterval;
        internal static ConfigEntry<int> cfg_DamageBonusPerTier;
        internal static ConfigEntry<int> cfg_ArmorBonusPerTier;
        internal static ConfigEntry<int> cfg_DurabilityBonusPerTier;
        internal static ConfigEntry<int> cfg_UpgradeTierPer25Levels;
        internal static ConfigEntry<int> cfg_MaxTierUnlockLevel;
        internal static ConfigEntry<float> cfg_ChanceExtraItemAt100;
        internal static ConfigEntry<float> cfg_SmelterSaveOreChanceAt100;
        internal static ConfigEntry<bool> cfg_EnableInventoryRepair;
        internal static ConfigEntry<int> cfg_InventoryRepairUnlockLevel;
        internal static ConfigEntry<float> cfg_SmeltingSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_KilnSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_InfusionExpireTime;

        // XP config
        internal static ConfigEntry<float> cfg_XPPerCraft;
        internal static ConfigEntry<float> cfg_XPPerSmelt;
        internal static ConfigEntry<float> cfg_XPPerRepair;
        internal static Dictionary<ZDOID, (int tier, float timestamp)> smelterInfusions = new();
        internal static Dictionary<ZDOID, (int tier, float timestamp)> kilnInfusions = new();


        private static Sprite s_skillIcon;

        private ConfigEntry<T> AddConfig<T>(string group, string name, T value, string description, bool synchronized = true)
        {
            var entry = Config.Bind(group, name, value, description);
            var synced = configSync.AddConfigEntry(entry);
            synced.SynchronizedConfig = synchronized;
            return entry;
        }

        private void Awake()
        {
            harmony = new Harmony(ModGUID);

            // Load embedded sprite
            try
            {
                s_skillIcon = LoadEmbeddedSprite("smithing.png", 64, 64);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to load embedded sprite: {ex}");
                s_skillIcon = null;
            }

            // Register skill
            try
            {
                if (s_skillIcon != null)
                {
                    blacksmithSkill = new Skill("Blacksmithing", s_skillIcon)
                    {
                        Configurable = true
                    };
                }
                else
                {
                    blacksmithSkill = new Skill("Blacksmithing", "smithing.png")
                    {
                        Configurable = true
                    };
                }

                blacksmithSkill.Name.English("Blacksmithing");
                blacksmithSkill.Description.English(
                    "Craft better, last longer. Improves durability, damage, and armor of crafted items. Grants smelting and repair bonuses."
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to construct SkillManager skill: {ex}");
            }

            string group = "Blacksmithing";

            // Config entries
            cfg_SkillGainFactor = AddConfig(group, "Skill gain factor", blacksmithSkill?.SkillGainFactor ?? 1f, "Rate at which you gain Blacksmithing XP.");
            cfg_SkillEffectFactor = AddConfig(group, "Skill effect factor", blacksmithSkill?.SkillEffectFactor ?? 1f, "Multiplier applied to all skill effects.");
            cfg_TierInterval = AddConfig(group, "Tier interval (levels)", 10, "Levels required per tier (e.g. 10 = one tier every 10 levels).");
            cfg_DamageBonusPerTier = AddConfig(group, "Damage bonus per tier", 1, "Flat damage added to all damage types per tier.");
            cfg_ArmorBonusPerTier = AddConfig(group, "Armor bonus per tier", 2, "Flat armor added per tier.");
            cfg_DurabilityBonusPerTier = AddConfig(group, "Durability bonus per tier", 50, "Flat durability added per tier.");
            cfg_UpgradeTierPer25Levels = AddConfig(group, "Extra upgrade tiers per 25 levels", 1, "Extra upgrade tiers unlocked every 25 levels.");
            cfg_MaxTierUnlockLevel = AddConfig(group, "Max tier unlock level", 100, "Level at which full 'master' benefits are unlocked.");
            cfg_ChanceExtraItemAt100 = AddConfig(group, "Chance to craft extra item at 100", 0.05f, "Chance at level 100 to produce an extra copy when crafting. Scales with level.");
            cfg_SmelterSaveOreChanceAt100 = AddConfig(group, "Smelter save ore chance at 100", 0.2f, "Chance at level 100 that ore is not consumed (scales with level).");
            cfg_EnableInventoryRepair = AddConfig(group, "Enable inventory repair", true, "Allow repairing items from inventory after reaching unlock level.");
            cfg_InventoryRepairUnlockLevel = AddConfig(group, "Inventory repair unlock level", 70, "Blacksmithing level required to repair items from inventory.");
            cfg_SmeltingSpeedBonusPerTier = AddConfig(group, "Smelting speed bonus per tier", 0.05f, "Smelting speed multiplier per blacksmithing tier. Example: 0.05 = +5% faster per tier.");
            cfg_InfusionExpireTime = AddConfig(group, "Infusion expire time (seconds)", 60f, "How long a smelter or kiln retains the contributor's tier bonus before it expires.");
            // XP configs
            cfg_XPPerCraft = AddConfig(group, "XP per craft", 0.50f, "Base XP granted when crafting an item.");
            cfg_XPPerSmelt = AddConfig(group, "XP per smelt", 0.75f, "Base XP granted when adding ore to smelter (filling).");
            cfg_XPPerRepair = AddConfig(group, "XP per repair", 0.05f, "Base XP granted when repairing an item.");

            // Wire dynamic config changes
            if (blacksmithSkill != null)
            {
                blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
                cfg_SkillGainFactor.SettingChanged += (_, _) => blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                cfg_SkillEffectFactor.SettingChanged += (_, _) => blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
            }

            harmony.PatchAll();

            Logger.LogInfo($"{ModName} v{ModVersion} loaded.");
        }
        // -----------------------
        // Utilities
        // -----------------------
        internal static int GetPlayerBlacksmithingLevel(Player player)
        {
            try
            {
                if (player == null) return 0;
                var skills = player.GetComponent<Skills>();
                if (skills == null) return 0;
                var skillType = Skill.fromName("Blacksmithing");
                float lvl = skills.GetSkillLevel(skillType);
                return Mathf.FloorToInt(lvl);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] GetPlayerBlacksmithingLevel error: {ex}");
                return 0;
            }
        }
        public static class CoroutineRunner
        {
            private class CoroutineHost : MonoBehaviour { }

            private static CoroutineHost host;

            public static void RunLater(Action action, float delaySeconds = 0.1f)
            {
                if (host == null)
                {
                    var go = new GameObject("BlacksmithingCoroutineHost");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    host = go.AddComponent<CoroutineHost>();
                }

                host.StartCoroutine(RunDelayed(action, delaySeconds));
            }

            private static IEnumerator RunDelayed(Action action, float delay)
            {
                yield return new WaitForSeconds(delay);
                action?.Invoke();
            }
        }

        internal static void GiveBlacksmithingXP(Player player, float amount)
        {
            if (player == null || amount <= 0f) return;
            try
            {
                float adjusted = amount * cfg_SkillGainFactor.Value;
                SkillManager.SkillExtensions.RaiseSkill(player, "Blacksmithing", adjusted);
                Debug.Log($"[BlacksmithingExpanded] Gave {adjusted:F2} XP to {player.GetPlayerName()}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to raise skill XP for {player?.GetPlayerName()}: {ex}");
            }
        }

        internal static int GetExtraUpgradeTiers(int level) => (level / 25) * cfg_UpgradeTierPer25Levels.Value;
        internal static float GetChanceScaledWithLevel(float maxChanceAt100, int level) => Mathf.Clamp01(maxChanceAt100 * (level / 100f));

        // -----------------------
        // ItemDataManager integration
        // -----------------------
        public class BlacksmithingData : ItemData
        {
            public int blacksmithLevel = 0;

            public override void Save() => Value = blacksmithLevel.ToString();

            public override void Load()
            {
                if (!string.IsNullOrEmpty(Value))
                    int.TryParse(Value, out blacksmithLevel);
            }
        }

        /// <summary>
        /// Applies tier-based stat bonuses to the crafted item.
        /// </summary>
        internal static void ApplyCraftedItemMultipliers(ItemDrop.ItemData item, int level)
        {
            if (item == null || item.m_shared == null || level <= 0) return;

            CacheBaseStats(item);
            string key = item.m_shared.m_name;

            float baseArmor = baseArmorLookup[key];
            HitData.DamageTypes baseDamage = baseDamageLookup[key].Clone();
            float baseDurability = baseDurabilityLookup[key];

            int tiers = level / cfg_TierInterval.Value;
            int bonusPerTier = cfg_DamageBonusPerTier.Value;

            // Reset to base
            item.m_shared.m_damages = baseDamage.Clone();

            // List of damage type setters
            var damageSetters = new List<Action>
            {
            () => item.m_shared.m_damages.m_blunt     += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_slash     += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_pierce    += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_fire      += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_frost     += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_lightning += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_poison    += tiers * bonusPerTier,
            () => item.m_shared.m_damages.m_spirit    += tiers * bonusPerTier,
            };

            // Shuffle and pick N random types
            int typesToBoost = Math.Min(tiers, damageSetters.Count);
            var rng = new System.Random();
            var selected = damageSetters.OrderBy(_ => rng.Next()).Take(typesToBoost);

            foreach (var apply in selected)
                apply();

            item.m_shared.m_armor = Mathf.RoundToInt(baseArmor + (tiers * cfg_ArmorBonusPerTier.Value));
            item.m_shared.m_maxDurability = baseDurability + (tiers * cfg_DurabilityBonusPerTier.Value);
            item.m_durability = item.GetMaxDurability();

            Debug.Log($"[BlacksmithingExpanded] Applied tier {tiers} bonuses to {item.m_shared.m_name} with randomized damage types.");
        }
        internal static void ApplyRandomDamageBonuses(ItemDrop.ItemData item, int level)
        {
            if (item == null || item.m_shared == null || level <= 0) return;

            int tiers = level / cfg_TierInterval.Value;
            int bonusPerTier = cfg_DamageBonusPerTier.Value;

            var damageSetters = new List<(string name, Action)>
        {
        ("Blunt",     () => item.m_shared.m_damages.m_blunt     += tiers * bonusPerTier),
        ("Slash",     () => item.m_shared.m_damages.m_slash     += tiers * bonusPerTier),
        ("Pierce",    () => item.m_shared.m_damages.m_pierce    += tiers * bonusPerTier),
        ("Fire",      () => item.m_shared.m_damages.m_fire      += tiers * bonusPerTier),
        ("Frost",     () => item.m_shared.m_damages.m_frost     += tiers * bonusPerTier),
        ("Lightning", () => item.m_shared.m_damages.m_lightning += tiers * bonusPerTier),
        ("Poison",    () => item.m_shared.m_damages.m_poison    += tiers * bonusPerTier),
        ("Spirit",    () => item.m_shared.m_damages.m_spirit    += tiers * bonusPerTier),
        };

            var rng = new System.Random();
            var selected = damageSetters.OrderBy(_ => rng.Next()).Take(Math.Min(tiers, damageSetters.Count)).ToList();

            foreach (var (_, apply) in selected)
                apply();

            var boostedNames = string.Join(", ", selected.Select(s => s.name));
            Debug.Log($"[BlacksmithingExpanded] Boosted damage types: {boostedNames}");
        }


        /// <summary>
        /// Attach BlacksmithingData to the item, for persistent tooltip/stat display.
        /// </summary>
        internal static void AttachBlacksmithingData(ItemDrop.ItemData item, int level)
        {
            if (item == null) return;
            var dataManager = item.Data();
            if (dataManager == null)
            {
                Debug.LogWarning("[BlacksmithingExpanded] item.Data() returned null.");
                return;
            }
            var data = dataManager.Add<BlacksmithingData>();
            if (data == null)
            {
               // Debug.LogWarning("[BlacksmithingExpanded] Failed to attach BlacksmithingData.");
                return;
            }
            data.blacksmithLevel = level;
            data.Save();
        }
        // -----------------------
        // Harmony patches (crafting, smelter, tooltip, repair)
        // -----------------------

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        public static class Patch_Blacksmithing_Crafting
        {
            static void Postfix(InventoryGui __instance)
            {
                var player = Player.m_localPlayer;
                if (player == null) return;

                // Get the last item added to inventory
                var inventory = player.GetInventory();
                var craftedItem = player.GetInventory()?.GetAllItems().LastOrDefault();
                if (craftedItem == null || craftedItem.m_shared == null) return;

                int level = GetPlayerBlacksmithingLevel(player);
                if (level <= 0) return;

                CoroutineRunner.RunLater(() =>
                {
                    ApplyCraftedItemMultipliers(craftedItem, level);
                    AttachBlacksmithingData(craftedItem, level);
                }, 0.1f);

                GiveBlacksmithingXP(player, cfg_XPPerCraft.Value);

                float chance = GetChanceScaledWithLevel(cfg_ChanceExtraItemAt100.Value, level);
                if (UnityEngine.Random.value <= chance)
                {
                    TryGiveExtraItemToCrafter(craftedItem, player);
                }
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
            typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
        public static class Patch_Blacksmithing_Tooltip
        {
            public static void Postfix(ItemDrop.ItemData item, int qualityLevel, bool crafting, float worldLevel, int stackOverride, ref string __result)
            {
                if (crafting && Player.m_localPlayer != null)
                {
                    __result += "\n<color=orange>Forged stats will vary based on blacksmithing tier</color>";
                }
                var data = item.Data()?.Get<BlacksmithingData>();
                if (data != null && data.blacksmithLevel > 0)
                {
                    __result += $"\n<color=orange>Forged by level {data.blacksmithLevel} blacksmith</color>";
                }
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        public static class Patch_Blacksmithing_RecipePreview
        {
            static void Postfix(InventoryGui __instance)
            {
                // REMOVE THIS PATCH — no preview injection needed
            }
        }

        [HarmonyPatch]
        public static class Patch_Smelter_OnAddOre_Postfix
        {
            static MethodInfo TargetMethod()
            {
                var m = AccessTools.Method(typeof(Smelter), "OnAddOre", new Type[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) });
                return m ?? AccessTools.Method(typeof(Smelter), "OnAddOre");
            }

            static void Postfix(Smelter __instance, Switch sw, Humanoid user, ItemDrop.ItemData item, bool __result)
            {
                try
                {
                    if (!__result || user == null) return;

                    var player = user as Player;
                    if (player == null) return;

                    BlacksmithingExpanded.GiveBlacksmithingXP(player, BlacksmithingExpanded.cfg_XPPerSmelt.Value);

                    int level = BlacksmithingExpanded.GetPlayerBlacksmithingLevel(player);
                    if (level > 0)
                    {
                        float chance = BlacksmithingExpanded.GetChanceScaledWithLevel(BlacksmithingExpanded.cfg_SmelterSaveOreChanceAt100.Value, level);
                        if (UnityEngine.Random.value <= chance)
                        {
                            player.Message(MessageHud.MessageType.TopLeft, "Smelter efficiency saved some ore!", 0, null);
                        }
                    }

                    var zdo = __instance.m_nview?.GetZDO();
                    if (zdo != null)
                    {
                        var zdoid = zdo.m_uid;
                        int tier = BlacksmithingExpanded.GetPlayerBlacksmithingTier(player);
                        BlacksmithingExpanded.smelterInfusions[zdoid] = (tier, Time.time);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Smelter OnAddOre postfix error: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static class Patch_Smelter_UpdateSmelter
        {
            static void Prefix(Smelter __instance)
            {
                var zdo = __instance.m_nview?.GetZDO();
                if (zdo == null) return;

                var zdoid = zdo.m_uid;
                if (!BlacksmithingExpanded.smelterInfusions.TryGetValue(zdoid, out var infusion)) return;

                float expireTime = BlacksmithingExpanded.cfg_InfusionExpireTime.Value;
                if (__instance.GetQueueSize() == 0 || __instance.GetFuel() <= 0f || Time.time - infusion.Item2 > expireTime)
                {
                    BlacksmithingExpanded.smelterInfusions.Remove(zdoid);
                    return;
                }

                float speedBonus = infusion.Item1 * BlacksmithingExpanded.cfg_SmeltingSpeedBonusPerTier.Value;
                __instance.m_secPerProduct /= (1f + speedBonus);
            }
        }

        internal static int GetPlayerBlacksmithingTier(Player player)
        {
            int level = GetPlayerBlacksmithingLevel(player);
            return level / cfg_TierInterval.Value;
        }
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        public static class Patch_Kiln_OnAddWood
        {
            static void Postfix(Smelter __instance, Humanoid user, ItemDrop.ItemData item, bool __result)
            {
                try
                {
                    if (!__result || user == null || __instance == null) return;
                    if (__instance.m_name != "charcoal_kiln") return;

                    var player = user as Player;
                    if (player == null) return;

                    BlacksmithingExpanded.GiveBlacksmithingXP(player, BlacksmithingExpanded.cfg_XPPerSmelt.Value);

                    var zdo = __instance.m_nview?.GetZDO();
                    if (zdo != null)
                    {
                        var zdoid = zdo.m_uid;
                        int tier = BlacksmithingExpanded.GetPlayerBlacksmithingTier(player);
                        BlacksmithingExpanded.kilnInfusions[zdoid] = (tier, Time.time);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Kiln OnAddWood postfix error: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static class Patch_Kiln_UpdateKiln
        {
            static void Prefix(Smelter __instance)
            {
                try
                {
                    if (__instance == null || __instance.m_name != "charcoal_kiln") return;

                    var zdo = __instance.m_nview?.GetZDO();
                    if (zdo == null) return;

                    var zdoid = zdo.m_uid;
                    if (!BlacksmithingExpanded.kilnInfusions.TryGetValue(zdoid, out var infusion)) return;

                    float expireTime = BlacksmithingExpanded.cfg_InfusionExpireTime.Value;
                    if (__instance.GetQueueSize() == 0 || Time.time - infusion.Item2 > expireTime)
                    {
                        BlacksmithingExpanded.kilnInfusions.Remove(zdoid);
                        return;
                    }

                    float speedBonus = infusion.Item1 * BlacksmithingExpanded.cfg_KilnSpeedBonusPerTier.Value;
                    __instance.m_secPerProduct /= (1f + speedBonus);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Kiln UpdateSmelter prefix error: {ex}");
                }
            }
        }


        [HarmonyPatch(typeof(InventoryGui), "OnRepairPressed")]
        public static class Patch_Blacksmithing_OnRepairPressed
        {
            static bool Prefix(InventoryGui __instance)
            {
                var player = Player.m_localPlayer;
                if (player == null || !cfg_EnableInventoryRepair.Value) return true;

                var inventory = player.GetInventory();
                if (inventory == null) return true;

                int level = GetPlayerBlacksmithingLevel(player);
                if (level < cfg_InventoryRepairUnlockLevel.Value) return true;

                foreach (var item in inventory.GetAllItems())
                {
                    if (item?.m_shared?.m_maxDurability > 0 && item.m_durability < item.GetMaxDurability())
                    {
                        int tiers = level / cfg_TierInterval.Value;
                        float bonusAmount = tiers * cfg_DurabilityBonusPerTier.Value;
                        item.m_durability = Mathf.Min(item.m_durability + bonusAmount, item.GetMaxDurability());

                        GiveBlacksmithingXP(player, cfg_XPPerRepair.Value);

                        var fx = ZNetScene.instance.GetPrefab("vfx_Smelter_add");
                        if (fx != null)
                            UnityEngine.Object.Instantiate(fx, player.transform.position, Quaternion.identity);

                        player.Message(MessageHud.MessageType.TopLeft,
                            $"Repaired with masterwork precision (+{level} skill)", 0, null);

                        return false; // Stop after one repair
                    }
                }
                return true;
            }
        }
        private static void CacheBaseStats(ItemDrop.ItemData item)
        {
            string key = item.m_shared.m_name;
            if (!baseArmorLookup.ContainsKey(key))
            {
                baseArmorLookup[key] = item.m_shared.m_armor;
                baseDamageLookup[key] = item.m_shared.m_damages.Clone();
                baseDurabilityLookup[key] = item.m_shared.m_maxDurability;
            }
        }

        internal static void TryGiveExtraItemToCrafter(ItemDrop.ItemData item, Player crafter)
        {
            try
            {
                if (item == null || crafter == null) return;
                var shared = item.m_shared;
                if (shared == null) return;
                var inv = crafter.GetInventory();
                if (inv == null) return;

                inv.AddItem(shared.m_name, 1, 1, 0, crafter.GetPlayerID(), crafter.GetPlayerName());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] TryGiveExtraItemToCrafter error: {ex}");
            }
        }

        // -----------------------
        // Embedded resource loader
        // -----------------------
        private static Sprite LoadEmbeddedSprite(string resourceName, int width, int height)
        {
            byte[] bytes = null;
            using (MemoryStream ms = new MemoryStream())
            {
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BlacksmithingExpanded.icons." + resourceName);
                if (stream == null) throw new FileNotFoundException("Embedded resource not found: " + resourceName);
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            Texture2D tex = new Texture2D(0, 0);
            tex.LoadImage(bytes);
            return Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.zero);
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
    }
}

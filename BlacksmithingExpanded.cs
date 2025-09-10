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
using LocalizationManager;
using UnityEngine;

namespace BlacksmithingExpanded
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BlacksmithingExpanded : BaseUnityPlugin
    {
        internal const string ModName = "Blacksmithing Expanded";
        internal const string ModVersion = "1.0.1";
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

        // Base stat caches
        private static readonly Dictionary<string, float> baseArmorLookup = new();
        private static readonly Dictionary<string, HitData.DamageTypes> baseDamageLookup = new();
        private static readonly Dictionary<string, float> baseDurabilityLookup = new();

        // General config
        internal static ConfigEntry<float> cfg_SkillGainFactor;
        internal static ConfigEntry<float> cfg_SkillEffectFactor;
        internal static ConfigEntry<int> cfg_InfusionTierInterval;
        internal static ConfigEntry<int> cfg_MaxTierUnlockLevel;
        internal static ConfigEntry<float> cfg_ChanceExtraItemAt100;
        internal static ConfigEntry<float> cfg_SmelterSaveOreChanceAt100;
        internal static ConfigEntry<bool> cfg_EnableInventoryRepair;
        internal static ConfigEntry<int> cfg_InventoryRepairUnlockLevel;
        internal static ConfigEntry<float> cfg_SmeltingSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_KilnSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_InfusionExpireTime;
        internal static ConfigEntry<bool> cfg_ShowBlacksmithLevelInTooltip;
        internal static ConfigEntry<bool> cfg_ShowInfusionInTooltip;

        // Durability
        internal static ConfigEntry<int> cfg_DurabilityTierInterval;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerTier;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_RespectOriginalDurability;
        internal static ConfigEntry<float> cfg_MaxDurabilityCap;
        internal static ConfigEntry<bool> cfg_ShowDurabilityBonusInTooltip;

        // Armor
        internal static ConfigEntry<int> cfg_GearMilestoneInterval; // shared milestone interval for gear
        internal static ConfigEntry<float> cfg_ArmorBonusPerTier;
        internal static ConfigEntry<float> cfg_ArmorBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_ArmorCap;

        // Weapons
        internal static ConfigEntry<bool> cfg_RespectOriginalStats;
        internal static ConfigEntry<int> cfg_StatTierInterval;
        internal static ConfigEntry<int> cfg_DamageBonusPerTier;
        internal static ConfigEntry<float> cfg_StatBonusPerUpgrade;
        internal static ConfigEntry<int> cfg_MaxStatTypesPerTier;
        internal static ConfigEntry<float> cfg_StatBonusMultiplierPerTier;
        internal static ConfigEntry<float> cfg_StatBonusCapPerType;

        // Elemental
        internal static ConfigEntry<bool> cfg_AlwaysAddElementalAtMax;
        internal static ConfigEntry<int> cfg_ElementalUnlockLevel;
        internal static ConfigEntry<float> cfg_FireBonusAtMax;
        internal static ConfigEntry<float> cfg_FrostBonusAtMax;
        internal static ConfigEntry<float> cfg_LightningBonusAtMax;
        internal static ConfigEntry<float> cfg_PoisonBonusAtMax;
        internal static ConfigEntry<float> cfg_SpiritBonusAtMax;
        internal static ConfigEntry<float> cfg_ElementalBonusPerTier;

        // Shields
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerTier;
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerTier;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerUpgrade;

        // Other
        internal static ConfigEntry<bool> cfg_ApplyUpgradeBonusAtTierZero;

        // Gear (shared multipliers for armor & weapons)
        internal static ConfigEntry<float> cfg_GearBonusPerMilestone;
        internal static ConfigEntry<float> cfg_GearUpgradeBonusPerMilestone;

        // XP config
        internal static ConfigEntry<float> cfg_XPPerCraft;
        internal static ConfigEntry<float> cfg_XPPerSmelt;
        internal static ConfigEntry<float> cfg_XPPerRepair;

        // Infusion tracking
        internal static Dictionary<ZDOID, (int tier, float timestamp)> smelterInfusions = new();
        internal static Dictionary<ZDOID, (int tier, float timestamp)> kilnInfusions = new();

        // Icon
        private static Sprite s_skillIcon;

        private ConfigEntry<T> AddConfig<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
        {
            var configEntry = Config.Bind(group, name, value, description);
            var syncedConfigEntry = configSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
            return configEntry;
        }

        private ConfigEntry<T> AddConfig<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
            => AddConfig(group, name, value, new ConfigDescription(description), synchronizedSetting);

        private void Awake()
        {
            //Localizer.Load();
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
                blacksmithSkill.Description.English("Craft better, last longer. Improves durability, damage, and armor of crafted items. Grants smelting and repair bonuses.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to construct SkillManager skill: {ex}");
            }

            // General
            cfg_SkillGainFactor = AddConfig("General", "Skill gain factor", 1f, "Rate at which you gain Blacksmithing XP.");
            cfg_SkillEffectFactor = AddConfig("General", "Skill effect factor", 1f, "Multiplier applied to all skill effects.");
            cfg_XPPerCraft = AddConfig("General", "XP per craft", 0.5f, "Base XP granted when crafting an item.");
            cfg_XPPerSmelt = AddConfig("General", "XP per smelt", 0.75f, "Base XP granted when adding ore to smelter.");
            cfg_XPPerRepair = AddConfig("General", "XP per repair", 0.05f, "Base XP granted when repairing an item.");
            cfg_InfusionTierInterval = AddConfig("General", "Workstation infusion milestone interval", 10, "Levels per infusion tier (smelter, kiln, repair).");
            cfg_KilnSpeedBonusPerTier = AddConfig("General", "Kiln infusion speed bonus per milestone", 0.05f, "Speed bonus per infusion tier.");
            cfg_SmeltingSpeedBonusPerTier = AddConfig("General", "Smelting infusion speed bonus per milestone", 0.05f, "Speed bonus per infusion tier.");
            cfg_InfusionExpireTime = AddConfig("General", "Infusion expire time (seconds)", 60f, "How long an infusion lasts.");
            cfg_ChanceExtraItemAt100 = AddConfig("General", "Chance to craft extra item at 100", 0.05f, "Chance to produce an extra copy when crafting at level 100.");
            cfg_SmelterSaveOreChanceAt100 = AddConfig("General", "Chance smelter saves ore at 100", 0.2f, "Chance ore is saved at level 100.");
            cfg_EnableInventoryRepair = AddConfig("General", "Inventory repair enabled", true, "Enable repairing from inventory.");
            cfg_InventoryRepairUnlockLevel = AddConfig("General", "Inventory repair unlock level", 70, "Level required for inventory repairs.");

            // Tooltip
            cfg_ShowBlacksmithLevelInTooltip = AddConfig("Tooltip", "Show blacksmith level in tooltip", true, "If true, shows the blacksmithing level used to forge the item in its tooltip.");
            cfg_ShowInfusionInTooltip = AddConfig("Tooltip", "Show elemental infusion in tooltip", false, "If true, shows the elemental infusion type applied to the item in its tooltip.");
            cfg_ShowDurabilityBonusInTooltip = AddConfig("Tooltip", "Show durability bonus in tooltip", false, "If true, shows bonus durability next to blacksmith level in item tooltip.");

            // Durability
            cfg_DurabilityTierInterval = AddConfig("Durability", "Durability milestone interval", 10, "Levels required per durability milestone.");
            cfg_DurabilityBonusPerTier = AddConfig("Durability", "Durability bonus per milestone", 50f, "Flat durability bonus per milestone.");
            cfg_DurabilityBonusPerUpgrade = AddConfig("Durability", "Durability bonus per milestone per upgrade", 50f, "Durability bonus applied per item upgrade.");
            cfg_RespectOriginalDurability = AddConfig("Durability", "Respect original durability", true, "Only boost if base durability > 0.");
            cfg_MaxDurabilityCap = AddConfig("Durability", "Max durability cap", 2000f, "Maximum durability after all bonuses.");

            // Armor
            cfg_GearMilestoneInterval = AddConfig("Armor", "Armor bonus milestone interval", 20, "Levels required per armor milestone.");
            cfg_ArmorBonusPerTier = AddConfig("Armor", "Armor bonus per milestone", 5f, "Flat armor per milestone.");
            cfg_ArmorBonusPerUpgrade = AddConfig("Armor", "Armor bonus per milestone per upgrade", 2f, "Extra armor per upgrade per milestone.");
            cfg_ArmorCap = AddConfig("Armor", "Armor cap", 300f, "Maximum armor value allowed after all bonuses. Set to 0 to disable.");

            // Weapons
            cfg_RespectOriginalStats = AddConfig("Weapons", "Respect original weapon stats", true, "Only scale stats that exist on base item.");
            cfg_StatTierInterval = AddConfig("Weapons", "Weapon bonus milestone interval", 20, "Levels required per weapon milestone.");
            cfg_DamageBonusPerTier = AddConfig("Weapons", "Weapon bonus increase per milestone", 10, "Flat base damage bonus per milestone.");
            cfg_StatBonusPerUpgrade = AddConfig("Weapons", "Weapon bonus increase per milestone per upgrade", 8f, "Flat damage bonus per upgrade per milestone.");
            cfg_StatBonusCapPerType = AddConfig("Weapons", "Stat bonus cap per damage type", 100f, "Maximum allowed bonus per damage type.");
            cfg_MaxStatTypesPerTier = AddConfig("Weapons", "Max stat types per tier", 3, "Number of damage types boosted per milestone.");

            // Elemental
            cfg_AlwaysAddElementalAtMax = AddConfig("Elemental", "Add elemental bonus at milestone", true, "Adds elemental bonus when milestone is reached.");
            cfg_ElementalUnlockLevel = AddConfig("Elemental", "Elemental unlock milestone", 100, "Milestone at which elemental bonuses are enabled.");
            cfg_FireBonusAtMax = AddConfig("Elemental", "Fire bonus at level 100", 20f, "Fire damage added at milestone 100.");
            cfg_FrostBonusAtMax = AddConfig("Elemental", "Frost bonus at level 100", 20f, "Frost damage added at milestone 100.");
            cfg_LightningBonusAtMax = AddConfig("Elemental", "Lightning bonus at level 100", 20f, "Lightning damage added at milestone 100.");
            cfg_PoisonBonusAtMax = AddConfig("Elemental", "Poison bonus at level 100", 20f, "Poison damage added at milestone 100.");
            cfg_SpiritBonusAtMax = AddConfig("Elemental", "Spirit bonus at level 100", 20f, "Spirit damage added at milestone 100.");
            cfg_ElementalBonusPerTier = AddConfig("Elemental", "Elemental bonus per weapon tier", 5f, "Elemental bonus applied per weapon milestone.");

            // Shields
            cfg_TimedBlockBonusPerTier = AddConfig("Shields", "Timed block bonus per milestone", 0.05f, "Parry bonus per shield milestone.");
            cfg_TimedBlockBonusPerUpgrade = AddConfig("Shields", "Timed block bonus per milestone per upgrade", 0.05f, "Parry bonus per upgrade per shield milestone.");
            cfg_BlockPowerBonusPerTier = AddConfig("Shields", "Block power bonus per milestone", 2f, "Block power bonus per shield milestone.");
            cfg_BlockPowerBonusPerUpgrade = AddConfig("Shields", "Block power bonus per milestone per upgrade", 1f, "Block power bonus per upgrade per shield milestone.");

            // Advanced
            cfg_StatBonusMultiplierPerTier = AddConfig("Advanced", "Stat bonus multiplier per tier", 1f, "Multiplier for advanced scaling.");
            cfg_ApplyUpgradeBonusAtTierZero = AddConfig("Advanced", "Apply upgrade bonus at tier 0", false, "If false, upgrade bonuses only start at first milestone.");
            cfg_GearBonusPerMilestone = AddConfig("Advanced", "Gear bonus per milestone", 5f, "Shared bonus applied to gear per milestone.");
            cfg_GearUpgradeBonusPerMilestone = AddConfig("Advanced", "Gear upgrade bonus per milestone", 2f, "Shared upgrade bonus applied to gear per milestone.");
            cfg_MaxTierUnlockLevel = AddConfig("Advanced", "Max tier unlock level", 100, "Maximum level used for stat scaling.");

            // Sync skill configs
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
            private class CoroutineHost : MonoBehaviour
            {
            }

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

        //  internal static int GetExtraUpgradeTiers(int level) => (level / 25) * cfg_UpgradeTierPer25Levels.Value;
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

        internal static void ApplyCraftedItemMultipliers(ItemDrop.ItemData item, int level)
        {
            if (item == null || item.m_shared == null || level <= 0) return;

            CacheBaseStats(item);
            string key = item.m_shared.m_name;
            bool isWeapon = item.IsWeapon();

            float baseArmor = baseArmorLookup[key];
            HitData.DamageTypes baseDamage = baseDamageLookup[key].Clone();
            float baseDurability = baseDurabilityLookup[key];

            // Clamp level so we don’t scale infinitely
            int cappedLevel = Mathf.Min(level, cfg_MaxTierUnlockLevel.Value);

            // Tiers / milestones
            int statTier = cappedLevel / cfg_StatTierInterval.Value;
            int durabilityTier = cappedLevel / cfg_DurabilityTierInterval.Value;
            int milestoneCount = cappedLevel / cfg_GearMilestoneInterval.Value;

            // -------------------
            // DAMAGE
            // -------------------
            float damageBonus = (statTier * cfg_DamageBonusPerTier.Value * cfg_StatBonusMultiplierPerTier.Value)
                                + (milestoneCount * cfg_GearBonusPerMilestone.Value);

            float upgradeDamageBonus = 0f;
            if (cfg_ApplyUpgradeBonusAtTierZero.Value || statTier > 0)
            {
                upgradeDamageBonus = (item.m_quality * cfg_StatBonusPerUpgrade.Value
                                      + item.m_quality * milestoneCount * cfg_GearUpgradeBonusPerMilestone.Value)
                                     * cfg_StatBonusMultiplierPerTier.Value;
            }

            // -------------------
            // ARMOR
            // -------------------
            float armorBonus = (statTier * cfg_ArmorBonusPerTier.Value)
                               + (milestoneCount * cfg_GearBonusPerMilestone.Value);

            float upgradeArmorBonus = 0f;
            if (cfg_ApplyUpgradeBonusAtTierZero.Value || statTier > 0)
            {
                upgradeArmorBonus = (item.m_quality * cfg_ArmorBonusPerUpgrade.Value
                                     + item.m_quality * milestoneCount * cfg_GearUpgradeBonusPerMilestone.Value);
            }

            // Reset to base
            item.m_shared.m_damages = baseDamage.Clone();

            if (cfg_RespectOriginalStats.Value)
            {
                // Only scale existing stats
                void ApplyStat(ref float stat, float baseValue)
                {
                    if (baseValue > 0f)
                    {
                        stat += damageBonus + upgradeDamageBonus;
                        stat = Mathf.Min(stat, cfg_StatBonusCapPerType.Value);
                    }
                }

                ApplyStat(ref item.m_shared.m_damages.m_blunt, baseDamage.m_blunt);
                ApplyStat(ref item.m_shared.m_damages.m_slash, baseDamage.m_slash);
                ApplyStat(ref item.m_shared.m_damages.m_pierce, baseDamage.m_pierce);
                ApplyStat(ref item.m_shared.m_damages.m_fire, baseDamage.m_fire);
                ApplyStat(ref item.m_shared.m_damages.m_frost, baseDamage.m_frost);
                ApplyStat(ref item.m_shared.m_damages.m_lightning, baseDamage.m_lightning);
                ApplyStat(ref item.m_shared.m_damages.m_poison, baseDamage.m_poison);
                ApplyStat(ref item.m_shared.m_damages.m_spirit, baseDamage.m_spirit);

                if (baseArmor > 0f)
                {
                    item.m_shared.m_armor = Mathf.RoundToInt(baseArmor + armorBonus + upgradeArmorBonus);
                    if (cfg_ArmorCap.Value > 0f)
                        item.m_shared.m_armor = Mathf.Min(item.m_shared.m_armor, cfg_ArmorCap.Value);
                }

                // -------------------
                // SHIELDS
                // -------------------
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    if (item.m_shared.m_blockPower > 0f)
                    {
                        item.m_shared.m_blockPower += (statTier * cfg_BlockPowerBonusPerTier.Value)
                                                      + (item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value);
                    }

                    if (item.m_shared.m_deflectionForce > 0f)
                    {
                        item.m_shared.m_deflectionForce += armorBonus + upgradeArmorBonus;
                    }

                    if (item.m_shared.m_timedBlockBonus > 0f)
                    {
                        item.m_shared.m_timedBlockBonus += (statTier * cfg_TimedBlockBonusPerTier.Value)
                                                           + (item.m_quality * cfg_TimedBlockBonusPerUpgrade.Value);
                    }
                }

                // -------------------
                // ELEMENTAL BONUSES
                // -------------------
                
                if (cappedLevel >= cfg_ElementalUnlockLevel.Value && cfg_AlwaysAddElementalAtMax.Value)
                {
                    float infusionBonus = (statTier * cfg_ElementalBonusPerTier.Value)
                                          + damageBonus + upgradeDamageBonus;

                    if (cappedLevel >= 100)
                    {
                        item.m_shared.m_damages.m_fire += cfg_FireBonusAtMax.Value;
                        item.m_shared.m_damages.m_frost += cfg_FrostBonusAtMax.Value;
                        item.m_shared.m_damages.m_lightning += cfg_LightningBonusAtMax.Value;
                        item.m_shared.m_damages.m_poison += cfg_PoisonBonusAtMax.Value;
                        item.m_shared.m_damages.m_spirit += cfg_SpiritBonusAtMax.Value;
                    }

                    if (isWeapon)
                    {
                        if (!item.m_customData.ContainsKey("ElementalInfusion"))
                        {
                            var options = new List<(string name, Action)>
                            {
                                ("Fire", () => item.m_shared.m_damages.m_fire += infusionBonus),
                                ("Frost", () => item.m_shared.m_damages.m_frost += infusionBonus),
                                ("Lightning", () => item.m_shared.m_damages.m_lightning += infusionBonus),
                                ("Poison", () => item.m_shared.m_damages.m_poison += infusionBonus),
                                ("Spirit", () => item.m_shared.m_damages.m_spirit += infusionBonus),
                            };

                            var rng = new System.Random();
                            var selected = options[rng.Next(options.Count)];
                            selected.Item2();
                            item.m_customData["ElementalInfusion"] = selected.name;

                            Debug.Log($"[BlacksmithingExpanded] Infused {item.m_shared.m_name} with {selected.name} (bonus={infusionBonus})");
                        }
                        else
                        {
                            string type = item.m_customData["ElementalInfusion"];
                            switch (type)
                            {
                                case "Fire": item.m_shared.m_damages.m_fire += infusionBonus; break;
                                case "Frost": item.m_shared.m_damages.m_frost += infusionBonus; break;
                                case "Lightning": item.m_shared.m_damages.m_lightning += infusionBonus; break;
                                case "Poison": item.m_shared.m_damages.m_poison += infusionBonus; break;
                                case "Spirit": item.m_shared.m_damages.m_spirit += infusionBonus; break;
                            }
                        }
                    }
                }
            }
            else
            {
                // Randomize damage types if not respecting base stats
                var setters = new List<(string name, Action)>
                {
                    ("Blunt", () => item.m_shared.m_damages.m_blunt += damageBonus + upgradeDamageBonus),
                    ("Slash", () => item.m_shared.m_damages.m_slash += damageBonus + upgradeDamageBonus),
                    ("Pierce", () => item.m_shared.m_damages.m_pierce += damageBonus + upgradeDamageBonus),
                    ("Fire", () => item.m_shared.m_damages.m_fire += damageBonus + upgradeDamageBonus),
                    ("Frost", () => item.m_shared.m_damages.m_frost += damageBonus + upgradeDamageBonus),
                    ("Lightning", () => item.m_shared.m_damages.m_lightning += damageBonus + upgradeDamageBonus),
                    ("Poison", () => item.m_shared.m_damages.m_poison += damageBonus + upgradeDamageBonus),
                    ("Spirit", () => item.m_shared.m_damages.m_spirit += damageBonus + upgradeDamageBonus),
                };

                var rng = new System.Random();
                var selected = setters.OrderBy(_ => rng.Next()).Take(Math.Min(cfg_MaxStatTypesPerTier.Value, setters.Count)).ToList();
                foreach (var s in selected) s.Item2();

                item.m_shared.m_armor = Mathf.RoundToInt(baseArmor + armorBonus + upgradeArmorBonus);
                if (cfg_ArmorCap.Value > 0f)
                    item.m_shared.m_armor = Mathf.Min(item.m_shared.m_armor, cfg_ArmorCap.Value);

                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    item.m_shared.m_blockPower += (statTier * cfg_BlockPowerBonusPerTier.Value)
                                                  + (item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value);

                    item.m_shared.m_deflectionForce += armorBonus + upgradeArmorBonus;
                    item.m_shared.m_timedBlockBonus += (statTier * cfg_TimedBlockBonusPerTier.Value)
                                                       + (item.m_quality * cfg_TimedBlockBonusPerUpgrade.Value);
                }

                Debug.Log($"[BlacksmithingExpanded] Randomized boost: {string.Join(", ", selected.Select(s => s.name))}");
            }

            // -------------------
            // DURABILITY
            // -------------------
            if (!cfg_RespectOriginalDurability.Value || baseDurability > 0f)
            {
                float durabilityBonus = (durabilityTier * cfg_DurabilityBonusPerTier.Value)
                                        + (item.m_quality * cfg_DurabilityBonusPerUpgrade.Value);

                float finalDurability = baseDurability + durabilityBonus;
                if (cfg_MaxDurabilityCap.Value > 0f)
                    finalDurability = Mathf.Min(finalDurability, cfg_MaxDurabilityCap.Value);

                item.m_shared.m_maxDurability = finalDurability;
                item.m_durability = item.GetMaxDurability();

                Debug.Log($"[BlacksmithingExpanded] Durability: base={baseDurability}, tierBonus={durabilityTier * cfg_DurabilityBonusPerTier.Value}, upgradeBonus={item.m_quality * cfg_DurabilityBonusPerUpgrade.Value}, final={finalDurability}");
            }
            else
            {
                item.m_shared.m_maxDurability = baseDurability;
                item.m_durability = item.GetMaxDurability();
            }

            // -------------------
            // FINAL DEBUG
            // -------------------
            Debug.Log($"[BlacksmithingExpanded] {item.m_shared.m_name}: level={level} (capped {cappedLevel}), statTier={statTier}, durabilityTier={durabilityTier}, milestones={milestoneCount}, damageBonus={damageBonus}, upgradeDamageBonus={upgradeDamageBonus}, armorBonus={armorBonus}, upgradeArmorBonus={upgradeArmorBonus}");
        }

        /// <summary>
        /// Attach BlacksmithingData to the item, for persistent tooltip/stat display.
        /// </summary>
        internal static void AttachBlacksmithingData(ItemDrop.ItemData item, int level)
        {
            if (item == null) return;

            // Save into ItemDataManager runtime object
            var data = item.Data().Add<BlacksmithingData>();
            if (data != null)
            {
                data.blacksmithLevel = level;
                data.Save();
            }

            // Mirror into Valheim’s persistent customData
            item.m_customData["BlacksmithingLevel"] = level.ToString();
        }

        internal static int GetBlacksmithingLevel(ItemDrop.ItemData item)
        {
            if (item == null) return 0;

            // ItemDataManager runtime lookup
            var data = item.Data().Get<BlacksmithingData>();
            if (data != null && data.blacksmithLevel > 0)
                return data.blacksmithLevel;

            // Fallback: persistent dictionary
            if (item.m_customData.TryGetValue("BlacksmithingLevel", out string stored))
            {
                if (int.TryParse(stored, out int parsed))
                    return parsed;
            }

            return 0;
        }

        // -----------------------
        // Harmony patches (crafting, smelter, tooltip, repair)
        // -----------------------
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        public static class Patch_Blacksmithing_Crafting
        {
            static void Postfix(InventoryGui __instance)
            {
                var player = Player.m_localPlayer;
                if (player == null) return;

                var inventory = player.GetInventory();
                if (inventory == null) return;

                // Safely get the last item added (crafted or upgraded)
                var craftedItem = inventory.GetAllItems().LastOrDefault();
                if (craftedItem == null || craftedItem.m_shared == null) return;

                int level = GetPlayerBlacksmithingLevel(player);
                if (level <= 0) return;
                if (craftedItem.IsWeapon() || craftedItem.IsEquipable())
                {
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

                    Debug.Log($"[BlacksmithingExpanded] Applied stats to crafted/upgraded item: {craftedItem.m_shared.m_name}");
                }
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
        public static class Patch_Blacksmithing_Tooltip
        {
            public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
            {
                if (item == null) return;

                // Try runtime data first
                var data = item.Data().Get<BlacksmithingData>();
                int level = (data != null && data.blacksmithLevel > 0)
                    ? data.blacksmithLevel
                    : 0;

                // Fallback: persistent storage
                if (level == 0 && item.m_customData.TryGetValue("BlacksmithingLevel", out string stored))
                {
                    if (int.TryParse(stored, out int parsed))
                        level = parsed;
                }

                if (level > 0)
                {
                    // Show blacksmithing level if enabled
                    if (BlacksmithingExpanded.cfg_ShowBlacksmithLevelInTooltip.Value)
                        __result += $"\n<color=orange>Forged at Blacksmithing {level}</color>";

                    // Show infusion if enabled and applied
                    if (BlacksmithingExpanded.cfg_ShowInfusionInTooltip.Value &&
                        item.m_customData.TryGetValue("ElementalInfusion", out string infusionType))
                    {
                        __result += $"\n<color=#87CEEB>Elemental Infusion: {infusionType}</color>";
                    }

                    // Optional: show durability bonus if config is enabled
                    if (BlacksmithingExpanded.cfg_ShowDurabilityBonusInTooltip.Value)
                    {
                        float baseDurability = item.m_shared.m_maxDurability;
                        if (baseDurability > 0 && level >= BlacksmithingExpanded.cfg_DurabilityTierInterval.Value)
                        {
                            int durabilityTier = level / BlacksmithingExpanded.cfg_DurabilityTierInterval.Value;
                            float bonus = (durabilityTier * BlacksmithingExpanded.cfg_DurabilityBonusPerTier.Value) +
                                          (item.m_quality * BlacksmithingExpanded.cfg_DurabilityBonusPerUpgrade.Value);

                            if (bonus > 0)
                                __result += $"\n<color=#90EE90>Durability Bonus: +{bonus:F0}</color>";
                        }
                    }
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
            return level / cfg_InfusionTierInterval.Value;
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
                        int tiers = level / cfg_InfusionTierInterval.Value;
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
    }
}
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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlacksmithingExpanded
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BlacksmithingExpanded : BaseUnityPlugin
    {
        internal const string ModName = "Blacksmithing Expanded";
        internal const string ModVersion = "1.0.6";
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

        // Base stat cache - single source of truth
        private static readonly Dictionary<string, ItemBaseStats> baseStatsCache = new();

        // Item filtering system
        private static ItemFilterConfig itemFilterConfig;
        private static readonly HashSet<string> whitelistedItems = new();
        private static readonly HashSet<string> blacklistedItems = new();
        private static string configPath;

        // YAML sync system (like Jewelcrafting)
        public static readonly CustomSyncedValue<List<string>> syncedWhitelistItems = new(configSync, "whitelist items", new List<string>());
        public static readonly CustomSyncedValue<List<string>> syncedBlacklistItems = new(configSync, "blacklist items", new List<string>());

        // Workstation infusions
        internal static Dictionary<ZDOID, WorkstationInfusion> smelterInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> kilnInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> blastFurnaceInfusions = new();
        private static readonly Dictionary<ZDOID, float> originalBlastFurnaceSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalSmelterSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalKilnSpeeds = new();
        private static readonly Dictionary<ZDOID, GameObject> activeGlowEffects = new();
        private static readonly Dictionary<string, BlacksmithingItemData> tempSpearDataStorage = new();

        // Config entries
        internal static ConfigEntry<float> cfg_SkillGainFactor;
        internal static ConfigEntry<float> cfg_SkillEffectFactor;
        internal static ConfigEntry<int> cfg_InfusionTierInterval;
        internal static ConfigEntry<float> cfg_ChanceExtraItemAt100;
        internal static ConfigEntry<float> cfg_SmelterSaveOreChanceAt100;
        internal static ConfigEntry<bool> cfg_EnableInventoryRepair;
        internal static ConfigEntry<int> cfg_InventoryRepairUnlockLevel;
        internal static ConfigEntry<float> cfg_SmeltingSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_KilnSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_InfusionExpireTime;
        internal static ConfigEntry<bool> cfg_ShowInfusionVisualEffect;
        internal static ConfigEntry<bool> cfg_ShowBlacksmithLevelInTooltip;
        internal static ConfigEntry<bool> cfg_ShowInfusionInTooltip;
        internal static ConfigEntry<float> cfg_FirstCraftBonusXP;

        // YAML Configuration
        internal static ConfigEntry<bool> cfg_UseYamlFiltering;
        internal static ConfigEntry<bool> cfg_LogFilteredItems;

        // Durability
        internal static ConfigEntry<int> cfg_DurabilityTierInterval;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerTier;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_RespectOriginalDurability;
        internal static ConfigEntry<float> cfg_MaxDurabilityCap;
        // NEW: Non-repairable items config
        internal static ConfigEntry<bool> cfg_AllowNonRepairableItems;

        // Armor & Weapons
        internal static ConfigEntry<int> cfg_StatTierInterval;
        internal static ConfigEntry<float> cfg_ArmorBonusPerTier;
        internal static ConfigEntry<float> cfg_ArmorBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_ArmorCap;
        internal static ConfigEntry<int> cfg_DamageBonusPerTier;
        internal static ConfigEntry<float> cfg_StatBonusPerUpgrade;
        // NEW: Percentage-based damage system
        internal static ConfigEntry<bool> cfg_UsePercentageDamageBonus;
        internal static ConfigEntry<float> cfg_DamagePercentageBonusPerTier;
        // NEW: Percentage-based upgrade bonuses - DEFAULTED TO FALSE FOR SAFETY
        internal static ConfigEntry<bool> cfg_UsePercentageUpgradeBonus;
        internal static ConfigEntry<float> cfg_StatPercentageBonusPerUpgrade;

        // Elemental
        internal static ConfigEntry<bool> cfg_AlwaysAddElementalAtMax;
        internal static ConfigEntry<int> cfg_ElementalUnlockLevel;
        internal static ConfigEntry<float> cfg_FireBonusPerTier;
        internal static ConfigEntry<float> cfg_FrostBonusPerTier;
        internal static ConfigEntry<float> cfg_LightningBonusPerTier;
        internal static ConfigEntry<float> cfg_PoisonBonusPerTier;
        internal static ConfigEntry<float> cfg_SpiritBonusPerTier;
        internal static ConfigEntry<bool> cfg_BoostElementalWeapons;
        internal static ConfigEntry<float> cfg_ElementalWeaponBoostChance;
        internal static ConfigEntry<bool> cfg_UsePercentageElementalBonus;
        internal static ConfigEntry<float> cfg_ElementalPercentageBonusPerTier;

        // Shields
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerTier;
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerTier;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerUpgrade;

        // XP
        internal static ConfigEntry<float> cfg_XPPerCraft;
        internal static ConfigEntry<float> cfg_XPPerSmelt;
        internal static ConfigEntry<float> cfg_XPPerRepair;
        internal static ConfigEntry<float> cfg_XPPerUpgrade;

        private static Sprite s_skillIcon;

        // Data structures
        private struct ItemBaseStats
        {
            public float armor;
            public HitData.DamageTypes damages;
            public float durability;
            public List<HitData.DamageModPair> resistances;
        }

        public class WorkstationInfusion
        {
            public int tier;
            public float timestamp;
            public float originalSpeed;
            public float bonusSpeed;
            public bool wasActive;

            public bool IsExpired => Time.time - timestamp > cfg_InfusionExpireTime.Value;
            public float RemainingTime => Mathf.Max(0f, cfg_InfusionExpireTime.Value - (Time.time - timestamp));
        }

        private class ItemFilterConfig
        {
            public List<string> Whitelist { get; set; } = new List<string>();
            public List<string> Blacklist { get; set; } = new List<string>();
        }

        private ConfigEntry<T> AddConfig<T>(string group, string name, T value, string description, bool sync = true)
        {
            var entry = Config.Bind(group, name, value, new ConfigDescription(description));
            var syncEntry = configSync.AddConfigEntry(entry);
            syncEntry.SynchronizedConfig = sync;
            return entry;
        }

        private void Awake()
        {
            harmony = new Harmony(ModGUID);
            configPath = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath), "BlacksmithExpItemList.yml");

            // Load skill icon
            try
            {
                s_skillIcon = LoadEmbeddedSprite("smithing.png", 64, 64);
                if (s_skillIcon == null)
                    throw new Exception("Failed to load embedded sprite: smithing.png");

                blacksmithSkill = new Skill("Blacksmithing", s_skillIcon)
                {
                    Configurable = true
                };
                blacksmithSkill.Name.English("Blacksmithing");
                blacksmithSkill.Description.English("Craft better, last longer. Improves durability, damage, and armor of crafted items.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Skill setup failed: {ex}");
            }

            // Setup configs
            SetupConfigs();

            // Initialize YAML filtering system
            InitializeYamlFiltering();

            // Setup sync listeners for YAML data
            syncedWhitelistItems.ValueChanged += () =>
            {
                whitelistedItems.Clear();
                foreach (var item in syncedWhitelistItems.Value)
                {
                    whitelistedItems.Add(item);
                }
                Logger.LogDebug($"[BlacksmithingExpanded] Received synced whitelist: {whitelistedItems.Count} items");
            };

            syncedBlacklistItems.ValueChanged += () =>
            {
                blacklistedItems.Clear();
                foreach (var item in syncedBlacklistItems.Value)
                {
                    blacklistedItems.Add(item);
                }
                Logger.LogDebug($"[BlacksmithingExpanded] Received synced blacklist: {blacklistedItems.Count} items");
            };

            // Sync skill configs
            if (blacksmithSkill != null)
            {
                blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
                cfg_SkillGainFactor.SettingChanged += (_, _) => blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                cfg_SkillEffectFactor.SettingChanged += (_, _) => blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
            }

            // Setup YAML config change handler (server only)
            cfg_UseYamlFiltering.SettingChanged += (_, _) =>
            {
                if (ZNet.instance == null || ZNet.instance.IsServer())
                {
                    ReloadYamlConfiguration();
                }
            };

            // Register ItemDataManager type for force loading
            ItemInfo.ForceLoadTypes.Add(typeof(BlacksmithingItemData));

            harmony.PatchAll();
            //Logger.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void SetupConfigs()
        {
            // General
            cfg_SkillGainFactor = AddConfig("General", "Skill gain factor", 1f, "Multiplier for blacksmithing XP gain rate (1.5 = 50% faster leveling)");
            cfg_SkillEffectFactor = AddConfig("General", "Skill effect factor", 1f, "Global multiplier for all blacksmithing bonuses (damage, armor, durability, etc). Higher = stronger effects");
            cfg_InfusionTierInterval = AddConfig("General", "Workstation infusion milestone interval", 10, "Every X blacksmithing levels unlocks a new tier of smelter/kiln speed bonus");
            cfg_SmeltingSpeedBonusPerTier = AddConfig("General", "Smelting speed bonus per tier", 0.15f, "Speed bonus per tier - 0.15 = 15% faster smelting. Stacks with each tier");
            cfg_KilnSpeedBonusPerTier = AddConfig("General", "Kiln speed bonus per tier", 0.15f, "Speed bonus per tier - 0.15 = 15% faster charcoal production. Stacks with each tier");
            cfg_InfusionExpireTime = AddConfig("General", "Infusion expire time", 300f, "Seconds that speed bonuses last after adding fuel/ore to smelters/kilns (300 = 5 minutes)");
            cfg_ShowInfusionVisualEffect = AddConfig("General", "Show infusion visual effect", true, "Show orange glowing light effect when smelters/kilns have speed bonuses active");
            cfg_SmelterSaveOreChanceAt100 = AddConfig("General", "Ore save chance at 100", 0.2f, "At level 100: chance to not consume ore when smelting (0.2 = 20% ore savings)");
            cfg_ChanceExtraItemAt100 = AddConfig("General", "Extra item chance at 100", 0.05f, "At level 100: chance to get bonus item when crafting (0.05 = 5% chance for double output)");
            cfg_EnableInventoryRepair = AddConfig("General", "Enable inventory repair", true, "Allow repairing items directly from inventory (bypasses workbench requirement)");
            cfg_InventoryRepairUnlockLevel = AddConfig("General", "Inventory repair unlock level", 70, "Blacksmithing level required to repair items from inventory without workbench");
            cfg_UsePercentageUpgradeBonus = AddConfig("PercentageSystem", "Use percentage upgrade bonus", true, "If enabled, upgrade bonuses are percentage-based instead of flat. When disabled, uses flat bonuses");
            cfg_StatPercentageBonusPerUpgrade = AddConfig("PercentageSystem", "Stat percentage bonus per upgrade", 2f, "Percentage bonus per upgrade level when using percentage upgrade system (2 = 2% per upgrade level)");

            // Item Filtering
            cfg_UseYamlFiltering = AddConfig("Item Filtering", "Use YAML item filtering", true, "Enable custom whitelist/blacklist system via BlacksmithExpItemList.yml file");
            cfg_LogFilteredItems = AddConfig("Item Filtering", "Log filtered items", false, "Write to console when items are blocked by whitelist/blacklist filters");

            // XP
            cfg_XPPerCraft = AddConfig("XP", "XP per craft", 1f, "Base blacksmithing XP gained when crafting any item");
            cfg_XPPerSmelt = AddConfig("XP", "XP per smelt", 0.75f, "Base blacksmithing XP gained when adding ore to smelters/kilns");
            cfg_XPPerRepair = AddConfig("XP", "XP per repair", 0.1f, "Base blacksmithing XP gained when repairing items");
            cfg_XPPerUpgrade = AddConfig("XP", "XP per upgrade", 3f, "Base blacksmithing XP gained when upgrading items at workbenches");
            cfg_FirstCraftBonusXP = AddConfig("XP", "First craft bonus XP", 10f, "One-time bonus XP when crafting each item type for the first time");

            // Tooltips
            cfg_ShowBlacksmithLevelInTooltip = AddConfig("Tooltip", "Show level in tooltip", true, "Display blacksmithing level used to craft item in item tooltips");
            cfg_ShowInfusionInTooltip = AddConfig("Tooltip", "Show infusion in tooltip", false, "Display elemental infusion type in item tooltips (Fire, Frost, etc.)");

            // Durability
            cfg_DurabilityTierInterval = AddConfig("Durability", "Durability tier interval", 10, "Every X blacksmithing levels unlocks next tier of durability bonuses");
            cfg_DurabilityBonusPerTier = AddConfig("Durability", "Durability bonus per tier", 50f, "Flat durability points added per tier when crafting items");
            cfg_DurabilityBonusPerUpgrade = AddConfig("Durability", "Durability bonus per upgrade", 50f, "Extra durability points per item quality level (star rating)");
            cfg_RespectOriginalDurability = AddConfig("Durability", "Respect original durability", true, "Only boost durability on items that already have durability (prevents boosting consumables/arrows)");
            cfg_MaxDurabilityCap = AddConfig("Durability", "Max durability cap", 2000f, "Maximum durability any item can reach (0 = no limit)");
            cfg_AllowNonRepairableItems = AddConfig("Durability", "Allow non-repairable items", false, "Allow blacksmithing bonuses on items with no durability (torches, consumables, etc.)");

            // Combat Stats
            cfg_BoostElementalWeapons = AddConfig("Stats", "Boost elemental weapons", true, "Allow boosting weapons that already have elemental damage (like Frostner)");
            cfg_ElementalWeaponBoostChance = AddConfig("Stats", "Elemental weapon boost chance", 0.5f, "For weapons with both physical and elemental damage: chance to boost elemental instead of physical (0.5 = 50% chance)");

            cfg_StatTierInterval = AddConfig("Stats", "Stat tier interval", 20, "Every X blacksmithing levels unlocks next tier of damage/armor bonuses");
            cfg_ArmorBonusPerTier = AddConfig("Stats", "Armor bonus per tier", 3f, "Flat armor points added per tier when crafting armor pieces");
            cfg_ArmorBonusPerUpgrade = AddConfig("Stats", "Armor bonus per upgrade", 1f, "Extra armor points per item quality level (star rating)");
            cfg_ArmorCap = AddConfig("Stats", "Armor cap", 300f, "Maximum armor value any piece can reach (0 = no limit)");
            cfg_UsePercentageDamageBonus = AddConfig("PercentageSystem", "Use percentage damage bonus", true, "If enabled, damage bonuses are percentage-based instead of flat. Much more balanced for all weapon types");
            cfg_DamagePercentageBonusPerTier = AddConfig("PercentageSystem", "Damage percentage bonus per tier", 3f, "Percentage damage bonus per tier when using percentage system (3 = 3% per tier)");
            cfg_DamageBonusPerTier = AddConfig("Stats", "Damage bonus per tier", 5, "Flat damage bonus added per stat tier. Applied to one random damage type (slash/pierce/blunt). Only used if percentage system is disabled");
            cfg_StatBonusPerUpgrade = AddConfig("Stats", "Stat bonus per upgrade", 4f, "Extra damage/armor bonus per item quality level (star rating). Only used if percentage upgrade system is disabled");

            // Elemental Damage
            cfg_AlwaysAddElementalAtMax = AddConfig("Elemental", "Add elemental at milestone", true, "Automatically add random elemental damage when reaching elemental unlock level");
            cfg_ElementalUnlockLevel = AddConfig("Elemental", "Elemental unlock level", 100, "Blacksmithing level required to add elemental damage bonuses to weapons");
            cfg_FireBonusPerTier = AddConfig("Elemental", "Fire bonus per tier", 3f, "Fire damage points per tier (causes burning damage over time)");
            cfg_FrostBonusPerTier = AddConfig("Elemental", "Frost bonus per tier", 6f, "Frost damage points per tier (causes instant cold damage)");
            cfg_LightningBonusPerTier = AddConfig("Elemental", "Lightning bonus per tier", 5f, "Lightning damage points per tier (good vs wet enemies)");
            cfg_PoisonBonusPerTier = AddConfig("Elemental", "Poison bonus per tier", 2.5f, "Poison damage points per tier (causes poison damage over time)");
            cfg_SpiritBonusPerTier = AddConfig("Elemental", "Spirit bonus per tier", 4f, "Spirit damage points per tier (extra effective vs undead enemies)");
            cfg_UsePercentageElementalBonus = AddConfig("PercentageSystem", "Use percentage elemental bonus", true, "If enabled, elemental bonuses are percentage-based instead of flat. Much more balanced for all weapon types");
            cfg_ElementalPercentageBonusPerTier = AddConfig("PercentageSystem", "Elemental percentage bonus per tier", 2f, "Percentage elemental bonus per tier when using percentage system (2 = 2% per tier)");

            // Shield Stats
            cfg_TimedBlockBonusPerTier = AddConfig("Shields", "Timed block bonus per tier", 0.05f, "Parry/perfect block bonus per tier (0.05 = 5% better parry window/damage)");
            cfg_TimedBlockBonusPerUpgrade = AddConfig("Shields", "Timed block bonus per upgrade", 0.05f, "Extra parry bonus per shield quality level (star rating)");
            cfg_BlockPowerBonusPerTier = AddConfig("Shields", "Block power bonus per tier", 2f, "Block strength points per tier (reduces stamina cost when blocking)");
            cfg_BlockPowerBonusPerUpgrade = AddConfig("Shields", "Block power bonus per upgrade", 1f, "Extra block power per shield quality level (star rating)");
        }

        // ================================
        // YAML FILTERING SYSTEM
        // ================================

        private void InitializeYamlFiltering()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    Logger.LogInfo($"[BlacksmithingExpanded] YAML not found at {configPath}. Generating default YAML...");
                    GenerateDefaultYaml();
                    Logger.LogInfo("[BlacksmithingExpanded] Default YAML created. Please restart the client.");
                    return;
                }

                ReloadYamlConfiguration();
                Logger.LogDebug($"[BlacksmithingExpanded] YAML filtering system initialized. File: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to initialize YAML filtering: {ex}");
            }
        }
        private void ReloadYamlConfiguration()
        {
            whitelistedItems.Clear();
            blacklistedItems.Clear();

            if (!cfg_UseYamlFiltering.Value)
            {
                Logger.LogInfo("[BlacksmithingExpanded] YAML filtering disabled");
                return;
            }

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(PascalCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                string yamlContent = File.ReadAllText(configPath);
                itemFilterConfig = deserializer.Deserialize<ItemFilterConfig>(yamlContent) ?? new ItemFilterConfig();

                if (itemFilterConfig.Whitelist != null)
                {
                    foreach (var item in itemFilterConfig.Whitelist.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        whitelistedItems.Add(item.Trim());
                    }
                }

                if (itemFilterConfig.Blacklist != null)
                {
                    foreach (var item in itemFilterConfig.Blacklist.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        blacklistedItems.Add(item.Trim());
                    }
                }

                Logger.LogInfo($"[BlacksmithingExpanded] Loaded YAML config - Whitelist: {whitelistedItems.Count} items, Blacklist: {blacklistedItems.Count} items");

                if (whitelistedItems.Count > 0)
                    Logger.LogDebug($"[BlacksmithingExpanded] Whitelisted items: {string.Join(", ", whitelistedItems)}");

                if (blacklistedItems.Count > 0)
                    Logger.LogDebug($"[BlacksmithingExpanded] Blacklisted items: {string.Join(", ", blacklistedItems)}");
            }
            catch (YamlDotNet.Core.YamlException yamlEx)
            {
                Logger.LogError($"[BlacksmithingExpanded] YAML parsing error: {yamlEx.Message}");
                Logger.LogError($"[BlacksmithingExpanded] Check your YAML syntax in: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to load YAML config: {ex}");
            }
        }

        private void GenerateDefaultYaml()
        {
            try
            {
                var defaultYaml = @"# If whitelist is empty:
# Items not on blacklist → Enhanced (normal mod behavior)
# Items on blacklist → Not enhanced

# If whitelist has items:
# Items on whitelist → Enhanced
# Items NOT on whitelist → Not enhanced (regardless of blacklist)

Whitelist:

Blacklist:
  - Club
  - AxeStone
  - Torch
  - Tankard
  - TankardAnniversary
  - TrinketBronzeHealth
  - TrinketBronzeStamina
  - TrinketCarapaceEitr
  - TrinketBlackDamageHealth
  - TrinketFlametalEitr
  - TrinketChitinSwim
  - TrinketFlametalStaminaHealth
  - TrinketIronHealth
  - TrinketIronStamina
  - TrinketScaleStaminaDamage
  - TrinketSilverDamage
  - TrinketSilverResist
  - Demister
  - DvergrKey
  - SaddleAsksvin
  - SaddleLox
";
                File.WriteAllText(configPath, defaultYaml);
                Logger.LogDebug($"[BlacksmithingExpanded] Created default YAML file at {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to create default YAML: {ex}");
            }
        }


        internal static bool IsItemAllowed(ItemDrop.ItemData item)
        {
            if (!cfg_UseYamlFiltering.Value) return true;
            if (item?.m_shared == null) return false;

            // Use prefab name instead of localization token
            string prefabName = GetItemPrefabName(item);
            if (string.IsNullOrEmpty(prefabName))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Could not determine prefab name for item - allowing by default");
                }
                return true;
            }

            // If whitelist has entries, only allow whitelisted items
            if (whitelistedItems.Count > 0)
            {
                bool allowed = whitelistedItems.Contains(prefabName);
                if (cfg_LogFilteredItems.Value)
                {
                    if (allowed)
                    {
                        Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' found in whitelist - allowing bonuses");
                    }
                    else
                    {
                        Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' not in whitelist - filtering out");
                    }
                }
                return allowed;
            }

            // If no whitelist, check blacklist
            if (blacklistedItems.Contains(prefabName))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' is blacklisted - filtering out");
                }
                return false;
            }

            // Item is allowed (not blacklisted and no whitelist restrictions)
            if (cfg_LogFilteredItems.Value)
            {
                Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' allowed (not blacklisted)");
            }
            return true;
        }

        private static string GetItemPrefabName(ItemDrop.ItemData item)
        {
            try
            {
                // Try to get the prefab name from the item's drop prefab
                if (item.m_dropPrefab != null)
                {
                    return item.m_dropPrefab.name;
                }

                // Fallback: try to find the prefab in the ZNetScene
                var prefab = ObjectDB.instance?.GetItemPrefab(item.m_shared.m_name);
                if (prefab != null)
                {
                    return prefab.name;
                }

                // Last resort: use the shared name but try to clean it
                string sharedName = item.m_shared.m_name;
                if (sharedName.StartsWith("$item_"))
                {
                    // Convert $item_club to Club (capitalize first letter)
                    string cleanName = sharedName.Substring(6); // Remove "$item_"
                    return char.ToUpper(cleanName[0]) + cleanName.Substring(1);
                }

                return sharedName;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Error getting prefab name: {ex}");
                return item.m_shared?.m_name ?? "Unknown";
            }
        }

        // ================================
        // ITEMDATAMANAGER IMPLEMENTATION
        // ================================

        private class BlacksmithingItemData : ItemData
        {
            public static readonly Dictionary<ItemDrop.ItemData.SharedData, BlacksmithingItemData> activeItems = new();

            [SerializeField] public int level = 0;
            [SerializeField] public string infusion = "";
            [SerializeField] public float baseDurability = 0f;
            [SerializeField] public float maxDurability = 0f;
            [SerializeField] public float armorBonus = 0f;
            [SerializeField] public float damageBlunt = 0f;
            [SerializeField] public float damageSlash = 0f;
            [SerializeField] public float damagePierce = 0f;
            [SerializeField] public float damageFire = 0f;
            [SerializeField] public float damageFrost = 0f;
            [SerializeField] public float damageLightning = 0f;
            [SerializeField] public float damagePoison = 0f;
            [SerializeField] public float damageSpirit = 0f;
            [SerializeField] public float blockPowerBonus = 0f;
            [SerializeField] public float timedBlockBonus = 0f;

            ~BlacksmithingItemData() => activeItems.Remove(Item.m_shared);

            public override void Load()
            {
                base.Load();
                activeItems[Item.m_shared] = this;

                if (!IsCloned && level > 0)
                {
                    var baseStats = GetBaseStats(Item);

                    // Apply durability
                    if (maxDurability > 0f)
                    {
                        Item.m_shared.m_maxDurability = maxDurability;
                        Item.m_durability = Mathf.Min(Item.m_durability, maxDurability);
                    }

                    // Apply armor
                    if (armorBonus > 0f)
                    {
                        Item.m_shared.m_armor = baseStats.armor + armorBonus;
                    }

                    // Apply damage bonuses
                    Item.m_shared.m_damages.m_blunt = baseStats.damages.m_blunt + damageBlunt;
                    Item.m_shared.m_damages.m_slash = baseStats.damages.m_slash + damageSlash;
                    Item.m_shared.m_damages.m_pierce = baseStats.damages.m_pierce + damagePierce;
                    Item.m_shared.m_damages.m_fire = baseStats.damages.m_fire + damageFire;
                    Item.m_shared.m_damages.m_frost = baseStats.damages.m_frost + damageFrost;
                    Item.m_shared.m_damages.m_lightning = baseStats.damages.m_lightning + damageLightning;
                    Item.m_shared.m_damages.m_poison = baseStats.damages.m_poison + damagePoison;
                    Item.m_shared.m_damages.m_spirit = baseStats.damages.m_spirit + damageSpirit;

                    // Apply shield bonuses
                    if (Item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                    {
                        if (blockPowerBonus > 0f) Item.m_shared.m_blockPower += blockPowerBonus;
                        if (timedBlockBonus > 0f) Item.m_shared.m_timedBlockBonus += timedBlockBonus;
                    }
                }
            }

            public override void Unload()
            {
                if (level > 0)
                {
                    var baseStats = GetBaseStats(Item);

                    // Make a copy of the shared data to avoid affecting other items
                    Item.m_shared = (ItemDrop.ItemData.SharedData)
                        AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone")
                        .Invoke(Item.m_shared, Array.Empty<object>());

                    // Reset to base stats
                    Item.m_shared.m_maxDurability = baseStats.durability;
                    Item.m_shared.m_armor = baseStats.armor;
                    Item.m_shared.m_damages = baseStats.damages.Clone();

                    if (Item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                    {
                        Item.m_shared.m_blockPower -= blockPowerBonus;
                        Item.m_shared.m_timedBlockBonus -= timedBlockBonus;
                    }
                }
                activeItems.Remove(Item.m_shared);
            }

            protected override bool AllowStackingIdenticalValues { get; set; } = true;
        }

        // ================================
        // CORE FUNCTIONALITY
        // ================================

        internal static int GetPlayerBlacksmithingLevel(Player player)
        {
            if (player?.GetComponent<Skills>() == null) return 0;
            try
            {
                var skillType = Skill.fromName("Blacksmithing");
                return Mathf.FloorToInt(player.GetComponent<Skills>().GetSkillLevel(skillType));
            }
            catch
            {
                return 0;
            }
        }

        internal static void GiveBlacksmithingXP(Player player, float amount)
        {
            if (player == null || amount <= 0f) return;
            try
            {
                float adjusted = amount * cfg_SkillGainFactor.Value;
                SkillManager.SkillExtensions.RaiseSkill(player, "Blacksmithing", adjusted);
                //Debug.Log($"[BlacksmithingExpanded] Gave {adjusted:F2} XP to {player.GetPlayerName()}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] XP grant failed: {ex}");
            }
        }

        private static void CacheBaseStats(ItemDrop.ItemData item)
        {
            string key = item.m_shared.m_name;
            if (!baseStatsCache.ContainsKey(key))
            {
                baseStatsCache[key] = new ItemBaseStats
                {
                    armor = item.m_shared.m_armor,
                    damages = item.m_shared.m_damages.Clone(),
                    durability = item.m_shared.m_maxDurability,
                    resistances = new List<HitData.DamageModPair>(item.m_shared.m_damageModifiers)
                };
            }
        }

        private static ItemBaseStats GetBaseStats(ItemDrop.ItemData item)
        {
            CacheBaseStats(item);
            return baseStatsCache[item.m_shared.m_name];
        }

        // ================================
        // ENHANCED CRAFTING SYSTEM
        // ================================

        internal static void ApplyCraftingBonuses(ItemDrop.ItemData item, int level)
        {
            if (item?.m_shared == null || level <= 0) return;
            if (item.m_shared.m_maxStackSize > 1) return; // Skip stackables

            // Check if item is allowed by YAML filtering
            if (!IsItemAllowed(item))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item '{item.m_shared.m_name}' filtered out by YAML configuration");
                }
                return;
            }

            var baseStats = GetBaseStats(item);

            // Check non-repairable items logic
            bool hasOriginalDurability = baseStats.durability > 0f;

            // If item has no durability and we don't allow non-repairable items, skip it
            if (!hasOriginalDurability && !cfg_AllowNonRepairableItems.Value)
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item '{item.m_shared.m_name}' has no durability and non-repairable items are disabled - skipping");
                }
                return;
            }

            // Check if this item already has blacksmithing data to prevent double-application
            var existingData = item.Data().Get<BlacksmithingItemData>();
            string preservedInfusion = "";

            if (existingData != null && existingData.level > 0)
            {
                // Item already has blacksmithing bonuses applied
                // For upgrades, we need to recalculate bonuses based on new quality level
                // but we should clear existing bonuses first to prevent stacking

                Debug.Log($"[BlacksmithingExpanded] Item '{item.m_shared.m_name}' already has blacksmithing data (level {existingData.level}). Recalculating for quality {item.m_quality}...");

                // PRESERVE the original infusion type
                preservedInfusion = existingData.infusion;

                // Clear existing bonuses from the data object before recalculating
                existingData.armorBonus = 0f;
                existingData.damageBlunt = 0f;
                existingData.damageSlash = 0f;
                existingData.damagePierce = 0f;
                existingData.damageFire = 0f;
                existingData.damageFrost = 0f;
                existingData.damageLightning = 0f;
                existingData.damagePoison = 0f;
                existingData.damageSpirit = 0f;
                existingData.blockPowerBonus = 0f;
                existingData.timedBlockBonus = 0f;
                // Don't clear infusion here - we'll restore it below
            }

            int statTier = level / cfg_StatTierInterval.Value;
            int durabilityTier = level / cfg_DurabilityTierInterval.Value;

            // Create or get existing blacksmithing data
            var data = item.Data().GetOrCreate<BlacksmithingItemData>();
            data.level = level;
            data.baseDurability = baseStats.durability;

            // Apply durability bonus
            if (hasOriginalDurability || cfg_AllowNonRepairableItems.Value)
            {
                bool shouldApplyDurability = (cfg_RespectOriginalDurability.Value && hasOriginalDurability) ||
                                           !cfg_RespectOriginalDurability.Value ||
                                           cfg_AllowNonRepairableItems.Value;

                if (shouldApplyDurability)
                {
                    float durabilityBonus = (durabilityTier * cfg_DurabilityBonusPerTier.Value) +
                                            (item.m_quality * cfg_DurabilityBonusPerUpgrade.Value);

                    data.maxDurability = baseStats.durability + durabilityBonus;

                    if (cfg_MaxDurabilityCap.Value > 0f)
                        data.maxDurability = Mathf.Min(data.maxDurability, cfg_MaxDurabilityCap.Value);
                }
            }

            // Apply damage bonuses with simplified calculation
            ApplyDamageBonuses(item, baseStats, statTier, data);

            // Apply armor bonus with percentage upgrade support
            if (baseStats.armor > 0f)
            {
                float armorTierBonus = statTier * cfg_ArmorBonusPerTier.Value;
                float armorUpgradeBonus;

                if (cfg_UsePercentageUpgradeBonus.Value)
                {
                    // Percentage-based upgrade bonus for armor
                    float upgradePercentage = item.m_quality * cfg_StatPercentageBonusPerUpgrade.Value / 100f;
                    armorUpgradeBonus = baseStats.armor * upgradePercentage;
                }
                else
                {
                    // Flat upgrade bonus for armor (SAFE DEFAULT - maintains v1.0.4 behavior)
                    armorUpgradeBonus = item.m_quality * cfg_ArmorBonusPerUpgrade.Value;
                }

                data.armorBonus = armorTierBonus + armorUpgradeBonus;
                if (cfg_ArmorCap.Value > 0f && baseStats.armor + data.armorBonus > cfg_ArmorCap.Value)
                    data.armorBonus = cfg_ArmorCap.Value - baseStats.armor;
            }

            // Apply shield bonuses
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                if (item.m_shared.m_blockPower > 0f)
                {
                    data.blockPowerBonus = statTier * cfg_BlockPowerBonusPerTier.Value +
                                          item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value;
                }

                if (item.m_shared.m_timedBlockBonus > 0f)
                {
                    data.timedBlockBonus = statTier * cfg_TimedBlockBonusPerTier.Value +
                                          item.m_quality * cfg_TimedBlockBonusPerUpgrade.Value;
                }
            }

            // Apply elemental infusion
            if (level >= cfg_ElementalUnlockLevel.Value && cfg_AlwaysAddElementalAtMax.Value && item.IsWeapon())
            {
                // Check if we have a preserved infusion from previous upgrade
                if (!string.IsNullOrEmpty(preservedInfusion))
                {
                    // Calculate effective tier for elemental infusion
                    float elementalEffectiveTier = CalculateElementalEffectiveTier(statTier, item.m_quality);

                    // Reapply the same infusion type with proper scaling
                    ApplySpecificInfusion(preservedInfusion, baseStats, elementalEffectiveTier, data);
                    Debug.Log($"[BlacksmithingExpanded] Preserved and scaled {preservedInfusion} infusion for {item.m_shared.m_name} (tier: {elementalEffectiveTier:F1})");
                }
                else
                {
                    // Check if we already applied elemental bonuses through damage system
                    bool appliedElementalBonus = HasElementalDamageBonus(data);
                    if (!appliedElementalBonus)
                    {
                        // This is a new item, apply random infusion
                        float elementalEffectiveTier = CalculateElementalEffectiveTier(statTier, item.m_quality);
                        ApplyElementalInfusion(item, baseStats, elementalEffectiveTier, data);
                    }
                }
            }

            // Save and load to apply changes
            data.Save();
            data.Load();

            Debug.Log($"[BlacksmithingExpanded] Applied bonuses to {item.m_shared.m_name}: level={level}, tier={statTier}, quality={item.m_quality}");
        }

        // SIMPLIFIED: Apply damage bonuses with cleaner logic
        private static void ApplyDamageBonuses(ItemDrop.ItemData item, ItemBaseStats baseStats, int statTier, BlacksmithingItemData data)
        {
            // Calculate tier bonus
            float tierBonus;
            if (cfg_UsePercentageDamageBonus.Value)
            {
                tierBonus = statTier * cfg_DamagePercentageBonusPerTier.Value / 100f;
            }
            else
            {
                tierBonus = statTier * cfg_DamageBonusPerTier.Value;
            }

            // Calculate upgrade bonus
            float upgradeBonus = 0f;
            if (item.m_quality > 0)
            {
                if (cfg_UsePercentageUpgradeBonus.Value)
                {
                    upgradeBonus = item.m_quality * cfg_StatPercentageBonusPerUpgrade.Value / 100f;
                }
                else
                {
                    // Flat upgrade bonus - convert to equivalent percentage if main system is percentage
                    if (cfg_UsePercentageDamageBonus.Value)
                    {
                        // Convert flat upgrade to percentage equivalent based on total base damage
                        float totalBaseDamage = GetTotalPhysicalDamage(baseStats);
                        if (totalBaseDamage > 0f)
                        {
                            upgradeBonus = (item.m_quality * cfg_StatBonusPerUpgrade.Value) / totalBaseDamage;
                        }
                    }
                    else
                    {
                        upgradeBonus = item.m_quality * cfg_StatBonusPerUpgrade.Value;
                    }
                }
            }

            // Apply total bonus
            ApplyRandomDamageBonus(item, baseStats, tierBonus, upgradeBonus, data);
        }

        // SIMPLIFIED: Calculate effective tier for elemental bonuses
        private static float CalculateElementalEffectiveTier(int statTier, int quality)
        {
            float tierComponent = statTier;

            if (cfg_UsePercentageUpgradeBonus.Value)
            {
                // Convert percentage upgrade to tier equivalent
                tierComponent += quality * cfg_StatPercentageBonusPerUpgrade.Value / cfg_ElementalPercentageBonusPerTier.Value;
            }
            else
            {
                // Convert flat upgrade to tier equivalent  
                tierComponent += quality * cfg_StatBonusPerUpgrade.Value / cfg_ElementalPercentageBonusPerTier.Value;
            }

            return tierComponent;
        }

        // Add this helper method:
        private static bool HasElementalDamageBonus(BlacksmithingItemData data)
        {
            return data.damageFire > 0f || data.damageFrost > 0f || data.damageLightning > 0f ||
                   data.damagePoison > 0f || data.damageSpirit > 0f;
        }

        // Add this method to apply a specific infusion type:
        private static void ApplySpecificInfusion(string infusionType, ItemBaseStats baseStats, float effectiveTiers, BlacksmithingItemData data)
        {
            if (cfg_UsePercentageElementalBonus.Value)
            {
                // Percentage-based elemental system
                float percentageBonus = effectiveTiers * cfg_ElementalPercentageBonusPerTier.Value / 100f;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats); // Use total damage

                switch (infusionType)
                {
                    case "Fire":
                        data.damageFire = totalBaseDamage * percentageBonus;
                        break;
                    case "Frost":
                        data.damageFrost = totalBaseDamage * percentageBonus;
                        break;
                    case "Lightning":
                        data.damageLightning = totalBaseDamage * percentageBonus;
                        break;
                    case "Poison":
                        data.damagePoison = totalBaseDamage * percentageBonus;
                        break;
                    case "Spirit":
                        data.damageSpirit = totalBaseDamage * percentageBonus;
                        break;
                }
            }
            else
            {
                // Flat bonus system
                switch (infusionType)
                {
                    case "Fire":
                        data.damageFire = effectiveTiers * cfg_FireBonusPerTier.Value;
                        break;
                    case "Frost":
                        data.damageFrost = effectiveTiers * cfg_FrostBonusPerTier.Value;
                        break;
                    case "Lightning":
                        data.damageLightning = effectiveTiers * cfg_LightningBonusPerTier.Value;
                        break;
                    case "Poison":
                        data.damagePoison = effectiveTiers * cfg_PoisonBonusPerTier.Value;
                        break;
                    case "Spirit":
                        data.damageSpirit = effectiveTiers * cfg_SpiritBonusPerTier.Value;
                        break;
                }
            }

            data.infusion = infusionType;
        }

        // FIXED: Helper method to calculate total physical damage for multi-damage-type weapons
        private static float GetTotalPhysicalDamage(ItemBaseStats baseStats)
        {
            return baseStats.damages.m_blunt + baseStats.damages.m_slash + baseStats.damages.m_pierce;
        }

        // UPDATED: Apply random damage bonus with separate tier and upgrade bonuses
        private static void ApplyRandomDamageBonus(ItemDrop.ItemData item, ItemBaseStats baseStats, float tierBonus, float upgradeBonus, BlacksmithingItemData data)
        {
            var validTypes = new List<System.Action>();

            if (cfg_UsePercentageDamageBonus.Value)
            {
                // Percentage-based damage system with TOTAL damage calculation (FIXED)
                float totalPercentageBonus = tierBonus + upgradeBonus;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats); // Use total damage instead of max

                // Always include physical damage types if they exist
                if (baseStats.damages.m_blunt > 0f)
                    validTypes.Add(() => data.damageBlunt = totalBaseDamage * totalPercentageBonus);
                if (baseStats.damages.m_slash > 0f)
                    validTypes.Add(() => data.damageSlash = totalBaseDamage * totalPercentageBonus);
                if (baseStats.damages.m_pierce > 0f)
                    validTypes.Add(() => data.damagePierce = totalBaseDamage * totalPercentageBonus);

                // Conditionally include elemental types if config allows and they exist
                if (cfg_BoostElementalWeapons.Value)
                {
                    if (baseStats.damages.m_fire > 0f)
                        validTypes.Add(() => data.damageFire = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_frost > 0f)
                        validTypes.Add(() => data.damageFrost = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_lightning > 0f)
                        validTypes.Add(() => data.damageLightning = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_poison > 0f)
                        validTypes.Add(() => data.damagePoison = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_spirit > 0f)
                        validTypes.Add(() => data.damageSpirit = totalBaseDamage * totalPercentageBonus);
                }
            }
            else
            {
                // Flat damage system (legacy)
                float totalFlatBonus = tierBonus + upgradeBonus;

                // Always include physical damage types if they exist
                if (baseStats.damages.m_blunt > 0f) validTypes.Add(() => data.damageBlunt = totalFlatBonus);
                if (baseStats.damages.m_slash > 0f) validTypes.Add(() => data.damageSlash = totalFlatBonus);
                if (baseStats.damages.m_pierce > 0f) validTypes.Add(() => data.damagePierce = totalFlatBonus);

                // Conditionally include elemental types if config allows and they exist
                if (cfg_BoostElementalWeapons.Value)
                {
                    if (baseStats.damages.m_fire > 0f) validTypes.Add(() => data.damageFire = totalFlatBonus);
                    if (baseStats.damages.m_frost > 0f) validTypes.Add(() => data.damageFrost = totalFlatBonus);
                    if (baseStats.damages.m_lightning > 0f) validTypes.Add(() => data.damageLightning = totalFlatBonus);
                    if (baseStats.damages.m_poison > 0f) validTypes.Add(() => data.damagePoison = totalFlatBonus);
                    if (baseStats.damages.m_spirit > 0f) validTypes.Add(() => data.damageSpirit = totalFlatBonus);
                }
            }

            if (validTypes.Count > 0)
            {
                validTypes[UnityEngine.Random.Range(0, validTypes.Count)]();
            }
        }

        private static void ApplyElementalInfusion(ItemDrop.ItemData item, ItemBaseStats baseStats, float effectiveTiers, BlacksmithingItemData data)
        {
            var elements = new List<(string name, System.Action apply)>();

            if (cfg_UsePercentageElementalBonus.Value)
            {
                // Percentage-based elemental system
                float percentageBonus = effectiveTiers * cfg_ElementalPercentageBonusPerTier.Value / 100f;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats); // Use total damage

                // Only add elements that don't already exist in base stats
                if (baseStats.damages.m_fire <= 0f)
                {
                    elements.Add(("Fire", () => {
                        data.damageFire = totalBaseDamage * percentageBonus;
                        data.infusion = "Fire";
                    }
                    ));
                }

                if (baseStats.damages.m_frost <= 0f)
                {
                    elements.Add(("Frost", () => {
                        data.damageFrost = totalBaseDamage * percentageBonus;
                        data.infusion = "Frost";
                    }
                    ));
                }

                if (baseStats.damages.m_lightning <= 0f)
                {
                    elements.Add(("Lightning", () => {
                        data.damageLightning = totalBaseDamage * percentageBonus;
                        data.infusion = "Lightning";
                    }
                    ));
                }

                if (baseStats.damages.m_poison <= 0f)
                {
                    elements.Add(("Poison", () => {
                        data.damagePoison = totalBaseDamage * percentageBonus;
                        data.infusion = "Poison";
                    }
                    ));
                }

                if (baseStats.damages.m_spirit <= 0f)
                {
                    elements.Add(("Spirit", () => {
                        data.damageSpirit = totalBaseDamage * percentageBonus;
                        data.infusion = "Spirit";
                    }
                    ));
                }
            }
            else
            {
                // Original flat bonus system
                if (baseStats.damages.m_fire <= 0f)
                {
                    elements.Add(("Fire", () => {
                        data.damageFire = effectiveTiers * cfg_FireBonusPerTier.Value;
                        data.infusion = "Fire";
                    }
                    ));
                }

                if (baseStats.damages.m_frost <= 0f)
                {
                    elements.Add(("Frost", () => {
                        data.damageFrost = effectiveTiers * cfg_FrostBonusPerTier.Value;
                        data.infusion = "Frost";
                    }
                    ));
                }

                if (baseStats.damages.m_lightning <= 0f)
                {
                    elements.Add(("Lightning", () => {
                        data.damageLightning = effectiveTiers * cfg_LightningBonusPerTier.Value;
                        data.infusion = "Lightning";
                    }
                    ));
                }

                if (baseStats.damages.m_poison <= 0f)
                {
                    elements.Add(("Poison", () => {
                        data.damagePoison = effectiveTiers * cfg_PoisonBonusPerTier.Value;
                        data.infusion = "Poison";
                    }
                    ));
                }

                if (baseStats.damages.m_spirit <= 0f)
                {
                    elements.Add(("Spirit", () => {
                        data.damageSpirit = effectiveTiers * cfg_SpiritBonusPerTier.Value;
                        data.infusion = "Spirit";
                    }
                    ));
                }
            }

            if (elements.Count > 0)
            {
                var selected = elements[UnityEngine.Random.Range(0, elements.Count)];
                selected.apply();

                string bonusType = cfg_UsePercentageElementalBonus.Value ? "%" : "flat";
                Debug.Log($"[BlacksmithingExpanded] Infused {item.m_shared.m_name} with {data.infusion} ({bonusType} bonus system)");
            }
        }

        // ================================
        // HARMONY PATCHES
        // ================================

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        public static class Patch_Crafting
        {
            static void Postfix(InventoryGui __instance)
            {
                var player = Player.m_localPlayer;
                if (player?.GetInventory() == null) return;

                var craftedItem = player.GetInventory().GetAllItems().LastOrDefault();
                if (craftedItem?.m_shared == null) return;

                int level = GetPlayerBlacksmithingLevel(player);
                if (level <= 0) return;

                // Apply bonuses immediately
                ApplyCraftingBonuses(craftedItem, level);

                // Handle XP
                HandleCraftingXP(player, craftedItem);

                // Extra item chance
                float extraChance = cfg_ChanceExtraItemAt100.Value * (level / 100f);
                if (UnityEngine.Random.value <= extraChance)
                {
                    player.GetInventory().AddItem(craftedItem.m_shared.m_name, 1, 1, 0, player.GetPlayerID(), player.GetPlayerName());
                    player.Message(MessageHud.MessageType.TopLeft, "Masterwork crafting created an extra item!");
                }
            }
        }

        private static void HandleCraftingXP(Player player, ItemDrop.ItemData item)
        {
            // First craft bonus
            string craftKey = "crafted_" + item.m_shared.m_name;
            if (!player.m_customData.ContainsKey(craftKey))
            {
                player.m_customData[craftKey] = "1";
                GiveBlacksmithingXP(player, cfg_FirstCraftBonusXP.Value);
            }

            // Regular XP
            GiveBlacksmithingXP(player, cfg_XPPerCraft.Value);
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
        public static class Patch_Tooltip
        {
            public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
            {
                if (item == null) return;

                var data = item.Data().Get<BlacksmithingItemData>();
                if (data?.level > 0)
                {
                    if (cfg_ShowBlacksmithLevelInTooltip.Value)
                    {
                        __result += $"\n<color=orange>Forged at Blacksmithing {data.level}</color>";
                    }

                    if (cfg_ShowInfusionInTooltip.Value && !string.IsNullOrEmpty(data.infusion))
                    {
                        __result += $"\n<color=#87CEEB>Elemental Infusion: {data.infusion}</color>";
                    }
                }
            }
        }

        // ================================
        // WORKSTATION PATCHES
        // ================================

        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        public static class Patch_Smelter_AddOre
        {
            static void Postfix(Smelter __instance, Humanoid user, bool __result)
            {
                if (!__result || !(user is Player player)) return;

                GiveBlacksmithingXP(player, cfg_XPPerSmelt.Value);

                var zdo = __instance.m_nview?.GetZDO();
                if (zdo == null) return;

                // Identify workstation type
                bool isKiln = __instance.m_name.Contains("charcoal_kiln");
                bool isBlastFurnace = __instance.m_name.Contains("blastfurnace");

                int level = GetPlayerBlacksmithingLevel(player);
                int tier = level / cfg_InfusionTierInterval.Value;

                if (tier <= 0) return;

                // Calculate speed bonus
                float speedBonusPerTier = isKiln ? cfg_KilnSpeedBonusPerTier.Value : cfg_SmeltingSpeedBonusPerTier.Value;
                float speedMultiplier = 1f + (tier * speedBonusPerTier);

                // Create infusion
                var infusion = new WorkstationInfusion
                {
                    tier = tier,
                    timestamp = Time.time,
                    originalSpeed = __instance.m_secPerProduct,
                    bonusSpeed = __instance.m_secPerProduct / speedMultiplier
                };

                // Store infusion based on type
                bool isNewInfusion = false;

                if (isKiln)
                {
                    if (kilnInfusions.ContainsKey(zdo.m_uid))
                    {
                        kilnInfusions[zdo.m_uid].timestamp = Time.time;
                        kilnInfusions[zdo.m_uid].tier = tier;
                    }
                    else
                    {
                        kilnInfusions[zdo.m_uid] = infusion;
                        isNewInfusion = true;
                    }
                }
                else if (isBlastFurnace)
                {
                    blastFurnaceInfusions[zdo.m_uid] = infusion;
                    isNewInfusion = true;
                }
                else
                {
                    smelterInfusions[zdo.m_uid] = infusion;
                    isNewInfusion = true;
                }

                // Handle ore save chance for smelters and blast furnaces
                if (!isKiln)
                {
                    float saveChance = cfg_SmelterSaveOreChanceAt100.Value * (level / 100f);
                    if (UnityEngine.Random.value <= saveChance)
                    {
                        if (__instance.GetFuel() < __instance.m_maxFuel)
                        {
                            __instance.m_nview.GetZDO().Set("fuel", __instance.GetFuel() + 1f);
                        }
                    }
                }

                // Apply speed immediately
                __instance.m_secPerProduct = infusion.bonusSpeed;

                // Only create light effect for NEW infusions
                if (isNewInfusion)
                {
                    ManageInfusionGlow(__instance.transform, zdo.m_uid, true);
                }

                string workstationType = isKiln ? "Kiln" : isBlastFurnace ? "Blast Furnace" : "Smelter";
                Debug.Log($"[BlacksmithingExpanded] {workstationType} infused: " +
                         $"Tier {tier}, Speed {infusion.originalSpeed:F2} -> {infusion.bonusSpeed:F2} " +
                         $"({speedMultiplier:F2}x faster)");
            }
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static class Patch_Smelter_Update
        {
            static void Prefix(Smelter __instance)
            {
                var zdo = __instance.m_nview?.GetZDO();
                if (zdo == null) return;

                bool isKiln = __instance.m_name.Contains("charcoal_kiln");
                bool isBlastFurnace = __instance.m_name.Contains("blastfurnace");

                if (isKiln)
                {
                    HandleKilnInfusion(__instance, zdo);
                }
                else if (isBlastFurnace)
                {
                    HandleBlastFurnaceInfusion(__instance, zdo);
                }
                else
                {
                    HandleSmelterInfusion(__instance, zdo);
                }
            }

            private static void HandleBlastFurnaceInfusion(Smelter blastFurnace, ZDO zdo)
            {
                if (!originalBlastFurnaceSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalBlastFurnaceSpeeds[zdo.m_uid] = blastFurnace.m_secPerProduct;
                }

                if (blastFurnaceInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    float timeSinceInfusion = Time.time - infusion.timestamp;
                    bool gracePeriodActive = timeSinceInfusion < 1f;

                    bool shouldExpire = !gracePeriodActive && (
                        infusion.IsExpired ||
                        blastFurnace.GetQueueSize() == 0 ||
                        blastFurnace.GetFuel() <= 0f);

                    if (shouldExpire)
                    {
                        blastFurnace.m_secPerProduct = originalBlastFurnaceSpeeds[zdo.m_uid];
                        blastFurnaceInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(blastFurnace.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        blastFurnace.m_secPerProduct = infusion.bonusSpeed;
                        infusion.wasActive = true;
                    }
                }
                else
                {
                    blastFurnace.m_secPerProduct = originalBlastFurnaceSpeeds[zdo.m_uid];
                }
            }

            private static void HandleKilnInfusion(Smelter kiln, ZDO zdo)
            {
                if (!originalKilnSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalKilnSpeeds[zdo.m_uid] = kiln.m_secPerProduct;
                }

                if (kilnInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    bool kilnStillRunning = kiln.GetFuel() > 0f || kiln.GetBakeTimer() > 0f;

                    if (infusion.IsExpired || !kilnStillRunning)
                    {
                        kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                        kilnInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(kiln.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        kiln.m_secPerProduct = infusion.bonusSpeed;
                    }
                }
                else
                {
                    kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                }
            }

            private static void HandleSmelterInfusion(Smelter smelter, ZDO zdo)
            {
                if (!originalSmelterSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalSmelterSpeeds[zdo.m_uid] = smelter.m_secPerProduct;
                }

                if (smelterInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    float timeSinceInfusion = Time.time - infusion.timestamp;
                    bool gracePeriodActive = timeSinceInfusion < 1f;

                    bool shouldExpire = !gracePeriodActive && (
                        infusion.IsExpired ||
                        smelter.GetQueueSize() == 0 ||
                        smelter.GetFuel() <= 0f);

                    if (shouldExpire)
                    {
                        smelter.m_secPerProduct = originalSmelterSpeeds[zdo.m_uid];
                        smelterInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(smelter.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        smelter.m_secPerProduct = infusion.bonusSpeed;
                        infusion.wasActive = true;
                    }
                }
                else
                {
                    smelter.m_secPerProduct = originalSmelterSpeeds[zdo.m_uid];
                }
            }
        }

        private static void ManageInfusionGlow(Transform workstation, ZDOID zdoid, bool enable)
        {
            if (!cfg_ShowInfusionVisualEffect.Value)
            {
                if (activeGlowEffects.TryGetValue(zdoid, out var existingEffect))
                {
                    if (existingEffect != null)
                        UnityEngine.Object.Destroy(existingEffect);

                    activeGlowEffects.Remove(zdoid);
                }
                return;
            }

            try
            {
                if (enable && !activeGlowEffects.ContainsKey(zdoid))
                {
                    var glowObject = new GameObject("InfusionGlow_Light");
                    glowObject.transform.position = workstation.position + Vector3.up * 0.5f;
                    glowObject.transform.SetParent(workstation);

                    var light = glowObject.AddComponent<Light>();
                    light.color = new Color(1f, 0.5f, 0.1f); // Fire Orange
                    light.intensity = 4f;
                    light.range = 8f;
                    light.type = LightType.Point;

                    glowObject.AddComponent<FlickerLight>();

                    activeGlowEffects[zdoid] = glowObject;
                }
                else if (!enable && activeGlowEffects.TryGetValue(zdoid, out var existingEffect))
                {
                    if (existingEffect != null)
                        UnityEngine.Object.Destroy(existingEffect);

                    activeGlowEffects.Remove(zdoid);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Light management failed: {ex}");
            }
        }

        public class FlickerLight : MonoBehaviour
        {
            private Light lightSource;
            private float baseIntensity;

            void Start()
            {
                lightSource = GetComponent<Light>();
                baseIntensity = lightSource.intensity;
            }

            void Update()
            {
                if (lightSource != null)
                {
                    lightSource.intensity = baseIntensity + UnityEngine.Random.Range(-0.2f, 0.2f);
                }
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "OnRepairPressed")]
        public static class Patch_InventoryRepair
        {
            static bool Prefix(InventoryGui __instance)
            {
                if (!cfg_EnableInventoryRepair.Value) return true;

                var player = Player.m_localPlayer;
                if (player == null) return true;

                int level = GetPlayerBlacksmithingLevel(player);
                if (level < cfg_InventoryRepairUnlockLevel.Value) return true;

                var inventory = player.GetInventory();
                if (inventory == null) return true;

                // Find first damaged item and repair it
                foreach (var item in inventory.GetAllItems())
                {
                    if (item?.m_shared?.m_maxDurability > 0 && item.m_durability < item.GetMaxDurability())
                    {
                        float repairAmount = item.GetMaxDurability() - item.m_durability;
                        item.m_durability = item.GetMaxDurability();

                        GiveBlacksmithingXP(player, cfg_XPPerRepair.Value);

                        // Visual effect
                        var fx = ZNetScene.instance.GetPrefab("vfx_Smelter_add");
                        if (fx != null)
                            UnityEngine.Object.Instantiate(fx, player.transform.position, Quaternion.identity);

                        player.Message(MessageHud.MessageType.TopLeft,
                            $"Repaired with masterwork precision! (+{repairAmount:F0} durability)", 0, null);

                        return false; // Prevent default repair, we handled it
                    }
                }

                return true; // No items to repair, allow default behavior
            }
        }

        // ================================
        // UTILITY METHODS
        // ================================

        [HarmonyPatch(typeof(Attack), "Start")]
        public static class Patch_AttackStart
        {
            static void Prefix(Attack __instance, ItemDrop.ItemData weapon, Humanoid character)
            {
                if (weapon?.IsWeapon() != true || !(character is Player player)) return;
                if (!weapon.m_shared.m_name.ToLower().Contains("spear")) return; // Only spears

                // Check if this is a secondary attack (spear throw)
                if (__instance.m_attackType == Attack.AttackType.Projectile)
                {
                    var data = weapon.Data().Get<BlacksmithingItemData>();
                    if (data?.level > 0)
                    {
                        // Create a unique key for this spear throw
                        string key = GenerateSpearKey(weapon, player);

                        // Store the blacksmithing data
                        var storedData = new BlacksmithingItemData();
                        CopyBlacksmithingData(data, storedData);
                        tempSpearDataStorage[key] = storedData;

          //              Debug.Log($"[BlacksmithingExpanded] Stored spear data for throw: {weapon.m_shared.m_name} (level {data.level}) Key: {key}");

                        // Clean up old entries
                        CleanupOldSpearData();
                    }
                }
            }
        }

        // Patch 2: Apply data to newly created ItemDrop instances (when spears land and become pickupable)
        [HarmonyPatch(typeof(ItemDrop), "Start")]
        public static class Patch_ItemDropStart
        {
            static void Postfix(ItemDrop __instance)
            {
                if (__instance?.m_itemData?.IsWeapon() != true) return;
                if (!__instance.m_itemData.m_shared.m_name.ToLower().Contains("spear")) return;

                var item = __instance.m_itemData;
                var existingData = item.Data().Get<BlacksmithingItemData>();

                // Only restore if the item doesn't already have blacksmithing data
                if (existingData?.level > 0) return;

                // Look for matching stored spear data
                var bestMatch = FindBestSpearMatch(item);
                if (bestMatch.Key != null && bestMatch.Value != null)
                {
                    var newData = item.Data().GetOrCreate<BlacksmithingItemData>();
                    CopyBlacksmithingData(bestMatch.Value, newData);

                    newData.Save();
                    // DON'T call Load() here - it triggers ApplyCraftingBonuses which can change infusions
                    // Instead, manually apply the stats to preserve the exact infusion type
                    ApplyStoredBlacksmithingStats(item, newData);

                    // Remove the used data
                    tempSpearDataStorage.Remove(bestMatch.Key);

          //          Debug.Log($"[BlacksmithingExpanded] Restored blacksmithing data to landed spear: {item.m_shared.m_name} (level {newData.level}, infusion: {newData.infusion})");
                }
            }
        }

        // Patch 3: Handle item cloning to preserve data (for completeness)
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.Clone))]
        public static class Patch_ItemDataClone
        {
            static void Postfix(ItemDrop.ItemData __result, ItemDrop.ItemData __instance)
            {
                if (__instance == null || __result == null) return;

                var originalData = __instance.Data().Get<BlacksmithingItemData>();
                if (originalData?.level > 0)
                {
                    var clonedData = __result.Data().GetOrCreate<BlacksmithingItemData>();
                    CopyBlacksmithingData(originalData, clonedData);

                    clonedData.Save();
                    clonedData.Load();

              //      Debug.Log($"[BlacksmithingExpanded] Copied blacksmithing data to cloned item: {__result.m_shared.m_name}");
                }
            }
        }

        // Helper method to generate unique spear key
        private static string GenerateSpearKey(ItemDrop.ItemData weapon, Player player)
        {
            return $"{weapon.m_shared.m_name}_{weapon.m_quality}_{weapon.m_durability:F1}_{player.GetPlayerID()}_{Time.time:F2}";
        }

        // Helper method to find best matching spear data
        private static KeyValuePair<string, BlacksmithingItemData> FindBestSpearMatch(ItemDrop.ItemData item)
        {
            string bestKey = null;
            BlacksmithingItemData bestData = null;
            float bestScore = 0f;

            var currentTime = Time.time;

            foreach (var kvp in tempSpearDataStorage.ToList())
            {
                var keyParts = kvp.Key.Split('_');
                if (keyParts.Length < 5) continue;

                string storedName = keyParts[0];
                if (!int.TryParse(keyParts[1], out int storedQuality)) continue;
                if (!float.TryParse(keyParts[2], out float storedDurability)) continue;
                if (!long.TryParse(keyParts[3], out long storedPlayerId)) continue;
                if (!float.TryParse(keyParts[4], out float timestamp)) continue;

                // Check basic match criteria
                if (storedName != item.m_shared.m_name || storedQuality != item.m_quality) continue;

                // Calculate match score (prefer recent throws with similar durability)
                float timeDiff = currentTime - timestamp;
                if (timeDiff > 60f) continue; // Ignore throws older than 60 seconds

                float durabilityDiff = Mathf.Abs(item.m_durability - storedDurability);
                float score = 1000f - (durabilityDiff * 10f) - (timeDiff * 5f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = kvp.Key;
                    bestData = kvp.Value;
                }
            }

            return new KeyValuePair<string, BlacksmithingItemData>(bestKey, bestData);
        }

        // Helper method to clean up old stored spear data
        private static void CleanupOldSpearData()
        {
            var currentTime = Time.time;
            var keysToRemove = new List<string>();

            foreach (var kvp in tempSpearDataStorage)
            {
                var keyParts = kvp.Key.Split('_');
                if (keyParts.Length >= 5 && float.TryParse(keyParts[4], out float timestamp))
                {
                    if (currentTime - timestamp > 120f) // Remove data older than 2 minutes
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                else
                {
                    keysToRemove.Add(kvp.Key); // Remove malformed keys
                }
            }

            foreach (var key in keysToRemove)
            {
                tempSpearDataStorage.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
            //    Debug.Log($"[BlacksmithingExpanded] Cleaned up {keysToRemove.Count} old spear data entries");
            }
        }

        // Helper method to manually apply stored blacksmithing stats without triggering random infusions
        private static void ApplyStoredBlacksmithingStats(ItemDrop.ItemData item, BlacksmithingItemData data)
        {
            var baseStats = GetBaseStats(item);

            // Apply durability
            if (data.maxDurability > 0f)
            {
                item.m_shared.m_maxDurability = data.maxDurability;
                item.m_durability = Mathf.Min(item.m_durability, data.maxDurability);
            }

            // Apply armor
            if (data.armorBonus > 0f)
            {
                item.m_shared.m_armor = baseStats.armor + data.armorBonus;
            }

            // Apply damage bonuses - preserve exact values from stored data
            item.m_shared.m_damages.m_blunt = baseStats.damages.m_blunt + data.damageBlunt;
            item.m_shared.m_damages.m_slash = baseStats.damages.m_slash + data.damageSlash;
            item.m_shared.m_damages.m_pierce = baseStats.damages.m_pierce + data.damagePierce;
            item.m_shared.m_damages.m_fire = baseStats.damages.m_fire + data.damageFire;
            item.m_shared.m_damages.m_frost = baseStats.damages.m_frost + data.damageFrost;
            item.m_shared.m_damages.m_lightning = baseStats.damages.m_lightning + data.damageLightning;
            item.m_shared.m_damages.m_poison = baseStats.damages.m_poison + data.damagePoison;
            item.m_shared.m_damages.m_spirit = baseStats.damages.m_spirit + data.damageSpirit;

            // Apply shield bonuses
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                if (data.blockPowerBonus > 0f) item.m_shared.m_blockPower += data.blockPowerBonus;
                if (data.timedBlockBonus > 0f) item.m_shared.m_timedBlockBonus += data.timedBlockBonus;
            }

           // Debug.Log($"[BlacksmithingExpanded] Manually applied stored stats to {item.m_shared.m_name} - preserving {data.infusion} infusion");
        }

        // Helper method to copy blacksmithing data between instances
        private static void CopyBlacksmithingData(BlacksmithingItemData source, BlacksmithingItemData target)
        {
            target.level = source.level;
            target.infusion = source.infusion;
            target.baseDurability = source.baseDurability;
            target.maxDurability = source.maxDurability;
            target.armorBonus = source.armorBonus;
            target.damageBlunt = source.damageBlunt;
            target.damageSlash = source.damageSlash;
            target.damagePierce = source.damagePierce;
            target.damageFire = source.damageFire;
            target.damageFrost = source.damageFrost;
            target.damageLightning = source.damageLightning;
            target.damagePoison = source.damagePoison;
            target.damageSpirit = source.damageSpirit;
            target.blockPowerBonus = source.blockPowerBonus;
            target.timedBlockBonus = source.timedBlockBonus;
        }
        private static Sprite LoadEmbeddedSprite(string resourceName, int width, int height)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BlacksmithingExpanded.icons." + resourceName))
                {
                    if (stream == null) return null;

                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        byte[] bytes = ms.ToArray();

                        Texture2D tex = new Texture2D(width, height);
                        if (tex.LoadImage(bytes))
                        {
                            return Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.zero);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to load sprite {resourceName}: {ex}");
            }
            return null;
        }
    }
}
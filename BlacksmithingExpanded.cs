using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
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
using ItemDataManager;

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

        // Base stat cache - single source of truth
        private static readonly Dictionary<string, ItemBaseStats> baseStatsCache = new();

        // Workstation infusions (unchanged)
        internal static Dictionary<ZDOID, WorkstationInfusion> smelterInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> kilnInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> blastFurnaceInfusions = new();
        private static readonly Dictionary<ZDOID, float> originalBlastFurnaceSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalSmelterSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalKilnSpeeds = new();
        private static readonly Dictionary<ZDOID, GameObject> activeGlowEffects = new();

        // Config entries (keeping your existing structure)
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

        // Durability
        internal static ConfigEntry<int> cfg_DurabilityTierInterval;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerTier;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_RespectOriginalDurability;
        internal static ConfigEntry<float> cfg_MaxDurabilityCap;

        // Armor & Weapons
        internal static ConfigEntry<int> cfg_StatTierInterval;
        internal static ConfigEntry<float> cfg_ArmorBonusPerTier;
        internal static ConfigEntry<float> cfg_ArmorBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_ArmorCap;
        internal static ConfigEntry<int> cfg_DamageBonusPerTier;
        internal static ConfigEntry<float> cfg_StatBonusPerUpgrade;

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

        // Data structures for clean organization
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

            // Sync skill configs
            if (blacksmithSkill != null)
            {
                blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
                cfg_SkillGainFactor.SettingChanged += (_, _) => blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                cfg_SkillEffectFactor.SettingChanged += (_, _) => blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
            }

            // Register ItemDataManager type for force loading
            ItemInfo.ForceLoadTypes.Add(typeof(BlacksmithingItemData));

            harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void SetupConfigs()
        {
            // General
            cfg_SkillGainFactor = AddConfig("General", "Skill gain factor", 1f, "Rate at which you gain Blacksmithing XP");
            cfg_SkillEffectFactor = AddConfig("General", "Skill effect factor", 1f, "Multiplier applied to all skill effects");
            cfg_InfusionTierInterval = AddConfig("General", "Workstation infusion milestone interval", 10, "Levels per infusion tier");
            cfg_SmeltingSpeedBonusPerTier = AddConfig("General", "Smelting speed bonus per tier", 0.15f, "Speed bonus per tier (15% = 0.15)");
            cfg_KilnSpeedBonusPerTier = AddConfig("General", "Kiln speed bonus per tier", 0.15f, "Speed bonus per tier (15% = 0.15)");
            cfg_InfusionExpireTime = AddConfig("General", "Infusion expire time", 300f, "How long infusions last after adding fuel/ore (seconds)");
            cfg_ShowInfusionVisualEffect = AddConfig("General", "Show infusion visual effect", true, "Show glowing effect when smelters/kilns are infused"); cfg_SmelterSaveOreChanceAt100 = AddConfig("General", "Ore save chance at 100", 0.2f, "Chance to save ore at level 100");
            cfg_ChanceExtraItemAt100 = AddConfig("General", "Extra item chance at 100", 0.05f, "Chance for extra item at level 100");
            cfg_EnableInventoryRepair = AddConfig("General", "Enable inventory repair", true, "Allow repairing from inventory");
            cfg_InventoryRepairUnlockLevel = AddConfig("General", "Inventory repair unlock level", 70, "Level for inventory repairs");

            // XP
            cfg_XPPerCraft = AddConfig("XP", "XP per craft", 5f, "Base XP when crafting");
            cfg_XPPerSmelt = AddConfig("XP", "XP per smelt", 0.75f, "Base XP when smelting");
            cfg_XPPerRepair = AddConfig("XP", "XP per repair", 1f, "Base XP when repairing");
            cfg_XPPerUpgrade = AddConfig("XP", "XP per upgrade", 5f, "XP for upgrading");
            cfg_FirstCraftBonusXP = AddConfig("XP", "First craft bonus XP", 25f, "Bonus XP for first craft of item type");

            // Tooltips
            cfg_ShowBlacksmithLevelInTooltip = AddConfig("Tooltip", "Show level in tooltip", true, "Show blacksmith level in tooltip");
            cfg_ShowInfusionInTooltip = AddConfig("Tooltip", "Show infusion in tooltip", false, "Show elemental infusion in tooltip");

            // Stats
            cfg_DurabilityTierInterval = AddConfig("Durability", "Durability tier interval", 10, "Levels per durability tier");
            cfg_DurabilityBonusPerTier = AddConfig("Durability", "Durability bonus per tier", 50f, "Durability bonus per tier");
            cfg_DurabilityBonusPerUpgrade = AddConfig("Durability", "Durability bonus per upgrade", 50f, "Durability bonus per upgrade");
            cfg_RespectOriginalDurability = AddConfig("Durability", "Respect original durability", true, "Only boost if base durability > 0");
            cfg_MaxDurabilityCap = AddConfig("Durability", "Max durability cap", 2000f, "Maximum durability cap");
            cfg_BoostElementalWeapons = AddConfig("Stats", "Boost elemental weapons", true, "Allow boosting elemental damage on weapons that already have it");
            cfg_ElementalWeaponBoostChance = AddConfig("Stats", "Elemental weapon boost chance", 0.5f, "Chance to boost elemental vs physical damage on mixed weapons (0.5 = 50/50)");

            cfg_StatTierInterval = AddConfig("Stats", "Stat tier interval", 20, "Levels per stat tier");
            cfg_ArmorBonusPerTier = AddConfig("Stats", "Armor bonus per tier", 5f, "Armor bonus per tier");
            cfg_ArmorBonusPerUpgrade = AddConfig("Stats", "Armor bonus per upgrade", 2f, "Armor bonus per upgrade");
            cfg_ArmorCap = AddConfig("Stats", "Armor cap", 300f, "Maximum armor value");
            cfg_DamageBonusPerTier = AddConfig("Stats", "Damage bonus per tier", 10, "Damage bonus per tier");
            cfg_StatBonusPerUpgrade = AddConfig("Stats", "Stat bonus per upgrade", 8f, "Stat bonus per upgrade");

            cfg_AlwaysAddElementalAtMax = AddConfig("Elemental", "Add elemental at milestone", true, "Add elemental at milestone");
            cfg_ElementalUnlockLevel = AddConfig("Elemental", "Elemental unlock level", 100, "Level for elemental bonuses");
            cfg_FireBonusPerTier = AddConfig("Elemental", "Fire bonus per tier", 3f, "Bonus fire damage per tier (DoT)");
            cfg_FrostBonusPerTier = AddConfig("Elemental", "Frost bonus per tier", 6f, "Bonus frost damage per tier (burst)");
            cfg_LightningBonusPerTier = AddConfig("Elemental", "Lightning bonus per tier", 5f, "Bonus lightning damage per tier");
            cfg_PoisonBonusPerTier = AddConfig("Elemental", "Poison bonus per tier", 2.5f, "Bonus poison damage per tier (DoT)");
            cfg_SpiritBonusPerTier = AddConfig("Elemental", "Spirit bonus per tier", 4f, "Bonus spirit damage per tier (anti-undead)");

            cfg_TimedBlockBonusPerTier = AddConfig("Shields", "Timed block bonus per tier", 0.05f, "Parry bonus per tier");
            cfg_TimedBlockBonusPerUpgrade = AddConfig("Shields", "Timed block bonus per upgrade", 0.05f, "Parry bonus per upgrade");
            cfg_BlockPowerBonusPerTier = AddConfig("Shields", "Block power bonus per tier", 2f, "Block power per tier");
            cfg_BlockPowerBonusPerUpgrade = AddConfig("Shields", "Block power bonus per upgrade", 1f, "Block power per upgrade");
        }

        // ================================
        // ITEMDATAMANAGER IMPLEMENTATION
        // ================================

        private class BlacksmithingItemData : ItemData
        {
            // Track which items have active modifications to avoid conflicts
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
        // CORE FUNCTIONALITY - SIMPLIFIED
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
                Debug.Log($"[BlacksmithingExpanded] Gave {adjusted:F2} XP to {player.GetPlayerName()}");
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
                //Debug.Log($"[BlacksmithingExpanded] Cached base stats for {key}");
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

            var baseStats = GetBaseStats(item);
            int statTier = level / cfg_StatTierInterval.Value;
            int durabilityTier = level / cfg_DurabilityTierInterval.Value;

            // Create or get existing blacksmithing data
            var data = item.Data().GetOrCreate<BlacksmithingItemData>();
            data.level = level;
            data.baseDurability = baseStats.durability;

            // Apply durability bonus
            if (!cfg_RespectOriginalDurability.Value || baseStats.durability > 0f)
            {
                float durabilityBonus = (durabilityTier * cfg_DurabilityBonusPerTier.Value) +
                                        (item.m_quality * cfg_DurabilityBonusPerUpgrade.Value);

                data.maxDurability = baseStats.durability + durabilityBonus;

                if (cfg_MaxDurabilityCap.Value > 0f)
                    data.maxDurability = Mathf.Min(data.maxDurability, cfg_MaxDurabilityCap.Value);
            }

            // Apply damage bonuses
            float damageBonus = statTier * cfg_DamageBonusPerTier.Value;
            float upgradeDamageBonus = item.m_quality * cfg_StatBonusPerUpgrade.Value;
            float totalDamageBonus = damageBonus + upgradeDamageBonus;

            if (totalDamageBonus > 0)
            {
                ApplyRandomDamageBonus(item, baseStats, totalDamageBonus, data);
            }

            // Apply armor bonus
            if (baseStats.armor > 0f)
            {
                float armorBonus = (statTier * cfg_ArmorBonusPerTier.Value) +
                                 (item.m_quality * cfg_ArmorBonusPerUpgrade.Value);

                data.armorBonus = armorBonus;
                if (cfg_ArmorCap.Value > 0f && baseStats.armor + armorBonus > cfg_ArmorCap.Value)
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
                ApplyElementalInfusion(item, baseStats, statTier, data);
            }

            // Save and load to apply changes
            data.Save();
            data.Load();

            Debug.Log($"[BlacksmithingExpanded] Applied bonuses to {item.m_shared.m_name}: level={level}, tier={statTier}");
        }

        private static void ApplyRandomDamageBonus(ItemDrop.ItemData item, ItemBaseStats baseStats, float bonus, BlacksmithingItemData data)
        {
            var validTypes = new List<System.Action>();

            // Always include physical damage types if they exist
            if (baseStats.damages.m_blunt > 0f) validTypes.Add(() => data.damageBlunt = bonus);
            if (baseStats.damages.m_slash > 0f) validTypes.Add(() => data.damageSlash = bonus);
            if (baseStats.damages.m_pierce > 0f) validTypes.Add(() => data.damagePierce = bonus);

            // Conditionally include elemental types if config allows and they exist
            if (cfg_BoostElementalWeapons.Value)
            {
                if (baseStats.damages.m_fire > 0f) validTypes.Add(() => data.damageFire = bonus);
                if (baseStats.damages.m_frost > 0f) validTypes.Add(() => data.damageFrost = bonus);
                if (baseStats.damages.m_lightning > 0f) validTypes.Add(() => data.damageLightning = bonus);
                if (baseStats.damages.m_poison > 0f) validTypes.Add(() => data.damagePoison = bonus);
                if (baseStats.damages.m_spirit > 0f) validTypes.Add(() => data.damageSpirit = bonus);
            }

            if (validTypes.Count > 0)
            {
                validTypes[UnityEngine.Random.Range(0, validTypes.Count)]();
            }
        }

        private static void ApplyElementalInfusion(ItemDrop.ItemData item, ItemBaseStats baseStats, int tier, BlacksmithingItemData data)
        {
            var elements = new List<(string name, System.Action apply)>
    {
        ("Fire", () => {
            data.damageFire = tier * cfg_FireBonusPerTier.Value;
            data.infusion = "Fire";
        }),
        ("Frost", () => {
            data.damageFrost = tier * cfg_FrostBonusPerTier.Value;
            data.infusion = "Frost";
        }),
        ("Lightning", () => {
            data.damageLightning = tier * cfg_LightningBonusPerTier.Value;
            data.infusion = "Lightning";
        }),
        ("Poison", () => {
            data.damagePoison = tier * cfg_PoisonBonusPerTier.Value;
            data.infusion = "Poison";
        }),
        ("Spirit", () => {
            data.damageSpirit = tier * cfg_SpiritBonusPerTier.Value;
            data.infusion = "Spirit";
        })
    };

            // Filter out elements already present in base stats
            elements.RemoveAll(e => e.name == "Fire" && baseStats.damages.m_fire > 0f);
            elements.RemoveAll(e => e.name == "Frost" && baseStats.damages.m_frost > 0f);
            elements.RemoveAll(e => e.name == "Lightning" && baseStats.damages.m_lightning > 0f);
            elements.RemoveAll(e => e.name == "Poison" && baseStats.damages.m_poison > 0f);
            elements.RemoveAll(e => e.name == "Spirit" && baseStats.damages.m_spirit > 0f);

            if (elements.Count > 0)
            {
                var selected = elements[UnityEngine.Random.Range(0, elements.Count)];
                selected.apply();
                Debug.Log($"[BlacksmithingExpanded] Infused {item.m_shared.m_name} with {data.infusion}");
            }
        }


        // ================================
        // HARMONY PATCHES - SIMPLIFIED
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
        // WORKSTATION PATCHES - SMELTSPEED
        // ================================
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        public static class Patch_Smelter_AddOre_CleanVisual
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
                bool isSmelter = !isKiln && !isBlastFurnace;

                int level = GetPlayerBlacksmithingLevel(player);
                int tier = level / cfg_InfusionTierInterval.Value;

                if (tier <= 0) return;

                // Calculate speed bonus (use smelter values for blast furnace)
                float speedBonusPerTier = isKiln ? cfg_KilnSpeedBonusPerTier.Value : cfg_SmeltingSpeedBonusPerTier.Value;
                float speedMultiplier = 1f + (tier * speedBonusPerTier);

                // Create enhanced infusion
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
                        // UPDATE existing infusion timestamp instead of replacing
                        kilnInfusions[zdo.m_uid].timestamp = Time.time;
                        kilnInfusions[zdo.m_uid].tier = tier;
                        //Debug.Log($"[BlacksmithingExpanded] Updated existing kiln infusion timestamp");
                    }
                    else
                    {
                        // CREATE new infusion
                        kilnInfusions[zdo.m_uid] = infusion;
                        isNewInfusion = true;
                        //Debug.Log($"[BlacksmithingExpanded] Created new kiln infusion");
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

                // Only create light effect for NEW infusions, not updates
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

        // Modify the UpdateSmelter patch to handle blast furnaces
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static class Patch_Smelter_Update_CleanVisual
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
                        //Debug.Log($"[BlacksmithingExpanded] Blast furnace infusion expired for {zdo.m_uid}");
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
                    // Check if kiln is still operational (has fuel OR is actively baking)
                    bool kilnStillRunning = kiln.GetFuel() > 0f || kiln.GetBakeTimer() > 0f;

                    // Expire if time limit reached OR kiln stopped running
                    if (infusion.IsExpired || !kilnStillRunning)
                    {
                        kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                        kilnInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(kiln.transform, zdo.m_uid, false);

                        string reason = infusion.IsExpired ? "time limit" : "kiln stopped running";
                        //Debug.Log($"[BlacksmithingExpanded] Kiln infusion expired due to {reason}");
                    }
                    else
                    {
                        kiln.m_secPerProduct = infusion.bonusSpeed;
                        float remainingTime = infusion.RemainingTime;
                        //Debug.Log($"[BlacksmithingExpanded] Kiln infusion active - {remainingTime:F0}s remaining, Fuel={kiln.GetFuel()}, BakeTimer={kiln.GetBakeTimer():F1}");
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
                    // Create a new GameObject to hold the light
                    var glowObject = new GameObject("InfusionGlow_Light");
                    glowObject.transform.position = workstation.position + Vector3.up * 0.5f;
                    glowObject.transform.SetParent(workstation);

                    // Add a purple point light
                    var light = glowObject.AddComponent<Light>();
                    light.color = new Color(1f, 0.5f, 0.1f); // Fire Orange
                    light.intensity = 4f;
                    light.range = 8f;
                    light.type = LightType.Point;

                    // Add flicker behavior
                    glowObject.AddComponent<FlickerLight>();

                    activeGlowEffects[zdoid] = glowObject;
                   // Debug.Log($"[BlacksmithingExpanded] Created infusion light for {workstation.name}");
                }
                else if (!enable && activeGlowEffects.TryGetValue(zdoid, out var existingEffect))
                {
                    if (existingEffect != null)
                        UnityEngine.Object.Destroy(existingEffect);

                    activeGlowEffects.Remove(zdoid);
                   // Debug.Log($"[BlacksmithingExpanded] Removed infusion light for {workstation.name}");
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


        // Inventory repair patch
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
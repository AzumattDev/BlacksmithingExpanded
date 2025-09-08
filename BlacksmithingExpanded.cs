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
        internal static ConfigEntry<int> cfg_InfusionTierInterval;
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
        internal static ConfigEntry<bool> cfg_RespectOriginalStats;
        internal static ConfigEntry<float> cfg_FireBonusAtMax;
        internal static ConfigEntry<float> cfg_FrostBonusAtMax;
        internal static ConfigEntry<float> cfg_LightningBonusAtMax;
        internal static ConfigEntry<float> cfg_PoisonBonusAtMax;
        internal static ConfigEntry<bool> cfg_AlwaysAddElementalAtMax;
        internal static ConfigEntry<int> cfg_StatTierInterval;
        internal static ConfigEntry<int> cfg_DurabilityTierInterval;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_RespectOriginalDurability;
        internal static ConfigEntry<float> cfg_ArmorBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_MaxDurabilityCap;
        internal static ConfigEntry<float> cfg_StatBonusPerUpgrade;
        internal static ConfigEntry<int> cfg_MaxStatTypesPerTier;
        internal static ConfigEntry<float> cfg_StatBonusMultiplierPerTier;
        internal static ConfigEntry<float> cfg_StatBonusCapPerType;
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerTier;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_ArmorCap;
        internal static ConfigEntry<int> cfg_ElementalUnlockLevel;
        internal static ConfigEntry<float> cfg_ElementalBonusPerTier;
        internal static ConfigEntry<bool> cfg_ApplyUpgradeBonusAtTierZero;
        internal static ConfigEntry<int> cfg_GearMilestoneInterval;
        internal static ConfigEntry<float> cfg_GearBonusPerMilestone;
        internal static ConfigEntry<float> cfg_GearUpgradeBonusPerMilestone;



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
            cfg_InfusionTierInterval = AddConfig(group, "Infusion tier interval", 10, "Levels required per infusion tier (e.g. 10 = one tier every 10 levels for smelters, kilns, and repairs)."); cfg_DamageBonusPerTier = AddConfig(group, "Damage bonus per tier", 1, "Flat damage added to all damage types per tier.");
            cfg_ArmorBonusPerTier = AddConfig(group, "Armor bonus per tier", 2, "Flat armor added per tier.");
            cfg_DurabilityBonusPerTier = AddConfig(group, "Durability bonus per tier", 50, "Flat durability added per tier.");
            cfg_UpgradeTierPer25Levels = AddConfig(group, "Extra upgrade tiers per 25 levels", 1, "Extra upgrade tiers unlocked every 25 levels.");
            cfg_MaxTierUnlockLevel = AddConfig(group, "Max tier unlock level", 100, "Level at which full 'master' benefits are unlocked.");
            cfg_ChanceExtraItemAt100 = AddConfig(group, "Chance to craft extra item at 100", 0.05f, "Chance at level 100 to produce an extra copy when crafting. Scales with level.");
            cfg_SmelterSaveOreChanceAt100 = AddConfig(group, "Smelter save ore chance at 100", 0.2f, "Chance at level 100 that ore is not consumed (scales with level).");
            cfg_KilnSpeedBonusPerTier = AddConfig(group, "Kiln speed bonus per tier", 0.05f, "Kiln speed multiplier per blacksmithing tier. Example: 0.05 = +5% faster per tier.");
            cfg_EnableInventoryRepair = AddConfig(group, "Enable inventory repair", true, "Allow repairing items from inventory after reaching unlock level.");
            cfg_InventoryRepairUnlockLevel = AddConfig(group, "Inventory repair unlock level", 70, "Blacksmithing level required to repair items from inventory.");
            cfg_SmeltingSpeedBonusPerTier = AddConfig(group, "Smelting speed bonus per tier", 0.05f, "Smelting speed multiplier per blacksmithing tier. Example: 0.05 = +5% faster per tier.");
            cfg_InfusionExpireTime = AddConfig(group, "Infusion expire time (seconds)", 60f, "How long a smelter or kiln retains the contributor's tier bonus before it expires.");
            cfg_RespectOriginalStats = AddConfig(group, "Respect original weapon stats", true, "If true, only scale existing weapon stats. Stats with zero or null values will not be modified.");
            cfg_AlwaysAddElementalAtMax = AddConfig(group, "Add elemental bonus at level 100", true, "If true, adds elemental damage at level 100 even when RespectOriginalStats is enabled.");
            cfg_FireBonusAtMax = AddConfig(group, "Fire bonus at level 100", 5f, "Amount of fire damage added at level 100.");
            cfg_FrostBonusAtMax = AddConfig(group, "Frost bonus at level 100", 5f, "Amount of frost damage added at level 100.");
            cfg_LightningBonusAtMax = AddConfig(group, "Lightning bonus at level 100", 5f, "Amount of lightning damage added at level 100.");
            cfg_PoisonBonusAtMax = AddConfig(group, "Poison bonus at level 100", 5f, "Amount of poison damage added at level 100.");
            cfg_StatTierInterval = AddConfig(group, "Stat tier interval", 25, "Levels required per stat tier (damage, armor, shield bonuses).");
            cfg_DurabilityTierInterval = AddConfig(group, "Durability tier interval", 20, "Levels required per durability tier.");
            cfg_DurabilityBonusPerUpgrade = AddConfig(group, "Durability bonus per upgrade level", 25f, "Extra durability added per item upgrade level (quality).");
            cfg_RespectOriginalDurability = AddConfig(group, "Respect original durability", true, "If true, only boost durability if the base durability is greater than zero.");
            cfg_ArmorBonusPerUpgrade = AddConfig(group, "Armor bonus per upgrade level", 2f, "Flat armor added per upgrade level (quality).");
            cfg_MaxDurabilityCap = AddConfig(group, "Max durability cap", 2000f, "Maximum durability allowed after all bonuses. Set to 0 to disable cap.");
            cfg_StatBonusPerUpgrade = AddConfig(group, "Stat bonus per upgrade level", 1f, "Flat stat bonus (damage and armor) added per item upgrade level.");
            cfg_MaxStatTypesPerTier = AddConfig(group, "Max stat types per tier", 3, "Maximum number of damage types boosted per stat tier when randomizing.");
            cfg_StatBonusMultiplierPerTier = AddConfig(group, "Stat bonus multiplier per tier", 1.0f, "Multiplier applied to stat bonus per tier for advanced scaling.");
            cfg_StatBonusCapPerType = AddConfig(group, "Stat bonus cap per damage type", 100f, "Maximum value allowed for any single damage type after scaling.");
            cfg_TimedBlockBonusPerTier = AddConfig(group, "Timed block bonus per tier", 0.05f, "Parry bonus added per stat tier for shields.");
            cfg_BlockPowerBonusPerUpgrade = AddConfig(group, "Block power bonus per upgrade", 2f, "Flat block power added per upgrade level for shields.");
            cfg_ArmorCap = AddConfig(group, "Armor cap", 300f, "Maximum armor value allowed after all bonuses. Set to 0 to disable.");
            cfg_ElementalUnlockLevel = AddConfig(group, "Elemental unlock level", 100, "Minimum blacksmithing level required before elemental bonuses are applied.");
            cfg_ElementalBonusPerTier = AddConfig(group, "Elemental bonus per tier", 2f, "Elemental damage added per stat tier, separate from physical bonuses.");
            cfg_ApplyUpgradeBonusAtTierZero = AddConfig(group, "Apply upgrade bonus at tier 0", false, "If false, upgrade-based stat bonuses are disabled until the player reaches stat tier 1.");
            cfg_GearMilestoneInterval = AddConfig(group, "Gear bonus milestone interval", 20, "Skill levels required per milestone. Applies to armor, shields, and weapons.");
            cfg_GearBonusPerMilestone = AddConfig(group, "Base gear bonus per milestone", 5f, "Flat bonus added to armor and damage per milestone.");
            cfg_GearUpgradeBonusPerMilestone = AddConfig(group, "Upgrade gear bonus per milestone", 2f, "Extra bonus per upgrade level per milestone. Applies to armor, shields, and weapon damage.");


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
        internal static void ApplyCraftedItemMultipliers(ItemDrop.ItemData item, int level)
        {
            if (item == null || item.m_shared == null || level <= 0) return;

            CacheBaseStats(item);
            string key = item.m_shared.m_name;

            float baseArmor = baseArmorLookup[key];
            HitData.DamageTypes baseDamage = baseDamageLookup[key].Clone();
            float baseDurability = baseDurabilityLookup[key];

            int statTier = level / cfg_StatTierInterval.Value;
            int durabilityTier = level / cfg_DurabilityTierInterval.Value;
            int milestoneCount = level / cfg_GearMilestoneInterval.Value;

            float damageBonus = statTier * cfg_DamageBonusPerTier.Value * cfg_StatBonusMultiplierPerTier.Value
                + milestoneCount * cfg_GearBonusPerMilestone.Value;

            float upgradeDamageBonus = (statTier > 0 || cfg_ApplyUpgradeBonusAtTierZero.Value ? item.m_quality * cfg_StatBonusPerUpgrade.Value : 0f)
                + item.m_quality * milestoneCount * cfg_GearUpgradeBonusPerMilestone.Value;

            float armorBonus = statTier * cfg_ArmorBonusPerTier.Value + milestoneCount * cfg_GearBonusPerMilestone.Value;
            float upgradeArmorBonus = (statTier > 0 || cfg_ApplyUpgradeBonusAtTierZero.Value ? item.m_quality * cfg_ArmorBonusPerUpgrade.Value : 0f)
                + item.m_quality * milestoneCount * cfg_GearUpgradeBonusPerMilestone.Value;

            // Reset to base
            item.m_shared.m_damages = baseDamage.Clone();

            if (cfg_RespectOriginalStats.Value)
            {
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

                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    if (item.m_shared.m_blockPower > 0f)
                        item.m_shared.m_blockPower += armorBonus + upgradeArmorBonus + item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value;

                    if (item.m_shared.m_deflectionForce > 0f)
                        item.m_shared.m_deflectionForce += armorBonus + upgradeArmorBonus;

                    if (item.m_shared.m_timedBlockBonus > 0f)
                        item.m_shared.m_timedBlockBonus += statTier * cfg_TimedBlockBonusPerTier.Value;
                }

                if (level >= cfg_ElementalUnlockLevel.Value && cfg_AlwaysAddElementalAtMax.Value)
                {
                    float elementalBonus = damageBonus + upgradeDamageBonus;

                    if (!item.m_customData.ContainsKey("ElementalInfusion"))
                    {
                        var elementalOptions = new List<(string name, Action)>
                {
                    ("Fire",      () => item.m_shared.m_damages.m_fire      += elementalBonus),
                    ("Frost",     () => item.m_shared.m_damages.m_frost     += elementalBonus),
                    ("Lightning", () => item.m_shared.m_damages.m_lightning += elementalBonus),
                    ("Poison",    () => item.m_shared.m_damages.m_poison    += elementalBonus),
                };

                        var rng = new System.Random();
                        var selected = elementalOptions[rng.Next(elementalOptions.Count)];
                        selected.Item2();
                        item.m_customData["ElementalInfusion"] = selected.name;

                        Debug.Log($"[BlacksmithingExpanded] Infused {item.m_shared.m_name} with {selected.name} at level {level}");
                    }
                    else
                    {
                        string type = item.m_customData["ElementalInfusion"];
                        switch (type)
                        {
                            case "Fire": item.m_shared.m_damages.m_fire += elementalBonus; break;
                            case "Frost": item.m_shared.m_damages.m_frost += elementalBonus; break;
                            case "Lightning": item.m_shared.m_damages.m_lightning += elementalBonus; break;
                            case "Poison": item.m_shared.m_damages.m_poison += elementalBonus; break;
                        }

                        Debug.Log($"[BlacksmithingExpanded] Reapplied elemental infusion: {type} at tier {statTier} (quality {item.m_quality})");
                    }
                }
            }
            else
            {
                var damageSetters = new List<(string name, Action)>
        {
            ("Blunt",     () => item.m_shared.m_damages.m_blunt     += damageBonus + upgradeDamageBonus),
            ("Slash",     () => item.m_shared.m_damages.m_slash     += damageBonus + upgradeDamageBonus),
            ("Pierce",    () => item.m_shared.m_damages.m_pierce    += damageBonus + upgradeDamageBonus),
            ("Fire",      () => item.m_shared.m_damages.m_fire      += damageBonus + upgradeDamageBonus),
            ("Frost",     () => item.m_shared.m_damages.m_frost     += damageBonus + upgradeDamageBonus),
            ("Lightning", () => item.m_shared.m_damages.m_lightning += damageBonus + upgradeDamageBonus),
            ("Poison",    () => item.m_shared.m_damages.m_poison    += damageBonus + upgradeDamageBonus),
            ("Spirit",    () => item.m_shared.m_damages.m_spirit    += damageBonus + upgradeDamageBonus),
        };

                var rng = new System.Random();
                var selected = damageSetters.OrderBy(_ => rng.Next()).Take(Math.Min(cfg_MaxStatTypesPerTier.Value, damageSetters.Count)).ToList();

                foreach (var entry in selected)
                    entry.Item2();

                item.m_shared.m_armor = Mathf.RoundToInt(baseArmor + armorBonus + upgradeArmorBonus);
                if (cfg_ArmorCap.Value > 0f)
                    item.m_shared.m_armor = Mathf.Min(item.m_shared.m_armor, cfg_ArmorCap.Value);

                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    item.m_shared.m_blockPower += armorBonus + upgradeArmorBonus + item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value;
                    item.m_shared.m_deflectionForce += armorBonus + upgradeArmorBonus;
                    item.m_shared.m_timedBlockBonus += statTier * cfg_TimedBlockBonusPerTier.Value;
                }

                var boostedNames = string.Join(", ", selected.Select(s => s.name));
                Debug.Log($"[BlacksmithingExpanded] Boosted damage types: {boostedNames}");
            }

            if (!cfg_RespectOriginalDurability.Value || baseDurability > 0f)
            {
                float durabilityBonus = (durabilityTier * cfg_DurabilityBonusPerTier.Value) + (item.m_quality * cfg_DurabilityBonusPerUpgrade.Value);
                float finalDurability = baseDurability + durabilityBonus;

                if (cfg_MaxDurabilityCap.Value > 0f)
                    finalDurability = Mathf.Min(finalDurability, cfg_MaxDurabilityCap.Value);

                item.m_shared.m_maxDurability = finalDurability;
                item.m_durability = item.GetMaxDurability();

                Debug.Log($"[BlacksmithingExpanded] Applied durability bonus: base={baseDurability}, tierBonus={durabilityTier * cfg_DurabilityBonusPerTier.Value}, upgradeBonus={item.m_quality * cfg_DurabilityBonusPerUpgrade.Value}, capped={cfg_MaxDurabilityCap.Value}");
            }
            else
            {
                item.m_shared.m_maxDurability = baseDurability;
                item.m_durability = item.GetMaxDurability();

                Debug.Log($"[BlacksmithingExpanded] Skipped durability boost for {item.m_shared.m_name} due to RespectOriginalDurability");
            }

            Debug.Log($"[BlacksmithingExpanded] Applied statTier={statTier}, durabilityTier={durabilityTier} to {item.m_shared.m_name} ({(cfg_RespectOriginalStats.Value ? "existing stat scaling" : "randomized damage types")}).");
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

                var inventory = player.GetInventory();
                if (inventory == null) return;

                // Safely get the last item added (crafted or upgraded)
                var craftedItem = inventory.GetAllItems().LastOrDefault();
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

                Debug.Log($"[BlacksmithingExpanded] Applied stats to crafted/upgraded item: {craftedItem.m_shared.m_name}");
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
            typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
        public static class Patch_Blacksmithing_Tooltip
        {
            public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
            {
                var data = item.Data().Get<BlacksmithingData>();
                if (data != null && data.blacksmithLevel > 0)
                {
                    // Always show the forged skill level
                    __result += $"\n<color=orange>Forged at Blacksmithing {data.blacksmithLevel}</color>";

                    // Show infusion if applied
                    if (item.m_customData.TryGetValue("ElementalInfusion", out string infusionType))
                    {
                        __result += $"\n<color=#87CEEB>Elemental Infusion: {infusionType}</color>";
                    }

                    // Optional: show durability bonus
                    float baseDurability = item.m_shared.m_maxDurability;
                    if (baseDurability > 0 && data.blacksmithLevel >= BlacksmithingExpanded.cfg_DurabilityTierInterval.Value)
                    {
                        int durabilityTier = data.blacksmithLevel / BlacksmithingExpanded.cfg_DurabilityTierInterval.Value;
                        float bonus = (durabilityTier * BlacksmithingExpanded.cfg_DurabilityBonusPerTier.Value) +
                                      (item.m_quality * BlacksmithingExpanded.cfg_DurabilityBonusPerUpgrade.Value);

                        if (bonus > 0)
                            __result += $"\n<color=#90EE90>Durability Bonus: +{bonus:F0}</color>";
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

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
    }
        }

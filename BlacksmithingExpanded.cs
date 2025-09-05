using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using System;
using System.Collections.Generic;
using System.IO;
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

        // Server-synced config helper
        private static readonly ConfigSync configSync = new(ModGUID)
        {
            DisplayName = ModName,
            CurrentVersion = ModVersion,
            MinimumRequiredVersion = ModVersion,
            ModRequired = true
        };

        internal static Skill blacksmithSkill;

        // Config entries
        internal static ConfigEntry<float> cfg_SkillGainFactor;
        internal static ConfigEntry<float> cfg_SkillEffectFactor;
        internal static ConfigEntry<float> cfg_DurabilityPercentPer5Levels;
        internal static ConfigEntry<float> cfg_DamagePercentPer10Levels;
        internal static ConfigEntry<float> cfg_ArmorPercentPer10Levels;
        internal static ConfigEntry<int> cfg_UpgradeTierPer25Levels;
        internal static ConfigEntry<int> cfg_MaxTierUnlockLevel;
        internal static ConfigEntry<float> cfg_ChanceExtraItemAt100;
        internal static ConfigEntry<float> cfg_SmelterSaveOreChanceAt100;
        internal static ConfigEntry<bool> cfg_EnableInventoryRepair;
        internal static ConfigEntry<int> cfg_InventoryRepairUnlockLevel;

        // XP config
        internal static ConfigEntry<float> cfg_XPPerCraft;
        internal static ConfigEntry<float> cfg_XPPerSmelt;
        internal static ConfigEntry<float> cfg_XPPerRepair;

        // embedded icon sprite cached
        private static Sprite s_skillIcon;

        // Config helper that registers entries with ServerSync
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

            // Load embedded sprite from resources/icons/smithing.png (embedded resource)
            try
            {
                s_skillIcon = LoadEmbeddedSprite("smithing.png", 64, 64);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to load embedded sprite: {ex}");
                s_skillIcon = null;
            }

            // create/register skill via SkillManager (Skill ctor registers with SkillManager)
            // Use the Sprite overload so the internal SkillDef has the icon early.
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
                    // fallback to string constructor if sprite somehow unavailable (Skill has overload string->sprite lookup in some versions)
                    blacksmithSkill = new Skill("Blacksmithing", "smithing.png")
                    {
                        Configurable = true
                    };
                }

                // Provide readable localized name + description immediately
                blacksmithSkill.Name.English("Blacksmithing");
                blacksmithSkill.Description.English(
                    "Craft better, last longer. Improves durability, damage, and armor of crafted items. Grants smelting and repair bonuses."
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to construct SkillManager skill: {ex}");
            }

            // Config group
            string group = "Blacksmithing";

            // Config entries (server-synced)
            cfg_SkillGainFactor = AddConfig(group, "Skill gain factor", blacksmithSkill?.SkillGainFactor ?? 1f, "Rate at which you gain Blacksmithing XP.");
            cfg_SkillEffectFactor = AddConfig(group, "Skill effect factor", blacksmithSkill?.SkillEffectFactor ?? 1f, "Multiplier applied to all skill effects.");

            cfg_DurabilityPercentPer5Levels = AddConfig(group, "Durability % per 5 levels", 1f, "Percent added to base durability every 5 levels (cumulative).");
            cfg_DamagePercentPer10Levels = AddConfig(group, "Damage % per 10 levels", 1f, "Percent added to base damage every 10 levels (cumulative).");
            cfg_ArmorPercentPer10Levels = AddConfig(group, "Armor % per 10 levels", 1f, "Percent added to base armor every 10 levels (cumulative).");

            cfg_UpgradeTierPer25Levels = AddConfig(group, "Extra upgrade tiers per 25 levels", 1, "Extra upgrade tiers unlocked every 25 levels.");
            cfg_MaxTierUnlockLevel = AddConfig(group, "Max tier unlock level", 100, "Level at which full 'master' benefits are unlocked.");

            cfg_ChanceExtraItemAt100 = AddConfig(group, "Chance to craft extra item at 100", 0.05f, "Chance at level 100 to produce an extra copy when crafting. Scales with level.");
            cfg_SmelterSaveOreChanceAt100 = AddConfig(group, "Smelter save ore chance at 100", 0.2f, "Chance at level 100 that ore is not consumed (scales with level).");

            cfg_EnableInventoryRepair = AddConfig(group, "Enable inventory repair", true, "Allow repairing items from inventory after reaching unlock level.");
            cfg_InventoryRepairUnlockLevel = AddConfig(group, "Inventory repair unlock level", 70, "Blacksmithing level required to repair items from inventory.");

            // XP configs
            cfg_XPPerCraft = AddConfig(group, "XP per craft", 0.50f, "Base XP granted when crafting an item.");
            cfg_XPPerSmelt = AddConfig(group, "XP per smelt", 0.75f, "Base XP granted when adding ore to smelter (filling).");
            cfg_XPPerRepair = AddConfig(group, "XP per repair", 0.30f, "Base XP granted when repairing an item.");

            // wire dynamic config changes to SkillManager skill fields
            if (blacksmithSkill != null)
            {
                blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
                cfg_SkillGainFactor.SettingChanged += (_, _) => blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                cfg_SkillEffectFactor.SettingChanged += (_, _) => blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
            }

            // Harmony patches
            harmony.PatchAll();

            Logger.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void OnDestroy()
        {
            // unpatch only ours
            harmony?.UnpatchSelf();
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

                // convert our skill name to Skills.SkillType via Skill.fromName using the literal we registered
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

        /// <summary>
        /// Give XP to player's Blacksmithing skill using SkillManager extension.
        /// We hardcode the skill name "Blacksmithing" to avoid null-key problems during startup / localization.
        /// </summary>
        internal static void GiveBlacksmithingXP(Player player, float amount)
        {
            if (player == null || amount <= 0f) return;

            try
            {
                // Multiply by configured SkillGainFactor (SkillManager uses skillDef.m_increseStep internally).
                float adjusted = amount * cfg_SkillGainFactor.Value;

                // Use SkillManager extension that accepts Character/Player; pass the hardcoded skill name string.
                SkillManager.SkillExtensions.RaiseSkill(player, "Blacksmithing", adjusted);

                Debug.Log($"[BlacksmithingExpanded] {player.GetPlayerName()} gained {adjusted} Blacksmithing XP (raw {amount}).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to raise skill XP for {player?.GetPlayerName()}: {ex}");
            }
        }

        internal static float GetDurabilityMultiplier(int level) => 1f + (level / 5f) * cfg_DurabilityPercentPer5Levels.Value / 100f;
        internal static float GetDamageMultiplier(int level) => 1f + (level / 10f) * cfg_DamagePercentPer10Levels.Value / 100f;
        internal static float GetArmorMultiplier(int level) => 1f + (level / 10f) * cfg_ArmorPercentPer10Levels.Value / 100f;
        internal static int GetExtraUpgradeTiers(int level) => (level / 25) * cfg_UpgradeTierPer25Levels.Value;
        internal static float GetChanceScaledWithLevel(float maxChanceAt100, int level) => Mathf.Clamp01(maxChanceAt100 * (level / 100f));

        internal static void ApplyCraftedItemMultipliers(ItemDrop.ItemData item, int level)
        {
            try
            {
                if (item == null || item.m_shared == null || level <= 0) return;

                // Durability (set to scaled max durability)
                item.m_durability = item.GetMaxDurability() * GetDurabilityMultiplier(level);

                // Damage multipliers (shared.m_damages)
                var shared = item.m_shared;
                float mult = GetDamageMultiplier(level);

                // Apply multipliers to the damage struct fields
                shared.m_damages.m_blunt *= mult;
                shared.m_damages.m_slash *= mult;
                shared.m_damages.m_pierce *= mult;
                shared.m_damages.m_fire *= mult;
                shared.m_damages.m_frost *= mult;
                shared.m_damages.m_lightning *= mult;
                shared.m_damages.m_poison *= mult;
                shared.m_damages.m_spirit *= mult;

                // Armor
                shared.m_armor = Mathf.RoundToInt(shared.m_armor * GetArmorMultiplier(level));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] ApplyCraftedItemMultipliers error: {ex}");
            }
        }

        internal static Player FindCrafterPlayer(ItemDrop.ItemData item)
        {
            try
            {
                if (item == null) return null;
                // look for common field names that could contain crafter/player info
                var t = item.GetType();
                var fieldsToTry = new[] { "m_crafter", "m_creator", "m_instigator", "m_crafterUID" };
                foreach (var fName in fieldsToTry)
                {
                    var f = AccessTools.Field(t, fName);
                    if (f == null) continue;
                    var val = f.GetValue(item);
                    if (val == null) continue;

                    if (val is Player p) return p;
                    if (val is string s)
                    {
                        foreach (var pObj in UnityEngine.Object.FindObjectsOfType<Player>())
                        {
                            if (pObj.GetPlayerName() == s) return pObj;
                        }
                    }
                }
            }
            catch { }
            return null;
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
                Debug.Log($"[BlacksmithingExpanded] Gave extra item {shared.m_name} to {crafter.GetPlayerName()}.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] TryGiveExtraItemToCrafter error: {ex}");
            }
        }

        // -----------------------
        // Harmony Patches
        // -----------------------

        // Ensure the vanilla Skills list gets our skill icon and configured increase step (prevents UI NREs and shows icon).
        [HarmonyPatch(typeof(Skills), nameof(Skills.Awake))]
        private static class Patch_Skills_Awake_SetSkillDefIcon
        {
            private static void Postfix(Skills __instance)
            {
                try
                {
                    // Convert the registered skill name to a SkillType and patch any SkillDef that matches.
                    var customSkillType = Skill.fromName("Blacksmithing");

                    foreach (var def in __instance.m_skills)
                    {
                        if (def != null && def.m_skill == customSkillType)
                        {
                            if (s_skillIcon != null)
                            {
                                def.m_icon = s_skillIcon;
                            }

                            // keep skill gain factor in sync if config exists
                            try
                            {
                                if (cfg_SkillGainFactor != null)
                                {
                                    def.m_increseStep = cfg_SkillGainFactor.Value;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Patch_Skills_Awake_SetSkillDefIcon error: {ex}");
                }
            }
        }

        // 1) Modify crafted item stats and give XP when player crafts via InventoryGui.DoCrafting
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        public static class Patch_CraftedItem_AdjustStats
        {
            static void Prefix(InventoryGui __instance)
            {
                try
                {
                    var item = __instance.m_craftRecipe?.m_item?.m_itemData;
                    if (item == null) return;

                    // find crafter (best-effort)
                    var crafter = BlacksmithingExpanded.FindCrafterPlayer(item) ?? Player.m_localPlayer;
                    if (crafter == null) return;

                    int level = BlacksmithingExpanded.GetPlayerBlacksmithingLevel(crafter);
                    if (level > 0)
                    {
                        BlacksmithingExpanded.ApplyCraftedItemMultipliers(item, level);

                        // award XP (configurable)
                        BlacksmithingExpanded.GiveBlacksmithingXP(crafter, cfg_XPPerCraft.Value);

                        // chance to produce an extra item at higher levels
                        float chance = BlacksmithingExpanded.GetChanceScaledWithLevel(cfg_ChanceExtraItemAt100.Value, level);
                        if (UnityEngine.Random.value <= chance)
                        {
                            BlacksmithingExpanded.TryGiveExtraItemToCrafter(item, crafter);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Crafting patch failed: {ex}");
                }
            }
        }

        // 2) Smelter: grant XP when player successfully adds ore to the smelter (filling action).
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

                    // award XP for smelting/filling action (SkillManager)
                    GiveBlacksmithingXP(player, cfg_XPPerSmelt.Value);

                    // optional – chance save ore mechanic (message)
                    int level = GetPlayerBlacksmithingLevel(player);
                    if (level > 0)
                    {
                        float chance = GetChanceScaledWithLevel(cfg_SmelterSaveOreChanceAt100.Value, level);
                        if (UnityEngine.Random.value <= chance)
                        {
                            try
                            {
                                var skills = player.GetComponent<Skills>();
                                if (skills != null)
                                {
                                    SkillManager.SkillExtensions.RaiseSkill(skills, "Blacksmithing", 0.01f);
                                }
                            }
                            catch { }

                            player.Message(MessageHud.MessageType.TopLeft, "Smelter efficiency saved some ore!", 0, null);
                        }
                    }

                    Debug.Log($"[BlacksmithingExpanded] {player.GetPlayerName()} added ore to smelter and gained {cfg_XPPerSmelt.Value} xp.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Smelter OnAddOre postfix error: {ex}");
                }
            }
        }

        // 3) Terminal command to repair all items in inventory (if enabled & unlocked).
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        public static class Patch_Terminal_InitTerminal_AddRepairCommand
        {
            static void Postfix()
            {
                try
                {
                    if (Terminal.commands == null) return;

                    if (!Terminal.commands.ContainsKey("repairhand"))
                    {
                        Terminal.ConsoleCommand cmd = new Terminal.ConsoleCommand(
                            "repairhand",
                            "Repairs all items in your inventory (if unlocked by Blacksmithing)",
                            (Terminal.ConsoleEventArgs args) =>
                            {
                                Player player = Player.m_localPlayer;
                                if (player == null) return;

                                if (!cfg_EnableInventoryRepair.Value)
                                {
                                    player.Message(MessageHud.MessageType.TopLeft, "Inventory repair is disabled in config.", 0, null);
                                    return;
                                }

                                int level = GetPlayerBlacksmithingLevel(player);
                                if (level < cfg_InventoryRepairUnlockLevel.Value)
                                {
                                    player.Message(MessageHud.MessageType.TopLeft, $"Need Blacksmithing {cfg_InventoryRepairUnlockLevel.Value} to repair inventory items!", 0, null);
                                    return;
                                }

                                var inv = player.GetInventory();
                                if (inv == null) return;

                                int repaired = 0;
                                foreach (var item in inv.GetAllItems())
                                {
                                    if (item != null && item.m_durability < item.GetMaxDurability())
                                    {
                                        item.m_durability = item.GetMaxDurability();
                                        repaired++;
                                        // XP per repaired item
                                        GiveBlacksmithingXP(player, cfg_XPPerRepair.Value);
                                    }
                                }

                                if (repaired > 0)
                                    player.Message(MessageHud.MessageType.TopLeft, $"Repaired {repaired} item(s)!", 0, null);
                                else
                                    player.Message(MessageHud.MessageType.TopLeft, "No items needed repairing.", 0, null);
                            },
                            true, true
                        );

                        Terminal.commands["repairhand"] = cmd;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Failed to add repairhand command: {ex}");
                }
            }
        }

        // 4) Optionally modify CraftingStation.GetLevel if you want station level bonuses
        [HarmonyPatch]
        public static class Patch_CraftingStation_LevelBonus
        {
            static MethodInfo TargetMethod()
            {
                var m = AccessTools.Method(typeof(CraftingStation), "GetLevel", new Type[] { typeof(bool) });
                if (m != null) return m;
                return AccessTools.Method(typeof(CraftingStation), "GetLevel");
            }

            static void Postfix(CraftingStation __instance, ref int __result)
            {
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null) return;

                    int level = GetPlayerBlacksmithingLevel(player);
                    if (level <= 0) return;

                    __result += GetExtraUpgradeTiers(level);
                }
                catch { }
            }
        }

        // -----------------------
        // Embedded resource helpers (same pattern as Cooking mod)
        // -----------------------
        private static byte[] ReadEmbeddedFileBytes(string name)
        {
            using MemoryStream stream = new();
            // Try full assembly namespace + ".icons." + name (common pattern)
            var asm = Assembly.GetExecutingAssembly();
            string baseName = asm.GetName().Name ?? "";
            string resourceName = baseName + ".icons." + name;

            var s = asm.GetManifestResourceStream(resourceName) ?? asm.GetManifestResourceStream("icons." + name) ?? asm.GetManifestResourceStream(name);
            if (s == null)
            {
                throw new FileNotFoundException($"Embedded resource not found: tried '{resourceName}', 'icons.{name}', and '{name}'");
            }
            s.CopyTo(stream);
            return stream.ToArray();
        }

        private static Texture2D loadTexture(string name)
        {
            Texture2D texture = new(2, 2);
            texture.LoadImage(ReadEmbeddedFileBytes(name));
            return texture;
        }

        private static Sprite LoadEmbeddedSprite(string name, int width, int height)
        {
            var tex = loadTexture("icons." + name);
            return Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.zero);
        }
    }
}

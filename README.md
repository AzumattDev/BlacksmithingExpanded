# 🔧 BlacksmithingExpanded

**BlacksmithingExpanded** adds a skill-based blacksmithing system to Valheim, enhancing crafting, smelting, and coal production based on player experience. The more you forge, the better your results.

---

## 🛠️ Features

- **Blacksmithing XP**  
  Earn XP by crafting items, repairing "broken" gear, smelting ore, and feeding kilns.

- **Tier Bonuses**  
  Your blacksmithing level determines your tier, which boosts forge performance.

- **Smelter Speed Boost**  
  Smelters process ore faster when infused by a higher-tier blacksmith.

- **Kiln Speed Boost**  
  Kilns produce coal faster when fed by a skilled player.

- **Ore Efficiency Chance**  
  Higher-tier players have a chance to save ore during smelting.

- **Inventory Repair Unlock**  
  Repair items directly from your inventory once you reach the required skill level.

- **Infusion Expiry**  
  Bonuses last for a configurable duration or until the structure becomes idle.

---

## ⚙️ Configuration

All features are fully configurable via BepInEx config files or server sync:

- `XPPerCraft` – XP gained per item crafted  
- `XPPerSmelt` – XP gained per ore smelted or wood added to kiln  
- `XPPerRepair` – XP gained per item repaired  
- `TierInterval` – Levels required per tier (e.g. every 20 levels = new tier)  
- `SmeltingSpeedBonusPerTier` – % faster smelting per tier  
- `KilnSpeedBonusPerTier` – % faster coal production per tier  
- `SmelterSaveOreChanceAt100` – Max chance to save ore at level 100  
- `InventoryRepairUnlockLevel` – Skill level required to enable inventory repairs  
- `InfusionExpireTime` – How long a smelter or kiln retains the contributor's tier bonus

---

## 💡 Philosophy

This mod is designed to be immersive, fair, and multiplayer-safe — rewarding skilled blacksmiths with faster forge performance and better resource efficiency. It integrates seamlessly with Valheim’s existing systems while adding depth to crafting and resource management.

---

## 🔗 Compatibility

- Fully compatible with most crafting and skill mods  
- No known conflicts with Blaxxun’s Blacksmithing mod — features stack independently - if used together you may have to set crafting xp to 0.

---

## 📦 Manual Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
2. Download and place `BlacksmithingExpanded.dll` into your `BepInEx/plugins` folder
3. Launch the game and configure settings via `BepInEx/config`


---

Craft with purpose. Smelt with pride. Let the forge remember your touch.

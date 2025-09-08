<p align="center"><img src="https://noobtrap.eu/images/crystallights/blacksmithexpandedheader.png" alt="BSHeader"></p>

A modular, skill-driven enhancement system for Valheim that rewards crafting, repairing, and smelting with scalable stat bonuses, elemental infusions, and structure upgrades. Built for transparency, tweakability, and community-driven balance.

---

## 🛠️ Features

- **Blacksmithing XP System**  
  Gain experience by crafting items, repairing gear, smelting ore, and feeding kilns.

- **Tier-Based Stat Scaling**  
  Your blacksmithing level determines your stat tier, which boosts:
  - Weapon damage (physical + elemental)
  - Armor and shield block power
  - Item durability
  - Parry bonus

- **Upgrade-Based Bonuses**  
  Items gain additional stat bonuses based on their upgrade level (quality).

- **Elemental Infusion**  
  At high levels, blacksmiths unlock elemental damage bonuses (fire, frost, lightning, poison) with configurable scaling and unlock thresholds.

- **Smelter & Kiln Infusion**  
  Structures infused by high-tier players process faster and may save resources.

- **Inventory Repair Unlock**  
  Repair items directly from your inventory once you reach the required skill level.

- **Infusion Expiry**  
  Bonuses last for a configurable duration or until the structure becomes idle.

---

## ⚙️ Configuration

Every mechanic is fully configurable via BepInEx config files or server sync. Highlights include:

### 🔧 XP & Tiering

- `XPPerCraft`, `XPPerSmelt`, `XPPerRepair` — XP gained per action  
- `TierInterval`, `StatTierInterval`, `DurabilityTierInterval` — level thresholds for tier scaling  
- `UpgradeTierPer25Levels` — extra upgrade tiers unlocked every 25 levels  
- `MaxTierUnlockLevel` — level at which full master bonuses unlock  

### ⚔️ Stat Scaling

- `DamageBonusPerTier`, `ArmorBonusPerTier`, `DurabilityBonusPerTier`  
- `StatBonusPerUpgrade`, `ArmorBonusPerUpgrade`, `DurabilityBonusPerUpgrade`  
- `StatBonusMultiplierPerTier` — multiplier applied to stat bonus per tier  
- `StatBonusCapPerType` — maximum value allowed per damage type  
- `MaxStatTypesPerTier` — limits randomized damage types per tier  
- `ArmorCap`, `MaxDurabilityCap` — hard caps for armor and durability  

### 🛡️ Shield Bonuses

- `TimedBlockBonusPerTier` — parry bonus added per stat tier  
- `BlockPowerBonusPerUpgrade` — block power added per upgrade level  

### 🔥 Elemental Control

- `ElementalUnlockLevel` — minimum level required before elemental bonuses apply  
- `ElementalBonusPerTier` — elemental damage added per stat tier  
- `AlwaysAddElementalAtMax` — toggle bonus at level 100  
- `FireBonusAtMax`, `FrostBonusAtMax`, `LightningBonusAtMax`, `PoisonBonusAtMax` — fixed bonuses at level 100  

### 🔁 Smelting & Repair

- `SmeltingSpeedBonusPerTier`, `KilnSpeedBonusPerTier`  
- `SmelterSaveOreChanceAt100` — chance to save ore at level 100  
- `InfusionExpireTime` — how long infusion bonuses last  
- `EnableInventoryRepair`, `InventoryRepairUnlockLevel` — unlock repair from inventory  

---

## 🔗 Compatibility

- Fully compatible with most crafting and skill mods  
- No known conflicts with Blaxxun’s Blacksmithing mod — features stack independently  
- If used together, set crafting XP to 0 to avoid double gain  

---

## 📦 Manual Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)  
2. Place `BlacksmithingExpanded.dll` into your `BepInEx/plugins` folder  
3. Launch the game and configure settings via `BepInEx/config`  

---

## 💬 Support & Community

<p align="center"><h2>For Questions or Comments find Gravebear in the Odin Plus Team on Discord:</h2></p>

<p align="center"><a href="https://discord.gg/mbkPcvu9ax"><img src="https://i.imgur.com/Ji3u63C.png"></a></p>

<p align="center">Visit my buymeacoffee for a free Admin craft Shark Hat and Tuna Sword!</p>

<p align="center"><a href="https://www.buymeacoffee.com/Gravebear"><img src="https://noobtrap.eu/images/crystallights/GBSupporter.png"></a></p>

---

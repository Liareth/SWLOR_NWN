﻿using System;
using System.Collections.Generic;
using System.Linq;
using NWN;
using SWLOR.Game.Server.Bioware.Contracts;
using SWLOR.Game.Server.Data.Contracts;
using SWLOR.Game.Server.Data.Entities;
using SWLOR.Game.Server.Enumeration;
using SWLOR.Game.Server.GameObject;
using SWLOR.Game.Server.Service.Contracts;

namespace SWLOR.Game.Server.Event.Delayed
{
    public class CraftCreateItem: IRegisteredEvent
    {
        private readonly INWScript _;
        private readonly IDataContext _db;
        private readonly IErrorService _error;
        private readonly ICraftService _craft;
        private readonly IComponentBonusService _componentBonus;
        private readonly IBiowareXP2 _biowareXP2;
        private readonly IColorTokenService _color;
        private readonly IBaseService _base;
        private readonly ISkillService _skill;
        private readonly IRandomService _random;

        public CraftCreateItem(
            INWScript script,
            IDataContext db,
            IErrorService error,
            ICraftService craft,
            IComponentBonusService componentBonus,
            IBiowareXP2 biowareXP2,
            IColorTokenService color,
            IBaseService @base,
            ISkillService skill,
            IRandomService random)
        {
            _ = script;
            _db = db;
            _error = error;
            _craft = craft;
            _componentBonus = componentBonus;
            _biowareXP2 = biowareXP2;
            _color = color;
            _base = @base;
            _skill = skill;
            _random = random;
        }

        public bool Run(params object[] args)
        {
            NWPlayer player = (NWPlayer) args[0];

            try
            {
                RunCreateItem(player);
                player.IsBusy = false;
            }
            catch (Exception ex)
            {
                _error.LogError(ex);

                return false;
            }

            return true;
        }
        
        private void RunCreateItem(NWPlayer player)
        {
            var model = _craft.GetPlayerCraftingData(player);

            CraftBlueprint blueprint = _db.CraftBlueprints.Single(x => x.CraftBlueprintID == model.BlueprintID);
            PCSkill pcSkill = _db.PCSkills.Single(x => x.PlayerID == player.GlobalID && x.SkillID == blueprint.SkillID);

            int pcEffectiveLevel = _craft.CalculatePCEffectiveLevel(player, pcSkill.Rank, (SkillType)blueprint.SkillID);
            int itemLevel = model.AdjustedLevel;
            float chance = CalculateBaseChanceToAddProperty(pcEffectiveLevel, itemLevel);
            float equipmentBonus = CalculateEquipmentBonus(player, (SkillType)blueprint.SkillID);

            if (chance <= 1.0f)
            {
                player.FloatingText(_color.Red("Critical failure! You don't have enough skill to create that item. All components were lost."));
                _craft.ClearPlayerCraftingData(player, true);
                return;
            }

            var craftedItems = new List<NWItem>();
            NWItem craftedItem = (_.CreateItemOnObject(blueprint.ItemResref, player.Object, blueprint.Quantity));
            craftedItem.IsIdentified = true;
            craftedItems.Add(craftedItem);

            // If item isn't stackable, loop through and create as many as necessary.
            if (craftedItem.StackSize < blueprint.Quantity)
            {
                for (int x = 2; x <= blueprint.Quantity; x++)
                {
                    craftedItem = (_.CreateItemOnObject(blueprint.ItemResref, player.Object));
                    craftedItem.IsIdentified = true;
                    craftedItems.Add(craftedItem);
                }
            }

            // Recommended level gets set regardless if all item properties make it on the final product.
            // Also mark who crafted the item. This is later used for display on the item's examination event.
            foreach (var item in craftedItems)
            {
                item.RecommendedLevel = itemLevel;
                item.SetLocalString("CRAFTER_PLAYER_ID", player.GlobalID);

                _base.ApplyCraftedItemLocalVariables(item, blueprint.BaseStructure);
            }

            int successAmount = 0;
            foreach (var component in model.MainComponents)
            {
                var result = RunComponentBonusAttempt(player, component, equipmentBonus, chance, craftedItems);
                successAmount += result.Item1;
                chance = result.Item2;
            }
            foreach (var component in model.SecondaryComponents)
            {
                var result = RunComponentBonusAttempt(player, component, equipmentBonus, chance, craftedItems);
                successAmount += result.Item1;
                chance = result.Item2;
            }
            foreach (var component in model.TertiaryComponents)
            {
                var result = RunComponentBonusAttempt(player, component, equipmentBonus, chance, craftedItems);
                successAmount += result.Item1;
                chance = result.Item2;
            }
            foreach (var component in model.EnhancementComponents)
            {
                var result = RunComponentBonusAttempt(player, component, equipmentBonus, chance, craftedItems);
                successAmount += result.Item1;
                chance = result.Item2;
            }

            // Structures gain increased durability based on the blueprint
            if (blueprint.BaseStructure != null)
            {
                foreach (var item in craftedItems)
                {
                    item.MaxDurability += (float)blueprint.BaseStructure.Durability;
                    item.Durability = item.MaxDurability;
                }
            }


            player.SendMessage("You created " + blueprint.Quantity + "x " + blueprint.ItemName + "!");
            int baseXP = 250 + successAmount * _random.Random(1, 50);
            float xp = _skill.CalculateRegisteredSkillLevelAdjustedXP(baseXP, model.AdjustedLevel, pcSkill.Rank);
            _skill.GiveSkillXP(player, blueprint.SkillID, (int)xp);
            _craft.ClearPlayerCraftingData(player, true);
        }


        private Tuple<int, float> RunComponentBonusAttempt(NWPlayer player, NWItem component, float equipmentBonus, float chance, List<NWItem> itemSet)
        {
            int successAmount = 0;
            foreach (var ip in component.ItemProperties)
            {
                int ipType = _.GetItemPropertyType(ip);
                if (ipType != (int)CustomItemPropertyType.ComponentBonus) continue;

                int bonusTypeID = _.GetItemPropertySubType(ip);
                int tlkID = Convert.ToInt32(_.Get2DAString("iprp_compbon", "Name", bonusTypeID));
                int amount = _.GetItemPropertyCostTableValue(ip);
                string bonusName = _.GetStringByStrRef(tlkID) + " " + amount;
                float random = _random.RandomFloat() * 100.0f;

                if (random + equipmentBonus <= chance)
                {
                    foreach (var item in itemSet)
                    {
                        // If the target item is a component itself, we want to add the component bonuses instead of the
                        // actual item property bonuses.
                        // In other words, we want the custom item property "Component Bonus: AC UP" instead of the "AC Bonus" item property.
                        var componentIP = item.ItemProperties.FirstOrDefault(x => _.GetItemPropertyType(x) == (int)CustomItemPropertyType.ComponentType);
                        if (componentIP == null)
                            _componentBonus.ApplyComponentBonus(item, ip);
                        else
                            _biowareXP2.IPSafeAddItemProperty(item, ip, 0.0f, AddItemPropertyPolicy.IgnoreExisting, false, false);

                    }
                    player.SendMessage(_color.Green("Successfully applied component property: " + bonusName));

                    chance -= _random.Random(1, 5);
                    if (chance < 1) chance = 1;

                    successAmount++;
                }
                else
                {
                    player.SendMessage(_color.Red("Failed to apply component property: " + bonusName));
                }
            }

            return new Tuple<int, float>(successAmount, chance);
        }


        private float CalculateEquipmentBonus(NWPlayer player, SkillType skillType)
        {
            int equipmentBonus = 0;

            switch (skillType)
            {
                case SkillType.Armorsmith: equipmentBonus = player.EffectiveArmorsmithBonus; break;
                case SkillType.Weaponsmith: equipmentBonus = player.EffectiveWeaponsmithBonus; break;
                case SkillType.Cooking: equipmentBonus = player.EffectiveCookingBonus; break;
                case SkillType.Engineering: equipmentBonus = player.EffectiveEngineeringBonus; break;
                case SkillType.Fabrication: equipmentBonus = player.EffectiveFabricationBonus; break;
                case SkillType.Medicine: equipmentBonus = player.EffectiveMedicineBonus; break;
            }

            return equipmentBonus * 0.5f; // +0.5% per equipment bonus
        }

        private float CalculateBaseChanceToAddProperty(int pcLevel, int blueprintLevel)
        {
            int delta = pcLevel - blueprintLevel;
            float percentage = 0.0f;

            if (delta <= -5)
            {
                percentage = 0.0f;
            }
            else if (delta >= 4)
            {
                percentage = 90.0f;
            }
            else
            {
                switch (delta)
                {
                    case -4:
                        percentage = 10.0f;
                        break;
                    case -3:
                        percentage = 15.0f;
                        break;
                    case -2:
                        percentage = 25.0f;
                        break;
                    case -1:
                        percentage = 35.0f;
                        break;
                    case 0:
                        percentage = 50.0f;
                        break;
                    case 1:
                        percentage = 65.0f;
                        break;
                    case 2:
                        percentage = 75.0f;
                        break;
                    case 3:
                        percentage = 85.0f;
                        break;
                }
            }


            return percentage;
        }
    }
}
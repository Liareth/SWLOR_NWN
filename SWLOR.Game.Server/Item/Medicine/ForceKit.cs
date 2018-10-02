﻿using System.Linq;
using NWN;
using SWLOR.Game.Server.Data.Contracts;
using SWLOR.Game.Server.Data.Entities;
using SWLOR.Game.Server.Enumeration;
using SWLOR.Game.Server.GameObject;
using SWLOR.Game.Server.Item.Contracts;
using SWLOR.Game.Server.Service.Contracts;
using SWLOR.Game.Server.ValueObject;
using static NWN.NWScript;

namespace SWLOR.Game.Server.Item.Medicine
{
    public class ForceKit: IActionItem
    {

        private readonly INWScript _;
        private readonly IDataContext _db;
        private readonly ISkillService _skill;
        private readonly IRandomService _random;
        private readonly IPerkService _perk;
        private readonly IPlayerStatService _playerStat;
        private readonly IAbilityService _ability;
        private readonly ICustomEffectService _customEffect;

        public ForceKit(
            INWScript script,
            IDataContext db,
            ISkillService skill,
            IRandomService random,
            IPerkService perk,
            IPlayerStatService playerStat,
            IAbilityService ability,
            ICustomEffectService customEffect)
        {
            _ = script;
            _db = db;
            _skill = skill;
            _random = random;
            _perk = perk;
            _playerStat = playerStat;
            _ability = ability;
            _customEffect = customEffect;
        }

        public CustomData StartUseItem(NWCreature user, NWItem item, NWObject target, Location targetLocation)
        {
            user.SendMessage("You begin applying a force pack to " + target.Name + "...");
            return null;
        }

        public void ApplyEffects(NWCreature user, NWItem item, NWObject target, Location targetLocation, CustomData customData)
        {
            NWPlayer player = (user.Object);
            
            PCSkill skill = _skill.GetPCSkill(player, SkillType.Medicine);
            int luck = _perk.GetPCPerkLevel(player, PerkType.Lucky);
            int perkDurationBonus = _perk.GetPCPerkLevel(player, PerkType.HealingKitExpert) * 6 + (luck * 2);
            float duration = 30.0f + (skill.Rank * 0.4f) + perkDurationBonus;
            int restoreAmount = 1 + item.GetLocalInt("HEALING_BONUS") + _playerStat.EffectiveMedicineBonus(player) + item.MedicineBonus;
            int delta = item.RecommendedLevel - skill.Rank;
            float effectivenessPercent = 1.0f;

            if (delta > 0)
            {
                effectivenessPercent = effectivenessPercent - (delta * 0.1f);
            }

            restoreAmount = (int)(restoreAmount * effectivenessPercent);

            int perkBlastBonus = _perk.GetPCPerkLevel(player, PerkType.ImmediateForcePack);
            if (perkBlastBonus > 0)
            {
                int blastHeal = restoreAmount * perkBlastBonus;
                if (_random.Random(100) + 1 <= luck / 2)
                {
                    blastHeal *= 2;
                }

                _ability.RestoreFP(target.Object, blastHeal);
            }

            float interval = 6.0f;
            BackgroundType background = (BackgroundType) player.Class1;

            if (background == BackgroundType.Medic)
                interval *= 0.5f;

            string data = (int)interval + ", " + restoreAmount;
            _customEffect.ApplyCustomEffect(user, target.Object, CustomEffectType.ForcePack, (int)duration, restoreAmount, data);

            player.SendMessage("You successfully apply a force pack to " + target.Name + ".");

            int xp = (int)_skill.CalculateRegisteredSkillLevelAdjustedXP(300, item.RecommendedLevel, skill.Rank);
            _skill.GiveSkillXP(player, SkillType.Medicine, xp);
        }

        public float Seconds(NWCreature user, NWItem item, NWObject target, Location targetLocation, CustomData customData)
        {
            if ( _random.Random(100) + 1 <= _perk.GetPCPerkLevel((NWPlayer)user, PerkType.SpeedyFirstAid) * 10)
            {
                return 0.1f;
            }

            PCSkill skill = _skill.GetPCSkill(user.Object, SkillType.Medicine);
            return 12.0f - (skill.Rank * 0.1f);
        }

        public bool FaceTarget()
        {
            return true;
        }

        public int AnimationID()
        {
            return ANIMATION_LOOPING_GET_MID;
        }

        public float MaxDistance(NWCreature user, NWItem item, NWObject target, Location targetLocation)
        {
            return 3.5f + _perk.GetPCPerkLevel(user.Object, PerkType.RangedHealing);
        }

        public bool ReducesItemCharge(NWCreature user, NWItem item, NWObject target, Location targetLocation, CustomData customData)
        {
            int consumeChance = _perk.GetPCPerkLevel((NWPlayer)user, PerkType.FrugalMedic) * 10;
            BackgroundType background = (BackgroundType) user.Class1;

            if (background == BackgroundType.Medic)
            {
                consumeChance += 5;
            }


            return _random.Random(100) + 1 > consumeChance;
        }

        public string IsValidTarget(NWCreature user, NWItem item, NWObject target, Location targetLocation)
        {
            if (!target.IsPlayer)
            {
                return "Only players may be targeted with this item.";
            }

            var dbTarget = _db.PlayerCharacters.Single(x => x.PlayerID == target.GlobalID);
            if (dbTarget.CurrentFP >= dbTarget.MaxFP)
            {
                return "Your target's FP is at their maximum.";
            }

            return null;
        }

        public bool AllowLocationTarget()
        {
            return false;
        }
    }
}
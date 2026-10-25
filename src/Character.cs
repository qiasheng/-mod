using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Characters;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;

namespace Zhiji
{
    public sealed class ZhijiCardPool : CardPoolModel
    {
        public override string Title => "zhiji";
        public override string EnergyColorName => "ironclad";
        public override string CardFrameMaterialPath => "card_frame_red";
        public override Color DeckEntryCardColor => new Color("B01818");
        public override Color EnergyOutlineColor => new Color("7A1010");
        public override bool IsColorless => false;

        protected override System.Collections.Generic.IEnumerable<CardModel> FilterThroughEpochs(
            MegaCrit.Sts2.Core.Unlocks.UnlockState unlockState, System.Collections.Generic.IEnumerable<CardModel> cards)
        {
            // 红神卡（贪食/存在的缺失/收获）只通过变身获得，不进卡池奖励
            var list = new System.Collections.Generic.List<CardModel>();
            foreach (var c in cards)
            {
                var z = c as ZhijiCard;
                if (z != null && z.RedGod) continue;          // 红神卡只通过变身获得
                if (c is ChildhoodEnd) continue;              // 饥饿满100自动发放
                if (c is HungerFeast) continue;               // 作弊卡只在初始牌组，不进奖励
                list.Add(c);
            }
            // 保证 普通/罕见/稀有 三档都至少有一张，否则奖励生成会抛 "couldn't generate a valid rarity"
            try
            {
                foreach (var rar in new[] { MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common, MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon, MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare })
                {
                    bool has = false;
                    foreach (var c in list) if (c != null && c.Rarity == rar) { has = true; break; }
                    if (has) continue;
                    foreach (var c in cards)
                    {
                        if (c == null || c.Rarity != rar || list.Contains(c)) continue;
                        var z2 = c as ZhijiCard;
                        if (z2 != null && z2.RedGod) continue;
                        list.Add(c); has = true; break;
                    }
                    if (!has)
                    {
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        {
                            if (c == null || c.Rarity != rar) continue;
                            var zz = c as ZhijiCard;
                            if (zz != null && zz.RedGod) continue;      // 红神卡绝不进奖励
                            if (c is ChildhoodEnd || c is HungerFeast) continue;
                            list.Add(c); has = true; break;
                        }
                    }
                    ZLog.W("ensure rarity " + rar + " -> " + has);
                }
            }
            catch (Exception e) { ZLog.W("ensure rarity fail " + e.Message); }
            return list;
        }

        // 取模型容错：取不到就跳过（避免开局 InvalidCastException）
        static CardModel C<T>() where T : CardModel
        {
            try { return ModelDb.Card<T>(); } catch (Exception e) { ZLog.W("pool card skip " + typeof(T).Name + ": " + e.Message); return null; }
        }

        protected override CardModel[] GenerateAllCards()
        {
            return new CardModel[]
            {
                C<Drive>(),
                C<Escape>(),
                C<PulseAid>(),
                C<ChildhoodEnd>(),
                C<Prepare>(),
                C<Suture>(),
                C<Siphon>(),
                C<Gluttony>(),
                C<TheMissing>(),
                C<Harvest>(),
                C<FleshRend>(),
                C<FleshWall>(),
                C<BalanceBlade>(),
                C<HungerFeast>(),
                C<BloodBank>(),
                C<GluttonRite>(),
                C<ReapRite>(),
                C<ThornBloom>(),
                C<Nourish>(),
                C<SanguineDebt>(),
                C<FeastFork>(),
                C<Forage>(),
                C<Brace>(),
                C<StrengthOffering>(),
                C<GreedySearch>(),
                C<SlitThroat>(),
                C<BloodLet>(),
                C<BloodDebt>(),
                C<ThousandBlades>(),
                C<Detonate>(),
                C<BloodFeast>(),
                C<BloodMist>(),
                C<Deepen>(),
                C<Snarl>(),
                C<Tear>(),
                C<EchoOfBlood>(),
                C<Chew>(),
                C<TangledTongue>(),
                C<Cleanup>(),
                C<BloodPact>(),
                C<BloodSurge>(),
                C<BloodSpike>(),
                C<Coagulate>(),
                C<BloodRain>(),
                C<BloodRepay>(),
                C<BleedInstinct>(),
                C<HungerRhythm>(),
                C<VileResonance>(),
                C<HungerPact>(),
                C<GluttonInstinct>(),
                C<BloodEcho>(),
                C<BloodVessel>(),
                C<ReapScythe>(),
                C<VileLoop>(),
                C<FeastCall>(),
                C<FeedGuest>(),
                C<DineTogether>(),
                C<SacrificeGuest>(),
                C<MultiplyGuest>(),
                C<ProtectGuest>(),
                C<DevourGuest>(),
                C<LullabyGuest>(),
                C<BloodTide>(),
                C<DodgeInstinct>(),
                C<DodgeStep>(),
                C<FleshPounce>(),
                C<RelentlessBite>(),
                C<BloodClaw>(),
                C<CommandGuest>(),
                C<Dismember>(),
                C<BloodResonance>(),
                C<GiantGrowth>(),
                C<LeftoverUse>(),
                C<TestOstySummon>(),
                C<UnboundBite>(),
                C<UnboundGluttony>(),
                C<UnboundFeast>(),
                C<UnboundRoar>(),
                C<VileBrand>(),
                C<VileWhisper>(),
                C<VileOffering>(),
                C<VileEnd>(),
                C<VileReckoning>(),
                C<HungerEcho>(),
                C<FeastEve>()
            }
            .Where(x => x != null)
            .Concat(SharedCards())
            .ToArray();
        }

        // 只并入少量通用牌，避免原版大池子稀释专属卡（构筑空间=专属卡为主）
        static IEnumerable<CardModel> SharedCards()
        {
            var outList = new List<CardModel>();
            void Add<T>() where T : CardModel
            {
                try { var c = ModelDb.Card<T>(); if (c != null && !outList.Contains(c)) outList.Add(c); }
                catch (Exception e) { ZLog.W("shared skip " + typeof(T).Name + ": " + e.Message); }
            }
            Add<MegaCrit.Sts2.Core.Models.Cards.PommelStrike>();
            Add<MegaCrit.Sts2.Core.Models.Cards.ShrugItOff>();
            Add<MegaCrit.Sts2.Core.Models.Cards.IronWave>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Armaments>();
            Add<MegaCrit.Sts2.Core.Models.Cards.BattleTrance>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Uppercut>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Bash>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Headbutt>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Whirlwind>();
            Add<MegaCrit.Sts2.Core.Models.Cards.TrueGrit>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Shockwave>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Rampage>();
            Add<MegaCrit.Sts2.Core.Models.Cards.SecondWind>();
            Add<MegaCrit.Sts2.Core.Models.Cards.FlameBarrier>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Bloodletting>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Inflame>();
            Add<MegaCrit.Sts2.Core.Models.Cards.TwinStrike>();
            Add<MegaCrit.Sts2.Core.Models.Cards.BodySlam>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Havoc>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Entrench>();
            Add<MegaCrit.Sts2.Core.Models.Cards.Rupture>();
            Add<MegaCrit.Sts2.Core.Models.Cards.BurningPact>();
            ZLog.W("shared currated cards " + outList.Count);
            return outList;
        }
    }

    public sealed class ZhijiRelicPool : RelicPoolModel
    {
        public override string EnergyColorName => "ironclad";
        public override Color LabOutlineColor => new Color("A01818");

        protected override IEnumerable<RelicModel> GenerateAllRelics()
        {
            return new RelicModel[]
            {
                ModelDb.Relic<FleshStilts>(),
                ModelDb.Relic<CrimsonStage>(),
                ModelDb.Relic<BloodGoblet>(),
                ModelDb.Relic<CarvingKnife>(),
                ModelDb.Relic<EndlessPlate>(),
                ModelDb.Relic<GoldenShroud>(),   // 也可作为稀有遗物随机获得（多渠道）
                ModelDb.Relic<BleedBoard>(),
                ModelDb.Relic<CrimsonCradle>(),
                ModelDb.Relic<BloodNail>(),
                ModelDb.Relic<CrimsonVisor>(),
                ModelDb.Relic<BloodHourglass>(),
                ModelDb.Relic<WarSigil>(),
                ModelDb.Relic<GuardSigil>(),
                ModelDb.Relic<RationBag>(),
                ModelDb.Relic<OldScythe>(),
                ModelDb.Relic<Stigmata>(),
                ModelDb.Relic<BloodFunnel>(),
                ModelDb.Relic<BlackNapkin>(),
                ModelDb.Relic<VileCrown>(),
                ModelDb.Relic<VileWatch>(),
                ModelDb.Relic<VilePurse>(),
                ModelDb.Relic<HungerBell>()
                // ModelDb.Relic<DinnerBell>()   // 召唤流暂停：不进遗物池
            };
        }
    }

    public sealed class Zhiji : CharacterModel
    {
        public override Color NameColor => new Color("B01818");
        public override CharacterGender Gender => CharacterGender.Feminine;
        protected override CharacterModel UnlocksAfterRunAs => null;
        public override int StartingHp => 132;   // 最大生命 132（开局满血）
        public override int StartingGold => 99;
        public override int MaxEnergy => 3;

        public override CardPoolModel CardPool => ModelDb.CardPool<ZhijiCardPool>();
        public override PotionPoolModel PotionPool => ModelDb.PotionPool<IroncladPotionPool>();
        public override RelicPoolModel RelicPool => ModelDb.RelicPool<ZhijiRelicPool>();

        public override IEnumerable<CardModel> StartingDeck => new CardModel[]
        {
            // 常规起点：3 打击 + 3 防御 + 2 张专属（构筑空间留给奖励/商店/事件）
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(),
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(),
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(),
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(),
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(),
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(),
            ModelDb.Card<Drive>(),
            ModelDb.Card<PulseAid>()
        };

        public override IReadOnlyList<RelicModel> StartingRelics => new[] { ModelDb.Relic<FleshStilts>() };

        public override string CharacterSelectSfx => "event:/sfx/characters/ironclad/ironclad_select";
        public override string CharacterTransitionSfx => "event:/sfx/ui/wipe_ironclad";

        public override float AttackAnimDelay => 0.15f;
        public override float CastAnimDelay => 0.25f;
        public override List<string> GetArchitectAttackVfx() => new List<string> { "vfx/vfx_attack_slash" };
    }
}

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace Zhiji
{
    public abstract class ZhijiCard : CardModel
    {
        protected ZhijiCard(int cost, CardType type, CardRarity rarity, TargetType target, bool showInLibrary = true)
            : base(cost, type, rarity, target, showInLibrary) { }

        public virtual bool RedGod => false;
        public virtual bool DotStyle => false;   // DOT 流卡牌（饭馆换牌后保留）

        // 升级状态：优先读引擎的 Upgraded 属性，取不到再用本地标记
        bool _up;
        protected bool Up
        {
            get
            {
                try
                {
                    var pi = typeof(CardModel).GetProperty("Upgraded",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (pi != null && pi.PropertyType == typeof(bool)) return (bool)pi.GetValue(this);
                }
                catch { }
                return _up;
            }
        }
        static bool _tipLogged;
        protected override System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip> ExtraHoverTips
        {
            get
            {
                var list = new System.Collections.Generic.List<MegaCrit.Sts2.Core.HoverTips.IHoverTip>();
                try { var b = base.ExtraHoverTips; if (b != null) foreach (var x in b) list.Add(x); } catch { }
                try
                {
                    string note;
                    var key = Id.Entry.ToUpperInvariant() + ".DESCRIPTION";
                    if (Loc.MechanicNotes.TryGetValue(key, out note) && !string.IsNullOrEmpty(note))
                    {
                        var title = new MegaCrit.Sts2.Core.Localization.LocString("cards", "mechanic_note.title");
                        var tip = new MegaCrit.Sts2.Core.HoverTips.HoverTip(title, note, Art.Get("icon_buff_bleed"));
                        list.Add(tip);
                        if (!_tipLogged) { _tipLogged = true; ZLog.W("hover tip built for " + Id.Entry + " total=" + list.Count); }
                    }
                }
                catch { }
                return list;
            }
        }

        protected override void OnUpgrade()
        {
            _up = true;
            bool bumped = false;
            try
            {
                foreach (var v in DynamicVars.Values)
                {
                    if (v is DamageVar) { v.UpgradeValueBy(BonusFor(v.BaseValue, 0.35m)); bumped = true; }
                    else if (v is BlockVar) { v.UpgradeValueBy(BonusFor(v.BaseValue, 0.30m)); bumped = true; }
                    else
                    {
                        try
                        {
                            var mUp = v.GetType().GetMethod("UpgradeValueBy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mUp != null) { mUp.Invoke(v, new object[] { BonusFor(v.BaseValue, 0.25m) }); bumped = true; }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e) { ZLog.W("upgrade bump fail " + e.Message); }
            // 没有任何数值可加：降 1 费（最低 0）
            // 旧实现靠反射找 BaseValue/SetBaseValue，两个都不存在时静默失败（done=False，升级等于没升）。
            // CardEnergyCost 提供了正规入口：UpgradeBy(减量) + FinalizeUpgrade()。
            if (!bumped)
            {
                var ec = EnergyCost;
                bool done = false;
                if (ec != null)
                {
                    try
                    {
                        int before = ec.GetResolved();
                        if (before > 0)
                        {
                            ec.UpgradeBy(-1);
                            try { ec.FinalizeUpgrade(); } catch { }
                            done = ec.GetResolved() < before;
                        }
                        else { done = true; }   // 0 费卡：无需降费
                    }
                    catch (Exception e3) { ZLog.W("upgrade cost api fail " + Id.Entry + " " + e3.Message); }
                }
                if (!done)
                {
                    // 兜底：老的反射路径（某些自定义费用模型可能只暴露 BaseValue）
                    try
                    {
                        var tEc = ec != null ? ec.GetType() : null;
                        var piBase = tEc != null ? tEc.GetProperty("BaseValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                        if (piBase != null && piBase.CanWrite)
                        {
                            int cur = Convert.ToInt32(piBase.GetValue(ec));
                            piBase.SetValue(ec, Math.Max(0, cur - 1));
                            done = true;
                        }
                    }
                    catch { }
                }
                ZLog.W("upgrade fallback cost reduce done=" + done + " card=" + Id.Entry + " cost=" + (ec != null ? ec.GetResolved().ToString() : "-"));
            }
            try { OnCustomUpgrade(); } catch (Exception e2) { ZLog.W("custom upgrade fail " + e2.Message); }
        }

        // 子类可在这里调整自定义数值（流血层数、上限等），保证描述里的数字也跟着变
        protected virtual void OnCustomUpgrade() { }

        static decimal BonusFor(decimal baseValue, decimal ratio)
        {
            var raw = System.Math.Round(baseValue * ratio, System.MidpointRounding.AwayFromZero);
            if (raw < 1m) raw = 1m;
            return raw;
        }

        /// 读动态变量（升级后会变），取不到用兜底值
        protected int DV(string name, int fallback)
        {
            try
            {
                var v = DynamicVars[name];
                if (v != null) return (int)v.BaseValue;
            }
            catch { }
            return fallback;
        }

        protected ZhijiState St => ZhijiStore.Of(Owner);

        /// 形态计数：有目标=进攻，无目标（空放/自身）=防守
        // 流血层数上限统一入口：无遗物 10 / 黄金寿衣 70 / 血肉砧板 90
        // 流血不再限制层数（用户建议：限制回复而不是限制流血）
        protected static int BleedCapValue(bool shroud, bool board) => 999;

        protected void CountUse(CardPlay play)
        {
            try
            {
                if (play == null) return;
                if (RedGod) return;                       // 红神牌不计形态计数
                if (play.Target == null) St.DefCount++; else St.AtkCount++;
            }
            catch { }
        }

        // 选择敌人：单敌直接返回，多敌弹列表
        protected async Task<Creature> PickEnemy()
        {
            try
            {
                var cs = Owner.Creature != null ? Owner.Creature.CombatState : null;
                var alive = new List<Creature>();
                if (cs != null && cs.Enemies != null)
                    foreach (var e in cs.Enemies) if (e != null && !e.IsDead) alive.Add(e);
                if (alive.Count == 0) return null;
                if (alive.Count == 1) return alive[0];
                var labels = new string[alive.Count];
                for (int i = 0; i < alive.Count; i++)
                    labels[i] = alive[i].Name + "  (" + alive[i].CurrentHp + "/" + alive[i].MaxHp + ")";
                int idx = await Visuals.ChooseMany("选择目标", labels);
                if (idx < 0 || idx >= alive.Count) idx = 0;
                return alive[idx];
            }
            catch (Exception e) { ZLog.W("pick enemy fail " + e.Message); return null; }
        }

        protected float RollDice(out int value)
        {
            string label;
            return Dice.Roll(St.Pulse, out value, out label);
        }

        protected async Task Hit(PlayerChoiceContext ctx, CardPlay play, Creature target, int dmg, string fx)
        {
            await DamageCmd.Attack(dmg).FromCard(this, play).Targeting(target).WithHitFx(fx).Execute(ctx);
            // 血潮（能力）：每次攻击附加等同伤害的流血
            try
            {
                int tide = Owner != null && Owner.Creature != null ? Owner.Creature.GetPowerAmount<BloodTidePower>() : 0;
                if (tide > 0 && target != null && !target.IsDead)
                {
                    await ApplyBleed(ctx, target, dmg * tide);
                    ZLog.W("blood tide +" + (dmg * tide) + " bleed");
                }
            }
            catch { }
            // 失控形态：击杀敌人回复 8 点生命（血线蹦迪）
            try
            {
                if (target != null && target.IsDead && Owner != null && Owner.Relics != null)
                {
                    bool unbound = false;
                    foreach (var r in Owner.Relics) if (r is UnboundHeart) { unbound = true; break; }
                    if (unbound)
                    {
                        await CreatureCmd.Heal(Owner.Creature, 8, true);
                        ZLog.W("unbound kill heal +8");
                    }
                }
            }
            catch { }
        }

        protected void GainReap(int n)
        {
            if (!RedGod) return;
            int before = St.Reap;
            St.Reap = System.Math.Min(100, St.Reap + n);
            ZLog.W("reap=" + St.Reap);
            if (before < 100 && St.Reap >= 100)
            {
                try
                {
                    var hp = CardPile.Get(PileType.Hand, Owner);
                    if (hp != null)
                        foreach (var c in hp.Cards)
                            if (c != null && c.Id.Entry == "HARVEST") { ZLog.W("harvest already in hand"); Sync(null); return; }
                }
                catch { }
                // 能量满了：把大招「收获」加入手牌
                try
                {
                    var state = Owner.Creature != null ? Owner.Creature.CombatState as MegaCrit.Sts2.Core.Combat.CombatState : null;
                    if (state != null)
                    {
                        var inst = state.CreateCard(ModelDb.Card<Harvest>(), Owner);
                        _ = CardPileCmd.Add(inst, PileType.Hand, CardPilePosition.Top, null, false);
                        ZLog.W("reap full -> harvest added to hand");
                    }
                }
                catch (Exception e) { ZLog.W("grant harvest fail " + e.Message); }
            }
            Sync(null);
        }

        // 每 1 点自助餐 = 2 伤害，血色舞台 +1，迫切饥饿每 2 层 +1；支持透支
        protected int FeastBonus()
        {
            if (!RedGod) return 0;
            int per = 1;                       // 基础倍率大砍：3 -> 1
            if (Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is CrimsonStage)) per += 1;
            per += St.Craving / 4;             // 平衡：迫切饥饿每 4 层 +1
            int spend = System.Math.Min(St.Feast, 8);   // 平衡：单次最多烧 8 自助餐
            if (St.Feast > 8) St.Feast -= 8;
            else St.Feast = 0;
            if (spend <= 0)
            {
                spend = 3;                  // 透支 3 点
                St.Debt += 3;
                ZLog.W("feast overdraft 5 -> debt=" + St.Debt);
            }
            int bonus = spend * per;
            ZLog.W("feast bonus=" + bonus + " (spend=" + spend + " per=" + per + " craving=" + St.Craving + ")");
            return bonus;
        }

        // 伤害修正：迫切饥饿 + 血肉溃烂（每15层流血 +25%）
        protected int ScaleDamage(int dmg, Creature target)
        {
            int d = dmg;
            if (!RedGod) return d;
            if (target != null)
            {
                int bleed = target.GetPowerAmount<BleedPower>();
                int stacks = bleed / 15;
                if (stacks > 0) d = (int)(d * (1f + 0.25f * stacks));
            }
            return d;
        }

        // 施加流血：红神 +50%；黄金寿衣（DOT 流）再 +50% 且上限提高到 99；否则上限 10
        protected async Task ApplyBleed(PlayerChoiceContext ctx, Creature target, int amount)
        {
            int amt = amount;
            if (RedGod) amt = (int)(amt * 1.5f);
            bool shroud = St.GoldShroud;
            bool board = false;
            try
            {
                if (Owner != null && Owner.Relics != null)
                {
                    if (!shroud) shroud = Owner.Relics.Any(r => r is GoldenShroud);
                    board = Owner.Relics.Any(r => r is BleedBoard);
                }
            }
            catch { }
            if (shroud) amt = (int)System.Math.Ceiling(amt * 1.5f);
            int thirst = 0;
            try { thirst = Owner != null ? Owner.Creature.GetPowerAmount<BloodThirstPower>() : 0; } catch { }
            if (thirst > 0) amt = (int)System.Math.Ceiling(amt * (1f + 0.5f * thirst));
            if (board) amt += 2;
            int cur = 0;
            try { cur = target.GetPowerAmount<BleedPower>(); } catch { }
            // 血之回响（能力牌）：每回合首次施加流血抽 1
            try
            {
                if (Owner != null && Owner.Creature != null && Owner.Creature.GetPowerAmount<BloodEchoPower>() > 0)
                {
                    int turnE = -1;
                    try { turnE = Owner.PlayerCombatState != null ? Owner.PlayerCombatState.TurnNumber : -1; } catch { }
                    var stE = ZhijiStore.Of(Owner);
                    if (stE.BleedEchoTurn != turnE)
                    {
                        stE.BleedEchoTurn = turnE;
                        _ = CardPileCmd.Draw(ctx, 1m, Owner);
                        ZLog.W("blood echo draw 1 (turn " + turnE + ")");
                    }
                }
            }
            catch { }
            // 血之本能（能力牌）：按层数提高施加量
            try
            {
                int inst = Owner != null && Owner.Creature != null ? Owner.Creature.GetPowerAmount<BleedInstinctPower>() : 0;
                if (inst > 0) amt += inst;
            }
            catch { }
            // 血之漏斗：每回合第一张施加流血的牌额外 +1
            try
            {
                if (Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is BloodFunnel))
                {
                    int turn = -1;
                    try { turn = Owner.PlayerCombatState != null ? Owner.PlayerCombatState.TurnNumber : -1; } catch { }
                    var stF = ZhijiStore.Of(Owner);
                    if (stF.FunnelTurn != turn) { stF.FunnelTurn = turn; amt += 1; ZLog.W("blood funnel +1 (turn " + turn + ")"); }
                }
            }
            catch { }
            // 血契之钉：每场战斗第一次施加流血额外 +2
            try
            {
                if (Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is BloodNail) && !ZhijiStore.Once(Owner, "blood_nail"))
                {
                    amt += 2;
                    ZLog.W("blood nail +2 first bleed");
                }
            }
            catch { }
            int cap = BleedCapValue(shroud, board);
            try
            {
                int capPow = Owner != null && Owner.Creature != null ? Owner.Creature.GetPowerAmount<BleedCapPower>() : 0;
                if (capPow > 0) cap += capPow;
            }
            catch { }
            if (cur > cap)
            {
                // 外部来源（调试/其它效果）超过上限时，先削回上限
                try { _ = PowerCmd.Apply<BleedPower>(ctx, target, -(cur - cap), Owner.Creature, this, true); } catch { }
                cur = cap;
                ZLog.W("bleed trimmed to " + cap);
            }
            int add = System.Math.Min(amt, System.Math.Max(0, cap - cur));
            if (add <= 0) { ZLog.W("bleed capped at " + cur); return; }
            await PowerCmd.Apply<BleedPower>(ctx, target, add, Owner.Creature, this, false);
            try { ZLog.W("bleed applied +" + add + " target=" + (target != null ? target.Name : "null") + " now=" + (target != null ? target.GetPowerAmount<BleedPower>() : -1)); } catch { }
        }

        // 用原版召唤系统召唤 Osty（抄骨妹），返回召唤后的血量
        protected async Task<int> SummonOsty(PlayerChoiceContext ctx, int hp)
        {
            try
            {
                var res = await MegaCrit.Sts2.Core.Commands.OstyCmd.Summon(ctx, Owner, hp, this);
                int cur = Owner.Osty != null ? (int)Owner.Osty.CurrentHp : -1;
                ZLog.W("osty summon hp=" + hp + " -> " + cur);
                try
                {
                    Owner.Creature.LoseHpInternal(2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZLog.W("tear flesh -2 (osty)");
                }
                catch { }
                return cur;
            }
            catch (Exception e) { ZLog.W("osty summon fail " + e.GetBaseException().Message); }
            return -1;
        }

        protected bool OstyAlive => Owner != null && Owner.Osty != null && !Owner.Osty.IsDead && Owner.Osty.CurrentHp > 0;

        // 召唤食客：返回槽位（1/2），满则 0
        protected int SummonMinion(int hp)
        {
            try
            {
                if (hp <= 0) hp = 1;
                int slot = 0;
                if (St.MinionHp <= 0) { St.MinionHp = hp; St.MinionMax = hp; slot = 1; }
                else if (St.MinionHp2 <= 0) { St.MinionHp2 = hp; St.MinionMax2 = hp; slot = 2; }
                if (slot > 0 && Owner != null && Owner.Creature != null)
                {
                    // 撕下一块自己的血肉：召唤代价 2 点生命（不吃替身）
                    ZhijiPatcher.BypassStand = true;
                    Owner.Creature.LoseHpInternal(2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZhijiPatcher.BypassStand = false;
                    ZLog.W("tear flesh -2 (slot " + slot + ")");
                }
                return slot;
            }
            catch { }
            return 0;
        }

        protected int MinionCount()
        {
            int n = 0;
            if (St.MinionHp > 0) n++;
            if (St.MinionHp2 > 0) n++;
            return n;
        }

        // 卑贱之物叠层：受「卑贱怀表」影响，每回合第一张额外 +1
        protected async Task ApplyVile(PlayerChoiceContext ctx, int amt)
        {
            try
            {
                if (Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is VileWatch))
                {
                    int turn = -1;
                    try { turn = Owner.PlayerCombatState != null ? Owner.PlayerCombatState.TurnNumber : -1; } catch { }
                    var stV = ZhijiStore.Of(Owner);
                    if (stV.VileWatchTurn != turn) { stV.VileWatchTurn = turn; amt += 1; ZLog.W("vile watch +1 (turn " + turn + ")"); }
                }
            }
            catch { }
            // 卑贱共鸣（能力牌）：每张卑贱卡额外叠层
            try
            {
                int res = Owner != null && Owner.Creature != null ? Owner.Creature.GetPowerAmount<VileResonancePower>() : 0;
                if (res > 0) amt += res;
            }
            catch { }
            // 卑贱王冠兜底：本场第一次叠层 +2（不依赖回合开始钩子）
            try
            {
                if (Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is VileCrown))
                {
                    var stC = ZhijiStore.Of(Owner);
                    if (!stC.VileCrownApplied) { stC.VileCrownApplied = true; amt += 2; ZLog.W("vile crown +2 (first stack)"); }
                }
            }
            catch { }
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(ctx, Owner.Creature, amt, Owner.Creature, this, false);
        }

        // 击杀结算：红神形态保留 5 点自助餐
        protected void OnKillBonus(Creature target)
        {
            if (target == null || target.CurrentHp > 0) return;
            if (RedGod) { St.Feast += 2; ZLog.W("kill bonus +2 feast"); }
        }

        protected void Sync(PlayerChoiceContext ctx) { ZhijiPowers.Sync(Owner, ctx); }
    }

    public sealed class Drive : ZhijiCard
    {
        public Drive() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(8m, ValueProp.Move),
            new BlockVar(6m, ValueProp.Move),
            new DynamicVar("Pulse", 5m),
            new DynamicVar("PulseSupport", 6m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(2m);
            if (DynamicVars.ContainsKey("PulseSupport")) DynamicVars["PulseSupport"].UpgradeValueBy(2m);
        }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int baseDmg = DV("Damage", 8);
            int blockAmt = DV("Block", 6);
            if (play.Target == null) { await CreatureCmd.GainBlock(Owner.Creature, blockAmt, ValueProp.Move, play, false); St.AddPulse(DV("PulseSupport", Up ? 8 : 6)); ZLog.W("drive -> support up=" + Up); }
            else
            {
                int v; float m = RollDice(out v);
                await Hit(ctx, play, play.Target, ScaleDamage((int)(baseDmg * m), play.Target), "vfx/vfx_attack_slash");
                OnKillBonus(play.Target);
                if (play.Target != null && play.Target.IsDead)
                {
                    try { await CreatureCmd.Heal(Owner.Creature, Up ? 3m : 2m, true); } catch { }
                }
                St.AddPulse(DV("Pulse", Up ? 7 : 5) + (m >= 1f ? 2 : 0));   // 驱力 = 搏动主源
                ZLog.W("drive -> attack");
            }
            Sync(ctx);
        }
    }

    /// 驱力·援护（与「驱力」成对，用掉任意一张就销毁另一张）
    public sealed class DriveGuard : ZhijiCard
    {
        public DriveGuard() : base(1, CardType.Skill, CardRarity.Common, TargetType.TargetedNoCreature) { }
        public bool TwinOnly => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new BlockVar(5m, ValueProp.Move), new DamageVar(8m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            var orig = Twin.FindInHand(Owner, typeof(Drive));
            if (orig != null) await CardCmd.Discard(ctx, orig);
            int dmg = DV("Damage", 8);
            int blk = DV("Block", 5);
            if (play.Target != null && !play.Target.IsPlayer && play.Target != Owner.Creature)
            {
                int v; float m = RollDice(out v);
                await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
                OnKillBonus(play.Target);
                ZLog.W("drive guard -> attack");
            }
            else
            {
                await CreatureCmd.GainBlock(Owner.Creature, blk, ValueProp.Move, play, false);
                ZLog.W("drive guard -> support");
            }
            St.AddPulse(6);
            await CardCmd.Exhaust(ctx, this, false, false);
            Sync(ctx);
        }
    }

    /// 搏动打击（与「搏动援护」成对）：对敌人造成伤害并积攒搏动
    public sealed class PulseStrike : ZhijiCard
    {
        public PulseStrike() : base(2, CardType.Attack, CardRarity.Rare, TargetType.TargetedNoCreature) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(12m, ValueProp.Move), new BlockVar(7m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int dmg = DV("Damage", 12);
            int blk = DV("Block", 7);
            if (play.Target == null) { await CreatureCmd.GainBlock(Owner.Creature, blk, ValueProp.Move, play, false); St.AddPulse(Up ? 8 : 6); ZLog.W("pulse strike -> support"); }
            else
            {
                int v; float m = RollDice(out v);
                int extra = 0;
                if (St.Pulse >= 3) { int use = Math.Min(6, St.Pulse); St.AddPulse(-use); extra = use * (Up ? 3 : 2); }
                await Hit(ctx, play, play.Target, ScaleDamage((int)((dmg + extra) * m), play.Target), "vfx/vfx_attack_blunt");
                OnKillBonus(play.Target);
                ZLog.W("pulse strike -> attack extra=" + extra + " dmg=" + dmg);
            }
            Sync(ctx);
        }
    }

    /// 逃离·掩护（与「逃离」成对）：护住自己并积攒搏动
    public sealed class EscapeCover : ZhijiCard
    {
        public EscapeCover() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.TargetedNoCreature) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new BlockVar(3m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 3), ValueProp.Move, play, false);
            St.AddPulse(4);
            Sync(ctx);
        }
    }

    public sealed class Escape : ZhijiCard
    {
        public override bool DotStyle => true;
        public Escape() : base(0, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(4m, ValueProp.Move),
            new BlockVar(3m, ValueProp.Move),
            new DynamicVar("Bleed", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(2m);
        }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int dmg = DV("Damage", 4);
            int blk = DV("Block", 3);
            if (play.Target == null) { await CreatureCmd.GainBlock(Owner.Creature, blk, ValueProp.Move, play, false); St.AddPulse(Up ? 4 : 3); ZLog.W("escape -> support"); }
            else
            {
                int v; float m = RollDice(out v);
                await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
                OnKillBonus(play.Target);
                St.AddPulse(4);
                if (m >= 1f)
                {
                    await ApplyBleed(ctx, play.Target, DV("Bleed", Up ? 5 : 3));
                    await PowerCmd.Apply<VulnerablePower>(ctx, play.Target, 1m, Owner.Creature, this, false);
                }
                ZLog.W("escape -> attack");
            }
            Sync(ctx);
        }
    }

    public sealed class PulseAid : ZhijiCard
    {
        public PulseAid() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(12m, ValueProp.Move), new BlockVar(7m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int b = Up ? 10 : 7;
            if (play.Target == null) { await CreatureCmd.GainBlock(Owner.Creature, b, ValueProp.Move, play, false); St.AddPulse(Up ? 8 : 6); ZLog.W("pulse aid -> support up=" + Up); }
            else
            {
                int v; float m = RollDice(out v);
                int extra = 0;
                if (St.Pulse >= 3) { int use = Math.Min(6, St.Pulse); St.AddPulse(-use); extra = use * (Up ? 3 : 2); }   // 每点搏动 +2/3 伤害
                await Hit(ctx, play, play.Target, ScaleDamage((int)((b + extra) * m), play.Target), "vfx/vfx_attack_blunt");
                OnKillBonus(play.Target);
                ZLog.W("pulse aid -> attack extra=" + extra + " pulse=" + St.Pulse);
            }
            Sync(ctx);
        }
    }

    public sealed class Prepare : ZhijiCard
    {
        public Prepare() : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            St.AddPulse(Up ? 4 : 3);
            await CardPileCmd.Draw(ctx, Up ? 3m : 2m, Owner);
            Sync(ctx);
        }
    }

    public sealed class ChildhoodEnd : ZhijiCard
    {
        public ChildhoodEnd() : base(0, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        // 保留：回合结束不会进弃牌堆（唯一作用是变身，进弃牌堆就会变成"摸到也用不了"的死牌）
        public override IEnumerable<CardKeyword> CanonicalKeywords => new[] { CardKeyword.Retain };
        protected override bool IsPlayable
        {
            get
            {
                // 只要求饥饿攒满：节奏由积累速度决定，不做回合硬限制
                return St.Hunger >= 100;
            }
        }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            if (St.Hunger < 100) { ZLog.W("childhood end: hunger=" + St.Hunger + " <100, refused"); return; }
            St.Evolve(Owner);

            var state = (CombatState)Owner.Creature.CombatState;
            var hand = CardPile.Get(PileType.Hand, Owner).Cards.ToList();
            // 不清空手牌：直接原地转换成红神牌（记录原件，回落时再还原）
            // 形态判定必须先做：能量上限取决于形态
            St.RForm = St.AtkCount > St.DefCount ? RedForm.War
                     : (St.DefCount > St.AtkCount ? RedForm.Guard : RedForm.Balance);
            ZhijiPatcher.ForceRedHand(Owner, 5);   // 变身演出后（0.25s）换成红神牌，避开出牌结算
            Owner.PlayerCombatState.ResetEnergy();   // 变身瞬间能量补满（按新形态的上限）
            try
            {
                int cap = Owner.PlayerCombatState.MaxEnergy;
                if (Owner.PlayerCombatState.Energy > cap) Owner.PlayerCombatState.Energy = cap;
            }
            catch { }
            ZLog.W("form=" + St.RForm + " atk=" + St.AtkCount + " def=" + St.DefCount);
            Visuals.SetOverlayForm(true);
            ZhijiPatcher.TransformCutscene();
            // 打出即离手：等出牌演出（放大/收牌）走完再移除，避免卡面预览卡在屏幕上
            try
            {
                var trR = Engine.GetMainLoop() as SceneTree;
                if (trR != null)
                {
                    var tmrR = trR.CreateTimer(0.9);
                    tmrR.Timeout += () =>
                    {
                        try
                        {
                            _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(this, true);
                            ZhijiPatcher.HideCardVisuals(this);
                            ZLog.W("childhood end consumed -> removed from run (delayed)");
                        }
                        catch (Exception eR) { ZLog.W("remove self fail " + eR.Message); }
                    };
                }
                else
                {
                    _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(this, true);
                    ZLog.W("childhood end consumed -> removed from run");
                }
            }
            catch (Exception eR) { ZLog.W("remove self schedule fail " + eR.Message); }
            if (MegaCrit.Sts2.Core.Models.ModelDb.Card<Gluttony>() == null)
                ZLog.W("childhood: gluttony model NULL");
            Visuals.PlayTransform();
            try
            {
                var tr = Engine.GetMainLoop() as SceneTree;
                if (tr != null)
                {
                    var tmr = tr.CreateTimer(12.0);   // 兜底超时（新动画 7.2s）
                    tmr.Timeout += () => Visuals.StopTransform();
                }
            }
            catch { }
            Sync(ctx);
            ZLog.W("evolved: feast=" + St.Feast + " craving=" + St.Craving);
        }
    }


    /// 缝合：回复生命并积攒搏动（常态续航）
    public sealed class Suture : ZhijiCard
    {
        public Suture() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new DynamicVar("Heal", 6m),
            new DynamicVar("Pulse", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Heal")) DynamicVars["Heal"].UpgradeValueBy(3m);
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await CreatureCmd.Heal(Owner.Creature, DV("Heal", Up ? 9 : 6), true);
            St.AddPulse(DV("Pulse", Up ? 4 : 3));
            ZLog.W("suture: heal up=" + Up);
            Sync(ctx);
        }
    }

    /// 啜饮：攻击并吸血（伤害的一半转为治疗）
    public sealed class Siphon : ZhijiCard
    {
        public Siphon() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new DamageVar(6m, ValueProp.Move),
            new DynamicVar("Pulse", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int v; float m = RollDice(out v);
            int dmg = (int)(DV("Damage", 6) * m);
            int before = play.Target != null ? play.Target.CurrentHp : 0;
            await Hit(ctx, play, play.Target, dmg, "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            int dealt = play.Target != null ? System.Math.Max(0, before - play.Target.CurrentHp) : dmg;
            int heal = System.Math.Max(1, dealt / 2);
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            St.AddPulse(DV("Pulse", 2));
            ZLog.W("siphon: dealt=" + dealt + " heal=" + heal);
            Sync(ctx);
        }
    }

    /// 饕餮（测试用作弊卡）：直接获得 100 点无休止饥饿
    public sealed class HungerFeast : ZhijiCard
    {
        public HungerFeast() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self, false) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            St.AddHunger(100);
            ZLog.W("cheat card: hunger=" + St.Hunger);
            Sync(ctx);
            // 消耗：一场战斗只会起效一次（打出后进消耗堆）
            try { await MegaCrit.Sts2.Core.Commands.CardCmd.Exhaust(ctx, this, false, false); } catch (Exception ex) { ZLog.W("cheat exhaust fail " + ex.Message); }
        }
    }

    public sealed class Gluttony : ZhijiCard
    {
        public Gluttony() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(12m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int v; float m = RollDice(out v);
            CountUse(play);
            int bonus = FeastBonus();
            GainReap(15);
            CountUse(play);
            int gb = DV("Damage", 12);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(gb * m) + bonus, play.Target), "vfx/vfx_attack_slash");
            if (m >= 1f)
            {
                await ApplyBleed(ctx, play.Target, 8);
                await CreatureCmd.Heal(Owner.Creature, 6m, true);
                if (play.Target.CurrentHp <= 0) { St.Feast += 5; OnKillBonus(play.Target); }
            }
            Sync(ctx);
        }
    }

    public sealed class TheMissing : ZhijiCard
    {
        public TheMissing() : base(2, CardType.Attack, CardRarity.Basic, TargetType.AllEnemies) { }
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(9m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            var enemies = Owner.Creature.CombatState.Enemies.ToList();
            int bonus = FeastBonus();
            GainReap(15);
            foreach (var e in enemies)
            {
                if (e == null || e.IsDead || e.CurrentHp <= 0) continue;   // 跳过尸体，防止对已死目标排队卡住
                await DamageCmd.Attack(ScaleDamage(DV("Damage", 9) + bonus, e)).FromCard(this, play).Targeting(e).WithHitFx("vfx/vfx_attack_blunt").Execute(ctx);
            }
            foreach (var e in enemies)
            {
                if (e == null || e.IsDead || e.CurrentHp <= 0) continue;
                await ApplyBleed(ctx, e, 6);
                OnKillBonus(e);
            }
            await PowerCmd.Apply<StrengthPower>(ctx, Owner.Creature, 3m, Owner.Creature, this, false);
            Sync(ctx);
        }
    }

    public sealed class Harvest : ZhijiCard
    {
        public Harvest() : base(0, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        protected override bool IsPlayable => St.Reap >= 100;
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(12m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int v; float m = RollDice(out v);
            int bonus = FeastBonus();
            // 大招代价：消耗当前生命的一半，这笔血量加成到每一段伤害（配合回落的回血+等量伤害形成续航）
            int hpCost = (int)(Owner.Creature.CurrentHp / 2);
            if (hpCost > 0)
            {
                ZhijiPatcher.BypassStand = true;
                Owner.Creature.LoseHpInternal(hpCost, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                ZhijiPatcher.BypassStand = false;
            }
            int dmg = ScaleDamage((int)(DV("Damage", 12) * m) + bonus + hpCost / 3, play.Target);
            ZLog.W("harvest hp cost=" + hpCost);
            St.Reap = 0;
            St.PendingRevert = true;
            St.Feast += 2;      // 成长：每次开大永久 +2 自助餐（强度拆分：4 -> 2）
            ZLog.W("harvest growth: feast=" + St.Feast);
            var formU = St.RForm;
            ZLog.W("harvest form=" + formU + " dmg=" + dmg);
            // ===== 多形态大招：战=单体多段，守=格挡转全体伤害，均=全体多段 =====
            if (formU == RedForm.Guard)
            {
                int blockAmt = hpCost + bonus + (int)(12 * m);
                try { Visuals.HarvestStart(play.Target); } catch { }
                await ZhijiPatcher.WaitSec(0.35f);
                await CreatureCmd.GainBlock(Owner.Creature, blockAmt, ValueProp.Move, play, false);
                ZLog.W("harvest guard block=" + blockAmt);
                var foes = Owner.Creature.CombatState.Enemies.ToList();
                int aoe = blockAmt / 2;
                int gi = 0;
                foreach (var e in foes)
                {
                    if (e == null || e.IsDead || e.CurrentHp <= 0) continue;
                    try { Visuals.HarvestHit(gi++, e); } catch { }
                    await DamageCmd.Attack(aoe).FromCard(this, play).Targeting(e).WithHitFx("vfx/vfx_attack_blunt").Execute(ctx);
                    await ZhijiPatcher.WaitSec(0.08f);
                }
                await CreatureCmd.Heal(Owner.Creature, 5m, true);
                try { Visuals.HarvestEnd(); } catch { }
                await ZhijiPatcher.WaitSec(0.30f);
            }
            else if (formU == RedForm.Balance)
            {
                var foes = Owner.Creature.CombatState.Enemies.ToList();
                int per = Math.Max(1, (int)(8 * m) + bonus / 2 + hpCost / 4);
                try { Visuals.HarvestStart(play.Target); } catch { }
                await ZhijiPatcher.WaitSec(0.35f);
                int bi = 0;
                for (int r = 0; r < 3; r++)
                {
                    foreach (var e in foes)
                    {
                        if (e == null || e.IsDead || e.CurrentHp <= 0) continue;
                        try { Visuals.HarvestHit(bi++, e); } catch { }
                        await DamageCmd.Attack(per).FromCard(this, play).Targeting(e).WithHitFx("vfx/vfx_attack_slash").Execute(ctx);
                    }
                    await ZhijiPatcher.WaitSec(0.10f);
                }
                await CreatureCmd.Heal(Owner.Creature, 4m, true);
                try { Visuals.HarvestEnd(); } catch { }
                await ZhijiPatcher.WaitSec(0.30f);
            }
            else
            {
            ZLog.W("harvest ult: dmg=" + dmg + " cinematic");
            // 镜头瞄准目标 → 五段斩击（每段对应一次伤害）→ 收镜
            try { Visuals.HarvestStart(play.Target); } catch (Exception e0) { ZLog.W("cine start fail " + e0.Message); }
            await ZhijiPatcher.WaitSec(0.45f);
            int hitIdx = 0;
            for (int i = 0; i < 5; i++)
            {
                // 目标已死就停下，避免对尸体继续排队造成卡死
                if (play.Target == null || play.Target.IsDead || play.Target.CurrentHp <= 0) break;
                try { Visuals.HarvestHit(hitIdx, play.Target); } catch { }
                await DamageCmd.Attack(dmg).FromCard(this, play).Targeting(play.Target).WithHitFx("vfx/vfx_attack_slash").Execute(ctx);
                await ZhijiPatcher.WaitSec(0.10f);
                hitIdx++;
            }
            try { Visuals.HarvestEnd(); } catch { }
            await ZhijiPatcher.WaitSec(0.35f);
            }
            if (play.Target != null && play.Target.CurrentHp <= 0) { St.Feast += 5; OnKillBonus(play.Target); }
            try { await MegaCrit.Sts2.Core.Commands.CardCmd.Exhaust(ctx, this, false, false); ZLog.W("harvest exhausted"); } catch (Exception ex) { ZLog.W("harvest exhaust fail " + ex.Message); }
            Sync(ctx);
        }
    }

    // ===== 成长卡（不进初始牌组，战斗/商店/事件中获取）=====

    /// 蓄血：获得搏动，并永久提升最大生命
    public sealed class BloodBank : ZhijiCard
    {
        public BloodBank() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Pulse", 4m),
            new DynamicVar("MaxHp", 1m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(2m);
            if (DynamicVars.ContainsKey("MaxHp")) DynamicVars["MaxHp"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            St.AddPulse(DV("Pulse", Up ? 6 : 4));
            try
            {
                var c = Owner.Creature;
                c.SetMaxHpInternal(c.MaxHp + DV("MaxHp", Up ? 2 : 1));
                await CreatureCmd.Heal(c, 1m, true);
            }
            catch (Exception e) { ZLog.W("bloodbank maxhp fail " + e.Message); }
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 饱食：本场战斗提升盛宴（越打越强）
    public sealed class GluttonRite : ZhijiCard
    {
        public GluttonRite() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Feast", 4m),
            new DynamicVar("Pulse", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Feast")) DynamicVars["Feast"].UpgradeValueBy(3m);
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            St.Feast += DV("Feast", Up ? 7 : 4);
            St.AddPulse(DV("Pulse", Up ? 3 : 2));
            ZLog.W("glutton rite: feast=" + St.Feast);
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 丰收仪典：大量收割值，抽一张
    public sealed class ReapRite : ZhijiCard
    {
        public ReapRite() : base(2, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Reap", 25m),
            new DynamicVar("Draw", 1m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Reap")) DynamicVars["Reap"].UpgradeValueBy(10m);
            if (DynamicVars.ContainsKey("Draw")) DynamicVars["Draw"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            GainReap(DV("Reap", Up ? 35 : 25));
            await CardPileCmd.Draw(ctx, DV("Draw", Up ? 2 : 1), Owner);
            Sync(ctx);
        }
    }

    /// 荆棘绽放：伤害 + 流血
    public sealed class ThornBloom : ZhijiCard
    {
        public override bool DotStyle => true;
        public ThornBloom() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(9m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 9) * m), play.Target), "vfx/vfx_attack_slash");
            await ApplyBleed(ctx, play.Target, Up ? 9 : 6);
            Sync(ctx);
        }
    }

    /// 滋养：直接回复生命 + 搏动
    public sealed class Nourish : ZhijiCard
    {
        public Nourish() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new DynamicVar("Heal", 7m),
            new DynamicVar("Pulse", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Heal")) DynamicVars["Heal"].UpgradeValueBy(4m);
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await CreatureCmd.Heal(Owner.Creature, DV("Heal", Up ? 11 : 7), true);
            St.AddPulse(DV("Pulse", Up ? 3 : 2));
            ZLog.W("nourish heal 7");
            Sync(ctx);
        }
    }

    /// 血偿：造成伤害并回复其一半
    public sealed class SanguineDebt : ZhijiCard
    {
        public SanguineDebt() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(8m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int v; float m = RollDice(out v);
            CountUse(play);
            int before = play.Target != null ? play.Target.CurrentHp : 0;
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 8) * m), play.Target), "vfx/vfx_attack_slash");
            int dealt = play.Target != null ? Math.Max(0, before - play.Target.CurrentHp) : 8;
            int heal = Math.Max(2, dealt / 2);
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            ZLog.W("sanguine debt heal=" + heal);
            Sync(ctx);
        }
    }

    /// 盛宴刀叉：高伤 + 流血，按目标流血层数回血
    public sealed class FeastFork : ZhijiCard
    {
        public override bool DotStyle => true;
        public FeastFork() : base(2, CardType.Attack, CardRarity.Rare, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new DamageVar(12m, ValueProp.Move),
            new DynamicVar("Bleed", 6m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(3m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            int v; float m = RollDice(out v);
            CountUse(play);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 12) * m), play.Target), "vfx/vfx_attack_slash");
            await ApplyBleed(ctx, play.Target, DV("Bleed", Up ? 9 : 6));
            int bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0;
            int heal = 3 + bleed;
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            ZLog.W("feast fork heal=" + heal + " bleed=" + bleed);
            Sync(ctx);
        }
    }

    // ===== 红神三形态专属牌 =====

    /// 啖食（战形态）：两段吸血
    public sealed class FleshRend : ZhijiCard
    {
        public FleshRend() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(8m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            GainReap(15);
            int bonus = FeastBonus();
            for (int i = 0; i < 2; i++)
            {
                if (play.Target == null || play.Target.IsDead || play.Target.CurrentHp <= 0) break;
                await Hit(ctx, play, play.Target, ScaleDamage(DV("Damage", 8) + bonus, play.Target), "vfx/vfx_attack_slash");
                await CreatureCmd.Heal(Owner.Creature, 2m, true);
            }
            Sync(ctx);
        }
    }

    /// 血肉壁障（守形态）：格挡 + 回血 + 出血反制
    public sealed class FleshWall : ZhijiCard
    {
        public FleshWall() : base(1, CardType.Skill, CardRarity.Basic, TargetType.Self) { }
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new BlockVar(14m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            GainReap(15);
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 14), ValueProp.Move, play, false);
            await CreatureCmd.Heal(Owner.Creature, Up ? 4m : 3m, true);
            try
            {
                var cs = Owner.Creature.CombatState;
                if (cs != null && cs.Enemies != null)
                    foreach (var e in cs.Enemies)
                        if (e != null && !e.IsDead) await ApplyBleed(ctx, e, Up ? 5 : 4);
            }
            catch { }
            Sync(ctx);
        }
    }

    /// 均衡之刃（均形态）：伤害随攻/守计数成长
    public sealed class BalanceBlade : ZhijiCard
    {
        public BalanceBlade() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }
        public override bool RedGod => true;
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(10m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            GainReap(15);
            int grow = St.AtkCount + St.DefCount;
            int dmg = DV("Damage", 10) + FeastBonus() + grow;
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            ZLog.W("balance blade dmg=" + dmg + " grow=" + grow);
            Sync(ctx);
        }
    }

    // ===== 构筑成长卡（与原版机制联动，不进初始牌组）=====

    /// 抄掠：攻击易伤目标时抽牌并积攒搏动
    public sealed class Forage : ZhijiCard
    {
        public Forage() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(8m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int dmg = DV("Damage", 8);
            int v; float m = RollDice(out v);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            bool vuln = false;
            try { vuln = play.Target != null && play.Target.GetPowerAmount<VulnerablePower>() > 0; } catch { }
            if (vuln)
            {
                await CardPileCmd.Draw(ctx, 1m, Owner);
                St.AddPulse(4);
                ZLog.W("forage -> vulnerable bonus");
            }
            else St.AddPulse(2);
            Sync(ctx);
        }
    }

    /// 蓄势：按当前搏动获得格挡（搏动越多越硬）
    public sealed class Brace : ZhijiCard
    {
        public Brace() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new BlockVar(5m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int baseBlock = DV("Block", 5);
            int pulseBonus = Math.Min(18, St.Pulse * 2);
            await CreatureCmd.GainBlock(Owner.Creature, baseBlock + pulseBonus, ValueProp.Move, play, false);
            ZLog.W("brace block=" + (baseBlock + pulseBonus) + " pulse=" + St.Pulse);
            Sync(ctx);
        }
    }

    /// 力量献祭：消耗力量换搏动与自助餐（和原版力量流互相配合/取舍）
    public sealed class StrengthOffering : ZhijiCard
    {
        public StrengthOffering() : base(0, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new BlockVar(6m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int str = 0;
            try { str = Owner.Creature.GetPowerAmount<StrengthPower>(); } catch { }
            int used = Math.Min(3, str);
            if (used > 0)
            {
                await PowerCmd.Apply<StrengthPower>(ctx, Owner.Creature, -used, Owner.Creature, this, false);
                St.AddPulse(used * 6);
                St.Feast += used * 2;
                await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 6), ValueProp.Move, play, false);
                ZLog.W("strength offering used=" + used + " pulse=" + St.Pulse + " feast=" + St.Feast);
            }
            else
            {
                St.AddPulse(DV("Pulse", 3));
                ZLog.W("strength offering -> no strength, tiny pulse");
            }
            Sync(ctx);
        }
    }

    /// 贪食翻找：抽牌并随抽到的牌数积攒搏动
    public sealed class GreedySearch : ZhijiCard
    {
        public GreedySearch() : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new BlockVar(4m, ValueProp.Move) };

        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int before = 0;
            try { var hp = CardPile.Get(PileType.Hand, Owner); before = hp != null ? hp.Cards.Count : 0; } catch { }
            await CardPileCmd.Draw(ctx, 2m, Owner);
            int after = before;
            try { var hp2 = CardPile.Get(PileType.Hand, Owner); after = hp2 != null ? hp2.Cards.Count : before; } catch { }
            int drawn = Math.Max(0, after - before);
            St.AddPulse(2 + drawn * 2);
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 4), ValueProp.Move, play, false);
            ZLog.W("greedy search drawn=" + drawn + " pulse=" + St.Pulse);
            Sync(ctx);
        }
    }

    // ===== 流血 DOT 流卡牌（黄金寿衣/阴暗饭馆事件体系）=====

    /// 割喉：轻伤害 + 稳定叠流血
    public sealed class SlitThroat : ZhijiCard
    {
        public override bool DotStyle => true;
        public SlitThroat() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(5m, ValueProp.Move),
            new DynamicVar("Bleed", 4m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            int dmg = DV("Damage", 5);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            int bleed = Up ? System.Math.Max(6, DV("Bleed", 6)) : DV("Bleed", 4);
            await ApplyBleed(ctx, play.Target, bleed);
            Sync(ctx);
        }
    }

    /// 放血：让目标现有流血层数翻倍（最多 +8，受上限约束）
    public sealed class BloodLet : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodLet() : base(1, CardType.Skill, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Max", 8m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Max")) DynamicVars["Max"].UpgradeValueBy(4m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int cur = 0;
            try { cur = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            // 放血=精确翻倍（不再吃寿衣/越割越深的加成倍率），仍受层数上限约束
            bool shroud = St.GoldShroud;
            bool board = false;
            try
            {
                if (Owner != null && Owner.Relics != null)
                {
                    if (!shroud) shroud = Owner.Relics.Any(r => r is GoldenShroud);
                    board = Owner.Relics.Any(r => r is BleedBoard);
                }
            }
            catch { }
            int cap = BleedCapValue(shroud, board);
            try
            {
                int capPow = Owner != null && Owner.Creature != null ? Owner.Creature.GetPowerAmount<BleedCapPower>() : 0;
                if (capPow > 0) cap += capPow;
            }
            catch { }
            int want = System.Math.Min(Up ? System.Math.Max(12, DV("Max", 12)) : DV("Max", 8), System.Math.Max(1, cur));
            int add = System.Math.Max(0, System.Math.Min(want, cap - cur));
            if (add > 0 && play.Target != null)
                try { await PowerCmd.Apply<BleedPower>(ctx, play.Target, add, Owner.Creature, this, false); } catch { }
            ZLog.W("blood let cur=" + cur + " add=" + add + " cap=" + cap);
            Sync(ctx);
        }
    }

    /// 血债：伤害随目标流血层数成长（每 5 层 +2）
    public sealed class BloodDebt : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodDebt() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(4m, ValueProp.Move),
            new DynamicVar("Per", 5m),
            new DynamicVar("Bonus", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bonus")) DynamicVars["Bonus"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            int baseDmg = DV("Damage", 4);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            int per = System.Math.Max(1, DV("Per", 5));
            int bonus = (bleed / per) * DV("Bonus", 2);
            await Hit(ctx, play, play.Target, ScaleDamage((int)((baseDmg + bonus) * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            ZLog.W("blood debt bleed=" + bleed + " bonus=" + bonus);
            Sync(ctx);
        }
    }

    /// 千刀：本场每触发过一次流血，此牌伤害 +1（上限 +20）
    public sealed class ThousandBlades : ZhijiCard
    {
        public override bool DotStyle => true;
        public ThousandBlades() : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(5m, ValueProp.Move),
            new DynamicVar("Max", 15m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Max")) DynamicVars["Max"].UpgradeValueBy(10m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            int bonus = System.Math.Min(DV("Max", 15), St.BleedTicks);
            int dmg = DV("Damage", 5) + bonus;
            await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            ZLog.W("thousand blades ticks=" + St.BleedTicks + " dmg=" + dmg);
            Sync(ctx);
        }
    }

    /// 引爆：消耗目标全部流血层数，造成层数×2 的伤害
    public sealed class Detonate : ZhijiCard
    {
        public override bool DotStyle => true;
        public Detonate() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Mult", 1.5m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Mult")) DynamicVars["Mult"].UpgradeValueBy(0.5m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            if (bleed <= 0) { ZLog.W("detonate: no bleed"); Sync(ctx); return; }
            try { _ = PowerCmd.Apply<BleedPower>(ctx, play.Target, -bleed, Owner.Creature, this, true); } catch { }
            decimal mult = 1.5m;
            try { mult = DynamicVars["Mult"].BaseValue; } catch { }
            int dmg = (int)System.Math.Floor(bleed * mult);
            bool board = false;
            try { board = Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is BleedBoard); } catch { }
            if (board) dmg = (int)(dmg * 1.5f);
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_blunt");
            ZLog.W("detonate bleed=" + bleed + " dmg=" + dmg);
            Sync(ctx);
        }
    }


    /// 血契：失去生命，换取大量流血
    public sealed class BloodPact : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodPact() : base(0, CardType.Skill, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Bleed", 5m),
            new DynamicVar("Hp", 6m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            ZhijiPatcher.BypassStand = true;
            try { Owner.Creature.LoseHpInternal(DV("Hp", 4), MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { }
            ZhijiPatcher.BypassStand = false;
            await ApplyBleed(ctx, play.Target, DV("Bleed", 5));
            Sync(ctx);
        }
    }

    /// 血潮：按目标流血层数直接造成伤害（不消耗层数）
    public sealed class BloodSurge : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodSurge() : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Mult", 1.5m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Mult")) DynamicVars["Mult"].UpgradeValueBy(0.5m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            int dmg = System.Math.Max(1, bleed * DV("Mult", 2));
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            ZLog.W("blood surge bleed=" + bleed + " dmg=" + dmg);
            Sync(ctx);
        }
    }


    /// 血刺：轻攻击；若目标有流血则追加伤害
    public sealed class BloodSpike : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodSpike() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(5m, ValueProp.Move),
            new DynamicVar("Bonus", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bonus")) DynamicVars["Bonus"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            int dmg = DV("Damage", 5);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            if (bleed > 0) dmg += DV("Bonus", 3);
            ZLog.W("blood spike bleed=" + bleed + " dmg=" + dmg);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(dmg * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            Sync(ctx);
        }
    }

    /// 凝血：获得格挡；若目标有流血则额外格挡
    public sealed class Coagulate : ZhijiCard
    {
        public override bool DotStyle => true;
        public Coagulate() : base(1, CardType.Skill, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new BlockVar(6m, ValueProp.Move),
            new DynamicVar("Bonus", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bonus")) DynamicVars["Bonus"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int block = DV("Block", 6);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            if (bleed > 0) block += DV("Bonus", 3);
            ZLog.W("coagulate bleed=" + bleed + " block=" + block);
            await CreatureCmd.GainBlock(Owner.Creature, block, ValueProp.Move, play, false);
            Sync(ctx);
        }
    }


    // ===== 卑贱之物流派（独立于 DOT/召唤/红神）=====

    /// 卑贱烙印：低伤害，叠 2 层卑贱之物
    public sealed class VileBrand : ZhijiCard
    {
        public VileBrand() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DamageVar(3m, ValueProp.Move),
            new DynamicVar("Vile", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Vile")) DynamicVars["Vile"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 3) * m), play.Target), "vfx/vfx_attack_slash");
            await ApplyVile(ctx, DV("Vile", 2));
            ZLog.W("vile brand +" + DV("Vile", 2) + " now=" + Owner.Creature.GetPowerAmount<VileThingPower>());
            Sync(ctx);
        }
    }

    /// 卑贱低语：0 费，低格挡，叠 1 层
    public sealed class VileWhisper : ZhijiCard
    {
        public VileWhisper() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new BlockVar(2m, ValueProp.Move),
            new DynamicVar("Vile", 1m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Vile")) DynamicVars["Vile"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 2), ValueProp.Move, play, false);
            await ApplyVile(ctx, DV("Vile", 1));
            ZLog.W("vile whisper +" + DV("Vile", 1) + " now=" + Owner.Creature.GetPowerAmount<VileThingPower>());
            Sync(ctx);
        }
    }

    /// 卑贱献祭：自伤换 3 层
    public sealed class VileOffering : ZhijiCard
    {
        public VileOffering() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Hp", 2m),
            new DynamicVar("Vile", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Vile")) DynamicVars["Vile"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            ZhijiPatcher.BypassStand = true;
            try { Owner.Creature.LoseHpInternal(DV("Hp", 2), MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { }
            ZhijiPatcher.BypassStand = false;
            await ApplyVile(ctx, DV("Vile", 3));
            ZLog.W("vile offering +" + DV("Vile", 3) + " now=" + Owner.Creature.GetPowerAmount<VileThingPower>());
            Sync(ctx);
        }
    }

    /// 卑贱终末：2 费，叠 4 层并抽 1
    public sealed class VileEnd : ZhijiCard
    {
        public VileEnd() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Vile", 4m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Vile")) DynamicVars["Vile"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await ApplyVile(ctx, DV("Vile", 4));
            await CardPileCmd.Draw(ctx, 1m, Owner);
            ZLog.W("vile end +" + DV("Vile", 4));
            Sync(ctx);
        }
    }


    /// 饥饿共鸣：红神流铺垫卡，大量搏动
    public sealed class HungerEcho : ZhijiCard
    {
        public HungerEcho() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Pulse", 8m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(4m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            St.AddPulse(DV("Pulse", 8));
            ZLog.W("hunger echo pulse +" + DV("Pulse", 8));
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 暴食前夕：红神流铺垫卡，直接堆饥饿
    public sealed class FeastEve : ZhijiCard
    {
        public FeastEve() : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Hunger", 15m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Hunger")) DynamicVars["Hunger"].UpgradeValueBy(10m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            St.AddHunger(DV("Hunger", 15));
            ZLog.W("feast eve hunger +" + DV("Hunger", 15));
            Sync(ctx);
            await Task.CompletedTask;
        }
    }


    /// 卑贱清算：消耗全部卑贱之物，每层造成伤害（额外回合与爆发的抉择）
    public sealed class VileReckoning : ZhijiCard
    {
        public VileReckoning() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Per", 3m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Per")) DynamicVars["Per"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int stacks = 0;
            try { stacks = Owner.Creature.GetPowerAmount<VileThingPower>(); } catch { }
            if (stacks > 0)
            {
                try { _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(ctx, Owner.Creature, -stacks, Owner.Creature, this, true); } catch { }
            }
            int dmg = System.Math.Max(1, stacks * DV("Per", 3));
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_blunt");
            OnKillBonus(play.Target);
            ZLog.W("vile reckoning stacks=" + stacks + " dmg=" + dmg);
            Sync(ctx);
        }
    }


    /// 血雨：对全体敌人施加少量流血
    public sealed class BloodRain : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodRain() : base(1, CardType.Skill, CardRarity.Common, TargetType.AllEnemies) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Bleed", 3m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            var cs = Owner.Creature != null ? Owner.Creature.CombatState : null;
            if (cs != null && cs.Enemies != null)
                foreach (var e in cs.Enemies)
                    if (e != null && !e.IsDead) await ApplyBleed(ctx, e, DV("Bleed", 3));
            ZLog.W("blood rain +" + DV("Bleed", 3) + " all");
            Sync(ctx);
        }
    }

    /// 血偿：按目标流血层数获得格挡
    public sealed class BloodRepay : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodRepay() : base(1, CardType.Skill, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Max", 20m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Max")) DynamicVars["Max"].UpgradeValueBy(10m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            int block = System.Math.Max(3, System.Math.Min(DV("Max", 20), bleed));
            await CreatureCmd.GainBlock(Owner.Creature, block, ValueProp.Move, play, false);
            ZLog.W("blood repay bleed=" + bleed + " block=" + block);
            Sync(ctx);
        }
    }



    /// 血潮（能力）：每次攻击附加等同伤害的流血
    public sealed class BloodTide : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodTide() : base(1, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Amt", 1m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Amt")) DynamicVars["Amt"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BloodTidePower>(ctx, Owner.Creature, DV("Amt", 1), Owner.Creature, this, false);
            ZLog.W("blood tide applied");
            Sync(ctx);
        }
    }


    // 测试用：尝试召唤原版 Osty（验证能否复用骨妹召唤系统）
    public sealed class TestOstySummon : ZhijiCard
    {
        public TestOstySummon() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self, false) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            try
            {
                var res = await MegaCrit.Sts2.Core.Commands.OstyCmd.Summon(ctx, Owner, 6m, this);
                ZLog.W("test osty summon amount=" + (res != null ? res.Amount.ToString() : "null") + " hp=" + (Owner.Osty != null ? Owner.Osty.CurrentHp.ToString() : "-"));
            }
            catch (Exception e) { ZLog.W("test osty fail " + e.GetBaseException().Message); }
            await Task.CompletedTask;
        }
    }


    /// 疾影（能力）：本场战斗闪避概率提高
    public sealed class DodgeInstinct : ZhijiCard
    {
        public DodgeInstinct() : base(1, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Pct", 10m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pct")) DynamicVars["Pct"].UpgradeValueBy(5m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<DodgeChancePower>(ctx, Owner.Creature, DV("Pct", 10), Owner.Creature, this, false);
            ZLog.W("dodge instinct +" + DV("Pct", 10) + "%");
            Sync(ctx);
        }
    }

    /// 侧步（技能）：本场闪避 +5%，抽一张
    public sealed class DodgeStep : ZhijiCard
    {
        public DodgeStep() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<DodgeChancePower>(ctx, Owner.Creature, 5, Owner.Creature, this, false);
            try { await MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(ctx, 1, Owner); } catch { }
            ZLog.W("sidestep +5% dodge");
            Sync(ctx);
        }
    }

    // ===== 能力牌（Power）：把机制从被动拆到卡牌 =====

    /// 血之本能（能力）：每层使施加的流血 +1
    public sealed class BleedInstinct : ZhijiCard
    {
        public override bool DotStyle => true;
        public BleedInstinct() : base(1, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Amt", 1m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Amt")) DynamicVars["Amt"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedInstinctPower>(ctx, Owner.Creature, DV("Amt", 1), Owner.Creature, this, false);
            ZLog.W("bleed instinct applied");
            Sync(ctx);
        }
    }

    /// 饥饿律动（能力）：每回合开始获得搏动
    public sealed class HungerRhythm : ZhijiCard
    {
        public HungerRhythm() : base(1, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Pulse", 3m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<HungerRhythmPower>(ctx, Owner.Creature, DV("Pulse", 3), Owner.Creature, this, false);
            ZLog.W("hunger rhythm applied");
            Sync(ctx);
        }
    }

    /// 卑贱共鸣（能力）：每张卑贱卡额外叠层
    public sealed class VileResonance : ZhijiCard
    {
        public VileResonance() : base(2, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Amt", 1m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Amt")) DynamicVars["Amt"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileResonancePower>(ctx, Owner.Creature, DV("Amt", 1), Owner.Creature, this, false);
            ZLog.W("vile resonance applied");
            Sync(ctx);
        }
    }


    /// 饥饿契约（能力）：每回合开始额外获得饥饿，加快变身
    public sealed class HungerPact : ZhijiCard
    {
        public HungerPact() : base(2, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Hunger", 3m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Hunger")) DynamicVars["Hunger"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<HungerPactPower>(ctx, Owner.Creature, DV("Hunger", 3), Owner.Creature, this, false);
            ZLog.W("hunger pact applied");
            Sync(ctx);
        }
    }

    /// 暴食本能（能力）：红神形态每回合开始获得自助餐
    public sealed class GluttonInstinct : ZhijiCard
    {
        public GluttonInstinct() : base(2, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Feast", 2m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Feast")) DynamicVars["Feast"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<GluttonInstinctPower>(ctx, Owner.Creature, DV("Feast", 2), Owner.Creature, this, false);
            ZLog.W("glutton instinct applied");
            Sync(ctx);
        }
    }


    /// 血之回响（能力）：每回合第一次施加流血时抽 1 张牌
    public sealed class BloodEcho : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodEcho() : base(1, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BloodEchoPower>(ctx, Owner.Creature, 1, Owner.Creature, this, false);
            ZLog.W("blood echo applied");
            Sync(ctx);
        }
    }


    /// 血之容器（能力）：提高流血上限
    public sealed class BloodVessel : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodVessel() : base(1, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Cap", 10m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Cap")) DynamicVars["Cap"].UpgradeValueBy(5m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedCapPower>(ctx, Owner.Creature, DV("Cap", 10), Owner.Creature, this, false);
            ZLog.W("blood vessel applied");
            Sync(ctx);
        }
    }


    /// 收割之镰：消耗全部收割值，换取伤害
    public sealed class ReapScythe : ZhijiCard
    {
        public ReapScythe() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int reap = St.Reap;
            St.Reap = 0;
            int dmg = System.Math.Max(4, System.Math.Min(60, reap / 2));
            bool mask = Up;
            if (mask) dmg = (int)(dmg * 1.35f);
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            ZLog.W("reap scythe reap=" + reap + " dmg=" + dmg);
            Sync(ctx);
        }
    }


    /// 卑贱循环（能力）：每回合开始自动叠卑贱之物
    public sealed class VileLoop : ZhijiCard
    {
        public VileLoop() : base(2, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Amt", 2m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Amt")) DynamicVars["Amt"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileLoopPower>(ctx, Owner.Creature, DV("Amt", 2), Owner.Creature, this, false);
            ZLog.W("vile loop applied");
            Sync(ctx);
        }
    }


    // ===== 附魔：失控（给卡牌加伤害 + 反伤）=====
    public sealed class UnboundRage : MegaCrit.Sts2.Core.Models.EnchantmentModel
    {
        public override bool CanEnchantCardType(MegaCrit.Sts2.Core.Entities.Cards.CardType cardType)
            => cardType == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack
            || cardType == MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill;

        public override decimal EnchantDamageMultiplicative(decimal originalDamage, MegaCrit.Sts2.Core.ValueProps.ValueProp props)
            => 1.5m;   // 引擎按"倍率因子"使用，返回 1.5 = 伤害 ×1.5

        public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay play)
        {
            try
            {
                var card = Card;
                var owner = card != null ? card.Owner : null;
                if (owner == null || owner.Creature == null) return;
                int dmg = 0;
                try { if (card.DynamicVars.ContainsKey("Damage")) dmg = (int)(card.DynamicVars["Damage"].BaseValue * 1.5m); } catch { }
                int recoil = dmg > 0 ? System.Math.Max(1, dmg / 2) : 0;
                if (recoil > 10) recoil = 10;
                if (recoil > 0)
                {
                    ZhijiPatcher.BypassStand = true;
                    owner.Creature.LoseHpInternal(recoil, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZhijiPatcher.BypassStand = false;
                    try { Visuals.Popup("失控！伤害 ×1.5，反噬 " + recoil, new Godot.Color(1f, 0.25f, 0.2f)); } catch { }
                    ZLog.W("unbound rage recoil " + recoil + " (dmg=" + dmg + ")");
                }
            }
            catch (Exception e) { ZLog.W("unbound rage onplay fail " + e.Message); }
            await Task.CompletedTask;
        }
    }


    // ===== 失控红神：召唤流卡牌 =====

    /// 开席：召唤肉块（血量随自助餐成长）
    public sealed class FeastCall : ZhijiCard
    {
        public FeastCall() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Base", 10m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Base")) DynamicVars["Base"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int hp = System.Math.Min(50, DV("Base", 10) + St.Feast / 2);
            await SummonOsty(ctx, hp);
            ZhijiPatcher.RewardSummonBuff(Owner);
            St.AddHunger(-8);   // 3.3.20：喂血肉能压饥饿——召唤流成为"管理自损"的正解
            Sync(ctx);
        }
    }

    /// 喂食：强化肉块，自己获得自助餐
    public sealed class FeedGuest : ZhijiCard
    {
        public FeedGuest() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Hp", 4m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Hp")) DynamicVars["Hp"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int add = DV("Hp", 4);
            if (OstyAlive) { Owner.Osty.SetMaxHpInternal(Owner.Osty.MaxHp + add); Owner.Osty.HealInternal(add); }
            else await SummonOsty(ctx, add);
            St.AddFeast(2);
            ZhijiPatcher.RewardSummonBuff(Owner);
            St.AddHunger(-6);
            ZLog.W("feed guest +" + add + " feast+2");
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 共餐：肉块替你咬一口，你回血
    public sealed class DineTogether : ZhijiCard
    {
        public DineTogether() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int hp = OstyAlive ? (int)Owner.Osty.CurrentHp : 0;
            int dmg = System.Math.Max(4, hp / 2);
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            int heal = System.Math.Max(1, dmg / 2);
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            ZLog.W("dine together hp=" + hp + " dmg=" + dmg + " heal=" + heal);
            Sync(ctx);
        }
    }


    /// 扑击：血肉自损换爆发伤害
    public sealed class FleshPounce : ZhijiCard
    {
        public FleshPounce() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int cur = OstyAlive ? (int)Owner.Osty.CurrentHp : 0;
            int spend = System.Math.Min(8, System.Math.Max(0, cur - 1));
            if (spend > 0) { try { Owner.Osty.LoseHpInternal(spend, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { } }
            int dmg = System.Math.Max(4, spend * 2);
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            ZLog.W("pounce spend=" + spend + " dmg=" + dmg);
            Sync(ctx);
        }
    }

    /// 连咬：两次基于最大生命的攻击
    public sealed class RelentlessBite : ZhijiCard
    {
        public RelentlessBite() : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int maxHp = OstyAlive ? (int)Owner.Osty.MaxHp : 0;
            int dmg = System.Math.Max(3, maxHp / 5);
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            await Hit(ctx, play, play.Target, ScaleDamage(dmg, play.Target), "vfx/vfx_attack_slash");
            ZLog.W("relentless bite maxHp=" + maxHp + " dmg=" + dmg);
            Sync(ctx);
        }
    }

    /// 血爪：攻击并附加流血
    public sealed class BloodClaw : ZhijiCard
    {
        public BloodClaw() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await Hit(ctx, play, play.Target, ScaleDamage(5, play.Target), "vfx/vfx_attack_slash");
            int maxHp = OstyAlive ? (int)Owner.Osty.MaxHp : 0;
            int bleed = System.Math.Max(2, maxHp / 4);
            await ApplyBleed(ctx, play.Target, bleed);
            ZLog.W("blood claw maxHp=" + maxHp + " bleed=" + bleed);
            Sync(ctx);
        }
    }

    /// 血神共鸣（能力）：血肉每回合追加一次攻击
    public sealed class BloodResonance : ZhijiCard
    {
        public BloodResonance() : base(2, CardType.Power, CardRarity.Rare, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<ResonancePower>(ctx, Owner.Creature, 1, Owner.Creature, this, false);
            ZLog.W("blood resonance applied");
            Sync(ctx);
        }
    }

    /// 巨化：血肉最大生命 +6 并治疗 3
    public sealed class GiantGrowth : ZhijiCard
    {
        public GiantGrowth() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            if (OstyAlive) { Owner.Osty.SetMaxHpInternal(Owner.Osty.MaxHp + 6); Owner.Osty.HealInternal(3); }
            ZhijiPatcher.RewardSummonBuff(Owner);
            ZLog.W("giant growth +6 max");
            Sync(ctx);
        }
    }

    /// 残渣利用（能力）：血肉死亡时回血与自助餐
    public sealed class LeftoverUse : ZhijiCard
    {
        public LeftoverUse() : base(1, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<LeftoverPower>(ctx, Owner.Creature, 1, Owner.Creature, this, false);
            ZLog.W("leftover use applied");
            Sync(ctx);
        }
    }

    /// 断肢：血肉自损抽牌
    public sealed class Dismember : ZhijiCard
    {
        public Dismember() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int draw = 2;
            if (OstyAlive)
            {
                int cost = System.Math.Min(4, System.Math.Max(0, (int)Owner.Osty.CurrentHp - 1));
                if (cost > 0) { try { Owner.Osty.LoseHpInternal(cost, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { } }
                if (Owner.Osty.IsDead || Owner.Osty.CurrentHp <= 0) draw = 3;
            }
            try { await MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(ctx, draw, Owner); } catch { }
            ZLog.W("dismember draw=" + draw);
            Sync(ctx);
        }
    }

    /// 号令（能力）：每回合血肉自动攻击
    public sealed class CommandGuest : ZhijiCard
    {
        public CommandGuest() : base(1, CardType.Power, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<CommandPower>(ctx, Owner.Creature, 1, Owner.Creature, this, false);
            ZLog.W("command guest applied");
            Sync(ctx);
        }
    }

    /// 献祭：杀死一个肉块，全屏伤害并回血
    public sealed class SacrificeGuest : ZhijiCard
    {
        public SacrificeGuest() : base(1, CardType.Skill, CardRarity.Common, TargetType.AllEnemies) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int lost = 0;
            if (OstyAlive)
            {
                lost = System.Math.Max(0, (int)Owner.Osty.CurrentHp / 2);
                try { Owner.Osty.LoseHpInternal(lost, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { }
            }
            int aoe = System.Math.Max(10, lost);
            var cs = Owner.Creature != null ? Owner.Creature.CombatState : null;
            if (cs != null && cs.Enemies != null)
                foreach (var e in cs.Enemies)
                    if (e != null && !e.IsDead) e.LoseHpInternal(aoe, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            await CreatureCmd.Heal(Owner.Creature, 6, true);
            ZLog.W("sacrifice guest: lost=" + lost + " aoe=" + aoe + " heal 6");
            Sync(ctx);
        }
    }


    /// 增殖：再召唤一个肉块
    public sealed class MultiplyGuest : ZhijiCard
    {
        public MultiplyGuest() : base(2, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int hp = System.Math.Max(12, 12 + St.Feast / 2);
            if (OstyAlive) { Owner.Osty.SetMaxHpInternal(Owner.Osty.MaxHp + 2); Owner.Osty.HealInternal(2); St.AddFeast(3); St.MinionHp2 = System.Math.Max(St.MinionHp2, hp); ZLog.W("multiply: osty +2 max, minion2 hp=" + St.MinionHp2); }
            else await SummonOsty(ctx, hp);
            ZhijiPatcher.RewardSummonBuff(Owner);
            Sync(ctx);
        }
    }

    /// 护食：挡伤害，顺手喂肉块
    public sealed class ProtectGuest : ZhijiCard
    {
        public ProtectGuest() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new BlockVar(6m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 6), ValueProp.Move, play, false);
            if (OstyAlive) Owner.Osty.HealInternal(3);
            ZLog.W("protect guest block=6");
            Sync(ctx);
        }
    }

    /// 暴食：吃掉肉块换自助餐
    public sealed class DevourGuest : ZhijiCard
    {
        public DevourGuest() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int gained = 0;
            if (OstyAlive) { gained = System.Math.Max(4, (int)Owner.Osty.CurrentHp); try { Owner.Osty.LoseHpInternal(Owner.Osty.CurrentHp, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { } }
            St.AddFeast(gained);
            ZhijiPatcher.BypassStand = true;
            Owner.Creature.LoseHpInternal(3, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            ZhijiPatcher.BypassStand = false;
            ZLog.W("devour guest: feast+" + gained + " selfloss=3");
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 哄睡：肉块满血，抽一张
    public sealed class LullabyGuest : ZhijiCard
    {
        public LullabyGuest() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            if (OstyAlive) Owner.Osty.HealInternal(Owner.Osty.MaxHp);
            try { await MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(ctx, 1, Owner); } catch { }
            ZLog.W("lullaby guest: heal full, draw 1");
            Sync(ctx);
        }
    }


    // ===== 失控红神专属牌（伤害高、自损）=====

    /// 失控·撕咬
    public sealed class UnboundBite : ZhijiCard
    {
        public UnboundBite() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(10m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await Hit(ctx, play, play.Target, ScaleDamage(DV("Damage", 10), play.Target), "vfx/vfx_attack_slash");
            ZhijiPatcher.BypassStand = true;
            Owner.Creature.LoseHpInternal(3, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            ZhijiPatcher.BypassStand = false;
            ZLog.W("unbound bite dmg=" + DV("Damage", 10) + " selfloss=3");
            Sync(ctx);
        }
    }

    /// 失控·贪食（击杀回血）
    public sealed class UnboundGluttony : ZhijiCard
    {
        public UnboundGluttony() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(12m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await Hit(ctx, play, play.Target, ScaleDamage(DV("Damage", 12), play.Target), "vfx/vfx_attack_slash");
            ZhijiPatcher.BypassStand = true;
            Owner.Creature.LoseHpInternal(2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            ZhijiPatcher.BypassStand = false;
            if (play.Target != null && play.Target.IsDead) await CreatureCmd.Heal(Owner.Creature, 8, true);
            ZLog.W("unbound gluttony dmg=12 selfloss=2 killheal=" + (play.Target != null && play.Target.IsDead));
            Sync(ctx);
        }
    }

    /// 失控·盛宴（回血后自损）
    public sealed class UnboundFeast : ZhijiCard
    {
        public UnboundFeast() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int heal = 12;
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            ZhijiPatcher.BypassStand = true;
            Owner.Creature.LoseHpInternal(3, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            ZhijiPatcher.BypassStand = false;
            ZLog.W("unbound feast heal=12 selfloss=3");
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 失控·咆哮（全体 + 流血）
    public sealed class UnboundRoar : ZhijiCard
    {
        public UnboundRoar() : base(2, CardType.Attack, CardRarity.Rare, TargetType.AllEnemies) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(6m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int dmg = DV("Damage", 6);
            var cs = Owner.Creature != null ? Owner.Creature.CombatState : null;
            if (cs != null && cs.Enemies != null)
                foreach (var e in cs.Enemies)
                    if (e != null && !e.IsDead)
                    {
                        e.LoseHpInternal(ScaleDamage(dmg, e), MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
                    }
            try { await ApplyVile(ctx, 2); } catch { }
            ZhijiPatcher.BypassStand = true;
            Owner.Creature.LoseHpInternal(3, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
            ZhijiPatcher.BypassStand = false;
            ZLog.W("unbound roar aoe=" + dmg + " selfloss=3");
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 万灵盛宴（3.3.19）：召唤流终结技。5 费，每给血肉加成一次永久 -1 费（最低 1）。
    public sealed class FeastFinale : ZhijiCard
    {
        public FeastFinale() : base(5, CardType.Attack, CardRarity.Rare, TargetType.AllEnemies) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DamageVar(10m, ValueProp.Move) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Damage")) DynamicVars["Damage"].UpgradeValueBy(4m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int bonus = St.Feast / 2 + St.SummonBuff * 2;
            int dmg = ScaleDamage(DV("Damage", 10) + bonus, play.Target);
            var foes = Owner.Creature.CombatState.Enemies.ToList();
            foreach (var e in foes)
            {
                if (e == null || e.IsDead || e.CurrentHp <= 0) continue;
                await DamageCmd.Attack(dmg).FromCard(this, play).Targeting(e).WithHitFx("vfx/vfx_attack_slash").Execute(ctx);
            }
            ZLog.W("feast finale dmg=" + dmg + " feast=" + St.Feast + " buffs=" + St.SummonBuff);
            Sync(ctx);
        }
    }

    // ===== 召唤流卡牌 =====

    /// 招来食客：召唤一个替本体挨打的食客（+12 血，最多 30）
    public sealed class SummonGuest : ZhijiCard
    {
        public SummonGuest() : base(1, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        public override bool DotStyle => false;
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            St.Summon(Up ? 18 : 12, Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is DinnerBell) ? 40 : 30);
            ZLog.W("summon guest -> hp=" + St.MinionHp);
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 加餐：给食客补血（+6），并让它在下一回合多咬一口
    public sealed class SecondHelping : ZhijiCard
    {
        public SecondHelping() : base(0, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            St.Summon(Up ? 10 : 6, Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is DinnerBell) ? 40 : 30);
            ZLog.W("second helping -> hp=" + St.MinionHp);
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 群宴：一次叫出两个食客（+24 血）
    public sealed class GrandBanquet : ZhijiCard
    {
        public GrandBanquet() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.Self) { }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            St.Summon(Up ? 34 : 24, Owner != null && Owner.Relics != null && Owner.Relics.Any(r => r is DinnerBell) ? 40 : 30);
            ZLog.W("grand banquet -> hp=" + St.MinionHp);
            Sync(ctx);
            await Task.CompletedTask;
        }
    }

    /// 血宴：按目标流血层数回复生命
    public sealed class BloodFeast : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodFeast() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Base", 2m),
            new DynamicVar("Max", 15m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Base")) DynamicVars["Base"].UpgradeValueBy(1m);
            if (DynamicVars.ContainsKey("Max")) DynamicVars["Max"].UpgradeValueBy(5m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int bleed = 0;
            try { bleed = play.Target != null ? play.Target.GetPowerAmount<BleedPower>() : 0; } catch { }
            int heal = System.Math.Min(DV("Max", Up ? 20 : 15), DV("Base", 2) + bleed / 3);
            await CreatureCmd.Heal(Owner.Creature, heal, true);
            ZLog.W("blood feast bleed=" + bleed + " heal=" + heal);
            Sync(ctx);
        }
    }

    /// 血雾：对全体敌人施加流血
    public sealed class BloodMist : ZhijiCard
    {
        public override bool DotStyle => true;
        public BloodMist() : base(1, CardType.Skill, CardRarity.Common, TargetType.AllEnemies) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Bleed", 3m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Bleed")) DynamicVars["Bleed"].UpgradeValueBy(2m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            var cs = Owner.Creature != null ? Owner.Creature.CombatState : null;
            if (cs != null && cs.Enemies != null)
            {
                foreach (var e in cs.Enemies)
                {
                    if (e == null || e.IsDead) continue;
                    await ApplyBleed(ctx, e, DV("Bleed", Up ? 5 : 3));
                }
            }
            ZLog.W("blood mist applied");
            Sync(ctx);
        }
    }

    /// 越割越深：本场战斗施加的流血额外 +50%（可叠加）
    public sealed class Deepen : ZhijiCard
    {
        public override bool DotStyle => true;
        public Deepen() : base(1, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[] { new DynamicVar("Percent", 50m) };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Percent")) DynamicVars["Percent"].UpgradeValueBy(50m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await PowerCmd.Apply<BloodThirstPower>(ctx, Owner.Creature, Up ? 2m : 1m, Owner.Creature, this, false);
            ZLog.W("deepen thirst=" + Owner.Creature.GetPowerAmount<BloodThirstPower>());
            Sync(ctx);
        }
    }

    // ===== 幼年卡池扩充（⑥：多吃原版机制）=====

    /// 龇牙：施加虚弱与易伤，并积攒搏动
    public sealed class Snarl : ZhijiCard
    {
        public Snarl() : base(1, CardType.Skill, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Weak", 2m),
            new DynamicVar("Vuln", 2m),
            new DynamicVar("Pulse", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Weak")) DynamicVars["Weak"].UpgradeValueBy(1m);
            if (DynamicVars.ContainsKey("Vuln")) DynamicVars["Vuln"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            if (play.Target != null)
            {
                await PowerCmd.Apply<WeakPower>(ctx, play.Target, DV("Weak", Up ? 3 : 2), Owner.Creature, this, false);
                await PowerCmd.Apply<VulnerablePower>(ctx, play.Target, DV("Vuln", Up ? 3 : 2), Owner.Creature, this, false);
            }
            St.AddPulse(3);
            ZLog.W("snarl weak/vuln applied");
            Sync(ctx);
        }
    }

    /// 撕扯：伤害 + 若目标有易伤则额外流血
    public sealed class Tear : ZhijiCard
    {
        public override bool DotStyle => true;
        public Tear() : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[] { new DamageVar(7m, ValueProp.Move) };
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 7) * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            bool vuln = false;
            try { vuln = play.Target != null && play.Target.GetPowerAmount<VulnerablePower>() > 0; } catch { }
            if (vuln) await ApplyBleed(ctx, play.Target, Up ? 5 : 3);
            ZLog.W("tear vuln=" + vuln);
            Sync(ctx);
        }
    }

    /// 血之回响：抽牌，并按本场流血触发次数额外抽（每4次+1，最多+2）
    public sealed class EchoOfBlood : ZhijiCard
    {
        public override bool DotStyle => true;
        public EchoOfBlood() : base(1, CardType.Skill, CardRarity.Rare, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Extra", 2m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Extra")) DynamicVars["Extra"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int extra = System.Math.Min(DV("Extra", Up ? 3 : 2), St.BleedTicks / 4);
            await CardPileCmd.Draw(ctx, 1m + extra, Owner);
            ZLog.W("echo of blood ticks=" + St.BleedTicks + " draw=" + (1 + extra));
            Sync(ctx);
        }
    }

    /// 咀嚼：轻攻击并积攒搏动
    public sealed class Chew : ZhijiCard
    {
        public Chew() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new DamageVar(6m, ValueProp.Move),
            new DynamicVar("Pulse", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            int v; float m = RollDice(out v);
            await Hit(ctx, play, play.Target, ScaleDamage((int)(DV("Damage", 6) * m), play.Target), "vfx/vfx_attack_slash");
            OnKillBonus(play.Target);
            St.AddPulse(DV("Pulse", Up ? 4 : 3));
            Sync(ctx);
        }
    }

    /// 缠舌：施加虚弱并抽牌
    public sealed class TangledTongue : ZhijiCard
    {
        public TangledTongue() : base(1, CardType.Skill, CardRarity.Uncommon, TargetType.AnyEnemy) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
        {
            new DynamicVar("Weak", 2m),
            new DynamicVar("Draw", 1m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Weak")) DynamicVars["Weak"].UpgradeValueBy(1m);
            if (DynamicVars.ContainsKey("Draw")) DynamicVars["Draw"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            if (play.Target != null)
                await PowerCmd.Apply<WeakPower>(ctx, play.Target, DV("Weak", Up ? 3 : 2), Owner.Creature, this, false);
            await CardPileCmd.Draw(ctx, DV("Draw", Up ? 2 : 1), Owner);
            ZLog.W("tangled tongue drawn");
            Sync(ctx);
        }
    }

    /// 收拾残局：获得格挡与搏动（低费防守填充）
    public sealed class Cleanup : ZhijiCard
    {
        public Cleanup() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self) { }
        protected override IEnumerable<DynamicVar> CanonicalVars => new[]
        {
            new BlockVar(5m, ValueProp.Move),
            new DynamicVar("Pulse", 3m)
        };
        protected override void OnCustomUpgrade()
        {
            if (DynamicVars.ContainsKey("Pulse")) DynamicVars["Pulse"].UpgradeValueBy(1m);
        }
        protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
        {
            CountUse(play);
            await CreatureCmd.GainBlock(Owner.Creature, DV("Block", 5), ValueProp.Move, play, false);
            St.AddPulse(DV("Pulse", Up ? 4 : 3));
            Sync(ctx);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace Zhiji
{
    public sealed class PulsePower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class HungerPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class FeastPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class StandPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 饥馑反噬：显示"下回合开始会因无休止饥饿失去多少生命"（数值 = 当前层数）
    public sealed class FaminePower : PowerModel
    {
        public override PowerType Type => PowerType.Debuff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 越割越深：每层让施加的流血额外 +50%
    public sealed class BloodThirstPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 召唤物：层数 = 剩余血量
    public sealed class DinerGuestPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 血之本能：每层使施加的流血额外 +1
    public sealed class ResonancePower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class LeftoverPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class CommandPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class DodgeChancePower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class BloodTidePower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public sealed class BleedInstinctPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 饥饿律动：每回合开始获得 3 点搏动/层
    public sealed class HungerRhythmPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 卑贱共鸣：每张卑贱卡额外 +1 层/层
    public sealed class VileResonancePower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 饥饿契约：每回合开始额外获得饥饿
    public sealed class HungerPactPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 暴食本能：红神形态每回合开始额外获得自助餐
    public sealed class GluttonInstinctPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 血之容器：流血上限 +X
    public sealed class BleedCapPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 血之回响：每回合第一次施加流血时抽 1 张牌
    public sealed class BloodEchoPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 卑贱循环：每回合开始获得卑贱之物
    public sealed class VileLoopPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 卑贱之物：叠满 10 层后自动获得一个额外回合（连动）
    public sealed class VileThingPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;

        public override bool ShouldTakeExtraTurn(Player player)
        {
            try
            {
                if (Amount >= 10)
                {
                    SetAmount(Amount - 10);
                    ZLog.W("vile thing: extra turn triggered, left=" + Amount);
                    return true;
                }
            }
            catch (Exception e) { ZLog.W("vile thing extra fail " + e.Message); }
            return false;
        }
    }

    public sealed class CravingPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    /// 收割值：红神形态下打牌累积，满 100 解锁大招「收获」
    public sealed class ReapPower : PowerModel
    {
        public override PowerType Type => PowerType.Buff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }

    public static class ZhijiPowers
    {
        static readonly HashSet<string> BellRuns = new HashSet<string>();
        public static void Sync(Player p, PlayerChoiceContext ctx = null)
        {
            if (p == null || p.Creature == null) return;
            var st = ZhijiStore.Of(p);
            ctx = ctx ?? new BlockingPlayerChoiceContext();
            Set<PulsePower>(p, ctx, st.Pulse);
            Set<HungerPower>(p, ctx, st.Hunger);
            Set<FeastPower>(p, ctx, st.Feast);
            Set<StandPower>(p, ctx, st.Stand);
            Set<CravingPower>(p, ctx, st.Craving);
            Set<ReapPower>(p, ctx, st.Reap);
            try { SetOrClear<DinerGuestPower>(p, ctx, st.MinionHp); } catch { }
            // 黄金寿衣：永久封印变身与饥饿
            try
            {
                bool shroud = false;
                try { shroud = p.Relics != null && p.Relics.Any(r => r is GoldenShroud); } catch { }
                // 开饭铃：换取召唤能力，本体最大生命 -25%（每局一次）
                try
                {
                    bool bell = p.Relics != null && p.Relics.Any(r => r is DinnerBell);
                    bool bellFirstRun = false;
                    if (bell)
                    {
                        try
                        {
                            var keyB = "bell|" + (p.RunState != null ? p.RunState.GetHashCode().ToString() : "?");
                            if (!BellRuns.Contains(keyB)) { BellRuns.Add(keyB); bellFirstRun = true; }
                        }
                        catch { bellFirstRun = !st.BellApplied; }
                    }
                    if (bell && bellFirstRun)
                    {
                        st.BellApplied = true;
                        int oldMax = (int)p.Creature.MaxHp;
                        int newMax = (int)System.Math.Ceiling(oldMax * 0.75);
                        p.Creature.SetMaxHpInternal(newMax);
                        try { int over = (int)p.Creature.CurrentHp - newMax; if (over > 0) p.Creature.LoseHpInternal(over, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); } catch { }
                        ZLog.W("dinner bell: max hp " + oldMax + " -> " + newMax + " (fragile body)");
                    }
                }
                catch (Exception eBell) { ZLog.W("bell fragile fail " + eBell.Message); }
                if (shroud)
                {
                    if (!st.GoldShroud)
                    {
                        ZLog.W("golden shroud detected -> seal transform");
                        st.GoldShroud = true;
                        st.Form = ZhijiForm.Normal;
                        st.Hunger = 0; st.Craving = 0;
                        try { Visuals.SetOverlayForm(false); ZLog.W("shroud: refresh gold overlay"); } catch { }
                    }
                }
                else if (st.GoldShroud)
                {
                    st.GoldShroud = false;
                    try { Visuals.SetOverlayForm(false); } catch { }
                }
            }
            catch { }
            // 饥馑反噬：幼年形态下，按当前饥饿预告下回合开始会掉的血
            try
            {
                int loss = 0;
                if (!st.GoldShroud && st.Form == ZhijiForm.Normal && st.Hunger > 0)
                    loss = (int)System.Math.Ceiling((double)p.Creature.MaxHp * (st.Hunger / 100.0) * 0.10);
                SetOrClear<FaminePower>(p, ctx, loss);
                if (loss > 0) ZLog.W("famine preview loss=" + loss);
            }
            catch (Exception eF) { ZLog.W("famine sync fail " + eF.Message); }
            try
            {
                ZLog.W("sync pulse=" + p.Creature.GetPowerAmount<PulsePower>()
                    + " hunger=" + p.Creature.GetPowerAmount<HungerPower>()
                    + " feast=" + p.Creature.GetPowerAmount<FeastPower>()
                    + " stand=" + p.Creature.GetPowerAmount<StandPower>());
            }
            catch (Exception e) { ZLog.W("sync log fail " + e.Message); }
            TryGrantEndCard(p, st);
            // 不再在 Sync 里扫描/移除牌堆（会造成残影与结算死锁）
        }

        // 饥饿满 100 时，本场战斗发放一张「童年的终结」（每场一次）
        static void TryGrantEndCard(Player p, ZhijiState st)
        {
            try
            {
                if (st.GoldShroud || st.Hunger < 100 || !st.EndCardAllowed) return;
                if (st.Form != ZhijiForm.Normal) return;      // 红神形态不发（避免红神手里出现幼年卡）
                // 任何牌堆里还有就不发（离开手牌会被移出这一局，所以这里通常查不到）
                // 手牌满 10 时不发，等有空位再发，避免"发出来就被挤掉"
                try
                {
                    int handCount = 0;
                    try { var hH = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, p); handCount = hH != null ? hH.Cards.Count : 0; } catch { }
                    if (handCount >= 10) { ZLog.W("hunger 100 but hand full -> wait"); return; }
                    var piles = new[] { MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust };
                    foreach (var pt in piles)
                    {
                        var pl = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(pt, p);
                        if (pl == null || pl.Cards == null) continue;
                        foreach (var c in pl.Cards)
                            if (c is ChildhoodEnd)
                            {
                                if (pt == MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand) { st.GotEndCard = true; return; }
                                // 上一次变身消耗后残留的卡：直接拿回手牌，而不是再发一张
                                _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(c,
                                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                                st.GotEndCard = true;
                                ZLog.W("childhood end re-granted from " + pt);
                                return;
                            }
                    }
                }
                catch { }
                // 每场战斗一次
                st.GotEndCard = true;
                st.EndCardAllowed = false;   // 本次饥饿周期已用掉，必须回落并重新攒满才会再给
                var state = p.Creature != null ? p.Creature.CombatState as MegaCrit.Sts2.Core.Combat.CombatState : null;
                if (state == null) return;
                var inst = state.CreateCard(MegaCrit.Sts2.Core.Models.ModelDb.Card<ChildhoodEnd>(), p);
                var t = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                _ = t.ContinueWith(x => ZLog.W("childhood end granted ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                ZLog.W("hunger 100 -> granted 童年的终结 (once per combat)");
            }
            catch (Exception e) { ZLog.W("grant end card fail " + e.Message); }
        }

        static void SetOrClear<T>(Player p, PlayerChoiceContext ctx, int value) where T : PowerModel
        {
            try
            {
                var cur = p.Creature.GetPower<T>();
                if (value > 0) { Set<T>(p, ctx, value); return; }
                if (cur != null) _ = PowerCmd.Remove<T>(p.Creature);
            }
            catch (Exception e) { ZLog.W("power clear fail " + typeof(T).Name + " " + e.Message); }
        }

        static void Set<T>(Player p, PlayerChoiceContext ctx, int value) where T : PowerModel
        {
            try
            {
                var cur = p.Creature.GetPower<T>();
                if (cur == null)
                {
                    if (value <= 0) return;
                    _ = PowerCmd.Apply<T>(ctx, p.Creature, (decimal)value, p.Creature, null, true);
                }
                else if (value > 0)
                {
                    var m = typeof(PowerModel).GetMethod("SetAmount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m != null)
                    {
                        if (m.GetParameters().Length == 2) m.Invoke(cur, new object[] { value, true });
                        else m.Invoke(cur, new object[] { value });
                    }
                    else ZLog.W("SetAmount not found");
                }
            }
            catch (Exception e) { ZLog.W("power sync fail " + typeof(T).Name + " " + e.Message); }
        }
    }
}

namespace Zhiji
{
    /// 流血：回合开始失去等同层数的生命
    public sealed class BleedPower : PowerModel
    {
        public override PowerType Type => PowerType.Debuff;
        public override PowerStackType StackType => PowerStackType.Counter;
    }
}

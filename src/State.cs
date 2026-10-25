using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace Zhiji
{
    public enum ZhijiForm { Normal = 0, RedGod = 1 }

    // 红神三形态：由幼年期"进攻/防守"操作次数决定
    public enum RedForm { War = 0, Guard = 1, Balance = 2 }

    /// 战斗内资源状态（按玩家分离）
    public class ZhijiState
    {
        public ZhijiForm Form = ZhijiForm.Normal;
        public int Pulse;        // 搏动
        public int Hunger;       // 无休止饥饿 0..100
        public int Feast;        // 自助餐
        public int Stand;        // 戏服替身 0..20
        public int Craving;      // 迫切饥饿
        public int Debt;         // 自助餐透支债务（回合结束扣血）
        public System.Collections.Generic.List<string> SavedHand = new System.Collections.Generic.List<string>();
        public int Reap;         // 收割值 0~100，红神形态累积
        public bool PendingRevert;
        public bool AutoPlayPending; // 大招结束后待回落
        public int LastBleed;    // 上回合敌人流血总量（用于"流血增长给搏动"）
        public bool GotEndCard;  // 本场战斗是否已发放「童年的终结」
        public bool EndCardAllowed = true;
        public bool GoldShroud;
        public int BleedTicks;
        public int FunnelTurn = -1;
        public int VileWatchTurn = -1;
        public bool VileCrownApplied;
        public int BleedEchoTurn = -1;
        public int LastInitTurn = -1;
        public int MinionHp;
        public int MinionMax;
        public int MinionHp2;
        public bool MinionDeathExploded;
        public bool Vassal;
        public bool RedHeartPending;   // 第二层「红神之心」事件待出现
        public bool RedHeartDone;      // 本局是否已完成红神之心事件
        public bool VassalFreeRevive;
        public bool DeathHandled;
        public int MinionMax2;
        public bool BellApplied;
        public bool DinerSeen;     // 本局是否已经把阴暗饭馆塞进第一层事件位   // 开饭铃的"本体变脆"是否已生效（每局一次）     // 召唤物血量（>0 时替本体挨打，并每回合攻击）   // 本场战斗流血触发次数（千刀成长用）   // 黄金寿衣：永远保持幼年形态 + 金色立绘  // 本次"饥饿周期"是否还可以获得（回落重置饥饿后重新置 true）
        public bool Restoring;   // 回落幼年、正在归还手牌（此期间禁止红神牌转化）
        public bool HpInit;      // 开局 72/132：第一次进战斗时把当前血量压到 72
        public int AtkCount;      // 对敌人出牌次数
        public int DefCount;      // 对自己/空放出牌次数
        public RedForm RForm = RedForm.Balance;   // 当前红神形态（变身时按 Atk/Def 决定）

        public void AddPulse(int n)
        {
            Pulse += n;
            Stand = Math.Min(20, Stand + System.Math.Max(1, n / 2));   // 替身积累减速：随搏动半速增长
            int gained = Pulse / 4;   // 平衡：3 -> 4，变身更慢
            if (gained > 0)
            {
                Pulse -= gained * 3;
                AddHunger(gained);
            }
        }

        public void AddHunger(int n)
        {
            Hunger = Math.Min(100, Hunger + n);
            int c = Hunger / 10;
            if (c > Craving) Craving = c;
        }

        public void AddFeast(int n) { Feast += n; }

        /// 召唤/补给：加召唤物血量（默认上限 30，会由调用处传入实际上限）
        public void Summon(int hp, int cap = 30)
        {
            MinionHp = System.Math.Min(cap, MinionHp + hp);
        }

        public void Evolve(Player p = null)
        {
            Form = ZhijiForm.RedGod;
            // 变身强度拆分：只转化一半搏动与四分之一替身，其余强度靠遗物/事件补
            Feast += Pulse / 2 + Stand / 4;
            // 遗物：变身赠餐（统一在这里结算，卡牌与调试路径一致）
            try
            {
                if (p != null && p.Relics != null)
                {
                    if (p.Relics.Any(r => r is FleshStilts)) { Feast += 8; ZLog.W("evolve relic stilts +8"); }
                    if (p.Relics.Any(r => r is EndlessPlate)) { Feast += 6; ZLog.W("evolve relic plate +6"); }
                    if (p.Relics.Any(r => r is CrimsonVisor)) { Feast += 10; ZLog.W("evolve relic visor +10"); }
                }
            }
            catch { }
            Pulse = 0;
            Stand = 0;
        }
    }

    public static class ZhijiStore
    {
        static readonly Dictionary<Player, ZhijiState> Map = new Dictionary<Player, ZhijiState>();

        public static ZhijiState Of(Player p)
        {
            if (p == null) return new ZhijiState();
            ZhijiState s;
            if (!Map.TryGetValue(p, out s))
            {
                s = new ZhijiState();
                Map[p] = s;
            }
            return s;
        }

        public static void Reset(Player p) { if (p != null) Map.Remove(p); }
        public static void ResetAll() { Map.Clear(); }

        // 整局只生效一次的标记（按 Player 实例，新开一局自然重置）
        static readonly HashSet<object> OnceFlags = new HashSet<object>();
        public static bool HasOnce(object player, string key)
        {
            if (player == null) return true;
            return OnceFlags.Contains(player.GetHashCode() + "|" + key);
        }

        public static bool Once(object player, string key)
        {
            if (player == null) return true;
            var k = player.GetHashCode() + "|" + key;
            if (OnceFlags.Contains(k)) return true;
            OnceFlags.Add(k);
            return false;
        }

        public static ZhijiForm CurrentForm()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var st = cm != null ? cm.DebugOnlyGetState() : null;
                if (st != null && st.Players != null && st.Players.Count > 0)
                    return Of(st.Players[0]).Form;
            }
            catch { }
            return ZhijiForm.Normal;
        }
    }

    /// 骰子伤害系统：1D6 + 搏动层数
    /// 5+ 爆击 ×1.5 / 3-4 正常 / 1-2 压制 ×0.5
    public static class Dice
    {
        static readonly Random Rng = new Random();

        // 1d12：骰面 1~12，搏动最多 +4，点数上限 12
        // 伤害倍率从 12%（1 点）线性到 150%（12 点）
        public static float Roll(int pulseBonus, out int value, out string label)
        {
            int die = Rng.Next(1, 13);
            int bonus = System.Math.Min(4, System.Math.Max(0, pulseBonus));
            value = System.Math.Min(12, die + bonus);
            float mult = 0.12f + (value - 1) * (1.38f / 11f);
            int pct = (int)System.Math.Round(mult * 100f);
            if (value >= 10) label = "爆击 " + pct + "%";
            else if (value >= 5) label = "命中 " + pct + "%";
            else label = "压制 " + pct + "%";
            ZLog.W("骰点 " + value + "(" + die + "+" + bonus + ") " + label + " x" + mult.ToString("0.00"));
            Visuals.ShowDice(value, label);
            return mult;
        }
    }

}

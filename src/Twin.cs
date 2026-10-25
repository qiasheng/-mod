using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Zhiji
{
    /// 成对卡：获得一张时同步获得配对版本；用掉任意一张只销毁"它自己的"那张配对
    public static class Twin
    {
        // 战斗开始时按牌堆里「驱力」的数量补齐「驱力·援护」
        public static void SetupPairs(Player p)
        {
            try
            {
                if (p == null) return;
                int drives = CountInPiles(p, typeof(Drive));
                int guards = CountInPiles(p, typeof(DriveGuard));
                int need = drives - guards;
                if (need <= 0) return;
                var state = p.Creature != null ? (MegaCrit.Sts2.Core.Combat.CombatState)p.Creature.CombatState : null;
                if (state == null) return;
                for (int i = 0; i < need; i++)
                {
                    var inst = state.CreateCard(ModelDb.Card<DriveGuard>(), p);
                    var t = CardPileCmd.Add(inst, PileType.Hand, CardPilePosition.Top, null, false);
                    _ = t.ContinueWith(x => ZLog.W("twin added hand ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    System.Threading.Thread.Sleep(20);
                }
                ZLog.W("twin setup drive=" + drives + " guard+" + need);
            }
            catch (Exception e) { ZLog.W("twin setup fail " + e.Message); }
        }

        public static int CountInPiles(Player p, Type cardType)
        {
            int n = 0;
            foreach (var t in new[] { PileType.Hand, PileType.Draw, PileType.Discard })
            {
                try
                {
                    var pile = CardPile.Get(t, p);
                    if (pile == null) continue;
                    foreach (var c in pile.Cards) if (c != null && c.GetType() == cardType) n++;
                }
                catch { }
            }
            return n;
        }

        static bool _removing;
        // 实例级配对表（主卡实例 → 镜像实例），避免"删除时找不到"的时序问题
        static readonly List<KeyValuePair<object, object>> Links = new List<KeyValuePair<object, object>>();
        // 记录"由配对生成的临时卡"实例，避免误删牌组里的真卡
        static readonly HashSet<object> Generated = new HashSet<object>();

        static bool IsGenerated(object card)
        {
            try { return card != null && Generated.Contains(card); } catch { return false; }
        }

        /// 任意途径离开手牌（打出/弃掉/消耗）时，同步移除它的配对
        // 只有"主卡"会生成配对（避免配对卡再生成主卡 → 无限刷牌）
        static readonly Dictionary<Type, Type> SpawnFrom = new Dictionary<Type, Type>
        {
            { typeof(Drive), typeof(DriveGuard) },
            { typeof(PulseAid), typeof(PulseStrike) },
            { typeof(Escape), typeof(EscapeCover) },
        };

        static readonly Dictionary<Type, Type> Pairs = new Dictionary<Type, Type>
        {
            { typeof(Drive), typeof(DriveGuard) },
            { typeof(DriveGuard), typeof(Drive) },
            { typeof(PulseAid), typeof(PulseStrike) },
            { typeof(PulseStrike), typeof(PulseAid) },
            { typeof(Escape), typeof(EscapeCover) },
            { typeof(EscapeCover), typeof(Escape) },
        };

        public static Type PartnerOf(object card)
        {
            if (card == null) return null;
            Type t;
            return Pairs.TryGetValue(card.GetType(), out t) ? t : null;
        }

        /// 卡牌进入手牌时，同步造出它的配对版本
        // 配对卡在"进入牌组"时生成一次（不再每次抽牌都生成，避免手牌压力）
        public static void OnEnterDeck(Player p, object card)
        {
            Type partnerType;
            if (card == null || !SpawnFrom.TryGetValue(card.GetType(), out partnerType)) return;
            try
            {
                int have = 0;
                var deck = CardPile.Get(PileType.Deck, p);
                if (deck != null) foreach (var c in deck.Cards) if (c != null && c.GetType() == partnerType) have++;
                int want = 0;
                if (deck != null) foreach (var c in deck.Cards) if (c != null && c.GetType() == card.GetType()) want++;
                if (have >= want) return;
                var inst = MegaCrit.Sts2.Core.Models.ModelDb.GetById<MegaCrit.Sts2.Core.Models.CardModel>(
                    MegaCrit.Sts2.Core.Models.ModelDb.GetId(partnerType));
                if (inst == null) { ZLog.W("pair deck: model not found " + partnerType.Name); return; }
                var rs = p.RunState;
                if (rs == null) { ZLog.W("pair deck: no runstate"); return; }
                var created = rs.CreateCard(inst, p);
                if (created == null) return;
                _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(created, PileType.Deck, CardPilePosition.Top, null, false);
                ZLog.W("pair added to deck " + partnerType.Name);
            }
            catch (Exception e) { ZLog.W("pair deck fail " + e.Message); }
        }

        public static void OnEnterHand(Player p, object card)
        {
            if (!CombatAlive()) return;
            Type partnerType;
            if (card == null || !SpawnFrom.TryGetValue(card.GetType(), out partnerType)) return;
            if (partnerType == null || p == null) return;
            try
            {
                var state = p.Creature != null ? p.Creature.CombatState as MegaCrit.Sts2.Core.Combat.CombatState : null;
                if (state == null) return;
                var canonical = MegaCrit.Sts2.Core.Models.ModelDb.GetById<MegaCrit.Sts2.Core.Models.CardModel>(
                    MegaCrit.Sts2.Core.Models.ModelDb.GetId(partnerType));
                if (canonical == null) return;
                var inst = state.CreateCard(canonical, p);
                Generated.Add(inst);
                Links.Add(new KeyValuePair<object, object>(card, inst));
                // 手牌上限 10：满了就进弃牌堆，否则 HandPosHelper 会抛异常导致卡牌残影
                int handCount = 0;
                try { var hp = CardPile.Get(PileType.Hand, p); if (hp != null) handCount = hp.Cards.Count; } catch { }
                var targetPile = handCount >= 10 ? PileType.Discard : PileType.Hand;
                var t = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst, targetPile, CardPilePosition.Top, null, false);
                _ = t.ContinueWith(x => ZLog.W("pair added " + partnerType.Name + " to " + targetPile + " ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
            }
            catch (Exception e) { ZLog.W("pair add fail " + e.Message); }
        }

        /// 主牌离开手牌 → 只移除"它生成的那张临时配对"，绝不碰牌组真卡
        static bool InHand(Player p, object card)
        {
            try
            {
                var pile = CardPile.Get(PileType.Hand, p);
                if (pile == null) return false;
                foreach (var c in pile.Cards.ToList()) if (ReferenceEquals(c, card)) return true;
            }
            catch { }
            return false;
        }

        /// 每帧/每次状态同步时兜底：主卡不在手牌了，就销毁它对应的镜像
        static bool CombatAlive()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null) return false;
                if (cm.IsEnding || cm.IsOverOrEnding) return false;
                return cm.IsInProgress || cm.IsStarting || cm.CurrentCombatId != null;
            }
            catch { return false; }
        }

        public static void Sweep(Player p)
        {
            try
            {
                if (!CombatAlive()) return;   // 战斗结算/收尾阶段不要再动牌堆，避免卡住结算
                for (int i = Links.Count - 1; i >= 0; i--)
                {
                    var main = Links[i].Key;
                    var mirror = Links[i].Value;
                    if (main == null || mirror == null) { Links.RemoveAt(i); continue; }
                    bool mAlive = mirror is MegaCrit.Sts2.Core.Models.CardModel mc && mc.Pile != null;
                    if (!mAlive) { Links.RemoveAt(i); continue; }
                    if (InHand(p, main)) continue;
                    Generated.Remove(mirror);
                    try
                    {
                        var mp = ((MegaCrit.Sts2.Core.Models.CardModel)mirror).Pile;
                        if (mp != null) mp.RemoveInternal((MegaCrit.Sts2.Core.Models.CardModel)mirror, false);
                    }
                    catch { }
                    Links.RemoveAt(i);
                    ZLog.W("sweep destroyed mirror of " + ((MegaCrit.Sts2.Core.Models.CardModel)main).Id.Entry);
                }
            }
            catch (Exception e) { ZLog.W("sweep fail " + e.Message); }
        }

        public static MegaCrit.Sts2.Core.Models.CardModel FindInHand(Player p, Type type)
        {
            try
            {
                var pile = CardPile.Get(PileType.Hand, p);
                if (pile == null) return null;
                foreach (var c in pile.Cards.ToList())
                    if (c != null && c.GetType() == type) return c;
            }
            catch { }
            return null;
        }

        /// 主卡打出后：销毁手牌里它生成的那张镜像
        public static void DestroyMirror(Player p, Type mirrorType)
        {
            try
            {
                var m = FindInHand(p, mirrorType);
                if (m == null) return;
                Generated.Remove(m);
                var pile = CardPile.Get(PileType.Hand, p);
                if (pile != null && pile.Cards.Contains(m)) pile.RemoveInternal(m, false);
                ZLog.W("mirror destroyed " + mirrorType.Name);
            }
            catch (Exception e) { ZLog.W("destroy mirror fail " + e.Message); }
        }

        /// 任意一张离开手牌 → 手牌里与它成对的另一张一起消失（只动"手牌"，不碰牌组/抽牌堆）
        public static void OnLeftHand(Player p, object card)
        {
            if (_removing || card == null) return;
            if (!CombatAlive()) return;
            var partnerType = PartnerOf(card);
            if (partnerType == null) return;
            try
            {
                _removing = true;
                RemovePartnerInHand(p, partnerType);
            }
            finally { _removing = false; }
        }

        static void RemovePartnerInHand(Player p, Type partnerType)
        {
            try
            {
                var pile = CardPile.Get(PileType.Hand, p);
                if (pile == null) return;
                foreach (var c in pile.Cards.ToList())
                {
                    if (c == null || c.GetType() != partnerType) continue;
                    Generated.Remove(c);
                    _ = CardPileCmd.RemoveFromCombat(c, false);
                    ZLog.W("twin consumed " + partnerType.Name + " from Hand");
                    return;
                }
            }
            catch (Exception e) { ZLog.W("twin remove fail " + e.Message); }
        }

        // 用掉一张，只销毁一张配对（优先手牌，其次抽牌堆，再弃牌堆）
        public static void RemovePartner(Player p, Type partnerType)
        {
            try
            {
                if (p == null) return;
                foreach (var t in new[] { PileType.Hand, PileType.Draw, PileType.Discard })
                {
                    var pile = CardPile.Get(t, p);
                    if (pile == null) continue;
                    foreach (var c in pile.Cards.ToList())
                    {
                        if (c == null || c.GetType() != partnerType) continue;
                        _ = CardPileCmd.RemoveFromCombat(c, false);
                        ZLog.W("twin consumed " + partnerType.Name + " from " + t);
                        return;
                    }
                }
            }
            catch (Exception e) { ZLog.W("twin remove fail " + e.Message); }
        }
    }
}

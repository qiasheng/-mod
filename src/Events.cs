using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Zhiji
{
    /// 阴暗饭馆：借用第一层一个原生事件的外观/场景，替换标题、描述与选项。
    public static class DinerEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.WoodCarvings";

        static LocString L(string key) => new LocString("events", key);

        public static LocString Title() => L("DARK_DINER.title");
        public static LocString InitialDescription() => L("DARK_DINER.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var eat = new EventOption(ev, () => Eat(ev), L("DARK_DINER.options.EAT.title"), L("DARK_DINER.options.EAT.description"), "EAT", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("DARK_DINER.options.LEAVE.title"), L("DARK_DINER.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { eat, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
                else ZLog.W("diner: SetEventFinished not found");
            }
            catch (Exception e) { ZLog.W("diner finish fail " + e.Message); }
        }

        static readonly System.Collections.Generic.Dictionary<EventModel, int> _pages = new System.Collections.Generic.Dictionary<EventModel, int>();
        const int PageSize = 3;

        // 只卖流血基础卡；补强卡（千刀/引爆/血宴/血雾/越割越深/回响）去路上拿
        static readonly (string key, string id)[] ShopItems = new (string, string)[]
        {
            ("DARK_DINER.options.BUY_SLIT", "SLIT_THROAT"),
            ("DARK_DINER.options.BUY_LET", "BLOOD_LET"),
            ("DARK_DINER.options.BUY_DEBT", "BLOOD_DEBT"),
        };

        public static void ShowShop(EventModel ev)
        {
            try
            {
                int page = 0;
                _pages.TryGetValue(ev, out page);
                int pages = (ShopItems.Length + PageSize - 1) / PageSize;
                if (page < 0) page = 0;
                if (page >= pages) page = pages - 1;
                _pages[ev] = page;

                var opts = new List<EventOption>();
                // 只保留「打开商店」和「离开」两个按钮：买卡交给引擎商人界面
                opts.Add(new EventOption(ev, async () =>
                {
                    try
                    {
                        var rmO = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsO = rmO != null ? rmO.DebugOnlyGetState() : null;
                        var plO = rsO != null && rsO.Players != null && rsO.Players.Count > 0 ? rsO.Players[0] : null;
                        Finish(ev, "DARK_DINER.pages.SHOP_FINISH.description");
                        ZhijiPatcher.RequestDotShop();     // 只让这一家店卖 DOT 卡
                        if (plO != null) ZhijiPatcher.OpenDotShop(plO);
                        await Task.CompletedTask;
                    }
                    catch (Exception eO) { ZLog.W("diner open shop fail " + eO.Message); }
                }, L("DARK_DINER.options.OPEN_SHOP.title"), L("DARK_DINER.options.OPEN_SHOP.description"), "OPEN_SHOP", System.Array.Empty<IHoverTip>()));
                opts.Add(new EventOption(ev, () => Leave(ev), L("DARK_DINER.options.LEAVE.title"), L("DARK_DINER.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>()));

                var m = typeof(EventModel).GetMethod("SetEventState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null)
                {
                    var desc = L("DARK_DINER.pages.SHOP.description");
                    m.Invoke(ev, new object[] { desc, opts });
                    ZLog.W("diner shop page " + (page + 1) + "/" + pages + " options=" + opts.Count);
                }
                else ZLog.W("diner: SetEventState not found");
            }
            catch (Exception e) { ZLog.W("diner shop fail " + e.Message); }
        }

        static EventOption Buy(EventModel ev, string key, Func<Task> give, IHoverTip[] tips = null)
        {
            return new EventOption(ev, async () =>
            {
                try
                {
                    var pl = ev.Owner;
                    if (pl == null) return;
                    if (pl.Gold < 30) { ZLog.W("diner: not enough points"); ShowShop(ev); return; }
                    pl.Gold -= 30;
                    await give();
                    ZLog.W("diner: bought -> gold=" + pl.Gold + " deck=" + (pl.Deck != null ? pl.Deck.Cards.Count : -1));
                }
                catch (Exception e) { ZLog.W("diner buy fail " + e.Message); }
                ShowShop(ev);
            }, L(key + ".title"), L(key + ".description"), key, tips ?? System.Array.Empty<IHoverTip>());
        }

        static async Task GiveCard(EventModel ev, CardModel proto)
        {
            try
            {
                var pl = ev.Owner;
                if (pl == null || proto == null) return;
                var rmE = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rsE = rmE != null ? rmE.DebugOnlyGetState() : null;
                if (rsE == null) { ZLog.W("diner: no runstate"); return; }
                var inst = rsE.CreateCard(proto, pl);   // 注册进 RunState + 绑定主人
                if (inst == null) return;
                // 必须走引擎的入牌组流程（会写运行历史、楼层、Hook），直接 AddInternal 会在离开事件时崩
                await CardPileCmd.Add(inst, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck,
                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                ZLog.W("diner: added card " + inst.Id.Entry + " deck=" + (pl.Deck != null ? pl.Deck.Cards.Count : -1));
            }
            catch (Exception e) { ZLog.W("diner add card fail " + e.Message); }
            await Task.CompletedTask;
        }

        static async Task Eat(EventModel ev)
        {
            try
            {
                var pl = ev.Owner;
                if (pl != null)
                {
                    try { await RelicCmd.Obtain<GoldenShroud>(pl); ZLog.W("diner: golden shroud obtained"); } catch (Exception e) { ZLog.W("diner relic fail " + e.Message); }
                    // 换牌：红神卡移出本局；幼年体系卡（喂饥饿的那批）每张换 30 点数；DOT 卡保留
                    int converted = 0;
                    try
                    {
                        var deck = pl.Deck;
                        if (deck != null && deck.Cards != null)
                        {
                            var outCards = new List<CardModel>();
                            foreach (var c in deck.Cards)
                            {
                                var z = c as ZhijiCard;
                                if (z == null) continue;                 // 原版卡（打击/防御等）保留
                                if (z.RedGod) { outCards.Add(c); continue; }
                                if (z.DotStyle) continue;
                                outCards.Add(c); converted++;
                            }
                            if (outCards.Count > 0)
                            {
                                await CardPileCmd.RemoveFromDeck(outCards, false);
                                ZLog.W("diner: removed " + outCards.Count + " cards (kid converted=" + converted + ")");
                            }
                        }
                    }
                    catch (Exception e) { ZLog.W("diner remove fail " + e.Message); }
                    try
                    {
                        int pts = converted * 35;
                        pl.Gold += pts;
                        ZLog.W("diner: points +" + pts + " (gold=" + pl.Gold + ")");
                    }
                    catch (Exception e) { ZLog.W("diner points fail " + e.Message); }
                    try
                    {
                        var st = ZhijiStore.Of(pl);
                        st.GoldShroud = true; st.Form = ZhijiForm.Normal; st.Hunger = 0; st.Craving = 0;
                    }
                    catch { }
                }
            }
            catch (Exception e) { ZLog.W("diner eat fail " + e.Message); }
            ShowShop(ev);
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            Finish(ev, "DARK_DINER.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }

    // ===== 红神的摇篮（用户设计稿）=====
    public static class RedCradleEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.TabletOfTruth";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("RED_CRADLE.title");
        public static LocString InitialDescription() => L("RED_CRADLE.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var lie = new EventOption(ev, () => Lie(ev), L("RED_CRADLE.options.LIE.title"), L("RED_CRADLE.options.LIE.description"), "LIE", System.Array.Empty<IHoverTip>());
            var tear = new EventOption(ev, () => Tear(ev), L("RED_CRADLE.options.TEAR.title"), L("RED_CRADLE.options.TEAR.description"), "TEAR", System.Array.Empty<IHoverTip>());
            var contract = new EventOption(ev, () => Contract(ev), L("RED_CRADLE.options.CONTRACT.title"), L("RED_CRADLE.options.CONTRACT.description"), "CONTRACT", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("RED_CRADLE.options.LEAVE.title"), L("RED_CRADLE.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { lie, tear, contract, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("red cradle finish fail " + e.Message); }
        }

        public static Player PlayerOfPublic(EventModel ev) => PlayerOf(ev);
        public static void AddCardsPublic(Player pl, Func<CardModel, bool> filter, int count) => AddRandomToDeck(pl, filter, count);

        static Player PlayerOf(EventModel ev)
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                return rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
            }
            catch { return null; }
        }

        static void AddRandomToDeck(Player pl, Func<CardModel, bool> filter, int count)
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null || pl == null) return;
                var pool = new List<CardModel>();
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    if (c == null) continue;
                    var z = c as ZhijiCard;
                    if (z != null && z.RedGod) continue;
                    if (c is ChildhoodEnd || c is HungerFeast) continue;
                    if (!filter(c)) continue;
                    pool.Add(c);
                }
                var rng = new System.Random();
                for (int i = 0; i < count && pool.Count > 0; i++)
                {
                    var proto = pool[rng.Next(pool.Count)];
                    var inst = rs.CreateCard(proto, pl);
                    if (inst == null) continue;
                    _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst,
                        MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck,
                        MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                    ZLog.W("red cradle added card " + inst.Id.Entry);
                }
            }
            catch (Exception e) { ZLog.W("red cradle add cards fail " + e.Message); }
        }

        static async Task Lie(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<CrimsonCradle>(pl)
                        .ContinueWith(x => ZLog.W("red cradle relic ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    ZhijiStore.Of(pl).AddHunger(30);
                    ZLog.W("red cradle: lie, hunger +30");
                }
            }
            catch (Exception e) { ZLog.W("red cradle lie fail " + e.Message); }
            Finish(ev, "RED_CRADLE.pages.LIE.description");
            await Task.CompletedTask;
        }

        static async Task Tear(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    pl.Gold += 120;
                    AddRandomToDeck(pl, c => c is SlitThroat || c is BloodLet || c is BloodDebt, 2);
                    ZLog.W("red cradle: tear, gold=120 +2 dot");
                }
            }
            catch (Exception e) { ZLog.W("red cradle tear fail " + e.Message); }
            Finish(ev, "RED_CRADLE.pages.TEAR.description");
            await Task.CompletedTask;
        }

        static async Task Contract(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    pl.Gold += 75;
                    AddRandomToDeck(pl, c => c is ZhijiCard z && !z.DotStyle, 2);
                    ZLog.W("red cradle: contract, gold=75 +2 kid cards");
                }
            }
            catch (Exception e) { ZLog.W("red cradle contract fail " + e.Message); }
            Finish(ev, "RED_CRADLE.pages.CONTRACT.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.15);
                    pl.Creature.HealInternal(heal);
                    pl.Gold += 50;
                    ZLog.W("red cradle: leave heal=" + heal + " gold=50");
                }
            }
            catch (Exception e) { ZLog.W("red cradle leave fail " + e.Message); }
            Finish(ev, "RED_CRADLE.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 红神教会（额外事件，可获遗物/卡）=====
    public static class RedChurchEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.DollRoom";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("RED_CHURCH.title");
        public static LocString InitialDescription() => L("RED_CHURCH.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var offer = new EventOption(ev, () => Offer(ev), L("RED_CHURCH.options.OFFER.title"), L("RED_CHURCH.options.OFFER.description"), "OFFER", System.Array.Empty<IHoverTip>());
            var listen = new EventOption(ev, () => Listen(ev), L("RED_CHURCH.options.LISTEN.title"), L("RED_CHURCH.options.LISTEN.description"), "LISTEN", System.Array.Empty<IHoverTip>());
            var smash = new EventOption(ev, () => Smash(ev), L("RED_CHURCH.options.SMASH.title"), L("RED_CHURCH.options.SMASH.description"), "SMASH", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("RED_CHURCH.options.LEAVE.title"), L("RED_CHURCH.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { offer, listen, smash, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("red church finish fail " + e.Message); }
        }

        static Player PlayerOf(EventModel ev) => RedCradleEvent.PlayerOfPublic(ev);

        static void AddCards(Player pl, Func<CardModel, bool> filter, int count) => RedCradleEvent.AddCardsPublic(pl, filter, count);

        static async Task Offer(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<Stigmata>(pl)
                        .ContinueWith(x => ZLog.W("red church relic ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    if (pl.Creature != null)
                    {
                        int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                        ZLog.W("red church: offer, max hp -" + loss);
                    }
                }
            }
            catch (Exception e) { ZLog.W("red church offer fail " + e.Message); }
            Finish(ev, "RED_CHURCH.pages.OFFER.description");
            await Task.CompletedTask;
        }

        static async Task Listen(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    AddCards(pl, c => c is ZhijiCard z && z.DotStyle, 2);
                    ZLog.W("red church: listen, +2 dot");
                }
            }
            catch (Exception e) { ZLog.W("red church listen fail " + e.Message); }
            Finish(ev, "RED_CHURCH.pages.LISTEN.description");
            await Task.CompletedTask;
        }

        static async Task Smash(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null)
                {
                    pl.Gold += 100;
                    AddCards(pl, c => c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare && !(c is ChildhoodEnd) && !(c is HungerFeast), 1);
                    ZLog.W("red church: smash, +100 gold +1 rare");
                }
            }
            catch (Exception e) { ZLog.W("red church smash fail " + e.Message); }
            Finish(ev, "RED_CHURCH.pages.SMASH.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = PlayerOf(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                    ZLog.W("red church: leave heal=" + heal);
                }
            }
            catch (Exception e) { ZLog.W("red church leave fail " + e.Message); }
            Finish(ev, "RED_CHURCH.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 猩红祭坛（红神强化事件）=====
    public static class CrimsonAltarEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.WhisperingHollow";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("CRIMSON_ALTAR.title");
        public static LocString InitialDescription() => L("CRIMSON_ALTAR.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var feast = new EventOption(ev, () => Feed(ev), L("CRIMSON_ALTAR.options.FEED.title"), L("CRIMSON_ALTAR.options.FEED.description"), "FEED", System.Array.Empty<IHoverTip>());
            var fire = new EventOption(ev, () => Fire(ev), L("CRIMSON_ALTAR.options.FIRE.title"), L("CRIMSON_ALTAR.options.FIRE.description"), "FIRE", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("CRIMSON_ALTAR.options.LEAVE.title"), L("CRIMSON_ALTAR.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { feast, fire, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("altar finish fail " + e.Message); }
        }

        static async Task Feed(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    ZhijiStore.Of(pl).AddFeast(8);
                    if (pl.Creature != null)
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - 5));
                    ZLog.W("crimson altar: feed, feast +8, max hp -5");
                }
            }
            catch (Exception e) { ZLog.W("altar feed fail " + e.Message); }
            Finish(ev, "CRIMSON_ALTAR.pages.FEED.description");
            await Task.CompletedTask;
        }

        static async Task Fire(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    RedCradleEvent.AddCardsPublic(pl, c => c is ZhijiCard z && z.DotStyle, 2);
                    ZhijiStore.Of(pl).AddHunger(20);
                    ZLog.W("crimson altar: fire, +2 dot, hunger +20");
                }
            }
            catch (Exception e) { ZLog.W("altar fire fail " + e.Message); }
            Finish(ev, "CRIMSON_ALTAR.pages.FIRE.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("altar leave fail " + e.Message); }
            Finish(ev, "CRIMSON_ALTAR.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 卑贱交易所（卑贱体系专属事件）=====
    public static class VileMarketEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.SlipperyBridge";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("VILE_MARKET.title");
        public static LocString InitialDescription() => L("VILE_MARKET.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var buy = new EventOption(ev, () => Buy(ev), L("VILE_MARKET.options.BUY.title"), L("VILE_MARKET.options.BUY.description"), "BUY", System.Array.Empty<IHoverTip>());
            var pledge = new EventOption(ev, () => Pledge(ev), L("VILE_MARKET.options.PLEDGE.title"), L("VILE_MARKET.options.PLEDGE.description"), "PLEDGE", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("VILE_MARKET.options.LEAVE.title"), L("VILE_MARKET.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { buy, pledge, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("vile market finish fail " + e.Message); }
        }

        static async Task Buy(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Gold >= 60)
                {
                    pl.Gold -= 60;
                    RedCradleEvent.AddCardsPublic(pl, c => c is VileBrand || c is VileWhisper || c is VileOffering || c is VileEnd, 2);
                    ZLog.W("vile market: bought 2 vile cards, gold=" + pl.Gold);
                }
                else ZLog.W("vile market: not enough gold");
            }
            catch (Exception e) { ZLog.W("vile market buy fail " + e.Message); }
            Finish(ev, "VILE_MARKET.pages.BUY.description");
            await Task.CompletedTask;
        }

        static async Task Pledge(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    if (pl.Creature != null)
                    {
                        int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                        ZLog.W("vile market: pledge -maxhp " + loss);
                    }
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<VileCrown>(pl)
                        .ContinueWith(x => ZLog.W("vile market crown ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                }
            }
            catch (Exception e) { ZLog.W("vile market pledge fail " + e.Message); }
            Finish(ev, "VILE_MARKET.pages.PLEDGE.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("vile market leave fail " + e.Message); }
            Finish(ev, "VILE_MARKET.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 血之馈赠（DOT 体系专属事件）=====
    public static class BloodGiftEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.SunkenStatue";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("BLOOD_GIFT.title");
        public static LocString InitialDescription() => L("BLOOD_GIFT.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var offer = new EventOption(ev, () => Sample(ev), L("BLOOD_GIFT.options.SAMPLE.title"), L("BLOOD_GIFT.options.SAMPLE.description"), "SAMPLE", System.Array.Empty<IHoverTip>());
            var shroud = new EventOption(ev, () => Wear(ev), L("BLOOD_GIFT.options.WEAR.title"), L("BLOOD_GIFT.options.WEAR.description"), "WEAR", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("BLOOD_GIFT.options.LEAVE.title"), L("BLOOD_GIFT.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { offer, shroud, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("blood gift finish fail " + e.Message); }
        }

        static async Task Offer(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                    RedCradleEvent.AddCardsPublic(pl, c => c is ZhijiCard z && z.DotStyle, 2);
                    pl.Gold += 20;
                    ZLog.W("blood gift: offer -maxhp " + loss + " +2 dot +20 gold");
                }
            }
            catch (Exception e) { ZLog.W("blood gift offer fail " + e.Message); }
            Finish(ev, "BLOOD_GIFT.pages.OFFER.description");
            await Task.CompletedTask;
        }

        static async Task Sample(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BleedBoard>(pl)
                        .ContinueWith(x => ZLog.W("blood gift board ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                }
            }
            catch (Exception e) { ZLog.W("blood gift sample fail " + e.Message); }
            Finish(ev, "BLOOD_GIFT.pages.SAMPLE.description");
            await Task.CompletedTask;
        }

        static async Task Wear(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    if (pl.Creature != null)
                    {
                        int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.15);
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                    }
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<GoldenShroud>(pl)
                        .ContinueWith(x => ZLog.W("blood gift shroud ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    try { var stS = ZhijiStore.Of(pl); stS.GoldShroud = true; stS.Form = ZhijiForm.Normal; } catch { }
                }
            }
            catch (Exception e) { ZLog.W("blood gift wear fail " + e.Message); }
            Finish(ev, "BLOOD_GIFT.pages.WEAR.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("blood gift leave fail " + e.Message); }
            Finish(ev, "BLOOD_GIFT.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 饥饿剧场（能力牌/金币事件）=====
    public static class HungerTheaterEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.TeaMaster";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("HUNGER_THEATER.title");
        public static LocString InitialDescription() => L("HUNGER_THEATER.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var act = new EventOption(ev, () => Act(ev), L("HUNGER_THEATER.options.ACT.title"), L("HUNGER_THEATER.options.ACT.description"), "ACT", System.Array.Empty<IHoverTip>());
            var watch = new EventOption(ev, () => Watch(ev), L("HUNGER_THEATER.options.WATCH.title"), L("HUNGER_THEATER.options.WATCH.description"), "WATCH", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("HUNGER_THEATER.options.LEAVE.title"), L("HUNGER_THEATER.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { act, watch, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("theater finish fail " + e.Message); }
        }

        static async Task Act(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    RedCradleEvent.AddCardsPublic(pl, c => c is ZhijiCard && (c as ZhijiCard) != null && c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Power, 1);
                    ZhijiStore.Of(pl).AddHunger(15);
                    ZLog.W("hunger theater: act +1 power card +15 hunger");
                }
            }
            catch (Exception e) { ZLog.W("theater act fail " + e.Message); }
            Finish(ev, "HUNGER_THEATER.pages.ACT.description");
            await Task.CompletedTask;
        }

        static async Task Watch(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    pl.Gold += 60;
                    if (pl.Creature != null)
                    {
                        int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.05);
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                    }
                    ZLog.W("hunger theater: watch +60 gold -5% maxhp");
                }
            }
            catch (Exception e) { ZLog.W("theater watch fail " + e.Message); }
            Finish(ev, "HUNGER_THEATER.pages.WATCH.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("theater leave fail " + e.Message); }
            Finish(ev, "HUNGER_THEATER.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 战备会议（红神纹章三选一）=====
    public static class WarCouncilEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.RanwidTheElder";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("WAR_COUNCIL.title");
        public static LocString InitialDescription() => L("WAR_COUNCIL.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var war = new EventOption(ev, () => War(ev), L("WAR_COUNCIL.options.WAR.title"), L("WAR_COUNCIL.options.WAR.description"), "WAR", System.Array.Empty<IHoverTip>());
            var guard = new EventOption(ev, () => Guard(ev), L("WAR_COUNCIL.options.GUARD.title"), L("WAR_COUNCIL.options.GUARD.description"), "GUARD", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("WAR_COUNCIL.options.LEAVE.title"), L("WAR_COUNCIL.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { war, guard, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("war council finish fail " + e.Message); }
        }

        static void PayHp(Player pl, double pct)
        {
            try
            {
                if (pl == null || pl.Creature == null) return;
                int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * pct);
                pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                ZLog.W("war council -maxhp " + loss);
            }
            catch { }
        }

        static async Task War(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    PayHp(pl, 0.05);
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<WarSigil>(pl)
                        .ContinueWith(x => ZLog.W("war council war sigil ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                }
            }
            catch (Exception e) { ZLog.W("war council war fail " + e.Message); }
            Finish(ev, "WAR_COUNCIL.pages.WAR.description");
            await Task.CompletedTask;
        }

        static async Task Guard(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    PayHp(pl, 0.05);
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<GuardSigil>(pl)
                        .ContinueWith(x => ZLog.W("war council guard sigil ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                }
            }
            catch (Exception e) { ZLog.W("war council guard fail " + e.Message); }
            Finish(ev, "WAR_COUNCIL.pages.GUARD.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("war council leave fail " + e.Message); }
            Finish(ev, "WAR_COUNCIL.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 丰收祭（收割体系事件）=====
    public static class HarvestFestivalEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.SelfHelpBook";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("HARVEST_FEST.title");
        public static LocString InitialDescription() => L("HARVEST_FEST.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var sell = new EventOption(ev, () => Sell(ev), L("HARVEST_FEST.options.SELL.title"), L("HARVEST_FEST.options.SELL.description"), "SELL", System.Array.Empty<IHoverTip>());
            var scythe = new EventOption(ev, () => Scythe(ev), L("HARVEST_FEST.options.SCYTHE.title"), L("HARVEST_FEST.options.SCYTHE.description"), "SCYTHE", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("HARVEST_FEST.options.LEAVE.title"), L("HARVEST_FEST.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { sell, scythe, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("harvest fest finish fail " + e.Message); }
        }

        static async Task Sell(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    var st = ZhijiStore.Of(pl);
                    int gold = st.Reap / 2;
                    st.Reap = 0;
                    pl.Gold += gold;
                    ZLog.W("harvest fest: sell reap -> gold " + gold);
                }
            }
            catch (Exception e) { ZLog.W("harvest fest sell fail " + e.Message); }
            Finish(ev, "HARVEST_FEST.pages.SELL.description");
            await Task.CompletedTask;
        }

        static async Task Scythe(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null) RedCradleEvent.AddCardsPublic(pl, c => c is ReapScythe, 1);
            }
            catch (Exception e) { ZLog.W("harvest fest scythe fail " + e.Message); }
            Finish(ev, "HARVEST_FEST.pages.SCYTHE.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    int heal = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                    pl.Creature.HealInternal(heal);
                }
            }
            catch (Exception e) { ZLog.W("harvest fest leave fail " + e.Message); }
            Finish(ev, "HARVEST_FEST.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }


    // ===== 孵化完成（失控红神路线入口）=====
    public static class HatchEvent
    {
        public const string Hijacked = "MegaCrit.Sts2.Core.Models.Events.Reflections";
        public const string HijackedB = "MegaCrit.Sts2.Core.Models.Events.DenseVegetation";
        public const string HijackedC = "MegaCrit.Sts2.Core.Models.Events.SunkenTreasury";

        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("HATCH.title");
        public static LocString InitialDescription() => L("HATCH.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var accept = new EventOption(ev, () => Accept(ev), L("HATCH.options.ACCEPT.title"), L("HATCH.options.ACCEPT.description"), "ACCEPT", System.Array.Empty<IHoverTip>());
            var refuse = new EventOption(ev, () => Refuse(ev), L("HATCH.options.REFUSE.title"), L("HATCH.options.REFUSE.description"), "REFUSE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { accept, refuse };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("hatch finish fail " + e.Message); }
        }

        static async Task Accept(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    bool already = false;
                    try { if (pl.Relics != null) foreach (var rH in pl.Relics) if (rH is UnboundHeart) { already = true; break; } } catch { }
                    if (!already)
                    {
                        _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<UnboundHeart>(pl)
                            .ContinueWith(x => ZLog.W("hatch heart ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    }
                    // 全牌组附魔「失控」
                    int n = 0;
                    try
                    {
                        if (pl.Deck != null && pl.Deck.Cards != null)
                            foreach (var c in pl.Deck.Cards)
                            {
                                if (c == null) continue;
                                if (c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack && c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill) continue;
                                try { MegaCrit.Sts2.Core.Commands.CardCmd.Enchant<UnboundRage>(c, 1m); n++; } catch { }
                            }
                    }
                    catch (Exception eE) { ZLog.W("hatch enchant fail " + eE.Message); }
                    ZLog.W("hatch accepted: enchanted " + n + " cards");
                    try { ZhijiStore.Once(pl, "hatch_seen"); } catch { }
                    // 附赠三张基础召唤牌，保证变身后有虫可用
                    try
                    {
                        RedCradleEvent.AddCardsPublic(pl, c => c is FeastCall, 1);
                        RedCradleEvent.AddCardsPublic(pl, c => c is MultiplyGuest, 1);
                        RedCradleEvent.AddCardsPublic(pl, c => c is SacrificeGuest, 1);
                        ZLog.W("hatch granted 3 summon cards");
                    }
                    catch (Exception eGs) { ZLog.W("hatch grant summons fail " + eGs.Message); }
                }
            }
            catch (Exception e) { ZLog.W("hatch accept fail " + e.Message); }
            Finish(ev, "HATCH.pages.ACCEPT.description");
            await Task.CompletedTask;
        }

        static async Task Refuse(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    pl.Gold += 75;
                    if (pl.Creature != null) pl.Creature.HealInternal((int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10));
                    try { ZhijiStore.Once(pl, "hatch_seen"); } catch { }
                }
            }
            catch (Exception e) { ZLog.W("hatch refuse fail " + e.Message); }
            Finish(ev, "HATCH.pages.REFUSE.description");
            await Task.CompletedTask;
        }
    }


    /// 第二层事件：红神之心（必须先完成第一层「孵化」并持有失控之心）
    public static class RedGodHeartEvent
    {
        static LocString L(string key) => new LocString("events", key);
        public static LocString Title() => L("RED_HEART.title");
        public static LocString InitialDescription() => L("RED_HEART.pages.INITIAL.description");

        public static IReadOnlyList<EventOption> BuildOptions(EventModel ev)
        {
            var accept = new EventOption(ev, () => Accept(ev), L("RED_HEART.options.ACCEPT.title"), L("RED_HEART.options.ACCEPT.description"), "ACCEPT", System.Array.Empty<IHoverTip>());
            var open = new EventOption(ev, () => OpenRib(ev), L("RED_HEART.options.OPEN.title"), L("RED_HEART.options.OPEN.description"), "OPEN", System.Array.Empty<IHoverTip>());
            var leave = new EventOption(ev, () => Leave(ev), L("RED_HEART.options.LEAVE.title"), L("RED_HEART.options.LEAVE.description"), "LEAVE", System.Array.Empty<IHoverTip>());
            return new List<EventOption> { accept, open, leave };
        }

        static void Finish(EventModel ev, string key)
        {
            try
            {
                var m = typeof(EventModel).GetMethod("SetEventFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(ev, new object[] { L(key) });
            }
            catch (Exception e) { ZLog.W("red heart finish fail " + e.Message); }
        }

        static void Consume(Player pl)
        {
            try
            {
                var st = ZhijiStore.Of(pl);
                st.RedHeartPending = false; st.RedHeartDone = true;
            }
            catch { }
        }

        static async Task Accept(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<RedGodHeart>(pl)
                        .ContinueWith(x => ZLog.W("red heart obtain ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                    if (pl.Creature != null)
                    {
                        int loss = (int)System.Math.Ceiling(pl.Creature.MaxHp * 0.10);
                        pl.Creature.SetMaxHpInternal(System.Math.Max(1, pl.Creature.MaxHp - loss));
                    }
                    ZLog.W("red heart accepted");
                }
                Consume(pl);
            }
            catch (Exception e) { ZLog.W("red heart accept fail " + e.Message); }
            Finish(ev, "RED_HEART.pages.ACCEPT.description");
            await Task.CompletedTask;
        }

        static async Task OpenRib(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null)
                {
                    try
                    {
                        if (pl.Osty != null && !pl.Osty.IsDead)
                        {
                            pl.Osty.SetMaxHpInternal(pl.Osty.MaxHp + 10);
                            pl.Osty.HealInternal(10);
                        }
                        else
                        {
                            RedCradleEvent.AddCardsPublic(pl, c => c is ZhijiCard, 2);
                        }
                    }
                    catch { }
                    if (pl.Creature != null) pl.Creature.LoseHpInternal(8, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZLog.W("red heart opened rib");
                }
                Consume(pl);
            }
            catch (Exception e) { ZLog.W("red heart open fail " + e.Message); }
            Finish(ev, "RED_HEART.pages.OPEN.description");
            await Task.CompletedTask;
        }

        static async Task Leave(EventModel ev)
        {
            try
            {
                var pl = RedCradleEvent.PlayerOfPublic(ev);
                if (pl != null && pl.Creature != null)
                {
                    pl.Creature.HealInternal((int)System.Math.Ceiling(pl.Creature.MaxHp * 0.20));
                }
                ZLog.W("red heart left");
                Consume(pl);
            }
            catch (Exception e) { ZLog.W("red heart leave fail " + e.Message); }
            Finish(ev, "RED_HEART.pages.LEAVE.description");
            await Task.CompletedTask;
        }
    }
}

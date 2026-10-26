using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Zhiji
{
    public static class ZLog
    {
        const string AndroidDir = "/storage/emulated/0/Android/data/com.megacrit.sts2re/files";

        // ===== 缓冲写入（3.3.15）=====
        // 原来每行 File.AppendAllText 一次 open/write/close；长局 1600+ 行，在手机上累积成 I/O 抖动。
        // 现在攒到 8KB 或每 1s 刷一次；异常/关键行立即刷，保证崩溃时也留有现场。
        static readonly System.Text.StringBuilder _buf = new System.Text.StringBuilder();
        static readonly object _bufLock = new object();
        static long _lastFlushMs;
        public static long FlushCount;
        public static long LinesWritten;
        static readonly string[] Quiet = { "sync pulse", "famine preview", "autotest probe", "engine log copied", "engine log not found", "relic packed path", "patched-pre", "patched-post", "debug: cmd" };

        // 日志目录按平台解析（254.3 的 Windows 跳过保留；3.3.2 起 Linux/PC 也落盘，方便无头测试）
        static string _logDir;
        static bool _logDirDone;
        static string LogDir()
        {
            if (_logDirDone) return _logDir;
            _logDirDone = true;
            try
            {
                string os = Godot.OS.GetName();
                if (os == "Android")
                {
                    _logDir = AndroidDir;
                }
                else if (os == "Windows")
                {
                    _logDir = null;   // 保持 254.3：Windows 不再逐行写文件
                }
                else
                {
                    // Linux/其他桌面：user:// 即 ~/.local/share/<游戏名>
                    var ud = Godot.OS.GetUserDataDir();
                    if (!string.IsNullOrEmpty(ud))
                    {
                        try { System.IO.Directory.CreateDirectory(ud); } catch { }
                        _logDir = ud;
                    }
                }
            }
            catch { _logDir = null; }
            return _logDir;
        }

        public static string LogDirPublic() { return LogDir(); }

        static void Flush(string path, bool evenIfEmpty)
        {
            try
            {
                if (_buf.Length == 0 && !evenIfEmpty) return;
                if (_buf.Length == 0) return;
                System.IO.File.AppendAllText(path, _buf.ToString());
                _buf.Clear();
                _lastFlushMs = System.Environment.TickCount64;
                FlushCount++;
            }
            catch { }
        }

        // 退出/交接时调用，确保缓冲区落盘
        public static void FlushNow()
        {
            try
            {
                var dir = LogDir();
                if (dir == null) return;
                lock (_bufLock) Flush(System.IO.Path.Combine(dir, "zhiji_log.txt"), true);
            }
            catch { }
        }

        public static void W(string s)
        {
            bool quiet = false;
            try { foreach (var q in Quiet) if (s.StartsWith(q)) { quiet = true; break; } } catch { }
            if (!quiet) { try { Godot.GD.Print("[zhiji] " + s); } catch { } }
            if (quiet) return;
            try
            {
                var dir = LogDir();
                if (dir == null) return;
                var path = System.IO.Path.Combine(dir, "zhiji_log.txt");
                // 4MB 上限：长局/长测时避免日志无限增长（轮转成 .old，只留一份）
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                    {
                        Flush(path, true);
                        var old = System.IO.Path.Combine(dir, "zhiji_log.old.txt");
                        try { System.IO.File.Delete(old); } catch { }
                        System.IO.File.Move(path, old);
                    }
                }
                catch { }
                lock (_bufLock)
                {
                    _buf.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ").Append(s).Append('\n');
                    LinesWritten++;
                    bool urgent = s.Contains("fail") || s.Contains("exception") || s.Contains("Fail")
                        || s.StartsWith("entry build") || s.Contains("defeat") || s.Contains("death settle")
                        || s.Contains("force finish");
                    long nowMs = System.Environment.TickCount64;
                    if (urgent || _buf.Length > 8192 || nowMs - _lastFlushMs > 1000)
                        Flush(path, false);
                }
            }
            catch { }
        }
    }

    public static class LogCopy
    {
        public static void Run()
        {
            try
            {
                string userDir = Godot.OS.GetUserDataDir();
                if (string.IsNullOrEmpty(userDir)) return;
                string logs = System.IO.Path.Combine(userDir, "logs");
                if (!System.IO.Directory.Exists(logs)) return;
                var files = System.IO.Directory.GetFiles(logs, "*.log");
                var best = (string)null;
                var bestTime = DateTime.MinValue;
                foreach (var f in files)
                {
                    var t = System.IO.File.GetLastWriteTime(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                if (best == null) return;
                string dst = "/storage/emulated/0/Android/data/com.megacrit.sts2re/files/zhiji_godotlog.txt";
                System.IO.File.Copy(best, dst, true);
                ZLog.W("copied godot log " + System.IO.Path.GetFileName(best));
            }
            catch (Exception e) { ZLog.W("logcopy fail " + e.Message); }
        }
    }

    public static class ZhijiHooks
    {
        static readonly System.Random _autoRng = new System.Random();

        public static void OnCombatSetUp()
        {
            ZhijiStore.ResetAll();
            try { Visuals.HideDeathSprite("combat setup"); } catch { }
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs != null && rs.Players != null && rs.Players.Count > 0)
                {
                    var pl = rs.Players[0];
                    if (pl.Relics != null && pl.Relics.Any(r => r is CrimsonCradle))
                    {
                        ZhijiStore.Of(pl).AddPulse(3);
                        ZLog.W("crimson cradle: +3 pulse at combat start");
                    }
                    if (pl.Relics != null && pl.Relics.Any(r => r is VileCrown))
                    {
                        _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(null, pl.Creature, 2m, pl.Creature, null, true);
                        ZLog.W("vile crown: +2 vile at combat start");
                    }
                }
            }
            catch { }
        }

        public static void OnPlayerTurnStart(Player player)
        {
            if (player == null) return;
            try { if (player.Character == null || player.Character.Id.Entry != "ZHIJI") return; } catch { return; }
            // 同一回合只初始化一次（防止 SetupPlayerTurn 与战斗初始化兜底重复触发）
            try
            {
                int turnNow = player.PlayerCombatState != null ? player.PlayerCombatState.TurnNumber : -1;
                var stD = ZhijiStore.Of(player);
                if (turnNow >= 0 && stD.LastInitTurn == turnNow) { ZLog.W("turn init skipped dup turn=" + turnNow); return; }
                stD.LastInitTurn = turnNow;
            }
            catch { }
            // 玩家已经死亡：不要再跑回合逻辑，并主动补一次死亡结算
            try
            {
                if (player.Creature == null || player.Creature.IsDead || (int)player.Creature.CurrentHp <= 0)
                {
                    var stDead = ZhijiStore.Of(player);
                    stDead.AutoPlayPending = false;
                    ZhijiPatcher.EnsureDeathSettled(player, "turn-start");
                    ZLog.W("turn start skipped (player dead)");
                    return;
                }
            }
            catch { }
            var st = ZhijiStore.Of(player);
            // 红神形态：缝补血肉，每回合回复 2 点生命
            try
            {
                if (st.Form == ZhijiForm.RedGod && player.Creature != null && player.Creature.CurrentHp < player.Creature.MaxHp)
                {
                    player.Creature.HealInternal(3);
                    ZLog.W("red god regen +3");
                }
            }
            catch { }
            if (player.Relics != null && Enumerable.Any(player.Relics, r => r is FleshStilts))
                st.AddPulse(1);   // 被动再削：+2 -> +1，变身速度交给卡片

            var state = player.Creature != null ? player.Creature.CombatState : null;
            if (state == null) return;
            // 召唤物：回合开始咬第一个活着的敌人
            try
            {
                if (st.MinionHp > 0 && state.Enemies != null)
                {
                    int atk = 3;
                    try { if (player.Relics != null && player.Relics.Any(r => r is DinnerBell)) atk += 4; } catch { }
                    foreach (var enemy in state.Enemies)
                    {
                        if (enemy == null || enemy.IsDead) continue;
                        enemy.LoseHpInternal(atk, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                        st.AddPulse(1);   // 3.3.19：召唤物输出计入搏动，召唤流也能过饥饿
                        st.AddHunger(-1);  // 3.3.20：咬一口也在"进食"，饥饿下降
                        ZLog.W("minion attacked for " + atk + " (hp=" + st.MinionHp + ") pulse+1");
                        break;
                    }
                }
            }
            catch (Exception eM) { ZLog.W("minion attack fail " + eM.Message); }
            // 第二个肉块也咬一口
            try
            {
                if (st.MinionHp2 > 0 && state.Enemies != null)
                {
                    int atk2 = 3;
                    try { if (player.Relics != null && player.Relics.Any(r => r is DinnerBell)) atk2 += 4; } catch { }
                    foreach (var enemy in state.Enemies)
                    {
                        if (enemy == null || enemy.IsDead) continue;
                        enemy.LoseHpInternal(atk2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                        st.AddPulse(1);
                        ZLog.W("minion2 attacked for " + atk2 + " (hp=" + st.MinionHp2 + ") pulse+1");
                        break;
                    }
                }
            }
            catch (Exception eM2) { ZLog.W("minion2 attack fail " + eM2.Message); }

            // 常态代价：按饥饿百分比自流血（每回合，寿衣/红神/回落中不触发）
            try
            {
                if (st.Form == ZhijiForm.Normal && !st.GoldShroud && !st.PendingRevert && !st.Restoring && st.Hunger > 0)
                {
                    int maxHp = (int)player.Creature.MaxHp;
                    double selfK = 0.06;   // 3.3.19：0.10 -> 0.06，减轻"必须对爆"的压力
                    bool hasPet = (st.MinionHp > 0 || st.MinionHp2 > 0)
                        || (player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0);
                    if (hasPet) selfK *= 0.5;   // 有召唤物在场再减半：奖励召唤流派
                    int loss = (int)System.Math.Ceiling(maxHp * (st.Hunger / 100.0) * selfK);
                    // 3.3.20（U9）：封顶，避免"拖长战局被自损暴毙"的设计漏洞
                    int capLoss = (int)System.Math.Ceiling(maxHp * 0.08);
                    if (loss > capLoss) loss = capLoss;
                    if (loss > 0)
                    {
                        decimal hpBefore = player.Creature.CurrentHp;
                        ZhijiPatcher.BypassStand = true;
                        player.Creature.LoseHpInternal(loss, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                        ZhijiPatcher.BypassStand = false;
                        // 3.3.21（N1/U9）：引擎会把玩家的 HP 损失钳在 1 —— 自损永远打不死人，
                        // 导致"对爆"流派零风险（玩家只会抓抽卡+直伤）。这里让饥饿真正致命。
                        if (loss >= hpBefore && !player.Creature.IsDead && (int)player.Creature.CurrentHp > 0)
                        {
                            player.Creature.SetCurrentHpInternal(0);
                            ZLog.W("hunger bleed LETHAL: hunger=" + st.Hunger + " loss=" + loss + " hp " + hpBefore + " -> 0");
                            ZhijiPatcher.EnsureDeathSettled(player, "famine");
                        }
                        else ZLog.W("hunger bleed: hunger=" + st.Hunger + " loss=" + loss + " hp=" + player.Creature.CurrentHp);
                    }
                }
            }
            catch (Exception eHG) { ZLog.W("hunger bleed fail " + eHG.Message); }
            // 饥饿契约（能力牌）：每回合开始额外饥饿
            try
            {
                int pact = player.Creature != null ? player.Creature.GetPowerAmount<HungerPactPower>() : 0;
                if (pact > 0) { st.AddHunger(pact); ZLog.W("hunger pact +" + pact + " hunger"); }
            }
            catch { }
            // 暴食本能（能力牌）：红神形态每回合 +自助餐
            try
            {
                int glut = player.Creature != null ? player.Creature.GetPowerAmount<GluttonInstinctPower>() : 0;
                if (glut > 0 && st.Form == ZhijiForm.RedGod) { st.AddFeast(glut); ZLog.W("glutton instinct +" + glut + " feast"); }
            }
            catch { }
            // 不灭血肉（失控之心）：用原版 Osty——没有就召唤，有就 +1 生命
            try
            {
                bool unboundFlesh = player.Relics != null && player.Relics.Any(r => r is UnboundHeart);
                if (unboundFlesh)
                {
                    bool alive = player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0;
                    if (!alive)
                    {
                        // 眷属复活：每场第一次免费，之后消耗 2 自助餐
                        bool blocked = false;
                        if (st.Vassal)
                        {
                            if (!st.VassalFreeRevive) st.VassalFreeRevive = true;
                            else if (st.Feast >= 2) { st.Feast -= 2; ZLog.W("vassal revive cost feast-2"); }
                            else { ZLog.W("vassal revive blocked (feast<2)"); blocked = true; }
                        }
                        int actIdx = 0;
                        try { actIdx = player.RunState.CurrentActIndex; } catch { }
                        int actHp = 4 + (actIdx + 1) * 2;
                        int baseHp = System.Math.Max(actHp, System.Math.Min(20, actHp + st.Feast / 4));
                        if (!blocked)
                        {
                            try
                            {
                                MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext ctxA = new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext();
                                var tA = MegaCrit.Sts2.Core.Commands.OstyCmd.Summon(ctxA, player, baseHp, null);
                                _ = tA.ContinueWith(x => ZLog.W("unbound osty auto summon hp=" + baseHp + " ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                            }
                            catch (Exception eOA) { ZLog.W("unbound osty summon fail " + eOA.GetBaseException().Message); }
                        }
                    }
                    else if (player.Osty.MaxHp < 40)
                    {
                        player.Osty.SetMaxHpInternal(player.Osty.MaxHp + 1);
                        player.Osty.HealInternal(1);
                        ZLog.W("unbound osty grow +1 max=" + player.Osty.MaxHp);
                    }
                    // 红神眷属：进化 + 每回合自动攻击
                    try
                    {
                        if (player.Relics != null && player.Relics.Any(r => r is RedGodHeart) && !st.Vassal)
                        {
                            st.Vassal = true;
                            try
                            {
                                if (player.Osty != null && !player.Osty.IsDead)
                                {
                                    player.Osty.SetMaxHpInternal(player.Osty.MaxHp + 10);
                                    player.Osty.HealInternal(10);
                                }
                            }
                            catch { }
                            ZLog.W("vassal evolved: flesh -> red god vassal (+10 max)");
                        }
                        if (st.Vassal && player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0)
                        {
                            var csV = player.Creature != null ? player.Creature.CombatState : null;
                            if (csV != null && csV.Enemies != null)
                                foreach (var enV in csV.Enemies)
                                {
                                    if (enV == null || enV.IsDead) continue;
                                    int biteV = System.Math.Max(4, (int)player.Osty.MaxHp / 3);
                                    enV.LoseHpInternal(biteV, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                    ZLog.W("vassal bite=" + biteV);
                                    break;
                                }
                        }
                        if (st.Vassal && player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0)
                        {
                            player.Osty.LoseHpInternal(2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                            ZLog.W("vassal hunger -2");
                        }
                    }
                    catch { }
                    // 血神共鸣：追加一次攻击
                    try
                    {
                        int resN = player.Creature != null ? player.Creature.GetPowerAmount<ResonancePower>() : 0;
                        if (resN > 0 && player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0)
                        {
                            var csR = player.Creature.CombatState;
                            if (csR != null && csR.Enemies != null)
                                foreach (var enR in csR.Enemies)
                                {
                                    if (enR == null || enR.IsDead) continue;
                                    int biteR = System.Math.Max(3, (int)player.Osty.MaxHp / 5);
                                    enR.LoseHpInternal(biteR, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                    ZLog.W("resonance bite=" + biteR);
                                    break;
                                }
                        }
                    }
                    catch { }
                    // 号令：每回合血肉自动攻击
                    try
                    {
                        int cmdN = player.Creature != null ? player.Creature.GetPowerAmount<CommandPower>() : 0;
                        if (cmdN > 0 && player.Osty != null && !player.Osty.IsDead && player.Osty.CurrentHp > 0)
                        {
                            var csA = player.Creature.CombatState;
                            if (csA != null && csA.Enemies != null)
                            {
                                foreach (var en in csA.Enemies)
                                {
                                    if (en == null || en.IsDead) continue;
                                    int bite = System.Math.Max(3, (int)player.Osty.MaxHp / 4);
                                    en.LoseHpInternal(bite, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                    ZLog.W("command guest bite=" + bite + " max=" + player.Osty.MaxHp);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            // 失控症状：手不听使唤（永久红神 15%，force 时 100%）
            try
            {
                bool unboundAuto = player.Relics != null && player.Relics.Any(r => r is UnboundHeart);
                bool forceAuto = ZhijiStore.Once(player, "force_auto");
                if (unboundAuto && (forceAuto || _autoRng.Next(100) < 15))
                {
                    // 回合开始钩子早于能量重置，这里只挂起，等能量就绪后由 UI 定时器执行
                    st.AutoPlayPending = true;
                    ZLog.W("unbound auto play pending");
                }
            }
            catch { }
            // 永久红神：失控在烧她（每回合 2 点）
            try
            {
                bool unboundBurn = player.Relics != null && player.Relics.Any(r => r is UnboundHeart);
                if (unboundBurn && player.Creature != null)
                {
                    ZhijiPatcher.BypassStand = true;
                    player.Creature.LoseHpInternal(2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZhijiPatcher.BypassStand = false;
                    ZLog.W("unbound self burn 2");
                }
            }
            catch { }
            // 旧镰刀：每场战斗第一次回合开始，收割 +10
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is OldScythe) && !ZhijiStore.Once(player, "old_scythe"))
                {
                    st.Reap = System.Math.Min(100, st.Reap + 10);
                    ZLog.W("old scythe +10 reap (" + st.Reap + ")");
                }
            }
            catch { }
            // 干粮袋：每场战斗第一次回合开始，饥饿 -20 并回复 4
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is RationBag) && !ZhijiStore.Once(player, "ration_bag"))
                {
                    st.Hunger = System.Math.Max(0, st.Hunger - 20);
                    if (player.Creature != null) player.Creature.HealInternal(4);
                    ZLog.W("ration bag: hunger -20, heal 4");
                }
            }
            catch { }
            // 饥饿反噬：饥饿 >=75 时，每回合 +2 搏动但失去 3 生命（变身前的风险收益）
            try
            {
                if (st.Form == ZhijiForm.Normal && st.Hunger >= 75 && player.Creature != null)
                {
                    st.AddPulse(2);
                    ZhijiPatcher.BypassStand = true;
                    player.Creature.LoseHpInternal(3, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZhijiPatcher.BypassStand = false;
                    ZLog.W("starvation surge: +2 pulse -3 hp (hunger=" + st.Hunger + ")");
                }
            }
            catch (Exception eSt) { ZLog.W("starvation surge fail " + eSt.Message); }
            // 饥饿律动（能力牌）：每回合开始获得搏动
            try
            {
                int rhythm = player.Creature != null ? player.Creature.GetPowerAmount<HungerRhythmPower>() : 0;
                if (rhythm > 0) { st.AddPulse(rhythm); ZLog.W("hunger rhythm +" + rhythm + " pulse"); }
            }
            catch { }
            // 卑贱循环（能力牌）：每回合自动叠层
            try
            {
                int loop = player.Creature != null ? player.Creature.GetPowerAmount<VileLoopPower>() : 0;
                if (loop > 0)
                {
                    _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(null, player.Creature, loop, player.Creature, null, true);
                    ZLog.W("vile loop +" + loop + " vile");
                }
            }
            catch { }
            // 卑贱钱袋：本场第一回合 +1 层
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is VilePurse) && !ZhijiStore.Once(player, "vile_purse"))
                {
                    _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(null, player.Creature, 1m, player.Creature, null, true);
                    ZLog.W("vile purse: +1 vile");
                }
            }
            catch { }
            // 饿钟：每回合饥饿 +1
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is HungerBell))
                {
                    st.AddHunger(1);
                    ZLog.W("hunger bell: +1 hunger");
                }
            }
            catch { }
            // 血之沙漏：本场第一回合对所有敌人施加 2 层流血
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is BloodHourglass) && !ZhijiStore.Once(player, "blood_hourglass"))
                {
                    if (state.Enemies != null)
                        foreach (var e in state.Enemies)
                            if (e != null && !e.IsDead)
                                _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedPower>(null, e, 2m, player.Creature, null, true);
                    ZLog.W("blood hourglass: 2 bleed to all");
                }
            }
            catch { }
            // 卑贱王冠：本场战斗第一次回合开始 +2 层卑贱之物（放这里比战斗初始化更可靠）
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is VileCrown) && !st.VileCrownApplied)
                {
                    st.VileCrownApplied = true;
                    _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(null, player.Creature, 2m, player.Creature, null, true);
                    ZLog.W("vile crown: +2 at first turn");
                }
            }
            catch (Exception eVC) { ZLog.W("vile crown fail " + eVC.Message); }
            // 漆黑餐巾：战斗第一次回合开始时对所有敌人施加 2 层流血
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is BlackNapkin) && !ZhijiStore.Once(player, "black_napkin"))
                {
                    if (state.Enemies != null)
                        foreach (var e in state.Enemies)
                            if (e != null && !e.IsDead)
                                _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedPower>(null, e, 2m, player.Creature, null, true);
                    ZLog.W("black napkin: 2 bleed to all");
                }
            }
            catch (Exception eBN) { ZLog.W("black napkin fail " + eBN.Message); }
            // 猩红圣痕：红神形态每回合开始 +1 自助餐
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is Stigmata) && st.Form == ZhijiForm.RedGod)
                {
                    st.AddFeast(1);
                    ZLog.W("stigmata: +1 feast");
                }
            }
            catch { }
            // 战纹章：红神形态每回合 +2 自助餐
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is WarSigil) && st.Form == ZhijiForm.RedGod)
                {
                    st.AddFeast(2);
                    ZLog.W("war sigil +2 feast");
                }
            }
            catch { }
            // 守纹章：红神形态每回合 +3 格挡
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is GuardSigil) && st.Form == ZhijiForm.RedGod && player.Creature != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.CreatureCmd.GainBlock(player.Creature, 3m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, null, false);
                    ZLog.W("guard sigil +3 block");
                }
            }
            catch { }
            // 猩红摇篮：红神形态每回合开始 +2 格挡
            try
            {
                if (player.Relics != null && player.Relics.Any(r => r is CrimsonCradle) && st.Form == ZhijiForm.RedGod && player.Creature != null)
                {
                    _ = MegaCrit.Sts2.Core.Commands.CreatureCmd.GainBlock(player.Creature, 2m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, null, false);
                    ZLog.W("crimson cradle: red form +2 block");
                }
            }
            catch (Exception eCC) { ZLog.W("cradle block fail " + eCC.Message); }
            // 遗物续航：餐刀每回合 +2；血玉酒杯在敌人有流血时 +3；血肉高跷 +1
            try
            {
                if (player.Relics != null && player.Creature != null)
                {
                    bool anyBleed = false;
                    if (state.Enemies != null)
                        foreach (var e in state.Enemies) { if (e != null && !e.IsDead && e.GetPowerAmount<BleedPower>() > 0) { anyBleed = true; break; } }
                    bool hasKnife = player.Relics.Any(r => r is CarvingKnife);
                    bool hasStilts = player.Relics.Any(r => r is FleshStilts);
                    bool hasGoblet = player.Relics.Any(r => r is BloodGoblet);
                    int healAmt = (hasKnife ? 1 : 0) + (hasStilts ? 1 : 0) + (anyBleed && hasGoblet ? 2 : 0);
                    if (healAmt > 4) healAmt = 4;   // 每回合遗物回复上限 4，防止多遗物叠成永动机
                    if (healAmt > 0 && player.Creature.CurrentHp < player.Creature.MaxHp)
                    {
                        player.Creature.HealInternal(healAmt);
                        ZLog.W("relic heal +" + healAmt + " (knife=" + hasKnife + " stilts=" + hasStilts + " goblet=" + hasGoblet + ")");
                    }
                }
            }
            catch (Exception eRH) { ZLog.W("relic heal fail " + eRH.Message); }
            foreach (var enemy in state.Enemies)
            {
                if (enemy == null || enemy.IsDead) continue;
                int bleed = enemy.GetPowerAmount<BleedPower>();
                if (bleed <= 0) continue;
                enemy.LoseHpInternal(bleed, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                // 每回合只掉 1 层，而不是全部掉光
                try
                {
                    _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedPower>(null, enemy, -1m, player.Creature, null, true);
                    st.BleedTicks++;
                    ZLog.W("bleed tick dmg=" + bleed + " left=" + (bleed - 1) + " ticks=" + st.BleedTicks);
                } catch (Exception eB) { ZLog.W("bleed decay fail " + eB.Message); }
            }
            // DOT 直伤绕过了引擎死亡处理：这里补一次结算，避免敌人显示"死亡"却不结束战斗
            try { Visuals.CleanupDeadEnemies(); } catch (Exception eCD) { ZLog.W("cleanup after dot fail " + eCD.Message); }
        }
    }

    [HarmonyPatch]
    public static class EntryPatch
    {
        static MethodBase TargetMethod()
        {
            try
            {
                ZLog.W("entry build 3.3.22");
            try
            {
                var treeW = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (treeW != null && treeW.Root != null)
                {
                    var wt = new Godot.Timer(); wt.WaitTime = 1.0; wt.OneShot = true;
                    wt.Timeout += () => { try { Visuals.EnsureWatcher(); } catch { } };
                    treeW.Root.CallDeferred(Godot.Node.MethodName.AddChild, wt);
                }
            }
            catch { }
                ZhijiPatcher.Install();
                ZhijiPatcher.LogSignatures();
                ZhijiPatcher.StartAutoTestWatcher();
                ZhijiPatcher.HookRoomEvents();
                ZhijiPatcher.TryPatchMapMarker();
                ZhijiPatcher.TryEnableMenuDebugPatch();
                ZhijiPatcher.AutoTestTickPublic();
                ZhijiPatcher.SelfCheckCards();
            }
            catch (Exception e) { ZLog.W("entry error " + e.Message); }
            return AccessTools.Method(typeof(EntryPatch), nameof(Noop));
        }
        static void Noop() { }
        static void Postfix() { }
    }

    public static class ZhijiPatcher
    {
        static readonly System.Random _autoRng = new System.Random();
        static bool _done;

        static int CostOf(MegaCrit.Sts2.Core.Models.CardModel c)
        {
            try { return c != null && c.EnergyCost != null ? (int)c.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.None) : 99; }
            catch { return 99; }
        }

        public static void TryConsumeAutoPlay()
        {
            try
            {
                var pl = ZhijiPatcher.DebugPlayerPublic();
                if (pl == null || pl.Creature == null) return;
                if (pl.Creature.IsDead || (int)pl.Creature.CurrentHp <= 0) { ZhijiStore.Of(pl).AutoPlayPending = false; return; }
                var st = ZhijiStore.Of(pl);
                if (!st.AutoPlayPending) return;
                var pcs = pl.PlayerCombatState;
                decimal energy = pcs != null ? pcs.Energy : 0m;
                if (energy <= 0m) return;
                var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                if (hand == null || hand.Cards == null) return;
                var cands = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var c in hand.Cards)
                {
                    if (c == null) continue;
                    try { if (c.EnergyCost != null && c.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.None) <= energy) cands.Add(c); } catch { }
                }
                if (cands.Count == 0) return;
                MegaCrit.Sts2.Core.Entities.Creatures.Creature tgt = null;
                try
                {
                    var cs = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null ? MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() : null;
                    if (cs != null && cs.Enemies != null) foreach (var e in cs.Enemies) if (e != null && !e.IsDead) { tgt = e; break; }
                }
                catch { }
                bool ok = false;
                MegaCrit.Sts2.Core.Models.CardModel pick = null;
                int tried = 0, failed = 0;
                var fails = new System.Text.StringBuilder();
                // 3.3.5：旧实现只随机试 3 张，手牌里有「需要选择/无目标」的牌时容易 ok=False；
                // 改为把候选全部试完（先打费的），仍不行再用空放（无目标）兜底。
                try { cands.Sort((a, b) => CostOf(a).CompareTo(CostOf(b))); } catch { }
                foreach (var pass in new MegaCrit.Sts2.Core.Entities.Creatures.Creature[] { tgt, null })
                {
                    foreach (var cand in cands)
                    {
                        if (cand == null) continue;
                        tried++;
                        try
                        {
                            if (cand.TryManualPlay(pass)) { ok = true; pick = cand; break; }
                            failed++;
                            fails.Append(cand.Id.Entry + " ");
                        }
                        catch (Exception e2) { failed++; fails.Append(cand.Id.Entry + "!"); ZLog.W("auto play fail " + cand.Id.Entry + " " + e2.Message); }
                    }
                    if (ok) break;
                    if (tgt == null) break;      // 本来就没有目标，不必再跑一遍
                }
                st.AutoPlayPending = false;
                ZLog.W("unbound auto play " + (pick != null ? pick.Id.Entry : "none") + " ok=" + ok
                    + " tried=" + tried + " failed=" + failed + " blocked=[" + fails.ToString().Trim() + "]");
            }
            catch { }
        }

        public static void Install()
        {
            if (_done) return;
            _done = true;
            var harmony = new Harmony("zhiji");
            TryPatch(harmony, "MegaCrit.Sts2.Core.Combat.CombatManager", "SetUpCombat", "AfterSetUpCombat");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Combat.CombatManager", "SetUpCombat", "BeforeSetUpCombat");
            TryPatch(harmony, "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", "ResetEnergy", "AfterResetEnergy");
            TryPatch(harmony, "MegaCrit.Sts2.Core.Models.ModelDb", "Init", "AfterModelDbInit");
            TryPatch(harmony, "MegaCrit.Sts2.Core.Localization.LocManager", "GetTable", "AfterGetTable");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.ModelDb", "get_AllAbstractModelSubtypes", "BeforeGetSubtypes");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.ModelDb", "get_AllAbstractModelSubtypes", "AfterGetSubtypes");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.ModelDb", "Init", "BeforeInit");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.ModelDb", "get_AllCharacters", "AfterAllCharacters");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", "EndOfTurnCleanup", "BeforeEndTurnCleanup");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.CardModel", "get_Portrait", "CardPortraitPrefix");
            var cm = "MegaCrit.Sts2.Core.Models.CharacterModel";
            TryPatchPrefix(harmony, cm, "get_VisualsPath", "VisualsPathPrefix");
            TryPatchPrefix(harmony, cm, "get_IconPath", "IconPathPrefix");
            TryPatchPrefix(harmony, cm, "get_IconTexture", "IconTexturePrefix");
            TryPatchPrefix(harmony, cm, "get_IconOutlineTexture", "IconTexturePrefix");
            TryPatchPrefix(harmony, cm, "get_CharacterSelectIconPath", "SelectIconPrefix");
            TryPatchPrefix(harmony, cm, "get_CharacterSelectLockedIconPath", "SelectLockedPrefix");
            TryPatchPrefix(harmony, cm, "get_CharacterSelectBg", "SelectBgPrefix");
            TryPatchPrefix(harmony, cm, "get_EnergyCounterPath", "EnergyCounterPrefix");
            TryPatchPrefix(harmony, cm, "get_RestSiteAnimPath", "RestSitePrefix");
            TryPatchPrefix(harmony, cm, "get_MerchantAnimPath", "MerchantPrefix");
            TryPatchPrefix(harmony, cm, "get_CharacterSelectTransitionPath", "TransitionPrefix");
            TryPatchPrefix(harmony, cm, "get_MapMarkerPath", "MapMarkerPrefix");
            TryPatchPrefix(harmony, cm, "get_TrailPath", "TrailPrefix");
            TryPatchPrefix(harmony, cm, "get_IconTexturePath", "TopIconPrefix");
            TryPatchPrefix(harmony, cm, "get_IconOutlineTexturePath", "TopIconOutlinePrefix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton", "Init", "ButtonInitPostfix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton", "Select", "ButtonSelectPostfix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Unlocks.UnlockState", "get_Characters", "AfterUnlockCharacters");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen", "SelectCharacter", "ScreenSelectPostfix");
            TryAddFinalizer(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen", "SelectCharacter", "SelectFinalizer");
            TryAddFinalizer(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton", "Select", "SelectFinalizer");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Localization.LocString", "GetFormattedText", "LocTextPrefix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Models.CharacterModel", "CreateVisuals", "CreateVisualsPostfix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi", "_ExitTree", "CombatUiExitPostfix");
            try
            {
                var gbT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Commands.CreatureCmd", false) : null;
                if (gbT != null)
                {
                    System.Reflection.MethodInfo pick = null;
                    foreach (var m in gbT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "GainBlock") continue;
                        if (m.GetParameters().Length == 5) { pick = m; break; }
                    }
                    var gbHook2 = typeof(ZhijiPatcher).GetMethod("GainBlockPrefix", BindingFlags.Public | BindingFlags.Static);
                    foreach (var mm in gbT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (mm.Name != "GainBlock") continue;
                        try { harmony.Patch(mm, prefix: new HarmonyMethod(gbHook2)); ZLog.W("patched GainBlock(" + mm.GetParameters().Length + ")"); } catch (Exception eG2) { ZLog.W("patch GainBlock fail " + eG2.Message); }
                    }
                }
            }
            catch (Exception eGB) { ZLog.W("GainBlock patch fail " + eGB.Message); }
            TryPatchExact(harmony, "MegaCrit.Sts2.Core.Combat.CombatManager", "EndCombatInternal", 0, "CombatEndPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.CardModel", "OnPlayWrapper", "CardPlayPrefix");
            try
            {
                var cfT2 = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Factories.CardFactory", false) : null;
                System.Reflection.MethodInfo cmPick = null;
                if (cfT2 != null)
                {
                    foreach (var m in cfT2.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "CreateForMerchant") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 3 && ps[0].ParameterType.Name == "Player" && ps[2].ParameterType.Name == "CardType") { cmPick = m; break; }
                    }
                }
                // 商人生成阶段替换卡池（在订阅/UI 之前完成，避免货架为空）
                // 商人卖卡：在"生成商人库存"的上下文里，让角色卡池只返回 DOT 基础卡
                try
                {
                    TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory", "CreateForNormalMerchant", "MerchantCtxPrefix");
                    TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory", "CreateForNormalMerchant", "MerchantCtxPostfix");
                    TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.CardPoolModel", "GetUnlockedCards", "PoolMerchantPrefix");
                }
                catch (Exception eM3) { ZLog.W("merchant ctx patch fail " + eM3.Message); }
            }
            catch (Exception eMC) { ZLog.W("merchant pick patch fail " + eMC.Message); }
            try
            {
                var cfT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Factories.CardFactory", false) : null;
                System.Reflection.MethodInfo cfPick = null;
                if (cfT != null)
                {
                    foreach (var m in cfT.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "CreateForReward") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 3 && ps[1].ParameterType == typeof(int)) { cfPick = m; break; }
                    }
                }
                if (cfPick != null)
                {
                    var hook = typeof(ZhijiPatcher).GetMethod("RewardBiasPostfix", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(cfPick, postfix: new HarmonyMethod(hook));
                    var hookPool = typeof(ZhijiPatcher).GetMethod("RewardPoolPrefix", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(cfPick, prefix: new HarmonyMethod(hookPool));
                    ZLog.W("patched reward bias (Player,int,options)");
                }
                else ZLog.W("reward bias: method not found");
            }
            catch (Exception eRB) { ZLog.W("reward bias hook fail " + eRB.Message); }
            try
            {
                TryPatchPrefix(harmony, DinerEvent.Hijacked, "GenerateInitialOptions", "DinerOptionsPrefix");
                // 测试用"第一层必出饭馆"已下线：饭馆回归随机事件（WoodCarvings 位置）
                // TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.ActModel", "PullNextEvent", "ForceDinerPostfix");
                TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.EventModel", "get_Title", "DinerTitlePrefix");
                TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.EventModel", "get_InitialDescription", "DinerDescPrefix");
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.EnchantmentModel", "get_IconPath", "EnchantIconPostfix"); } catch { }
            // 卡面附魔图标：使用原版贴图（不再自定义）
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.CardModel", "GetResultLocationForCardPlay", "PowerExhaustPostfix"); } catch { }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Entities.Creatures.Creature", "get_Name", "CreatureNamePostfix"); } catch { }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.PowerModel", "get_Description", "PowerDescPostfix"); } catch { }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.PowerModel", "get_IconPath", "PowerIconPostfix"); } catch { }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Combat.NPower", "_Ready", "PowerNodeIconPostfix"); } catch { }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.NRun", "_Process", "NRunProcessPostfix"); } catch (Exception eRH1) { ZLog.W("nrun patch fail " + eRH1.Message); }
            // 3.3.4：战败/结算界面出现时，强制收起 mod 自绘层（防止立绘盖住分数面板）
            try
            {
                var ovT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack", false) : null;
                var ovM = ovT != null ? ovT.GetMethod("Push", BindingFlags.Public | BindingFlags.Instance) : null;
                if (ovM != null)
                {
                    var hPost = typeof(ZhijiPatcher).GetMethod("OverlayPushPostfix", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(ovM, postfix: new HarmonyMethod(hPost));
                    ZLog.W("patched overlay stack push");
                }
                else ZLog.W("overlay stack push not found");
            }
            catch (Exception eOV) { ZLog.W("overlay push patch fail " + eOV.Message); }
            try
            {
                var capT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Models.Relics.LargeCapsule", false) : null;
                var capM = capT != null ? capT.GetMethod("AfterObtained", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                if (capM != null)
                {
                    var hPre = typeof(ZhijiPatcher).GetMethod("LargeCapsulePrefix", BindingFlags.Public | BindingFlags.Static);
                    var hPost = typeof(ZhijiPatcher).GetMethod("LargeCapsulePostfix", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(capM, prefix: new HarmonyMethod(hPre), postfix: new HarmonyMethod(hPost));
                    ZLog.W("patched large capsule AfterObtained");
                }
                else ZLog.W("large capsule method not found");
            }
            catch (Exception eCap) { ZLog.W("large capsule patch fail " + eCap.Message); }
            try { TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "_Ready", "EventRoomReadyPostfix"); } catch (Exception eRH2) { ZLog.W("event ready patch fail " + eRH2.Message); }
            try { TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "SetPortrait", "EventPortraitPrefix"); } catch (Exception eRH3) { ZLog.W("setportrait patch fail " + eRH3.Message); }
            // 所有覆盖了 Title/InitialDescription 的事件也要经过我们的前缀（保证强制孵化完成时标题也替换）
            try
            {
                var asmEv = GameAsm();
                if (asmEv != null)
                {
                    int nEv = 0;
                    foreach (var t in asmEv.GetTypes())
                    {
                        try
                        {
                            if (!typeof(MegaCrit.Sts2.Core.Models.EventModel).IsAssignableFrom(t) || t.IsAbstract) continue;
                            if (t == typeof(MegaCrit.Sts2.Core.Models.EventModel)) continue;
                            foreach (var m in new[] { "get_Title", "get_InitialDescription" })
                            {
                                var mi = t.GetMethod(m, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                                if (mi == null) continue;
                                var hookN = m == "get_Title" ? "DinerTitlePrefix" : "DinerDescPrefix";
                                var hm = typeof(ZhijiPatcher).GetMethod(hookN, BindingFlags.Public | BindingFlags.Static);
                                if (hm != null) { harmony.Patch(mi, prefix: new HarmonyMethod(hm)); nEv++; }
                            }
                        }
                        catch { }
                    }
                    ZLog.W("event title overrides patched=" + nEv);
                }
            }
            catch (Exception eEv) { ZLog.W("event title patch fail " + eEv.Message); }
                TryPatchPrefix(harmony, RedCradleEvent.Hijacked, "GenerateInitialOptions", "CradleOptionsPrefix");
                TryPatchPrefix(harmony, RedChurchEvent.Hijacked, "GenerateInitialOptions", "ChurchOptionsPrefix");
                TryPatchPrefix(harmony, CrimsonAltarEvent.Hijacked, "GenerateInitialOptions", "AltarOptionsPrefix");
                TryPatchPrefix(harmony, VileMarketEvent.Hijacked, "GenerateInitialOptions", "MarketOptionsPrefix");
                TryPatchPrefix(harmony, BloodGiftEvent.Hijacked, "GenerateInitialOptions", "GiftOptionsPrefix");
                TryPatchPrefix(harmony, HungerTheaterEvent.Hijacked, "GenerateInitialOptions", "TheaterOptionsPrefix");
                TryPatchPrefix(harmony, WarCouncilEvent.Hijacked, "GenerateInitialOptions", "CouncilOptionsPrefix");
                TryPatchPrefix(harmony, HarvestFestivalEvent.Hijacked, "GenerateInitialOptions", "FestivalOptionsPrefix");
                TryPatchPrefix(harmony, HatchEvent.Hijacked, "GenerateInitialOptions", "HatchOptionsPrefix");
                TryPatchPrefix(harmony, HatchEvent.HijackedB, "GenerateInitialOptions", "HatchOptionsPrefix");
                TryPatchPrefix(harmony, HatchEvent.HijackedC, "GenerateInitialOptions", "HatchOptionsPrefix");
            }
            catch (Exception eOV) { ZLog.W("diner hook fail " + eOV.Message); }
            try
            {
                var cmT2 = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Models.CardModel", false) : null;
                if (cmT2 != null)
                {
                    System.Reflection.MethodInfo pickDesc = null;
                    foreach (var m in cmT2.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "GetDescriptionForPile") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType.Name == "PileType") { pickDesc = m; break; }
                    }
                    var dHookAll = typeof(ZhijiPatcher).GetMethod("CardDescPostfix", BindingFlags.Public | BindingFlags.Static);
                    int descN = 0;
                    foreach (var m in cmT2.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "GetDescriptionForPile" && m.Name != "GetDescriptionForUpgradePreview") continue;
                        if (m.ReturnType != typeof(string)) continue;
                        try { harmony.Patch(m, postfix: new HarmonyMethod(dHookAll)); descN++; } catch (Exception eDp) { ZLog.W("desc patch fail " + m.Name + " " + eDp.Message); }
                    }
                    ZLog.W("patched card desc overloads=" + descN);
                }
            }
            catch (Exception eDsc) { ZLog.W("desc patch fail " + eDsc.Message); }
            try
            {
                var necT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter", false) : null;
                var createM = necT != null ? necT.GetMethod("Create", BindingFlags.Public | BindingFlags.Static) : null;
                if (createM != null)
                {
                    harmony.Patch(createM, postfix: new HarmonyMethod(typeof(ZhijiPatcher).GetMethod("EnergyOrbPostfix", BindingFlags.Public | BindingFlags.Static)));
                    ZLog.W("patched energy orb create");
                }
            }
            catch (Exception eEO) { ZLog.W("energy orb patch fail " + eEO.Message); }
            try
            {
                var cmEN = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Models.CardModel", false) : null;
                var pE = cmEN != null ? cmEN.GetProperty("EnergyIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var gE = pE != null ? pE.GetGetMethod(true) : null;
                if (gE != null)
                {
                    harmony.Patch(gE, postfix: new HarmonyMethod(typeof(ZhijiPatcher).GetMethod("CardEnergyIconPostfix", BindingFlags.Public | BindingFlags.Static)));
                    ZLog.W("patched card energy icon");
                }
                else ZLog.W("energy icon getter not found");
            }
            catch (Exception eEI) { ZLog.W("energy icon patch fail " + eEI.Message); }
            TryPatchPrefixEx(harmony, "MegaCrit.Sts2.Core.Entities.Creatures.Creature", "LoseHpInternal", "LoseHpPrefix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Entities.Creatures.Creature", "LoseHpInternal", "LoseHpPostfix");
            // 3.3.3：黄金寿衣 —— 流血中的敌人无法回复生命
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Entities.Creatures.Creature", "HealInternal", "HealInternalPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Commands.CardCmd", "Exhaust", "ExhaustPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Commands.CardPileCmd", "DrawInternal", "DrawInternalPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager", "UpdateAfterCombatWon", "ProgressCombatWonPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager", "CheckFifteenElitesDefeatedEpoch", "ProgressEpochSkipPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager", "CheckFifteenBossesDefeatedEpoch", "ProgressEpochSkipPrefix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "AddInternal", "PileAddPostfix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.PowerModel", "get_Icon", "PowerIconPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.PowerModel", "get_BigIcon", "PowerIconPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_Icon", "RelicIconPrefix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Relics.NRelic", "Create", "NRelicCreatePostfix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Cards.NCard", "UpdatePortrait", "NCardPortraitPostfix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_DynamicDescription", "RelicDynamicDescPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_BigIcon", "RelicBigIconPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.CardModel", "IsValidTarget", "IsValidTargetPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay", "TryPlayCard", "TryPlayCardPrefix");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Models.AncientEventModel", "GenerateInitialOptionsWrapper", "AncientOptionsPostfix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.EventModel", "GenerateInitialOptionsWrapper", "HatchForceOptionsPrefix");
            // 最终幕「建筑师」：吱咿没有专属对话 → Dialogue 为 null，点继续会 NRE 卡死
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Models.Events.TheArchitect", "LoadDialogue", "ArchitectDialoguePostfix");
            TryPatchVoidPrefix(harmony, "MegaCrit.Sts2.Core.Models.Events.TheArchitect", "WinRun", "ArchitectWinRunPrefix");
            // 童年的终结 / 收获：不进弃牌堆
            try
            {
                var cpcT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Commands.CardPileCmd", false) : null;
                if (cpcT != null)
                {
                    var pre = typeof(ZhijiPatcher).GetMethod(nameof(PileAddPrefix), BindingFlags.Public | BindingFlags.Static);
                    int n = 0;
                    foreach (var m in cpcT.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "Add") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 2 && ps[0].ParameterType.Name == "CardModel" && ps[0].Name == "card" && ps[1].ParameterType.Name == "PileType")
                        { harmony.Patch(m, prefix: new HarmonyMethod(pre)); n++; }
                    }
                    ZLog.W("pile add patched: " + n);
                    var pileT = GameAsm().GetType("MegaCrit.Sts2.Core.Entities.Cards.CardPile", false);
                    var aiM = pileT != null ? pileT.GetMethod("AddInternal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                    if (aiM != null)
                    {
                        var postAi = typeof(ZhijiPatcher).GetMethod(nameof(PileAddInternalPostfix), BindingFlags.Public | BindingFlags.Static);
                        harmony.Patch(aiM, postfix: new HarmonyMethod(postAi));
                        ZLog.W("AddInternal postfix ok");
                    }
                }
            }
            catch (Exception ep) { ZLog.W("pile patch fail " + ep.Message); }
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", "get_MaxEnergy", "MaxEnergyPostfix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Combat.CombatManager", "SetupPlayerTurn", "AfterSetupPlayerTurn");
            TryPatchPostfixTwo(harmony, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton", "get_IsLocked", "CharLockPostfix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_IconOutline", "RelicIconPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_Icon", "RelicIconPrefix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Relics.NRelic", "Create", "NRelicCreatePostfix");
            TryPatchPostfixRef(harmony, "MegaCrit.Sts2.Core.Nodes.Cards.NCard", "UpdatePortrait", "NCardPortraitPostfix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_PackedIconPath", "RelicPackedPathPrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Models.RelicModel", "get_PackedIconOutlinePath", "RelicPackedOutlinePrefix");
            TryPatchPrefix(harmony, "MegaCrit.Sts2.Core.Localization.LocString", "GetRawText", "LocRawPrefix");
            ZLog.W("patched all");
            try
            {
                MegaCrit.Sts2.Core.Runs.RunManager.Instance.RunStarted += st2 =>
                {
                    try { if (st2 != null && st2.Players != null && st2.Players.Count > 0) Visuals.ShowDifficultyToggle(st2.Players[0]); } catch { }
                    try { Visuals.ResetTransformCount(); } catch { }
                    try { ZhijiPrefs.Load(); RefreshForceHatchCache(); ZLog.W("force hatch cache=" + _forceHatchCached); }
                    catch (Exception e) { ZLog.W("diff toggle hook fail " + e.Message); }
                };
            }
            catch (Exception e) { ZLog.W("run started hook fail " + e.Message); }
            // 调试按钮已移除
            // 预载 Osty 相关资源（否则召唤时缺资源会崩溃）
            try
            {
                foreach (var path in new[] {
                    "res://scenes/creature_visuals/osty.tscn",
                    "res://scenes/vfx/vfx_heal_osty.tscn",
                    "res://scenes/vfx/vfx_osty_death.tscn" })
                {
                    try
                    {
                        if (!Godot.ResourceLoader.Exists(path)) { ZLog.W("preload skip (missing) " + path); continue; }
                        var res = Godot.ResourceLoader.Load(path, "PackedScene");
                        ZLog.W("preload " + path + " ok=" + (res != null));
                    }
                    catch (Exception eP) { ZLog.W("preload fail " + path + " " + eP.Message); }
                }
            }
            catch { }
            StartEngineLogTimer();
        }

        static Assembly GameAsm()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == "sts2") return a; } catch { }
            }
            return null;
        }

        static void TryPatchPostfixRef(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m == null) { ZLog.W("no method " + methodName); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, postfix: new HarmonyMethod(hook));
                ZLog.W("patched-ref " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("patch fail " + typeName + "." + methodName + " " + e.Message); }
        }

        static void TryPatchPrefix(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m == null) { ZLog.W("no method " + methodName); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, prefix: new HarmonyMethod(hook));
                ZLog.W("patched-pre " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("patch fail " + typeName + "." + methodName + " " + e.Message); }
        }

        // 同名重载全部挂上（StartTargeting 有两个重载）
        static void TryPatchAllOverloads(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                int n = 0;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != methodName) continue;
                    try { harmony.Patch(m, prefix: new HarmonyMethod(hook)); n++; ZLog.W("patched-overload " + methodName + "/" + m.GetParameters().Length); }
                    catch (Exception e) { ZLog.W("overload fail " + methodName + " " + e.Message); }
                }
                if (n == 0) ZLog.W("no overloads for " + methodName);
            }
            catch (Exception e) { ZLog.W("patch all fail " + e.Message); }
        }

        static void TryPatchExact(Harmony harmony, string typeName, string methodName, int argc, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                MethodInfo found = null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name == methodName && m.GetParameters().Length == argc) { found = m; break; }
                }
                if (found == null) { ZLog.W("no method " + methodName + "/" + argc); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(found, prefix: new HarmonyMethod(hook));
                ZLog.W("patched-exact " + typeName + "." + methodName + "/" + argc);
            }
            catch (Exception e) { ZLog.W("patch exact fail " + methodName + " " + e.Message); }
        }

        static void TryPatchPostfixTwo(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m == null) { ZLog.W("no method " + methodName); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, postfix: new HarmonyMethod(hook));
                ZLog.W("patched-post2 " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("patch fail " + typeName + "." + methodName + " " + e.Message); }
        }


        // 卡池 AllCards 出口过滤：红神牌/变身卡/作弊卡永远不进奖励与商店
        // 过滤结果要缓存：奖励界面靠引用/索引匹配，每次返回新 List 会导致选牌无响应
        static bool _poolCacheUnbound;
        static readonly System.Collections.Generic.Dictionary<object, System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> _poolCache
            = new System.Collections.Generic.Dictionary<object, System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>>();

        static readonly System.Collections.Generic.HashSet<string> _summonOnlyCards = new System.Collections.Generic.HashSet<string>
        {
            "FEAST_CALL","MULTIPLY_GUEST","PROTECT_GUEST","LULLABY_GUEST","DEVOUR_GUEST",
            "FEED_GUEST","DINE_TOGETHER","SACRIFICE_GUEST",
            "UNBOUND_BITE","UNBOUND_GLUTTONY","UNBOUND_FEAST","UNBOUND_ROAR"
        };

        static bool PlayerHasUnbound()
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl == null || pl.Relics == null) return false;
                foreach (var r in pl.Relics) if (r is UnboundHeart) return true;
            }
            catch { }
            return false;
        }

        public static void PoolAllCardsPostfix(ref System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel> __result)
        {
            try
            {
                if (__result == null || _poolBuildDepth > 0) return;   // 构建卡池时不过滤
                var src = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>(__result);
                if (src.Count == 0) return;
                var key = (object)__result;
                bool hasUnbound = PlayerHasUnbound();
                if (_poolCacheUnbound != hasUnbound) { _poolCache.Clear(); _poolCacheUnbound = hasUnbound; }
                System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel> cached;
                if (_poolCache.TryGetValue(key, out cached)) { __result = cached; return; }
                var keep = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                int removed = 0;
                foreach (var c in src)
                {
                    if (c == null) continue;
                    var z = c as ZhijiCard;
                    if (z != null && z.RedGod) { removed++; continue; }   // 只滤红神牌；童年的终结/作弊卡由稀有度过滤处理
                    // 召唤/失控牌只在持有【失控之心】后出现（设定：随从是红神撕下的血肉）
                    if (!hasUnbound && _summonOnlyCards.Contains(c.Id.Entry)) { removed++; continue; }
                    keep.Add(c);
                }
                if (removed == 0) return;
                if (_poolCache.Count > 64) _poolCache.Clear();
                _poolCache[key] = keep;
                __result = keep;
                ZLog.W("pool AllCards filtered red=" + removed + " left=" + keep.Count);
            }
            catch (Exception e) { ZLog.W("pool filter fail " + e.Message); }
        }


        // 启动时打印关键 API 的签名（用于精确写 Harmony 补丁）
        public static void LogSignatures()
        {
            try
            {
                var asm = GameAsm();
                if (asm == null) { ZLog.W("SIG no asm"); return; }
                var names = new[] { "MegaCrit.Sts2.Core.Commands.CardCmd", "MegaCrit.Sts2.Core.Commands.CardPileCmd" };
                var want = new System.Collections.Generic.HashSet<string> { "Transform", "TransformTo", "TransformToRandom", "Draw", "DrawInternal", "Discard", "Exhaust", "Upgrade" };
                foreach (var tn in names)
                {
                    var t = asm.GetType(tn, false);
                    if (t == null) { ZLog.W("SIG no type " + tn); continue; }
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (!want.Contains(m.Name)) continue;
                        var parts = new System.Text.StringBuilder();
                        foreach (var pr in m.GetParameters())
                            parts.Append(pr.ParameterType.Name).Append(" ").Append(pr.Name).Append(", ");
                        ZLog.W("SIG " + t.Name + "." + m.Name + "(" + parts.ToString().TrimEnd(' ', ',') + ") -> " + m.ReturnType.Name
                            + (m.IsStatic ? " [static]" : ""));
                    }
                }
            }
            catch (Exception e) { ZLog.W("SIG fail " + e.Message); }
        }

        // 红神形态能量上限：战/守 +1，均 +3
        public static void MaxEnergyPostfix(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState __instance, ref int __result)
        {
            try
            {
                var f = typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState).GetField("_player",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var pl = f != null ? f.GetValue(__instance) as MegaCrit.Sts2.Core.Entities.Players.Player : null;
                if (pl == null || pl.Character == null || pl.Character.Id.Entry != "ZHIJI") return;
                var st = ZhijiStore.Of(pl);
                if (st.Form != ZhijiForm.RedGod) return;
                __result += st.RForm == RedForm.Balance ? 1 : 1;   // 3.3.19：平衡形态不再 +3（6 能量太多）
            }
            catch { }
        }

        public static void PoolBuildPrefix() { _poolBuildDepth++; }
        public static void PoolBuildPostfix() { if (_poolBuildDepth > 0) _poolBuildDepth--; }


        // 启动自检：确保每张自定义卡都能取到 CanonicalVars（缺了会导致开局崩溃）
        public static void SelfCheckCards()
        {
            try
            {
                int ok = 0, bad = 0;
                foreach (var t in typeof(ZhijiCard).Assembly.GetTypes())
                {
                    if (t.IsAbstract || !typeof(ZhijiCard).IsAssignableFrom(t)) continue;
                    try
                    {
                        var inst = System.Activator.CreateInstance(t) as ZhijiCard;
                        if (inst == null) { ZLog.W("SELFTEST null " + t.Name); bad++; continue; }
                        // CanonicalVars 是 protected：用反射取，确认不为 null
                        var pi = typeof(MegaCrit.Sts2.Core.Models.CardModel).GetProperty("CanonicalVars",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        var vars = pi != null ? pi.GetValue(inst) : null;
                        if (vars == null) { ZLog.W("SELFTEST NOVARS " + t.Name); bad++; }
                        else ok++;
                    }
                    catch (Exception e)
                    {
                        var ie0 = e;
                        while (ie0.InnerException != null) ie0 = ie0.InnerException;
                        ZLog.W("SELFTEST FAIL " + t.Name + " " + ie0.GetType().Name + ": " + ie0.Message + " @" + (ie0.StackTrace ?? "").Split('\n')[0]);
                        bad++;
                    }
                }
                ZLog.W("SELFTEST cards ok=" + ok + " bad=" + bad);
            }
            catch (Exception e) { ZLog.W("SELFTEST error " + e.Message); }
        }

        /// 供"难度开关"点击时立即重新强化（按开关当下的档位重算，不叠加）
        public static void ReapplyEnemyBuff()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                if (cs == null || cs.Players == null || cs.Players.Count == 0) return;
                ApplyEnemyBuff(cs.Players[0]);
            }
            catch (Exception e) { ZLog.W("reapply buff fail " + e.Message); }
        }

        // 敌人强化：在玩家第一回合开始时执行（此时敌人血量已初始化完毕）
        public static void ApplyEnemyBuff(MegaCrit.Sts2.Core.Entities.Players.Player pl)
        {
            try
            {
                if (pl == null || Visuals.BuffMode == 0) return;
                var cs = pl.Creature != null ? pl.Creature.CombatState : null;
                if (cs == null || cs.Enemies == null) return;
                int act = 1;
                try { var rs = pl.RunState; if (rs != null) act = rs.CurrentActIndex + 1; } catch { }
                int mult = Visuals.BuffMode == 2 ? 4000 : (act <= 1 ? 5 : (act == 2 ? 10 : 20));
                int n = 0;
                foreach (var e in cs.Enemies)
                {
                    if (e == null) continue;
                    object key = e;
                    if (!_enemyBaseHp.ContainsKey(key)) _enemyBaseHp[key] = (int)e.MaxHp;
                    int baseHp = _enemyBaseHp[key];
                    e.SetMaxHpInternal(baseHp * mult);
                    if (!e.IsDead) e.SetCurrentHpInternal(e.MaxHp);
                    n++;
                }
                ZLog.W("buff enemies(late): act=" + act + " x" + mult + " count=" + n);
                Visuals.RefreshEnemyHpBars();
            }
            catch (Exception e) { ZLog.W("buff late fail " + e.Message); }
        }

        // 红神形态：拦截引擎的"回合抽牌"，改由我们发放红神牌（彻底避免双份手牌）
        public static bool DrawInternalPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            decimal count,
            bool fromHandDraw,
            ref System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> __result)
        {
            try
            {
                if (player == null) return true;
                if (player.Character == null || player.Character.Id.Entry != "ZHIJI") return true;
                var st = ZhijiStore.Of(player);
                if (st.Form != ZhijiForm.RedGod) return true;
                // 永久失控：不做红神手牌替换，正常从牌组抽牌（否则召唤卡永远摸不到）
                bool unboundDraw = player.Relics != null && player.Relics.Any(r => r is UnboundHeart);
                if (unboundDraw) return true;

                __result = System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>>(
                    System.Array.Empty<MegaCrit.Sts2.Core.Models.CardModel>());

                if (fromHandDraw)
                {
                    ZLog.W("red form: turn draw intercepted");
                    __result = RedDrawAsync(player, 5);   // 引擎会 await 这个 Task，时序天然正确
                    return false;
                }
                else
                {
                    int n = count > 0m ? (int)System.Math.Ceiling(count) : 0;
                    if (n > 0) { ZLog.W("red form: extra draw " + n + " -> red cards"); AddRedCards(player, n); }
                }
                return false;
            }
            catch (Exception e) { ZLog.W("draw prefix fail " + e.Message); }
            return true;
        }


        // ===== 自动测试（仅当我手动放置 /sdcard/Download/zhiji_autotest 时启用）=====
        static bool _autoStarted;

        public static void StartAutoTestWatcher()
        {
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null || _autoWatcher != null) return;
                _autoWatcher = new Godot.Timer();
                _autoWatcher.WaitTime = 2.0;
                _autoWatcher.Autostart = true;
                _autoWatcher.Timeout += AutoTestTick;
                // 启动阶段直接加节点会失败，改为延迟挂载（AutoTestTick 本身也由 pump 驱动，节点只是保险）
                tree.Root.CallDeferred(Godot.Node.MethodName.AddChild, _autoWatcher);
                try { tree.ProcessFrame += DebugHotkeyTick; ZLog.W("process-frame hook ok"); } catch (Exception e9) { ZLog.W("process-frame hook fail " + e9.Message); }
                StartDebugPump();
                ZLog.W("autotest watcher ready");
            }
            catch (Exception e) { ZLog.W("autotest watcher fail " + e.Message); }
        }

        static Godot.Timer _autoWatcher;

        public static void AutoTestTickPublic() { AutoTestTick(); }

        static bool _f10Down;
        static bool _pumpStarted;

        // 用 SceneTreeTimer 自我重排，保证每 0.25s 跑一次（Godot Timer 在早期加节点不可靠）
        public static void StartDebugPump()
        {
            if (_pumpStarted) return;
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                _pumpStarted = true;
                Godot.SceneTreeTimer Next()
                {
                    var t = tree.CreateTimer(0.25);
                    t.Timeout += () => { try { DebugHotkeyTick(); AutoTestTick(); Visuals.TickSceneCharacters(); Visuals.SyncOverlayForUI(); Visuals.SyncStatPanel(); } catch { } Next(); };
                    return t;
                }
                Next();
                ZLog.W("debug pump started");
            }
            catch (Exception e) { ZLog.W("debug pump fail " + e.Message); }
        }

        // 调试热键（仅当 user://zhiji_debug 存在）：F10 = 饥饿拉满并塞一张「童年的终结」
        static void DebugHotkeyTick()
        {
            try
            {
                // 始终启用：避免"无存档+已解锁纪元"时单人模式被时间线按钮顶掉（原版逻辑）
                bool cmd = false;
                string cmdText = "";
                try
                {
                    var cp = Godot.ProjectSettings.GlobalizePath("user://zhiji_cmd");
                    if (System.IO.File.Exists(cp))
                    {
                        cmd = true;
                        try { cmdText = System.IO.File.ReadAllText(cp).Trim().ToLowerInvariant(); } catch { }
                        try { System.IO.File.Delete(cp); } catch { }
                        ZLog.W("debug: cmd '" + cmdText + "'");
                    }
                }
                catch { }
                if (cmd && cmdText.StartsWith("press:"))
                {
                    // 调试：按界面文字点击按钮（无头环境鼠标不可靠时用）
                    try
                    {
                        string key = cmdText.Substring(6);
                        var treeP = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                        int hit = 0;
                        bool LabelHas(Godot.Node n, string k)
                        {
                            if (n is Godot.Label lb && !string.IsNullOrEmpty(lb.Text) && lb.Text.Contains(k)) return true;
                            if (n is Godot.RichTextLabel rl && !string.IsNullOrEmpty(rl.Text) && rl.Text.Contains(k)) return true;
                            foreach (var c in n.GetChildren()) { if (LabelHas(c, k)) return true; }
                            return false;
                        }
                        bool AncestorsVisible(Godot.Node n)
                        {
                            try
                            {
                                var p = n.GetParent();
                                int guard = 0;
                                while (p != null && guard++ < 32)
                                {
                                    if (p is Godot.CanvasItem ci2 && !ci2.Visible) return false;
                                    p = p.GetParent();
                                }
                            }
                            catch { }
                            return true;
                        }
                        void Walk(Godot.Node n)
                        {
                            if (n == null) return;
                            if (n is Godot.BaseButton b && (b.Visible) && !b.Disabled && AncestorsVisible(b))
                            {
                                string btext = (b as Godot.Button)?.Text;
                                bool textHit = !string.IsNullOrEmpty(btext) && btext.Contains(key);
                                bool labelHit = !textHit && LabelHas(b, key);
                                if (textHit || labelHit)
                                {
                                    b.CallDeferred(Godot.BaseButton.MethodName.EmitSignal, Godot.BaseButton.SignalName.Pressed);
                                    hit++;
                                    ZLog.W("debug: press hit " + b.GetType().Name + " label=" + labelHit);
                                }
                            }
                            else if (n is Godot.Control ctl && ctl.Visible && AncestorsVisible(ctl) && LabelHas(n, key))
                            {
                                // 自定义控件（如事件选项 NEventOptionButton）：反射调用 OnRelease/Press
                                foreach (var mn in new[] { "OnRelease", "Press", "OnPress", "Clicked" })
                                {
                                    var mi = n.GetType().GetMethod(mn, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (mi == null || mi.GetParameters().Length != 0) continue;
                                    try { mi.Invoke(n, null); hit++; ZLog.W("debug: press ctl " + n.GetType().Name + "." + mn); }
                                    catch (Exception ei) { ZLog.W("debug: press ctl fail " + ei.Message); }
                                    break;
                                }
                            }
                            foreach (var c in n.GetChildren()) Walk(c);
                        }
                        if (treeP != null) Walk(treeP.Root);
                        ZLog.W("debug: press '" + key + "' -> " + hit);
                    }
                    catch (Exception eP) { ZLog.W("debug press fail " + eP.Message); }
                }
                if (cmd && (cmdText == "next" || cmdText == "n"))
                {
                    try
                    {
                        bool mapOk = false;
                        try { var msN = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance; mapOk = msN != null && Godot.GodotObject.IsInstanceValid(msN) && msN.Visible; } catch { }
                        if (!mapOk)
                        {
                            ZLog.W("debug: travel skipped (map not visible)");
                        }
                        else
                        {
                        var rmN = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsN = rmN != null ? rmN.DebugOnlyGetState() : null;
                        var curN = rsN != null ? rsN.CurrentMapPoint : null;
                        if (curN == null && rsN != null && rsN.Map != null) curN = rsN.Map.StartingMapPoint;
                        MegaCrit.Sts2.Core.Map.MapPoint childN = null;
                        if (curN != null && curN.Children != null)
                            foreach (var cc in curN.Children) { if (cc != null) { childN = cc; break; } }
                        if (childN != null)
                        {
                            ZLog.W("debug: travel -> " + childN.coord);
                            _ = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance.TravelToMapCoord(childN.coord);
                        }
                        else ZLog.W("debug: no child node");
                        }
                    }
                    catch (Exception eN) { ZLog.W("debug travel fail " + eN.Message); }
                }
                if (cmd && (cmdText == "arch" || cmdText == "architect"))
                {
                    try
                    {
                        var rmA2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsA = rmA2 != null ? rmA2.DebugOnlyGetState() : null;
                        var curA = rsA != null ? rsA.CurrentRoom : null;
                        string curName = curA != null ? curA.GetType().Name : "null";
                        bool mapVisible = false;
                        try { var ms = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance; mapVisible = ms != null && Godot.GodotObject.IsInstanceValid(ms) && ms.Visible; } catch { }
                        if (curName != "MapRoom" && !mapVisible)
                        {
                            ZLog.W("arch: not on map (" + curName + ", ui=" + mapVisible + ") -> ignored");
                        }
                        else
                        {
                            MegaCrit.Sts2.Core.Models.EventModel evModel = null;
                            try
                            {
                                var gm = typeof(MegaCrit.Sts2.Core.Models.ModelDb).GetMethod("Event", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                var generic = gm != null ? gm.MakeGenericMethod(new[] { GameAsm().GetType("MegaCrit.Sts2.Core.Models.Events.TheArchitect", true) }) : null;
                                evModel = generic != null ? generic.Invoke(null, null) as MegaCrit.Sts2.Core.Models.EventModel : null;
                            }
                            catch (Exception eG) { ZLog.W("arch model fail " + eG.Message); }
                            if (evModel == null) ZLog.W("arch: no event model");
                            else
                            {
                                ZLog.W("arch: entering architect event");
                                _ = rmA2.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Ancient, evModel, true);
                            }
                        }
                    }
                    catch (Exception eA2) { ZLog.W("arch fail " + eA2.Message); }
                }
                if (cmd && cmdText == "fmttest")
                {
                    try
                    {
                        var ls = new MegaCrit.Sts2.Core.Localization.LocString("cards", "DRIVE.description");
                        ls.Add(new MegaCrit.Sts2.Core.Localization.DynamicVars.DamageVar(9m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move));
                        ls.Add(new MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar(7m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move));
                        ZLog.W("fmttest raw=" + ls.GetRawText().Replace("\n", " / "));
                        ZLog.W("fmttest fmt=" + ls.GetFormattedText().Replace("\n", " / "));
                    }
                    catch (Exception eF) { ZLog.W("fmttest fail " + eF.Message); }
                }
                if (cmd && cmdText == "goldshroud")
                {
                    try
                    {
                        var cmG2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csG2 = cmG2 != null ? cmG2.DebugOnlyGetState() : null;
                        var plG2 = csG2 != null && csG2.Players != null && csG2.Players.Count > 0 ? csG2.Players[0] : null;
                        if (plG2 != null) { var stG2 = ZhijiStore.Of(plG2); stG2.GoldShroud = !stG2.GoldShroud; ZLog.W("gold shroud=" + stG2.GoldShroud); }
                    }
                    catch (Exception eGS) { ZLog.W("goldshroud fail " + eGS.Message); }
                }
                if (cmd && cmdText == "relic:dinner_bell")
                {
                    try
                    {
                        var cmD2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csD2 = cmD2 != null ? cmD2.DebugOnlyGetState() : null;
                        var plD2 = csD2 != null && csD2.Players != null && csD2.Players.Count > 0 ? csD2.Players[0] : null;
                        if (plD2 != null)
                        {
                            var trD2 = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<DinnerBell>(plD2);
                            _ = trD2.ContinueWith(x => ZLog.W("debug relic DINNER_BELL ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eDb) { ZLog.W("debug dinner bell fail " + eDb.Message); }
                }
                if (cmd && cmdText == "relic:carving_knife")
                {
                    try
                    {
                        var cmK = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csK = cmK != null ? cmK.DebugOnlyGetState() : null;
                        var plK = csK != null && csK.Players != null && csK.Players.Count > 0 ? csK.Players[0] : null;
                        if (plK != null) { var trK = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<CarvingKnife>(plK); _ = trK.ContinueWith(x => ZLog.W("debug relic KNIFE ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion))); }
                    }
                    catch (Exception eK) { ZLog.W("debug knife fail " + eK.Message); }
                }
                if (cmd && (cmdText == "relic:crimson_visor" || cmdText == "relic:blood_hourglass"))
                {
                    try
                    {
                        var cmX = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csX = cmX != null ? cmX.DebugOnlyGetState() : null;
                        var plX = csX != null && csX.Players != null && csX.Players.Count > 0 ? csX.Players[0] : null;
                        if (plX != null)
                        {
                            if (cmdText == "relic:crimson_visor") _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<CrimsonVisor>(plX);
                            else _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BloodHourglass>(plX);
                            ZLog.W("debug relic " + cmdText);
                        }
                    }
                    catch (Exception eX) { ZLog.W("debug relic fail " + eX.Message); }
                }
                if (cmd && (cmdText == "relic:vile_purse" || cmdText == "relic:hunger_bell"))
                {
                    try
                    {
                        var cmP = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csP = cmP != null ? cmP.DebugOnlyGetState() : null;
                        var plP = csP != null && csP.Players != null && csP.Players.Count > 0 ? csP.Players[0] : null;
                        if (plP != null)
                        {
                            if (cmdText == "relic:vile_purse") _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<VilePurse>(plP);
                            else _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<HungerBell>(plP);
                            ZLog.W("debug relic " + cmdText);
                        }
                    }
                    catch (Exception eP2) { ZLog.W("debug purse fail " + eP2.Message); }
                }
                if (cmd && (cmdText == "relic:vile_watch" || cmdText == "relic:vile_crown"))
                {
                    try
                    {
                        var cmV = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csV = cmV != null ? cmV.DebugOnlyGetState() : null;
                        var plV = csV != null && csV.Players != null && csV.Players.Count > 0 ? csV.Players[0] : null;
                        if (plV != null)
                        {
                            if (cmdText == "relic:vile_watch") _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<VileWatch>(plV);
                            else _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<VileCrown>(plV);
                            ZLog.W("debug relic " + cmdText);
                        }
                    }
                    catch (Exception eV) { ZLog.W("debug vile relic fail " + eV.Message); }
                }
                if (cmd && (cmdText == "relic:blood_funnel" || cmdText == "relic:black_napkin"))
                {
                    try
                    {
                        var cmR = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csR = cmR != null ? cmR.DebugOnlyGetState() : null;
                        var plR = csR != null && csR.Players != null && csR.Players.Count > 0 ? csR.Players[0] : null;
                        if (plR != null)
                        {
                            if (cmdText == "relic:blood_funnel") _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BloodFunnel>(plR);
                            else _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BlackNapkin>(plR);
                            ZLog.W("debug relic " + cmdText);
                        }
                    }
                    catch (Exception eRN) { ZLog.W("debug relic fail " + eRN.Message); }
                }
                if (cmd && cmdText == "relic:blood_nail")
                {
                    try
                    {
                        var cmN = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csN = cmN != null ? cmN.DebugOnlyGetState() : null;
                        var plN = csN != null && csN.Players != null && csN.Players.Count > 0 ? csN.Players[0] : null;
                        if (plN != null) { var trN = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BloodNail>(plN); _ = trN.ContinueWith(x => ZLog.W("debug relic NAIL ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion))); }
                    }
                    catch (Exception eN) { ZLog.W("debug nail fail " + eN.Message); }
                }
                if (cmd && cmdText == "relic:blood_goblet")
                {
                    try
                    {
                        var cmG = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csG = cmG != null ? cmG.DebugOnlyGetState() : null;
                        var plG = csG != null && csG.Players != null && csG.Players.Count > 0 ? csG.Players[0] : null;
                        if (plG != null) { var trG = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BloodGoblet>(plG); _ = trG.ContinueWith(x => ZLog.W("debug relic GOBLET ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion))); }
                    }
                    catch (Exception eG) { ZLog.W("debug goblet fail " + eG.Message); }
                }
                if (cmd && cmdText == "relic:endless_plate")
                {
                    try
                    {
                        var cmP = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csP2 = cmP != null ? cmP.DebugOnlyGetState() : null;
                        var plP = csP2 != null && csP2.Players != null && csP2.Players.Count > 0 ? csP2.Players[0] : null;
                        if (plP != null) { var trP = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<EndlessPlate>(plP); _ = trP.ContinueWith(x => ZLog.W("debug relic PLATE ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion))); }
                    }
                    catch (Exception eP) { ZLog.W("debug plate fail " + eP.Message); }
                }
                if (cmd && cmdText == "relic:bleed_board")
                {
                    try
                    {
                        var cmB = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csB = cmB != null ? cmB.DebugOnlyGetState() : null;
                        var plB = csB != null && csB.Players != null && csB.Players.Count > 0 ? csB.Players[0] : null;
                        if (plB != null)
                        {
                            var trB = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<BleedBoard>(plB);
                            _ = trB.ContinueWith(x => ZLog.W("debug relic BLEED_BOARD ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eBd) { ZLog.W("debug bleed board fail " + eBd.Message); }
                }
                if (cmd && cmdText == "relic:golden_shroud")
                {
                    try
                    {
                        var cmR2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csR2 = cmR2 != null ? cmR2.DebugOnlyGetState() : null;
                        var plR2 = csR2 != null && csR2.Players != null && csR2.Players.Count > 0 ? csR2.Players[0] : null;
                        if (plR2 != null)
                        {
                            var rm = MegaCrit.Sts2.Core.Models.ModelDb.Relic<GoldenShroud>();
                            if (rm != null)
                            {
                                var tr = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<GoldenShroud>(plR2);
                                _ = tr.ContinueWith(x => ZLog.W("debug relic GOLDEN_SHROUD ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                            }
                        }
                    }
                    catch (Exception eRl) { ZLog.W("debug relic fail " + eRl.Message); }
                }
                if (cmd && cmdText == "diner")
                {
                    try
                    {
                        var rmD = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsD = rmD != null ? rmD.DebugOnlyGetState() : null;
                        if (rsD != null)
                        {
                            var evD = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.WoodCarvings>();
                            MegaCrit.Sts2.Core.Map.MapCoord coord = rsD.CurrentMapCoord.HasValue ? rsD.CurrentMapCoord.Value : new MegaCrit.Sts2.Core.Map.MapCoord(0, 0);
                            var tD = rmD.EnterMapCoordDebug(coord, MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evD, true);
                            _ = tD.ContinueWith(x => ZLog.W("debug diner opened ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                        }
                    }
                    catch (Exception eD2) { ZLog.W("debug diner fail " + eD2.Message); }
                }
                if (cmd && cmdText.StartsWith("bleed:"))
                {
                    try
                    {
                        int nB = 5; int.TryParse(cmdText.Substring(6), out nB);
                        var cmBl = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csBl = cmBl != null ? cmBl.DebugOnlyGetState() : null;
                        if (csBl != null && csBl.Enemies != null)
                        {
                            foreach (var e in csBl.Enemies)
                            {
                                if (e == null || e.IsDead) continue;
                                _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<BleedPower>(null, e, nB, e, null, true);
                                ZLog.W("debug bleed +" + nB + " -> " + e.GetPowerAmount<BleedPower>() + " on " + e.Name);
                                break;
                            }
                        }
                    }
                    catch (Exception eBl) { ZLog.W("debug bleed fail " + eBl.Message); }
                }
                if (cmd && cmdText == "deck")
                {
                    try
                    {
                        var rmDk = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsDk = rmDk != null ? rmDk.DebugOnlyGetState() : null;
                        var plDk = rsDk != null && rsDk.Players != null && rsDk.Players.Count > 0 ? rsDk.Players[0] : null;
                        if (plDk != null && plDk.Deck != null)
                        {
                            var sb = new System.Text.StringBuilder();
                            foreach (var c in plDk.Deck.Cards) sb.Append(c.Id.Entry + " ");
                            ZLog.W("deck(" + plDk.Deck.Cards.Count + "): " + sb.ToString());
                        }
                    }
                    catch (Exception eDk) { ZLog.W("deck fail " + eDk.Message); }
                }
                if (cmd && cmdText.StartsWith("ticks:"))
                {
                    try
                    {
                        int nT = 0; int.TryParse(cmdText.Substring(6), out nT);
                        var cmT = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csT = cmT != null ? cmT.DebugOnlyGetState() : null;
                        var plT = csT != null && csT.Players != null && csT.Players.Count > 0 ? csT.Players[0] : null;
                        if (plT != null) { ZhijiStore.Of(plT).BleedTicks = nT; ZLog.W("debug ticks=" + nT); }
                    }
                    catch (Exception eT) { ZLog.W("debug ticks fail " + eT.Message); }
                }
                if (cmd && cmdText == "proceed")
                {
                    try
                    {
                        var tNR = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", false) : null;
                        if (tNR != null)
                        {
                            var instP = tNR.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                            if (instP != null)
                            {
                                var mP = tNR.GetMethod("Proceed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (mP != null) { mP.Invoke(instP, null); ZLog.W("debug proceed invoked"); }
                                else ZLog.W("debug proceed: no method");
                            }
                            else ZLog.W("debug proceed: no room instance");
                        }
                    }
                    catch (Exception eP3) { ZLog.W("debug proceed fail " + eP3.GetBaseException().Message); }
                }
                if (cmd && cmdText == "scene:off") { Visuals.SceneEnabled = false; ZLog.W("scene sprites off"); }
                if (cmd && cmdText == "scene:on") { Visuals.SceneEnabled = true; ZLog.W("scene sprites on"); }
                if (cmd && cmdText == "proceedroom")
                {
                    try
                    {
                        var treeP = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                        int hitP = 0;
                        if (treeP != null) hitP = PressProceedButtons(treeP.Root);
                        ZLog.W("proceedroom hit=" + hitP);
                    }
                    catch (Exception eP) { ZLog.W("proceedroom fail " + eP.GetBaseException().Message); }
                }
                if (cmd && cmdText == "shopinfo")
                {
                    try
                    {
                        var rmSI = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsSI = rmSI != null ? rmSI.DebugOnlyGetState() : null;
                        var roomSI = rsSI != null ? rsSI.CurrentRoom : null;
                        ZLog.W("shopinfo room=" + (roomSI != null ? roomSI.GetType().Name : "null"));
                        if (roomSI != null)
                        {
                            var gm = roomSI.GetType().GetMethod("GetLocalInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var inv = gm != null ? gm.Invoke(roomSI, null) : null;
                            if (inv == null) ZLog.W("shopinfo inv null");
                            else
                            {
                                var allP = inv.GetType().GetProperty("AllEntries");
                                var all = allP != null ? allP.GetValue(inv) as System.Collections.IEnumerable : null;
                                int n = 0, stocked = 0;
                                var sb = new System.Text.StringBuilder();
                                if (all != null)
                                {
                                    foreach (var e in all)
                                    {
                                        n++;
                                        var crP = e.GetType().GetProperty("CreationResult");
                                        var cr = crP != null ? crP.GetValue(e) : null;
                                        var stP = e.GetType().GetProperty("IsStocked");
                                        bool st = stP != null && (bool)stP.GetValue(e);
                                        if (st) stocked++;
                                        if (n <= 12)
                                        {
                                            string card = "null";
                                            if (cr != null)
                                            {
                                                var cardP = cr.GetType().GetProperty("Card");
                                                var c = cardP != null ? cardP.GetValue(cr) : null;
                                                if (c != null) card = (string)c.GetType().GetProperty("Id").GetValue(c).ToString();
                                            }
                                            sb.Append(e.GetType().Name).Append(":").Append(st ? "S" : "-").Append(":").Append(card).Append(" ");
                                        }
                                    }
                                }
                                ZLog.W("shopinfo entries=" + n + " stocked=" + stocked + " | " + sb.ToString());
                            }
                        }
                    }
                    catch (Exception eSI) { ZLog.W("shopinfo fail " + eSI.GetBaseException().Message); }
                }
                if (cmd && cmdText == "shroud:off")
                {
                    try
                    {
                        var rmS = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsS2 = rmS != null ? rmS.DebugOnlyGetState() : null;
                        var plS = rsS2 != null && rsS2.Players != null && rsS2.Players.Count > 0 ? rsS2.Players[0] : null;
                        if (plS != null)
                        {
                            var found = new System.Collections.Generic.List<GoldenShroud>();
                            if (plS.Relics != null) foreach (var r in plS.Relics) { if (r is GoldenShroud g) found.Add(g); }
                            foreach (var g in found) { try { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Remove(g); } catch { } }
                            ZLog.W("shroud:off removed " + found.Count + " relic(s)");
                            var tOff = (Godot.Engine.GetMainLoop() as Godot.SceneTree)?.CreateTimer(0.6);
                            if (tOff != null) tOff.Timeout += () => { try { ZhijiStore.Of(plS).GoldShroud = false; ZLog.W("shroud:off flag cleared"); } catch { } };
                        }
                    }
                    catch (Exception eSO) { ZLog.W("shroud:off fail " + eSO.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("frenzy:"))
                {
                    try
                    {
                        string wantF = cmdText.Substring(7).Trim().ToUpperInvariant();
                        var plF = DebugPlayer();
                        MegaCrit.Sts2.Core.Models.CardModel cardF = null;
                        if (plF != null)
                        {
                            var handF = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plF);
                            if (handF != null) foreach (var c in handF.Cards) if (c != null && c.Id.Entry == wantF) { cardF = c; break; }
                        }
                        if (cardF == null) ZLog.W("frenzy: card not in hand");
                        else
                        {
                            var enchF = MegaCrit.Sts2.Core.Commands.CardCmd.Enchant<UnboundRage>(cardF, 1m);
                            ZLog.W("frenzy enchant " + wantF + " -> " + (enchF != null ? enchF.Id.Entry : "null"));
                        }
                    }
                    catch (Exception eF) { ZLog.W("frenzy fail " + eF.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("enchant:"))
                {
                    try
                    {
                        string wantE = cmdText.Substring(8).Trim().ToUpperInvariant();
                        var cmE = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csE = cmE != null ? cmE.DebugOnlyGetState() : null;
                        var plE = csE != null && csE.Players != null && csE.Players.Count > 0 ? csE.Players[0] : null;
                        if (plE != null && plE.Creature != null)
                        {
                            MegaCrit.Sts2.Core.Models.CardModel cardE = null;
                            try { if (plE != null) { var handE = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plE); if (handE != null) foreach (var c in handE.Cards) if (c != null && c.Id.Entry == wantE) { cardE = c; break; } } } catch { }
                            if (cardE != null)
                            {
                                var ench = MegaCrit.Sts2.Core.Commands.CardCmd.Enchant<MegaCrit.Sts2.Core.Models.Enchantments.Sharp>(cardE, 2m);
                                ZLog.W("debug enchant " + wantE + " -> " + (ench != null ? ench.Id.Entry : "null"));
                            }
                            else ZLog.W("debug enchant: card not in hand");
                        }
                    }
                    catch (Exception eE) { ZLog.W("debug enchant fail " + eE.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("giveup:"))
                {
                    try
                    {
                        string wantU = cmdText.Substring(7).Trim().ToUpperInvariant();
                        var cmU2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csU2 = cmU2 != null ? cmU2.DebugOnlyGetState() : null;
                        var plU2 = csU2 != null && csU2.Players != null && csU2.Players.Count > 0 ? csU2.Players[0] : null;
                        if (plU2 != null && csU2 != null)
                        {
                            MegaCrit.Sts2.Core.Models.CardModel protoU = null;
                            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards) if (c != null && c.Id.Entry == wantU) { protoU = c; break; }
                            if (protoU != null)
                            {
                                var instU = csU2.CreateCard(protoU, plU2);
                                instU.UpgradeInternal();
                                try { instU.FinalizeUpgradeInternal(); } catch { }
                                _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(instU,
                                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                                ZLog.W("debug giveup " + wantU + " up=" + instU.IsUpgraded);
                            }
                        }
                    }
                    catch (Exception eGU) { ZLog.W("giveup fail " + eGU.GetBaseException().Message); }
                }
                if (cmd && cmdText == "poolcount")
                {
                    try
                    {
                        var pool = MegaCrit.Sts2.Core.Models.ModelDb.CardPool<ZhijiCardPool>();
                        int total = 0, vanilla = 0;
                        var sb = new System.Text.StringBuilder();
                        if (pool != null && pool.AllCards != null)
                            foreach (var c in pool.AllCards)
                            {
                                total++;
                                if (!(c is ZhijiCard)) { vanilla++; if (vanilla <= 12) sb.Append(c.Id.Entry).Append(" "); }
                            }
                        ZLog.W("poolcount total=" + total + " vanilla=" + vanilla + " | " + sb.ToString());
                    }
                    catch (Exception ePC) { ZLog.W("poolcount fail " + ePC.GetBaseException().Message); }
                }
                if (cmd && cmdText == "church")
                {
                    try
                    {
                        var evD2 = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.DollRoom>();
                        if (evD2 != null)
                        {
                            var rmD2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var tD2 = rmD2.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evD2, true);
                            _ = tD2.ContinueWith(x => ZLog.W("church room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eC2) { ZLog.W("church open fail " + eC2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "hatch")
                {
                    try
                    {
                        var evH2 = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Reflections>();
                        if (evH2 != null)
                        {
                            var tH2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evH2, true);
                            _ = tH2.ContinueWith(x => ZLog.W("hatch room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eH3) { ZLog.W("hatch open fail " + eH3.GetBaseException().Message); }
                }
                if (cmd && cmdText == "festival")
                {
                    try
                    {
                        var evF = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.SelfHelpBook>();
                        if (evF != null)
                        {
                            var tF = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evF, true);
                            _ = tF.ContinueWith(x => ZLog.W("festival room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eF2) { ZLog.W("festival open fail " + eF2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "council")
                {
                    try
                    {
                        var evC = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.RanwidTheElder>();
                        if (evC != null)
                        {
                            var tC = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evC, true);
                            _ = tC.ContinueWith(x => ZLog.W("council room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eC2) { ZLog.W("council open fail " + eC2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "theater")
                {
                    try
                    {
                        var evT = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.TeaMaster>();
                        if (evT != null)
                        {
                            var tT = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evT, true);
                            _ = tT.ContinueWith(x => ZLog.W("theater room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eT2) { ZLog.W("theater open fail " + eT2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "gift")
                {
                    try
                    {
                        var evG2 = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.SunkenStatue>();
                        if (evG2 != null)
                        {
                            var tG2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evG2, true);
                            _ = tG2.ContinueWith(x => ZLog.W("gift room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eG3) { ZLog.W("gift open fail " + eG3.GetBaseException().Message); }
                }
                if (cmd && cmdText == "market")
                {
                    try
                    {
                        var evM = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.SlipperyBridge>();
                        if (evM != null)
                        {
                            var tM = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evM, true);
                            _ = tM.ContinueWith(x => ZLog.W("market room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eM2) { ZLog.W("market open fail " + eM2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "altar")
                {
                    try
                    {
                        var evA = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.WhisperingHollow>();
                        if (evA != null)
                        {
                            var tA = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evA, true);
                            _ = tA.ContinueWith(x => ZLog.W("altar room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eA) { ZLog.W("altar open fail " + eA.GetBaseException().Message); }
                }
                if (cmd && cmdText == "autoplay")
                {
                    try { var plAp = DebugPlayer(); if (plAp != null) { ZhijiStore.Once(plAp, "force_auto"); ZLog.W("debug autoplay armed"); } } catch { }
                }
                if (cmd && cmdText.StartsWith("popup:"))
                {
                    try { Visuals.Popup(cmdText.Substring(6), new Godot.Color(1f, 0.25f, 0.2f)); ZLog.W("debug popup"); } catch { }
                }
                if (cmd && cmdText.StartsWith("pulse:"))
                {
                    try
                    {
                        int nP = 0; int.TryParse(cmdText.Substring(6), out nP);
                        var plP2 = DebugPlayer();
                        if (plP2 != null)
                        {
                            var stP = ZhijiStore.Of(plP2);
                            int before = stP.Pulse, beforeH = stP.Hunger;
                            stP.AddPulse(nP);
                            ZLog.W("debug pulse +" + nP + " : pulse " + before + "->" + stP.Pulse + " hunger " + beforeH + "->" + stP.Hunger);
                        }
                    }
                    catch (Exception eP2) { ZLog.W("debug pulse fail " + eP2.Message); }
                }
                if (cmd && cmdText.StartsWith("hunger:"))
                {
                    try
                    {
                        int nH = 0; int.TryParse(cmdText.Substring(7), out nH);
                        var plH = DebugPlayer();
                        if (plH != null) { ZhijiStore.Of(plH).Hunger = nH; ZLog.W("debug hunger=" + nH); }
                    }
                    catch (Exception eH) { ZLog.W("debug hunger fail " + eH.Message); }
                }
                if (cmd && cmdText.StartsWith("hurt:"))
                {
                    try
                    {
                        int nHurt = 0; int.TryParse(cmdText.Substring(5), out nHurt);
                        var plHu = DebugPlayer();
                        if (plHu != null && plHu.Creature != null && nHurt > 0)
                        { plHu.Creature.LoseHpInternal(nHurt, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); ZLog.W("debug hurt " + nHurt); }
                    }
                    catch (Exception eHu) { ZLog.W("debug hurt fail " + eHu.Message); }
                }
                // 临时调试：逐个试引擎的「死亡/战败」入口（定案后删掉）
                if (cmd && cmdText == "t:asstop")
                {
                    try
                    {
                        if (_autoslayer == null) { ZLog.W("t:asstop: no inst"); }
                        else
                        {
                            var mStop = _autoslayer.GetType().GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);
                            mStop?.Invoke(_autoslayer, null);
                            ZLog.W("t:asstop ok");
                        }
                    }
                    catch (Exception eSt) { ZLog.W("t:asstop fail " + eSt.GetBaseException().Message); }
                }
                if (cmd && cmdText == "t:autoslay")
                {
                    // 引擎自带 AutoSlayer：程序化开局并自动打牌（无需鼠标输入，适合 GPU 性能测试）
                    try
                    {
                        var asT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", false) : null;
                        if (asT == null) { ZLog.W("t:autoslay: type not found"); }
                        else
                        {
                            var inst = Activator.CreateInstance(asT);
                            _autoslayer = inst;
                            var startM = asT.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                            var logPath = System.IO.Path.Combine(ZLog.LogDirPublic() ?? "/tmp", "autoslay.log");
                            startM.Invoke(inst, new object[] { "zhiji_perf", logPath });
                            ZLog.W("t:autoslay started log=" + logPath);
                        }
                    }
                    catch (Exception eAS) { ZLog.W("t:autoslay fail " + eAS.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:"))
                {
                    try
                    {
                        string which = cmdText.Substring(2);
                        var plT = DebugPlayer();
                        var cmT = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        ZLog.W("t:" + which + " start hp=" + (plT?.Creature?.CurrentHp.ToString() ?? "null")
                            + " dead=" + (plT?.Creature?.IsDead.ToString() ?? "-")
                            + " inProgress=" + (cmT?.IsInProgress.ToString() ?? "-")
                            + " aboutToLose=" + (cmT?.IsAboutToLose.ToString() ?? "-")
                            + " ending=" + (cmT?.IsEnding.ToString() ?? "-"));
                        if (plT == null) { ZLog.W("t: no player"); }
                        else if (which == "kill")
                        {
                            _ = MegaCrit.Sts2.Core.Commands.CreatureCmd.Kill(plT.Creature, true)
                                .ContinueWith(t => ZLog.W("t:kill done=" + t.IsCompleted + " err=" + (t.Exception?.GetBaseException().Message ?? "-")));
                            ZLog.W("t:kill fired");
                        }
                        else if (which == "invoke")
                        {
                            plT.Creature.InvokeDiedEvent();
                            ZLog.W("t:invoke done");
                        }
                        else if (which == "endrun")
                        {
                            var rmT = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var rT = rmT.OnEnded(false);
                            ZLog.W("t:endrun returned " + (rT?.GetType().Name ?? "null"));
                        }
                        else if (which == "pending" && cmT != null)
                        {
                            var tsT0 = LiveTurnState(cmT);
                            bool okT = MarkPendingLoss(cmT, tsT0);
                            ZLog.W("t:pending ok=" + okT + " ts=" + (tsT0 == null ? "null" : "ok") + " aboutToLose=" + (cmT.IsAboutToLose.ToString()));
                        }
                        else if (which == "process" && cmT != null)
                        {
                            var tsT = LiveTurnState(cmT);
                            bool okT = ProcessPendingLoss(cmT, tsT);
                            ZLog.W("t:process ok=" + okT + " aboutToLose=" + cmT.IsAboutToLose + " ending=" + cmT.IsEnding);
                        }
                        else if (which == "over")
                        {
                            bool okT = ShowDefeatScreen();
                            ZLog.W("t:over ok=" + okT);
                        }
                        else if (which == "heal")
                        {
                            var csH = cmT != null ? cmT.DebugOnlyGetState() : null;
                            int nH = 0;
                            if (csH != null && csH.Enemies != null)
                                foreach (var eH in csH.Enemies)
                                {
                                    if (eH == null || eH.IsDead) continue;
                                    int before = eH.CurrentHp;
                                    // 先扣 10 再回 10：能直观看出治疗是否被拦截
                                    eH.LoseHpInternal(10, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                    int mid = eH.CurrentHp;
                                    eH.HealInternal(10);
                                    ZLog.W("t:heal enemy hp " + before + " -10 -> " + mid + " +10 -> " + eH.CurrentHp
                                        + " max=" + eH.MaxHp + " bleed=" + eH.GetPowerAmount<BleedPower>());
                                    nH++;
                                    if (nH >= 1) break;
                                }
                            ZLog.W("t:heal done n=" + nH);
                        }
                        else if (which == "anim" || which == "animsync" || which == "animback")
                        {
                            try
                            {
                                Visuals.NoPrefetch = (which == "animsync");
                                Visuals.TransformPlayed = 0;      // 允许重复播放全屏演出
                                Visuals.PlayTransform(which == "animback" ? "transform_back" : "transform");
                                ZLog.W("t:" + which + " started noPrefetch=" + Visuals.NoPrefetch);
                            }
                            catch (Exception eA2) { ZLog.W("t:anim fail " + eA2.GetBaseException().Message); }
                        }
                        else if (which == "upaudit")
                        {
                            // 逐张升级所有吱咿卡，找出「升级后数值/费用都没变」的静默无效升级
                            try
                            {
                                var rmU = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                                var rsU = rmU != null ? rmU.DebugOnlyGetState() : null;
                                var plU = rsU != null && rsU.Players != null && rsU.Players.Count > 0 ? rsU.Players[0] : null;
                                if (plU == null) { ZLog.W("t:upaudit no player"); }
                                else
                                {
                                    int total = 0, noop = 0, failed = 0;
                                    var noopNames = new System.Text.StringBuilder();
                                    foreach (var canon in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                                    {
                                        if (!(canon is ZhijiCard)) continue;
                                        if (canon.MaxUpgradeLevel <= 0) continue;
                                        total++;
                                        try
                                        {
                                            var inst = rsU.CreateCard(canon, plU);
                                            if (inst == null) { failed++; continue; }
                                            string sig0 = CardSig(inst);
                                            MegaCrit.Sts2.Core.Commands.CardCmd.Upgrade(inst, MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle.None);
                                            string sig1 = CardSig(inst);
                                            if (sig0 == sig1) { noop++; noopNames.Append(canon.Id.Entry + " "); }
                                        }
                                        catch (Exception eUA) { failed++; if (failed <= 3) ZLog.W("upaudit fail " + canon.Id.Entry + " " + eUA.GetBaseException().Message); }
                                    }
                                    ZLog.W("t:upaudit total=" + total + " noop=" + noop + " failed=" + failed + " noopCards=[" + noopNames.ToString().Trim() + "]");
                                }
                            }
                            catch (Exception eU2) { ZLog.W("t:upaudit fail " + eU2.GetBaseException().Message); }
                        }
                        else if (which == "mem")
                        {
                            try
                            {
                                double fps = Godot.Engine.GetFramesPerSecond();
                                double memStatic = Godot.Performance.GetMonitor(Godot.Performance.Monitor.MemoryStatic);
                                double objs = Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectCount);
                                double nodes = Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectNodeCount);
                                double orphans = Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectOrphanNodeCount);
                                double res = Godot.Performance.GetMonitor(Godot.Performance.Monitor.ObjectResourceCount);
                                double draws = Godot.Performance.GetMonitor(Godot.Performance.Monitor.RenderTotalDrawCallsInFrame);
                                long managed = System.GC.GetTotalMemory(false);
                                ZLog.W("t:mem fps=" + fps.ToString("0.0")
                                    + " memStaticMB=" + ((long)memStatic / 1048576)
                                    + " objs=" + (long)objs + " nodes=" + (long)nodes + " orphans=" + (long)orphans
                                    + " res=" + (long)res + " draws=" + (long)draws
                                    + " managedMB=" + (managed / 1048576)
                                    + " enemyHpCache=" + EnemyBaseHpCount()
                                    + " logLines=" + ZLog.LinesWritten + " logFlushes=" + ZLog.FlushCount);
                            }
                            catch (Exception eM2) { ZLog.W("t:mem fail " + eM2.GetBaseException().Message); }
                        }
                        else if (which.StartsWith("fps"))
                        {
                            try
                            {
                                double fps = Godot.Engine.GetFramesPerSecond();
                                long staticMem = (long)Godot.OS.GetStaticMemoryUsage();
                                string driver = "-";
                                try { driver = Godot.RenderingServer.GetVideoAdapterName(); } catch { }
                                ZLog.W("t:fps fps=" + fps.ToString("0.0") + " staticMemMB=" + (staticMem / 1048576)
                                    + " adapter=" + driver);
                            }
                            catch (Exception eF) { ZLog.W("t:fps fail " + eF.GetBaseException().Message); }
                        }
                        else if (which == "report")
                        {
                            // 生成本地反馈包：状态 + 遗物 + 牌组 + 敌人 + 最近日志
                            try
                            {
                                var sbR = new System.Text.StringBuilder();
                                var rmR = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                                var rsR = rmR != null ? rmR.DebugOnlyGetState() : null;
                                var plR = rsR != null && rsR.Players != null && rsR.Players.Count > 0 ? rsR.Players[0] : null;
                                sbR.AppendLine("=== zhiji report ===");
                                sbR.AppendLine("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                                sbR.AppendLine("build: " + typeof(ZhijiPatcher).Assembly.GetName().Version);
                                if (plR != null)
                                {
                                    var stR = ZhijiStore.Of(plR);
                                    sbR.AppendLine("form=" + stR.Form + " hunger=" + stR.Hunger + " pulse=" + stR.Pulse
                                        + " feast=" + stR.Feast + " stand=" + stR.Stand + " reap=" + stR.Reap
                                        + " shroud=" + stR.GoldShroud);
                                    try
                                    {
                                        sbR.Append("relics:");
                                        if (plR.Relics != null) foreach (var rr in plR.Relics) sbR.Append(" " + rr.Id.Entry);
                                        sbR.AppendLine();
                                    }
                                    catch { }
                                    try
                                    {
                                        sbR.Append("deck:");
                                        var deck = plR.Deck;
                                        if (deck != null && deck.Cards != null) foreach (var cc in deck.Cards) sbR.Append(" " + cc.Id.Entry + (cc.IsUpgraded ? "+" : ""));
                                        sbR.AppendLine();
                                    }
                                    catch { }
                                    try { sbR.AppendLine("hp=" + plR.Creature.CurrentHp + "/" + plR.Creature.MaxHp + " gold=" + plR.Gold); } catch { }
                                }
                                try
                                {
                                    var cmR = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                                    var csR = cmR != null ? cmR.DebugOnlyGetState() : null;
                                    if (csR != null && csR.Enemies != null)
                                    {
                                        sbR.Append("enemies:");
                                        foreach (var eR in csR.Enemies) if (eR != null) sbR.Append(" " + eR.Name + "(" + eR.CurrentHp + "/" + eR.MaxHp + ")");
                                        sbR.AppendLine();
                                    }
                                }
                                catch { }
                                sbR.AppendLine("--- last log ---");
                                try
                                {
                                    var dirR = ZLog.LogDirPublic();
                                    if (dirR != null)
                                    {
                                        var fR = System.IO.Path.Combine(dirR, "zhiji_log.txt");
                                        if (System.IO.File.Exists(fR))
                                        {
                                            var linesR = System.IO.File.ReadAllLines(fR);
                                            int fromR = System.Math.Max(0, linesR.Length - 200);
                                            for (int i = fromR; i < linesR.Length; i++) sbR.AppendLine(linesR[i]);
                                        }
                                    }
                                }
                                catch { }
                                string outDir = ZLog.LogDirPublic() ?? System.IO.Path.GetTempPath();
                                string outPath = System.IO.Path.Combine(outDir, "zhiji_report.txt");
                                System.IO.File.WriteAllText(outPath, sbR.ToString());
                                ZLog.W("report written -> " + outPath);
                                Visuals.Popup("反馈包已生成：zhiji_report.txt", new Godot.Color(0.4f, 1f, 0.5f));
                            }
                            catch (Exception eRep) { ZLog.W("t:report fail " + eRep.GetBaseException().Message); }
                        }
                        else if (which == "perf")
                        {
                            // 测量 0.7s 心跳里各子函数的开销（每项 20 次取平均/最大）
                            try
                            {
                                System.Func<string, Action, string> timeIt = (name, act) =>
                                {
                                    long total = 0, max = 0; int n = 20;
                                    for (int i = 0; i < n; i++)
                                    {
                                        var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                        try { act(); } catch { }
                                        long ms = sw2.ElapsedMilliseconds;
                                        total += ms;
                                        if (ms > max) max = ms;
                                    }
                                    return name + "=" + (total / (double)n).ToString("0.00") + "/" + max;
                                };
                                var parts = new System.Collections.Generic.List<string>();
                                parts.Add(timeIt("occlusion", () => Visuals.SyncUiOcclusion()));
                                {   // 原始遍历开销（去掉节流）
                                    long saved = Visuals.OcclusionIntervalMs;
                                    Visuals.OcclusionIntervalMs = 0;
                                    parts.Add(timeIt("occlusionRaw", () => Visuals.SyncUiOcclusion()));
                                    Visuals.OcclusionIntervalMs = saved;
                                }
                                parts.Add(timeIt("handRefresh", () => Visuals.RefreshHandValues()));
                                parts.Add(timeIt("eventArt", () => Visuals.EventArtTickPublic()));
                                parts.Add(timeIt("ostySkin", () => Visuals.UpdateOstySkin()));
                                var plP = DebugPlayer();
                                parts.Add(timeIt("sync", () => { if (plP != null) ZhijiPowers.Sync(plP); }));
                                parts.Add(timeIt("autoPlay", () => ZhijiPatcher.TryConsumeAutoPlay()));
                                ZLog.W("t:perf avg/max(20x) " + string.Join(" ", parts));
                            }
                            catch (Exception eP) { ZLog.W("t:perf fail " + eP.GetBaseException().Message); }
                        }
                        else if (which.StartsWith("bench"))
                        {
                            // t:bench:N —— 纯解码/建纹理成本（不依赖渲染帧率）
                            int nb = 30;
                            int.TryParse(which.Substring(5).TrimStart(':'), out nb);
                            if (nb <= 0) nb = 30;
                            try
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                long maxD = 0; int nD = 0;
                                for (int i = 0; i < nb; i++)
                                {
                                    var t1 = System.Diagnostics.Stopwatch.StartNew();
                                    var img = Art.GetImage("transform", i);
                                    if (img != null)
                                    {
                                        var tex = Godot.ImageTexture.CreateFromImage(img);
                                        img.Dispose();
                                        nD++;
                                    }
                                    long d = t1.ElapsedMilliseconds;
                                    if (d > maxD) maxD = d;
                                }
                                ZLog.W("t:bench decode+tex n=" + nD + " totalMs=" + sw.ElapsedMilliseconds
                                    + " avgMs=" + (nD > 0 ? (sw.ElapsedMilliseconds / (double)nD).ToString("0.0") : "-") + " maxMs=" + maxD);
                            }
                            catch (Exception eB) { ZLog.W("t:bench fail " + eB.GetBaseException().Message); }
                        }
                        else if (which == "auto")
                        {
                            try
                            {
                                var plA = DebugPlayer();
                                if (plA == null) ZLog.W("t:auto no player");
                                else
                                {
                                    ZhijiStore.Of(plA).AutoPlayPending = true;
                                    ZhijiPatcher.TryConsumeAutoPlay();
                                    ZLog.W("t:auto done pending=" + ZhijiStore.Of(plA).AutoPlayPending);
                                }
                            }
                            catch (Exception eA) { ZLog.W("t:auto fail " + eA.GetBaseException().Message); }
                        }
                        else if (which.StartsWith("rare"))
                        {
                            // t:rare:N —— 统计 N 次三选一里的稀有数量（限 1 是否生效）
                            int nRoll = 200;
                            int.TryParse(which.Substring(4).TrimStart(':'), out nRoll);
                            if (nRoll <= 0) nRoll = 200;
                            var rmR = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var rsR = rmR != null ? rmR.DebugOnlyGetState() : null;
                            var plR = rsR != null && rsR.Players != null && rsR.Players.Count > 0 ? rsR.Players[0] : null;
                            if (plR == null) { ZLog.W("t:rare no player"); }
                            else
                            {
                                var poolsR = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardPoolModel>();
                                try { poolsR.Add(plR.Character.CardPool); } catch { }
                                var optsR = MegaCrit.Sts2.Core.Runs.CardCreationOptions.ForNonCombatWithDefaultOdds(poolsR);
                                int rolls = 0, before2 = 0, after2 = 0, capped = 0, maxRareBefore = 0, maxRareAfter = 0;
                                for (int i = 0; i < nRoll; i++)
                                {
                                    try
                                    {
                                        var res = MegaCrit.Sts2.Core.Factories.CardFactory.CreateForReward(plR, 3, optsR);
                                        var lst = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>(res);
                                        if (lst.Count < 2) continue;
                                        int rb = 0;
                                        foreach (var rr in lst) if (rr.Card != null && rr.Card.Rarity.ToString() == "Rare") rb++;
                                        rolls++; before2 += rb;
                                        if (rb > maxRareBefore) maxRareBefore = rb;
                                        int rep = ApplyRareCap(lst, plR, "t:rare");
                                        if (rep > 0) capped++;
                                        int ra = 0;
                                        foreach (var rr in lst) if (rr.Card != null && rr.Card.Rarity.ToString() == "Rare") ra++;
                                        after2 += ra;
                                        if (ra > maxRareAfter) maxRareAfter = ra;
                                    }
                                    catch (Exception eR3) { if (i == 0) ZLog.W("t:rare roll fail " + eR3.GetBaseException().Message); }
                                }
                                ZLog.W("t:rare rolls=" + rolls + " rareBefore=" + before2 + " rareAfter=" + after2
                                    + " cappedRolls=" + capped + " maxBefore=" + maxRareBefore + " maxAfter=" + maxRareAfter);
                            }
                        }
                        else if (which == "evdump")
                        {
                            try
                            {
                                var treeE = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                                Godot.Node roomE = null;
                                var evType = GameAsmType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom");
                                if (evType != null)
                                {
                                    var inst = evType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                                    roomE = inst as Godot.Node;
                                }
                                if (roomE == null && treeE != null) roomE = Visuals.FindRoomNodePublic(treeE.Root, "NEventRoom", 0);
                                if (roomE == null) { ZLog.W("evdump: no event room"); }
                                else Visuals.DumpNodeTree(roomE, 0, 6);
                            }
                            catch (Exception eD) { ZLog.W("evdump fail " + eD.GetBaseException().Message); }
                        }
                        else if (which == "room")
                        {
                            var roomT = CurrentCombatRoomDebug();
                            ZLog.W("t:room " + (roomT?.GetType().FullName ?? "null"));
                        }
                        else if (which == "lose" && cmT != null)
                        {
                            cmT.LoseCombat();
                            ZLog.W("t:lose returned");
                        }
                        else if (which == "endcombat" && cmT != null)
                        {
                            var miT = typeof(MegaCrit.Sts2.Core.Combat.CombatManager).GetMethod("EndCombatInternal",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                                null, Type.EmptyTypes, null);
                            if (miT == null) { ZLog.W("t: endcombat not found"); }
                            else
                            {
                                var taskT = (System.Threading.Tasks.Task)miT.Invoke(cmT, null);
                                if (taskT != null) _ = taskT.ContinueWith(t => ZLog.W("t:endcombat done=" + t.IsCompleted + " err=" + (t.Exception?.GetBaseException().Message ?? "-")));
                                ZLog.W("t:endcombat fired");
                            }
                        }
                        else ZLog.W("t: unknown " + which);
                    }
                    catch (Exception eT) { ZLog.W("t: fail " + eT.GetBaseException().Message); }
                }
                if (cmd && cmdText == "relic:sated_stomach")
                {
                    try
                    {
                        var plSS = DebugPlayer();
                        if (plSS != null) { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<SatedStomach>(plSS); ZLog.W("debug relic sated"); }
                    }
                    catch (Exception eSS) { ZLog.W("debug sated fail " + eSS.Message); }
                }
                if (cmd && cmdText == "ench")
                {
                    try
                    {
                        var plEC = DebugPlayer();
                        if (plEC != null && plEC.Deck != null && plEC.Deck.Cards != null)
                            foreach (var cEC in plEC.Deck.Cards)
                            {
                                if (cEC == null) continue;
                                string en = "none";
                                try { en = cEC.Enchantment != null ? cEC.Enchantment.GetType().Name : "none"; } catch { }
                                ZLog.W("ench " + cEC.Id.Entry + " -> " + en);
                            }
                    }
                    catch (Exception eEC) { ZLog.W("ench fail " + eEC.Message); }
                }
                if (cmd && cmdText == "killminion")
                {
                    try
                    {
                        var plKM = DebugPlayer();
                        if (plKM != null && plKM.Osty != null) { plKM.Osty.LoseHpInternal(plKM.Osty.CurrentHp + 1, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered); ZLog.W("debug killminion"); }
                    }
                    catch (Exception eKM) { ZLog.W("killminion fail " + eKM.Message); }
                }
                if (cmd && cmdText == "enchantall")
                {
                    try
                    {
                        var plEA = DebugPlayer();
                        int nEA = 0;
                        if (plEA != null && plEA.Deck != null && plEA.Deck.Cards != null)
                            foreach (var cEA in plEA.Deck.Cards)
                            {
                                if (cEA == null) continue;
                                try { MegaCrit.Sts2.Core.Commands.CardCmd.Enchant<UnboundRage>(cEA, 1m); nEA++; } catch { }
                            }
                        ZLog.W("debug enchantall n=" + nEA);
                    }
                    catch (Exception eEA) { ZLog.W("enchantall fail " + eEA.Message); }
                }
                if (cmd && cmdText == "capsule")
                {
                    try
                    {
                        var plCap = DebugPlayer();
                        if (plCap != null)
                        {
                            var tCap = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.LargeCapsule>(plCap);
                            _ = tCap.ContinueWith(x => ZLog.W("debug capsule obtain ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) + " ex=" + x.Exception?.GetBaseException().Message));
                        }
                    }
                    catch (Exception eCap2) { ZLog.W("debug capsule fail " + eCap2.GetBaseException().Message); }
                    return;
                }
                if (cmd && cmdText == "redheart")
                {
                    _forceRedHeartCached = true;
                    try { var plRH = DebugPlayer(); if (plRH != null) { var stRH = ZhijiStore.Of(plRH); stRH.RedHeartPending = true; } } catch { }
                    try
                    {
                        var evRH = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.DenseVegetation>();
                        if (evRH != null)
                        {
                            var tRH = MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evRH, true);
                            _ = tRH.ContinueWith(x => ZLog.W("red heart room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                        }
                    }
                    catch (Exception eRH) { ZLog.W("red heart open fail " + eRH.GetBaseException().Message); }
                    ZLog.W("debug: force red god heart event");
                    return;
                }
                if (cmd && (cmdText == "relic:red_god_heart" || cmdText == "relic:vassal_heart"))
                {
                    try
                    {
                        var plVH = DebugPlayer();
                        if (plVH != null) { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<RedGodHeart>(plVH); ZLog.W("debug relic red god heart"); }
                    }
                    catch (Exception eVH) { ZLog.W("debug vassal fail " + eVH.Message); }
                }
                if (cmd && cmdText == "relic:unbound_heart")
                {
                    try
                    {
                        var plUH = DebugPlayer();
                        if (plUH != null) { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<UnboundHeart>(plUH); ZLog.W("debug relic unbound heart"); }
                    }
                    catch (Exception eUH) { ZLog.W("debug unbound heart fail " + eUH.Message); }
                }
                if (cmd && cmdText == "relic:old_scythe")
                {
                    try
                    {
                        var plOS = DebugPlayer();
                        if (plOS != null) { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<OldScythe>(plOS); ZLog.W("debug relic old scythe"); }
                    }
                    catch (Exception eOS) { ZLog.W("debug old scythe fail " + eOS.Message); }
                }
                if (cmd && cmdText == "relic:ration_bag")
                {
                    try
                    {
                        var cmRB = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csRB = cmRB != null ? cmRB.DebugOnlyGetState() : null;
                        var plRB = csRB != null && csRB.Players != null && csRB.Players.Count > 0 ? csRB.Players[0] : null;
                        if (plRB == null)
                        {
                            var rmRB = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var rsRB = rmRB != null ? rmRB.DebugOnlyGetState() : null;
                            plRB = rsRB != null && rsRB.Players != null && rsRB.Players.Count > 0 ? rsRB.Players[0] : null;
                        }
                        if (plRB != null) { _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<RationBag>(plRB); ZLog.W("debug relic ration bag"); }
                    }
                    catch (Exception eRB2) { ZLog.W("debug ration fail " + eRB2.Message); }
                }
                if (cmd && (cmdText == "relic:war_sigil" || cmdText == "relic:guard_sigil"))
                {
                    try
                    {
                        var cmS = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csS = cmS != null ? cmS.DebugOnlyGetState() : null;
                        var plS = csS != null && csS.Players != null && csS.Players.Count > 0 ? csS.Players[0] : null;
                        if (plS != null)
                        {
                            if (cmdText == "relic:war_sigil") _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<WarSigil>(plS);
                            else _ = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<GuardSigil>(plS);
                            ZLog.W("debug relic " + cmdText);
                        }
                        else ZLog.W("debug relic: no player");
                    }
                    catch (Exception eSg) { ZLog.W("debug sigil fail " + eSg.GetBaseException().Message); }
                }
                if (cmd && cmdText == "evolve")
                {
                    try
                    {
                        var cmEV = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csEV = cmEV != null ? cmEV.DebugOnlyGetState() : null;
                        var plEV = csEV != null && csEV.Players != null && csEV.Players.Count > 0 ? csEV.Players[0] : null;
                        if (plEV != null)
                        {
                            var stEV = ZhijiStore.Of(plEV);
                            stEV.Evolve(plEV);
                            Visuals.ShowOverlay(true);
                            ZLog.W("debug evolve form=" + stEV.Form + " feast=" + stEV.Feast);
                        }
                    }
                    catch (Exception eEV) { ZLog.W("debug evolve fail " + eEV.GetBaseException().Message); }
                }
                if (cmd && cmdText == "normal")
                {
                    try
                    {
                        var cmNG = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csNG = cmNG != null ? cmNG.DebugOnlyGetState() : null;
                        var plNG = csNG != null && csNG.Players != null && csNG.Players.Count > 0 ? csNG.Players[0] : null;
                        if (plNG != null)
                        {
                            ZhijiStore.Of(plNG).Form = ZhijiForm.Normal;
                            Visuals.ShowOverlay(false);
                            ZLog.W("debug normal form=Normal");
                        }
                    }
                    catch (Exception eNG) { ZLog.W("debug normal fail " + eNG.GetBaseException().Message); }
                }
                if (cmd && cmdText == "descallall")
                {
                    try
                    {
                        int nC = 0, bad = 0;
                        var sbA = new System.Text.StringBuilder();
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        {
                            if (!(c is ZhijiCard)) continue;
                            nC++;
                            string d = null;
                            try { d = c.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, null); } catch (Exception eD) { d = "ERR:" + eD.GetBaseException().Message; }
                            bool issue = string.IsNullOrWhiteSpace(d) || d.Contains("{") || d.Contains("NO TEXT") || d.Contains("Unimplemented") || d.StartsWith("ERR:");
                            if (issue) { bad++; if (bad <= 6) sbA.Append(c.Id.Entry).Append("=>[").Append(d).Append("] "); }
                        }
                        ZLog.W("descallall cards=" + nC + " bad=" + bad + " | " + sbA.ToString());
                    }
                    catch (Exception eDA) { ZLog.W("descallall fail " + eDA.GetBaseException().Message); }
                }
                if (cmd && cmdText == "poolsizes")
                {
                    try
                    {
                        var pool = MegaCrit.Sts2.Core.Models.ModelDb.CardPool<ZhijiCardPool>();
                        int cards = pool != null && pool.AllCards != null ? pool.AllCards.Count() : -1;
                        var rp = MegaCrit.Sts2.Core.Models.ModelDb.RelicPool<ZhijiRelicPool>();
                        int relics = rp != null && rp.AllRelics != null ? rp.AllRelics.Count() : -1;
                        int powers = MegaCrit.Sts2.Core.Models.ModelDb.AllPowers != null ? MegaCrit.Sts2.Core.Models.ModelDb.AllPowers.Count() : -1;
                        ZLog.W("poolsizes cards=" + cards + " relics=" + relics + " powers=" + powers);
                    }
                    catch (Exception ePS) { ZLog.W("poolsizes fail " + ePS.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("ehp:"))
                {
                    try
                    {
                        int nE = 0; int.TryParse(cmdText.Substring(4), out nE);
                        var cmE = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csE = cmE != null ? cmE.DebugOnlyGetState() : null;
                        if (csE != null && csE.Enemies != null)
                            foreach (var e in csE.Enemies)
                                if (e != null && !e.IsDead) { SetHpWithEvents(e, nE, nE); ZLog.W("debug ehp=" + nE); break; }
                    }
                    catch (Exception eE2) { ZLog.W("debug ehp fail " + eE2.Message); }
                }
                if (cmd && cmdText == "ctxcheck")
                {
                    try
                    {
                        var asmC = GameAsm();
                        foreach (var tn in new[] { "GameActionPlayerChoiceContext", "BlockingPlayerChoiceContext", "HookPlayerChoiceContext", "ThrowingPlayerChoiceContext", "BranchingPlayerChoiceContext" })
                        {
                            var tt = asmC != null ? asmC.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer." + tn, false) : null;
                            if (tt == null) { ZLog.W("ctx " + tn + " null"); continue; }
                            foreach (var c in tt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                var ps = "";
                                foreach (var pp in c.GetParameters()) ps += pp.ParameterType.Name + " " + pp.Name + ",";
                                ZLog.W("ctx " + tn + " ctor(" + ps + ")");
                            }
                        }
                    }
                    catch (Exception eCtx) { ZLog.W("ctxcheck fail " + eCtx.Message); }
                }
                if (cmd && cmdText == "rescheck")
                {
                    try
                    {
                        string[] paths = new string[] {
                            "res://images/enchantments/sharp.png",
                            "res://mods/zhiji/art/summon_idle.png",
                            "res://zhiji/art/summon_idle.png",
                            "res://mods/zhiji/zhiji.json"
                        };
                        foreach (var pth in paths)
                        {
                            bool ex = false;
                            try { ex = Godot.ResourceLoader.Exists(pth, ""); } catch (Exception ee) { ZLog.W("rescheck err " + pth + " " + ee.Message); }
                            ZLog.W("rescheck " + pth + " exists=" + ex);
                        }
                        ZLog.W("rescheck exe=" + Godot.OS.GetExecutablePath());
                    }
                    catch (Exception eRC) { ZLog.W("rescheck fail " + eRC.Message); }
                }
                if (cmd && cmdText == "minions")
                {
                    try
                    {
                        var plM = DebugPlayer();
                        if (plM != null) { var stM = ZhijiStore.Of(plM); ZLog.W("minions hp=" + stM.MinionHp + "/" + stM.MinionMax + " hp2=" + stM.MinionHp2 + "/" + stM.MinionMax2 + " form=" + stM.Form); }
                    }
                    catch (Exception eM3) { ZLog.W("minions fail " + eM3.Message); }
                }
                if (cmd && cmdText == "animcheck")
                {
                    try
                    {
                        foreach (var pre in new[] { "kid_idle","kid_skill1","kid_defend","kid_hit","kid_dodge","red_idle","red_attack","red_defend","red_hit","red_dodge","unbound_idle","unbound_attack","unbound_defend","unbound_hit","unbound_dodge" })
                            ZLog.W("animframes " + pre + "=" + Visuals.FrameCount(pre));
                    }
                    catch (Exception eAC) { ZLog.W("animcheck fail " + eAC.Message); }
                }
                if (cmd && cmdText == "piles")
                {
                    try
                    {
                        var plP2 = DebugPlayer();
                        if (plP2 != null)
                        {
                            foreach (var pt in new[] { MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust })
                            {
                                var pile = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(pt, plP2);
                                var sbP = new System.Text.StringBuilder();
                                if (pile != null) foreach (var c in pile.Cards) if (c != null) sbP.Append(c.Id.Entry).Append(',');
                                ZLog.W("pile " + pt + ": " + sbP.ToString());
                            }
                        }
                    }
                    catch (Exception eP2) { ZLog.W("piles fail " + eP2.Message); }
                }
                if (cmd && cmdText == "hand")
                {
                    try
                    {
                        var plH2 = DebugPlayer();
                        var handH = plH2 != null ? MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plH2) : null;
                        var sbH = new System.Text.StringBuilder();
                        if (handH != null) foreach (var c in handH.Cards) if (c != null) sbH.Append(c.Id.Entry).Append(' ');
                        ZLog.W("hand: " + sbH.ToString());
                    }
                    catch (Exception eH2) { ZLog.W("hand fail " + eH2.Message); }
                }
                if (cmd && cmdText == "relics")
                {
                    try
                    {
                        var plR = DebugPlayer();
                        if (plR != null && plR.Relics != null)
                        {
                            var sbR = new System.Text.StringBuilder();
                            foreach (var r in plR.Relics) sbR.Append(r.Id.Entry).Append(' ');
                            ZLog.W("relics: " + sbR.ToString());
                        }
                        else ZLog.W("relics: no player");
                    }
                    catch (Exception eRl) { ZLog.W("relics fail " + eRl.GetBaseException().Message); }
                }
                if (cmd && cmdText == "enchcheck")
                {
                    try
                    {
                        var eC = MegaCrit.Sts2.Core.Models.ModelDb.Enchantment<UnboundRage>();
                        if (eC == null) ZLog.W("enchcheck: null");
                        else
                        {
                            string tC = "", dC = "";
                            try { tC = eC.Title.GetFormattedText(); } catch (Exception ex1) { tC = "ERR " + ex1.Message; }
                            try { dC = eC.DynamicDescription.GetFormattedText(); } catch (Exception ex2) { dC = "ERR " + ex2.Message; }
                            ZLog.W("enchcheck title=[" + tC + "] desc=[" + dC + "] icon=" + eC.IconPath);
                        }
                    }
                    catch (Exception eC2) { ZLog.W("enchcheck fail " + eC2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "libcheck")
                {
                    try
                    {
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        {
                            if (c == null) continue;
                            if (c is HungerFeast || c is ChildhoodEnd)
                                ZLog.W("libcheck " + c.Id.Entry + " show=" + c.ShouldShowInCardLibrary);
                        }
                    }
                    catch (Exception eLC) { ZLog.W("libcheck fail " + eLC.GetBaseException().Message); }
                }
                if (cmd && cmdText == "dotlist")
                {
                    try
                    {
                        var sbD = new System.Text.StringBuilder();
                        int nD = 0;
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        {
                            var z = c as ZhijiCard;
                            if (z == null || !z.DotStyle) continue;
                            nD++;
                            sbD.Append(c.Id.Entry).Append(' ');
                        }
                        ZLog.W("dotlist count=" + nD + ": " + sbD.ToString());
                    }
                    catch (Exception eDL) { ZLog.W("dotlist fail " + eDL.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("reap:"))
                {
                    try
                    {
                        int nR = 0;
                        if (int.TryParse(cmdText.Substring(5), out nR))
                        {
                            var plR2 = DebugPlayer();
                            if (plR2 != null) { var stR2 = ZhijiStore.Of(plR2); stR2.Reap = System.Math.Max(0, System.Math.Min(100, nR)); ZLog.W("debug reap=" + stR2.Reap); }
                        }
                    }
                    catch (Exception eR3) { ZLog.W("debug reap fail " + eR3.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("popup:"))
                {
                    try { Visuals.Popup(cmdText.Substring(6), new Godot.Color(1f, 0.25f, 0.2f)); ZLog.W("debug popup"); } catch { }
                }
                if (cmd && cmdText.StartsWith("hunger:"))
                {
                    try
                    {
                        int nH = 0;
                        if (int.TryParse(cmdText.Substring(7), out nH))
                        {
                            var plH = DebugPlayer();
                            if (plH != null) { var stH = ZhijiStore.Of(plH); stH.Hunger = System.Math.Max(0, System.Math.Min(100, nH)); ZLog.W("debug hunger=" + stH.Hunger); }
                        }
                    }
                    catch (Exception eH2) { ZLog.W("debug hunger fail " + eH2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "redgod")
                {
                    try
                    {
                        var cmRG = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csRG = cmRG != null ? cmRG.DebugOnlyGetState() : null;
                        var plRG = csRG != null && csRG.Players != null && csRG.Players.Count > 0 ? csRG.Players[0] : null;
                        if (plRG != null)
                        {
                            var stRG = ZhijiStore.Of(plRG);
                            stRG.Form = ZhijiForm.RedGod;
                            Visuals.ShowOverlay(true);
                            ZLog.W("debug redgod form=RedGod feast=" + stRG.Feast);
                        }
                    }
                    catch (Exception eRG) { ZLog.W("debug redgod fail " + eRG.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("vile:"))
                {
                    try
                    {
                        int nV = 0;
                        if (int.TryParse(cmdText.Substring(5), out nV) && nV != 0)
                        {
                            var plV2 = DebugPlayer();
                            if (plV2 != null && plV2.Creature != null)
                            {
                                _ = MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<VileThingPower>(null, plV2.Creature, nV, plV2.Creature, null, true);
                                ZLog.W("debug vile +" + nV + " now=" + plV2.Creature.GetPowerAmount<VileThingPower>());
                            }
                        }
                    }
                    catch (Exception eV2) { ZLog.W("debug vile fail " + eV2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "cradle")
                {
                    try
                    {
                        var evT = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.TabletOfTruth>();
                        if (evT != null)
                        {
                            var rmC = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var tC = rmC.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Event, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, evT, true);
                            _ = tC.ContinueWith(x => ZLog.W("cradle room ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                                + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                        }
                    }
                    catch (Exception eC) { ZLog.W("cradle open fail " + eC.GetBaseException().Message); }
                }
                if (cmd && cmdText == "restroom")
                {
                    try
                    {
                        var rmR = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var tR = rmR.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.RestSite);
                        _ = tR.ContinueWith(x => ZLog.W("restroom ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                            + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                    }
                    catch (Exception eR) { ZLog.W("restroom fail " + eR.GetBaseException().Message); }
                }
                if (cmd && cmdText == "openshopdot")
                {
                    try
                    {
                        var rmD2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsD2 = rmD2 != null ? rmD2.DebugOnlyGetState() : null;
                        var plD2 = rsD2 != null && rsD2.Players != null && rsD2.Players.Count > 0 ? rsD2.Players[0] : null;
                        if (plD2 != null) { RequestDotShop(); OpenDotShop(plD2); }
                    }
                    catch (Exception eD2) { ZLog.W("openshopdot fail " + eD2.Message); }
                }
                if (cmd && cmdText == "openshop")
                {
                    try
                    {
                        var rmOS = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsOS = rmOS != null ? rmOS.DebugOnlyGetState() : null;
                        var plOS = rsOS != null && rsOS.Players != null && rsOS.Players.Count > 0 ? rsOS.Players[0] : null;
                        if (plOS != null) OpenDotShop(plOS);
                    }
                    catch (Exception eOS) { ZLog.W("openshop fail " + eOS.Message); }
                }
                if (cmd && cmdText == "state")
                {
                    try
                    {
                        var cmS = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csS = cmS != null ? cmS.DebugOnlyGetState() : null;
                        var plS = csS != null && csS.Players != null && csS.Players.Count > 0 ? csS.Players[0] : null;
                        if (plS != null)
                        {
                            var stS = ZhijiStore.Of(plS);
                            int bl = 0; int hp = 0;
                            try { if (csS.Enemies != null) foreach (var e in csS.Enemies) if (e != null && !e.IsDead) { bl = e.GetPowerAmount<BleedPower>(); hp = e.CurrentHp; break; } } catch { }
                            bool relic = false; try { relic = plS.Relics != null && plS.Relics.Any(r => r is GoldenShroud); } catch { }
                            ZLog.W("state form=" + stS.Form + " goldShroud=" + stS.GoldShroud + " relic=" + relic
                                + " hunger=" + stS.Hunger + " pulse=" + stS.Pulse + " feast=" + stS.Feast
                                + " | enemy bleed=" + bl + " hp=" + hp);
                        }
                    }
                    catch (Exception eS) { ZLog.W("state fail " + eS.Message); }
                }
                if (cmd && cmdText.StartsWith("deckadd:"))
                {
                    try
                    {
                        string wantDA = cmdText.Substring(8).Trim().ToUpperInvariant();
                        var rmDA = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsDA = rmDA != null ? rmDA.DebugOnlyGetState() : null;
                        var plDA = rsDA != null && rsDA.Players != null && rsDA.Players.Count > 0 ? rsDA.Players[0] : null;
                        MegaCrit.Sts2.Core.Models.CardModel protoDA = null;
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards) if (c != null && c.Id.Entry == wantDA) { protoDA = c; break; }
                        if (plDA == null || protoDA == null) ZLog.W("deckadd: missing " + wantDA);
                        else
                        {
                            var instDA = rsDA.CreateCard(protoDA, plDA);
                            var tDA = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(instDA,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                            _ = tDA.ContinueWith(x => ZLog.W("deckadd " + wantDA + " ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                        }
                    }
                    catch (Exception eDA) { ZLog.W("deckadd fail " + eDA.Message); }
                }
                if (cmd && cmdText == "rewardtest")
                {
                    try
                    {
                        var rmRT = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsRT = rmRT != null ? rmRT.DebugOnlyGetState() : null;
                        var plRT = rsRT != null && rsRT.Players != null && rsRT.Players.Count > 0 ? rsRT.Players[0] : null;
                        if (plRT != null)
                        {
                            var poolsRT = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardPoolModel>();
                            try { poolsRT.Add(plRT.Character.CardPool); } catch { }
                            var optsRT = MegaCrit.Sts2.Core.Runs.CardCreationOptions.ForNonCombatWithDefaultOdds(poolsRT);
                            var resRT = MegaCrit.Sts2.Core.Factories.CardFactory.CreateForReward(plRT, 3, optsRT);
                            var sbRT = new System.Text.StringBuilder();
                            foreach (var rRT in resRT) sbRT.Append(rRT.Card.Id.Entry + " ");
                            ZLog.W("rewardtest: " + sbRT.ToString());
                        }
                    }
                    catch (Exception eRT) { ZLog.W("rewardtest fail " + eRT.GetBaseException().Message); }
                }
                if (cmd && cmdText == "filter")
                {
                    try
                    {
                        var rmF = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsF = rmF != null ? rmF.DebugOnlyGetState() : null;
                        var usF = rsF != null ? rsF.UnlockState : MegaCrit.Sts2.Core.Unlocks.UnlockState.all;
                        var poolF = MegaCrit.Sts2.Core.Models.ModelDb.CardPool<ZhijiCardPool>();
                        var mF = typeof(ZhijiCardPool).GetMethod("FilterThroughEpochs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (mF != null && poolF != null)
                        {
                            var res = mF.Invoke(poolF, new object[] { usF, poolF.AllCards }) as System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>;
                            var sbF = new System.Text.StringBuilder(); int nF = 0;
                            if (res != null) foreach (var c in res) { nF++; sbF.Append(c.Id.Entry + " "); }
                            ZLog.W("filtered(" + nF + "): " + sbF.ToString());
                        }
                        else ZLog.W("filter: method/pool not found");
                    }
                    catch (Exception eF2) { ZLog.W("filter fail " + eF2.Message); }
                }
                if (cmd && cmdText == "pool")
                {
                    try
                    {
                        var sbP = new System.Text.StringBuilder();
                        int nP = 0;
                        foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        {
                            if (!(c is ZhijiCard)) continue;
                            nP++;
                            sbP.Append(c.Id.Entry + "(" + c.Rarity + "," + c.Type + ") ");
                        }
                        ZLog.W("pool cards=" + nP + ": " + sbP.ToString());
                    }
                    catch (Exception eP) { ZLog.W("pool fail " + eP.Message); }
                }
                if (cmd && cmdText.StartsWith("give:"))
                {
                    try
                    {
                        string wantG = cmdText.Substring(5).Trim().ToUpperInvariant();
                        var cmG = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csG = cmG != null ? cmG.DebugOnlyGetState() : null;
                        if (csG != null && csG.Players != null && csG.Players.Count > 0)
                        {
                            var plG = csG.Players[0];
                            MegaCrit.Sts2.Core.Models.CardModel proto = null;
                            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards) { if (c != null && c.Id.Entry == wantG) { proto = c; break; } }
                            if (proto == null) ZLog.W("debug give: no model " + wantG);
                            else
                            {
                                var instG = csG.CreateCard(proto, plG);
                                var tG = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(instG, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                                _ = tG.ContinueWith(x => ZLog.W("debug give " + wantG + " ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
                            }
                        }
                    }
                    catch (Exception eG2) { ZLog.W("debug give fail " + eG2.Message); }
                }
                if (cmd && cmdText.StartsWith("descall:"))
                {
                    try
                    {
                        string wantD = cmdText.Substring(8).Trim().ToUpperInvariant();
                        var cmD = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csD = cmD != null ? cmD.DebugOnlyGetState() : null;
                        var plD = csD != null && csD.Players != null && csD.Players.Count > 0 ? csD.Players[0] : null;
                        var handD = plD != null ? MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plD) : null;
                        MegaCrit.Sts2.Core.Models.CardModel cardD = null;
                        if (handD != null && handD.Cards != null)
                            foreach (var c in handD.Cards) { if (c != null && c.Id.Entry == wantD) { cardD = c; break; } }
                        if (cardD == null) ZLog.W("descall: not in hand");
                        else ZLog.W("descall " + wantD + " => " + cardD.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, null));
                    }
                    catch (Exception eD2) { ZLog.W("descall fail " + eD2.Message); }
                }
                if (cmd && cmdText.StartsWith("desc:"))
                {
                    try
                    {
                        string wantD = cmdText.Substring(5).Trim().ToUpperInvariant();
                        var cmD = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csD = cmD != null ? cmD.DebugOnlyGetState() : null;
                        if (csD != null && csD.Players != null && csD.Players.Count > 0)
                        {
                            var plD = csD.Players[0];
                            var pilesD = new[] { MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw };
                            foreach (var ptD in pilesD)
                            {
                                var pileD = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(ptD, plD);
                                if (pileD == null || pileD.Cards == null) continue;
                                foreach (var cD in pileD.Cards)
                                {
                                    if (cD == null || cD.Id.Entry != wantD) continue;
                                    var keysD = string.Join(",", cD.DynamicVars.Keys);
                                    var txtD = "";
                                    try { txtD = cD.GetDescriptionForPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand); } catch (Exception eD) { txtD = "DESC FAIL " + eD.Message; }
                                    ZLog.W("desc " + wantD + " up=" + cD.IsUpgraded + " vars=[" + keysD + "] text=" + txtD.Replace("\n", " / "));
                                    return;
                                }
                            }
                            ZLog.W("desc " + wantD + " not found");
                        }
                    }
                    catch (Exception eD2) { ZLog.W("desc fail " + eD2.Message); }
                }
                if (cmd && cmdText.StartsWith("upgrade:"))
                {
                    try
                    {
                        string wantU = cmdText.Substring(8).Trim().ToUpperInvariant();
                        var cmU = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csU = cmU != null ? cmU.DebugOnlyGetState() : null;
                        int n = 0;
                        if (csU != null && csU.Players != null && csU.Players.Count > 0)
                        {
                            var plU = csU.Players[0];
                            var allPiles = new[] { MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust };
                            foreach (var pt in allPiles)
                            {
                                var pile = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(pt, plU);
                                if (pile == null || pile.Cards == null) continue;
                                foreach (var c in pile.Cards)
                                {
                                    if (c == null || c.Id.Entry != wantU || c.IsUpgraded) continue;
                                    c.UpgradeInternal();
                                    try { c.FinalizeUpgradeInternal(); } catch { }
                                    n++;
                                }
                            }
                        }
                        ZLog.W("debug upgrade " + wantU + " -> " + n);
                    }
                    catch (Exception eU) { ZLog.W("debug upgrade fail " + eU.Message); }
                }
                if (cmd && cmdText.StartsWith("play:"))
                {
                    try
                    {
                        string want = cmdText.Substring(5).Trim().ToUpperInvariant();
                        var cmP = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csP = cmP != null ? cmP.DebugOnlyGetState() : null;
                        if (csP != null && csP.Players != null && csP.Players.Count > 0)
                        {
                            var plP = csP.Players[0];
                            var handP = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plP);
                            MegaCrit.Sts2.Core.Models.CardModel cardP = null;
                            if (handP != null && handP.Cards != null)
                                foreach (var cc in handP.Cards) { if (cc != null && cc.Id.Entry == want) { cardP = cc; break; } }
                            if (cardP == null) ZLog.W("debug play: " + want + " not in hand");
                            else
                            {
                                MegaCrit.Sts2.Core.Entities.Creatures.Creature tp = null;
                                if (cardP.TargetType != MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self
                                    && cardP.TargetType != MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllEnemies
                                    && cardP.TargetType != MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllAllies
                                    && csP.Enemies != null)
                                    foreach (var ee in csP.Enemies) { if (ee != null && !ee.IsDead) { tp = ee; break; } }
                                bool okp = cardP.TryManualPlay(tp);
                                if (!okp)
                                {
                                    // 少数群体牌需要空目标或首个敌人，兜底再试一次
                                    MegaCrit.Sts2.Core.Entities.Creatures.Creature alt = null;
                                    if (tp == null && csP.Enemies != null)
                                        foreach (var ee in csP.Enemies) { if (ee != null && !ee.IsDead) { alt = ee; break; } }
                                    if (alt != null) okp = cardP.TryManualPlay(alt);
                                }
                                ZLog.W("debug play " + want + " -> " + okp);
                            }
                        }
                    }
                    catch (Exception eP2) { ZLog.W("debug play fail " + eP2.Message); }
                }
                if (cmd && cmdText.StartsWith("cast"))
                {
                    try
                    {
                        var cmC = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csC = cmC != null ? cmC.DebugOnlyGetState() : null;
                        if (csC != null && csC.Players != null && csC.Players.Count > 0)
                        {
                            var plC = csC.Players[0];
                            var handC = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plC);
                            MegaCrit.Sts2.Core.Models.CardModel cardC = null;
                            if (handC != null && handC.Cards != null)
                                foreach (var cc in handC.Cards) { if (cc != null && cc.Id.Entry == "HARVEST") { cardC = cc; break; } }
                            MegaCrit.Sts2.Core.Entities.Creatures.Creature foe = null;
                            if (csC.Enemies != null)
                                foreach (var ee in csC.Enemies) { if (ee != null && !ee.IsDead) { foe = ee; break; } }
                            if (cardC == null) ZLog.W("debug cast: no harvest in hand");
                            else if (foe == null) ZLog.W("debug cast: no enemy");
                            else
                            {
                                bool okc = cardC.TryManualPlay(foe);
                                ZLog.W("debug cast harvest -> " + okc);
                            }
                        }
                    }
                    catch (Exception eC) { ZLog.W("debug cast fail " + eC.Message); }
                }
                if (cmd && cmdText == "act")
                {
                    // 危险：直接 EnterNextAct 会在远古事件里触发原生崩溃，已禁用
                    ZLog.W("debug: act command disabled (unsafe)");
                }
                if (cmd && cmdText.StartsWith("t:ev:"))
                {
                    // 进入指定事件宿主（不设置任何 force 标志），用于模拟"自然进入"的事件
                    try
                    {
                        string evName = cmdText.Substring(5).Trim();
                        var asmEv = GameAsm();
                        var evT = FindGameTypeCI(evName);
                        if (evT == null) { ZLog.W("t:ev type not found: " + evName); }
                        else
                        {
                            var rmEv = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var rsEv = rmEv != null ? rmEv.DebugOnlyGetState() : null;
                            if (rsEv == null) { ZLog.W("t:ev no run state"); }
                            else
                            {
                                var coordEv = rsEv.CurrentMapCoord.HasValue ? rsEv.CurrentMapCoord.Value : new MegaCrit.Sts2.Core.Map.MapCoord(0, 0);
                                var tEv = rmEv.EnterMapCoordDebug(coordEv, MegaCrit.Sts2.Core.Rooms.RoomType.Event,
                                    MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, (MegaCrit.Sts2.Core.Models.AbstractModel)null, true);
                                ZLog.W("t:ev " + evName + " (no model) fired");
                                _ = tEv;
                            }
                        }
                    }
                    catch (Exception eEv) { ZLog.W("t:ev fail " + eEv.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:evm:"))
                {
                    // t:evm:<EventTypeName> —— 带模型进入事件房（真正的"自然宿主"）
                    try
                    {
                        string evName = cmdText.Substring(6).Trim();
                        var asmEv2 = GameAsm();
                        var evT2 = FindGameTypeCI(evName);
                        if (evT2 == null) { ZLog.W("t:evm type not found: " + evName); }
                        else
                        {
                            var rmEv2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                            var rsEv2 = rmEv2 != null ? rmEv2.DebugOnlyGetState() : null;
                            if (rsEv2 == null) { ZLog.W("t:evm no run state"); }
                            else
                            {
                                var modelEv = MegaCrit.Sts2.Core.Models.ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.EventModel>(
                                    MegaCrit.Sts2.Core.Models.ModelDb.GetId(evT2));
                                if (modelEv == null) { ZLog.W("t:evm model null"); }
                                else
                                {
                                    var coordEv2 = rsEv2.CurrentMapCoord.HasValue ? rsEv2.CurrentMapCoord.Value : new MegaCrit.Sts2.Core.Map.MapCoord(0, 0);
                                    var tEv2 = rmEv2.EnterMapCoordDebug(coordEv2, MegaCrit.Sts2.Core.Rooms.RoomType.Event,
                                        MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, modelEv, true);
                                    _ = tEv2.ContinueWith(x => ZLog.W("t:evm " + evName + " ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)));
                                }
                            }
                        }
                    }
                    catch (Exception eEv2) { ZLog.W("t:evm fail " + eEv2.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:anim:"))
                {
                    var arg = cmdText.Substring(7).Trim();
                    if (arg == "0" || arg == "off") ZhijiPrefs.AnimOn = false;
                    else if (arg == "1" || arg == "on") ZhijiPrefs.AnimOn = true;
                    else if (arg == "speed2") ZhijiPrefs.AnimSpeed = 2;
                    else if (arg == "speed1") ZhijiPrefs.AnimSpeed = 1;
                    ZhijiPrefs.Save();
                    ZLog.W("t:anim animOn=" + ZhijiPrefs.AnimOn + " speed=" + ZhijiPrefs.AnimSpeed);
                }
                if (cmd && cmdText == "t:sumbuff")
                {
                    var plSB = CurrentPlayer();
                    if (plSB == null) ZLog.W("t:sumbuff no player");
                    else RewardSummonBuff(plSB);
                }
                if (cmd && cmdText == "t:famine")
                {
                    // 直接跑一次"幼年形态饥饿自损"（不需要等敌人回合），用于验证致死逻辑
                    try
                    {
                        var plF2 = CurrentPlayer();
                        if (plF2 == null || plF2.Creature == null) ZLog.W("t:famine no player");
                        else
                        {
                            var stF2 = ZhijiStore.Of(plF2);
                            int maxHp2 = (int)plF2.Creature.MaxHp;
                            double k2 = 0.06;
                            bool pet2 = (stF2.MinionHp > 0 || stF2.MinionHp2 > 0)
                                || (plF2.Osty != null && !plF2.Osty.IsDead && plF2.Osty.CurrentHp > 0);
                            if (pet2) k2 *= 0.5;
                            int loss2 = (int)System.Math.Ceiling(maxHp2 * (stF2.Hunger / 100.0) * k2);
                            int cap2 = (int)System.Math.Ceiling(maxHp2 * 0.08);
                            if (loss2 > cap2) loss2 = cap2;
                            decimal hpB = plF2.Creature.CurrentHp;
                            if (loss2 > 0)
                            {
                                ZhijiPatcher.BypassStand = true;
                                plF2.Creature.LoseHpInternal(loss2, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                ZhijiPatcher.BypassStand = false;
                                if (loss2 >= hpB && !plF2.Creature.IsDead && (int)plF2.Creature.CurrentHp > 0)
                                {
                                    plF2.Creature.SetCurrentHpInternal(0);
                                    ZLog.W("t:famine LETHAL hunger=" + stF2.Hunger + " loss=" + loss2 + " hp " + hpB + " -> 0");
                                    ZhijiPatcher.EnsureDeathSettled(plF2, "famine-debug");
                                }
                                else ZLog.W("t:famine hunger=" + stF2.Hunger + " loss=" + loss2 + " hp=" + plF2.Creature.CurrentHp);
                            }
                            else ZLog.W("t:famine loss=0 hunger=" + stF2.Hunger);
                        }
                    }
                    catch (Exception eF2) { ZLog.W("t:famine fail " + eF2.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:hurt:"))
                {
                    try
                    {
                        int nH; int.TryParse(cmdText.Substring(7).Trim(), out nH);
                        var plH = CurrentPlayer();
                        if (plH == null || plH.Creature == null) ZLog.W("t:hurt no player");
                        else
                        {
                            ZhijiPatcher.BypassStand = true;
                            plH.Creature.LoseHpInternal(nH, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                            ZhijiPatcher.BypassStand = false;
                            ZLog.W("t:hurt " + nH + " -> hp=" + plH.Creature.CurrentHp + "/" + plH.Creature.MaxHp + " dead=" + plH.Creature.IsDead);
                        }
                    }
                    catch (Exception eHt) { ZLog.W("t:hurt fail " + eHt.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:enemyheal:"))
                {
                    try
                    {
                        int nEH; int.TryParse(cmdText.Substring(12).Trim(), out nEH);
                        var csEH = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null ? MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() : null;
                        if (csEH == null) ZLog.W("t:enemyheal no combat");
                        else
                        {
                            int k = 0;
                            foreach (var e in csEH.Enemies)
                            {
                                if (e == null || e.IsDead) continue;
                                decimal before = e.CurrentHp;
                                e.HealInternal(nEH);
                                ZLog.W("t:enemyheal " + nEH + " -> " + before + " => " + e.CurrentHp + " (" + e.GetType().Name + ")");
                                k++;
                            }
                            if (k == 0) ZLog.W("t:enemyheal no enemy");
                        }
                    }
                    catch (Exception eEH) { ZLog.W("t:enemyheal fail " + eEH.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:play:"))
                {
                    try
                    {
                        string wantP = cmdText.Substring(7).Trim().ToUpperInvariant();
                        var plP = CurrentPlayer();
                        var handP = plP != null ? MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plP) : null;
                        if (handP == null) ZLog.W("t:play no hand");
                        else
                        {
                            MegaCrit.Sts2.Core.Entities.Creatures.Creature tgtP = null;
                            try
                            {
                                var csP = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null ? MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() : null;
                                if (csP != null && csP.Enemies != null) foreach (var e in csP.Enemies) if (e != null && !e.IsDead) { tgtP = e; break; }
                            }
                            catch { }
                            bool played = false;
                            foreach (var cP in handP.Cards)
                            {
                                if (cP == null || cP.Id.Entry != wantP) continue;
                                played = cP.TryManualPlay(tgtP);
                                ZLog.W("t:play " + wantP + " ok=" + played);
                                break;
                            }
                            if (!played) ZLog.W("t:play not found/blocked " + wantP);
                        }
                    }
                    catch (Exception eP2) { ZLog.W("t:play fail " + eP2.GetBaseException().Message); }
                }
                if (cmd && cmdText == "t:hand")
                {
                    try
                    {
                        var plH = CurrentPlayer();
                        var hand = plH != null ? MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, plH) : null;
                        if (hand == null) ZLog.W("t:hand no hand");
                        else
                        {
                            var sb = new System.Text.StringBuilder();
                            foreach (var c in hand.Cards) sb.Append(c.Id.Entry).Append('(').Append(c.EnergyCost != null ? c.EnergyCost.GetResolved().ToString() : "?").Append(") ");
                            ZLog.W("t:hand " + sb.ToString().Trim());
                        }
                    }
                    catch (Exception eH) { ZLog.W("t:hand fail " + eH.Message); }
                }
                if (cmd && cmdText == "t:winrun")
                {
                    var okW = ShowVictoryScreen("debug");
                    ZLog.W("t:winrun ok=" + okW);
                }
                if (cmd && cmdText == "t:evallowed")
                {
                    // 列出当前 act 下允许出现的事件（含我们的宿主 DenseVegetation/SunkenTreasury 是否在内）
                    try
                    {
                        var rmEA = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsEA = rmEA != null ? rmEA.DebugOnlyGetState() : null;
                        if (rsEA == null) { ZLog.W("t:evallowed no run state"); }
                        else
                        {
                            var allowed = new System.Text.StringBuilder();
                            int n = 0, total = 0;
                            foreach (var m in MegaCrit.Sts2.Core.Models.ModelDb.All)
                            {
                                var ev = m as MegaCrit.Sts2.Core.Models.EventModel;
                                if (ev == null) continue;
                                total++;
                                bool ok = false;
                                try
                                {
                                    var mi = ev.GetType().GetMethod("IsAllowed", BindingFlags.Public | BindingFlags.Instance);
                                    if (mi != null) ok = (bool)mi.Invoke(ev, new object[] { rsEA });
                                }
                                catch { }
                                if (!ok) continue;
                                n++;
                                if (n <= 24) allowed.Append(ev.GetType().Name).Append(' ');
                            }
                            ZLog.W("t:evallowed act=" + rsEA.CurrentActIndex + " allowed=" + n + "/" + total + " [" + allowed.ToString().Trim() + "]");
                        }
                    }
                    catch (Exception eEA) { ZLog.W("t:evallowed fail " + eEA.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("t:act:"))
                {
                    // 只改 RunState.CurrentActIndex（不触发远古事件/房间重建），用于测试第二层事件
                    try
                    {
                        int wantAct = 0;
                        int.TryParse(cmdText.Substring(6), out wantAct);
                        var rmAct = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsAct = rmAct != null ? rmAct.DebugOnlyGetState() : null;
                        if (rsAct == null) { ZLog.W("t:act no run state"); }
                        else
                        {
                            int before = rsAct.CurrentActIndex;
                            rsAct.CurrentActIndex = wantAct;
                            ZLog.W("t:act " + before + " -> " + rsAct.CurrentActIndex);
                        }
                    }
                    catch (Exception eAct) { ZLog.W("t:act fail " + eAct.GetBaseException().Message); }
                }
                if (cmd && cmdText.StartsWith("setph:"))
                {
                    try
                    {
                        int wantHp = int.Parse(cmdText.Substring(6).Trim());
                        var cmH = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csH = cmH != null ? cmH.DebugOnlyGetState() : null;
                        var plH = csH != null && csH.Players != null && csH.Players.Count > 0 ? csH.Players[0] : null;
                        if (plH != null && plH.Creature != null) { plH.Creature.SetCurrentHpInternal(wantHp); ZLog.W("debug setph -> " + wantHp); }
                    }
                    catch (Exception eHp) { ZLog.W("debug setph fail " + eHp.Message); }
                }
                if (cmd && (cmdText == "heal" || cmdText == "hp"))
                {
                    try
                    {
                        var cmH = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csH = cmH != null ? cmH.DebugOnlyGetState() : null;
                        var plH = csH != null && csH.Players != null && csH.Players.Count > 0 ? csH.Players[0] : null;
                        if (plH != null && plH.Creature != null)
                        {
                            plH.Creature.SetCurrentHpInternal(plH.Creature.MaxHp);
                            ZLog.W("debug: healed to " + plH.Creature.CurrentHp + "/" + plH.Creature.MaxHp);
                        }
                    }
                    catch (Exception eH) { ZLog.W("debug heal fail " + eH.Message); }
                }
                if (cmd && cmdText == "win")
                {
                    try
                    {
                        var cmW = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                        var csW = cmW != null ? cmW.DebugOnlyGetState() : null;
                        int nw = 0;
                        if (csW != null && csW.Enemies != null)
                            foreach (var ew in csW.Enemies)
                            {
                                if (ew == null || ew.IsDead) continue;
                                ew.LoseHpInternal(999999, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                nw++;
                            }
                        ZLog.W("debug: win -> killed " + nw);
                    }
                    catch (Exception eW) { ZLog.W("debug win fail " + eW.Message); }
                }
                if (cmd && cmdText == "kill")
                {
                    var cmK = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csK = cmK != null ? cmK.DebugOnlyGetState() : null;
                    if (csK != null && csK.Enemies != null)
                    {
                        int nk = 0;
                        foreach (var ek in csK.Enemies) { if (ek != null && !ek.IsDead) { ek.SetCurrentHpInternal(1); nk++; } }
                        ZLog.W("debug: enemies set to 1 hp (" + nk + ")");
                    }
                }
                if (cmd && (cmdText == "harvest" || cmdText == "h"))
                {
                    var cm2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var cs3 = cm2 != null ? cm2.DebugOnlyGetState() : null;
                    var pl3 = cs3 != null && cs3.Players != null && cs3.Players.Count > 0 ? cs3.Players[0] : null;
                    if (pl3 != null)
                    {
                        var st3 = ZhijiStore.Of(pl3);
                        st3.Reap = 100;
                        var hm = SafeCard<Harvest>(null);
                        if (hm != null)
                        {
                            var hi = cs3.CreateCard(hm, pl3);
                            if (hi != null) _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(hi,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                            ZLog.W("debug: granted HARVEST reap=100");
                        }
                    }
                }
                if (cmd && (cmdText == "guard" || cmdText == "war" || cmdText == "balance"))
                {
                    var cmF = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csF = cmF != null ? cmF.DebugOnlyGetState() : null;
                    var plF = csF != null && csF.Players != null && csF.Players.Count > 0 ? csF.Players[0] : null;
                    if (plF != null)
                    {
                        var stF = ZhijiStore.Of(plF);
                        if (cmdText == "guard") { stF.AtkCount = 0; stF.DefCount = 3; }
                        else if (cmdText == "war") { stF.AtkCount = 3; stF.DefCount = 0; }
                        else { stF.AtkCount = 1; stF.DefCount = 1; }
                        stF.Hunger = 100;
                        if (stF.Form == ZhijiForm.Normal)
                        {
                            var modelF = SafeCard<ChildhoodEnd>(null);
                            if (modelF != null)
                            {
                                var instF = csF.CreateCard(modelF, plF);
                                if (instF != null) _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(instF,
                                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                                ZLog.W("debug: form preset " + cmdText + " granted CHILDHOOD_END");
                            }
                        }
                    }
                }
                bool down = Godot.Input.IsKeyPressed(Godot.Key.F10) || (cmd && cmdText == "grant");
                if (down && !_f10Down)
                {
                    var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var cs = cm != null ? cm.DebugOnlyGetState() : null;
                    var pl = cs != null && cs.Players != null && cs.Players.Count > 0 ? cs.Players[0] : null;
                    if (pl != null)
                    {
                        var st = ZhijiStore.Of(pl);
                        st.Hunger = 100;
                        if (st.Form == ZhijiForm.Normal)
                        {
                            var model = SafeCard<ChildhoodEnd>(null);
                            if (model != null)
                            {
                                var inst = cs.CreateCard(model, pl);
                                if (inst != null) _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst,
                                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                                ZLog.W("debug: granted CHILDHOOD_END");
                            }
                        }
                    }
                }
                _f10Down = down;
            }
            catch (Exception e) { ZLog.W("debug hotkey fail " + e.Message); }
        }

        // 读档回到"事件已完成页"时，按钮可能不是可点选项；这里自动 Proceed 兜底（仅调试模式）
        static long _lastAutoProceed;
        static void AutoProceedEvent()
        {
            try
            {
                if (!Godot.FileAccess.FileExists(Godot.ProjectSettings.GlobalizePath("user://zhiji_debug"))) return;
                long now = System.Environment.TickCount64;
                if (now - _lastAutoProceed < 4000) return;
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var room = rs != null ? rs.CurrentRoom : null;
                if (room == null || room.GetType().Name != "EventRoom") return;
                var evProp = room.GetType().GetProperty("CanonicalEvent");
                var ev = evProp != null ? evProp.GetValue(room) as MegaCrit.Sts2.Core.Models.EventModel : null;
                if (ev == null || !ev.IsFinished) return;
                var tNR = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", false) : null;
                var inst = tNR != null ? tNR.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) : null;
                if (inst == null) return;
                var m = tNR.GetMethod("Proceed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m != null)
                {
                    _lastAutoProceed = now;
                    m.Invoke(inst, null);
                    ZLog.W("auto proceed (finished event page)");
                }
            }
            catch { }
        }

        static void AutoTestTick()
        {
            // 自动测试已停用：不再探测标志文件 / 启动 AutoSlayer（会自行退出游戏）
            return;
#pragma warning disable CS0162
            try
            {
                DebugHotkeyTick();
                if (_autoStarted) return;
                bool flag = false;
                string[] flagPaths = {
                    Godot.ProjectSettings.GlobalizePath("user://zhiji_autotest"),
                    "/storage/emulated/0/Android/data/com.megacrit.sts2re/files/zhiji_autotest",
                    "/sdcard/Download/zhiji_autotest"
                };
                foreach (var fp in flagPaths)
                {
                    try
                    {
                        bool ex = Godot.FileAccess.FileExists(fp);
                        if (ex) { flag = true; ZLog.W("autotest flag found: " + fp); break; }
                    }
                    catch (Exception ee) { ZLog.W("autotest probe fail " + fp + " " + ee.Message); }
                }
                if (!flag) return;
                // AutoSlayer 失败时会直接退出游戏（闪退），因此不再自动启动；
                // 仅在发现标志文件时记录，避免误触发。
                ZLog.W("autotest: flag detected, AutoSlayer disabled in this build");
            }
            catch (Exception e)
            {
                var ie = e;
                while (ie.InnerException != null) ie = ie.InnerException;
                ZLog.W("autotest tick fail " + ie.GetType().Name + " " + ie.Message + " | " + ie.StackTrace);
            }
        }

        // 自动测试期间：只允许选吱咿（其它角色视为锁定，AutoSlayer 会过滤掉）
        public static void CharLockPostfix(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton __instance, ref bool __result)
        {
            try
            {
                var t = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", false) : null;
                var pi = t != null ? t.GetProperty("IsActive") : null;
                bool active = pi != null && (bool)pi.GetValue(null);
                if (!active) return;
                var ch = __instance != null ? __instance.Character : null;
                if (ch == null) return;
                __result = ch.Id.Entry != "ZHIJI";
            }
            catch { }
        }


        /// 红神形态的抽牌实现：作为 DrawInternal 的返回值交给引擎 await
        /// 因此所有换堆操作都发生在引擎的回合流程内部，不会和 UI 抢时序
        public static async System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> RedDrawAsync(
            MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            var result = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
            try
            {
                if (pl == null) return result;
                if (_redDrawing) { ZLog.W("reddraw skipped (busy)"); return result; }
                _redDrawing = true;
                var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                int before = hand != null ? hand.Cards.Count : -1;
                if (hand != null)
                {
                    foreach (var c in hand.Cards.ToList())
                    {
                        if (c == null) continue;
                        var z = c as ZhijiCard;
                        var target = (z != null && z.RedGod)
                            ? MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust
                            : MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard;
                        try
                        {
                            await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(c, target,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                        }
                        catch (Exception e2) { ZLog.W("reddraw clear fail " + e2.Message); }
                    }
                }
                var st = pl.Creature != null ? pl.Creature.CombatState : null;
                for (int i = 0; i < count && st != null; i++)
                {
                    var model = RedFor(null);
                    if (model == null) continue;
                    var inst = st.CreateCard(model, pl);
                    if (inst == null) continue;
                    var res = await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst,
                        MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                        MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                    if (res.success)
                    {
                        result.Add(inst);
                        try
                        {
                            MegaCrit.Sts2.Core.Entities.Cards.UnplayableReason why;
                            MegaCrit.Sts2.Core.Models.AbstractModel blocker;
                            bool can = inst.CanPlay(out why, out blocker);
                            int energy = pl.PlayerCombatState != null ? (int)pl.PlayerCombatState.Energy : -1;
                            int maxE = pl.PlayerCombatState != null ? pl.PlayerCombatState.MaxEnergy : -1;
                            ZLog.W("reddraw card " + inst.Id.Entry + " cost=" + (inst.EnergyCost != null ? inst.EnergyCost.GetAmountToSpend().ToString() : "?")
                                + " canplay=" + can + " why=" + why + " energy=" + energy + "/" + maxE
                                + " pile=" + (inst.Pile != null ? inst.Pile.Type.ToString() : "null"));
                        }
                        catch (Exception e3) { ZLog.W("canplay probe fail " + e3.Message); }
                    }
                    else ZLog.W("reddraw add failed " + model.Id.Entry);
                }
                Visuals.RefreshHandLayout();
                ZLog.W("reddraw done: before=" + before + " -> " + result.Count);
            }
            catch (Exception e) { ZLog.W("reddraw fail " + e.Message); }
            finally { _redDrawing = false; }
            return result;
        }

        public static bool PowerIconPrefix(object __instance, ref Godot.Texture2D __result)
            => CharPatches.PowerIcon(__instance, ref __result);

        public static bool RelicIconPrefix(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref Godot.Texture2D __result)
            => CharPatches.RelicIcon(__instance, ref __result);

        // 把会造成卡死的 ToyBox（巨大扭蛋）从先古事件选项里剔除
        public static void AncientOptionsPostfix(ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (__result == null || __result.Count == 0) return;
                var keep = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Events.EventOption>();
                bool removed = false;
                foreach (var opt in __result)
                {
                    if (opt != null && opt.Relic != null && opt.Relic.Id.Entry == "TOY_BOX") { removed = true; continue; }
                    keep.Add(opt);
                }
                if (!removed) return;
                if (keep.Count == 0) return;
                __result = keep;
                ZLog.W("ancient options: toybox removed, left=" + keep.Count);
            }
            catch (Exception e) { ZLog.W("ancient options patch fail " + e.Message); }
        }

        // 空放（无目标）时：允许我们的三张牌直接以"防御模式"打出
        public static bool TryPlayCardPrefix(MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay __instance,
            MegaCrit.Sts2.Core.Entities.Creatures.Creature target)
        {
            try
            {
                var pi = typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay).GetProperty("Card",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var card = pi != null ? pi.GetValue(__instance) as MegaCrit.Sts2.Core.Models.CardModel : null;
                if (card == null || !(card is ZhijiCard)) return true;
                var e = card.Id.Entry;
                if (e != "DRIVE" && e != "ESCAPE" && e != "PULSE_AID") return true;
                if (target != null) return true;      // 有敌人目标 → 原流程
                bool ok = card.TryManualPlay(null);
                ZLog.W("empty-drop play ok=" + ok + " (" + e + ")");
                return false;                          // 跳过原方法的 CancelPlayCard
            }
            catch (Exception e2) { ZLog.W("tryplay patch fail " + e2.Message); return true; }
        }

        public static bool IsValidTargetPrefix(MegaCrit.Sts2.Core.Models.CardModel __instance,
            MegaCrit.Sts2.Core.Entities.Creatures.Creature target, ref bool __result)
        {
            try
            {
                if (__instance == null) return true;
                if (!(__instance is ZhijiCard)) return true;
                var e = __instance.Id.Entry;
                if (e != "DRIVE" && e != "ESCAPE" && e != "PULSE_AID") return true;
                if (target == null) { __result = true; return false; }   // 空放合法
                if (!target.IsDead) { __result = true; return false; }   // 敌人也放行
            }
            catch { }
            return true;
        }

        public static bool RelicBigIconPrefix(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref Godot.Texture2D __result)
            => CharPatches.RelicBigIcon(__instance, ref __result);

        public static bool RelicDynamicDescPrefix(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
            => CharPatches.RelicDynamicDesc(__instance, ref __result);

        static Player DebugPlayer()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                if (cs != null && cs.Players != null && cs.Players.Count > 0)
                {
                    var p0 = cs.Players[0];
                    if (p0 != null && p0.Character != null && p0.Character.Id.Entry == "ZHIJI") return p0;
                }
            }
            catch { }
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs != null && rs.Players != null && rs.Players.Count > 0)
                {
                    var p0 = rs.Players[0];
                    if (p0 != null && p0.Character != null && p0.Character.Id.Entry == "ZHIJI") return p0;
                }
            }
            catch { }
            return null;
        }

        public static Player DebugPlayerPublic() => DebugPlayer();

        static bool IsOurPlayer(MegaCrit.Sts2.Core.Entities.Creatures.Creature c)
        {
            try
            {
                if (c == null || c.CombatState == null) return false;
                if (c.CombatState.Players == null) return false;
                foreach (var p in c.CombatState.Players)
                    if (p != null && p.Creature == c && p.Character != null && p.Character.Id.Entry == "ZHIJI") return true;
            }
            catch { }
            return false;
        }

        // 第一层第一次随机事件 -> 强制换成"阴暗饭馆"（只在本局第一次、且还没拿黄金寿衣时）
        public static void ForceDinerPostfix(ref MegaCrit.Sts2.Core.Models.EventModel __result)
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null || rs.CurrentActIndex != 0) return;
                if (rs.Players == null || rs.Players.Count == 0) return;
                var pl = rs.Players[0];
                var st = ZhijiStore.Of(pl);
                if (st.DinerSeen) return;
                bool hasShroud = false;
                try { hasShroud = pl.Relics != null && pl.Relics.Any(r => r is GoldenShroud); } catch { }
                if (hasShroud) { st.DinerSeen = true; return; }
                var ev = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.WoodCarvings>();
                if (ev == null) return;
                st.DinerSeen = true;
                __result = ev;
                ZLog.W("first act event forced -> dark diner");
            }
            catch (Exception e) { ZLog.W("force diner fail " + e.Message); }
        }

        // 打开商人房间（引擎原生流程），卡片商品由 MerchantInventory 补丁替换成流血基础卡
        [System.ThreadStatic] static bool _merchantCtx;
        // 只有饭馆事件打开的商店才卖 DOT 卡；地图上的普通商人保持原版随机卡池
        public static bool DotShopRequested;

        public static void RequestDotShop() { DotShopRequested = true; }
        public static bool MerchantCtxPrefix(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            try
            {
                _merchantCtx = DotShopRequested && player != null && player.Relics != null && player.Relics.Any(r => r is GoldenShroud);
                ZLog.W("merchant ctx shroud=" + _merchantCtx);
            }
            catch { _merchantCtx = false; }
            return true;
        }

        // 吱咿卡面是正方形，引擎按原版比例会留黑边：改成"等比铺满裁切"，让卡面填满卡框
        // 吱咿卡牌左上角的能量图标：换成专属图标（原版按 EnergyColorName 取铁甲图标）
        // 吱咿能量球：tint 成红金色（场景资源是原版共用，无法直接换场景）
        public static void EnergyOrbPostfix(object __result, object player)
        {
            try
            {
                if (__result == null || player == null) return;
                var pl = (MegaCrit.Sts2.Core.Entities.Players.Player)player;
                if (pl.Character == null || pl.Character.Id.Entry != "ZHIJI") return;
                var node = __result as Godot.Node;
                if (node == null) return;
                if (node is Godot.CanvasItem ci) ci.Modulate = new Godot.Color(1f, 0.72f, 0.55f, 1f);
                ZLog.W("energy orb tinted for zhiji");
            }
            catch (Exception e) { ZLog.W("energy orb tint fail " + e.Message); }
        }

        public static void CardEnergyIconPostfix(object __instance, ref Godot.Texture2D __result)
        {
            try
            {
                if (!(__instance is ZhijiCard)) return;
                var t = Art.Get("icon_energy");
                if (t != null) __result = t;
            }
            catch { }
        }

        public static void NCardPortraitPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var t = __instance.GetType();
                var modelP = t.GetProperty("Model", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var model = modelP != null ? modelP.GetValue(__instance) : null;
                if (!(model is ZhijiCard)) return;
                var f = t.GetField("_portrait", BindingFlags.Instance | BindingFlags.NonPublic);
                var rect = f != null ? f.GetValue(__instance) as Godot.TextureRect : null;
                if (rect == null) return;
                rect.StretchMode = Godot.TextureRect.StretchModeEnum.KeepAspectCovered;
            }
            catch (Exception eP) { ZLog.W("card portrait fill fail " + eP.GetBaseException().Message); }
        }

        public static void NRelicCreatePostfix(MegaCrit.Sts2.Core.Models.RelicModel relic, ref object __result)
        {
            try
            {
                if (relic == null || __result == null) return;
                string key = null;
                switch (relic.GetType().Name)
                {
                    case "GoldenShroud": key = "icon_shroud"; break;
                    case "FleshStilts": key = "icon_stilts"; break;
                    case "CrimsonStage": key = "icon_stage"; break;
                    case "BloodGoblet": key = "icon_craving"; break;
                    case "CarvingKnife": key = "icon_bleed"; break;
                    case "EndlessPlate": key = "icon_feast"; break;
                    case "BleedBoard": key = "icon_bleed"; break;
                    case "DinnerBell": key = "icon_hunger"; break;
                    case "CrimsonCradle": key = "icon_cradle"; break;
                    case "BloodNail": key = "icon_bleed"; break;
                    case "Stigmata": key = "icon_feast"; break;
                    case "BloodFunnel": key = "icon_bleed"; break;
                    case "VilePurse": key = "icon_vile"; break;
                    case "HungerBell": key = "icon_hunger"; break;
                    case "VileCrown": key = "icon_vile"; break;
                    case "CrimsonVisor": key = "icon_feast"; break;
                    case "WarSigil": key = "icon_feast"; break;
                    case "RationBag": key = "icon_hunger"; break;
                    case "OldScythe": key = "icon_feast"; break;
                    case "UnboundHeart": key = "icon_bleed"; break;
                    case "SatedStomach": key = "icon_feast"; break;
                    case "ShadowCloak": key = "icon_dodge"; break;
                    case "RedGodHeart": key = "vassal_idle"; break;
                    case "GuardSigil": key = "icon_bleed"; break;
                    case "BloodHourglass": key = "icon_bleed"; break;
                    case "VileWatch": key = "icon_vile"; break;
                    case "BlackNapkin": key = "icon_bleed_big"; break;
                }
                if (key == null) return;
                var tex = Art.Get(key);
                if (tex == null) return;
                var t = __result.GetType();
                var iconNode = t.GetProperty("Icon")?.GetValue(__result) as Godot.TextureRect;
                var outlineNode = t.GetProperty("Outline")?.GetValue(__result) as Godot.TextureRect;
                if (iconNode != null) iconNode.Texture = tex;
                if (outlineNode != null) outlineNode.Texture = tex;
                ZLog.W("relic node texture -> " + key + " (" + relic.GetType().Name + ")");
            }
            catch (Exception e) { ZLog.W("relic node texture fail " + e.GetBaseException().Message); }
        }

        static string VanillaSpriteFor(string typeName)
        {
            switch (typeName)
            {
                case "GoldenShroud": case "FleshStilts": return "burning_blood";
                case "CrimsonStage": case "BloodGoblet": return "blood_vial";
                case "CarvingKnife": return "book_repair_knife";
                case "EndlessPlate": return "gold_plated_cables";
                case "BleedBoard": return "blood_soaked_rose";
                case "DinnerBell": return "calling_bell";
                case "CrimsonCradle": return "burning_blood";
                case "BloodNail": return "blood_soaked_rose";
                case "Stigmata": return "blood_vial";
                case "BloodFunnel": return "blood_soaked_rose";
                case "VilePurse": return "calling_bell";
                case "HungerBell": return "calling_bell";
                case "VileCrown": return "burning_blood";
                case "VileWatch": return "calling_bell";
                case "BlackNapkin": return "blood_soaked_rose";
                case "CrimsonVisor": return "burning_blood";
                case "WarSigil": return "burning_blood";
                case "RationBag": return "gold_plated_cables";
                case "OldScythe": return "book_repair_knife";
                case "UnboundHeart": return "burning_blood";
                case "SatedStomach": return "gold_plated_cables";
                case "ShadowCloak": return "burning_blood";
                case "RedGodHeart": return "burning_blood";
                case "GuardSigil": return "gold_plated_cables";
                case "BloodHourglass": return "blood_soaked_rose";
                default: return null;
            }
        }

        public static bool RelicPackedPathPrefix(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref string __result)
        {
            try
            {
                if (__instance == null) return true;
                var name = VanillaSpriteFor(__instance.GetType().Name);
                if (name == null) return true;
                __result = "res://images/atlases/relic_atlas.sprites/" + name + ".tres";
                ZLog.W("relic packed path -> " + name + " (" + __instance.GetType().Name + ")");
                return false;
            }
            catch { return true; }
        }

        public static bool RelicPackedOutlinePrefix(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref string __result)
        {
            return RelicPackedPathPrefix(__instance, ref __result);
        }

        static int PressProceedButtons(Godot.Node node)
        {
            int n = 0;
            try
            {
                if (node == null) return 0;
                var tn = node.GetType().Name;
                if (tn == "NProceedButton")
                {
                    try
                    {
                        var m = node.GetType().GetMethod("OnRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
                        if (m != null) { m.Invoke(node, null); n++; ZLog.W("proceed pressed " + node.Name); }
                        else
                        {
                            var em = node.GetType().GetMethod("EmitSignal", new[] { typeof(string) });
                            if (em != null) { em.Invoke(node, new object[] { "pressed" }); n++; ZLog.W("proceed emitted " + node.Name); }
                        }
                    }
                    catch (Exception e1) { ZLog.W("proceed invoke fail " + e1.GetBaseException().Message); }
                }
                foreach (var c in node.GetChildren())
                    n += PressProceedButtons(c);
            }
            catch { }
            return n;
        }

        public static void MerchantCtxPostfix()
        {
            _merchantCtx = false;
            DotShopRequested = false;
        }

        public static bool PoolMerchantPrefix(MegaCrit.Sts2.Core.Models.CardPoolModel __instance,
            ref System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.CardModel> __result)
        {
            try
            {
                if (!_merchantCtx) return true;
                if (!(__instance is ZhijiCardPool)) return true;
                var list = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    var z = c as ZhijiCard;
                    if (z == null || !z.DotStyle) continue;
                    list.Add(c);   // 商人可能要求不同稀有度，全部 DOT 卡都放进池子
                }
                // 商人会要求 Attack/Skill/Power 三类 + 不同稀有度，补几张原版 Power 卡防止生成失败
                int addedP = 0;
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    if (c == null || c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Power) continue;
                    if (c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic
                        || c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Event
                        || c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Ancient) continue;
                    list.Add(c); addedP++;
                    if (addedP >= 3) break;
                }
                if (list.Count == 0) return true;
                __result = list;
                ZLog.W("merchant pool -> DOT basics n=" + list.Count);
                return false;
            }
            catch { return true; }
        }

        // 商人卡池替换：在 CreateForNormalMerchant 订阅之前填好条目
        public static bool MerchantPopulatePrefix(MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory __instance)
        {
            try
            {
                var pl = __instance != null ? __instance.Player : null;
                if (pl == null) return true;
                bool shroud = false;
                try { shroud = pl.Relics != null && pl.Relics.Any(r => r is GoldenShroud); } catch { }
                if (!shroud) return true;
                var invT = __instance.GetType();
                var listF = invT.GetField("_characterCardEntries", BindingFlags.Instance | BindingFlags.NonPublic);
                var list = listF != null ? listF.GetValue(__instance) as System.Collections.IList : null;
                if (list == null) return true;
                list.Clear();
                var cefT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry", false) : null;
                if (cefT == null) return true;
                var atk = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                var skl = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    var z = c as ZhijiCard;
                    if (z == null || !z.DotStyle) continue;
                    if (c.Id.Entry == "SLIT_THROAT" || c.Id.Entry == "BLOOD_DEBT") atk.Add(c);
                    if (c.Id.Entry == "BLOOD_LET") skl.Add(c);
                }
                var plan = new (System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> pool, MegaCrit.Sts2.Core.Entities.Cards.CardType type)[]
                {
                    (atk, MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack),
                    (skl, MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill),
                    (atk, MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack),
                };
                foreach (var it in plan)
                {
                    if (it.pool.Count == 0) continue;
                    var entry = System.Activator.CreateInstance(cefT, new object[] { pl, __instance, it.pool, it.type });
                    try { cefT.GetMethod("Populate")?.Invoke(entry, null); } catch (Exception ep) { ZLog.W("merchant populate fail " + ep.GetBaseException().Message); }
                    list.Add(entry);
                }
                ZLog.W("merchant populate replaced n=" + list.Count);
                return false;   // 跳过原随机填充
            }
            catch (Exception e) { ZLog.W("merchant populate prefix fail " + e.GetBaseException().Message); return true; }
        }

        // 商人卖卡：有黄金寿衣时，把商人随机到的卡换成对应的流血基础卡（保留原条目/UI/购买逻辑）
        static readonly System.Random _merchantRng = new System.Random();
        public static void MerchantCardPickPostfix(MegaCrit.Sts2.Core.Entities.Players.Player player,
            ref MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult __result)
        {
            try
            {
                if (player == null) return;
                bool shroud = false;
                try { shroud = player.Relics != null && player.Relics.Any(r => r is GoldenShroud); } catch { }
                if (!shroud) return;
                var want = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    var z = c as ZhijiCard;
                    if (z == null || !z.DotStyle) continue;
                    if (c.Id.Entry == "THOUSAND_BLADES" || c.Id.Entry == "DETONATE" || c.Id.Entry == "BLOOD_FEAST"
                        || c.Id.Entry == "BLOOD_MIST" || c.Id.Entry == "DEEPEN" || c.Id.Entry == "ECHO_OF_BLOOD") continue;
                    want.Add(c);
                }
                if (want.Count == 0) return;
                var proto = want[_merchantRng.Next(want.Count)];
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null) return;
                var inst = rs.CreateCard(proto, player);
                if (inst == null) return;
                __result = new MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult(inst);
                ZLog.W("merchant card -> " + inst.Id.Entry);
            }
            catch (Exception e) { ZLog.W("merchant card pick fail " + e.GetBaseException().Message); }
        }

        public static void MerchantDotCardsPostfix(MegaCrit.Sts2.Core.Entities.Players.Player player,
            ref MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory __result)
        {
            try
            {
                if (player == null || __result == null) return;
                bool shroud = false;
                try { shroud = player.Relics != null && player.Relics.Any(r => r is GoldenShroud); } catch { }
                if (!shroud) return;
                var inv = __result;
                var invT = inv.GetType();
                var listF = invT.GetField("_characterCardEntries", BindingFlags.Instance | BindingFlags.NonPublic);
                var list = listF != null ? listF.GetValue(inv) as System.Collections.IList : null;
                if (list == null) { ZLog.W("merchant dot: list missing"); return; }
                list.Clear();
                string[] ids = { "SLIT_THROAT", "BLOOD_LET", "BLOOD_DEBT" };
                var cefT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry", false) : null;
                if (cefT == null) return;
                foreach (var id in ids)
                {
                    MegaCrit.Sts2.Core.Models.CardModel proto = null;
                    foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards) if (c != null && c.Id.Entry == id) { proto = c; break; }
                    if (proto == null) continue;
                    var pool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> { proto };
                    var entry = System.Activator.CreateInstance(cefT, new object[] { player, inv, pool, proto.Type });
                    try { cefT.GetMethod("Populate")?.Invoke(entry, null); } catch (Exception ep) { ZLog.W("merchant populate fail " + ep.GetBaseException().Message); }
                    list.Add(entry);
                }
                ZLog.W("merchant cards replaced with DOT basics (n=" + list.Count + ")");
            }
            catch (Exception e) { ZLog.W("merchant dot cards fail " + e.GetBaseException().Message); }
        }

        public static void OpenDotShop(MegaCrit.Sts2.Core.Entities.Players.Player pl)
        {
            try
            {
                var roomT = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Rooms.MerchantRoom", false) : null;
                if (roomT == null) { ZLog.W("openshop: no room type"); return; }
                var room = System.Activator.CreateInstance(roomT) as MegaCrit.Sts2.Core.Rooms.AbstractRoom;
                if (room == null) { ZLog.W("openshop: room create fail"); return; }
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                _ = rm.EnterRoom(room).ContinueWith(x => ZLog.W("openshop ok=" + (x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    + (x.Exception != null ? (" err=" + x.Exception.GetBaseException().Message) : "")));
            }
            catch (Exception e) { ZLog.W("openshop fail " + e.GetBaseException().Message); }
        }

        // 奖励偏向：DOT 流玩家更容易刷到 DOT 卡；普通玩家更容易刷到咿幼年卡
        static readonly System.Random _rewardRng = new System.Random();
        // 奖励卡池守卫：强制只用角色卡池（70% 专属 + 30% 精选通用），避免外部共享池稀释
        public static void RewardPoolPrefix(MegaCrit.Sts2.Core.Entities.Players.Player player,
            ref MegaCrit.Sts2.Core.Runs.CardCreationOptions options)
        {
            try
            {
                if (player == null || options == null) return;
                if (player.Character == null || player.Character.Id.Entry != "ZHIJI") return;
                var pool = player.Character.CardPool;
                if (pool != null) options = options.WithCardPools(new[] { pool });
            }
            catch (Exception e) { ZLog.W("reward pool guard fail " + e.Message); }
        }

        // 稀有卡限 1（3.3.5）：三选一里最多保留一张稀有，其余降为罕见。
        // 旧实现嵌在「失控模式」分支里，常态/黄金寿衣路线根本没生效。
        static int ApplyRareCap(System.Collections.Generic.List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> list,
            MegaCrit.Sts2.Core.Entities.Players.Player player, string tag)
        {
            int replaced = 0;
            try
            {
                if (list == null || list.Count < 2) return 0;
                int rareCount = 0;
                foreach (var rr in list) { try { if (rr.Card != null && rr.Card.Rarity.ToString() == "Rare") rareCount++; } catch { } }
                if (rareCount <= 1) return 0;
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null) return 0;
                var pool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var cc in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                    if (cc is ZhijiCard && cc.Rarity.ToString() == "Uncommon") pool.Add(cc);
                if (pool.Count == 0)
                    foreach (var cc in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                        if (cc != null && cc.Rarity.ToString() == "Uncommon") pool.Add(cc);
                if (pool.Count == 0) { ZLog.W(tag + " rare cap: no uncommon pool"); return 0; }
                bool kept = false;
                for (int i = 0; i < list.Count && rareCount > 1; i++)
                {
                    try
                    {
                        if (list[i].Card == null || list[i].Card.Rarity.ToString() != "Rare") continue;
                        if (!kept) { kept = true; continue; }
                        var pick = pool[_rewardRng.Next(pool.Count)];
                        var inst = rs.CreateCard(pick, player);
                        if (inst == null) continue;
                        list[i].ModifyCard(inst);
                        rareCount--; replaced++;
                        ZLog.W(tag + " rare cap -> " + inst.Id.Entry);
                    }
                    catch { }
                }
            }
            catch (Exception e) { ZLog.W(tag + " rare cap fail " + e.Message); }
            return replaced;
        }

        public static void RewardBiasPostfix(MegaCrit.Sts2.Core.Entities.Players.Player player,
            ref System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> __result)
        {
            try
            {
                if (player == null || __result == null) return;
                if (player.Character == null || player.Character.Id.Entry != "ZHIJI") return;
                var list = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>(__result);
                if (list.Count == 0) return;
                bool dot = false;
                try { dot = player.Relics != null && player.Relics.Any(r => r is GoldenShroud); } catch { }
                ZLog.W("reward hook: cards=" + list.Count + " dotMode=" + dot);
                bool unboundMode = false;
                try { unboundMode = player.Relics != null && player.Relics.Any(r => r is UnboundHeart); } catch { }
                // 未持有失控之心：奖励里不允许出现召唤/失控牌
                if (!unboundMode)
                {
                    try
                    {
                        var rmN = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsN = rmN != null ? rmN.DebugOnlyGetState() : null;
                        if (rsN != null)
                        {
                            var normalPool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                            {
                                var zn = c as ZhijiCard;
                                if (zn == null || zn.RedGod) continue;
                                if (_summonOnlyCards.Contains(c.Id.Entry)) continue;
                                if (c is ChildhoodEnd || c is HungerFeast) continue;
                                if (c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic) continue;
                                normalPool.Add(c);
                            }
                            if (normalPool.Count > 0)
                                for (int i = 0; i < list.Count; i++)
                                {
                                    var cur = list[i].Card;
                                    if (cur == null || !_summonOnlyCards.Contains(cur.Id.Entry)) continue;
                                    var repl = normalPool[_rewardRng.Next(normalPool.Count)];
                                    var instN = rsN.CreateCard(repl, player);
                                    if (instN != null) { list[i].ModifyCard(instN); ZLog.W("reward scrub summon -> " + instN.Id.Entry); }
                                }
                        }
                    }
                    catch (Exception eScrub) { ZLog.W("reward scrub fail " + eScrub.Message); }
                }
                if (unboundMode)
                {
                    try
                    {
                        var rmU = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsU = rmU != null ? rmU.DebugOnlyGetState() : null;
                        if (rsU != null)
                        {
                            var wanted2 = new System.Collections.Generic.HashSet<string>
                            {
                                "FEAST_CALL","MULTIPLY_GUEST","PROTECT_GUEST","LULLABY_GUEST","DEVOUR_GUEST",
                                "FEED_GUEST","DINE_TOGETHER","SACRIFICE_GUEST",
                                "UNBOUND_BITE","UNBOUND_GLUTTONY","UNBOUND_FEAST","UNBOUND_ROAR"
                            };
                            var unPool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                                if (c != null && wanted2.Contains(c.Id.Entry)) unPool.Add(c);
                            if (unPool.Count > 0 && list.Count > 0)
                            {
                                // 只替换 1 个槽位为召唤/失控牌，其余保持原奖励池，避免"永远三四张同卡"
                                var usedU = new System.Collections.Generic.HashSet<string>();
                                foreach (var rr in list) { try { if (rr.Card != null) usedU.Add(rr.Card.Id.Entry); } catch { } }
                                int idx = _rewardRng.Next(list.Count);
                                MegaCrit.Sts2.Core.Models.CardModel pickU = null;
                                for (int t = 0; t < 20; t++)
                                {
                                    var cand = unPool[_rewardRng.Next(unPool.Count)];
                                    if (cand != null && !usedU.Contains(cand.Id.Entry)) { pickU = cand; break; }
                                }
                                if (pickU != null)
                                {
                                    var instU = rsU.CreateCard(pickU, player);
                                    if (instU != null) { list[idx].ModifyCard(instU); ZLog.W("unbound reward bias: 1 slot -> " + instU.Id.Entry); }
                                }
                                ZLog.W("unbound reward mixed: " + list.Count + " cards");
                            }
                        }
                    }
                    catch (Exception eUR) { ZLog.W("unbound reward pool fail " + eUR.Message); }
                }
                // 全模式稀有卡限 1（3.3.5）
                try { ApplyRareCap(list, player, "reward"); } catch { }
                // 保底：3 选 1 中至少 2 张是吱咿专属卡（用原版牌占位则替换）
                try
                {
                    int own = 0;
                    foreach (var r in list) if (r.Card is ZhijiCard) own++;
                    if (own < 2 && list.Count >= 2)
                    {
                        var rmG = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsG = rmG != null ? rmG.DebugOnlyGetState() : null;
                        if (rsG != null)
                        {
                            var ownPool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                            {
                                var z = c as ZhijiCard;
                                if (z == null || z.RedGod) continue;
                                if (!unboundMode && _summonOnlyCards.Contains(c.Id.Entry)) continue;
                                if (c is ChildhoodEnd || c is HungerFeast) continue;
                                if (c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic) continue;
                                ownPool.Add(c);
                            }
                            var used = new System.Collections.Generic.HashSet<string>();
                            foreach (var r in list) { try { if (r.Card != null) used.Add(r.Card.Id.Entry); } catch { } }
                            for (int i = 0; i < list.Count && own < 2; i++)
                            {
                                if (list[i].Card is ZhijiCard) continue;
                                MegaCrit.Sts2.Core.Models.CardModel pickG = null;
                                for (int tryN = 0; tryN < 12; tryN++)
                                {
                                    var cand = ownPool[_rewardRng.Next(ownPool.Count)];
                                    if (cand == null || used.Contains(cand.Id.Entry)) continue;
                                    pickG = cand; break;
                                }
                                if (pickG == null) continue;
                                var instG = rsG.CreateCard(pickG, player);
                                if (instG == null) continue;
                                list[i].ModifyCard(instG);
                                used.Add(instG.Id.Entry);
                                own++;
                                ZLog.W("reward guarantee: slot " + i + " -> " + instG.Id.Entry);
                            }
                        }
                    }
                }
                catch (Exception eG) { ZLog.W("reward guarantee fail " + eG.Message); }
                bool hasTarget = false;
                foreach (var r in list)
                {
                    var z = r.Card as ZhijiCard;
                    if (z == null) continue;
                    if (dot && z.DotStyle) hasTarget = true;
                    if (!dot && !z.DotStyle && !z.RedGod) hasTarget = true;
                }
                if (hasTarget) return;
                // 没选饭馆事件的玩家也能玩流血：小概率直接把一张 DOT 卡塞进奖励
                if (!dot && _rewardRng.NextDouble() < 0.12)
                {
                    var dotPool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                    foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                    {
                        var z = c as ZhijiCard;
                        if (z == null || !z.DotStyle || z.RedGod) continue;
                        if (c is ChildhoodEnd || c is HungerFeast) continue;
                        if (c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic) continue;
                        dotPool.Add(c);
                    }
                    if (dotPool.Count > 0)
                    {
                        var rmD = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                        var rsD = rmD != null ? rmD.DebugOnlyGetState() : null;
                        if (rsD != null)
                        {
                            var instD = rsD.CreateCard(dotPool[_rewardRng.Next(dotPool.Count)], player);
                            if (instD != null)
                            {
                                list[_rewardRng.Next(list.Count)].ModifyCard(instD);
                                __result = list;
                                ZLog.W("reward bias injected: " + instD.Id.Entry + " (dot=True, no-event fallback)");
                                return;
                            }
                        }
                    }
                }
                if (_rewardRng.NextDouble() > (dot ? 0.35 : 0.2)) return;
                var pool = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel>();
                foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
                {
                    var z = c as ZhijiCard;
                    if (z == null || z.RedGod) continue;
                    if (c is ChildhoodEnd || c is HungerFeast) continue;
                    if (c.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic) continue;
                    if (dot && z.DotStyle) pool.Add(c);
                    if (!dot && !z.DotStyle) pool.Add(c);
                }
                if (pool.Count == 0) return;
                int pick = _rewardRng.Next(pool.Count);
                int slot = _rewardRng.Next(list.Count);
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null) return;
                var inst = rs.CreateCard(pool[pick], player);
                if (inst == null) return;
                list[slot].ModifyCard(inst);
                __result = list;
                ZLog.W("reward bias injected: " + inst.Id.Entry + " (dot=" + dot + ")");
            }
            catch (Exception e) { ZLog.W("reward bias fail " + e.Message); }
        }

        // 跑局期间是否吱咿（缓存兜底）
        // 事件在"房间切换中"生成选项时，RunManager.DebugOnlyGetState() 偶尔拿不到玩家，
        // 旧实现直接 return false，导致事件劫持被跳过、显示成原版事件（玩家看到的就是"事件出 bug"）。
        static volatile bool _zhijiRunCached;

        static bool IsZhijiRun()
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null || rs.Players == null || rs.Players.Count == 0) return _zhijiRunCached;
                var p = rs.Players[0];
                bool ok = p != null && p.Character != null && p.Character.Id.Entry == "ZHIJI";
                _zhijiRunCached = ok;      // 拿到状态就以它为准（非吱咿会立刻清掉缓存）
                return ok;
            }
            catch { return _zhijiRunCached; }
        }

        static bool _forceHatchCached;
        static int _forceHatchCheckedFor = -1;

        public static bool ForceHatchCached => _forceHatchCached;
        static bool _forceRedHeartCached;
        static bool _redHeartArtActive;
        static bool _settingPortrait;
        public static bool ForceRedHeartCached => _forceRedHeartCached;
        public static System.Type GameAsmType(string name)
        {
            try { var a = GameAsm(); return a != null ? a.GetType(name, false) : null; } catch { return null; }
        }
        public static bool RedHeartArtActive => _redHeartArtActive;

        // 命令文本被统一转小写，按名字找类型时必须忽略大小写
        static Type FindGameTypeCI(string shortName)
        {
            try
            {
                var asm = GameAsm();
                if (asm == null) return null;
                foreach (var t in asm.GetTypes())
                {
                    if (t == null) continue;
                    if (string.Equals(t.Name, shortName, StringComparison.OrdinalIgnoreCase)) return t;
                }
            }
            catch { }
            return null;
        }

        public static bool ShouldForceRedGodHeart(MegaCrit.Sts2.Core.Models.EventModel ev)
        {
            try
            {
                if (!IsZhijiRun()) return false;
                if (ev != null)
                {
                    // 3.3.17：原来只在 DenseVegetation/SunkenTreasury 两个宿主上劫持，
                    // 但第二层事件是 50+ 个里随机抽，实测两局长跑（共 8 个事件房）一次都没抽到，
                    // 等于玩家几乎遇不到红神之心。改为：非远古/开局祝福类事件都可以劫持
                    //（与第一层「孵化」的策略一致）。
                    var host = ev.GetType().FullName ?? "";
                    if (host.Contains("Ancient") || host.Contains("Neow") || host.Contains("Deprecated")) return false;
                    if (host.Contains("MockEventModel")) return false;
                }
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl == null) return false;
                bool hasHeart = false, hasRG = false;
                if (pl.Relics != null) foreach (var r in pl.Relics) { if (r is UnboundHeart) hasHeart = true; if (r is RedGodHeart) hasRG = true; }
                ZLog.W("red heart check hasHeart=" + hasHeart + " hasRG=" + hasRG + " cached=" + _forceRedHeartCached);
                if (!hasHeart || (hasRG && !_forceRedHeartCached)) return false;
                var st = ZhijiStore.Of(pl);
                if (st == null) return false;
                _redHeartArtActive = true;
                if (_forceRedHeartCached) return true;
                if (!ZhijiStore.HasOnce(pl, "hatch_seen")) { _redHeartArtActive = false; return false; }
                int act = 1;
                try { act = pl.RunState.CurrentActIndex; } catch { }
                return act == 1 && !st.RedHeartDone;
            }
            catch { }
            return false;
        }

        static void RefreshForceHatchCache()
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl == null) return;
                bool hasRelic = false;
                if (pl.Relics != null) foreach (var r in pl.Relics) if (r is UnboundHeart) { hasRelic = true; break; }
                _forceHatchCached = !hasRelic && !ZhijiStore.HasOnce(pl, "hatch_seen");
            }
            catch { }
        }

        static Godot.Texture2D _unboundIcon;
        static bool _unboundIconTried;

        static Godot.Texture2D UnboundIcon()
        {
            if (_unboundIcon != null || _unboundIconTried) return _unboundIcon;
            _unboundIconTried = true;
            try { _unboundIcon = Art.Get("enchant_unbound_rage"); } catch { }
            return _unboundIcon;
        }

        public static void EventRoomCanonicalPostfix(object __instance, ref MegaCrit.Sts2.Core.Models.EventModel __result)
        {
            try
            {
                if (__result == null) return;
                if (ShouldForceHatch(__result))
                {
                    var host = MegaCrit.Sts2.Core.Models.ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Reflections>();
                    if (host != null) { __result = host; }
                }
            }
            catch { }
        }

        public static void PowerExhaustPostfix(MegaCrit.Sts2.Core.Models.CardModel __instance,
            ref MegaCrit.Sts2.Core.Entities.Cards.CardLocation __result)
        {
            try
            {
                if (__instance == null) return;
                if (!(__instance is ZhijiCard)) return;
                if (__instance.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Power) return;
                __result = new MegaCrit.Sts2.Core.Entities.Cards.CardLocation(__instance.Owner,
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust,
                    MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom);
            }
            catch { }
        }

        static readonly Dictionary<string, string> PowerArtMap = new Dictionary<string, string>
        {
            {"PulsePower","pulse_aid"}, {"HungerPower","hunger_feast"}, {"FeastPower","blood_feast"},
            {"StandPower","escape"}, {"FaminePower","hunger_echo"}, {"BloodThirstPower","blood_let"},
            {"DinerGuestPower","feast_call"}, {"ResonancePower","blood_resonance"}, {"LeftoverPower","leftover_use"},
            {"CommandPower","command_guest"}, {"DodgeChancePower","dodge_instinct"}, {"BloodTidePower","blood_tide"},
            {"BleedInstinctPower","bleed_instinct"}, {"HungerRhythmPower","hunger_rhythm"},
            {"VileResonancePower","vile_resonance"}, {"HungerPactPower","hunger_pact"},
            {"GluttonInstinctPower","glutton_instinct"}, {"BleedCapPower","blood_vessel"},
            {"BloodEchoPower","blood_echo"}, {"VileLoopPower","vile_loop"}, {"VileThingPower","vile_resonance"},
            {"CravingPower","feast_call"}, {"ReapPower","harvest"}, {"BleedPower","blood_let"},
        };

        public static void PowerNodeIconPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var t = __instance.GetType();
                var modelProp = t.GetProperty("Model");
                var model = modelProp != null ? modelProp.GetValue(__instance) : null;
                if (model == null) return;
                string tn = model.GetType().Name;
                // 优先使用专门生成的 buff 图标（icon_buff_<snake>）
                string tnNo = tn.EndsWith("Power") ? tn.Substring(0, tn.Length - 5) : tn;
                string snake = System.Text.RegularExpressions.Regex.Replace(tnNo, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
                string key = "icon_buff_" + snake;
                var iconTex = Art.Get(key);
                if (iconTex == null && !PowerArtMap.TryGetValue(tn, out key)) return;
                var tex = iconTex != null ? iconTex : Art.Get(key);
                if (tex == null) return;
                var iconProp = t.GetField("_icon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var icon = iconProp != null ? iconProp.GetValue(__instance) as Godot.TextureRect : null;
                if (icon == null)
                {
                    var node = __instance as Godot.Node;
                    if (node != null) icon = node.GetNodeOrNull<Godot.TextureRect>("%Icon");
                }
                if (icon != null) icon.Texture = tex;
            }
            catch { }
        }

        public static void PowerIconPostfix(object __instance, ref string __result)
        {
            try
            {
                if (__instance == null) return;
                var tn = __instance.GetType().FullName ?? "";
                if (!tn.StartsWith("Zhiji.")) return;
                // 我们的能力牌使用原版图标，避免缺图导致 HUD 不显示
                __result = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("powers/strength.png");
            }
            catch { }
        }

        static void ApplyEventPortrait(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom room)
        {
            try
            {
                var layout = room.Layout as Godot.Node;
                if (layout == null) return;
                var tex = Art.Get("event_red_heart");
                if (tex == null) { ZLog.W("event portrait tex missing"); return; }
                int n = 0;
                var stack = new Stack<Godot.Node>();
                stack.Push(layout);
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (node == null) continue;
                    foreach (var ch in node.GetChildren()) if (ch is Godot.Node cn) stack.Push(cn);
                    if (node is Godot.TextureRect tr)
                    {
                        var nm = tr.Name.ToString();
                        if (n < 8) ZLog.W("event node tex " + nm + " size=" + tr.Size + " hasTex=" + (tr.Texture != null));
                        if (tr.Size.X >= 400 || nm.ToLower().Contains("portrait") || nm.ToLower().Contains("art"))
                        {
                            tr.Texture = tex; n++;
                        }
                    }
                }
                ZLog.W("red heart portrait textures applied=" + n);
            }
            catch (Exception e) { ZLog.W("apply event portrait fail " + e.Message); }
        }

        public static void EventRefreshPostfix(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)
        {
            try
            {
                if (__instance == null || !_redHeartArtActive) return;
                ApplyEventPortrait(__instance);
            }
            catch { }
        }

        static long _lastRunTick;
        public static bool LargeCapsulePrefix(MegaCrit.Sts2.Core.Models.Relics.LargeCapsule __instance, ref System.Threading.Tasks.Task __result)
        {
            ZLog.W("large capsule AfterObtained start (custom safe path)");
            try
            {
                var owner = __instance != null ? __instance.Owner : null;
                if (owner == null) return true;
                __result = CapsuleSafe(owner);
                return false;
            }
            catch (Exception e) { ZLog.W("capsule safe fail " + e.Message); return true; }
        }

        static async System.Threading.Tasks.Task CapsuleSafe(MegaCrit.Sts2.Core.Entities.Players.Player owner)
        {
            try
            {
                var pool = new List<MegaCrit.Sts2.Core.Models.RelicModel>();
                var owned = new System.Collections.Generic.HashSet<string>();
                try { if (owner.Relics != null) foreach (var r in owner.Relics) owned.Add(r.Id.Entry); } catch { }
                foreach (var r in MegaCrit.Sts2.Core.Models.ModelDb.AllRelics)
                {
                    if (r == null) continue;
                    try { if (owned.Contains(r.Id.Entry)) continue; } catch { }
                    try { if (r.Rarity.ToString() == "Boss") continue; } catch { }
                    try { if (r.Rarity.ToString() == "Event") continue; } catch { }
                    pool.Add(r);
                }
                var rng = new System.Random();
                int got = 0;
                for (int i = 0; i < 2 && pool.Count > 0; i++)
                {
                    var pick = pool[rng.Next(pool.Count)];
                    pool.Remove(pick);
                    try
                    {
                        var mut = pick.ToMutable();
                        await MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain(mut, owner);
                        got++;
                        ZLog.W("capsule safe relic +" + pick.Id.Entry);
                    }
                    catch (Exception eR) { ZLog.W("capsule relic fail " + pick.Id.Entry + " " + eR.GetBaseException().Message); }
                }
                // 一对打击/防御：从牌组里找原型复制
                try
                {
                    var rs = owner.RunState;
                    foreach (var want in new[] { "STRIKE", "DEFEND" })
                    {
                        MegaCrit.Sts2.Core.Models.CardModel proto = null;
                        try { if (owner.Deck != null && owner.Deck.Cards != null) foreach (var c in owner.Deck.Cards) if (c != null && c.Id.Entry.ToUpperInvariant().Contains(want)) { proto = c; break; } } catch { }
                        ZLog.W("capsule card proto " + want + " = " + (proto != null ? proto.Id.Entry : "null"));
                        if (proto == null) continue;
                        // 牌组里拿到的是可变实例，引擎拒绝（"Mutable model ... used in incorrect place"）；
                        // 必须用 ModelDb 里的 canonical 模型来 CreateCard。
                        MegaCrit.Sts2.Core.Models.CardModel canonical = null;
                        try { canonical = MegaCrit.Sts2.Core.Models.ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.CardModel>(proto.Id); } catch { }
                        if (canonical == null) { ZLog.W("capsule card canonical null " + proto.Id.Entry); continue; }
                        var inst = rs != null ? rs.CreateCard(canonical, owner) : null;
                        if (inst == null) continue;
                        _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck, MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                        ZLog.W("capsule card added " + inst.Id.Entry);
                    }
                }
                catch (Exception eC) { ZLog.W("capsule cards fail " + eC.GetBaseException().Message); }
                ZLog.W("capsule safe done relics=" + got);
            }
            catch (Exception e) { ZLog.W("capsule safe grant fail " + e.GetBaseException().Message); }
        }
        public static void LargeCapsulePostfix(object __instance) { ZLog.W("large capsule AfterObtained returned"); }

        public static void NRunProcessPostfix()
        {
            try
            {
                long now = System.Environment.TickCount64;
                if (now - _lastRunTick < 500) return;
                _lastRunTick = now;
                Visuals.EventArtTickPublic();
            }
            catch { }
        }

        public static void EventRoomReadyPostfix(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)
        {
            try
            {
                if (__instance == null) return;
                var f = __instance.GetType().GetField("_event", BindingFlags.NonPublic | BindingFlags.Instance);
                var model = f != null ? f.GetValue(__instance) as MegaCrit.Sts2.Core.Models.EventModel : null;
                bool force = model != null && ShouldForceRedGodHeart(model);
                ZLog.W("event room ready host=" + (model != null ? model.GetType().Name : "null") + " force=" + force);
                if (!force) return;
                _redHeartArtActive = true;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                var t = new Godot.Timer(); t.WaitTime = 0.25; t.OneShot = true;
                t.Timeout += () =>
                {
                    try
                    {
                        if (__instance == null || !Godot.GodotObject.IsInstanceValid(__instance)) { ZLog.W("event art timer skipped (room freed)"); return; }
                        ApplyEventArt(__instance);
                    }
                    catch { }
                };
                tree.Root.CallDeferred(Godot.Node.MethodName.AddChild, t);
            }
            catch (Exception e) { ZLog.W("event ready fail " + e.Message); }
        }

        static void ApplyEventArt(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom room)
        {
            try
            {
                // 关键：本方法会由 0.25s 定时器回调（房间可能已释放），未校验就访问会原生崩溃
                if (room == null || !Godot.GodotObject.IsInstanceValid(room)) { ZLog.W("event art skipped (room freed)"); return; }
                var tex = Art.Get("event_red_heart");
                if (tex == null) { ZLog.W("event art missing"); return; }
                try { if (room.VfxContainer != null) room.VfxContainer.Visible = false; } catch { }
                int applied = 0, seen = 0;
                var stack = new Stack<Godot.Node>();
                stack.Push(room);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n == null || !Godot.GodotObject.IsInstanceValid(n)) continue;
                    foreach (var ch in n.GetChildren()) if (ch is Godot.Node cn) stack.Push(cn);
                    if (n is Godot.TextureRect tr && tr.Texture != null && tr.Size.X > 200)
                    {
                        seen++;
                        ZLog.W("event tex " + tr.Name + " size=" + tr.Size + " parent=" + tr.GetParent()?.Name);
                        tr.Texture = tex; applied++;
                        // 隐藏同一布局下原版事件的其他大图层（尖刺/背景特效）
                        try
                        {
                            var par = tr.GetParent();
                            if (par != null)
                                foreach (var sib in par.GetChildren())
                                {
                                    if (sib is Godot.CanvasItem ci && ci != tr)
                                    {
                                        var nm = ci.Name.ToString().ToLower();
                                        bool big = ci is Godot.Control cc && (cc.Size.X > 500 || cc.Size.Y > 400);
                                        if (big || nm.Contains("vfx") || nm.Contains("fx") || nm.Contains("bg") || nm.Contains("spike") || nm.Contains("particle"))
                                        {
                                            ci.Visible = false;
                                            ZLog.W("event art hide sibling " + ci.Name);
                                        }
                                    }
                                }
                        }
                        catch { }
                    }
                }
                // 隐藏原版场景的前景剪影 / 特效层（与 Visuals 周期路径共用同一实现）
                int hid = Visuals.HideVanillaEventDecor(room, true);
                ZLog.W("event art applied=" + applied + " candidates=" + seen + " hiddenScene=" + hid);
            }
            catch (Exception e) { ZLog.W("event art apply fail " + e.Message); }
        }

        public static bool EventPortraitPrefix(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)
        {
            try
            {
                if (__instance == null || _settingPortrait) return true;
                if (!_redHeartArtActive)
                {
                    try
                    {
                        var f = __instance.GetType().GetField("_event", BindingFlags.NonPublic | BindingFlags.Instance);
                        var model = f != null ? f.GetValue(__instance) as MegaCrit.Sts2.Core.Models.EventModel : null;
                        if (model != null && ShouldForceRedGodHeart(model)) _redHeartArtActive = true;
                    }
                    catch { }
                }
                if (!_redHeartArtActive) return true;
                var tex = Art.Get("event_red_heart");
                if (tex == null) return true;
                _settingPortrait = true;
                try { __instance.SetPortrait(tex); } finally { _settingPortrait = false; }
                ZLog.W("red heart event portrait applied");
                return false;
            }
            catch (Exception e) { ZLog.W("event portrait fail " + e.Message); return true; }
        }

        public static void PowerDescPostfix(MegaCrit.Sts2.Core.Models.PowerModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.GetType().Name != "DieForYouPower") return;
                __result = new MegaCrit.Sts2.Core.Localization.LocString("powers", "die_for_you.description");
            }
            catch { }
        }

        public static void CreatureNamePostfix(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance, ref string __result)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.Monster == null) return;
                if (__instance.Monster.GetType().Name != "Osty") return;
                var pl = __instance.Player;
                if (pl != null && pl.Character != null && pl.Character.Id.Entry == "ZHIJI") __result = "血肉";
            }
            catch { }
        }

        public static void CardEnchantIconPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var t = __instance.GetType();
                var modelProp = t.GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);
                var model = modelProp != null ? modelProp.GetValue(__instance) : null;
                object ench = null;
                if (model != null)
                {
                    var ep = model.GetType().GetProperty("Enchantment");
                    ench = ep != null ? ep.GetValue(model) : null;
                }
                bool ours = ench is UnboundRage;
                var field = t.GetField("_enchantmentIcon", BindingFlags.NonPublic | BindingFlags.Instance);
                var icon = field != null ? field.GetValue(__instance) as Godot.TextureRect : null;
                if (icon == null) return;
                if (ours) { var tex = UnboundIcon(); if (tex != null) icon.Texture = tex; }
            }
            catch { }
        }

        public static void EnchantIconPostfix(object __instance, ref string __result)
        {
            try
            {
                if (__instance is UnboundRage)
                    __result = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("enchantments/sharp.png");
            }
            catch { }
        }

        public static bool ShouldForceHatch(MegaCrit.Sts2.Core.Models.EventModel ev)
        {
            try
            {
                if (!IsZhijiRun()) return false;
                if (ev != null)
                {
                    var tn = ev.GetType().FullName ?? "";
                    if (tn.Contains("Ancient") || tn.Contains("Neow")) return false;   // 不抢开局祝福事件
                }
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl == null) return false;
                if (pl.Relics != null) foreach (var r in pl.Relics) if (r is UnboundHeart) return false;
                if (ZhijiStore.HasOnce(pl, "hatch_seen")) return false;
                // 用缓存前先刷新，避免开局缓存为 true 后一直把之后的事件标题盖成「孵化完成」
                try { RefreshForceHatchCache(); } catch { }
                if (_forceHatchCached) return true;
                int actH = 0;
                try { actH = pl.RunState.CurrentActIndex; } catch { }
                return actH == 0;   // 红神的摇篮只在第一层出现一次
            }
            catch { }
            return false;
        }

        public static bool RedHeartOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (ShouldForceRedGodHeart(__instance)) { __result = RedGodHeartEvent.BuildOptions(__instance); return false; }
            }
            catch (Exception e) { ZLog.W("red heart options fail " + e.Message); }
            return true;
        }

        public static bool HatchForceOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance,
            ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (__instance != null && ShouldForceHatch(__instance))
                {
                    __result = HatchEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("hatch force fail " + e.Message); }
            return true;
        }

        public static bool DinerTitlePrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try
            {
                if (__instance != null)
                {
                    var fn = __instance.GetType().FullName;
                    if (!IsZhijiRun()) return true;
                    if (ShouldForceRedGodHeart(__instance)) { __result = RedGodHeartEvent.Title(); return false; }
                    if (ShouldForceHatch(__instance)) { __result = HatchEvent.Title(); return false; }
                    if (fn == DinerEvent.Hijacked) { __result = DinerEvent.Title(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == RedCradleEvent.Hijacked) { __result = RedCradleEvent.Title(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == RedChurchEvent.Hijacked) { __result = RedChurchEvent.Title(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == CrimsonAltarEvent.Hijacked) { __result = CrimsonAltarEvent.Title(); return false; }
                    if (fn == VileMarketEvent.Hijacked) { __result = VileMarketEvent.Title(); return false; }
                    if (fn == BloodGiftEvent.Hijacked) { __result = BloodGiftEvent.Title(); return false; }
                    if (fn == HungerTheaterEvent.Hijacked) { __result = HungerTheaterEvent.Title(); return false; }
                    if (fn == WarCouncilEvent.Hijacked) { __result = WarCouncilEvent.Title(); return false; }
                    if (fn == HarvestFestivalEvent.Hijacked) { __result = HarvestFestivalEvent.Title(); return false; }
                    if (ShouldForceHatch(__instance)) { __result = HatchEvent.Title(); return false; }
                }
            }
            catch { }
            return true;
        }

        public static bool DinerDescPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try
            {
                if (__instance != null)
                {
                    var fn = __instance.GetType().FullName;
                    if (!IsZhijiRun()) return true;
                    if (ShouldForceRedGodHeart(__instance)) { __result = RedGodHeartEvent.InitialDescription(); return false; }
                    if (ShouldForceHatch(__instance)) { __result = HatchEvent.InitialDescription(); return false; }
                    if (fn == DinerEvent.Hijacked) { __result = DinerEvent.InitialDescription(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == RedCradleEvent.Hijacked) { __result = RedCradleEvent.InitialDescription(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == RedChurchEvent.Hijacked) { __result = RedChurchEvent.InitialDescription(); return false; }
                    if (!IsZhijiRun()) return true;
                    if (fn == CrimsonAltarEvent.Hijacked) { __result = CrimsonAltarEvent.InitialDescription(); return false; }
                    if (fn == VileMarketEvent.Hijacked) { __result = VileMarketEvent.InitialDescription(); return false; }
                    if (fn == BloodGiftEvent.Hijacked) { __result = BloodGiftEvent.InitialDescription(); return false; }
                    if (fn == HungerTheaterEvent.Hijacked) { __result = HungerTheaterEvent.InitialDescription(); return false; }
                    if (fn == WarCouncilEvent.Hijacked) { __result = WarCouncilEvent.InitialDescription(); return false; }
                    if (fn == HarvestFestivalEvent.Hijacked) { __result = HarvestFestivalEvent.InitialDescription(); return false; }
                    if (ShouldForceHatch(__instance)) { __result = HatchEvent.InitialDescription(); return false; }
                }
            }
            catch { }
            return true;
        }

        public static bool HatchOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (ShouldForceRedGodHeart(__instance)) { __result = RedGodHeartEvent.BuildOptions(__instance); return false; }
                // 3.3.18：必须连同 ShouldForceHatch 一起判断。原来只看宿主类型，
                // 导致拿过失控之心 / 已孵化后，进这些宿主仍反复出现「孵化完成」事件（可重复领奖）。
                if (ShouldForceHatch(__instance))
                {
                    __result = HatchEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("hatch options fail " + e.Message); }
            return true;
        }

        public static bool FestivalOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == HarvestFestivalEvent.Hijacked)
                {
                    __result = HarvestFestivalEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("festival options fail " + e.Message); }
            return true;
        }

        public static bool CouncilOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == WarCouncilEvent.Hijacked)
                {
                    __result = WarCouncilEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("council options fail " + e.Message); }
            return true;
        }

        public static bool TheaterOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == HungerTheaterEvent.Hijacked)
                {
                    __result = HungerTheaterEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("theater options fail " + e.Message); }
            return true;
        }

        public static bool GiftOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == BloodGiftEvent.Hijacked)
                {
                    __result = BloodGiftEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("gift options fail " + e.Message); }
            return true;
        }

        public static bool MarketOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == VileMarketEvent.Hijacked)
                {
                    __result = VileMarketEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("market options fail " + e.Message); }
            return true;
        }

        public static bool AltarOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == CrimsonAltarEvent.Hijacked)
                {
                    __result = CrimsonAltarEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("altar options fail " + e.Message); }
            return true;
        }

        public static bool ChurchOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == RedChurchEvent.Hijacked)
                {
                    __result = RedChurchEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("church options fail " + e.Message); }
            return true;
        }

        public static bool CradleTitlePrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try { if (__instance != null && __instance.GetType().FullName == RedCradleEvent.Hijacked) { __result = RedCradleEvent.Title(); return false; } } catch { }
            return true;
        }

        public static bool CradleDescPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try { if (__instance != null && __instance.GetType().FullName == RedCradleEvent.Hijacked) { __result = RedCradleEvent.InitialDescription(); return false; } } catch { }
            return true;
        }

        public static bool CradleOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == RedCradleEvent.Hijacked)
                {
                    __result = RedCradleEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("cradle options fail " + e.Message); }
            return true;
        }

        public static bool DinerOptionsPrefix(MegaCrit.Sts2.Core.Models.EventModel __instance, ref System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption> __result)
        {
            try
            {
                try
                {
                    ZLog.W("diner options probe inst=" + (__instance != null ? __instance.GetType().FullName : "null")
                        + " zhijiRun=" + IsZhijiRun() + " opts=" + (__result != null ? __result.Count : -1));
                }
                catch { }
                if (!IsZhijiRun()) return true;
                if (__instance != null && __instance.GetType().FullName == DinerEvent.Hijacked)
                {
                    __result = DinerEvent.BuildOptions(__instance);
                    return false;
                }
            }
            catch (Exception e) { ZLog.W("diner options fail " + e.Message); }
            return true;
        }

        public static void CardDescPostfix(MegaCrit.Sts2.Core.Models.CardModel __instance, ref string __result)
        {
            try
            {
                if (!(__instance is ZhijiCard zcStart) || string.IsNullOrEmpty(__result)) return;
                __result = System.Text.RegularExpressions.Regex.Replace(__result, @"\{([A-Za-z_][A-Za-z0-9_]*)\}",
                    new System.Text.RegularExpressions.MatchEvaluator(m =>
                    {
                        try
                        {
                            var name = m.Groups[1].Value;
                            var v = __instance.DynamicVars[name];
                            var dv = v.BaseValue;
                            return (dv == System.Math.Floor(dv)) ? ((int)dv).ToString() : dv.ToString("0.#");
                        }
                        catch { return m.Value; }
                    }));
                if (zcStart.DotStyle && !__result.Contains("流血："))
                {
                    __result += "\n流血：每回合开始失去 1 层，并受到等于层数的伤害。";
                }
            }
            catch { }
        }

        public static void CardPlayPrefix(MegaCrit.Sts2.Core.Models.CardModel __instance)
        {
            try
            {
                if (__instance != null && __instance.Owner != null && __instance.Owner.Character != null
                    && __instance.Owner.Character.Id.Entry == "ZHIJI") Visuals.Attack();
            }
            catch { }
        }

        public static void GainBlockPrefix(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature)
        {
            try { if (IsOurPlayer(creature)) Visuals.Defend(); } catch { }
        }

        static decimal _lastStandUsed;

        public static void LoseHpPrefix(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance, ref decimal amount)
        {
            _lastStandUsed = 0;
            try
            {
                try
                {
                    if (IsOurPlayer(__instance))
                    {
                        var pSs = PlayerOf(__instance);
                        if (pSs != null && pSs.Relics != null && pSs.Relics.Any(r => r is SatedStomach) && amount > 0)
                        {
                            int turnS = -1;
                            try { turnS = pSs.PlayerCombatState != null ? pSs.PlayerCombatState.TurnNumber : -1; } catch { }
                            if (!ZhijiStore.Once(pSs, "sated_" + turnS))
                            {
                                int healS = (int)System.Math.Ceiling(amount * 0.5m);
                                if (healS > 0) { __instance.HealInternal(healS); ZLog.W("sated stomach heal " + healS); }
                            }
                        }
                    }
                }
                catch { }
                // 血肉/眷属也能闪避：判定在它掉血之前
                try
                {
                    var rmD2 = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                    var rsD2 = rmD2 != null ? rmD2.DebugOnlyGetState() : null;
                    if (rsD2 != null && rsD2.Players != null)
                    {
                        foreach (var plD2 in rsD2.Players)
                        {
                            if (plD2 == null || plD2.Osty == null || plD2.Osty != __instance) continue;
                            if (amount <= 0) continue;
                            int chanceD2 = 10;
                            try
                            {
                                if (plD2.Relics != null) foreach (var rr in plD2.Relics) if (rr is ShadowCloak) { chanceD2 += 5; break; }
                                if (plD2.Creature != null) chanceD2 += plD2.Creature.GetPowerAmount<DodgeChancePower>();
                            }
                            catch { }
                            if (chanceD2 > 60) chanceD2 = 60;
                            if (_autoRng.Next(100) < chanceD2)
                            {
                                amount = 0;
                                Visuals.Dodge();
                                ZLog.W("vassal dodge! chance=" + chanceD2);
                                return;
                            }
                        }
                    }
                }
                catch { }
                // 血肉死亡爆裂：骨镰/血肉死亡时对全体造成最大生命一半伤害
                try
                {
                    var rmE = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                    var rsE = rmE != null ? rmE.DebugOnlyGetState() : null;
                    if (rsE != null && rsE.Players != null)
                    {
                        foreach (var plE in rsE.Players)
                        {
                            if (plE == null || plE.Osty == null || plE.Osty != __instance) continue;
                            if (amount >= __instance.CurrentHp && __instance.CurrentHp > 0)
                            {
                                var stE = ZhijiStore.Of(plE);
                                if (!stE.MinionDeathExploded)
                                {
                                    stE.MinionDeathExploded = true;
                                    int boom = System.Math.Max(4, (int)__instance.MaxHp / 2);
                                    var csE2 = plE.Creature != null ? plE.Creature.CombatState : null;
                                    if (csE2 != null && csE2.Enemies != null)
                                        foreach (var en2 in csE2.Enemies)
                                            if (en2 != null && !en2.IsDead) en2.LoseHpInternal(boom, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                    ZLog.W("vassal death explosion=" + boom);
                                    try
                                    {
                                        int lo = plE.Creature != null ? plE.Creature.GetPowerAmount<LeftoverPower>() : 0;
                                        if (lo > 0)
                                        {
                                            plE.Creature.HealInternal(6);
                                            stE.AddFeast(3);
                                            ZLog.W("leftover use heal 6 feast+3");
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
                if (BypassStand)
                {
                    // 自伤（自燃/饥饿/反噬/大招代价）不直接致死：至少留 1 点血
                    try
                    {
                        if (IsOurPlayer(__instance) && amount > 0 && amount >= __instance.CurrentHp)
                        {
                            amount = System.Math.Max(0, (int)__instance.CurrentHp - 1);
                            ZLog.W("self dmg clamped to " + amount);
                        }
                    }
                    catch { }
                    return;          // 自流血/大招代价：不吃替身与格挡
                }
                if (!IsOurPlayer(__instance)) return;
                // 概率闪避：完全免除这次伤害（红神形态概率更高）
                try
                {
                    if (amount > 0)
                    {
                        var pD = PlayerOf(__instance);
                        var stD = pD != null ? ZhijiStore.Of(pD) : null;
                        int chance = 10;
                        try
                        {
                            if (pD.Relics != null) foreach (var rD in pD.Relics) if (rD is ShadowCloak) { chance += 5; break; }
                            if (pD.Creature != null) chance += pD.Creature.GetPowerAmount<DodgeChancePower>();
                        }
                        catch { }
                        if (chance > 60) chance = 60;
                        if (_autoRng.Next(100) < chance)
                        {
                            amount = 0;
                            Visuals.Dodge();
                            ZLog.W("dodge! chance=" + chance);
                            return;
                        }
                    }
                }
                catch { }
                Visuals.Hit();
                var p = PlayerOf(__instance);
                if (p == null) return;
                var st = ZhijiStore.Of(p);
                // 召唤物先挨打（嘲讽）：每次最多挡 8 点，有开饭铃则 12 点
                if (st.MinionHp > 0)
                {
                    int dmgM = (int)System.Math.Ceiling(amount);
                    int soakCap = 8;
                    try { if (p.Relics != null && p.Relics.Any(r => r is DinnerBell)) soakCap = 12; } catch { }
                    int useM = System.Math.Min(System.Math.Min(st.MinionHp, dmgM), soakCap);
                    if (useM > 0)
                    {
                        st.MinionHp -= useM;
                        amount -= useM;
                        ZLog.W("minion absorbed=" + useM + " left=" + st.MinionHp);
                        if (st.MinionHp <= 0)
                        {
                            ZLog.W("minion died");
                            // 食客死亡：残渣炸开，对全体敌人造成 10 点伤害
                            try
                            {
                                var csE = p.Creature != null ? p.Creature.CombatState : null;
                                if (csE != null && csE.Enemies != null)
                                    foreach (var ee in csE.Enemies)
                                        if (ee != null && !ee.IsDead) ee.LoseHpInternal(10, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                                ZLog.W("minion death explosion 10");
                            }
                            catch { }
                        }
                        ZhijiPowers.Sync(p);
                    }
                }
                // 第二个肉块同样可以承伤
                if (st.MinionHp2 > 0 && amount > 0)
                {
                    int dmgM2 = (int)System.Math.Ceiling(amount);
                    int soakCap2 = 8;
                    try { if (p.Relics != null && p.Relics.Any(r => r is DinnerBell)) soakCap2 = 12; } catch { }
                    int useM2 = System.Math.Min(System.Math.Min(st.MinionHp2, dmgM2), soakCap2);
                    if (useM2 > 0)
                    {
                        st.MinionHp2 -= useM2;
                        amount -= useM2;
                        ZLog.W("minion2 absorbed=" + useM2 + " left=" + st.MinionHp2);
                        if (st.MinionHp2 <= 0)
                        {
                            ZLog.W("minion2 died");
                            try
                            {
                                var csE2 = p.Creature != null ? p.Creature.CombatState : null;
                                if (csE2 != null && csE2.Enemies != null)
                                    foreach (var ee in csE2.Enemies)
                                        if (ee != null && !ee.IsDead) ee.LoseHpInternal(10, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                            }
                            catch { }
                        }
                        ZhijiPowers.Sync(p);
                    }
                }
                if (st.Stand <= 0) return;
                int dmg = (int)System.Math.Ceiling(amount);
                // 替身削弱：每次最多吸收伤害的一半（原来 1:1 全挡）
                int cap = (int)System.Math.Ceiling(dmg * 0.5);
                int use = System.Math.Min(st.Stand, cap);
                if (use <= 0) return;
                st.Stand -= use;
                amount -= use;
                int gain = use / 5;
                if (gain > 0) { st.AddPulse(10 * gain); st.AddHunger(1 * gain); }
                _lastStandUsed = use;
                ZLog.W("stand absorbed=" + use + " left=" + st.Stand);
                ZhijiPowers.Sync(p);
            }
            catch { }
        }

        public static void LoseHpPostfix(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance)
        {
            try
            {
                if (!IsOurPlayer(__instance)) return;
                var p = PlayerOf(__instance);
                if (p == null) return;
                var st = ZhijiStore.Of(p);
                if (_lastStandUsed > 0) { st.AddHunger(10); _lastStandUsed = 0; }
                st.AddPulse(3);
                ZhijiPowers.Sync(p);

            }
            catch { }
        }

        // ===== 黄金寿衣（3.3.3）：流血中的敌人无法回复生命 =====
        // 背景：流血已取消层数上限后，寿衣只剩「施加流血 ×1.5」，作为稀有遗物价值偏低；
        // 而 DOT 流最怕遇到会回血的敌人。寿衣改为：身上有流血的敌人，回复被完全阻止。

        // 找到带黄金寿衣的吱咿玩家（单人：通常只有一位）
        static Player ShroudOwnerOf(MegaCrit.Sts2.Core.Entities.Creatures.Creature c)
        {
            try
            {
                var cs = c != null ? c.CombatState : null;
                if (cs == null || cs.Players == null) return null;
                foreach (var p in cs.Players)
                {
                    if (p == null || p.Character == null || p.Character.Id.Entry != "ZHIJI") continue;
                    bool on = false;
                    try { on = ZhijiStore.Of(p).GoldShroud; } catch { }
                    if (!on)
                    {
                        try { on = p.Relics != null && p.Relics.Any(r => r is GoldenShroud); } catch { }
                    }
                    if (on) return p;
                }
            }
            catch { }
            return null;
        }

        public static void HealInternalPrefix(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance, ref decimal amount)
        {
            try
            {
                if (amount <= 0) return;
                if (__instance == null || __instance.IsDead) return;
                if (__instance.IsPlayer) return;          // 玩家自身回复不受限
                if (__instance.PetOwner != null) return;  // 自己的血肉/眷属不受限
                if (ShroudOwnerOf(__instance) == null) return;
                int bleed = 0;
                try { bleed = __instance.GetPowerAmount<BleedPower>(); } catch { }
                if (bleed <= 0) return;                   // 没流血的敌人正常回血
                ZLog.W("shroud blocks heal " + amount + " bleed=" + bleed);
                amount = 0;
            }
            catch { }
        }

        static readonly Random _redRng = new Random();

        // ===== 形态切换：用引擎原生 CardCmd.Transform 原位替换 =====
        static bool _transforming;
        static bool _winResolved;
        static bool _enemyBuffed;   // 本场战斗是否已强化敌人
        // 卡片"数值签名"：费用 + 所有动态数值 + 是否已升级
        static string CardSig(MegaCrit.Sts2.Core.Models.CardModel c)
        {
            var sb = new System.Text.StringBuilder();
            try { sb.Append("c=").Append(c.EnergyCost != null ? c.EnergyCost.GetResolved() : -1); } catch { }
            try { foreach (var kv in c.DynamicVars) sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value.BaseValue); } catch { }
            try { sb.Append("|up=").Append(c.IsUpgraded); } catch { }
            return sb.ToString();
        }

        static object _autoslayer;

        public static int EnemyBaseHpCount() { try { return _enemyBaseHp.Count; } catch { return -1; } }

        static readonly System.Collections.Generic.Dictionary<object, int> _enemyBaseHp
            = new System.Collections.Generic.Dictionary<object, int>();
        static bool _rebuilding;    // 正在重建红神手牌（期间禁止再替换）
        static int _redHandPending;
        static int _convertPending;
        static readonly System.Collections.Generic.HashSet<MegaCrit.Sts2.Core.Models.CardModel> _exhausting = new System.Collections.Generic.HashSet<MegaCrit.Sts2.Core.Models.CardModel>();  // 已排队的重建次数（防重复）   // 本场战斗是否已主动结算过胜利
        static int _poolBuildDepth;  // >0 表示正在构建卡池（此时不过滤，否则 ModelDb 取不到牌）   // 本场战斗是否已主动结算过胜利
        // 变身后"欠"玩家的幼年牌（entry 队列），回落时原样还回去
        static readonly System.Collections.Generic.List<string> _kidBacklog = new System.Collections.Generic.List<string>();
        // 红神牌实例 -> 它的原始幼年牌实例（还原时按原件 Transform，保升级）
        static readonly System.Collections.Generic.Dictionary<MegaCrit.Sts2.Core.Models.CardModel, MegaCrit.Sts2.Core.Models.CardModel> _redOrigin
            = new System.Collections.Generic.Dictionary<MegaCrit.Sts2.Core.Models.CardModel, MegaCrit.Sts2.Core.Models.CardModel>();
        // 正在等待"认领"的原件（Transform 会产生新实例，靠 FIFO 配对）
        // 延迟执行的转换任务（不在牌堆回调里直接 Transform，避免重入卡死）
        static readonly System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel[]> _todo
            = new System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel[]>();

        public static void QueueTransform(MegaCrit.Sts2.Core.Models.CardModel card, MegaCrit.Sts2.Core.Models.CardModel to)
        {
            if (card == null || to == null) return;
            _todo.Add(new[] { card, to });
        }

        /// 在安全时机统一执行排队的转换
        public static void DrainTransforms()
        {
            if (_todo.Count == 0) return;
            var batch = _todo.ToArray();
            _todo.Clear();
            int n = 0;
            foreach (var pair in batch)
            {
                try
                {
                    var card = pair[0]; var to = pair[1];
                    if (card == null || to == null) continue;
                    TransformCard(card, to);
                    n++;
                }
                catch (Exception e) { ZLog.W("drain fail " + e.Message); }
            }
            if (n > 0) ZLog.W("drain transforms=" + n);
        }

        static readonly System.Collections.Generic.Queue<MegaCrit.Sts2.Core.Models.CardModel> _pendingOrigin
            = new System.Collections.Generic.Queue<MegaCrit.Sts2.Core.Models.CardModel>();

        // 安全取模型：取不到就退回到暴食，绝不让异常冒到引擎
        static MegaCrit.Sts2.Core.Models.CardModel SafeCard<T>(MegaCrit.Sts2.Core.Models.CardModel fallback) where T : MegaCrit.Sts2.Core.Models.CardModel
        {
            try { var c = MegaCrit.Sts2.Core.Models.ModelDb.Card<T>(); if (c != null) return c; } catch { }
            return fallback;
        }

        static MegaCrit.Sts2.Core.Models.CardModel RedFor(MegaCrit.Sts2.Core.Models.CardModel kid)
        {
            var st = kid != null ? ZhijiStore.Of(kid.Owner) : null;
            int r = _redRng.Next(100);
            if (st != null && st.RForm == RedForm.War)
                return r < 60
                    ? SafeCard<Gluttony>(null)
                    : SafeCard<FleshRend>(SafeCard<Gluttony>(null));
            if (st != null && st.RForm == RedForm.Guard)
                return r < 55
                    ? SafeCard<TheMissing>(SafeCard<Gluttony>(null))
                    : SafeCard<FleshWall>(SafeCard<Gluttony>(null));
            // 均形态：三张混合
            if (r < 40) return SafeCard<Gluttony>(null);
            if (r < 70) return SafeCard<TheMissing>(SafeCard<Gluttony>(null));
            return SafeCard<BalanceBlade>(SafeCard<Gluttony>(null));
        }

        // 红神牌 → 对应幼年牌（可逆映射）
        static MegaCrit.Sts2.Core.Models.CardModel KidFor(MegaCrit.Sts2.Core.Models.CardModel red)
        {
            string e = red != null ? red.Id.Entry : "";
            if (e == "THE_MISSING") return MegaCrit.Sts2.Core.Models.ModelDb.Card<PulseAid>();
            if (e == "GLUTTONY") return MegaCrit.Sts2.Core.Models.ModelDb.Card<Drive>();
            return null;   // 收获等特殊牌不还原
        }

        /// 延后把手里幼年牌原位换成红神牌（Transform 是引擎原生接口，会同步处理节点）
        public static void ScheduleConvertHand(MegaCrit.Sts2.Core.Entities.Players.Player pl)
        {
            try
            {
                if (pl == null || _convertPending > 0) return;
                _convertPending++;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) { _convertPending--; return; }
                var t = tree.CreateTimer(0.20);
                t.Timeout += () =>
                {
                    try
                    {
                        if (ZhijiStore.Of(pl).Form == ZhijiForm.RedGod)
                        {
                            ConvertHandToRed(pl);   // 排队
                            DrainTransforms();      // 立刻执行
                            Visuals.RefreshHandLayout();
                        }
                    }
                    catch (Exception e) { ZLog.W("sched convert fail " + e.Message); }
                    finally { _convertPending--; }
                };
                ZLog.W("schedule convert in 0.2s");
            }
            catch (Exception e) { ZLog.W("schedule convert err " + e.Message); _convertPending = 0; }
        }

        /// 红神形态：只往手牌追加红神牌（不删除、不重建，避免 UI 幽灵/卡死）
        public static void AddRedCards(MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            try
            {
                if (pl == null) return;
                var st = pl.Creature != null ? pl.Creature.CombatState as MegaCrit.Sts2.Core.Combat.CombatState : null;
                if (st == null) return;
                for (int k = 0; k < count; k++)
                {
                    var m = RedFor(null);
                    if (m == null) continue;
                    var inst = st.CreateCard(m, pl);
                    if (inst == null) continue;
                    _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst,
                        MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                        MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                }
                ZLog.W("red cards added: " + count);
            }
            catch (Exception e) { ZLog.W("add red fail " + e.Message); }
        }

        /// 延后重建红神手牌：等引擎抽牌与 UI 同步完成后再执行，避免手牌卡住
        static int _lastRebuildTurn = -1;
        static bool _forceRedHand;
        static bool _redDrawing;

        public static void ResetTurnDedupe() { _lastRebuildTurn = -1; }

        /// 变身瞬间：强制重建红神手牌（跳过"本回合已重建"的去重）
        public static void ForceRedHand(MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            _forceRedHand = true;
            ScheduleRedHand(pl, count);
        }

        public static void ScheduleRedHand(MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            try
            {
                if (pl == null || _redHandPending > 0) return;
                int turn = -1;
                try { turn = pl.PlayerCombatState != null ? pl.PlayerCombatState.TurnNumber : -1; } catch { }
                if (turn >= 0 && turn == _lastRebuildTurn && !_forceRedHand)
                { ZLog.W("red hand already rebuilt turn " + turn); return; }
                _forceRedHand = false;
                _lastRebuildTurn = turn;
                _redHandPending++;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) { RebuildRedHand(pl, count); _redHandPending--; return; }
                var t = tree.CreateTimer(0.25);
                t.Timeout += () =>
                {
                    try
                    {
                        if (ZhijiStore.Of(pl).Form == ZhijiForm.RedGod) _ = RedDrawAsync(pl, count);
                    }
                    catch (Exception e) { ZLog.W("sched red hand fail " + e.Message); }
                    finally { _redHandPending--; }
                };
                ZLog.W("schedule red hand in 0.2s");
            }
            catch (Exception e) { ZLog.W("schedule fail " + e.Message); _redHandPending = 0; }
        }

        /// 红神手牌重建（async 版本）：弃掉幼年牌 → 补红神牌，全部 await，保证节点正确回收
        public static async System.Threading.Tasks.Task RebuildRedHandAsync(MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            if (pl == null) return;
            if (_rebuilding) { ZLog.W("rebuild skipped (busy)"); return; }
            try
            {
                if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance == null
                    || MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsOverOrEnding)
                { ZLog.W("rebuild skipped (combat idle/ending)"); return; }
            }
            catch { }
            _rebuilding = true;
            try
            {
                var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                int before = hand != null ? hand.Cards.Count : -1;

                if (hand != null)
                {
                    foreach (var c in hand.Cards.ToList())
                    {
                        if (c == null) continue;
                        var z = c as ZhijiCard;
                        if (z != null && z.RedGod)
                            await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(c,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                        else
                            await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(c,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false);
                    }
                }

                var st = pl.Creature != null ? pl.Creature.CombatState as MegaCrit.Sts2.Core.Combat.CombatState : null;
                if (st != null)
                {
                    for (int k = 0; k < count; k++)
                    {
                        var m = RedFor(null);
                        if (m == null) continue;
                        var inst = st.CreateCard(m, pl);
                        if (inst != null)
                        {
                            var res = await MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(inst,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, false);
                            if (!res.success) ZLog.W("add red failed: " + m.Id.Entry);
                        }
                    }
                }
                var h2 = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                string entries = "";
                if (h2 != null) foreach (var c2 in h2.Cards) if (c2 != null) entries += c2.Id.Entry + ",";
                ZLog.W("rebuild red hand: before=" + before + " -> " + count + " hand=[" + entries + "]");
                Visuals.RefreshHandLayout();
            }
            catch (Exception e) { ZLog.W("rebuild async fail " + e.Message); }
            finally { _rebuilding = false; }
        }

        public static void RebuildRedHand(MegaCrit.Sts2.Core.Entities.Players.Player pl, int count)
        {
            _ = RebuildRedHandAsync(pl, count);
        }

        public static void ConvertHandToRed(MegaCrit.Sts2.Core.Entities.Players.Player pl)
        {
            try
            {
                if (pl == null) return;
                var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                if (hand == null) return;
                int n = 0;
                foreach (var c in hand.Cards.ToList())
                {
                    var z = c as ZhijiCard;
                    if (c == null || z == null || z.RedGod) continue;
                    _pendingOrigin.Enqueue(c);
                    QueueTransform(c, RedFor(c));
                    n++;
                }
                ZLog.W("hand converted to red: " + n);
            }
            catch (Exception e) { ZLog.W("convert hand fail " + e.Message); }
        }

        static void TransformCard(MegaCrit.Sts2.Core.Models.CardModel card, MegaCrit.Sts2.Core.Models.CardModel replacement)
        {
            if (card == null || replacement == null || _transforming) return;
            try
            {
                _transforming = true;
                var t = GameAsm() != null ? GameAsm().GetType("MegaCrit.Sts2.Core.Commands.CardCmd", false) : null;
                if (t == null) { ZLog.W("transform: no CardCmd"); return; }
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "Transform") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(card)) continue;
                    object style = ps[2].ParameterType.IsEnum ? System.Activator.CreateInstance(ps[2].ParameterType) : null;
                    var res = m.Invoke(null, new object[] { card, replacement, style });
                    ZLog.W("transform " + card.Id.Entry + " -> " + replacement.Id.Entry);
                    break;
                }
            }
            catch (Exception e) { ZLog.W("transform fail " + e.Message); }
            finally { _transforming = false; }
        }

        static object _hpInitRun;   // 记录已经做过"开局 72 血"的这次 run，避免每场战斗都重置

        static bool HasRelic(MegaCrit.Sts2.Core.Entities.Players.Player p, string entry)
        {
            try
            {
                if (p == null || p.Relics == null) return false;
                foreach (var r in p.Relics) if (r != null && r.Id.Entry == entry) return true;
            }
            catch { }
            return false;
        }

        static MegaCrit.Sts2.Core.Entities.Players.Player PlayerOf(MegaCrit.Sts2.Core.Entities.Creatures.Creature c)
        {
            try
            {
                if (c == null || c.CombatState == null || c.CombatState.Players == null) return null;
                foreach (var p in c.CombatState.Players) if (p != null && p.Creature == c) return p;
            }
            catch { }
            return null;
        }

        // 驱力进入手牌时，同步把一张「驱力·援护」也加入手牌
        public static void PileAddPostfix(MegaCrit.Sts2.Core.Entities.Cards.CardPile __instance, MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                if (__instance == null || card == null) return;
                // 红神临时牌永不进弃牌堆：落到弃牌堆就移出战斗
                // 只在"进入弃牌堆"时处理；已经进消耗堆的就不再处理（否则无限递归）
                if (__instance.Type == MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust) return;
                if (__instance.Type == MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard)
                {
                    var zrD = card as ZhijiCard;
                    if (zrD != null && zrD.RedGod && !_exhausting.Contains(card))
                    {
                        _exhausting.Add(card);
                        _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(card,
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust,
                                MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Bottom, null, false)
                            .ContinueWith(t => { _exhausting.Remove(card); });
                        ZLog.W("red card exhausted: " + card.Id.Entry);
                    }
                    return;
                }
                if (__instance.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand) return;
                var p = card.Owner;
                if (p == null || p.Character == null || p.Character.Id.Entry != "ZHIJI") return;
                // 红神牌进入手牌：认领它对应的原件
                var zrNew = card as ZhijiCard;
                if (zrNew != null && zrNew.RedGod && _pendingOrigin.Count > 0)
                {
                    var origin = _pendingOrigin.Dequeue();
                    if (origin != null) { _redOrigin[card] = origin; ZLog.W("map " + card.Id.Entry + " <- " + origin.Id.Entry); }
                }
                var zc = card as ZhijiCard;
                var zstate = ZhijiStore.Of(p);
                // 红神手牌统一由回合开始重建（RebuildRedHand），这里不再逐张替换
            }
            catch (Exception e) { ZLog.W("pair hook fail " + e.Message); }
        }

        // 任意原因离开手牌（打出/弃掉/消耗）→ 移除配对
        public static void PileRemovePostfix(MegaCrit.Sts2.Core.Entities.Cards.CardPile __instance, MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                if (__instance == null || card == null) return;
                if (__instance.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand) return;
                if (card.Owner == null) return;
                Twin.OnLeftHand(card.Owner, card);
            }
            catch (Exception e) { ZLog.W("pair remove hook fail " + e.Message); }
        }

        // 悬停节点级别放行：让我们自己的角色节点也能被指
        public static bool AllowedTargetNodePrefix(MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager __instance,
            Godot.Node node, ref bool __result)
        {
            try
            {
                if (_dragCard == null || node == null) return true;
                if (!(_dragCard is ZhijiCard)) return true;
                var entry = _dragCard.Id.Entry;
                if (entry != "DRIVE" && entry != "ESCAPE" && entry != "PULSE_AID") return true;
                var creatureNode = node as MegaCrit.Sts2.Core.Nodes.Combat.NCreature;
                if (creatureNode != null)
                {
                    var cr = creatureNode.Entity;
                    if (cr != null && cr.IsPlayer)
                    {
                        ZLog.W("target node allow self for " + entry);
                        __result = true;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }

        // 松手时的第二道校验：让我们的双用牌可以把自己当目标
        public static bool ProgressEpochSkipPrefix(MegaCrit.Sts2.Core.Entities.Players.Player localPlayer)
        {
            try
            {
                if (localPlayer != null && localPlayer.Character != null && localPlayer.Character.Id.Entry == "ZHIJI")
                {
                    ZLog.W("skip vanilla epoch check for ZHIJI");
                    return false;
                }
            }
            catch { }
            return true;
        }

        public static bool ProgressCombatWonPrefix(MegaCrit.Sts2.Core.Entities.Players.Player localPlayer)
        {
            try
            {
                if (localPlayer != null && localPlayer.Character != null && localPlayer.Character.Id.Entry == "ZHIJI")
                {
                    ZLog.W("skip ProgressSaveManager.UpdateAfterCombatWon for ZHIJI");
                    return false;
                }
            }
            catch { }
            return true;
        }

        // 引擎级"一牌两用"：记录当前拖拽的牌，允许指向自己的同时仍可指敌人
        static MegaCrit.Sts2.Core.Models.CardModel _dragCard;

        public static void TargetStartPrefix(MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager __instance, object __1)
        {
            _dragCard = null;
            try
            {
                var holder = __1 as MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder;
                if (holder == null) return;
                var node = holder.CardNode;
                if (node == null || node.Model == null) return;
                _dragCard = node.Model;
            }
            catch { }
        }

        public static bool AllowedTargetPrefix(MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager __instance,
            MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, ref bool __result)
        {
            try
            {
                if (_dragCard == null) return true;
                if (!(_dragCard is ZhijiCard)) return true;
                var entry = _dragCard.Id.Entry;
                if (entry != "DRIVE" && entry != "PULSE_AID" && entry != "ESCAPE") return true;
                if (creature == null) return true;
                // 允许把敌人或自己当目标
                if (!creature.IsDead)
                {
                    __result = true;
                    return false;
                }
            }
            catch { }
            return true;
        }

        public static void ExhaustPrefix(MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                if (card == null || card.Owner == null) return;
                if (card.Owner.Character == null || card.Owner.Character.Id.Entry != "ZHIJI") return;
                var z = card as ZhijiCard;
                if (z != null && z.RedGod) return;
                ZhijiStore.Of(card.Owner).AddHunger(2);
                ZhijiPowers.Sync(card.Owner);
            }
            catch { }
        }

        static void TryPatchPrefixEx(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { ZLog.W("no method " + methodName); return; }
                var pre = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                var post = typeof(ZhijiPatcher).GetMethod("LoseHpPostfix", BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, prefix: new HarmonyMethod(pre), postfix: new HarmonyMethod(post));
                ZLog.W("patched-ex " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("patch ex fail " + e.Message); }
        }

        static bool _eventsHooked;

        static bool _roomsHooked;
        public static void HookRoomEvents()
        {
            if (_roomsHooked) return;
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm == null) return;
                rm.RoomEntered += () =>
                {
                    try
                    {
                        var rsR = rm.DebugOnlyGetState();
                        var cur = rsR != null ? rsR.CurrentRoom : null;
                        ZLog.W("room entered: " + (cur != null ? cur.GetType().Name : "null"));
                        try { if (cur != null && cur.GetType().Name == "EventRoom") RefreshForceHatchCache(); } catch { }
                        try
                        {
                            if (cur != null && cur.GetType().Name == "MapRoom")
                            {
                                var plR2 = rsR != null && rsR.Players != null && rsR.Players.Count > 0 ? rsR.Players[0] : null;
                                bool uh2 = false;
                                if (plR2 != null && plR2.Relics != null) foreach (var rr in plR2.Relics) if (rr is UnboundHeart) { uh2 = true; break; }
                                if (uh2) Visuals.ShowThought();
                            }
                        }
                        catch { }
                        try { Visuals.ForceSceneRefresh(); } catch { }
                    }
                    catch { }
                };
                _roomsHooked = true;
                ZLog.W("room events hooked");
            }
            catch (Exception e) { ZLog.W("hook rooms fail " + e.Message); }
        }

        static void HookCombatEvents()
        {
            if (_eventsHooked) return;
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null) return;
                cm.CombatWon += _ => { try { Visuals.HideOverlay(); ZLog.W("combat won -> hide"); } catch { } };
                cm.CombatEnded += _ => { try { Visuals.HideOverlay(); ZLog.W("combat ended -> hide"); } catch { } };
                _eventsHooked = true;
                ZLog.W("combat events hooked");
                CopyEngineLog("start");
            }
            catch (Exception e) { ZLog.W("hook combat events fail " + e.Message); }
        }

        public static void TransformCutscene()
        {
            try
            {
                try { MegaCrit.Sts2.Core.Nodes.NGame.Instance?.ScreenShake(0, 0, 90f); } catch { }
                ZLog.W("transform cutscene");
            }
            catch (Exception e) { ZLog.W("cutscene fail " + e.Message); }
        }

        static bool _logTimerStarted;

        // 每 5 秒把引擎日志同步到外部存储，卡死时也能拿到最后一段
        public static void TryPatchMapMarker()
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapMarker", false) : null;
                if (t == null) { ZLog.W("map marker: no type"); return; }
                var m = t.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { ZLog.W("map marker: no method"); return; }
                var hook = typeof(CharPatches).GetMethod("MapMarkerAfter", BindingFlags.Public | BindingFlags.Static);
                var h = new Harmony("zhiji.mapmarker");
                h.Patch(m, postfix: new HarmonyMethod(hook));
                ZLog.W("map marker patched");
            }
            catch (Exception e) { ZLog.W("map marker patch fail " + e.Message); }
        }

        public static void TryEnableMenuDebugPatch()
        {
            try
            {
                // 始终启用：避免"无存档+已解锁纪元"时单人模式被时间线按钮顶掉（原版逻辑）
                var asm = GameAsm();
                var t = asm != null ? asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu", false) : null;
                if (t == null) { ZLog.W("menu patch: no type"); return; }
                var m = t.GetMethod("UpdateTimelineButtonBehavior", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { ZLog.W("menu patch: no method"); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(nameof(MainMenuAfter), BindingFlags.Public | BindingFlags.Static);
                var h = new Harmony("zhiji.menudebug");
                h.Patch(m, postfix: new HarmonyMethod(hook));
                ZLog.W("menu patch: installed");
            }
            catch (Exception e) { ZLog.W("menu patch fail " + e.Message); }
        }

        // 调试：主菜单在"无存档且已解锁纪元"时会禁用单人模式，测试时强制打开
        public static void MainMenuAfter(object __instance)
        {
            try
            {
                var t = __instance != null ? __instance.GetType() : null;
                if (t == null) return;
                foreach (var name in new[] { "_singleplayerButton", "_multiplayerButton", "_compendiumButton" })
                {
                    var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var btn = f != null ? f.GetValue(__instance) : null;
                    if (btn == null) continue;
                    var m = btn.GetType().GetMethod("Enable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (m != null) { m.Invoke(btn, null); ZLog.W("debug: menu enable " + name); }
                }
            }
            catch (Exception e) { ZLog.W("menu enable fail " + e.Message); }
        }

        /// 按秒等待（用 SceneTreeTimer，跨低帧率也准确）
        public static System.Threading.Tasks.Task WaitSec(float sec)
        {
            try
            {
                // 演出跳过：按住空格/Esc 时直接略过等待
                try
                {
                    if (Godot.Input.IsKeyPressed(Godot.Key.Space) || Godot.Input.IsKeyPressed(Godot.Key.Escape))
                        return System.Threading.Tasks.Task.CompletedTask;
                }
                catch { }
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return System.Threading.Tasks.Task.CompletedTask;
                var timer = tree.CreateTimer(sec);
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                timer.Timeout += () => { try { tcs.TrySetResult(true); } catch { } };
                return tcs.Task;
            }
            catch { return System.Threading.Tasks.Task.CompletedTask; }
        }

        static bool SpecialCard(MegaCrit.Sts2.Core.Models.CardModel c)
            => c != null && (c is Harvest);

        // 童年的终结：只要离开手牌（进弃牌/消耗），就从这一局里移除，不再被摸到
        static void VanishEndCard(MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) { _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(card, true); return; }
                var timer = tree.CreateTimer(0.05);
                timer.Timeout += () =>
                {
                    try
                    {
                        _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(card, true);
                        HideCardVisuals(card);
                        ZLog.W("childhood end left hand -> removed from run");
                    }
                    catch (Exception e2) { ZLog.W("remove end card fail " + e2.Message); }
                };
            }
            catch (Exception e) { ZLog.W("vanish end card fail " + e.Message); }
        }

        // 卡被移出战斗后，把残留的 NCard 视觉节点隐藏/回收，避免卡牌卡在屏幕上
        public static void HideCardVisuals(MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                if (card == null) return;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
                if (room == null || !Godot.GodotObject.IsInstanceValid(room)) return;
                int hidden = 0;
                var stack = new System.Collections.Generic.Stack<Godot.Node>();
                stack.Push(room);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n == null) continue;
                    if (n.GetType().Name == "NCard")
                    {
                        try
                        {
                            var c = n.GetType().GetProperty("Card")?.GetValue(n);
                            if (ReferenceEquals(c, card))
                            {
                                var ci = n as Godot.CanvasItem;
                                if (ci != null) ci.Visible = false;
                                n.QueueFree();
                                hidden++;
                            }
                        }
                        catch { }
                    }
                    foreach (var ch in n.GetChildren()) stack.Push(ch);
                }
                ZLog.W("hid card visuals=" + hidden);
            }
            catch (Exception e) { ZLog.W("hide card visuals fail " + e.GetBaseException().Message); }
        }

        // 引擎真正的入堆原语：入堆后把特殊牌挪去消耗堆（延迟一帧，避免改到正在遍历的列表）
        public static void PileAddInternalPostfix(MegaCrit.Sts2.Core.Entities.Cards.CardPile __instance, MegaCrit.Sts2.Core.Models.CardModel card)
        {
            try
            {
                if (__instance == null || card == null) return;
                var t = __instance.Type;
                if (card is ChildhoodEnd)
                {
                    if (t == MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard || t == MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust)
                    {
                        ZLog.W("special card CHILDHOOD_END landed in " + t + " -> removed from run");
                        VanishEndCard(card);
                    }
                    return;
                }
                if (!SpecialCard(card)) return;
                if (t != MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard
                    && t != MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw
                    && t != MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck) return;
                ZLog.W("special card " + card.Id.Entry + " landed in " + t + " -> moving to Exhaust");
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                var timer = tree.CreateTimer(0.05);
                timer.Timeout += () =>
                {
                    try
                    {
                        _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(card,
                            MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust,
                            MegaCrit.Sts2.Core.Entities.Cards.CardPilePosition.Top, null, true);
                    }
                    catch (Exception e2) { ZLog.W("move to exhaust fail " + e2.Message); }
                };
            }
            catch (Exception e) { ZLog.W("pile postfix fail " + e.Message); }
        }

        // 特殊牌（童年的终结 / 收获）：永远不进弃牌堆/抽牌堆，避免被反复摸到
        public static void PileAddPrefix(MegaCrit.Sts2.Core.Models.CardModel card, ref MegaCrit.Sts2.Core.Entities.Cards.PileType newPileType)
        {
            try
            {
                if (card == null) return;
                if (!(card is ChildhoodEnd) && !(card is Harvest)) return;
                if (newPileType == MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard
                    || newPileType == MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw
                    || newPileType == MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                {
                    var from = newPileType;
                    newPileType = MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust;
                    ZLog.W("special card " + card.Id.Entry + " redirected " + from + " -> Exhaust");
                }
            }
            catch { }
        }

        static void TryPatchVoidPrefix(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) { ZLog.W("vp no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { ZLog.W("vp no method " + methodName); return; }
                var pre = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, prefix: new HarmonyMethod(pre));
                ZLog.W("patched-vp " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("vp fail " + e.Message); }
        }

        static object ArchDialogue(object inst)
        {
            var t = inst.GetType();
            var dp = t.GetProperty("Dialogue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return dp != null ? dp.GetValue(inst) : null;
        }

        static void SetArchDialogue(object inst, object val)
        {
            var t = inst.GetType();
            var dp = t.GetProperty("Dialogue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var sp = dp != null ? dp.GetSetMethod(true) : null;
            if (sp != null) sp.Invoke(inst, new[] { val });
        }

        static object AnyDialogue(object inst)
        {
            try
            {
                var t = inst.GetType();
                var dsp = t.GetProperty("DialogueSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var set = dsp != null ? dsp.GetValue(inst) : null;
                if (set == null) return null;
                var m = set.GetType().GetMethod("GetAllDialogues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var all = m != null ? m.Invoke(set, null) as System.Collections.IEnumerable : null;
                if (all == null) return null;
                foreach (var d in all) { if (d != null) return d; }
            }
            catch (Exception e) { ZLog.W("any dialogue fail " + e.Message); }
            return null;
        }

        public static void ArchitectDialoguePostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                if (ArchDialogue(__instance) != null) return;
                var d = AnyDialogue(__instance);
                if (d == null) { ZLog.W("architect: no fallback dialogue"); return; }
                SetArchDialogue(__instance, d);
                ZLog.W("architect: fallback dialogue applied");
            }
            catch (Exception e) { ZLog.W("architect dialogue fail " + e.Message); }
        }

        static bool _victoryShown;

        // 3.3.19：WinRun 是返回 Task 的 async 方法。旧实现兜底分支 return false 跳过原方法，
        // 会让该方法返回 null Task -> 调用方 await null 抛 NRE -> 第三层通关结算卡死。
        // 现在：兜底时给一个已完成的 Task，并手动走与战败对称的胜利结算链路。
        public static bool ArchitectWinRunPrefix(object __instance, ref System.Threading.Tasks.Task __result)
        {
            try
            {
                if (__instance == null) return true;
                if (ArchDialogue(__instance) != null) { ZLog.W("architect winrun: dialogue ok"); return true; }
                var d = AnyDialogue(__instance);
                if (d != null)
                {
                    SetArchDialogue(__instance, d);
                    ZLog.W("architect winrun: dialogue fixed from set");
                    return true;
                }
                ZLog.W("architect winrun: no dialogue -> force victory settle");
                __result = System.Threading.Tasks.Task.CompletedTask;   // 绝不能返回 null Task
                ShowVictoryScreen("architect");
                return false;
            }
            catch (Exception e) { ZLog.W("architect winrun fail " + e.GetBaseException().Message); }
            return true;
        }

        // 胜利结算：与 ShowDefeatScreen 对称（OnEnded(true) -> NGameOverScreen -> OverlayStack.Push）
        // 3.3.19：给召唤物加成 -> 累计次数，并给手牌里的「万灵盛宴」减 1 费（最低 1）
        public static void RewardSummonBuff(Player pl)
        {
            try
            {
                if (pl == null) return;
                var st = ZhijiStore.Of(pl);
                st.SummonBuff++;
                var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                int n = 0;
                if (hand != null && hand.Cards != null)
                {
                    foreach (var c in hand.Cards)
                    {
                        if (!(c is FeastFinale)) continue;
                        var ec = c.EnergyCost;
                        if (ec == null) continue;
                        int before = ec.GetResolved();
                        if (before <= 1) continue;
                        ec.UpgradeBy(-1);
                        try { ec.FinalizeUpgrade(); } catch { }
                        if (ec.GetResolved() < before) n++;
                    }
                }
                ZLog.W("summon buff #" + st.SummonBuff + " -> feast finale -" + n + " cost");
            }
            catch (Exception e) { ZLog.W("summon buff fail " + e.Message); }
        }

        public static bool ShowVictoryScreen(string tag)
        {
            if (_victoryShown) { ZLog.W("victory: already shown"); return true; }
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rm == null || rs == null) { ZLog.W("victory: no run state"); return false; }
                var serialized = rm.OnEnded(true);
                ZLog.W("victory(" + tag + "): OnEnded(true) -> " + (serialized != null ? "ok" : "null") + " gameOver=" + rm.IsGameOver);
                var screen = MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen.Create(rs, serialized);
                var stack = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance;
                if (screen == null || stack == null) { ZLog.W("victory: screen/stack null"); return false; }
                stack.Push(screen);
                _victoryShown = true;
                ZLog.W("victory: screen pushed");
                return true;
            }
            catch (Exception e) { ZLog.W("victory: push fail " + e.GetBaseException().Message); return false; }
        }

        public static void StartEngineLogTimer()
        {
            if (_logTimerStarted) return;
            _logTimerStarted = true;
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) { ZLog.W("no tree for log timer"); return; }
                var t = new Godot.Timer();
                t.WaitTime = 60.0;
                t.Autostart = true;
                t.Timeout += () =>
                {
                    bool dbgLog = false;
                    try { dbgLog = Godot.FileAccess.FileExists(Godot.ProjectSettings.GlobalizePath("user://zhiji_debug")); } catch { }
                    if (!dbgLog) return;
                    CopyEngineLog("live");
                    try
                    {
                        var p1 = Godot.ProjectSettings.GlobalizePath("user://autoslay.log");
                        if (System.IO.File.Exists(p1))
                        {
                            try { System.IO.File.Copy(p1, "/storage/emulated/0/Android/data/com.megacrit.sts2re/files/autoslay.log", true); } catch { }
                            try { System.IO.File.Copy(p1, "/storage/emulated/0/Download/autoslay.log", true); } catch { }
                        }
                    }
                    catch { }
                };
                // 启动阶段场景树正在构建子节点，直接 AddChild 会失败（Parent node is busy setting up children）
                tree.Root.CallDeferred(Godot.Node.MethodName.AddChild, t);
                ZLog.W("engine log timer started (deferred)");
            }
            catch (Exception e) { ZLog.W("log timer fail " + e.Message); }
        }

        // 复制游戏引擎日志到外部存储（排查卡死用）
        public static void CopyEngineLog(string tag)
        {
            try
            {
                string[] cands = {
                    Godot.ProjectSettings.GlobalizePath("user://logs/sts2.log"),
                    "/data/data/com.megacrit.sts2re/files/logs/sts2.log",
                    "/data/user/0/com.megacrit.sts2re/files/logs/sts2.log"
                };
                foreach (var src in cands)
                {
                    if (!System.IO.File.Exists(src)) continue;
                    var dst = "/storage/emulated/0/Download/engine_" + tag + ".log";
                    System.IO.File.Copy(src, dst, true);
                    ZLog.W("engine log copied -> " + dst);
                    return;
                }
                ZLog.W("engine log not found");
            }
            catch (Exception e) { ZLog.W("copy engine log fail " + e.Message); }
        }

        // ===== 死亡结算（3.3.2 重做）=====
        // 旧实现的问题：三处（回合开始 / tick / 视觉 tick）各自 `_ = HandlePlayerDeath(...)`，
        // 紧接着又用反射去乱调 LoseCombat，async 竞态 + 早于 pending-loss 登记，
        // 结果 HP=0 时仍停在战斗界面（待办 #1）。
        // 新实现：统一入口 → await HandlePlayerDeath（带超时兜底）→ 下一帧在主线程调 LoseCombat()。
        static bool _deathLosePending;
        static double _deathLoseDueMsec;
        static bool _defeatScreenShown;
        static string _deathTag = "death";

        // 当前战斗房间（PendingLoss 需要）
        static MegaCrit.Sts2.Core.Rooms.CombatRoom CurrentCombatRoomDebug()
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null) return null;
                return rs.CurrentRoom as MegaCrit.Sts2.Core.Rooms.CombatRoom;
            }
            catch { return null; }
        }

        // 等价引擎的死亡登记：把「本场要输」记到 CombatTurnState.PendingLoss
        // 注意：CombatManager.LiveTurnStateFor / PendingLossState 都是 internal，只能反射。
        static object LiveTurnState(object cm)
        {
            try
            {
                var mi = cm.GetType().GetMethod("LiveTurnStateFor",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi == null) return null;
                var idProp = cm.GetType().GetProperty("CurrentCombatId",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var id = idProp != null ? idProp.GetValue(cm) : null;
                return mi.Invoke(cm, new object[] { id });
            }
            catch (Exception e) { ZLog.W("turn state fail " + e.GetBaseException().Message); return null; }
        }

        static bool MarkPendingLoss(object cm, object turnState)
        {
            try
            {
                if (cm == null || turnState == null) return false;
                var prop = turnState.GetType().GetProperty("PendingLoss",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop == null) { ZLog.W("pending: no PendingLoss prop"); return false; }
                if (prop.GetValue(turnState) != null) return true;   // 已登记
                var room = CurrentCombatRoomDebug();
                if (room == null) { ZLog.W("pending: no combat room"); return false; }
                var inst = Activator.CreateInstance(prop.PropertyType, new object[] { room });
                prop.SetValue(turnState, inst);
                return true;
            }
            catch (Exception e) { ZLog.W("pending fail " + e.GetBaseException().Message); return false; }
        }

        static bool ProcessPendingLoss(object cm, object turnState)
        {
            try
            {
                var mi = cm.GetType().GetMethod("ProcessPendingLoss",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi == null) { ZLog.W("process: method not found"); return false; }
                mi.Invoke(cm, new object[] { turnState });
                return true;
            }
            catch (Exception e) { ZLog.W("process fail " + e.GetBaseException().Message); return false; }
        }

        // 兜底入口：tick 里发现玩家已死但引擎没结算时调用（幂等）
        public static void EnsureDeathSettled(Player player, string tag)
        {
            try
            {
                if (player == null || player.Creature == null) return;
                if (!player.Creature.IsDead && player.Creature.CurrentHp > 0) return;
                var st = ZhijiStore.Of(player);
                if (st.DeathHandled) return;
                st.DeathHandled = true;
                st.AutoPlayPending = false;
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null || !cm.IsInProgress)
                {
                    ZLog.W(tag + ": death, no live combat (settle skipped)");
                    return;
                }
                ZLog.W(tag + ": death settle start hp=" + player.Creature.CurrentHp
                    + " aboutToLose=" + cm.IsAboutToLose + " ending=" + cm.IsEnding);
                // 死亡表现：收起立绘、藏掉原版角色、改显示「肉堆」死亡贴图
                try
                {
                    bool red = st.Form == ZhijiForm.RedGod;
                    Visuals.HideOverlay();
                    Visuals.HidePlayerCreature(player);
                    Visuals.ShowDeathSprite(red);
                }
                catch (Exception ev) { ZLog.W(tag + ": death visuals fail " + ev.Message); }
                _deathTag = tag;
                _ = SettleDeathAsync(cm, cm.CurrentCombatId, player, tag);
            }
            catch (Exception e) { ZLog.W(tag + ": death settle fail " + e.Message); }
        }

        static async System.Threading.Tasks.Task SettleDeathAsync(
            MegaCrit.Sts2.Core.Combat.CombatManager cm,
            MegaCrit.Sts2.Core.Combat.CombatId? id,
            Player player, string tag)
        {
            bool done = false;
            try
            {
                var death = cm.HandlePlayerDeath(id, player);
                // HandlePlayerDeath 会等回合循环，循环停摆时可能永不完成：超时后照样继续收尾
                var timeout = System.Threading.Tasks.Task.Delay(2000);
                done = await System.Threading.Tasks.Task.WhenAny(death, timeout) == death;
                if (done) { try { await death; } catch (Exception ed) { ZLog.W(tag + ": HandlePlayerDeath threw " + ed.GetBaseException().Message); } }
            }
            catch (Exception e) { ZLog.W(tag + ": settle async fail " + e.GetBaseException().Message); }
            try
            {
                ZLog.W(tag + ": HandlePlayerDeath done=" + done + " aboutToLose=" + cm.IsAboutToLose
                    + " inProgress=" + cm.IsInProgress + " ending=" + cm.IsEnding);
            }
            catch { }
            // 交给主线程下一帧执行：先给引擎 0.25s 自己处理 pending loss，避免重复结算
            _deathLoseDueMsec = Godot.Time.GetTicksMsec() + 50;
            _deathLosePending = true;
        }

        // 由 _Process 在主线程调用（每帧）
        public static void FlushDeathLose()
        {
            if (!_deathLosePending) return;
            try
            {
                if (Godot.Time.GetTicksMsec() < _deathLoseDueMsec) return;
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                string tag = _deathTag ?? "death";
                if (cm == null) { _deathLosePending = false; ZLog.W(tag + ": flush lose skipped (no manager)"); return; }
                if (!cm.IsInProgress) { _deathLosePending = false; ZLog.W(tag + ": flush lose skipped (combat ended)"); return; }
                if (cm.IsEnding) { _deathLosePending = false; ZLog.W(tag + ": flush lose skipped (engine already ending)"); return; }
                _deathLosePending = false;
                // 引擎链路：PendingLoss 登记后由 ProcessPendingLoss 结束战斗
                var ts = LiveTurnState(cm);
                if (ts != null && MarkPendingLoss(cm, ts))
                {
                    bool ok = ProcessPendingLoss(cm, ts);
                    ZLog.W(tag + ": flush ProcessPendingLoss ok=" + ok + " inProgress=" + cm.IsInProgress);
                }
                // 战斗界面已收尾，再弹战败结算
                ShowDefeatScreen();
            }
            catch (Exception e)
            {
                _deathLosePending = false;
                ZLog.W((_deathTag ?? "death") + ": flush lose fail " + e.GetBaseException().Message);
            }
        }

        // 战败界面：RunManager.OnEnded(false) -> NGameOverScreen.Create -> NOverlayStack.Push
        static bool ShowDefeatScreen()
        {
            if (_defeatScreenShown) return true;
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rm == null || rs == null) { ZLog.W("defeat: no run state"); return false; }
                var serialized = rm.OnEnded(false);
                ZLog.W("defeat: OnEnded(false) -> " + (serialized != null ? "ok" : "null") + " gameOver=" + rm.IsGameOver);
                var screen = MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen.Create(rs, serialized);
                var stack = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance;
                if (screen == null || stack == null) { ZLog.W("defeat: screen/stack null"); return false; }
                stack.Push(screen);
                _defeatScreenShown = true;
                ZLog.W("defeat: game over screen pushed");
                return true;
            }
            catch (Exception e) { ZLog.W("defeat: push fail " + e.GetBaseException().Message); return false; }
        }

        // tick 兜底：扫描玩家血量为 0 但引擎还没结算的情况
        public static void TickDeathSettle()
        {
            try
            {
                FlushDeathLose();
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null || !cm.IsInProgress) return;
                var cs = cm.DebugOnlyGetState();
                if (cs == null || cs.Players == null) return;
                foreach (var pl in cs.Players)
                {
                    if (pl == null || pl.Creature == null) continue;
                    if (!pl.Creature.IsDead && pl.Creature.CurrentHp > 0) continue;
                    EnsureDeathSettled(pl, "tick");
                }
            }
            catch (Exception e) { ZLog.W("tick death settle fail " + e.Message); }
        }

        public static void CombatEndPrefix()
        {
            try
            {
                Visuals.HideOverlay();
                if (PlayerDeathHandled())
                {
                    // 死亡结算：保持原版角色隐藏，死亡贴图留在屏幕上（战败界面背景）
                    var plD = CurrentPlayer();
                    if (plD != null)
                    {
                        try { Visuals.HidePlayerCreature(plD); } catch { }
                        try { Visuals.ShowDeathSprite(ZhijiStore.Of(plD).Form == ZhijiForm.RedGod); } catch { }
                    }
                    ZLog.W("combat end: keep death sprite");
                }
                else
                {
                    Visuals.RestorePlayerCreature();
                }
            }
            catch (Exception e) { ZLog.W("combat end hide fail " + e.Message); }
        }

        // 结算/战败界面被推入时回调
        public static void OverlayPushPostfix(MegaCrit.Sts2.Core.Nodes.Screens.Overlays.IOverlayScreen screen)
        {
            try
            {
                if (screen == null) return;
                string n = screen.GetType().Name;
                if (!n.Contains("GameOver")) return;
                ZLog.W("game over screen pushed -> hide self-drawn");
                Visuals.HideAllSelfDrawn("game over");
            }
            catch (Exception e) { ZLog.W("overlay push postfix fail " + e.Message); }
        }

        // 当前玩家
        static Player CurrentPlayer()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                if (cs != null && cs.Players != null && cs.Players.Count > 0) return cs.Players[0];
            }
            catch { }
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs != null && rs.Players != null && rs.Players.Count > 0) return rs.Players[0];
            }
            catch { }
            return null;
        }

        // 玩家是否已走死亡结算（用于视觉分支）
        static bool PlayerDeathHandled()
        {
            try
            {
                var pl = CurrentPlayer();
                if (pl == null) return false;
                return ZhijiStore.Of(pl).DeathHandled;
            }
            catch { return false; }
        }

        public static void CombatUiExitPostfix(object __instance)
        {
            try { Visuals.HideOverlay(); Visuals.RestorePlayerCreature(); Visuals.HideDeathSprite("ui exit"); Visuals.Despawn(); } catch (Exception e) { ZLog.W("ui exit fail " + e.Message); }
        }

        public static void CreateVisualsPostfix(object __instance, object __result)
        {
            try { CharPatches.AfterCreateVisuals(__instance, __result); }
            catch (Exception e) { ZLog.W("visuals postfix fail " + e.Message); }
        }

        public static bool LocTextPrefix(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
        {
            try
            {
                string v;
                if (__instance != null && Loc.TryGet(__instance.LocTable, __instance.LocEntryKey, out v))
                {
                    __result = v;
                    return false;
                }
                // 惰性补齐：引擎按需加载的表（如 enchantments）第一次查不到时，主动拉表并合并
                try
                {
                    if (__instance != null && !string.IsNullOrEmpty(__instance.LocTable))
                    {
                        var lm = MegaCrit.Sts2.Core.Localization.LocManager.Instance;
                        if (lm != null)
                        {
                            var lt = lm.GetTable(__instance.LocTable);
                            if (lt != null)
                            {
                                Loc.Ensure(__instance.LocTable, lt);
                                if (Loc.TryGet(__instance.LocTable, __instance.LocEntryKey, out v)) { __result = v; return false; }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
            return true;
        }

        public static bool LocRawPrefix(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
            => LocTextPrefix(__instance, ref __result);

        public static void SelectFinalizer(Exception __exception)
        {
            if (__exception != null) ZLog.W("EX in select: " + __exception.GetType().Name + ": " + __exception.Message + "\n" + __exception.StackTrace);
        }

        static void TryAddFinalizer(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                var t = asm != null ? asm.GetType(typeName, false) : null;
                if (t == null) return;
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m == null) return;
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, finalizer: new HarmonyMethod(hook));
                ZLog.W("patched-fin " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("finalizer fail " + typeName + "." + methodName + " " + e.Message); }
        }

        static void TryPatch(Harmony harmony, string typeName, string methodName, string hookName)
        {
            try
            {
                var asm = GameAsm();
                if (asm == null) { ZLog.W("no sts2 asm"); return; }
                var t = asm.GetType(typeName, false);
                if (t == null) { ZLog.W("no type " + typeName); return; }
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (m == null) { ZLog.W("no method " + methodName); return; }
                var hook = typeof(ZhijiPatcher).GetMethod(hookName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(m, postfix: new HarmonyMethod(hook));
                ZLog.W("patched " + typeName + "." + methodName);
            }
            catch (Exception e) { ZLog.W("patch fail " + typeName + "." + methodName + " " + e.Message); }
        }

        static Type[] _mine;

        public static Type[] Mine()
        {
            if (_mine != null) return _mine;
            var list = new List<Type>();
            foreach (var t in typeof(Zhiji).Assembly.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (typeof(AbstractModel).IsAssignableFrom(t)) list.Add(t);
            }
            _mine = list.ToArray();
            ZLog.W("mine types=" + _mine.Length);
            return _mine;
        }

        public static void BeforeInit()
        {
            try
            {
                var f = typeof(ModelDb).GetField("_allAbstractModelSubtypes", BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) f.SetValue(null, null);
                ZLog.W("init prefix, cache cleared");
            }
            catch (Exception e) { ZLog.W("init prefix fail " + e.Message); }
        }

        public static void BeforeGetSubtypes()
        {
            // no-op, kept for symmetry
        }

        static CharacterModel PickZhiji()
        {
            return ModelDb.GetByIdOrNull<CharacterModel>(ModelDb.GetId(typeof(Zhiji)));
        }

        // 用吱咿原位顶替「故障机器人」的插槽：走原版按钮的完整路径，输入/高亮/启程全部正常
        static List<CharacterModel> Substitute(IEnumerable<CharacterModel> src)
        {
            var list = new List<CharacterModel>(src ?? new CharacterModel[0]);
            var mine = PickZhiji();
            if (mine == null) return list;
            if (!list.Contains(mine)) list.Add(mine);   // 追加为第6个，保留原版5人
            return list;
        }

        public static void AfterUnlockCharacters(ref IEnumerable<CharacterModel> __result)
        {
            try { __result = Substitute(__result); }
            catch (Exception e) { ZLog.W("unlock chars fail " + e.Message); }
        }

        static bool _loggedChars;

        public static void AfterAllCharacters(ref IEnumerable<CharacterModel> __result)
        {
            try
            {
                var list = Substitute(__result);
                __result = list;
                if (!_loggedChars) { _loggedChars = true; ZLog.W("AllCharacters -> " + list.Count + " (append zhiji)"); }
            }
            catch (Exception e) { ZLog.W("allchars fail " + e.Message); }
        }

        public static void AfterGetSubtypes(ref Type[] __result)
        {
            try
            {
                var mine = Mine();
                var list = new List<Type>(__result ?? new Type[0]);
                bool changed = false;
                foreach (var t in mine) if (!list.Contains(t)) { list.Add(t); changed = true; }
                if (changed) __result = list.ToArray();
            }
            catch (Exception e) { ZLog.W("subtypes fail " + e.Message); }
        }

        static bool _injected;

        public static void AfterModelDbInit()
        {
            try
            {
                if (!_injected)
                {
                    _injected = true;
                    int n = 0;
                    foreach (var t in typeof(Zhiji).Assembly.GetTypes())
                    {
                        if (t.IsAbstract || t.IsInterface) continue;
                        if (!typeof(AbstractModel).IsAssignableFrom(t)) continue;
                        try { ModelDb.Inject(t); n++; }
                        catch (Exception e) { ZLog.W("inject fail " + t.Name + " " + e.Message); }
                    }
                    // InitIds 在启动早期会失败（缓存未初始化），这里不需要，跳过
                    try
                    {
                        var sf = typeof(ModelDb).GetField("_allAbstractModelSubtypes", BindingFlags.NonPublic | BindingFlags.Static);
                        if (sf != null) sf.SetValue(null, null);
                        ZLog.W("subtype cache left null (getter patch appends)");
                    }
                    catch (Exception e) { ZLog.W("subtype reset fail " + e.Message); }
                    int cleared = 0;
                    foreach (var f in typeof(ModelDb).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Nullable<>)) continue;
                            if (!f.Name.StartsWith("_all") && f.Name != "_acts" && f.Name != "_actsByIndex" && f.Name != "_badges" && f.Name != "_achievements") continue;
                            f.SetValue(null, null); cleared++;
                        }
                        catch { }
                    }
                    ZLog.W("injected models=" + n + " caches cleared=" + cleared);
                }
                var did = ModelDb.GetId(typeof(Drive));
                var cid = ModelDb.GetId(typeof(Zhiji));
                var rid = ModelDb.GetId(typeof(FleshStilts));
                var pid = ModelDb.GetId(typeof(BleedPower));
                ZLog.W("db all=" + ModelDb.All.Count() + " cards=" + ModelDb.AllCards.Count() + " chars=" + ModelDb.AllCharacters.Count() + " relics=" + ModelDb.AllRelics.Count());
                ZLog.W("ids drive=[" + did.Category + "/" + did.Entry + "] char=[" + cid.Category + "/" + cid.Entry + "]");
                ZLog.W("lookup drive=" + (ModelDb.GetByIdOrNull<CardModel>(did) != null)
                    + " char=" + (ModelDb.GetByIdOrNull<CharacterModel>(cid) != null)
                    + " relic=" + (ModelDb.GetByIdOrNull<RelicModel>(rid) != null)
                    + " power=" + (ModelDb.GetByIdOrNull<PowerModel>(pid) != null));
            }
            catch (Exception e) { ZLog.W("dbcheck fail " + e.Message); }
        }

        public static void AfterGetTable(object __result, string name)
        {
            try { Loc.Ensure(name, __result as LocTable); } catch (Exception e) { ZLog.W("loc hook fail " + e.Message); }
        }

        public static bool BypassStand;
        static bool _twinDone;
        static int _redCycle;
        static bool _overflowBusy;

        // 手牌超过 10 张时，让玩家自选弃牌（弃到 10 张）
        public static async void FixHandOverflow(MegaCrit.Sts2.Core.Entities.Players.Player p)
        {
            if (_overflowBusy || p == null) return;
            try
            {
                var pile = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, p);
                if (pile == null) return;
                int over = pile.Cards.Count - 10;
                if (over <= 0) return;
                _overflowBusy = true;
                var prefs = new MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs(
                    MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs.DiscardSelectionPrompt, over);
                var ctx = new MegaCrit.Sts2.Core.GameActions.Multiplayer.BlockingPlayerChoiceContext();
                var sel = await MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromCombatPile(ctx, pile, p, prefs);
                int n = 0;
                foreach (var c in sel)
                {
                    await MegaCrit.Sts2.Core.Commands.CardCmd.Discard(ctx, c);
                    n++;
                }
                ZLog.W("overflow: discarded " + n + " (was over by " + over + ")");
            }
            catch (Exception e) { ZLog.W("overflow fix fail " + e.Message); }
            finally { _overflowBusy = false; }
        }

        // SetUpCombat 之前也尝试强化一次（若此时敌人已存在，UI 构建时会用上强化后的血量）
        public static void BeforeSetUpCombat()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                if (cs == null || cs.Enemies == null || cs.Enemies.Count == 0) return;
                int n = ApplyBuffToEnemies(cs.Enemies);
                if (n > 0) { ZLog.W("buff enemies(pre) count=" + n); Visuals.RefreshEnemyHpBars(); }
            }
            catch (Exception e) { ZLog.W("buff pre fail " + e.Message); }
        }


        // 通过反射调用 set_MaxHp / set_CurrentHp（公开 setter，会触发血量变更事件 → 血条刷新）
        static void SetHpWithEvents(MegaCrit.Sts2.Core.Entities.Creatures.Creature c, int max, int cur)
        {
            try
            {
                var t = typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature);
                var pm = t.GetProperty("MaxHp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pc = t.GetProperty("CurrentHp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pm != null) pm.SetValue(c, max);
                if (pc != null) pc.SetValue(c, cur);
                else c.SetCurrentHpInternal(cur);
            }
            catch (Exception e) { ZLog.W("set hp fail " + e.Message); }
        }

        // 统一的强化实现（按基础血量 × 倍率，重复调用不会叠加）
        static int ApplyBuffToEnemies(System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Entities.Creatures.Creature> enemies)
        {
            int n = 0;
            if (enemies == null || Visuals.BuffMode == 0) return 0;
            int act = 1;
            try
            {
                var cm2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs2 = cm2 != null ? cm2.DebugOnlyGetState() : null;
                var rs = cs2 != null && cs2.Players != null && cs2.Players.Count > 0 ? cs2.Players[0].RunState : null;
                if (rs != null) act = rs.CurrentActIndex + 1;
            }
            catch { }
            int mult = Visuals.BuffMode == 2 ? 4000 : (act <= 1 ? 5 : (act == 2 ? 10 : 20));
            foreach (var e in enemies)
            {
                if (e == null) continue;
                object key = e;
                if (!_enemyBaseHp.ContainsKey(key)) _enemyBaseHp[key] = (int)e.MaxHp;
                int baseHp = _enemyBaseHp[key];
                int beforeHp = e.MaxHp;
                SetHpWithEvents(e, baseHp * mult, baseHp * mult);
                ZLog.W("buff enemy: base=" + baseHp + " before=" + beforeHp + " after=" + e.MaxHp + "/" + e.CurrentHp + " x" + mult);
                n++;
            }
            return n;
        }

        public static void AfterSetUpCombat()
        {
            try
            {
                // 只对吱咿生效：否则会改掉原版角色的立绘/数值
                try
                {
                    var cmG = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csG = cmG != null ? cmG.DebugOnlyGetState() : null;
                    var plG = csG != null && csG.Players != null && csG.Players.Count > 0 ? csG.Players[0] : null;
                    // 每场战斗刷新"是否吱咿"缓存，避免上一局的缓存影响这一局
                    try { _zhijiRunCached = plG != null && plG.Character != null && plG.Character.Id.Entry == "ZHIJI"; } catch { }
                    if (plG == null || plG.Character == null || plG.Character.Id.Entry != "ZHIJI") return;
                }
                catch { return; }
                ZhijiHooks.OnCombatSetUp();
                _redOrigin.Clear();
                _pendingOrigin.Clear();
                // 敌人基础血量缓存按场次存活：不清会一直持有旧战斗的 Creature（内存泄漏）
                try { _enemyBaseHp.Clear(); } catch { }
                _winResolved = false;
                // 每场战斗重置死亡收尾状态，否则第二次死亡不会弹结算界面
                _defeatScreenShown = false;
                _deathLosePending = false;
                _enemyBuffed = false;
                _redHandPending = 0;
                _lastRebuildTurn = -1;
                _forceRedHand = false;
                _rebuilding = false;
                _exhausting.Clear();
                try
                {
                    var cmR = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csR = cmR != null ? cmR.DebugOnlyGetState() : null;
                    var zp = csR != null && csR.Players != null && csR.Players.Count > 0 ? csR.Players[0] : null;
                    var z0 = ZhijiStore.Of(zp);
                    bool unboundHeart = false;
                    try { unboundHeart = zp != null && zp.Relics != null && zp.Relics.Any(r => r is UnboundHeart); } catch { }
                    z0.Form = unboundHeart ? ZhijiForm.RedGod : ZhijiForm.Normal;
                    if (unboundHeart)
                    {
                        Visuals.ShowOverlay(true);
                        // 把牌组里未附魔的攻击/技能牌补上「失控」
                        try
                        {
                            int enchN = 0;
                            if (zp.Deck != null && zp.Deck.Cards != null)
                                foreach (var c in zp.Deck.Cards)
                                {
                                    if (c == null || c.Enchantment != null) continue;
                                    if (c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack && c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill) continue;
                                    try { MegaCrit.Sts2.Core.Commands.CardCmd.Enchant<UnboundRage>(c, 1m); enchN++; } catch { }
                                }
                            if (enchN > 0) ZLog.W("unbound heart enchanted " + enchN + " deck cards");
                        }
                        catch { }
                    }
                    z0.RForm = RedForm.Balance;
                    z0.Pulse = 0; z0.Hunger = 0; z0.Feast = 0; z0.Stand = 0;
                    z0.Craving = 0; z0.Reap = 0; z0.Debt = 0; z0.LastBleed = 0;
                    z0.AtkCount = 0; z0.DefCount = 0;
                    z0.PendingRevert = false; z0.GotEndCard = false; z0.Restoring = false; z0.EndCardAllowed = true;
                    z0.BleedTicks = 0;      // 千刀成长按场重置
                    z0.MinionHp = 0;        // 召唤物不跨战斗保留
                    z0.MinionHp2 = 0;
                    z0.MinionDeathExploded = false;   // 每场战斗允许爆裂一次
                    z0.VassalFreeRevive = false;      // 每场战斗第一次复活免费
                    z0.DeathHandled = false;
                    ZLog.W("combat state reset (hp kept)");
                }
                catch (Exception e0) { ZLog.W("combat state reset fail " + e0.Message); }

                // 调试模式：敌人血量 ×N（默认 400），方便一场战斗内跑完整套流程
                try
                {
                    var cmR2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csR = cmR2 != null ? cmR2.DebugOnlyGetState() : null;
                    bool dbg2 = Godot.FileAccess.FileExists(Godot.ProjectSettings.GlobalizePath("user://zhiji_debug"));
                    if (dbg2 && csR != null && csR.Enemies != null)
                    {
                        int mult = 400;
                        try
                        {
                            var mp = Godot.ProjectSettings.GlobalizePath("user://zhiji_hpmult");
                            if (System.IO.File.Exists(mp))
                            {
                                int v;
                                if (int.TryParse(System.IO.File.ReadAllText(mp).Trim(), out v) && v > 0) mult = v;
                            }
                        }
                        catch { }
                        var seen = new System.Collections.Generic.HashSet<object>();
                        foreach (var en in csR.Enemies)
                        {
                            if (en == null) continue;
                            if (!seen.Add(en)) continue;
                            int baseHp = (int)en.MaxHp;
                            en.SetMaxHpInternal(baseHp * mult);
                            if (!en.IsDead) en.SetCurrentHpInternal(en.MaxHp);
                            ZLog.W("debug hp x" + mult + ": " + baseHp + " -> " + en.MaxHp);
                        }
                        Visuals.RefreshEnemyHpBars();
                    }
                }
                catch (Exception e9) { ZLog.W("debug hp buff fail " + e9.Message); }
                Visuals.ShowOverlay(ZhijiStore.CurrentForm() == ZhijiForm.RedGod);
                HookCombatEvents();
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var st = cm != null ? cm.DebugOnlyGetState() : null;
                try { var pl0 = st != null && st.Players != null && st.Players.Count > 0 ? st.Players[0] : null; if (pl0 != null) { ZhijiStore.ResetCombat(pl0); Visuals.HidePlayerCreature(pl0); } } catch { }
                try { Visuals.Warmup(); } catch { }
                // 兜底：某些入口（调试跳房）不会触发 SetupPlayerTurn，这里延迟补一次回合初始化
                try
                {
                    var plB = st != null && st.Players != null && st.Players.Count > 0 ? st.Players[0] : null;
                    var treeB = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                    if (plB != null && treeB != null)
                    {
                        var tmrB = treeB.CreateTimer(0.8);
                        tmrB.Timeout += () => { try { ZhijiHooks.OnPlayerTurnStart(plB); } catch (Exception eB2) { ZLog.W("turn init fallback fail " + eB2.Message); } };
                    }
                }
                catch (Exception eFB) { ZLog.W("turn init fallback schedule fail " + eFB.Message); }
                if (st != null && st.Players != null && st.Players.Count > 0)
                {
                    var p0 = st.Players[0];
                    var pz = ZhijiStore.Of(p0);
                    bool newRun = !object.ReferenceEquals(_hpInitRun, p0.RunState);
                    bool fullHp = p0.Creature != null && p0.Creature.CurrentHp >= p0.Creature.MaxHp;
                    if (newRun && p0.Creature != null)
                    {
                        _hpInitRun = p0.RunState;   // 无论是否改血，都标记这次 run 已处理
                    }
                    if (newRun && fullHp && p0.Creature != null)
                    {
                        try
                        {
                            // 旧的测试用"开局压到 72 血"逻辑已移除，保留日志
                            ZLog.W("initial hp " + p0.Creature.CurrentHp + "/" + p0.Creature.MaxHp);
                        }
                        catch (Exception e2) { ZLog.W("init hp fail " + e2.Message); }
                    }
                    var ch = p0.Character != null ? p0.Character.Id.Entry : "?";
                    var deck = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, p0);
                    ZLog.W("RUN character=" + ch + " drawPile=" + (deck != null ? deck.Cards.Count : -1));
                    if (deck != null && deck.Cards.Count > 0)
                    {
                        var names = "";
                        for (int i = 0; i < Math.Min(6, deck.Cards.Count); i++) names += deck.Cards[i].Id.Entry + ",";
                        ZLog.W("deck sample: " + names);
                    }
                    try
                    {
                        var rl = "";
                        if (p0.Relics != null) foreach (var r in p0.Relics) rl += r.Id.Entry + ",";
                        ZLog.W("relics: " + rl);
                    }
                    catch { }
                if (Visuals.BuffMode != 0 && st != null && st.Enemies != null)
                {
                    try
                    {
                        int act = 1;
                        try { var rs = p0.RunState; if (rs != null) act = rs.CurrentActIndex + 1; } catch { }
                        int mult = Visuals.BuffMode == 2
                            ? 4000                                            // 测试模式：4000 倍血
                            : (act <= 1 ? 5 : (act == 2 ? 10 : 20));           // 正常：5/10/20 倍
                        int n = 0;
                        foreach (var e in st.Enemies)
                        {
                            if (e == null || e.IsDead) continue;
                            int baseHp = (int)e.MaxHp;
                            SetHpWithEvents(e, baseHp * mult, baseHp * mult);
                            n++;
                        }
                        string hp = "";
                        foreach (var e in st.Enemies) if (e != null) hp += e.MaxHp + "/" + e.CurrentHp + " ";
                        ZLog.W("buff enemies: act=" + act + " x" + mult + " count=" + n + " hp=" + hp);
                        Visuals.RefreshEnemyHpBars();
                    }
                    catch (Exception e3) { ZLog.W("buff enemies fail " + e3.Message); }
                }
                    ZhijiPowers.Sync(p0);
            StartDebugPump();

                }
                ZLog.W("combat setup");
            }
            catch (Exception e) { ZLog.W("setup fail " + e.Message); }
        }

        // 血色舞台：变身后回合结束失去4生命
        public static void BeforeEndTurnCleanup(object __instance)
        {
            try
            {
                var pcs = __instance as PlayerCombatState;
                if (pcs == null) return;
                var f = typeof(PlayerCombatState).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic);
                var pl = f != null ? f.GetValue(pcs) as Player : null;
                if (pl == null) return;
                var st = ZhijiStore.Of(pl);
                if (st.Debt > 0)
                {
                    int d = st.Debt; st.Debt = 0;
                    pl.Creature.LoseHpInternal(d, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                    ZLog.W("feast debt paid hp=" + d);
                }
                // 赤红摇篮：回合结束敌人流血≥10 → +5 搏动
                try
                {
                    var cstate = pl.Creature != null ? pl.Creature.CombatState : null;
                    if (cstate != null && cstate.Enemies != null)
                    {
                        int total = 0;
                        foreach (var e in cstate.Enemies) if (e != null && !e.IsDead) total += e.GetPowerAmount<BleedPower>();
                        if (total >= 10) { st.AddPulse(5); ZhijiPowers.Sync(pl); }
                    }
                }
                catch { }
                try { DrainTransforms(); } catch { }
                try { Visuals.CleanupDeadEnemies(); } catch { }
                if (st.Form != ZhijiForm.RedGod) return;
                // 大招结束后：点击结束回合 → 回落幼年，造成一段伤害 + 回血
                bool unboundNow = false;
                try { unboundNow = pl.Relics != null && pl.Relics.Any(r => r is UnboundHeart); } catch { }
                if (st.PendingRevert && unboundNow) { st.PendingRevert = false; ZLog.W("unbound heart: revert skipped"); }
                if (st.PendingRevert)
                {
                    st.PendingRevert = false;
                    try
                    {
                        // 回落：恢复缺失生命的一半，并对全体敌人造成等量伤害
                        int missing = (int)(pl.Creature.MaxHp - pl.Creature.CurrentHp);
                        int healAmt = missing > 0 ? missing / 2 : 0;
                        if (healAmt > 0) pl.Creature.HealInternal(healAmt);
                        int dmg = System.Math.Max(15, healAmt);   // 至少 15 点保底
                        var cs = pl.Creature != null ? pl.Creature.CombatState : null;
                        if (cs != null && cs.Enemies != null)
                        {
                            foreach (var e in cs.Enemies)
                            {
                                if (e == null || e.IsDead) continue;
                                e.LoseHpInternal(dmg, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                            }
                        }
                        ZLog.W("revert: heal=" + healAmt + " aoe=" + dmg);
                        // 回落：红神牌会在弃牌时被移出战斗，无需还原
                        // 先切回幼年形态并加锁，避免归还的手牌被"红神转化"逻辑吃掉
                        st.Restoring = true;
                        st.Form = ZhijiForm.Normal;
                        // 先清空当前手牌（红神牌），避免带回幼年期
                        try
                        {
                            var hp0 = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(
                                MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                            if (hp0 != null)
                            {
                                foreach (var c0 in hp0.Cards.ToList())
                                {
                                    if (c0 == null) continue;
                                    _ = MegaCrit.Sts2.Core.Commands.CardPileCmd.RemoveFromCombat(c0, false);  // skipVisuals=false：UI 同步移除，避免残影
                                    HideCardVisuals(c0);   // 再兜一层，防止残留卡面
                                }
                                ZLog.W("cleared red hand before restore");
                            }
                        }
                        catch (Exception e5) { ZLog.W("clear hand fail " + e5.Message); }

                        // 不再从 SavedHand 还牌（Transform 已还原原件）
                        st.Restoring = false;
                        st.Feast = 0;
                        st.Reap = 0;
                        st.Hunger = 0;          // 清空饥饿，重新累计
                        st.Craving = 0;
                        st.GotEndCard = false;  // 允许再次获得「童年的终结」
                        st.EndCardAllowed = true;   // 新的饥饿周期开始，重新攒满 100 可再拿
                        Visuals.SetOverlayForm(false);
                        Visuals.PlayTransform("transform_back");
                        try { MegaCrit.Sts2.Core.Nodes.NGame.Instance?.ScreenShake(0, 0, 90f); } catch { }
                        ZhijiPowers.Sync(pl);
                        ZLog.W("reverted to kid: aoe15, heal half missing, hunger reset, hand restored");
                    }
                    catch (Exception e2) { ZLog.W("revert fail " + e2.Message); }
                    return;
                }
                if (pl.Relics == null || !pl.Relics.Any(r => r is CrimsonStage)) return;
                pl.Creature.LoseHpInternal(4m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                ZhijiPowers.Sync(pl);
            }
            catch (Exception e) { ZLog.W("endturn fail " + e.Message); }
        }

        public static void ButtonInitPostfix(object __instance, MegaCrit.Sts2.Core.Models.CharacterModel character)
            => CharPatches.AfterButtonInit(__instance, character);

        public static void ButtonSelectPostfix(object __instance) => CharPatches.AfterSelect(__instance);

        public static void ScreenSelectPostfix(object __instance, object charSelectButton, MegaCrit.Sts2.Core.Models.CharacterModel characterModel)
            => CharPatches.AfterScreenSelect(__instance, charSelectButton, characterModel);

        public static bool CardPortraitPrefix(MegaCrit.Sts2.Core.Models.CardModel __instance, ref Godot.Texture2D __result)
            => CharPatches.CardPortrait(__instance, ref __result);

        public static bool IconTexturePrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref Godot.Texture2D __result)
            => CharPatches.IconTexture(__instance, ref __result);

        public static bool VisualsPathPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.VisualsPath(__instance, ref __result);
        public static bool IconPathPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.IconPath(__instance, ref __result);
        public static bool SelectIconPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.SelectIcon(__instance, ref __result);
        public static bool SelectLockedPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.SelectLockedIcon(__instance, ref __result);
        public static bool SelectBgPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.SelectBg(__instance, ref __result);
        public static bool EnergyCounterPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.EnergyCounter(__instance, ref __result);
        public static bool RestSitePrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.RestSite(__instance, ref __result);
        public static bool MerchantPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.Merchant(__instance, ref __result);
        public static bool TransitionPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.Transition(__instance, ref __result);
        public static bool MapMarkerPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.MapMarker(__instance, ref __result);
        public static bool TrailPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.Trail(__instance, ref __result);
        public static bool TopIconPrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.TopIconTexture(__instance, ref __result);
        public static bool TopIconOutlinePrefix(MegaCrit.Sts2.Core.Models.CharacterModel __instance, ref string __result)
            => CharPatches.TopIconOutline(__instance, ref __result);

        public static void AfterSetupPlayerTurn(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            try
            {
                if (player == null) return;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) { ZhijiHooks.OnPlayerTurnStart(player); return; }
                var t = tree.CreateTimer(0.25);
                t.Timeout += () =>
                {
                    try { ZhijiHooks.OnPlayerTurnStart(player); }
                    catch (Exception e) { ZLog.W("turn start hook fail " + e.Message); }
                };
            }
            catch (Exception e) { ZLog.W("after setup turn fail " + e.GetBaseException().Message); }
        }

        public static void AfterResetEnergy(object __instance)
        {
            try
            {
                var pcs = __instance as PlayerCombatState;
                if (pcs == null) return;
                var f = typeof(PlayerCombatState).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic);
                var pl = f != null ? f.GetValue(pcs) as Player : null;
                if (pl != null)
                {
                    // 非吱咿角色：不跑任何吱咿回合逻辑
                    try { if (pl.Character == null || pl.Character.Id.Entry != "ZHIJI") return; } catch { return; }
                    if (!_enemyBuffed) { _enemyBuffed = true; ApplyEnemyBuff(pl); }
                    DrainTransforms();
                    Visuals.CleanupDeadEnemies();
                    var st = ZhijiStore.Of(pl);
                    if (st.Form == ZhijiForm.RedGod)
                    {
                        // 红神形态的额外收益已拆到能力牌（暴食本能）与遗物（猩红圣痕），本体不再白给
                        // （旧的弃牌堆还原/手牌转换逻辑已删除，统一由 DrawInternal + RedDrawAsync 处理）
                        // 迫切饥饿增伤已并入 FeastBonus
                    }
                    // 流血增长 → 搏动
                    try
                    {
                        var cstate = pl.Creature != null ? pl.Creature.CombatState : null;
                        if (cstate != null && cstate.Enemies != null)
                        {
                            int total = 0;
                            foreach (var e in cstate.Enemies) if (e != null && !e.IsDead) total += e.GetPowerAmount<BleedPower>();
                            if (total > st.LastBleed) st.AddPulse(System.Math.Min(10, total - st.LastBleed));
                            st.LastBleed = total;
                        }
                    }
                    catch { }
                    ZhijiPowers.Sync(pl);
                    FixHandOverflow(pl);
                }
            }
            catch (Exception e) { ZLog.W("turnstart fail " + e.Message); }
        }
    }

    public static class Loc
    {
        public static readonly Dictionary<string, string> MechanicNotes = new Dictionary<string, string>();
        static Dictionary<string, string> _all;
        static readonly HashSet<string> Done = new HashSet<string>();

        static Dictionary<string, string> All()
        {
            if (_all != null) return _all;
            _all = new Dictionary<string, string>();
            foreach (var t in new[] { "cards", "characters", "relics", "powers", "events", "enchantments" })
            {
                var d = Build(t);
                if (d == null) continue;
                foreach (var kv in d)
                {
                    _all[t + "|" + kv.Key.ToUpperInvariant()] = kv.Value;
                }
            }
            ZLog.W("loc dict entries=" + _all.Count);
            return _all;
        }

        public static bool TryGet(string table, string key, out string val)
        {
            val = null;
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key)) return false;
            return All().TryGetValue(table.ToLowerInvariant() + "|" + key.ToUpperInvariant(), out val);
        }

        public static void Ensure(string table, LocTable t)
        {
            if (t == null || table == null || Done.Contains(table)) return;
            var d = Build(table);
            if (d == null) return;
            if (table == "cards")
            {
                try
                {
                    foreach (var k in new List<string>(d.Keys))
                    {
                        if (!k.EndsWith(".description", StringComparison.OrdinalIgnoreCase)) continue;
                        var note = ExplainMechanics(d[k]);
                        d[k] = note;
                        if (note != d[k]) { }
                        try { MechanicNotes[k.ToUpperInvariant()] = note; } catch { }
                    }
                }
                catch { }
            }
            try
            {
                var upper = new Dictionary<string, string>();
                foreach (var kv in d)
                {
                    var k = kv.Key.ToUpperInvariant();
                    if (!upper.ContainsKey(k)) upper[k] = kv.Value;
                }
                foreach (var kv in upper) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                t.MergeWith(d);
                Done.Add(table);
                ZLog.W("loc " + table + " +" + d.Count);
            }
            catch (Exception e) { ZLog.W("loc fail " + table + " " + e.Message); }
        }

        static readonly (string term, string text)[] MechanicTips = new (string, string)[]
        {
            ("空放", "没有合法目标时自动改为防御效果"),
            ("搏动", "红神资源：提升伤害与格挡"),
            ("饥饿", "满足条件后触发形态变化"),
            ("自助餐", "召唤血肉的资源"),
            ("流血", "每回合失去生命并受到层数伤害"),
            ("替身", "优先吸收伤害，每层挡1点，最多挡一半"),
            ("失控", "伤害+50%，打出后反伤"),
            ("血肉", "召唤物，替本体承受伤害"),
            ("眷属", "血肉的进化形态，每回合自动攻击"),
            ("收割", "终结类资源"),
            ("残渣", "血肉死亡时爆炸并回复"),
            ("开席", "消耗自助餐召唤血肉"),
            ("献祭", "牺牲血肉换取收益"),
            ("卑贱之物", "每回合造成伤害")
        };

        static string ExplainMechanics(string desc)
        {
            try
            {
                if (string.IsNullOrEmpty(desc) || desc.Contains("机制说明：")) return desc;
                var sb = new System.Text.StringBuilder();
                foreach (var mt in MechanicTips)
                {
                    if (!desc.Contains(mt.term)) continue;
                    if (sb.Length == 0) sb.Append("\n[gold]机制说明[/gold]：");
                    else sb.Append("；");
                    sb.Append(mt.term).Append("—").Append(mt.text);
                }
                if (sb.Length == 0) return desc;
                return desc + sb.ToString() + "。";
            }
            catch { return desc; }
        }

        static Dictionary<string, string> Build(string table)
        {
            switch (table)
            {
                case "events": return new Dictionary<string, string>
                {
                    {"RED_HEART.title", "红神之心"},
                    {"RED_HEART.pages.INITIAL.description", "第二层的教堂倒悬在天花板上。祭坛中央，一颗巨大的心脏还在跳动——频率和你胸腔里的那颗一模一样。\n「[gold]失控[/gold]之心只是门。」一个没有脸的声音说，「把它带回去，或者把你的心留下。」"},
                    {"RED_HEART.options.ACCEPT.title", "接过心脏"},
                    {"RED_HEART.options.ACCEPT.description", "获得【红神之心】：[gold]血肉[/gold]进化为红神[gold]眷属[/gold]。失去 10% 最大生命。"},
                    {"RED_HEART.options.OPEN.title", "剖开胸腔"},
                    {"RED_HEART.options.OPEN.description", "把心脏按进[gold]血肉[/gold]：血肉最大生命 +10 并回复 10；自己失去 8 生命。没有血肉则获得 2 张召唤牌。"},
                    {"RED_HEART.options.LEAVE.title", "关上门"},
                    {"RED_HEART.options.LEAVE.description", "回复 20% 最大生命，离开这里。"},
                    {"RED_HEART.pages.ACCEPT.description", "心脏沉进你的胸腔。[gold]血肉[/gold]在脚边抬头，第一次用你的眼睛看你。"},
                    {"RED_HEART.pages.OPEN.description", "你把心脏按进[gold]血肉[/gold]的胸腔。它学会了你的心跳。"},
                    {"RED_HEART.pages.LEAVE.description", "门在你身后合上。心跳声慢慢远去。"},
                    {"HATCH.title", "孵化完成"},
                    {"HATCH.pages.INITIAL.description", "展厅里那台摇篮彻底打开了。\n经理把一份合同推过来：「KPI 达标。签了字，你就是产品本身。」\n吱咿看着合同上自己的名字，饿得几乎握不住笔。"},
                    {"HATCH.options.ACCEPT.title", "签下名字"},
                    {"HATCH.options.ACCEPT.description", "获得遗物【[gold]失控[/gold]之心】：永久红神，全牌组附魔【失控】。\n本局不再回落幼年。"},
                    {"HATCH.options.REFUSE.title", "推开合同"},
                    {"HATCH.options.REFUSE.description", "获得 75 金币，回复 10% 最大生命。"},
                    {"HATCH.pages.ACCEPT.description", "笔尖落下的瞬间，[gold]饥饿[/gold]从胃里烧到眼睛。\n经理鼓掌：「上线成功。」"},
                    {"HATCH.pages.REFUSE.description", "你把合同推回去。经理叹了口气，把一袋金币放在桌上，像给离职员工的遣散费。"},
                    {"HARVEST_FEST.title", "丰收祭"},
                    {"HARVEST_FEST.pages.INITIAL.description", "田埂上摆着去年的镰刀和今年的账本。\n戴草帽的会计抬头：「收成可以换钱，也可以换把新镰刀。」"},
                    {"HARVEST_FEST.options.SELL.title", "上交收成"},
                    {"HARVEST_FEST.options.SELL.description", "清空[gold]收割[/gold]值，每 2 点换 1 金币。"},
                    {"HARVEST_FEST.options.SCYTHE.title", "领一把镰刀"},
                    {"HARVEST_FEST.options.SCYTHE.description", "获得卡牌【[gold]收割[/gold]之镰】。"},
                    {"HARVEST_FEST.options.LEAVE.title", "离开"},
                    {"HARVEST_FEST.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"HARVEST_FEST.pages.SELL.description", "会计数了又数，把一小袋金币推过来。[gold]收割[/gold]值清零。"},
                    {"HARVEST_FEST.pages.SCYTHE.description", "新镰刀比旧的轻，刃口却更冷。你把[gold]收割[/gold]值留在了刀柄里。"},
                    {"HARVEST_FEST.pages.LEAVE.description", "你绕过田埂。会计还在算，算盘声像一场没完的[gold]收割[/gold]。"},
                    {"WAR_COUNCIL.title", "战备会议"},
                    {"WAR_COUNCIL.pages.INITIAL.description", "三张红木桌拼成长案，三位红西装中层各坐一头。\n「战、守，还是回去？签个字，我们好做绩效。」"},
                    {"WAR_COUNCIL.options.WAR.title", "签战纹章"},
                    {"WAR_COUNCIL.options.WAR.description", "失去 5% 最大生命；获得遗物【战纹章】。"},
                    {"WAR_COUNCIL.options.GUARD.title", "签守纹章"},
                    {"WAR_COUNCIL.options.GUARD.description", "失去 5% 最大生命；获得遗物【守纹章】。"},
                    {"WAR_COUNCIL.options.LEAVE.title", "不签"},
                    {"WAR_COUNCIL.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"WAR_COUNCIL.pages.WAR.description", "战字那一栏盖了章。你听见远处有人在磨刀，也有人在鼓掌。"},
                    {"WAR_COUNCIL.pages.GUARD.description", "守字那一栏盖了章。红木桌下伸出一只手，把一面小盾推进你口袋。"},
                    {"WAR_COUNCIL.pages.LEAVE.description", "你推门出去。三位中层同时低头写字，像什么都没发生。"},
                    {"HUNGER_THEATER.title", "饥饿剧场"},
                    {"HUNGER_THEATER.pages.INITIAL.description", "一间由胃壁改成的剧场，观众席上坐满了戴红鼻子的影子。\n舞台中央悬着一只巨大的、还在跳动的聚光灯。\n主持人说：「上台表演，或者坐下面看——票钱都一样。」"},
                    {"HUNGER_THEATER.options.ACT.title", "上台表演"},
                    {"HUNGER_THEATER.options.ACT.description", "获得 1 张随机能力牌；[gold]饥饿[/gold] +15。"},
                    {"HUNGER_THEATER.options.WATCH.title", "坐着当观众"},
                    {"HUNGER_THEATER.options.WATCH.description", "获得 60 金币；失去 5% 最大生命。"},
                    {"HUNGER_THEATER.options.LEAVE.title", "离开"},
                    {"HUNGER_THEATER.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"HUNGER_THEATER.pages.ACT.description", "你在聚光灯下演了一段[gold]饥饿[/gold]。影子们鼓掌，塞给你一张还带着体温的牌。"},
                    {"HUNGER_THEATER.pages.WATCH.description", "演出很长。你数着影子们的红鼻子，直到有人往你手里塞了票钱。"},
                    {"HUNGER_THEATER.pages.LEAVE.description", "你走出剧场。身后的掌声像一场没有尽头的消化。"},
                    {"BLOOD_GIFT.title", "血之馈赠"},
                    {"BLOOD_GIFT.pages.INITIAL.description", "沉没的雕像只剩半截手臂露在水面，掌心托着一只仍在[gold]搏动[/gold]的血囊。\n水面下有声音说：「割开我，或者被我割开。」"},
                    {"BLOOD_GIFT.options.OFFER.title", "割开血囊"},
                    {"BLOOD_GIFT.options.OFFER.description", "失去 10% 最大生命；获得 2 张随机 DOT 卡 + 20 金币。"},
                    {"BLOOD_GIFT.options.SAMPLE.title", "抽取血样"},
                    {"BLOOD_GIFT.options.SAMPLE.description", "获得遗物【[gold]血肉[/gold]砧板】。"},
                    {"BLOOD_GIFT.options.WEAR.title", "披上寿衣"},
                    {"BLOOD_GIFT.options.WEAR.description", "失去 15% 最大生命；获得遗物【黄金寿衣】（不换牌）。"},
                    {"BLOOD_GIFT.options.LEAVE.title", "离开"},
                    {"BLOOD_GIFT.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"BLOOD_GIFT.pages.OFFER.description", "血囊在你手里瘪下去，像一声被掐断的笑。水流把两张湿透的牌推到你脚边。"},
                    {"BLOOD_GIFT.pages.SAMPLE.description", "你把血样滴在随身带的砧板上。它吸饱了，开始发烫。"},
                    {"BLOOD_GIFT.pages.WEAR.description", "金线寿衣顺着水面爬上你的身体。它不换走任何一张牌——只是把你缝进了另一种活着的方式。"},
                    {"BLOOD_GIFT.pages.LEAVE.description", "你退开。血囊还在[gold]搏动[/gold]，耐心得像一个不会挂断的电话。"},
                    {"VILE_MARKET.title", "卑贱交易所"},
                    {"VILE_MARKET.pages.INITIAL.description", "一间用旧合同糊墙的铺子，柜台后坐着个戴纸袋的商人。\n「[gold]卑贱之物[/gold]？有。按斤卖，也按斤收。」\n他推来一只信封，里面漏出几缕灰白色的低语。"},
                    {"VILE_MARKET.options.BUY.title", "买一包卑贱样本"},
                    {"VILE_MARKET.options.BUY.description", "支付 60 金币，获得 2 张随机卑贱卡。"},
                    {"VILE_MARKET.options.PLEDGE.title", "用血换王冠"},
                    {"VILE_MARKET.options.PLEDGE.description", "失去 10% 最大生命，获得遗物【卑贱王冠】。"},
                    {"VILE_MARKET.options.LEAVE.title", "离开"},
                    {"VILE_MARKET.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"VILE_MARKET.pages.BUY.description", "信封里是几张写着字的皮肤。商人说：「用的时候别念出声。」"},
                    {"VILE_MARKET.pages.PLEDGE.description", "纸袋商人量了量你的血，点点头：「成色不错。」一顶轻飘飘的冠扣在你头上。"},
                    {"VILE_MARKET.pages.LEAVE.description", "你退出铺子。身后传来翻合同的声音，像有人在数你的工龄。"},
                    {"CRIMSON_ALTAR.title", "猩红祭坛"},
                    {"CRIMSON_ALTAR.pages.INITIAL.description", "一座由[gold]血肉[/gold]与金线垒成的祭坛立在走廊尽头。\n坛上摆着半碗还温着的红汤，汤面倒映出一个更红、更[gold]饥饿[/gold]的自己。\n旁边的牌位写着：「献给未来的品牌人格。」"},
                    {"CRIMSON_ALTAR.options.FEED.title", "饮下红汤"},
                    {"CRIMSON_ALTAR.options.FEED.description", "永久 +8 [gold]自助餐[/gold]；失去 5 点最大生命。"},
                    {"CRIMSON_ALTAR.options.FIRE.title", "偷走火种"},
                    {"CRIMSON_ALTAR.options.FIRE.description", "获得 2 张随机 DOT 卡；[gold]饥饿[/gold] +20。"},
                    {"CRIMSON_ALTAR.options.LEAVE.title", "离开"},
                    {"CRIMSON_ALTAR.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"CRIMSON_ALTAR.pages.FEED.description", "红汤入喉，像吞下一小段自己。你听见远处有人盖章：「续约成功。」"},
                    {"CRIMSON_ALTAR.pages.FIRE.description", "火种烫手。它在你怀里跳，像一颗还没决定要不要长大的心脏。"},
                    {"CRIMSON_ALTAR.pages.LEAVE.description", "你退出走廊。祭坛上的红汤还在冒热气，仿佛永远等得到下一个人。"},
                    {"RED_CHURCH.title", "红神教会"},
                    {"RED_CHURCH.pages.INITIAL.description", "废弃的展厅深处，一群穿红袍的人围着一尊没有脸的红神像低声祷告。\n祭坛上摆着合同、血样和一只空摇篮。\n领祷者抬起头：「新来的？红神只认喂养，不认虔诚。」"},
                    {"RED_CHURCH.options.OFFER.title", "献上鲜血"},
                    {"RED_CHURCH.options.OFFER.description", "失去 10% 最大生命。\n获得遗物【猩红圣痕】：红神形态每回合开始 +1 [gold]自助餐[/gold]。"},
                    {"RED_CHURCH.options.LISTEN.title", "聆听布道"},
                    {"RED_CHURCH.options.LISTEN.description", "获得 2 张随机 DOT 卡。"},
                    {"RED_CHURCH.options.SMASH.title", "砸碎神像"},
                    {"RED_CHURCH.options.SMASH.description", "获得 100 金币 + 1 张随机稀有卡。"},
                    {"RED_CHURCH.options.LEAVE.title", "离开"},
                    {"RED_CHURCH.options.LEAVE.description", "回复 10% 最大生命。"},
                    {"RED_CHURCH.pages.OFFER.description", "你的血渗进祭坛的裂缝。红袍们齐声念诵：「转化率 +1。」"},
                    {"RED_CHURCH.pages.LISTEN.description", "布道很长，你只记住了两句：「[gold]流血[/gold]要趁热」「疼痛是留存率」。"},
                    {"RED_CHURCH.pages.SMASH.description", "神像碎成红金色的渣。红袍们没有阻拦，只是记下了你的工号。"},
                    {"RED_CHURCH.pages.LEAVE.description", "你退出展厅。身后祷告声没停，像一台不肯休眠的服务器。"},
                    {"RED_CRADLE.title", "红神的摇篮"},
                    {"RED_CRADLE.pages.INITIAL.description", "红色射灯下，一间被布置成展厅的房间。中央摆着一张由[gold]血肉[/gold]、金线和合同纸编成的摇篮，正在缓慢[gold]搏动[/gold]。\n穿红西装的经理递来名片：\n「红色品牌经理。我们不做神，我们做神的摇篮。」\n他说红神不是天生的，是「喂养出来的品牌人格」。摇篮会替她记住[gold]饥饿[/gold]、放大搏动，把每一次疼痛转成增长数据。\n吱咿盯着摇篮，像看见一个更舒服、也更危险的自己。"},
                    {"RED_CRADLE.options.LIE.title", "躺进摇篮"},
                    {"RED_CRADLE.options.LIE.description", "「别担心，第一次孵化都有 KPI。」\n获得遗物【猩红摇篮】；[gold]饥饿[/gold] +30。"},
                    {"RED_CRADLE.options.TEAR.title", "拆掉摇篮"},
                    {"RED_CRADLE.options.TEAR.description", "「拆得好，材料费从你账上扣。」\n获得 120 金币 + 2 张随机 DOT 基础卡。"},
                    {"RED_CRADLE.options.CONTRACT.title", "问经理要合同"},
                    {"RED_CRADLE.options.CONTRACT.description", "「你终于像个渠道商了。」\n获得 2 张随机幼年卡 + 75 金币。"},
                    {"RED_CRADLE.options.LEAVE.title", "盖上白布离开"},
                    {"RED_CRADLE.options.LEAVE.description", "「样品不带走吗？」\n回复 15% 最大生命 + 50 金币。"},
                    {"RED_CRADLE.pages.LIE.description", "摇篮记住了你的心跳。下一次[gold]饥饿[/gold]满时，它会轻轻推你一把。\n经理在背后记下：「孵化率 +1，疼痛转化正常。」"},
                    {"RED_CRADLE.pages.TEAR.description", "你扯断金线，合同纸碎了一地。经理微笑：「拆得好，材料费从你账上扣。」"},
                    {"RED_CRADLE.pages.CONTRACT.description", "经理把试用装塞进你牌组，像塞三张传单。"},
                    {"RED_CRADLE.pages.LEAVE.description", "你听见摇篮在身后轻轻哭了一声，也可能是射灯电流。"},
                    {"DARK_DINER.title", "阴暗饭馆"},
                    {"DARK_DINER.pages.INITIAL.description", "门帘后飘出油与铁锈的味道。\n老板不抬头：「饿了吧？坐下吃。吃饱了，就不饿了。」"},
                    {"DARK_DINER.pages.DONE.description", "你吃完了。[gold]饥饿[/gold]从身体里退潮，只剩一层薄薄的、金色的饿。"},
                    {"DARK_DINER.pages.LEAVE.description", "你掀帘离开。[gold]饥饿[/gold]还在。"},
                    {"DARK_DINER.options.EAT.title", "坐下吃饭"},
                    {"DARK_DINER.options.EAT.description", "获得【黄金寿衣】。永远保持幼年形态，封印无休止[gold]饥饿[/gold]与变身。\n红神卡牌移出这一局，并每张幼年卡换 35 点数。"},
                    {"DARK_DINER.options.LEAVE.title", "离开"},
                    {"DARK_DINER.options.LEAVE.description", "什么也不做。"},
                    {"DARK_DINER.options.OPEN_SHOP.title", "打开商店"},
                    {"DARK_DINER.options.OPEN_SHOP.description", "用金币挑选[gold]流血[/gold]卡、遗物和药水。"},
                    {"DARK_DINER.options.NEXT.title", "看看别的菜"},
                    {"DARK_DINER.options.NEXT.description", "翻到下一页。"},
                    {"DARK_DINER.pages.SHOP_FINISH.description", "你付了账，肥商人掀开帘子，把货摊推到你面前。"},
                    {"DARK_DINER.pages.SHOP.description", "柜台上的菜牌只写着价格：「一道菜，三十点。」"},
                    {"DARK_DINER.options.BUY_SLIT.title", "点割喉（30点）"},
                    {"DARK_DINER.options.BUY_SLIT.description", "加入牌组：造成伤害并施加[gold]流血[/gold]。"},
                    {"DARK_DINER.options.BUY_LET.title", "点放血（30点）"},
                    {"DARK_DINER.options.BUY_LET.description", "加入牌组：[gold]流血[/gold]层数翻倍。"},
                    {"DARK_DINER.options.BUY_DEBT.title", "点血债（30点）"},
                    {"DARK_DINER.options.BUY_DEBT.description", "加入牌组：按目标[gold]流血[/gold]层数增伤。"},
                    {"DARK_DINER.options.BUY_BLADES.title", "点千刀（30点）"},
                    {"DARK_DINER.options.BUY_BLADES.description", "加入牌组：本场每次[gold]流血[/gold]触发都让它更痛。"},
                    {"DARK_DINER.options.BUY_DETONATE.title", "点引爆（30点）"},
                    {"DARK_DINER.options.BUY_DETONATE.description", "加入牌组：消耗目标全部[gold]流血[/gold]，造成层数×2 伤害。"},
                    {"DARK_DINER.options.BUY_FEAST.title", "点血宴（30点）"},
                    {"DARK_DINER.options.BUY_FEAST.description", "加入牌组：按目标[gold]流血[/gold]层数回复生命。"},
                    {"DARK_DINER.options.BUY_MIST.title", "点血雾（30点）"},
                    {"DARK_DINER.options.BUY_MIST.description", "加入牌组：对全体敌人施加[gold]流血[/gold]。"},
                    {"DARK_DINER.options.BUY_DEEPEN.title", "点越割越深（30点）"},
                    {"DARK_DINER.options.BUY_DEEPEN.description", "加入牌组：本场施加的[gold]流血[/gold]额外+50%（可叠加）。"}
                };
                case "cards": return new Dictionary<string, string>
                {
                    {"mechanic_note.title", "机制说明"},
                    {"slitthroat.title", "割喉"},
                    {"chew.title", "咀嚼"},
                    {"chew.description", "造成{Damage}点伤害，获得{Pulse}点[gold]搏动[/gold]。"},
                    {"tangled_tongue.title", "缠舌"},
                    {"tangled_tongue.description", "施加{Weak}层虚弱并抽{Draw}张牌。"},
                    {"cleanup.title", "收拾残局"},
                    {"cleanup.description", "获得{Block}点格挡与{Pulse}点[gold]搏动[/gold]。"},
                    {"echo_of_blood.title", "血之回响"},
                    {"echo_of_blood.description", "抽1张牌；本场每触发4次[gold]流血[/gold]额外抽1张（最多+{Extra}）。"},
                    {"snarl.title", "龇牙"},
                    {"snarl.description", "施加{Weak}层虚弱与{Vuln}层易伤，获得{Pulse}点[gold]搏动[/gold]。"},
                    {"tear.title", "撕扯"},
                    {"tear.description", "造成{Damage}点伤害；若目标有易伤，额外施加3层[gold]流血[/gold]。"},
                    {"blood_feast.title", "血宴"},
                    {"blood_feast.description", "按目标[gold]流血[/gold]层数回复生命（{Base} + 层数/3，最多{Max}）。"},
                    {"hunger_echo.title", "饥饿共鸣"},
                    {"hunger_echo.description", "获得{Pulse}点[gold]搏动[/gold]。"},
                    {"feast_eve.title", "暴食前夕"},
                    {"feast_eve.description", "[gold]饥饿[/gold]+{Hunger}。"},
                    {"vile_brand.title", "卑贱烙印"},
                    {"vile_brand.description", "造成{Damage}点伤害，获得{Vile}层【[gold]卑贱之物[/gold]】。"},
                    {"vile_whisper.title", "卑贱低语"},
                    {"vile_whisper.description", "获得{Block}点格挡，获得{Vile}层【[gold]卑贱之物[/gold]】。"},
                    {"vile_offering.title", "卑贱献祭"},
                    {"vile_offering.description", "失去{Hp}点生命，获得{Vile}层【[gold]卑贱之物[/gold]】。"},
                    {"vile_loop.title", "卑贱循环"},
                    {"vile_loop.description", "获得 1 层【卑贱循环】：每回合开始获得 2 层[gold]卑贱之物[/gold]（升级 3）。"},
                    {"vile_reckoning.title", "卑贱清算"},
                    {"vile_reckoning.description", "消耗全部【[gold]卑贱之物[/gold]】，每层造成{Per}点伤害。"},
                    {"vile_end.title", "卑贱终末"},
                    {"vile_end.description", "获得{Vile}层【[gold]卑贱之物[/gold]】，抽1张牌。"},
                    {"hunger_pact.title", "饥饿契约"},
                    {"hunger_pact.description", "获得 1 层【[gold]饥饿[/gold]契约】：每回合开始额外获得 3 点饥饿（升级 5）。"},
                    {"glutton_instinct.title", "暴食本能"},
                    {"glutton_instinct.description", "获得 1 层【暴食本能】：红神形态每回合开始获得 2 点[gold]自助餐[/gold]（升级 3）。"},
                    {"bleed_instinct.title", "血之本能"},
                    {"bleed_instinct.description", "获得 1 层【血之本能】：每层让你施加的[gold]流血[/gold]额外 +1（升级 2 层）。"},
                    {"hunger_rhythm.title", "饥饿律动"},
                    {"hunger_rhythm.description", "获得 1 层【[gold]饥饿[/gold]律动】：每回合开始获得 3 点[gold]搏动[/gold]（升级 4）。"},
                    {"vile_resonance.title", "卑贱共鸣"},
                    {"vile_resonance.description", "获得 1 层【卑贱共鸣】：每张卑贱卡额外 +1 层（升级 2）。"},
                    {"feast_fork.title", "血叉"},
                    {"feast_fork.description", "造成{Damage}点伤害，施加{Bleed}层[gold]流血[/gold]；\n回复 3 点生命，另有等同于流血层数的治疗。"},
                    {"reap_scythe.title", "收割之镰"},
                    {"reap_scythe.description", "消耗全部[gold]收割[/gold]值，造成其一半数值的伤害（至少4，最多60）。"},
                    {"blood_rain.title", "血雨"},
                    {"blood_rain.description", "对所有敌人施加{Bleed}层[gold]流血[/gold]。"},
                    {"blood_vessel.title", "血之容器"},
                    {"blood_vessel.description", "获得 1 层【血之容器】：[gold]流血[/gold]上限 +10（升级 +15）。"},
                    {"blood_echo.title", "血之回响"},
                    {"blood_echo.description", "获得 1 层【血之回响】：每回合第一次施加[gold]流血[/gold]时，抽 1 张牌。"},
                    {"feast_finale.title", "万灵盛宴"},
                    {"feast_finale.description", "对所有敌人造成 {Damage} 点伤害（+[gold]自助餐[/gold]/2 + 每次召唤加成×2）。\n每给【[gold]血肉[/gold]】加成一次，本牌费用 -1（最低 1）。"},
                    {"feast_call.title", "开席"},
                    {"feast_call.description", "撕下一块[gold]血肉[/gold]（失去 2 点生命），召唤【血肉】（生命 = {Base} + [gold]自助餐[/gold]/2，上限 20）。"},
                    {"feed_guest.title", "喂食"},
                    {"feed_guest.description", "【[gold]血肉[/gold]】获得 {Hp} 点最大生命与等量生命；你获得 2 点[gold]自助餐[/gold]。"},
                    {"dine_together.title", "共餐"},
                    {"dine_together.description", "【[gold]血肉[/gold]】发动一次攻击（伤害 = 其当前生命的一半）；你回复伤害的一半。"},
                    {"test_osty_summon.title", "测试召唤"},
                    {"test_osty_summon.description", "调试用：尝试召唤 Osty。"},
                    {"dodge_instinct.title", "疾影"},
                    {"dodge_instinct.description", "获得 {Pct}% 闪避概率（本场战斗）。"},
                    {"red_god_heart.title", "红神之心"},
                    {"red_god_heart.description", "【红神之心】[gold]血肉[/gold]进化为红神[gold]眷属[/gold]：每回合自动攻击（最大生命 1/3），死亡时对全体造成最大生命一半伤害。"},
                    {"blood_resonance.title", "血神共鸣"},
                    {"blood_resonance.description", "获得 1 层【血神共鸣】：每回合开始[gold]血肉[/gold]追加一次攻击（最大生命 1/5）。"},
                    {"giant_growth.title", "巨化"},
                    {"giant_growth.description", "[gold]血肉[/gold]最大生命 +6，并回复 3 点生命。"},
                    {"leftover_use.title", "残渣利用"},
                    {"leftover_use.description", "获得 1 层【[gold]残渣[/gold]利用】：[gold]血肉[/gold]死亡时回复 4 点生命、获得 3 [gold]自助餐[/gold]。"},
                    {"dismember.title", "断肢"},
                    {"dismember.description", "[gold]血肉[/gold]失去 4 点生命（至少留 1），抽 2 张牌；若因此死亡则抽 3 张。"},
                    {"flesh_pounce.title", "扑击"},
                    {"flesh_pounce.description", "[gold]血肉[/gold]失去至多 8 点生命，对目标造成失去量 2 倍的伤害。"},
                    {"relentless_bite.title", "连咬"},
                    {"relentless_bite.description", "造成两次伤害，每次为[gold]血肉[/gold]最大生命的 1/5（至少 3）。"},
                    {"blood_claw.title", "血爪"},
                    {"blood_claw.description", "造成 5 点伤害，附加[gold]流血[/gold]，数值为[gold]血肉[/gold]最大生命的 1/4。"},
                    {"command_guest.title", "号令"},
                    {"command_guest.description", "获得 1 层【号令】：每回合开始[gold]血肉[/gold]自动攻击一次。"},
                    {"dodge_step.title", "侧步"},
                    {"dodge_step.description", "本场战斗获得 5% 闪避概率；抽 1 张牌。"},
                    {"shadow_cloak.title", "残影斗篷"},
                    {"shadow_cloak.description", "闪避概率 +5%。"},
                    {"blood_tide.title", "血潮"},
                    {"blood_tide.description", "每当你造成攻击伤害时，对目标施加等量[gold]流血[/gold]（每层+100%）。"},
                    {"unbound_bite.title", "失控·撕咬"},
                    {"unbound_bite.description", "造成 {Damage} 点伤害，失去 3 点生命。"},
                    {"unbound_gluttony.title", "失控·贪食"},
                    {"unbound_gluttony.description", "造成 {Damage} 点伤害，失去 2 点生命；击杀时回复 8 点生命。"},
                    {"unbound_feast.title", "失控·盛宴"},
                    {"unbound_feast.description", "回复 12 点生命，失去 3 点生命。"},
                    {"unbound_roar.title", "失控·咆哮"},
                    {"unbound_roar.description", "对全体敌人造成 {Damage} 点伤害并施加 2 层[gold]卑贱之物[/gold]；失去 3 点生命。"},
                    {"multiply_guest.title", "增殖"},
                    {"multiply_guest.description", "若【[gold]血肉[/gold]】在场：其最大生命 +2 并回复 2，你获得 3 点[gold]自助餐[/gold]。\n否则撕下一块血肉（失去 2 点生命）召唤它。"},
                    {"protect_guest.title", "护食"},
                    {"protect_guest.description", "获得 {Block} 点格挡；【[gold]血肉[/gold]】回复 3 点生命。"},
                    {"devour_guest.title", "暴食"},
                    {"devour_guest.description", "吃掉【[gold]血肉[/gold]】：获得其生命值的[gold]自助餐[/gold]（至少 4），失去 3 点生命。"},
                    {"lullaby_guest.title", "哄睡"},
                    {"lullaby_guest.description", "【[gold]血肉[/gold]】回复至满血；抽 1 张牌。"},
                    {"sacrifice_guest.title", "献祭"},
                    {"sacrifice_guest.description", "[gold]献祭[/gold]【[gold]血肉[/gold]】：对全体敌人造成 8 点伤害，回复 6 点生命。"},
                    {"blood_repay.title", "血偿"},
                    {"blood_repay.description", "获得等同于目标[gold]流血[/gold]层数的格挡（至少3，最多{Max}）。"},
                    {"blood_spike.title", "血刺"},
                    {"blood_spike.description", "造成{Damage}点伤害；若目标有[gold]流血[/gold]，额外+{Bonus}伤害。"},
                    {"coagulate.title", "凝血"},
                    {"coagulate.description", "获得{Block}点格挡；若目标有[gold]流血[/gold]，额外+{Bonus}格挡。"},
                    {"blood_pact.title", "血契"},
                    {"blood_pact.description", "失去{Hp}点生命，对目标施加{Bleed}层[gold]流血[/gold]。"},
                    {"blood_surge.title", "血潮"},
                    {"blood_surge.description", "造成目标[gold]流血[/gold]层数×{Mult}的伤害（不消耗层数）。"},
                    {"blood_mist.title", "血雾"},
                    {"blood_mist.description", "对所有敌人施加{Bleed}层[gold]流血[/gold]。"},
                    {"deepen.title", "越割越深"},
                    {"deepen.description", "本场战斗施加的[gold]流血[/gold]额外+{Percent}%（可叠加）。"},
                    {"blood_thirst_power.title", "越割越深"},
                    {"blood_thirst_power.description", "每层使施加的[gold]流血[/gold]额外+50%。"},
                    {"summon_guest.title", "招来食客"},
                    {"summon_guest.description", "召唤一个食客替你挨打：获得12点食客生命（上限30）。\n食客每回合开始咬敌人3点（开饭铃+4）。"},
                    {"second_helping.title", "加餐"},
                    {"second_helping.description", "给食客补6点生命。"},
                    {"grand_banquet.title", "群宴"},
                    {"grand_banquet.description", "一次叫出更多食客：获得24点食客生命。"},
                    {"thousand_blades.title", "千刀"},
                    {"thousand_blades.description", "造成{Damage}点伤害；本场每触发过一次[gold]流血[/gold]，额外+1（最多+{Max}）。"},
                    {"detonate.title", "引爆"},
                    {"detonate.description", "消耗目标全部[gold]流血[/gold]层数，造成层数×{Mult}的伤害。"},
                    {"slit_throat.title", "割喉"},
                    {"slit_throat.description", "造成{Damage}点伤害，施加{Bleed}层[gold]流血[/gold]。"},
                    {"bloodlet.title", "放血"},
                    {"blood_let.title", "放血"},
                    {"blood_let.description", "使目标[gold]流血[/gold]层数翻倍（最多+{Max}，受上限约束）。"},
                    {"blooddebt.title", "血债"},
                    {"blood_debt.title", "血债"},
                    {"blood_debt.description", "造成{Damage}点伤害，目标每{Per}层[gold]流血[/gold]额外+{Bonus}伤害。"},
                    {"suture.title", "缝合"},
                    {"suture.description", "回复{Heal}点生命，获得{Pulse}点[gold]搏动[/gold]。"},
                    {"siphon.title", "啜饮"},
                    {"siphon.description", "造成{Damage}点伤害，回复所造成伤害的一半生命。\\n获得{Pulse}点[gold]搏动[/gold]。"},
                    {"drive.title", "驱力"},
                    {"forage.title", "抄掠"},
                    {"forage.description", "造成{Damage}点伤害。\n若目标有易伤：抽1张牌，获得4点[gold]搏动[/gold]；否则获得2点搏动。"},
                    {"brace.title", "蓄势"},
                    {"brace.description", "获得{Block}点格挡，每点[gold]搏动[/gold]额外+2格挡（最多+18）。"},
                    {"strength_offering.title", "力量献祭"},{"strengthoffering.title", "力量献祭"},
                    {"strength_offering.description", "消耗至多3点力量：每点换6[gold]搏动[/gold]与2[gold]自助餐[/gold]，并获得{Block}点格挡。\n没有力量时只获得3搏动。"},
                    {"greedy_search.title", "贪婪翻找"},{"greedysearch.title", "贪婪翻找"},
                    {"greedy_search.description", "抽2张牌，每抽到1张获得2点[gold]搏动[/gold]；获得{Block}点格挡。"},
                    {"drive.description", "造成{Damage}点伤害，获得{Pulse}点[gold]搏动[/gold]（暴击时 +2）。\n[gold]空放[/gold]：获得{Block}点格挡与{PulseSupport}点搏动。"},
                    {"drive_guard.title", "驱力·援护"},
                    {"drive_guard.description", "给予{Block}点格挡，获得6点[gold]搏动[/gold]；对敌使用时造成{Damage}点伤害。"},
                    {"nourish.title", "滋养"},
                    {"nourish.description", "回复{Heal}点生命，获得{Pulse}点[gold]搏动[/gold]。"},
                    {"sanguine_debt.title", "血偿"},
                    {"sanguinedebt.title", "血偿"},
                    {"sanguine_debt.description", "造成{Damage}点伤害，回复所造成伤害的一半。"},
                    {"sanguinedebt.description", "造成{Damage}点伤害，回复所造成伤害的一半。"},
                    {"feastfork.title", "盛宴刀叉"},
                    {"feastfork.description", "造成{Damage}点伤害，施加6层[gold]流血[/gold]。\n按目标流血层数额外回血。"},
                    {"blood_bank.title", "蓄血"},
                    {"bloodbank.title", "蓄血"},
                    {"blood_bank.description", "获得4点[gold]搏动[/gold]。\n永久提升1点最大生命。"},
                    {"bloodbank.description", "获得{Pulse}点[gold]搏动[/gold]。\n永久提升{MaxHp}点最大生命。"},
                    {"glutton_rite.title", "饱食仪典"},
                    {"gluttonrite.title", "饱食仪典"},
                    {"glutton_rite.description", "本场战斗盛宴+{Feast}。\n获得{Pulse}点[gold]搏动[/gold]。"},
                    {"gluttonrite.description", "本场战斗盛宴+4。\n获得2点[gold]搏动[/gold]。"},
                    {"reap_rite.title", "丰收仪典"},
                    {"reaprite.title", "丰收仪典"},
                    {"reap_rite.description", "获得{Reap}点[gold]收割[/gold]。\n抽{Draw}张牌。"},
                    {"reaprite.description", "获得30点[gold]收割[/gold]。\n抽1张牌。"},
                    {"thorn_bloom.title", "荆棘绽放"},
                    {"thornbloom.title", "荆棘绽放"},
                    {"thorn_bloom.description", "造成{Damage}点伤害。\n施加6层[gold]流血[/gold]。"},
                    {"thornbloom.description", "造成{Damage}点伤害。\n施加6层[gold]流血[/gold]。"},
                    {"hunger_feast.title", "饕餮（作弊卡）"},
                    {"hunger_feast.description", "获得100点[gold]饥饿[/gold]。"},
                    {"pulse_strike.title", "搏动打击"},
                    {"pulse_strike.description", "造成{Damage}点伤害；消耗最多6点[gold]搏动[/gold]，每点额外+2伤害。\n[gold]空放[/gold]：获得{Block}点格挡与6点搏动。"},
                    {"escape_cover.title", "逃离·掩护"},
                    {"escape_cover.description", "获得{Block}点格挡，获得4点[gold]搏动[/gold]。"},
                    {"escape.title", "逃离"},
                    {"escape.description", "造成{Damage}点伤害，命中时施加{Bleed}层[gold]流血[/gold]与1层易伤。\n[gold]空放[/gold]：获得{Block}点格挡与3点[gold]搏动[/gold]。"},
                    {"pulse_aid.title", "搏动"},
                    {"pulse_aid.description", "造成{Damage}点伤害；消耗最多6点[gold]搏动[/gold]，每点额外+2伤害。\n[gold]空放[/gold]（拖到空白/自己）：获得{Block}点格挡与6点搏动。"},
                    {"prepare.title", "预备"},
                    {"prepare.description", "获得3点[gold]搏动[/gold]，抽2张牌。"},
                    {"childhood_end.title", "童年的终结"},
                    {"childhood_end.description", "0费。[gold]饥饿[/gold]满100时自动获得一张，饥饿满即可使用。\n变身【红神】：[gold]搏动[/gold]与[gold]替身[/gold]转为[gold]自助餐[/gold]。\n使用后能量补满，手牌替换为红神牌。"},
                    {"gluttony.title", "贪食"},
                    {"gluttony.description", "造成{Damage}点伤害，消耗[gold]自助餐[/gold]增伤。\n命中：8层[gold]流血[/gold]，回复6点生命。\n击杀：+8自助餐。"},
                    {"the_missing.title", "存在的缺失"},
                    {"the_missing.description", "对所有敌人造成{Damage}点伤害。\n消耗[gold]自助餐[/gold]增伤。\n命中：6层[gold]流血[/gold]，获得3点力量。"},
                    {"flesh_rend.title", "血肉撕裂"},
                    {"flesh_rend.description", "造成{Damage}点伤害（两段），每段回复2点生命。"},
                    {"flesh_wall.title", "血肉壁障"},
                    {"flesh_wall.description", "获得{Block}点格挡，回复3点生命，给予4层[gold]流血[/gold]。"},
                    {"balance_blade.title", "均衡之刃"},
                    {"balance_blade.description", "造成{Damage}点伤害，按[gold]自助餐[/gold]与攻防计数增伤。"},
                    {"harvest.title", "收获"},
                    {"harvest.description", "【大招】[gold]收割[/gold]值满100才可打出。\n战：{Damage}点×5段；守：格挡转全体伤害；均：全体3轮。\n击杀：返还15[gold]自助餐[/gold]。\n打出后结束回合会回落幼年：全体15伤害并回复10生命。"}
                };
                case "characters": return new Dictionary<string, string>
                {
                    {"zhiji.title", "吱咿"},
                    {"zhiji.description", "[gold]饥饿[/gold]的戏服演员。\n常态积攒[gold]搏动[/gold]与饥饿，满足后觉醒为红神；\n也可披上寿衣，以[gold]流血[/gold]为主轴。"},
                    {"zhiji.titleObject", "吱咿"},
                    {"zhiji.pronounObject", "她"},
                    {"zhiji.pronounSubject", "她"},
                    {"zhiji.pronounPossessive", "她的"},
                    {"zhiji.possessiveAdjective", "她的"},
                    {"zhiji.cardsModifierTitle", "无休止饥饿"},
                    {"zhiji.cardsModifierDescription", "在常态与红神之间觉醒的饥饿戏服。"},
                    {"zhiji.bestiaryQuote", "……饿。"},
                    {"zhiji.eventDeathPrevention", "戏服还没演完。"}
                };
                case "relics": return new Dictionary<string, string>
                {
                    {"vile_purse.title", "卑贱钱袋"},
                    {"vile_purse.description", "战斗第一回合开始时，获得 1 层【[gold]卑贱之物[/gold]】。"},
                    {"hunger_bell.title", "饿钟"},
                    {"hunger_bell.description", "每回合开始时，[gold]饥饿[/gold] +1。"},
                    {"vile_crown.title", "卑贱王冠"},
                    {"vile_crown.description", "每场战斗开始时，获得 2 层【[gold]卑贱之物[/gold]】。"},
                    {"vile_watch.title", "卑贱怀表"},
                    {"vile_watch.description", "每回合第一张施加【[gold]卑贱之物[/gold]】的牌，额外 +1 层。"},
                    {"blood_funnel.title", "血之漏斗"},
                    {"blood_funnel.description", "每回合第一张施加[gold]流血[/gold]的牌，额外 +1 层。"},
                    {"black_napkin.title", "漆黑餐巾"},
                    {"black_napkin.description", "战斗第一回合开始时，对所有敌人施加 2 层[gold]流血[/gold]。"},
                    {"stigmata.title", "猩红圣痕"},
                    {"stigmata.description", "红神形态：每回合开始获得 1 点[gold]自助餐[/gold]。"},
                    {"sated_stomach.title", "饱食之胃"},
                    {"sated_stomach.description", "每回合第一次失去生命时，回复该次伤害的一半。"},
                    {"unbound_heart.title", "失控之心"},
                    {"unbound_heart.description", "永久红神形态，本局不再回落。\n牌组中的攻击/技能牌附魔【[gold]失控[/gold]】：伤害+50%，打出后反伤。"},
                    {"old_scythe.title", "旧镰刀"},
                    {"old_scythe.description", "每场战斗开始时，获得 10 点[gold]收割[/gold]值。"},
                    {"ration_bag.title", "干粮袋"},
                    {"ration_bag.description", "战斗开始时：[gold]饥饿[/gold] -20，回复 4 点生命。"},
                    {"war_sigil.title", "战纹章"},
                    {"war_sigil.description", "红神形态：每回合开始获得 2 点[gold]自助餐[/gold]。"},
                    {"guard_sigil.title", "守纹章"},
                    {"guard_sigil.description", "红神形态：每回合开始获得 3 点格挡。"},
                    {"crimson_visor.title", "猩红面甲"},
                    {"crimson_visor.description", "变身时额外获得 10 点[gold]自助餐[/gold]。"},
                    {"blood_hourglass.title", "血之沙漏"},
                    {"blood_hourglass.description", "战斗第一回合开始时，对所有敌人施加 2 层[gold]流血[/gold]。"},
                    {"blood_nail.title", "血契之钉"},
                    {"blood_nail.description", "每场战斗第一次施加[gold]流血[/gold]时，额外 +2 层。"},
                    {"crimson_cradle.title", "猩红摇篮"},
                    {"crimson_cradle.description", "每场战斗开始获得 3 点[gold]搏动[/gold]；红神形态每回合开始获得 2 点格挡。"},
                    {"dinner_bell.title", "开饭铃"},
                    {"dinner_bell.description", "召唤物的生命上限+10、每回合攻击+4、每次替你挡伤最多12点。"},
                    {"diner_guest_power.title", "食客"},
                    {"diner_guest_power.description", "层数=剩余生命。敌人攻击时优先替你承受（每次最多8点）。每回合开始咬敌人。"},
                    {"bleed_board.title", "血肉砧板"},
                    {"bleed_board.description", "每次施加[gold]流血[/gold]额外 +2，流血上限提高到 90。\n引爆伤害提高 50%。"},
                    {"golden_shroud.title", "黄金寿衣"},
                    {"golden_shroud.description", "永远保持幼年形态，封印无休止[gold]饥饿[/gold]与变身。\n[gold]流血[/gold]无层数上限，且叠流血速度提高50%。"},
                    {"flesh_stilts.title", "血肉高跷"},
                    {"flesh_stilts.description", "每回合开始获得1点[gold]搏动[/gold]。\n变身时额外获得8点[gold]自助餐[/gold]。"},
                    {"flesh_stilts.flavor", "踩着别人的骨头，才能看得更远。"},
                    {"blood_goblet.title", "血玉酒杯"},
                    {"blood_goblet.description", "敌人身上有[gold]流血[/gold]时，每回合开始回复 2 点生命。"},
                    {"carving_knife.title", "餐刀"},
                    {"carving_knife.description", "每回合开始回复 1 点生命。"},
                    {"endless_plate.title", "无尽餐盘"},
                    {"endless_plate.description", "变身时额外获得 6 点[gold]自助餐[/gold]。"},
                    {"crimson_stage.title", "血色舞台"},
                    {"crimson_stage.description", "[gold]自助餐[/gold]增伤+1且无消耗上限。\n变身后每回合结束失去4点生命。"},
                    {"crimson_stage.flavor", "幕布落下之前，没有人可以退场。"}
                };
                case "zhiji_powers": return new Dictionary<string, string>
                {
                    {"die_for_you.description", "[gold]血肉[/gold]会吸收所有未被格挡的攻击伤害。"}
                };
                case "enchantments": return new Dictionary<string, string>
                {
                    {"unbound_rage.title", "失控"},
                    {"unbound_rage.description", "这张牌造成的伤害提高 50%；打出后失去等同其伤害一半的生命（最多10）。"},
                    {"unbound_rage.extraCardText", "失控：伤害+50%，打出后反伤"}
                };
                case "powers": return new Dictionary<string, string>
                {
                    {"die_for_you.description", "[gold]血肉[/gold]会吸收所有未被格挡的攻击伤害。"},
                    {"die_for_you.title", "为你而死"},
                    {"bleed_cap_power.title", "血之容器"},
                    {"bleed_cap_power.description", "[gold]流血[/gold]上限提高等同于层数的数值。"},
                    {"blood_echo_power.title", "血之回响"},
                    {"blood_echo_power.description", "每回合第一次施加[gold]流血[/gold]时，抽 1 张牌。"},
                    {"hunger_pact_power.title", "饥饿契约"},
                    {"hunger_pact_power.description", "每回合开始时，额外获得等同于层数的[gold]饥饿[/gold]。"},
                    {"glutton_instinct_power.title", "暴食本能"},
                    {"glutton_instinct_power.description", "红神形态下，每回合开始时获得等同于层数的[gold]自助餐[/gold]。"},
                    {"bleed_instinct_power.title", "血之本能"},
                    {"bleed_instinct_power.description", "每层使你施加的[gold]流血[/gold]额外 +1 层。"},
                    {"hunger_rhythm_power.title", "饥饿律动"},
                    {"hunger_rhythm_power.description", "每回合开始时，获得等同于层数的[gold]搏动[/gold]。"},
                    {"vile_loop_power.title", "卑贱循环"},
                    {"vile_loop_power.description", "每回合开始时，获得等同于层数的[gold]卑贱之物[/gold]。"},
                    {"vile_resonance_power.title", "卑贱共鸣"},
                    {"vile_resonance_power.description", "每张施加【[gold]卑贱之物[/gold]】的牌，额外 +层数 层。"},
                    {"reap_power.title", "收割"},
                    {"reap_power.description", "[gold]收割[/gold]值：用于终结技等结算的储备资源。"},
                    {"vile_thing_power.title", "卑贱之物"},
                    {"vile_thing_power.description", "叠满 10 层时，自动获得一个额外回合。\n（连动：这回合结束后仍是你的回合，敌人不会行动）"},
                    {"bleed_power.title", "流血"},
                    {"bleed_power.description", "回合开始时失去等同层数的生命。"},
                    {"pulse_power.title", "搏动"},
                    {"pulse_power.description", "每4点[gold]搏动[/gold]转化为1点无休止[gold]饥饿[/gold]。搏动使骰点最多+4（1d12，伤害12%~150%）。"},
                    {"hunger_power.title", "无休止饥饿"},
                    {"hunger_power.description", "达到100点解锁【童年的终结】。"},
                    {"feast_power.title", "自助餐"},
                    {"feast_power.description", "红神形态的资源。攻击时消耗换取伤害。"},
                    {"famine_power.title", "饥馑反噬"},
                    {"famine_power.description", "下回合开始时，因无休止[gold]饥饿[/gold]失去等同此数值的生命。"},
                    {"FAMINE_POWER.title", "饥馑反噬"},
                    {"FAMINE_POWER.description", "下回合开始时，因无休止[gold]饥饿[/gold]失去等同此数值的生命。"},
                    {"stand_power.title", "戏服替身"},
                    {"stand_power.description", "受到伤害时优先由戏服[gold]替身[/gold]承受；每层抵挡1点，但每次最多只挡该次伤害的50%。\n替身随[gold]搏动[/gold]增长，但只有搏动量的一半。"},
                    {"craving_power.title", "迫切饥饿"},
                    {"craving_power.description", "红神形态下每层使[gold]自助餐[/gold]增伤+1。"},
                    // 引擎按 Id 大写查找，补一份大写键
                    {"BLEED_POWER.title", "流血"},
                    {"BLEED_POWER.description", "回合开始时失去等同层数的生命。"},
                    {"PULSE_POWER.title", "搏动"},
                    {"PULSE_POWER.description", "每4点[gold]搏动[/gold]转化为1点无休止[gold]饥饿[/gold]。搏动使骰点最多+4（1d12，伤害12%~150%）。"},
                    {"HUNGER_POWER.title", "无休止饥饿"},
                    {"HUNGER_POWER.description", "达到100点解锁【童年的终结】。"},
                    {"FEAST_POWER.title", "自助餐"},
                    {"FEAST_POWER.description", "红神形态的资源。攻击时消耗换取伤害。"},
                    {"STAND_POWER.title", "戏服替身"},
                    {"STAND_POWER.description", "受到伤害时优先由戏服[gold]替身[/gold]承受；每层抵挡1点，但每次最多只挡该次伤害的50%。\n替身随[gold]搏动[/gold]增长，但只有搏动量的一半。"},
                    {"CRAVING_POWER.title", "迫切饥饿"},
                    {"CRAVING_POWER.description", "红神形态下每层使[gold]自助餐[/gold]增伤+1。"},
                    {"REAP_POWER.title", "收割"},
                    {"REAP_POWER.description", "红神形态的[gold]收割[/gold]计数，满值时获得【收获】。"}
                };
            }
            return null;
        }

    }
}

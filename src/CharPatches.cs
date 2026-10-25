using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

namespace Zhiji
{
    /// 让吱咿复用铁甲战士的资源路径（立绘/动画/图标），避免资源缺失崩溃
    public static class CharPatches
    {
        static bool Mine(object inst, ref string __result, string ironcladPath)
        {
            if (!(inst is Zhiji)) return true;
            __result = ironcladPath;
            return false;
        }

        public static bool VisualsPath(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("creature_visuals/ironclad"));

        public static bool IconPath(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("ui/character_icons/ironclad_icon"));

        public static bool SelectIcon(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, ImageHelper.GetImagePath("packed/character_select/char_select_ironclad.png"));

        public static bool SelectLockedIcon(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, ImageHelper.GetImagePath("packed/character_select/char_select_ironclad_locked.png"));

        public static bool SelectBg(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("screens/char_select/char_select_bg_ironclad"));

        public static bool EnergyCounter(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("combat/energy_counters/ironclad_energy_counter"));

        public static bool RestSite(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("rest_site/characters/ironclad_rest_site"));

        public static bool Merchant(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("merchant/characters/ironclad_merchant"));

        public static bool Transition(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, "res://materials/transitions/ironclad_transition_mat.tres");

        public static bool MapMarker(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, ImageHelper.GetImagePath("packed/map/icons/map_marker_ironclad.png"));

        public static bool Trail(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, SceneHelper.GetScenePath("vfx/card_trail_ironclad"));

        // 顶栏/局内头像：直接换成本体立绘纹理
        public static bool IconTexture(CharacterModel __instance, ref Texture2D __result)
        {
            if (!(__instance is Zhiji)) return true;
            var t = Art.Get("zhiji");
            if (t == null) return true;
            __result = t;
            return false;
        }

        // 地图上的玩家标记：换成吱咿头像
        public static void MapMarkerAfter(object __instance, MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            try
            {
                if (player == null || !(player.Character is Zhiji)) return;
                var tr = __instance as Godot.TextureRect;
                if (tr == null) return;
                var tex = Art.Get("map_marker");
                if (tex == null) { ZLog.W("map marker: no art"); return; }
                tr.Texture = tex;
                ZLog.W("map marker replaced");
            }
            catch (Exception e) { ZLog.W("map marker fail " + e.Message); }
        }

        public static bool TopIconTexture(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, ImageHelper.GetImagePath("ui/top_panel/character_icon_ironclad.png"));

        public static bool TopIconOutline(CharacterModel __instance, ref string __result)
            => Mine(__instance, ref __result, ImageHelper.GetImagePath("ui/top_panel/character_icon_ironclad_outline.png"));

        // 角色按钮初始化后强制解锁并换成铁甲战士图标
        public static void AfterButtonInit(object __instance, MegaCrit.Sts2.Core.Models.CharacterModel character)
        {
            if (!(character is Zhiji)) return;
            var inst = __instance;
            try
            {
                Godot.Callable.From(() => ApplySelectButton(inst)).CallDeferred();
            }
            catch (Exception e) { ZLog.W("button init defer fail " + e.Message); }
        }

        static void ApplySelectButton(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var t = __instance.GetType();
                var tex = Art.Get("zhiji");
                if (tex == null) { ZLog.W("select icon: no art"); return; }

                var iconField = t.GetField("_icon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var iconObj = iconField != null ? iconField.GetValue(__instance) : null;
                if (iconObj != null)
                {
                    var p = iconObj.GetType().GetProperty("Texture");
                    if (p != null) { p.SetValue(iconObj, tex); ZLog.W("select icon applied"); }
                }

                var locked = t.GetField("_isLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (locked != null) locked.SetValue(__instance, false);
                var lockField = t.GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var lockObj = lockField != null ? lockField.GetValue(__instance) : null;
                if (lockObj is CanvasItem ci) ci.Visible = false;
            }
            catch (Exception e) { ZLog.W("select icon fail " + e.Message); }
        }

        public static void AfterSelect(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var f = t.GetField("_character", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var ch = f != null ? f.GetValue(__instance) as MegaCrit.Sts2.Core.Models.CharacterModel : null;
                ZLog.W("button focus -> " + (ch != null ? ch.Id.Entry : "?") + (ch is Zhiji ? " (ZHIJI)" : ""));
            }
            catch (Exception e) { ZLog.W("after select fail " + e.Message); }
        }

        public static void AfterScreenSelect(object __instance, object charSelectButton, MegaCrit.Sts2.Core.Models.CharacterModel characterModel)
        {
            try
            {
                var t = __instance.GetType();
                string nl = "?";
                var nf = t.GetField("_name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var nobj = nf != null ? nf.GetValue(__instance) : null;
                if (nobj != null)
                {
                    var tp = nobj.GetType().GetProperty("Text");
                    if (tp != null) nl = System.Convert.ToString(tp.GetValue(nobj));
                }
                var eb = t.GetField("_embarkButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var eobj = eb != null ? eb.GetValue(__instance) : null;
                string dis = "?";
                if (eobj != null)
                {
                    var dp = eobj.GetType().GetProperty("Disabled");
                    if (dp != null) dis = System.Convert.ToString(dp.GetValue(eobj));
                }
                ZLog.W("SCREEN select " + (characterModel != null ? characterModel.Id.Entry : "?") + " name=" + nl + " embarkDisabled=" + dis);
                if (characterModel is Zhiji) ApplySelectBg(__instance);
            }
            catch (Exception e) { ZLog.W("screen select log fail " + e.Message); }
        }

        // 选人界面的背景大图：用我们自己的立绘替换铁甲战士的场景
        static void ApplySelectBg(object screen)
        {
            try
            {
                var t = screen.GetType();
                var bf = t.GetField("_bgContainer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var cont = bf != null ? bf.GetValue(screen) as Godot.Control : null;
                if (cont == null) { ZLog.W("no bg container"); return; }
                foreach (var c in cont.GetChildren())
                {
                    var ci = c as Godot.CanvasItem;
                    if (ci != null) ci.Visible = false;
                }
                var tex = Art.Get("zhiji_bg");
                if (tex == null) { ZLog.W("no bg art"); return; }
                var tr = new Godot.TextureRect();
                tr.Name = "ZhijiSelectBg";
                tr.Texture = tex;
                tr.ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize;
                tr.StretchMode = Godot.TextureRect.StretchModeEnum.KeepAspectCovered;
                tr.SetAnchorsPreset(Godot.Control.LayoutPreset.FullRect);
                tr.MouseFilter = Godot.Control.MouseFilterEnum.Ignore;
                cont.AddChild(tr);
                ZLog.W("select bg applied");
            }
            catch (Exception e) { ZLog.W("select bg fail " + e.Message); }
        }

        // 战斗中的小人：隐藏 Spine，换成我们的贴图
        static bool _visLogged;
        public static void AfterCreateVisuals(object character, object visuals)
        {
            // 不再挂到 creature 节点上（CreateVisuals 早于入树，时序不可靠）
            // 战斗小人改为独立 CanvasLayer 覆盖层，由战斗开始/结束事件控制
        }

        // 能力图标
        public static bool PowerIcon(object __instance, ref Godot.Texture2D __result)
        {
            try
            {
                string key = null;
                switch (__instance.GetType().Name)
                {
                    case "PulsePower": key = "icon_pulse"; break;
                    case "HungerPower": key = "icon_hunger"; break;
                    case "FeastPower": key = "icon_feast"; break;
                    case "StandPower": key = "icon_stand"; break;
                    case "CravingPower": key = "icon_craving"; break;
                    case "BleedPower": key = "icon_bleed"; break;
                    case "ReapPower": key = "icon_hunger"; break;
                    case "SanguineDebtPower": key = "icon_craving"; break;
                    case "BloodBankPower": key = "icon_stilts"; break;
                }
                if (key == null)
                {
                    // 我们自己的 Power 没单独配图时，统一用饥饿图标兜底，避免出现 NO PE 占位图
                    var tn = __instance.GetType().FullName ?? "";
                    if (tn.StartsWith("Zhiji.")) key = "icon_hunger";
                    else return true;
                }
                var t = Art.Get(key);
                if (t == null) return true;
                __result = t;
                return false;
            }
            catch { return true; }
        }

        public static bool RelicIcon(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref Godot.Texture2D __result)
        {
            try
            {
                if (__instance is VileThingPower)
                {
                    var tv = Art.Get("icon_vile");
                    if (tv == null) return true;
                    __result = tv; return false;
                }
                if (__instance is GoldenShroud)
                {
                    var tg = Art.Get("icon_shroud");
                    if (tg == null) return true;
                    __result = tg; return false;
                }
                if (__instance is CrimsonCradle)
                {
                    var tc = Art.Get("icon_cradle");
                    if (tc == null) return true;
                    __result = tc; return false;
                }
                if (__instance is CrimsonCradle)
                {
                    var tc = Art.Get("icon_cradle");
                    if (tc == null) return true;
                    __result = tc; return false;
                }
                if (!(__instance is FleshStilts) && !(__instance is CrimsonStage)) return true;
                var keyName = __instance is FleshStilts ? "icon_stilts" : "icon_stage";
                var t = Art.Get(keyName);
                ZLog.W("relic icon " + __instance.GetType().Name + " key=" + keyName + " tex=" + (t != null));
                if (t == null) return true;
                __result = t;
                return false;
            }
            catch { return true; }
        }

        // 遗物的动态描述：跳过原版的变量/图标前缀处理，避免选人界面 NRE
        public static bool RelicDynamicDesc(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref MegaCrit.Sts2.Core.Localization.LocString __result)
        {
            try
            {
                if (!(__instance is FleshStilts) && !(__instance is CrimsonStage)) return true;
                __result = new MegaCrit.Sts2.Core.Localization.LocString("relics", __instance.Id.Entry + ".description");
                return false;
            }
            catch { return true; }
        }

        // 遗物大图（详情页）
        public static bool RelicBigIcon(MegaCrit.Sts2.Core.Models.RelicModel __instance, ref Godot.Texture2D __result)
        {
            try
            {
                string key = null;
                if (__instance is FleshStilts) key = "icon_stilts_big";
                else if (__instance is CrimsonStage) key = "icon_stage_big";
                else if (__instance is CrimsonCradle) key = "icon_cradle_big";
                if (key == null)
                {
                    // 我们自己的 Power 没单独配图时，统一用饥饿图标兜底，避免出现 NO PE 占位图
                    var tn = __instance.GetType().FullName ?? "";
                    if (tn.StartsWith("Zhiji.")) key = "icon_hunger";
                    else return true;
                }
                var t = Art.Get(key);
                if (t == null) return true;
                __result = t;
                return false;
            }
            catch { return true; }
        }

        // 卡图：优先用 mod 自带贴图
        public static bool CardPortrait(CardModel __instance, ref Texture2D __result)
        {
            if (!(__instance is ZhijiCard)) return true;
            var t = (Texture2D)null;
            // 升级后的卡用"_up"版本卡面（金色调 + 发光），让升级有视觉反馈
            try { if (__instance.IsUpgraded) t = Art.Get(__instance.Id.Entry + "_up"); } catch { }
            if (t == null) t = Art.Get(__instance.Id.Entry);
            if (t == null)
            {
                // 镜像/衍生卡没有独立立绘时，退回主卡立绘，避免奖励界面拿到 null 纹理卡死
                switch (__instance.Id.Entry)
                {
                    case "DRIVE_GUARD": t = Art.Get("drive"); break;
                    case "PULSE_STRIKE": t = Art.Get("pulse_aid"); break;
                    case "ESCAPE_COVER": t = Art.Get("escape"); break;
                    case "HUNGER_FEAST": t = Art.Get("hunger_feast"); break;
                    case "BLOODBANK": t = Art.Get("suture"); break;
                    case "GLUTTONRITE": t = Art.Get("hunger_feast"); break;
                    case "REAPRITE": t = Art.Get("gluttony"); break;
                    case "THORNBLOOM": t = Art.Get("drive"); break;
                    case "NOURISH": t = Art.Get("suture"); break;
                    case "SANGUINEDEBT": t = Art.Get("siphon"); break;
                    case "FEASTFORK": t = Art.Get("hunger_feast"); break;
                    case "REND": case "FLESH_REND": t = Art.Get("gluttony"); break;
                    case "FLESHWALL": case "FLESH_WALL": t = Art.Get("suture"); break;
                    case "BALANCEBLADE": case "BALANCE_BLADE": t = Art.Get("drive"); break;
                    default: t = Art.Get("zhiji_idle"); break;
                }
            }
            if (t == null) return true;
            __result = t;
            return false;
        }
    }
}

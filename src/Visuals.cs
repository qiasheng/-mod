using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace Zhiji
{
    /// 战斗内的小人：用 GIF 拆帧做成 AnimatedSprite2D
    public static class Visuals
    {
        static AnimatedSprite2D _sprite;
        static bool _red;

        static bool IsUnboundMode()
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

        static string RedP(string kind) => (IsUnboundMode() ? "unbound_" : "red_") + kind;


        // 黄金寿衣：幼年立绘换成金色披肩版本（gold_* 帧，缺失则回退 kid_*）
        static string KidPrefix(string prefix)
        {
            try
            {
                if (string.IsNullOrEmpty(prefix)) return prefix;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return prefix;
                MegaCrit.Sts2.Core.Entities.Players.Player pl = null;
                try { var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance; var cs = cm != null ? cm.DebugOnlyGetState() : null; pl = cs != null && cs.Players != null && cs.Players.Count > 0 ? cs.Players[0] : null; } catch { }
                if (pl == null) { pl = MegaCrit.Sts2.Core.Runs.RunManager.Instance != null ? MegaCrit.Sts2.Core.Runs.RunManager.Instance.DebugOnlyGetState()?.Players?[0] : null; }
                if (pl == null || !ZhijiStore.Of(pl).GoldShroud) return prefix;
                string cand = prefix.StartsWith("kid_") ? "gold_" + prefix.Substring(4) : prefix;
                string dir = Art.Dir();
                if (dir == null) return prefix;
                string animDir = Path.Combine(dir, "anim");
                if (!Directory.Exists(animDir)) return prefix;
                return Directory.GetFiles(animDir, cand + "_*.png").Length > 0 ? cand : prefix;
            }
            catch { return prefix; }
        }

        static SpriteFrames Build(string prefix, float fps = 10f, float scale = 0.5f)
        {
            var sf = new SpriteFrames();
            string dir = Art.Dir();
            if (dir == null) return sf;
            string anim = Path.Combine(dir, "anim");
            if (!Directory.Exists(anim)) { ZLog.W("no anim dir"); return sf; }
            var firstSize = Vector2.Zero;
            var files = new List<string>(Directory.GetFiles(anim, prefix + "_*.png"));
            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0) { ZLog.W("no frames for " + prefix); return sf; }
            if (!sf.HasAnimation("default")) sf.AddAnimation("default");
            sf.SetAnimationLoop("default", true);
            sf.SetAnimationSpeed("default", fps);
            // 所有帧统一贴到 640x320 画布、底边对齐，避免不同动作帧尺寸不同导致立绘上下跳
            const int CW = 900, CH = 320;
            foreach (var f in files)
            {
                try
                {
                    using var img = new Image();
                    if (img.LoadPngFromBuffer(File.ReadAllBytes(f)) != Error.Ok) continue;
                    int w = img.GetWidth(), h = img.GetHeight();
                    float k = System.Math.Min(1f, System.Math.Min(CW / (float)w, CH / (float)h));
                    if (k < 1f)
                    {
                        img.Resize((int)(w * k), (int)(h * k), Image.Interpolation.Lanczos);
                        w = img.GetWidth(); h = img.GetHeight();
                    }
                    using var canvas = Image.CreateEmpty(CW, CH, false, Image.Format.Rgba8);
                    canvas.Fill(new Color(0, 0, 0, 0));
                    canvas.BlendRect(img, new Rect2I(0, 0, w, h), new Vector2I((CW - w) / 2, CH - h));
                    var tex = ImageTexture.CreateFromImage(canvas);
                    if (firstSize == Vector2.Zero) { firstSize = new Vector2(CW, CH); ZLog.W("frame size " + prefix + " " + firstSize); }
                    sf.AddFrame("default", tex);
                }
                catch { }
            }
            ZLog.W("frames " + prefix + " = " + files.Count);
            return sf;
        }

        // ==== 休息点 / 商人：用吱咿闲置动画替换铁甲战士模型 ====
        static readonly HashSet<ulong> _sceneCharsDone = new HashSet<ulong>();
        static readonly List<CanvasItem> _sceneSprites = new List<CanvasItem>();
        static int _sceneTick;

        // 进入房间时立刻刷新一次场景替身（避免先看到原版战士）
        public static bool SceneEnabled = true;
        public static bool SceneMerchantReplace = true;   // 商人立绘替换（排查货架不渲染时先关）
        public static void ForceSceneRefresh()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree != null) WalkScene(tree.Root);
            }
            catch { }
        }

        public static void TickSceneCharacters()
        {
            try
            {
                _sceneTick++;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                if (SceneEnabled && (_sceneTick & 3) == 1) WalkScene(tree.Root);   // 约每 1 秒扫一次替换

                // 地图/牌堆等全屏界面打开时，把场景替身收起来，避免"站在地图上"
                bool uiOnTop = false;
                try
                {
                    var ms = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
                    if (ms != null && GodotObject.IsInstanceValid(ms) && ms.Visible) uiOnTop = true;
                }
                catch { }
                if (PileScreenVisible(tree.Root, 0)) uiOnTop = true;
                if (ShopScreenVisible(tree.Root, 0)) uiOnTop = true;
                if (OverlayScreenVisible(tree.Root, 0)) uiOnTop = true;
                _sceneSprites.RemoveAll(sp => sp == null || !GodotObject.IsInstanceValid(sp));
                foreach (var sp in _sceneSprites) sp.Visible = !uiOnTop;
            }
            catch { }
        }

        // 各类全屏界面（升级/选牌/看牌/牌堆/地图/商店）出现时都要把场景替身藏起来
        static readonly string[] _fullScreenNames = new[]
        {
            "NDeckUpgradeSelectScreen", "NCardUpgradeScreen",
            "NCardGridSelectionScreen", "NCardRewardSelectionScreen",
            "NChooseACardSelectionScreen", "NSimpleCardSelectScreen",
            "NDeckCardSelectScreen", "NCombatPileCardSelectScreen",
            "NCardsViewScreen", "NSimpleCardsViewScreen", "NInspectCardScreen",
            "NCardPileScreen", "NDeckViewScreen", "NCardGridScreen"
        };

        static bool OverlayScreenVisible(Node n, int depth)
        {
            if (n == null || depth > 16) return false;
            try
            {
                if (n is CanvasItem ci && ci.Visible)
                {
                    var tn = n.GetType().Name;
                    foreach (var want in _fullScreenNames)
                        if (tn == want) return true;
                }
            }
            catch { }
            foreach (var c in n.GetChildren())
                if (OverlayScreenVisible(c, depth + 1)) return true;
            return false;
        }

        static bool ShopScreenVisible(Node n, int depth)
        {
            if (n == null || depth > 14) return false;
            try
            {
                var tn = n.GetType().Name;
                if (tn.Contains("MerchantInventory") && n is CanvasItem ci && ci.Visible) return true;
            }
            catch { }
            foreach (var c in n.GetChildren())
                if (ShopScreenVisible(c, depth + 1)) return true;
            return false;
        }

        static void WalkScene(Node n)
        {
            if (n == null) return;
            string tn = n.GetType().Name;
            if (tn == "NCreature") TryReplacePlayerCreature(n);
            if (tn == "NRestSiteCharacter" || (tn == "NMerchantCharacter" && SceneMerchantReplace))
            {
                ulong id = (ulong)n.GetInstanceId();
                if (!_sceneCharsDone.Contains(id))
                {
                    _sceneCharsDone.Add(id);
                    ApplySceneCharacter(n, tn == "NMerchantCharacter");
                }
            }
            foreach (var c in n.GetChildren()) WalkScene(c);
        }

        static readonly HashSet<ulong> _playerCreatureDone = new HashSet<ulong>();

        // 事件/结局等非战斗场景里的玩家 creature：换成吱咿闲置动画
        static void TryReplacePlayerCreature(Node n)
        {
            try
            {
                var nc = n as MegaCrit.Sts2.Core.Nodes.Combat.NCreature;
                if (nc == null) return;
                ulong id = (ulong)nc.GetInstanceId();
                if (_playerCreatureDone.Contains(id)) return;

                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                if (rs == null) return;
                var room = rs.CurrentRoom;
                string roomName = room != null ? room.GetType().Name : "";
                if (roomName == "CombatRoom") return;          // 战斗里由战斗覆盖层负责

                var pc = (rs.Players != null && rs.Players.Count > 0 && rs.Players[0] != null) ? rs.Players[0].Creature : null;
                if (pc == null || nc.Entity != pc) return;

                _playerCreatureDone.Add(id);

                try { if (nc.Visuals != null) nc.Visuals.Visible = false; } catch { }
                HideSpineRecursive(nc, 0);

                var f = Frames(KidPrefix("kid_idle"));
                if (f == null || f.GetFrameCount("default") == 0) return;
                var spr = new AnimatedSprite2D();
                spr.Name = "ZhijiEventChar";
                spr.SpriteFrames = f;
                spr.Centered = true;
                float frameH = 320f;
                var treeVp = (Engine.GetMainLoop() as SceneTree);
                var vpS = treeVp != null ? treeVp.Root.GetVisibleRect().Size : new Vector2(1920, 1080);
                float targetH = vpS.Y * 0.30f;      // 事件场景里按屏高比例，避免 Control 尺寸为 0
                float sc = targetH / frameH;
                spr.Scale = new Vector2(sc, sc);
                spr.Position = new Vector2(nc.Size.X * 0.5f, nc.Size.Y * 0.72f);
                spr.ZIndex = -2;   // 事件/商店 UI 之下，避免立绘盖住商店页面
                nc.AddChild(spr);
                spr.Play("default");
                _sceneSprites.Add(spr);
                ZLog.W("event char replaced: " + roomName + " size=" + nc.Size.X + "x" + nc.Size.Y);
            }
            catch (Exception e) { ZLog.W("event char fail " + e.Message); }
        }

        static void ApplySceneCharacter(Node node, bool merchant)
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                var vp = tree != null ? tree.Root.GetVisibleRect().Size : new Vector2(1920, 1080);

                foreach (var c in node.GetChildren())
                {
                    string ct = c.GetType().Name;
                    if (c is CanvasItem ci2 && !(c is Control) && (c is Node2D || ct.Contains("Spine") || ct.Contains("Sprite")))
                    {
                        try { ci2.Visible = false; } catch { }
                    }
                }

                var f = Frames(KidPrefix("kid_idle"));
                if (f == null || f.GetFrameCount("default") == 0) return;
                var spr = new AnimatedSprite2D();
                spr.Name = "ZhijiSceneChar";
                spr.SpriteFrames = f;
                spr.Centered = true;
                float frameH = 320f;
                float targetH = vp.Y * (merchant ? 0.34f : 0.46f);
                float sc = targetH / frameH;
                spr.Scale = new Vector2(sc, sc);
                spr.FlipH = true;              // 原图朝左，翻转后面向火堆/店主
                spr.Position = new Vector2(0, -targetH * 0.5f);
                spr.ZIndex = 20;
                node.AddChild(spr);
                spr.Play("default");
                _sceneSprites.Add(spr);
                ZLog.W("scene character replaced: " + (merchant ? "merchant" : "rest"));
            }
            catch (Exception e) { ZLog.W("scene char fail " + e.Message); }
        }

        // ================= 战斗状态计数器 HUD =================
        static float _lastThoughtAt;
        static string _thought;
        static readonly System.Random _autoRngThought = new System.Random();

        static CanvasLayer _statLayer;
        static Label _statLabel;
        static Godot.TextureRect _summon1, _summon2;
        static Godot.ColorRect _summon1Bg, _summon1Fill, _summon2Bg, _summon2Fill;
        static Label _summon1Hp, _summon2Hp;
        static Label _thoughtLabel;
        static Godot.TextureRect _ostySkin;
        static Godot.ColorRect _ostyBg, _ostyFill;
        static Label _ostyHp;

        static void EnsureStatPanel()
        {
            if (_statLayer != null && GodotObject.IsInstanceValid(_statLayer)) return;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                _statLayer = new CanvasLayer();
                _statLayer.Name = "ZhijiStatLayer";
                _statLayer.Layer = 5;
                tree.Root.AddChild(_statLayer);
                _statLabel = new Label();
                _statLabel.Position = new Vector2(18, 150);
                _statLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                var ls = new LabelSettings();
                ls.FontSize = 26;
                ls.FontColor = new Color(1f, 0.92f, 0.92f, 0.95f);
                ls.OutlineSize = 8;
                ls.OutlineColor = new Color(0f, 0f, 0f, 0.85f);
                _statLabel.LabelSettings = ls;
                _statLayer.AddChild(_statLabel);
                _thoughtLabel = new Label();
                _thoughtLabel.Position = new Vector2(18, 236);
                _thoughtLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                var lsT = new LabelSettings();
                lsT.FontSize = 24;
                lsT.FontColor = new Color(1f, 0.55f, 0.55f, 0.95f);
                lsT.OutlineSize = 7;
                lsT.OutlineColor = new Color(0f, 0f, 0f, 0.9f);
                _thoughtLabel.LabelSettings = lsT;
                _thoughtLabel.Visible = false;
                _statLayer.AddChild(_thoughtLabel);
                try
                {
                    _ostySkin = new Godot.TextureRect();
                    _ostySkin.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _ostySkin.StretchMode = Godot.TextureRect.StretchModeEnum.Scale;
                    _ostySkin.ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize;
                    _ostySkin.Visible = false;
                    _statLayer.AddChild(_ostySkin);
                }
                catch { }
                _summon1 = MakeSummonSlot(new Vector2(48, 700), out _summon1Bg, out _summon1Fill, out _summon1Hp);
                _summon2 = MakeSummonSlot(new Vector2(228, 700), out _summon2Bg, out _summon2Fill, out _summon2Hp);
                try
                {
                    var tmr = new Godot.Timer();
                    tmr.WaitTime = 0.7;
                    tmr.Autostart = true;
                    tmr.OneShot = false;
                    tmr.Timeout += () => UpdateMapThought();
                    _statLayer.AddChild(tmr);
                }
                catch (Exception eT) { ZLog.W("thought timer fail " + eT.Message); }
                // 死亡结算专用快 tick（0.15s）：0.7s 的常规 tick 会让死亡收尾拖到 2s+
                try
                {
                    var tmrD = new Godot.Timer();
                    tmrD.WaitTime = 0.15;
                    tmrD.Autostart = true;
                    tmrD.OneShot = false;
                    tmrD.Timeout += () => ZhijiPatcher.TickDeathSettle();
                    _statLayer.AddChild(tmrD);
                    ZLog.W("death settle timer ready");
                }
                catch (Exception eD) { ZLog.W("death timer fail " + eD.Message); }
                ZLog.W("stat panel ready");
            }
            catch (Exception e) { ZLog.W("stat panel fail " + e.Message); }
        }

        static bool _ostyHidden;

        static void EnsureOstyBar()
        {
            try
            {
                if (_statLayer == null || !GodotObject.IsInstanceValid(_statLayer)) return;
                if (_ostyBg != null && GodotObject.IsInstanceValid(_ostyBg) && _ostyFill != null && GodotObject.IsInstanceValid(_ostyFill) && _ostyHp != null && GodotObject.IsInstanceValid(_ostyHp)) return;
                _ostyBg = new Godot.ColorRect(); _ostyBg.Color = new Color(0f, 0f, 0f, 0.75f); _ostyBg.MouseFilter = Control.MouseFilterEnum.Ignore;
                _statLayer.AddChild(_ostyBg);
                _ostyFill = new Godot.ColorRect(); _ostyFill.Color = new Color(0.85f, 0.15f, 0.15f, 1f); _ostyFill.MouseFilter = Control.MouseFilterEnum.Ignore;
                _statLayer.AddChild(_ostyFill);
                _ostyHp = new Label(); _ostyHp.MouseFilter = Control.MouseFilterEnum.Ignore;
                var ls = new LabelSettings(); ls.FontSize = 17; ls.FontColor = new Color(1f, 0.85f, 0.85f, 1f); ls.OutlineSize = 5; ls.OutlineColor = new Color(0f, 0f, 0f, 0.9f);
                _ostyHp.LabelSettings = ls;
                _statLayer.AddChild(_ostyHp);
            }
            catch { }
        }

        public static void UpdateOstySkin()
        {
            try
            {
                if (_statLayer == null || !GodotObject.IsInstanceValid(_statLayer)) return;
                if (_ostySkin == null || !GodotObject.IsInstanceValid(_ostySkin))
                {
                    _ostySkin = new Godot.TextureRect();
                    _ostySkin.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _ostySkin.StretchMode = Godot.TextureRect.StretchModeEnum.Scale;
                    _ostySkin.ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize;
                    _ostySkin.Visible = false;
                    _statLayer.AddChild(_ostySkin);
                }
                bool inCombat = false;
                try { inCombat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null && MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() != null; } catch { }
                if (!inCombat)
                {
                    try { _ostySkin.Visible = false; if (_ostyBg != null && GodotObject.IsInstanceValid(_ostyBg)) _ostyBg.Visible = false; if (_ostyFill != null && GodotObject.IsInstanceValid(_ostyFill)) _ostyFill.Visible = false; if (_ostyHp != null && GodotObject.IsInstanceValid(_ostyHp)) _ostyHp.Visible = false; } catch { }
                    return;
                }
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                // HP 归零兜底：引擎偶尔不走死亡结算（自伤/调试），统一交给 ZhijiPatcher 处理
                try
                {
                    if (pl != null && pl.Creature != null && !pl.Creature.IsDead && (int)pl.Creature.CurrentHp <= 0)
                        ZhijiPatcher.EnsureDeathSettled(pl, "visual-tick");
                }
                catch { }
                // 玩家死亡：隐藏所有自绘层，避免残留在结算界面
                try
                {
                    bool dead = pl == null || pl.Creature == null || pl.Creature.IsDead || (int)pl.Creature.CurrentHp <= 0;
                    if (dead)
                    {
                        if (_statLayer != null && GodotObject.IsInstanceValid(_statLayer)) _statLayer.Visible = false;
                        if (_fxLayer != null && GodotObject.IsInstanceValid(_fxLayer)) _fxLayer.Visible = false;
                        if (_zoomLayer != null && GodotObject.IsInstanceValid(_zoomLayer)) _zoomLayer.Visible = false;
                        if (_choiceLayer != null && GodotObject.IsInstanceValid(_choiceLayer)) _choiceLayer.Visible = false;
                        return;
                    }
                    else if (_statLayer != null && GodotObject.IsInstanceValid(_statLayer)) _statLayer.Visible = true;
                }
                catch { }
                // 保险：战斗中途装/换 mod 时，引擎的玩家立绘可能没被隐藏，会和自绘层叠在一起
                try
                {
                    if (pl != null && pl.Creature != null && HiddenCreature.Count == 0)
                    {
                        var pn = pl.Creature.GetCreatureNode();
                        if (pn != null && GodotObject.IsInstanceValid(pn)) HidePlayerCreature(pl);
                    }
                }
                catch { }
                var osty = pl != null ? pl.Osty : null;
                bool alive = osty != null && !osty.IsDead && osty.CurrentHp > 0;
                if (!alive)
                {
                    _ostySkin.Visible = false;
                    try { if (_ostyBg != null && GodotObject.IsInstanceValid(_ostyBg)) _ostyBg.Visible = false; if (_ostyFill != null && GodotObject.IsInstanceValid(_ostyFill)) _ostyFill.Visible = false; if (_ostyHp != null && GodotObject.IsInstanceValid(_ostyHp)) _ostyHp.Visible = false; } catch { }
                    return;
                }
                var nc = osty.GetCreatureNode();
                if (nc == null || !GodotObject.IsInstanceValid(nc)) { _ostySkin.Visible = false; return; }
                try { nc.Modulate = new Color(1f, 1f, 1f, 0f); nc.Visible = false; } catch { }
                EnsureOstyBar();
                var gp = nc.GetGlobalTransformWithCanvas().Origin;
                bool vassal = false;
                try { vassal = ZhijiStore.Of(pl).Vassal; } catch { }
                var wantTex = Art.Get(vassal ? "vassal" : "summon_idle");
                if (wantTex != null && _ostySkin.Texture != wantTex) _ostySkin.Texture = wantTex;
                _ostySkin.Visible = true;
                if (vassal)
                {
                    _ostySkin.Size = new Vector2(155, 272);
                    _ostySkin.Position = new Vector2(gp.X - 77f, gp.Y - 272f);
                }
                else
                {
                    float grow = 1f;
                    try { grow = System.Math.Clamp(0.85f + (float)osty.MaxHp / 40f, 0.85f, 1.35f); } catch { }
                    float sw = 170f * grow, sh = 128f * grow;
                    _ostySkin.Size = new Vector2(sw, sh);
                    _ostySkin.Position = new Vector2(gp.X - sw * 0.5f, gp.Y - sh);
                }
                // 专用血条（不与其他 HUD 刷新互相覆盖）
                try
                {
                    var cur = (int)osty.CurrentHp; var max = (int)osty.MaxHp;
                    if (_ostyBg != null && GodotObject.IsInstanceValid(_ostyBg)) { _ostyBg.Visible = true; _ostyBg.Position = new Vector2(gp.X - 60f, gp.Y + 6f); _ostyBg.Size = new Vector2(120, 9); }
                    if (_ostyFill != null && GodotObject.IsInstanceValid(_ostyFill))
                    {
                        _ostyFill.Visible = true;
                        _ostyFill.Position = new Vector2(gp.X - 60f, gp.Y + 6f);
                        _ostyFill.Size = new Vector2(120f * System.Math.Clamp((float)cur / System.Math.Max(1, max), 0f, 1f), 9f);
                    }
                    if (_ostyHp != null && GodotObject.IsInstanceValid(_ostyHp)) { _ostyHp.Visible = true; _ostyHp.Position = new Vector2(gp.X - 56f, gp.Y + 17f); _ostyHp.Text = (vassal ? "眷属 " : "血肉 ") + cur + "/" + max; }
                }
                catch { }
            }
            catch { }
        }

        static bool _eventArtDumped;
        public static void EventArtTickPublic() { try { ApplyEventArtTick(); } catch { } }
        static void ApplyEventArtTick()
        {
            try
            {
                if (!ZhijiPatcher.RedHeartArtActive) return;
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                Godot.Node room = null;
                try
                {
                    var tNR = ZhijiPatcher.GameAsmType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom");
                    if (tNR != null)
                    {
                        var inst = tNR.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                        room = inst as Godot.Node;
                    }
                }
                catch { }
                if (room == null) room = FindNodeByTypeName(tree.Root, "NEventRoom", 0);
                // 事件房可能在两次 tick 之间被释放：对已释放的 Godot 对象调 GetChildren 会原生段错误
                if (room == null || !GodotObject.IsInstanceValid(room)) { _eventArtDumped = false; return; }
                if (room == null)
                {
                    if (!_eventArtDumped)
                    {
                        _eventArtDumped = true;
                        ZLog.W("event art: no NEventRoom found active=" + ZhijiPatcher.RedHeartArtActive);
                        DumpEventNodes(tree.Root, 0);
                    }
                    return;
                }
                var tex = Art.Get("event_red_heart");
                if (tex == null) return;
                var stack = new Stack<Godot.Node>();
                stack.Push(room);
                int applied = 0, seen = 0;
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n == null) continue;
                    foreach (var ch in n.GetChildren()) if (ch is Godot.Node cn) stack.Push(cn);
                    if (n is Godot.TextureRect tr && tr.Size.X >= 300)
                    {
                        seen++;
                        if (!_eventArtDumped) ZLog.W("event art node " + tr.Name + " size=" + tr.Size + " parent=" + tr.GetParent()?.Name);
                        try { tr.Texture = tex; applied++; } catch { }
                    }
                }
                int hid = HideVanillaEventDecor(room, !_eventArtDumped);
                if (!_eventArtDumped) { ZLog.W("event art walk: room=" + room.Name + " texrects=" + seen + " applied=" + applied + " hidden=" + hid); _eventArtDumped = true; }
            }
            catch (Exception e) { if (!_eventArtDumped) { ZLog.W("event art tick fail " + e.Message); _eventArtDumped = true; } }
        }

        // 隐藏原版事件场景的前景剪影 / 特效层（藤蔓 DenseVegetationForeground、虫子、雾与落叶粒子）
        // 这些不是 TextureRect，旧逻辑漏掉 → 换完背景仍叠在画面上（待办 #5）
        public static int HideVanillaEventDecor(Godot.Node room, bool log)
        {
            int hid = 0;
            try
            {
                if (room == null || !GodotObject.IsInstanceValid(room)) return 0;
                var stack = new Stack<Godot.Node>();
                stack.Push(room);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n == null || !GodotObject.IsInstanceValid(n)) continue;
                    foreach (var ch in n.GetChildren()) if (ch is Godot.Node cn) stack.Push(cn);
                    var nm = n.Name.ToString();
                    bool target = nm.EndsWith("Vfx") || nm.Contains("Foreground") || nm == "VfxContainer" || nm == "dark_overlay";
                    if (!target || !(n is Godot.CanvasItem ci)) continue;
                    if (!ci.Visible) continue;
                    ci.Visible = false;
                    hid++;
                    if (log) ZLog.W("event art hide scene node " + n.GetType().Name + " '" + nm + "'");
                }
            }
            catch (Exception e) { if (log) ZLog.W("hide event decor fail " + e.Message); }
            return hid;
        }

        public static Godot.Node FindRoomNodePublic(Godot.Node n, string typeName, int depth)
        {
            return FindNodeByTypeName(n, typeName, depth);
        }

        // 事件节点树 dump：类型 / 名字 / 可见 / 尺寸（用于定位原版剪影/特效残留）
        public static void DumpNodeTree(Godot.Node n, int depth, int maxDepth)
        {
            try
            {
                if (n == null || !GodotObject.IsInstanceValid(n) || depth > maxDepth) return;
                string sz = "";
                if (n is Godot.Control c) sz = " size=" + c.Size.X.ToString("0") + "x" + c.Size.Y.ToString("0");
                else if (n is Godot.Sprite2D sp) sz = " tex=" + (sp.Texture != null ? sp.Texture.GetWidth() + "x" + sp.Texture.GetHeight() : "null") + " pos=" + sp.Position.X.ToString("0") + "," + sp.Position.Y.ToString("0");
                else if (n is Godot.AnimatedSprite2D asp) sz = " frames=" + (asp.SpriteFrames != null ? "yes" : "null");
                ZLog.W("evdump " + new string(' ', depth) + n.GetType().Name + " '" + n.Name + "' vis=" + (n is Godot.CanvasItem ci ? ci.Visible.ToString() : "?") + sz);
                foreach (var ch in n.GetChildren()) DumpNodeTree(ch, depth + 1, maxDepth);
            }
            catch { }
        }

        static void DumpEventNodes(Godot.Node n, int depth)
        {
            if (n == null || depth > 10) return;
            var tn = n.GetType().Name;
            if (tn.Contains("Event") || n.Name.ToString().ToLower().Contains("event") || tn.Contains("Portrait"))
                ZLog.W("event node " + depth + " " + tn + " name=" + n.Name);
            foreach (var ch in n.GetChildren()) if (ch is Godot.Node cn) DumpEventNodes(cn, depth + 1);
        }

        static Godot.Node FindNodeByTypeName(Godot.Node node, string typeName, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node.GetType().Name == typeName) return node;
            foreach (var ch in node.GetChildren())
                if (ch is Godot.Node cn) { var r = FindNodeByTypeName(cn, typeName, depth + 1); if (r != null) return r; }
            return null;
        }

        static void UpdateMapThought()
        {
            try { ApplyEventArtTick(); } catch { }
            try { ZhijiPatcher.TryConsumeAutoPlay(); } catch { }
            try { UpdateOstySkin(); } catch { }
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var room = rs != null ? rs.CurrentRoom : null;
                bool isMap = room != null && room.GetType().Name == "MapRoom";
                bool hasUH = false;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl != null && pl.Relics != null) foreach (var r in pl.Relics) if (r is UnboundHeart) { hasUH = true; break; }
                if (_thoughtLabel == null || !GodotObject.IsInstanceValid(_thoughtLabel)) return;
                bool inCombat2 = false;
                try { inCombat2 = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null && MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() != null; } catch { }
                if (!inCombat2)
                {
                    UpdateSummonSlot(_summon1, _summon1Bg, _summon1Fill, _summon1Hp, 0, 0, 1);
                    UpdateSummonSlot(_summon2, _summon2Bg, _summon2Fill, _summon2Hp, 0, 0, 2);
                }
                if (isMap && hasUH)
                {
                    float t = (float)Godot.Time.GetTicksMsec() / 1000f;
                    if (t - _lastThoughtAt > 12f || string.IsNullOrEmpty(_thought))
                    {
                        _lastThoughtAt = t;
                        string[] thoughts = { "「这条路闻起来更香。」", "「左边那个，能吃。」", "「别拦我。」", "「还有一个胃是空的。」" };
                        _thought = thoughts[_autoRngThought.Next(thoughts.Length)];
                    }
                    _thoughtLabel.Visible = true;
                    _thoughtLabel.Text = "她的想法：" + _thought;
                }
                else
                {
                    _thoughtLabel.Visible = false;
                }
            }
            catch { }
        }

        public static void ShowThought()
        {
            try
            {
                EnsureStatPanel();
                if (_thoughtLabel == null || !GodotObject.IsInstanceValid(_thoughtLabel)) return;
                string[] thoughts = { "「这条路闻起来更香。」", "「左边那个，能吃。」", "「别拦我。」", "「还有一个胃是空的。」" };
                _thought = thoughts[_autoRngThought.Next(thoughts.Length)];
                _thoughtLabel.Visible = true;
                _thoughtLabel.Text = "她的想法：" + _thought;
            }
            catch { }
        }

        static void EnsureSummonSlots()
        {
            try
            {
                if (_statLayer == null || !GodotObject.IsInstanceValid(_statLayer)) return;
                bool broken = _summon1 == null || !GodotObject.IsInstanceValid(_summon1)
                    || _summon1Bg == null || !GodotObject.IsInstanceValid(_summon1Bg)
                    || _summon1Fill == null || !GodotObject.IsInstanceValid(_summon1Fill)
                    || _summon1Hp == null || !GodotObject.IsInstanceValid(_summon1Hp)
                    || _summon2 == null || !GodotObject.IsInstanceValid(_summon2);
                if (broken)
                {
                    _thoughtLabel = new Label();
                _thoughtLabel.Position = new Vector2(18, 236);
                _thoughtLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                var lsT = new LabelSettings();
                lsT.FontSize = 24;
                lsT.FontColor = new Color(1f, 0.55f, 0.55f, 0.95f);
                lsT.OutlineSize = 7;
                lsT.OutlineColor = new Color(0f, 0f, 0f, 0.9f);
                _thoughtLabel.LabelSettings = lsT;
                _thoughtLabel.Visible = false;
                _statLayer.AddChild(_thoughtLabel);
                try
                {
                    _ostySkin = new Godot.TextureRect();
                    _ostySkin.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _ostySkin.StretchMode = Godot.TextureRect.StretchModeEnum.Scale;
                    _ostySkin.ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize;
                    _ostySkin.Visible = false;
                    _statLayer.AddChild(_ostySkin);
                }
                catch { }
                _summon1 = MakeSummonSlot(new Vector2(48, 700), out _summon1Bg, out _summon1Fill, out _summon1Hp);
                    _summon2 = MakeSummonSlot(new Vector2(228, 700), out _summon2Bg, out _summon2Fill, out _summon2Hp);
                    ZLog.W("summon slots rebuilt");
                }
            }
            catch { }
        }

        static Godot.TextureRect MakeSummonSlot(Vector2 pos, out Godot.ColorRect bg, out Godot.ColorRect fill, out Label hp)
        {
            var tex = new Godot.TextureRect();
            tex.Position = pos;
            tex.Size = new Vector2(120, 90);
            tex.StretchMode = Godot.TextureRect.StretchModeEnum.Scale;
            tex.ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize;
            tex.MouseFilter = Control.MouseFilterEnum.Ignore;
            _statLayer.AddChild(tex);
            bg = new Godot.ColorRect();
            bg.Position = pos;
            bg.Size = new Vector2(100, 9);
            bg.Color = new Color(0f, 0f, 0f, 0.75f);
            bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            _statLayer.AddChild(bg);
            fill = new Godot.ColorRect();
            fill.Position = bg.Position;
            fill.Size = new Vector2(100, 9);
            fill.Color = new Color(0.85f, 0.15f, 0.15f, 1f);
            fill.MouseFilter = Control.MouseFilterEnum.Ignore;
            _statLayer.AddChild(fill);
            hp = new Label();
            hp.Position = pos;
            hp.MouseFilter = Control.MouseFilterEnum.Ignore;
            var ls2 = new LabelSettings();
            ls2.FontSize = 16;
            ls2.FontColor = new Color(1f, 0.85f, 0.85f, 1f);
            ls2.OutlineSize = 5;
            ls2.OutlineColor = new Color(0f, 0f, 0f, 0.9f);
            hp.LabelSettings = ls2;
            _statLayer.AddChild(hp);
            return tex;
        }

        // 按玩家站位摆放肉块（参考骨妹：站在角色右侧、脚底对齐）
        static Vector2 SummonAnchor(int slot)
        {
            float px = 374f, py = 680f;
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                var pl = cs != null && cs.Players != null && cs.Players.Count > 0 ? cs.Players[0] : null;
                var nc = pl != null && pl.Creature != null ? pl.Creature.GetCreatureNode() : null;
                if (nc != null && GodotObject.IsInstanceValid(nc))
                {
                    var gp = nc.GetGlobalTransformWithCanvas().Origin;
                    if (gp.X > 1 && gp.Y > 1) { px = gp.X; py = gp.Y; }
                }
            }
            catch { }
            float dx = slot == 1 ? 135f : 245f;
            return new Vector2(px + dx, py);
        }

        static void UpdateSummonSlot(Godot.TextureRect tex, Godot.ColorRect bg, Godot.ColorRect fill, Label hp, int cur, int max, int slot)
        {
            try
            {
                bool on = cur > 0;
                var anchor = SummonAnchor(slot);
                float w = 120f, h = 90f;
                if (tex != null)
                {
                    tex.Visible = on;
                    tex.Position = new Vector2(anchor.X - w / 2f, anchor.Y - h);
                    if (on && tex.Texture == null) tex.Texture = Art.Get("summon_idle");
                }
                if (bg != null) { bg.Visible = on; bg.Position = new Vector2(anchor.X - 50f, anchor.Y + 2f); }
                if (fill != null)
                {
                    fill.Visible = on;
                    float p = max > 0 ? System.Math.Clamp((float)cur / max, 0f, 1f) : 0f;
                    fill.Position = new Vector2(anchor.X - 50f, anchor.Y + 2f);
                    fill.Size = new Vector2(100f * p, 9f);
                }
                if (hp != null) { hp.Visible = on; hp.Position = new Vector2(anchor.X - 46f, anchor.Y + 12f); hp.Text = on ? ((slot == 1 ? "左手 " : "右手 ") + cur + "/" + max) : ""; }
            }
            catch { }
        }

        /// 每 0.25s 刷新一次：形态 / 搏动 / 饥饿 / 自助餐 / 收割
        public static void SyncStatPanel()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var st = cm != null ? cm.DebugOnlyGetState() : null;
                var pl = st != null && st.Players != null && st.Players.Count > 0 ? st.Players[0] : null;
                if (pl == null || !(pl.Character is Zhiji)) { if (_statLabel != null) _statLabel.Visible = false; return; }
                string roomNow = "";
                try { var rmS = MegaCrit.Sts2.Core.Runs.RunManager.Instance; var rsS = rmS != null ? rmS.DebugOnlyGetState() : null; var crS = rsS != null ? rsS.CurrentRoom : null; roomNow = crS != null ? crS.GetType().Name : ""; } catch { }
                if (roomNow != "CombatRoom") { if (_statLabel != null) _statLabel.Visible = false; return; }
                bool fighting = false;
                try { fighting = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress; } catch { }
                if (!fighting) { if (_statLabel != null) _statLabel.Visible = false; return; }
                EnsureStatPanel();
                if (_statLabel == null) return;
                var z = ZhijiStore.Of(pl);
                string formName = z.Form == ZhijiForm.RedGod
                    ? (z.RForm == RedForm.War ? "红神·战" : (z.RForm == RedForm.Guard ? "红神·守" : "红神·均"))
                    : "幼年";
                _statLabel.Visible = true;
                int selfLoss = 0;
                try
                {
                    if (z.Form == ZhijiForm.Normal && z.Hunger > 0)
                        selfLoss = (int)System.Math.Ceiling((double)pl.Creature.MaxHp * (z.Hunger / 100.0) * 0.10);
                }
                catch { }
                _statLabel.Text = formName
                    + "    搏动 " + z.Pulse
                    + "    饥饿 " + z.Hunger + "/100"
                    + "\n自助餐 " + z.Feast
                    + "    收割 " + z.Reap + "/100"
                    + (selfLoss > 0 ? ("    下回合自伤 -" + selfLoss) : "")
                    + (z.Form == ZhijiForm.Normal && z.Hunger >= 75 ? "    饥饿反噬：+2搏动/-3生命" : "")
                    + (z.AtkCount + z.DefCount > 0 ? ("    攻" + z.AtkCount + "/守" + z.DefCount) : "");
                try
                {
                    int dodgeP = 10;
                    bool hasCloak = false;
                    if (pl.Relics != null) foreach (var r in pl.Relics) if (r is ShadowCloak) { hasCloak = true; break; }
                    if (hasCloak) dodgeP += 5;
                    if (pl.Creature != null) dodgeP += pl.Creature.GetPowerAmount<DodgeChancePower>();
                    if (dodgeP > 60) dodgeP = 60;
                    _statLabel.Text += "    闪避 " + dodgeP + "%";
                }
                catch { }
                // 失控心声：地图上显示她的想法（纯反馈），并隐藏战斗用的肉块 UI
                bool inCombatNow = true;
                try
                {
                    var cmI = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    inCombatNow = cmI != null && cmI.DebugOnlyGetState() != null;
                }
                catch { }
                if (!inCombatNow)
                {
                    try
                    {
                        bool hasUH = false;
                        if (pl.Relics != null) foreach (var rl2 in pl.Relics) if (rl2 is UnboundHeart) { hasUH = true; break; }
                        if (hasUH)
                        {
                            float t2 = (float)Godot.Time.GetTicksMsec() / 1000f;
                            if (t2 - _lastThoughtAt > 12f || string.IsNullOrEmpty(_thought))
                            {
                                _lastThoughtAt = t2;
                                string[] thoughts2 = { "「这条路闻起来更香。」", "「左边那个，能吃。」", "「别拦我。」", "「还有一个胃是空的。」" };
                                _thought = thoughts2[_autoRngThought.Next(thoughts2.Length)];
                            }
                        }
                    }
                    catch { }
                    if (_thoughtLabel != null && GodotObject.IsInstanceValid(_thoughtLabel))
                    {
                        _thoughtLabel.Visible = true;
                        _thoughtLabel.Text = string.IsNullOrEmpty(_thought) ? "" : ("她的想法：" + _thought);
                    }
                    UpdateSummonSlot(_summon1, _summon1Bg, _summon1Fill, _summon1Hp, 0, 0, 1);
                    UpdateSummonSlot(_summon2, _summon2Bg, _summon2Fill, _summon2Hp, 0, 0, 2);
                    return;
                }
                if (_thoughtLabel != null && GodotObject.IsInstanceValid(_thoughtLabel)) _thoughtLabel.Visible = false;
                // 失控心声：战斗中偶尔显示（纯反馈，不强制选路）
                try
                {
                    bool unboundLine = false;
                    var cmL = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csL = cmL != null ? cmL.DebugOnlyGetState() : null;
                    if (csL == null && pl.Relics != null)
                    {
                        foreach (var rl in pl.Relics) if (rl is UnboundHeart) { unboundLine = true; break; }
                    }
                    if (unboundLine)
                    {
                        float t = (float)Godot.Time.GetTicksMsec() / 1000f;
                        if (t - _lastThoughtAt > 12f)
                        {
                            _lastThoughtAt = t;
                            string[] thoughts = { "「这条路闻起来更香。」", "「左边那个，能吃。」", "「别拦我。」", "「还有一个胃是空的。」" };
                            _thought = thoughts[_autoRngThought.Next(thoughts.Length)];
                        }
                        if (!string.IsNullOrEmpty(_thought)) _statLabel.Text += "\n她的想法：" + _thought;
                    }
                }
                catch { }
                EnsureSummonSlots();
                bool anyAlive = false;
                try
                {
                    var cmA = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var csA = cmA != null ? cmA.DebugOnlyGetState() : null;
                    if (csA != null && csA.Enemies != null)
                        foreach (var eA in csA.Enemies) if (eA != null && !eA.IsDead) { anyAlive = true; break; }
                }
                catch { }
                if (anyAlive)
                {
                    UpdateSummonSlot(_summon1, _summon1Bg, _summon1Fill, _summon1Hp, z.MinionHp, z.MinionMax, 1);
                    UpdateSummonSlot(_summon2, _summon2Bg, _summon2Fill, _summon2Hp, z.MinionHp2, z.MinionMax2, 2);
                }
                else
                {
                    UpdateSummonSlot(_summon1, _summon1Bg, _summon1Fill, _summon1Hp, 0, 0, 1);
                    UpdateSummonSlot(_summon2, _summon2Bg, _summon2Fill, _summon2Hp, 0, 0, 2);
                }
            }
            catch { }
        }

        // ================= 收获·大招演出 =================
        static CanvasLayer _fxLayer;
        static ColorRect _flashRect;

        static void EnsureFx()
        {
            if (_fxLayer != null && GodotObject.IsInstanceValid(_fxLayer)) return;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                _fxLayer = new CanvasLayer();
                _fxLayer.Name = "ZhijiFxLayer";
                _fxLayer.Layer = 95;
                tree.Root.AddChild(_fxLayer);
                _flashRect = new ColorRect();
                _flashRect.Color = new Color(1f, 1f, 1f, 0f);
                _flashRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                _flashRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _fxLayer.AddChild(_flashRect);
            }
            catch (Exception e) { ZLog.W("fx init fail " + e.Message); }
        }

        /// 取敌人在屏幕上的归一化位置（0~1），用于镜头对焦
        public static Vector2 CreatureNorm(MegaCrit.Sts2.Core.Entities.Creatures.Creature c)
        {
            try
            {
                var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (room == null || tree == null || c == null) return new Vector2(0.7f, 0.5f);
                var nc = room.GetCreatureNode(c);
                if (nc == null) return new Vector2(0.7f, 0.5f);
                var vp = tree.Root.GetVisibleRect().Size;
                var p = nc.GetGlobalTransformWithCanvas().Origin;
                return new Vector2(Mathf.Clamp(p.X / vp.X, 0.05f, 0.95f), Mathf.Clamp(p.Y / vp.Y, 0.05f, 0.95f));
            }
            catch { return new Vector2(0.7f, 0.5f); }
        }

        static void Flash(float peak, float up, float down)
        {
            try
            {
                EnsureFx();
                if (_flashRect == null) return;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                _flashRect.Color = new Color(1f, 1f, 1f, 0f);
                var t1 = tree.CreateTimer(up);
                t1.Timeout += () =>
                {
                    try
                    {
                        if (_flashRect != null && GodotObject.IsInstanceValid(_flashRect)) _flashRect.Color = new Color(1f, 1f, 1f, peak);
                        var t2 = tree.CreateTimer(down);
                        t2.Timeout += () => { try { if (_flashRect != null && GodotObject.IsInstanceValid(_flashRect)) _flashRect.Color = new Color(1f, 1f, 1f, 0f); } catch { } };
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// 一道斜向斩击光带
        static void SlashBand(Vector2 norm, float angleDeg, float len, float thick, Color col, float dur)
        {
            try
            {
                EnsureFx();
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || _fxLayer == null) return;
                var vp = tree.Root.GetVisibleRect().Size;
                var band = new ColorRect();
                band.Color = col;
                band.MouseFilter = Control.MouseFilterEnum.Ignore;
                band.PivotOffset = new Vector2(0.5f, 0.5f);
                band.Size = new Vector2(vp.X * len, thick);
                band.Position = new Vector2(norm.X * vp.X - band.Size.X * 0.5f, norm.Y * vp.Y - thick * 0.5f);
                band.RotationDegrees = angleDeg;
                band.Scale = new Vector2(0.15f, 1f);
                band.Modulate = new Color(1, 1, 1, 0f);
                _fxLayer.AddChild(band);
                int steps = 6; float step = dur / steps;
                int i = 0;
                void Tick()
                {
                    try
                    {
                        if (band == null || !GodotObject.IsInstanceValid(band)) return;
                        i++;
                        float p = (float)i / steps;
                        band.Scale = new Vector2(0.15f + 1.1f * p, 1f - 0.6f * p);
                        band.Modulate = new Color(1, 1, 1, p < 0.45f ? p / 0.45f : Math.Max(0f, 1f - (p - 0.45f) / 0.55f));
                        if (p >= 1f) { try { band.QueueFree(); } catch { } return; }
                        var t = tree.CreateTimer(step);
                        t.Timeout += Tick;
                    }
                    catch { }
                }
                var t0 = tree.CreateTimer(step);
                t0.Timeout += Tick;
            }
            catch { }
        }

        static readonly List<CanvasItem> _hiddenPlayCards = new List<CanvasItem>();

        static void HidePlayedCardsWalk(Node n, bool inHand, int depth)
        {
            if (n == null || depth > 8) return;
            string tn = n.GetType().Name;
            bool nowInHand = inHand || tn == "NPlayerHand";
            if (!nowInHand && tn == "NCard")
            {
                var ci = n as CanvasItem;
                if (ci != null && ci.Visible) { ci.Visible = false; _hiddenPlayCards.Add(ci); }
            }
            foreach (var c in n.GetChildren()) HidePlayedCardsWalk(c, nowInHand, depth + 1);
        }

        /// 演出期间把"正在打出的卡"隐藏掉，避免挡住目标
        public static void HidePlayedCards()
        {
            try
            {
                _hiddenPlayCards.Clear();
                var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
                if (room == null) return;
                HidePlayedCardsWalk(room, false, 0);
                if (_hiddenPlayCards.Count > 0) ZLog.W("hid played cards=" + _hiddenPlayCards.Count);
            }
            catch (Exception e) { ZLog.W("hide played cards fail " + e.Message); }
        }

        public static void RestorePlayedCards()
        {
            try
            {
                foreach (var ci in _hiddenPlayCards)
                    if (ci != null && GodotObject.IsInstanceValid(ci)) ci.Visible = true;
                _hiddenPlayCards.Clear();
            }
            catch { }
        }

        public static void HarvestStart(MegaCrit.Sts2.Core.Entities.Creatures.Creature target)
        {
            try
            {
                EnsureFx();
                HidePlayedCards();
                var n = CreatureNorm(target);
                PlayScreenZoomXY(1.0f, 1.9f, 0.5f, 0.55f, n.X, n.Y, 0.45f, 0.02f, 0.98f);
                SetZoomPulse(0.5f);
                SetZoomVignette(0.55f);
                ZLog.W("harvest cine start focus=" + n.X.ToString("0.00") + "," + n.Y.ToString("0.00"));
            }
            catch (Exception e) { ZLog.W("harvest start fail " + e.Message); }
        }

        public static void HarvestHit(int idx, MegaCrit.Sts2.Core.Entities.Creatures.Creature target)
        {
            try
            {
                var n = CreatureNorm(target);
                float ang = -60f + (idx % 4) * 40f;
                // 外层红光 + 内层白色刀光，形成"斩击"层次
                SlashBand(n, ang, 1.6f, 46f, new Color(0.95f, 0.08f, 0.08f, 0.75f), 0.22f);
                SlashBand(n, ang, 1.6f, 14f, new Color(1f, 0.97f, 0.97f, 1f), 0.20f);
                SlashBand(n, ang + 62f, 1.4f, 34f, new Color(0.9f, 0.1f, 0.1f, 0.65f), 0.24f);
                SlashBand(n, ang + 62f, 1.4f, 10f, new Color(1f, 1f, 1f, 1f), 0.22f);
                SetZoomPulse(0.75f - idx * 0.1f);
                var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
                if (game != null)
                {
                    try
                    {
                        game.ScreenShake(
                            idx % 2 == 0 ? MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeStrength.Medium : MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeStrength.Weak,
                            MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeDuration.Short);
                    }
                    catch { }
                }
                Flash(0.28f, 0.02f, 0.12f);
                ZLog.W("harvest hit fx " + idx + " at " + n.X.ToString("0.00") + "," + n.Y.ToString("0.00"));
            }
            catch (Exception e) { ZLog.W("harvest hit fail " + e.Message); }
        }

        public static void HarvestEnd()
        {
            try
            {
                PlayScreenZoomXY(1.9f, 1.0f, _fx0, _f0, 0.5f, 0.55f, 0.40f, 0.02f, 0.98f);
                RestorePlayedCards();
                SetZoomPulse(0f);
                SetZoomVignette(0f);
                Flash(0.5f, 0.03f, 0.35f);
                var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
                if (game != null)
                {
                    try { game.ScreenShake(MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeStrength.Strong, MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeDuration.Short); } catch { }
                }
                ZLog.W("harvest cine end");
            }
            catch (Exception e) { ZLog.W("harvest end fail " + e.Message); }
        }

        static readonly List<CanvasItem> Hidden = new List<CanvasItem>();

        static bool _overlayWanted;

        static bool PileScreenVisible(Node n, int depth)
        {
            if (n == null || depth > 10) return false;
            try
            {
                if (n.GetType().Name == "NCardPileScreen")
                {
                    var ci = n as CanvasItem;
                    if (ci != null && ci.IsVisibleInTree()) return true;
                }
                foreach (var c in n.GetChildren()) if (PileScreenVisible(c, depth + 1)) return true;
            }
            catch { }
            return false;
        }

        /// 打开抽牌/弃牌/消耗堆界面时，把自己的立绘藏起来，避免盖住 UI
        public static void SyncOverlayForUI()
        {
            try
            {
                if (_overlay == null || !GodotObject.IsInstanceValid(_overlay)) return;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                bool pile = PileScreenVisible(tree.Root, 0);
                if (pile)
                {
                    if (_overlay.Visible) { _overlay.Visible = false; ZLog.W("overlay hidden (pile screen)"); }
                }
                else if (_overlayWanted && !_overlay.Visible)
                {
                    _overlay.Visible = true;
                    ZLog.W("overlay restored (pile screen closed)");
                }
            }
            catch { }
        }

        static CanvasLayer _layer;
        static AnimatedSprite2D _overlay;
        static Vector2 _overlayBase;
        static float _overlaySc = 1f;

        // 站位：与战斗里的玩家位置对齐（左侧、脚踩地面，血条在其正下方）
        static void PlaceOverlay(Vector2 vp, bool red)
        {
            float frameH = 320f;
            float h = vp.Y * (red ? 0.42f : 0.22f);    // 屏幕高度占比（红神/失控放大，恢复变身后的压迫感）
            float sc = h / frameH;
            _overlay.Centered = true;
            _overlay.FlipH = red;                       // 红神原图朝左，翻转后朝右
            _overlay.Scale = new Vector2(sc, sc);
            float groundY = vp.Y * 0.63f;               // 脚底（地板线，血条上方）
            float centerX = vp.X * 0.195f;              // 玩家站位（兜底）
            // 优先用玩家的 creature 节点位置，保证立绘正好在血条上方
            try
            {
                var cmO = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var stO = cmO != null ? cmO.DebugOnlyGetState() : null;
                var plO = stO != null && stO.Players != null && stO.Players.Count > 0 ? stO.Players[0] : null;
                var ncO = plO != null && plO.Creature != null ? plO.Creature.GetCreatureNode() : null;
                if (ncO != null && GodotObject.IsInstanceValid(ncO))
                {
                    var gp = ncO.GetGlobalTransformWithCanvas().Origin;
                    if (gp.X > 1 && gp.Y > 1) { centerX = gp.X; groundY = gp.Y; }
                }
            }
            catch { }
            _overlay.Position = new Vector2(centerX, groundY - h * 0.5f);
            _overlayBase = _overlay.Position;
            _overlaySc = sc;
        }

        // ===== 死亡贴图（3.3.3）=====
        // 玩家死亡后不再显示原版角色立绘（观感割裂），改为「肉堆 + 红冠」：
        // 常态 art/death_kid.png，红神 art/death_red.png。
        static Sprite2D _deathSprite;
        static bool _deathWanted;

        public static bool DeathSpriteVisible()
        {
            try { return _deathSprite != null && GodotObject.IsInstanceValid(_deathSprite) && _deathSprite.Visible; }
            catch { return false; }
        }

        static void EnsureSpriteLayer()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
                var watcher = new OverlayWatcher();
                watcher.Name = "ZhijiOverlayWatcher";
                tree.Root.AddChild(watcher);
                _layer = new CanvasLayer();
                _layer.Name = "ZhijiSpriteLayer";
                _layer.Layer = 1;
                tree.Root.AddChild(_layer);
            }
            catch { }
        }

        // 结算/战败界面是否已经出现（RunManager 标记 + 顶层 overlay 类型）
        static bool GameOverUp()
        {
            try
            {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm != null && rm.IsGameOver) return true;
            }
            catch { }
            try
            {
                var st = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance;
                if (st != null && GodotObject.IsInstanceValid(st))
                {
                    var peek = st.Peek();
                    if (peek != null && peek.GetType().Name.Contains("GameOver")) return true;
                }
            }
            catch { }
            return false;
        }

        // 结算界面出现：mod 自绘层全部收起（死亡贴图保留，作为战败背景）
        public static void HideAllSelfDrawn(string why)
        {
            try
            {
                HideOverlay();
                Godot.CanvasLayer[] layers = { _statLayer, _fxLayer, _zoomLayer, _choiceLayer };
                int n = 0;
                foreach (var cl in layers)
                {
                    if (cl == null || !GodotObject.IsInstanceValid(cl)) continue;
                    if (!cl.Visible) continue;
                    cl.Visible = false;
                    n++;
                }
                ZLog.W("self-drawn hidden (" + why + ") n=" + n);
            }
            catch (Exception e) { ZLog.W("hide self-drawn fail " + e.Message); }
        }

        public static void ShowDeathSprite(bool red)
        {
            try
            {
                EnsureSpriteLayer();
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || _layer == null || !GodotObject.IsInstanceValid(_layer)) return;
                var tex = Art.Get(red ? "death_red" : "death_kid");
                if (tex == null) { ZLog.W("death art missing red=" + red); return; }
                if (_deathSprite == null || !GodotObject.IsInstanceValid(_deathSprite))
                {
                    _deathSprite = new Sprite2D();
                    _deathSprite.Name = "ZhijiDeath";
                    _layer.AddChild(_deathSprite);
                }
                _deathSprite.Texture = tex;
                _deathSprite.Centered = true;
                _deathSprite.FlipH = false;
                var vp = tree.Root.GetVisibleRect().Size;
                float h = vp.Y * (red ? 0.135f : 0.125f);
                float sc = h / System.Math.Max(1f, tex.GetHeight());
                _deathSprite.Scale = new Vector2(sc, sc);
                float groundY = vp.Y * 0.63f;
                float centerX = vp.X * 0.195f;
                try
                {
                    var cmO = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                    var stO = cmO != null ? cmO.DebugOnlyGetState() : null;
                    var plO = stO != null && stO.Players != null && stO.Players.Count > 0 ? stO.Players[0] : null;
                    var ncO = plO != null && plO.Creature != null ? plO.Creature.GetCreatureNode() : null;
                    if (ncO != null && GodotObject.IsInstanceValid(ncO))
                    {
                        var gp = ncO.GetGlobalTransformWithCanvas().Origin;
                        if (gp.X > 1 && gp.Y > 1) { centerX = gp.X; groundY = gp.Y; }
                    }
                }
                catch { }
                _deathSprite.Position = new Vector2(centerX, groundY - h * 0.5f);
                _deathSprite.ZIndex = 6;
                _deathSprite.Visible = true;
                _deathWanted = true;
                ZLog.W("death sprite " + (red ? "red" : "kid") + " h=" + h.ToString("0") + " pos="
                    + centerX.ToString("0") + "," + (_deathSprite.Position.Y).ToString("0"));
            }
            catch (Exception e) { ZLog.W("death sprite fail " + e.Message); }
        }

        public static void HideDeathSprite(string why)
        {
            try
            {
                _deathWanted = false;
                bool was = _deathSprite != null && GodotObject.IsInstanceValid(_deathSprite) && _deathSprite.Visible;
                if (_deathSprite != null && GodotObject.IsInstanceValid(_deathSprite)) _deathSprite.Visible = false;
                if (was && !string.IsNullOrEmpty(why)) ZLog.W("death sprite hidden (" + why + ")");
            }
            catch { }
        }

        public static void Warmup()
        {
            try
            {
                foreach (var p in new[] { "kid_idle","kid_attack","kid_skill1","kid_defend","kid_hit","kid_dodge",
                                          "red_idle","red_attack","red_defend","red_hit","red_dodge",
                                          "unbound_idle","unbound_attack","unbound_defend","unbound_hit","unbound_dodge" })
                {
                    try { var f = Frames(p); if (f != null) { var _ = f.GetFrameCount("default"); } } catch { }
                }
                ZLog.W("overlay warmup done");
            }
            catch { }
        }

        static readonly string[] BlockingUiTypes = {
            "NCardPileScreen","NCardsViewScreen","NDeckViewScreen","NSimpleCardsViewScreen",
            "NChooseACardSelectionScreen","NCardRewardSelectionScreen","NDeckCardSelectScreen",
            "NDeckUpgradeSelectScreen","NDeckTransformSelectScreen","NDeckEnchantSelectScreen",
            "NInspectCardScreen","NRewardsScreen","NCardGridSelectionScreen","NChooseARelicSelection"
        };

        static bool FindVisibleUi(Godot.Node n, int depth)
        {
            if (n == null || depth > 12) return false;
            var tn = n.GetType().Name;
            if (n is Godot.CanvasItem ci)
            {
                if (!ci.Visible) return false;      // 父节点不渲染，子树也看不到 → 直接剪枝
                foreach (var t in BlockingUiTypes) if (tn == t) return true;
            }
            foreach (var ch in n.GetChildren()) if (ch is Godot.Node cn) { if (FindVisibleUi(cn, depth + 1)) return true; }
            return false;
        }

        static long _lastCardRefresh;
        static int _lastSig;
        static bool _refreshDbg;
        public static void RefreshHandValues()
        {
            try
            {
                if (!_refreshDbg) { _refreshDbg = true; ZLog.W("refresh tick alive"); }
                long now = System.Environment.TickCount64;
                if (now - _lastCardRefresh < 300) return;
                _lastCardRefresh = now;
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                var rs = rm != null ? rm.DebugOnlyGetState() : null;
                var pl = rs != null && rs.Players != null && rs.Players.Count > 0 ? rs.Players[0] : null;
                if (pl == null) return;
                var st = ZhijiStore.Of(pl);
                int sig = st.Pulse * 31 + st.Hunger * 13 + st.Feast * 17 + st.Stand * 7 + st.Craving * 3;
                if (sig == _lastSig) return;
                _lastSig = sig;
                var handNode = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
                if (handNode != null)
                {
                    var hand = MegaCrit.Sts2.Core.Entities.Cards.CardPile.Get(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, pl);
                    int n = 0;
                    if (hand != null && hand.Cards != null)
                        foreach (var c in hand.Cards)
                        {
                            if (c == null) continue;
                            try { var node = handNode.GetCard(c); if (node != null && GodotObject.IsInstanceValid(node)) { node.UpdateVisuals(MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.None); n++; } } catch { }
                        }
                    if (n > 0) ZLog.W("hand refresh cards=" + n + " sig=" + sig);
                }
                
            }
            catch { }
        }

        public static long OcclusionIntervalMs = 150;   // 调试可设为 0 测原始开销
        static long _lastOcclusion;
        public static void SyncUiOcclusion()
        {
            try
            {
                // 每帧调用太浪费：150ms 一次足够（界面开合不会更快）
                long nowO = System.Environment.TickCount64;
                if (OcclusionIntervalMs > 0 && nowO - _lastOcclusion < OcclusionIntervalMs) return;
                _lastOcclusion = nowO;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                bool blocked = FindVisibleUi(tree.Root, 0);
                if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.Visible = !blocked;
                if (_statLayer != null && GodotObject.IsInstanceValid(_statLayer)) _statLayer.Visible = !blocked;
            }
            catch { }
        }

        public static void EnsureWatcher()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || tree.Root == null) return;
                if (tree.Root.GetNodeOrNull("ZhijiOverlayWatcher") != null) return;
                var watcher = new OverlayWatcher();
                watcher.Name = "ZhijiOverlayWatcher";
                tree.Root.CallDeferred(Godot.Node.MethodName.AddChild, watcher);
                ZLog.W("overlay watcher queued");
            }
            catch (Exception e) { ZLog.W("ensure watcher fail " + e.Message); }
        }

        public static void ShowOverlay(bool red)
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                if (_layer == null || !GodotObject.IsInstanceValid(_layer))
                {
                    var watcher = new OverlayWatcher();
                    watcher.Name = "ZhijiOverlayWatcher";
                    tree.Root.AddChild(watcher);
                    _layer = new CanvasLayer();
                    _layer.Name = "ZhijiSpriteLayer";
                    _layer.Layer = 1;
                    tree.Root.AddChild(_layer);
                }
                // 结算/战败界面期间不允许再弹出立绘（否则会盖住分数面板）
                if (GameOverUp()) { ZLog.W("overlay skipped (game over screen up)"); return; }
                _red = red;                                  // ← 每次显示都按真实形态重置，避免沿用上一局的标记
                if (_deathWanted || DeathSpriteVisible()) HideDeathSprite("overlay shown");
                if (_overlay == null || !GodotObject.IsInstanceValid(_overlay))
                {
                    _overlay = new AnimatedSprite2D();
                    _overlay.Name = "ZhijiOverlay";
                    _layer.AddChild(_overlay);
                }
                var vp = tree.Root.GetVisibleRect().Size;
                var frames = Frames(red ? RedP("idle") : KidPrefix("kid_idle"));
                if (frames == null || frames.GetFrameCount("default") == 0) { ZLog.W("overlay: no frames"); return; }
                _overlay.SpriteFrames = frames;
                PlaceOverlay(vp, red);
                _overlay.ZIndex = 5;
                _overlay.Visible = true;
                _overlayWanted = true;
                _overlay.Play("default");
                ZLog.W("overlay shown red=" + red + " vp=" + vp.X + "x" + vp.Y + " scale=" + _overlay.Scale.X.ToString("0.00") + " pos=" + _overlay.Position.X.ToString("0") + "," + _overlay.Position.Y.ToString("0"));
            }
            catch (Exception e) { ZLog.W("overlay fail " + e.Message); }
        }

        static readonly List<CanvasItem> HiddenCreature = new List<CanvasItem>();

        static void HideSpineRecursive(Node n, int depth)
        {
            if (n == null || depth > 4) return;
            try
            {
                foreach (var c in n.GetChildren())
                {
                    var cn = c as Node;
                    if (cn == null) continue;
                    string nm = cn.Name.ToString();
                    string tn = cn.GetType().Name;
                    if (nm.Contains("Visuals") || nm.Contains("Spine") || tn.Contains("Spine"))
                    {
                        var ci = cn as CanvasItem;
                        if (ci != null && ci.Visible) { ci.Visible = false; HiddenCreature.Add(ci); }
                    }
                    HideSpineRecursive(cn, depth + 1);
                }
            }
            catch { }
        }

        public static void HidePlayerCreature(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            try
            {
                if (player == null || player.Creature == null) return;
                var node = player.Creature.GetCreatureNode();
                if (node == null) { ZLog.W("no creature node yet"); return; }
                HideSpineRecursive(node, 0);
                try
                {
                    var ostyC = player.Osty;
                    if (ostyC != null)
                    {
                        var on = ostyC.GetCreatureNode();
                        if (on != null && GodotObject.IsInstanceValid(on)) { on.Visible = false; on.Modulate = new Color(1f, 1f, 1f, 0f); }
                    }
                }
                catch { }
                ZLog.W("hid player spine nodes=" + HiddenCreature.Count);
            }
            catch (Exception e) { ZLog.W("hide player fail " + e.Message); }
        }

        /// 清理已经死亡的敌人节点（直接 LoseHpInternal 打死时引擎不会自动移除尸体）

        // ===== 屏幕实时放大（Godot 4 hint_screen_texture）=====
        static CanvasLayer _zoomLayer;
        static ColorRect _zoomRect;
        static ShaderMaterial _zoomMat;
        static Timer _zoomTimer;
        static float _zDur, _zT;
        static float _z0, _z1;          // zoom 起止
        static ulong _animStartMs;      // 演出起始时间（按真实时间推进，低帧率不拖慢）
        static float _f0, _f1;          // focus 起止（旧 API，仅 Y）
        static float _fx0 = 0.5f, _fx1 = 0.5f;   // focus X 起止
        static float _bt, _bb;          // 横带上下边界（0~1）
        static bool _zActive;

        const string ZoomShader = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform float zoom = 1.0;
uniform float focus_x = 0.5;
uniform float focus_y = 0.5;
uniform float band_top = 0.0;
uniform float band_bottom = 1.0;
uniform float alpha = 1.0;
uniform float vignette = 0.0;     // 暗角强度
uniform float pulse = 0.0;        // 红光脉冲
void fragment() {
    vec2 uv = SCREEN_UV;
    if (uv.y < band_top || uv.y > band_bottom) { COLOR = vec4(0.0); }
    else {
        vec2 c = vec2(focus_x, focus_y);
        vec2 z = (uv - c) / max(zoom, 1.0) + vec2(0.5, 0.5);   // 把焦点拉到屏幕中心
        vec3 col = texture(screen_tex, z).rgb;
        // 暗角
        float d = distance(uv, vec2(0.5, (band_top + band_bottom) * 0.5)) / 0.75;
        col *= mix(1.0, smoothstep(1.15, 0.25, d), vignette);
        // 红光脉冲（边缘更强）
        col = mix(col, vec3(0.75, 0.05, 0.05), pulse * smoothstep(0.35, 1.05, d));
        // 横带上下缘的暗影
        float edge = min(uv.y - band_top, band_bottom - uv.y);
        col *= mix(0.45, 1.0, smoothstep(0.0, 0.06, edge));
        COLOR = vec4(col, alpha);
    }
}
";

        public static void EnsureScreenZoom()
        {
            if (_zoomLayer != null) return;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                var sh = new Shader();
                sh.Code = ZoomShader;
                _zoomMat = new ShaderMaterial();
                _zoomMat.Shader = sh;
                _zoomLayer = new CanvasLayer();
                _zoomLayer.Layer = 92;
                // 必须先拷贝屏幕，否则采不到本帧内容
                var bb = new BackBufferCopy();
                bb.CopyMode = BackBufferCopy.CopyModeEnum.Viewport;
                _zoomLayer.AddChild(bb);
                _zoomRect = new ColorRect();
                _zoomRect.Color = new Color(1, 1, 1, 1);
                _zoomRect.Material = _zoomMat;
                _zoomRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                _zoomRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _zoomRect.Visible = false;
                _zoomLayer.AddChild(_zoomRect);
                tree.Root.AddChild(_zoomLayer);
                _zoomTimer = new Timer();
                _zoomTimer.WaitTime = 1.0 / 60.0;
                _zoomTimer.Autostart = false;
                _zoomTimer.Timeout += OnZoomTick;
                _zoomLayer.AddChild(_zoomTimer);
                ZLog.W("screen zoom layer ready");
            }
            catch (Exception e) { ZLog.W("screen zoom init fail " + e.Message); }
        }

        /// 实时放大横带内的游戏画面：zoom 从 z0→z1，焦点从 f0→f1，dur 秒
        public static void PlayScreenZoom(float z0, float z1, float f0, float f1, float dur, float bandTop = 0.06f, float bandBottom = 0.75f)
        {
            try
            {
                EnsureScreenZoom();
                if (_zoomRect == null || _zoomMat == null) return;
                _z0 = z0; _z1 = z1; _f0 = f0; _f1 = f1;
                _zDur = Math.Max(0.05f, dur); _zT = 0f;
                _bt = bandTop; _bb = bandBottom;
                _zActive = true;
                _zoomRect.Visible = true;
                ApplyZoom(z0, f0);
                if (_zoomTimer != null) _zoomTimer.Start();
                ZLog.W("screen zoom start " + z0 + "->" + z1 + " focus " + f0 + "->" + f1 + " dur=" + dur);
            }
            catch (Exception e) { ZLog.W("screen zoom play fail " + e.Message); }
        }

        /// 带 X 焦点的版本（瞄准目标用）
        public static void PlayScreenZoomXY(float z0, float z1, float fx0, float fy0, float fx1, float fy1, float dur, float bandTop = 0.05f, float bandBottom = 0.95f)
        {
            try
            {
                EnsureScreenZoom();
                if (_zoomRect == null || _zoomMat == null) return;
                _z0 = z0; _z1 = z1;
                _fx0 = fx0; _fx1 = fx1; _f0 = fy0; _f1 = fy1;
                _zDur = Math.Max(0.05f, dur); _zT = 0f;
                _bt = bandTop; _bb = bandBottom;
                _zActive = true;
                _zoomRect.Visible = true;
                ApplyZoomXY(z0, fx0, fy0);
                if (_zoomTimer != null) _zoomTimer.Start();
                ZLog.W("zoom-xy " + z0 + "->" + z1 + " focus " + fx0 + "," + fy0 + " -> " + fx1 + "," + fy1 + " dur=" + dur);
            }
            catch (Exception e) { ZLog.W("zoom-xy fail " + e.Message); }
        }

        static void ApplyZoomXY(float z, float fx, float fy)
        {
            if (_zoomMat == null) return;
            _zoomMat.SetShaderParameter("zoom", z);
            _zoomMat.SetShaderParameter("focus_x", fx);
            _zoomMat.SetShaderParameter("focus_y", fy);
            _zoomMat.SetShaderParameter("band_top", _bt);
            _zoomMat.SetShaderParameter("band_bottom", _bb);
            _zoomMat.SetShaderParameter("alpha", 1.0f);
        }

        static void ApplyZoom(float z, float f)
        {
            if (_zoomMat == null) return;
            _zoomMat.SetShaderParameter("zoom", z);
            _zoomMat.SetShaderParameter("focus_x", 0.5f);
            _zoomMat.SetShaderParameter("focus_y", f);
            _zoomMat.SetShaderParameter("band_top", _bt);
            _zoomMat.SetShaderParameter("band_bottom", _bb);
            _zoomMat.SetShaderParameter("alpha", 1.0f);
        }

        public static void SetZoomVignette(float v)
        {
            try { if (_zoomMat != null) _zoomMat.SetShaderParameter("vignette", v); } catch { }
        }

        /// 红光脉冲（0~1），收获出伤害时打一下
        public static void SetZoomPulse(float v)
        {
            try { if (_zoomMat != null) _zoomMat.SetShaderParameter("pulse", v); } catch { }
        }

        static void OnZoomTick()
        {
            try
            {
                if (!_zActive) { StopScreenZoom(); return; }
                _zT += 1f / 60f;
                float p = Math.Min(1f, _zT / _zDur);
                float e = p * p * (3f - 2f * p);
                ApplyZoomXY(_z0 + (_z1 - _z0) * e, _fx0 + (_fx1 - _fx0) * e, _f0 + (_f1 - _f0) * e);
                if (p >= 1f) { _zActive = false; StopScreenZoom(); }
            }
            catch (Exception e) { ZLog.W("zoom tick fail " + e.Message); StopScreenZoom(); }
        }

        public static void StopScreenZoom()
        {
            _zActive = false;
            try
            {
                if (_zoomTimer != null) _zoomTimer.Stop();
                if (_zoomRect != null) _zoomRect.Visible = false;
            }
            catch { }
        }

        /// 刷新所有敌人的血条 UI（直接改 MaxHp 时血条不会自动更新）
        public static void RefreshEnemyHpBars()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                var cs = cm != null ? cm.DebugOnlyGetState() : null;
                if (cs == null || cs.Enemies == null) return;
                int n = 0;
                foreach (var e in cs.Enemies)
                {
                    if (e == null) continue;
                    var node = e.GetCreatureNode();
                    if (node == null) continue;
                    // 血条真实节点类型是 NCreatureStateDisplay（挂在 %HealthBar 上）
                    var bar = FindNodeByType(node, "NCreatureStateDisplay", 0);
                    if (bar == null) bar = FindNodeByType(node, "NHealthBar", 0);
                    if (bar == null) { ZLog.W("no hp node for " + e.GetType().Name); continue; }
                    foreach (var mn in new[] { "RefreshValues", "RefreshText" })
                    {
                        var mi = bar.GetType().GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                        if (mi != null) mi.Invoke(bar, null);
                    }
                    ZLog.W("hp node refreshed: " + e.MaxHp + "/" + e.CurrentHp);
                    n++;
                }
                if (n > 0) ZLog.W("hp bars refreshed: " + n);
            }
            catch (Exception e) { ZLog.W("hp bar refresh fail " + e.Message); }
        }

        /// 强制刷新手牌布局（清空/新增手牌后调用，修复"牌都挤在右半边"）
        public static void RefreshHandLayout()
        {
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree == null) return;
                var found = FindNodeByType(tree.Root, "NPlayerHand", 0);
                if (found == null) { ZLog.W("hand node not found"); return; }
                var t = found.GetType();
                // 先清掉可能卡住的"正在出牌"状态，否则手牌会全部不可点
                foreach (var mn in new[] { "CancelAllCardPlay", "UpdateHandDisabledState" })
                {
                    var mi2 = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                    if (mi2 != null) { mi2.Invoke(found, null); }
                }
                foreach (var mn in new[] { "ForceRefreshCardIndices", "RefreshLayout" })
                {
                    var mi = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                    if (mi != null) { mi.Invoke(found, null); ZLog.W("hand layout: " + mn); }
                }
            }
            catch (Exception e) { ZLog.W("hand layout fail " + e.Message); }
        }

        static Godot.Node FindNodeByType(Godot.Node root, string typeName, int depth)
        {
            if (root == null || depth > 12) return null;
            try
            {
                if (root.GetType().Name == typeName) return root;
                foreach (var c in root.GetChildren())
                {
                    var cn = c as Godot.Node;
                    if (cn == null) continue;
                    var r = FindNodeByType(cn, typeName, depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        public static void CleanupDeadEnemies()
        {
            try
            {
                var cs = MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null
                    ? MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() : null;
                if (cs == null || cs.Enemies == null) return;
                int removed = 0; bool anyAlive = false;
                foreach (var e in cs.Enemies)
                {
                    if (e == null) continue;
                    if (!e.IsDead)
                    {
                        anyAlive = true;
                        // 复活/被召唤回来的敌人：恢复可见
                        try
                        {
                            var n2 = e.GetCreatureNode();
                            if (n2 != null && GodotObject.IsInstanceValid(n2)) (n2 as CanvasItem).Visible = true;
                        }
                        catch { }
                        continue;
                    }
                    var node = e.GetCreatureNode();
                    if (node != null && GodotObject.IsInstanceValid(node))
                    {
                        // 只隐藏、不销毁：引擎的胜利流程还会用到这个节点
                        try { (node as CanvasItem).Visible = false; } catch { }
                        HideSpineRecursive(node, 0);
                        removed++;
                    }
                }
                if (removed > 0) ZLog.W("cleanup dead enemy nodes=" + removed);
                // 全灭时主动结算一次（直接扣血打死不会自动触发胜利判定）
                if (!anyAlive)
                {
                    TryResolveWin("all enemies dead");
                }
            }
            catch (Exception ex) { ZLog.W("cleanup dead fail " + ex.Message); }
        }

        /// 手动触发一次战斗胜利判定（用于绕过引擎死亡动画直接击杀的情况）
        public static void TryResolveWin(string why)
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null) return;
                System.Reflection.MethodInfo mi = null;
                foreach (var m in cm.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "CheckWinCondition") continue;
                    if (m.GetParameters().Length != 0) continue;   // 只取无参版本，避免 Ambiguous match
                    mi = m; break;
                }
                if (mi == null) { ZLog.W("nowin method"); return; }
                var res = mi.Invoke(cm, null);
                ZLog.W("manual win check (" + why + ") -> " + (res == null ? "void" : "task"));
            }
            catch (Exception e) { ZLog.W("manual win fail " + e.Message); }
        }

        public static void RestorePlayerCreature()
        {
            try
            {
                foreach (var h in HiddenCreature)
                {
                    try { if (h != null && GodotObject.IsInstanceValid(h)) h.Visible = true; } catch { }
                }
                if (HiddenCreature.Count > 0) ZLog.W("restored spine nodes=" + HiddenCreature.Count);
                HiddenCreature.Clear();
            }
            catch { }
        }

        public static bool OverlayVisible()
        {
            return _overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.Visible;
        }

        // ===== 一牌两用：战斗内二选一按钮 =====
        static CanvasLayer _choiceLayer;
        static int _choiceResult;

        public static int ChoiceResult { get { return _choiceResult; } set { _choiceResult = value; } }

        public static async System.Threading.Tasks.Task<int> Choose(string left, string right)
        {
            _choiceResult = -1;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return 0;
                var vp = tree.Root.GetVisibleRect().Size;
                _choiceLayer = new CanvasLayer();
                _choiceLayer.Layer = 85;
                tree.Root.AddChild(_choiceLayer);
                var box = new Godot.HBoxContainer();
                box.Position = new Vector2(vp.X * 0.5f - 320, vp.Y * 0.72f);
                box.Size = new Vector2(640, 90);
                box.AddThemeConstantOverride("separation", 24);
                _choiceLayer.AddChild(box);
                for (int i = 0; i < 2; i++)
                {
                    var b = new Godot.Button();
                    b.Text = i == 0 ? left : right;
                    b.CustomMinimumSize = new Vector2(300, 80);
                    b.AddThemeFontSizeOverride("font_size", 30);
                    int idx = i;
                    b.Pressed += () => { _choiceResult = idx; };
                    box.AddChild(b);
                }
                ZLog.W("choice shown: " + left + " / " + right);
                while (_choiceResult < 0)
                {
                    var t = tree.CreateTimer(0.05);
                    await t.ToSignal(t, "timeout");
                }
                var r = _choiceResult;
                HideChoice();
                ZLog.W("choice picked " + r);
                return r;
            }
            catch (Exception e) { ZLog.W("choice fail " + e.Message); HideChoice(); return 0; }
        }

        // 多选项（选择敌人等）
        public static async System.Threading.Tasks.Task<int> ChooseMany(string title, string[] labels)
        {
            _choiceResult = -1;
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return 0;
                var vp = tree.Root.GetVisibleRect().Size;
                _choiceLayer = new CanvasLayer();
                _choiceLayer.Layer = 85;
                tree.Root.AddChild(_choiceLayer);
                var box = new Godot.VBoxContainer();
                box.Position = new Vector2(vp.X * 0.5f - 260, vp.Y * 0.55f);
                box.AddThemeConstantOverride("separation", 14);
                _choiceLayer.AddChild(box);
                if (!string.IsNullOrEmpty(title))
                {
                    var lbl = new Godot.Label();
                    lbl.Text = title;
                    lbl.AddThemeFontSizeOverride("font_size", 26);
                    box.AddChild(lbl);
                }
                for (int i = 0; i < labels.Length; i++)
                {
                    var b = new Godot.Button();
                    b.Text = labels[i];
                    b.CustomMinimumSize = new Vector2(520, 64);
                    b.AddThemeFontSizeOverride("font_size", 26);
                    int idx = i;
                    b.Pressed += () => { _choiceResult = idx; };
                    box.AddChild(b);
                }
                ZLog.W("choose-many shown n=" + labels.Length);
                while (_choiceResult < 0)
                {
                    var t = tree.CreateTimer(0.05);
                    await t.ToSignal(t, "timeout");
                }
                var r = _choiceResult;
                HideChoice();
                return r;
            }
            catch (Exception e) { ZLog.W("choose-many fail " + e.Message); HideChoice(); return 0; }
        }

        public static void HideChoice()
        {
            try { if (_choiceLayer != null && GodotObject.IsInstanceValid(_choiceLayer)) _choiceLayer.QueueFree(); } catch { }
            _choiceLayer = null;
        }

        // ===== 强化敌人开关 =====
        // 0=关  1=普通（5/10/20 倍）  2=测试（4000 倍，方便测机制）
        public static int BuffMode = 0;   // 已停用（强化敌人开关移除）
        public static bool BuffEnemies => BuffMode != 0;
        static Button _diffBtn;

        public static void ShowDifficultyToggle(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            if (true) return;   // 开关已按需求移除
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                if (_diffBtn != null && GodotObject.IsInstanceValid(_diffBtn)) return;
                var vp = tree.Root.GetVisibleRect().Size;
                var layer = new CanvasLayer();
                layer.Layer = 94;
                tree.Root.AddChild(layer);
                _diffBtn = new Button();
                _diffBtn.Text = BuffMode == 0 ? "强化敌人：关"
                              : (BuffMode == 1 ? "强化敌人：开" : "测试血量：4000×");
                _diffBtn.Position = new Vector2(24, vp.Y - 210);
                _diffBtn.Size = new Vector2(260, 56);
                _diffBtn.AddThemeFontSizeOverride("font_size", 22);
                _diffBtn.Pressed += () =>
                {
                    BuffMode = (BuffMode + 1) % 3;
                    _diffBtn.Text = BuffMode == 0 ? "强化敌人：关"
                              : (BuffMode == 1 ? "强化敌人：开" : "测试血量：4000×");
                    ZLog.W("buff mode = " + BuffMode);
                    try { ZhijiPatcher.ReapplyEnemyBuff(); } catch { }
                };
                layer.AddChild(_diffBtn);
                ZLog.W("difficulty toggle shown");
            }
            catch (Exception e) { ZLog.W("difficulty toggle fail " + e.Message); }
        }

        // ===== 测试用：一键获得「巨大扭蛋」 =====
        static CanvasLayer _relicBtnLayer;

        public static void ShowRelicButton(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || player == null) return;
                if (_relicBtnLayer != null && GodotObject.IsInstanceValid(_relicBtnLayer)) return;
                var vp = tree.Root.GetVisibleRect().Size;
                _relicBtnLayer = new CanvasLayer();
                _relicBtnLayer.Layer = 95;
                tree.Root.AddChild(_relicBtnLayer);
                var b = new Button();
                b.Text = "获得巨大扭蛋";
                b.Position = new Vector2(24, vp.Y - 120);
                b.Size = new Vector2(300, 64);
                b.AddThemeFontSizeOverride("font_size", 24);
                b.Pressed += () =>
                {
                    try
                    {
                        var canonical = MegaCrit.Sts2.Core.Models.ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.ToyBox>();
                        if (canonical == null) { ZLog.W("toybox model null"); return; }
                        var model = canonical.ToMutable();   // 关键：canonical 不可变，必须取 mutable 副本
                        var t = MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain(model, player);
                        _ = t.ContinueWith(x =>
                        {
                            ZLog.W("toybox obtain status=" + x.Status);
                            if (x.Exception != null) ZLog.W("toybox ex: " + x.Exception.GetBaseException());
                        });

                        ZLog.W("debug: obtaining ToyBox");
                    }
                    catch (Exception e) { ZLog.W("obtain toybox fail " + e.Message); }
                };
                _relicBtnLayer.AddChild(b);
                ZLog.W("relic button shown");
            }
            catch (Exception e) { ZLog.W("relic button fail " + e.Message); }
        }

        // ===== 全屏变身演出（用 Godot Timer 驱动，逐帧懒加载） =====
        static Godot.Timer _animTimer;
        static Godot.CanvasLayer _animLayer;
        static Godot.TextureRect _animRect;
        static int _animIndex;
        static int _animTotal;

        static string _animFolder = "transform";

        // ===== 变身帧后台预取（3.3.6）=====
        // 实测（PC/Xvfb）：单帧 File+Webp 解码+建纹理 avg 5ms / max 10ms（t:bench:30 → 149ms/30）。
        // 播放是按真实时间跳帧的（低帧率时一次跳很多帧），所以"顺序预取"命中率极低；
        // 这里改为**单槽预测**：主线程每 tick 预测下一帧号，后台线程只解码那一帧。
        static readonly System.Collections.Generic.Dictionary<int, Godot.Image> _pre =
            new System.Collections.Generic.Dictionary<int, Godot.Image>();
        static readonly object _preLock = new object();
        static System.Threading.Thread _preThread;
        static volatile int _preToken;
        static volatile int _preWant = -1;        // 主线程写入的"想要的帧号"
        static string _preFolder;
        const int PreBufferMax = 4;               // 最多缓存 4 帧（1280x720 RGBA ≈ 3.7MB/帧）
        static int _animPrefetchHits, _animTotalFrames, _animStalls, _animMaxFrameMs;
        static int _animLastIndex;
        public static bool NoPrefetch;            // 调试：true = 走旧的同步逐帧加载

        static void StopPrefetch()
        {
            try
            {
                _preToken++;
                _preThread = null;
                _preWant = -1;
                lock (_preLock)
                {
                    foreach (var kv in _pre) { try { kv.Value?.Dispose(); } catch { } }
                    _pre.Clear();
                }
            }
            catch { }
        }

        static void StartPrefetch(string folder)
        {
            try
            {
                StopPrefetch();
                _preFolder = folder;
                int token = _preToken;
                _preThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        while (token == _preToken)
                        {
                            int want = _preWant;
                            if (want < 0 || want > 900) { System.Threading.Thread.Sleep(2); continue; }
                            bool have;
                            lock (_preLock) have = _pre.ContainsKey(want);
                            if (have) { System.Threading.Thread.Sleep(2); continue; }
                            var img = Art.GetImage(folder, want);
                            if (img == null) { System.Threading.Thread.Sleep(5); continue; }
                            lock (_preLock)
                            {
                                if (!_pre.ContainsKey(want)) _pre[want] = img;
                                else { try { img.Dispose(); } catch { } }
                                // 缓存超限：丢掉最旧的
                                while (_pre.Count > PreBufferMax)
                                {
                                    int oldest = int.MaxValue;
                                    foreach (var k in _pre.Keys) if (k < oldest) oldest = k;
                                    try { _pre[oldest]?.Dispose(); } catch { }
                                    _pre.Remove(oldest);
                                }
                            }
                        }
                    }
                    catch { }
                });
                _preThread.IsBackground = true;
                _preThread.Start();
            }
            catch (Exception e) { ZLog.W("prefetch start fail " + e.Message); }
        }

        // 主线程取一帧已解码的图（无则返回 null，调用方回退到同步加载）
        static Godot.Texture2D TakePrefetched(int index)
        {
            Godot.Image img = null;
            lock (_preLock)
            {
                if (_pre.TryGetValue(index, out img)) _pre.Remove(index);
            }
            if (img == null) return null;
            try { return Godot.ImageTexture.CreateFromImage(img); }
            finally { try { img.Dispose(); } catch { } }
        }

        public static int TransformPlayed;
        public static void ResetTransformCount() { TransformPlayed = 0; }

        public static void PlayTransform(string folder = "transform")
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                StopTransform();
                // 3.3.19：变身演出开关（user://zhiji_prefs.json）
                ZhijiPrefs.Load();
                // 3.3.27：开关对**所有**变形演出生效（含 transform_back 与任何调用方）
                if (!ZhijiPrefs.AnimOn)
                {
                    if (folder == "transform") TransformPlayed++;
                    ZLog.W("transform anim disabled, skip folder=" + folder + " (#" + TransformPlayed + ")");
                    return;
                }
                // 第二次及以后：原地变身，不再放全屏演出
                if (folder == "transform" && TransformPlayed >= 1)
                {
                    TransformPlayed++;
                    ZLog.W("transform in place (#" + TransformPlayed + ")");
                    return;
                }
                if (folder == "transform") TransformPlayed++;
                _animFolder = folder;
                var first = Art.GetSequence(folder, 0);
                if (first == null) { ZLog.W("no transform frames"); return; }
                _animLayer = new CanvasLayer();
                _animLayer.Layer = 90;
                tree.Root.AddChild(_animLayer);
                var vp = tree.Root.GetVisibleRect().Size;
                _animRect = new TextureRect();
                _animRect.Position = Vector2.Zero;
                _animRect.Size = vp;
                _animRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                _animRect.StretchMode = TextureRect.StretchModeEnum.Scale;
                _animRect.MouseFilter = Control.MouseFilterEnum.Ignore;
                _animRect.Texture = first;
                _animLayer.AddChild(_animRect);
                _animIndex = 0;
                _animLastIndex = 0;
                _animTotal = 0;
                _animPrefetchHits = 0; _animTotalFrames = 0; _animStalls = 0; _animMaxFrameMs = 0;
                if (!NoPrefetch) StartPrefetch(folder);
                _animStartMs = Godot.Time.GetTicksMsec();
                _animTimer = new Timer();
                _animTimer.WaitTime = (1.0 / 30.0) * (ZhijiPrefs.AnimSpeed >= 2 ? 2.0 : 1.0);   // 2 倍速
                _animTimer.Autostart = true;
                _animTimer.Timeout += OnAnimTick;
                tree.Root.AddChild(_animTimer);
                ZLog.W("transform anim start size=" + vp);
            }
            catch (Exception e) { ZLog.W("transform play fail " + e.Message); }
        }

        static void OnAnimTick()
        {
            try
            {
                // 空格/Esc/鼠标左键：跳过变身演出
                try
                {
                    if (Godot.Input.IsKeyPressed(Godot.Key.Space) || Godot.Input.IsKeyPressed(Godot.Key.Escape) || Godot.Input.IsMouseButtonPressed(Godot.MouseButton.Left))
                    {
                        ZLog.W("transform anim skipped by input");
                        StopTransform();
                        return;
                    }
                }
                catch { }
                // 按真实时间计算帧号：低帧率时直接跳帧，不会把演出拖长
                ulong now = Godot.Time.GetTicksMsec();
                int target = (int)((now - _animStartMs) / 1000.0 * 30.0);
                if (target > _animIndex) _animIndex = target;
                else _animIndex++;
                ulong t0 = Godot.Time.GetTicksMsec();
                // 预测下一帧号：按上一次 tick 的跳帧步长
                try
                {
                    int step = _animIndex - _animLastIndex;
                    if (step < 1) step = 1;
                    _preWant = _animIndex + step;
                }
                catch { }
                var tex = TakePrefetched(_animIndex);
                bool hit = tex != null;
                if (tex == null) tex = Art.GetSequence(_animFolder, _animIndex);
                ulong cost = Godot.Time.GetTicksMsec() - t0;
                if (tex == null || _animIndex > 900)
                {
                    ZLog.W("transform anim done at " + _animIndex
                        + " frames=" + _animTotalFrames + " prefetchHits=" + _animPrefetchHits
                        + " stalls=" + _animStalls + " maxFrameMs=" + _animMaxFrameMs);
                    StopTransform();
                    return;
                }
                if (hit) _animPrefetchHits++;
                _animLastIndex = _animIndex;
                _animTotalFrames++;
                if ((int)cost > _animMaxFrameMs) _animMaxFrameMs = (int)cost;
                if (cost > 12ul) _animStalls++;
                _animRect.Texture = tex;
            }
            catch (Exception e) { ZLog.W("anim tick fail " + e.Message); StopTransform(); }
        }

        public static void StopTransform()
        {
            try
            {
                StopPrefetch();
                if (_animTimer != null && GodotObject.IsInstanceValid(_animTimer)) { _animTimer.Stop(); _animTimer.QueueFree(); }
                _animTimer = null;
                if (_animLayer != null && GodotObject.IsInstanceValid(_animLayer)) _animLayer.QueueFree();
                _animLayer = null; _animRect = null; _animIndex = 0;
            }
            catch { }
        }

        static CanvasLayer _diceLayer;
        static Label _diceLabel;
        static Timer _diceTimer;
        static int _diceFrame;
        static int _diceFinal;
        static string _diceText;

        // 骰子演出：快速跳动 -> 定格 -> 上浮淡出
        public static void ShowDice(int value, string label)
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                var vp = tree.Root.GetVisibleRect().Size;
                if (_diceLayer == null || !GodotObject.IsInstanceValid(_diceLayer))
                {
                    _diceLayer = new CanvasLayer();
                    _diceLayer.Layer = 88;
                    tree.Root.AddChild(_diceLayer);
                    _diceLabel = new Label();
                    _diceLabel.AddThemeFontSizeOverride("font_size", 110);
                    _diceLabel.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.3f));
                    _diceLabel.AddThemeColorOverride("font_outline_color", new Color(0.15f, 0.02f, 0.02f));
                    _diceLabel.AddThemeConstantOverride("outline_size", 14);
                    _diceLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _diceLayer.AddChild(_diceLabel);

                    _diceTimer = new Timer();
                    _diceTimer.WaitTime = 0.075;
                    _diceTimer.Timeout += DiceTick;
                    _diceLayer.AddChild(_diceTimer);
                }
                _diceFinal = value;
                _diceText = label;
                _diceFrame = 0;
                _diceLabel.Text = "?";
                _diceLabel.Visible = true;
                _diceLabel.Modulate = new Color(1, 1, 1, 1);
                _diceLabel.Scale = Vector2.One;
                _diceLabel.Position = new Vector2(vp.X * 0.44f, vp.Y * 0.26f);
                _diceTimer.Start();
            }
            catch (Exception e) { ZLog.W("dice ui fail " + e.Message); }
        }

        static void DiceTick()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || _diceLabel == null) return;
                _diceFrame++;
                if (_diceFrame <= 7)
                {
                    var rnd = new Random();
                    _diceLabel.Text = rnd.Next(1, 7).ToString();
                    var s = 0.9f + (float)rnd.NextDouble() * 0.25f;
                    _diceLabel.Scale = new Vector2(s, s);
                    return;
                }
                if (_diceFrame == 8)
                {
                    _diceLabel.Text = _diceFinal + "   " + _diceText;
                    _diceLabel.Position = new Vector2(tree.Root.GetVisibleRect().Size.X * 0.36f,
                                                      tree.Root.GetVisibleRect().Size.Y * 0.26f);
                    var tw = _diceLabel.CreateTween();
                    tw.SetParallel(true);
                    tw.TweenProperty(_diceLabel, "scale", new Vector2(1.25f, 1.25f), 0.12f);
                    tw.Chain().TweenProperty(_diceLabel, "scale", Vector2.One, 0.12f);
                    return;
                }
                if (_diceFrame >= 20)
                {
                    _diceTimer.Stop();
                    var tw2 = _diceLabel.CreateTween();
                    tw2.SetParallel(true);
                    tw2.TweenProperty(_diceLabel, "position:y", _diceLabel.Position.Y - 70f, 0.35f);
                    tw2.TweenProperty(_diceLabel, "modulate:a", 0.0f, 0.35f);
                    tw2.Chain().TweenCallback(Callable.From(() => { try { _diceLabel.Visible = false; } catch { } }));
                }
            }
            catch (Exception e) { ZLog.W("dice tick fail " + e.Message); }
        }

        // 商店等全屏 UI 打开时，把战斗立绘层压到 UI 下面（layer -1），而不是直接隐藏
        public static void SetOverlayBehindUi(bool behind)
        {
            try
            {
                if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
                int want = behind ? -1 : 1;
                if (_layer.Layer != want) _layer.Layer = want;
            }
            catch { }
        }

        public static bool IsOverlayVisible()
        {
            try { return _overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.Visible; }
            catch { return false; }
        }

        public static void HideOverlay()
        {
            try
            {
                _overlayWanted = false;
                if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) _overlay.Visible = false;
                ZLog.W("overlay hidden");
            }
            catch { }
        }

        public static void SetOverlayForm(bool red)
        {
            try
            {
                if (_overlay == null || !GodotObject.IsInstanceValid(_overlay) || !_overlay.Visible) return;
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                var vp = tree.Root.GetVisibleRect().Size;
                var frames = Frames(red ? RedP("idle") : KidPrefix("kid_idle"));
                if (frames == null || frames.GetFrameCount("default") == 0) return;
                // 已经是同一形态同一套帧：只保证在播放，不重复重算缩放/位置（避免立绘大小跳动）
                if (_red == red && _overlay.SpriteFrames == frames)
                {
                    if (_overlay.Animation != "default") _overlay.Play("default");
                    return;
                }
                _red = red;
                _overlay.SpriteFrames = frames;
                PlaceOverlay(vp, red);
                _overlay.Play("default");
                ZLog.W("overlay form red=" + red);
            }
            catch (Exception e) { ZLog.W("overlay form fail " + e.Message); }
        }

        // 攻击：前置冲刺 + 闪白 + 动作帧
        public static void Lunge(bool red)
        {
            try
            {
                if (_overlay == null || !GodotObject.IsInstanceValid(_overlay)) return;
                var tree = _overlay.GetTree();
                if (tree == null) return;
                var vp = tree.Root.GetVisibleRect().Size;
                float sc = _overlaySc;
                var basePos = _overlayBase != Vector2.Zero ? _overlayBase : _overlay.Position;
                var tw = _overlay.CreateTween();
                tw.SetTrans(Tween.TransitionType.Cubic);
                tw.TweenProperty(_overlay, "position", basePos + new Vector2(vp.X * 0.045f, 0f), 0.12f);
                tw.Parallel().TweenProperty(_overlay, "scale", new Vector2(sc * 1.06f, sc * 1.06f), 0.12f);
                tw.TweenProperty(_overlay, "position", basePos, 0.18f);
                tw.Parallel().TweenProperty(_overlay, "scale", new Vector2(sc, sc), 0.18f);
            }
            catch (Exception e) { ZLog.W("lunge fail " + e.Message); }
        }

        public static void OneShotOverlay(string prefix, float secs)
        {
            try
            {
                if (_overlay == null || !GodotObject.IsInstanceValid(_overlay) || !_overlay.Visible) return;
                var f = Frames(prefix);
                if (f == null || f.GetFrameCount("default") == 0) return;
                _overlay.SpriteFrames = f;
                _overlay.Play("default");
                var tree = _overlay.GetTree();
                if (tree == null) return;
                var t = tree.CreateTimer(secs);
                t.Timeout += () => { try { SetOverlayForm(_red); } catch { } };
            }
            catch (Exception e) { ZLog.W("overlay oneshot fail " + e.Message); }
        }

        public static bool IsRed() { return _red; }

        public static bool HasSprite()
        {
            return _sprite != null && GodotObject.IsInstanceValid(_sprite);
        }

        public static bool IsCombatVisuals(Node node)
        {
            try
            {
                var p = node;
                for (int i = 0; i < 12 && p != null; i++)
                {
                    string n = p.GetType().Name;
                    if (n.Contains("Combat")) return true;
                    p = p.GetParent();
                }
            }
            catch { }
            return false;
        }

        public static bool InCombat()
        {
            try
            {
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                if (cm == null) return false;
                return cm.IsInProgress || cm.IsStarting || cm.CurrentCombatId != null;
            }
            catch { return false; }
        }

        public static void HideSpine(Node parent)
        {
            try
            {
                Hidden.Clear();
                foreach (var c in parent.GetChildren())
                {
                    var cn = c as Node;
                    if (cn == null) continue;
                    var nm = cn.Name.ToString();
                    if (nm == "Visuals" || nm.Contains("Spine") || nm == "SpineSprite")
                    {
                        var ci = cn as CanvasItem;
                        if (ci != null && ci.Visible) { ci.Visible = false; Hidden.Add(ci); }
                    }
                }
                ZLog.W("hid spine nodes=" + Hidden.Count);
            }
            catch (Exception e) { ZLog.W("hide spine fail " + e.Message); }
        }

        public static bool Spawn(Node parent, bool red)
        {
            try
            {
                if (_sprite != null && GodotObject.IsInstanceValid(_sprite))
                {
                    if (_sprite.GetParent() == parent) return true;
                    _sprite.QueueFree();
                }
                _sprite = null;
                _red = red;
                _sprite = new AnimatedSprite2D();
                _sprite.Name = "ZhijiAnim";
                _sprite.SpriteFrames = Build(red ? RedP("idle") : KidPrefix("kid_idle"));
                _sprite.Centered = false;
                _sprite.ZIndex = 20;
                var s = red ? 1.50f : 0.62f;
                _sprite.Scale = new Vector2(s, s);
                _sprite.Position = new Vector2(red ? -300f : -110f, red ? -520f : -230f);
                parent.AddChild(_sprite);
                _sprite.Play("default");
                ZLog.W("animated sprite spawned red=" + red + " scale=" + _sprite.Scale.X + " pos=" + _sprite.Position.X + "," + _sprite.Position.Y);
                return true;
            }
            catch (Exception e) { ZLog.W("visuals spawn fail " + e.Message); return false; }
        }

        public static void Despawn()
        {
            try
            {
                if (_sprite != null && GodotObject.IsInstanceValid(_sprite)) _sprite.QueueFree();
                _sprite = null;
                foreach (var h in Hidden)
                {
                    try { if (h != null && GodotObject.IsInstanceValid(h)) h.Visible = true; } catch { }
                }
                Hidden.Clear();
                ZLog.W("sprite despawned");
            }
            catch (Exception e) { ZLog.W("despawn fail " + e.Message); }
        }

        static readonly Dictionary<string, SpriteFrames> Cache = new Dictionary<string, SpriteFrames>();

        public static int FrameCount(string prefix)
        {
            try { var f = Frames(prefix); return f != null ? f.GetFrameCount("default") : -1; } catch { return -1; }
        }

        static SpriteFrames Frames(string prefix)
        {
            SpriteFrames f;
            if (Cache.TryGetValue(prefix, out f) && f != null) return f;
            float fps = prefix == "gold_idle" ? 20f : 10f;   // 金色形态 GIF 是 40 帧/2 秒
            f = Build(prefix, fps);
            Cache[prefix] = f;
            return f;
        }

        public static void OneShot(string prefix, float secs)
        {
            try
            {
                if (_sprite == null || !GodotObject.IsInstanceValid(_sprite)) return;
                var f = Frames(prefix);
                if (f == null || f.GetFrameCount("default") == 0) return;
                _sprite.SpriteFrames = f;
                _sprite.Play("default");
                var spr = _sprite;
                var tree = _sprite.GetTree();
                if (tree == null) return;
                var timer = tree.CreateTimer(secs);
                timer.Timeout += () => { try { if (spr != null && GodotObject.IsInstanceValid(spr)) SetForm(_red); } catch { } };
            }
            catch (Exception e) { ZLog.W("oneshot fail " + prefix + " " + e.Message); }
        }

        public static void Attack()
        {
            string p1 = _red ? RedP("attack") : KidPrefix("kid_attack");
            if (Frames(p1) == null || Frames(p1).GetFrameCount("default") == 0) p1 = _red ? RedP("attack") : KidPrefix("kid_skill1");
            OneShotOverlay(p1, 0.55f);
            Lunge(_red);
        }

        public static void Defend()
        {
            OneShotOverlay(_red ? RedP("defend") : KidPrefix("kid_defend"), 0.5f);
            ZLog.W("anim defend red=" + _red);
        }

        static Godot.Label _popLabel;
        static float _popUntil;
        public static void Popup(string text, Godot.Color col)
        {
            try
            {
                if (_statLayer == null || !GodotObject.IsInstanceValid(_statLayer)) return;
                if (_popLabel == null || !GodotObject.IsInstanceValid(_popLabel))
                {
                    _popLabel = new Godot.Label();
                    _popLabel.MouseFilter = Godot.Control.MouseFilterEnum.Ignore;
                    _popLabel.AddThemeFontSizeOverride("font_size", 26);
                    _popLabel.AddThemeColorOverride("font_color", col);
                    _statLayer.AddChild(_popLabel);
                }
                _popLabel.Text = text;
                _popLabel.Visible = true;
                var vp = _statLayer.GetViewport().GetVisibleRect().Size;
                _popLabel.Position = new Vector2(vp.X * 0.14f, vp.Y * 0.42f);
                _popUntil = (float)Godot.Time.GetTicksMsec() / 1000f + 1.6f;
                // 独立计时器隐藏：不依赖其他刷新循环
                try
                {
                    var treeP = Engine.GetMainLoop() as SceneTree;
                    if (treeP != null)
                    {
                        var tP = treeP.CreateTimer(1.6);
                        tP.Timeout += () =>
                        {
                            try
                            {
                                if (_popLabel != null && GodotObject.IsInstanceValid(_popLabel)) _popLabel.Visible = false;
                            }
                            catch { }
                        };
                    }
                }
                catch { }
            }
            catch { }
        }
        public static void TickPopup()
        {
            try
            {
                if (_popLabel == null || !GodotObject.IsInstanceValid(_popLabel)) return;
                if (!_popLabel.Visible) return;
                if ((float)Godot.Time.GetTicksMsec() / 1000f > _popUntil) { _popLabel.Visible = false; return; }
                _popLabel.Position += new Vector2(0, -0.6f);
            }
            catch { }
        }

        public static void Dodge()
        {
            try
            {
                ZLog.W("anim dodge red=" + _red);
                if (_red)
                {
                    var f = Frames(RedP("dodge"));
                    if (f != null && f.GetFrameCount("default") > 0) { OneShotOverlay(RedP("dodge"), 0.45f); return; }
                }
                var kd = KidPrefix("kid_dodge");
                var kf = Frames(kd);
                OneShotOverlay((kf != null && kf.GetFrameCount("default") > 0) ? kd : KidPrefix("kid_defend"), 0.45f);
            }
            catch { }
        }

        public static void Hit()
        {
            try
            {
                if (_red) { OneShotOverlay(RedP("hit"), 0.45f); ZLog.W("anim hit red=True"); return; }
                string kh = KidPrefix("kid_hit");
                var f = Frames(kh);
                if (f != null && f.GetFrameCount("default") > 0) { OneShotOverlay(kh, 0.45f); ZLog.W("anim hit kid"); return; }
                OneShotOverlay(KidPrefix("kid_defend"), 0.4f); ZLog.W("anim hit kid(fallback)");
            }
            catch { }
        }

        public static void SetForm(bool red)
        {
            try
            {
                _red = red;
                if (_sprite == null || !GodotObject.IsInstanceValid(_sprite)) return;
                _sprite.SpriteFrames = Build(red ? RedP("idle") : KidPrefix("kid_idle"));
                var s = red ? 1.50f : 0.62f;
                _sprite.Scale = new Vector2(s, s);
                _sprite.Position = new Vector2(red ? -300f : -110f, red ? -520f : -230f);
                _sprite.Play("default");
                ZLog.W("form switched red=" + red);
            }
            catch (Exception e) { ZLog.W("form switch fail " + e.Message); }
        }

        public static void Flash(Node parent)
        {
            try
            {
                // 变身瞬闪：拉一个白色覆盖层再淡出
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                var rect = new ColorRect();
                rect.Color = new Color(1, 0.85f, 0.85f, 0.85f);
                rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                rect.MouseFilter = Control.MouseFilterEnum.Ignore;
                tree.Root.AddChild(rect);
                var tw = rect.CreateTween();
                tw.TweenProperty(rect, "color:a", 0.0f, 0.45f);
                tw.TweenCallback(Callable.From(() => { try { rect.QueueFree(); } catch { } }));
            }
            catch (Exception e) { ZLog.W("flash fail " + e.Message); }
        }
    }
}

namespace Zhiji
{
    /// 每帧兜底：不在战斗中就把小人藏起来（不依赖任何事件是否触发）
    public partial class OverlayWatcher : Godot.Node
    {
        public override void _Process(double delta)
        {
            try { Visuals.TickPopup(); } catch { }
            try { Visuals.EventArtTickPublic(); } catch { }
            try { Visuals.SyncUiOcclusion(); } catch { }
            try { Visuals.RefreshHandValues(); } catch { }
            try
            {
                if (!Visuals.OverlayVisible()) return;
                var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
                bool inCombat = cm != null && !cm.IsEnding && !cm.IsOverOrEnding && !cm.IsAboutToLose
                    && (cm.IsInProgress || cm.IsStarting || cm.CurrentCombatId != null);
                if (inCombat)
                {
                    // 敌人全灭（结算界面已出现）→ 立刻隐藏
                    try
                    {
                        var st = cm.DebugOnlyGetState();
                        if (st != null && st.Enemies != null)
                        {
                            bool anyAlive = false;
                            foreach (var e in st.Enemies) if (e != null && !e.IsDead) { anyAlive = true; break; }
                            if (!anyAlive) inCombat = false;
                        }
                    }
                    catch { }
                }
                if (!inCombat) Visuals.HideOverlay();
            }
            catch { }
        }
    }
}

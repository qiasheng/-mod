using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Zhiji
{
    /// 血肉高跷：每回合开始+4搏动；变身额外+25自助餐
    public sealed class FleshStilts : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血色舞台：自助餐增伤+1且无上限；变身后每回合结束失去4生命
    public sealed class CrimsonStage : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血玉酒杯：每回合开始时，若敌人身上有流血，回复 3 点生命
    public sealed class BloodGoblet : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 餐刀：每回合开始时回复 2 点生命（稳定续航）
    public sealed class CarvingKnife : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 无尽餐盘：每次变身时自助餐 +6（成长性）
    public sealed class EndlessPlate : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 黄金寿衣：永久保持幼年形态，封印无休止饥饿与变身；流血无上限、每回合只掉 1 层
    public sealed class GoldenShroud : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血肉砧板（DOT 流）：每次施加流血额外 +2，流血上限提高到 120；引爆伤害 +50%
    public sealed class BleedBoard : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 失控之心：永久红神，牌组获得「失控」附魔
    public sealed class UnboundHeart : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 饱食之胃：每回合第一次失去生命时，回复其一半
    public sealed class SatedStomach : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 红神之心：血肉进化为红神眷属（第二层事件奖励）
    public sealed class RedGodHeart : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Event;
    }

    /// 残影斗篷：闪避概率 +5%
    public sealed class ShadowCloak : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 旧镰刀：每场战斗开始获得 10 点收割
    public sealed class OldScythe : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 干粮袋：战斗开始饥饿 -20，回复 4 生命（稳一手，不急着变身）
    public sealed class RationBag : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 卑贱钱袋：战斗第一回合开始获得 1 层卑贱之物
    public sealed class VilePurse : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 饿钟：每回合开始饥饿 +1
    public sealed class HungerBell : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 卑贱王冠：每场战斗开始获得 2 层卑贱之物
    public sealed class VileCrown : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 卑贱怀表：每回合第一张卑贱卡额外 +1 层
    public sealed class VileWatch : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血之漏斗：每回合第一张施加流血的牌额外 +1
    public sealed class BloodFunnel : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 漆黑餐巾：战斗第一回合对所有敌人施加 2 层流血
    public sealed class BlackNapkin : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 猩红圣痕：红神形态每回合开始 +1 自助餐（把变身强度拆到遗物）
    public sealed class Stigmata : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血契之钉：每场战斗第一次施加流血额外 +2
    public sealed class BloodNail : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 战纹章：红神形态每回合 +2 自助餐
    public sealed class WarSigil : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 守纹章：红神形态每回合 +3 格挡
    public sealed class GuardSigil : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }

    /// 猩红面甲：变身时额外获得 10 点自助餐
    public sealed class CrimsonVisor : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 血之沙漏：战斗第一回合对所有敌人施加 2 层流血
    public sealed class BloodHourglass : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
    }

    /// 猩红摇篮：每场战斗开始 +3 搏动；红神形态每回合开始 +2 格挡
    public sealed class CrimsonCradle : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Rare;
    }

    /// 开饭铃（召唤流）：召唤物血量上限 +10、攻击 +4、每次挨打最多再挡 4 点
    public sealed class DinnerBell : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Uncommon;
    }
}
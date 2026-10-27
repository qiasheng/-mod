# 吱咿 Zhiji（Slay the Spire 2 Mod）

双形态角色 Mod：**常态·吱咿** 与 **觉醒·红神**。

- 常态：搏动、无休止饥饿、自助餐、戏服替身
- 觉醒：红神卡牌、失控附魔、召唤血肉
- 支线：流血 / 卑贱之物 / 黄金寿衣（Dot 流）
- 事件链：第一层「红神的摇篮」→ 第二层「红神之心」（血肉进化为眷属）

## 安装（玩家）

1. 下载 Release 中的 `zhiji_<版本>.zip`；
2. 删除旧的 `mods/zhiji` 目录；
3. 解压到游戏的 `mods/` 下，确保路径为 `mods/zhiji/zhiji.dll` 与 `mods/zhiji/zhiji.json`；
4. 启动游戏，日志出现 `entry build <版本>` 即加载成功。

日志位置：

- Android：`Android/data/com.megacrit.sts2re/files/zhiji_log.txt`
- Linux：`~/.local/share/SlayTheSpire2/zhiji_log.txt`（3.3.2 起）
- Windows：不写文件（避免每次日志触发一次异常，见 `docs/实现参数.md` §254.3）

## 构建（开发者）

依赖：**.NET SDK 9**（游戏程序集是 `net9.0`）+ 游戏程序集（`sts2.dll` 及同目录 dll）+ `GodotSharp.dll`。

```bash
# 1) 把游戏 dll 放进 refs/（sts2.dll 及其同目录所有 dll），再从 Godot 4.5.1 mono 补一个 GodotSharp.dll
mkdir -p refs
cp "<游戏>/data_sts2_linuxbsd_x86_64/"*.dll refs/
cp "<Godot mono>/GodotSharp/Api/Release/GodotSharp.dll" refs/

# 2) 构建
dotnet build zhiji.csproj          # 产物 bin/zhiji.dll
```

产物 `zhiji.dll`，与 `pkg/zhiji/` 一起打包为 mod：

```bash
tar czf zhiji.zip -C pkg zhiji
```

> `build.rsp`（旧版 `csc @build.rsp` 用法）保留作参考，里面是容器绝对路径，PC 上请用 `zhiji.csproj`。
> 本机无头测试流程见 `RUNBOOK-linux.md`。

## 目录结构

```
src/            Mod 源码（C# / Godot 4.5）
pkg/zhiji/      mod 清单 zhiji.json（art/ 美术资源不入库，见下）
docs/           实现细节与历次修复记录（实现参数.md）
build.rsp       编译参数
```

## 玩法机制（摘要）

| 机制 | 说明 |
|---|---|
| 搏动 | 红神资源：提升伤害与格挡 |
| 饥饿 | 达到 100 可触发形态变化 |
| 自助餐 | 召唤血肉的资源 |
| 血肉 / 眷属 | 召唤物，替本体承伤；眷属每回合自动攻击 |
| 替身 | 优先吸收伤害，每层挡 1 点，最多挡一半 |
| 空放 | 没有合法目标时自动改为防御效果 |
| 失控 | 伤害 +50%，打出后反伤 |
| 流血 | 每回合失去生命并受到层数伤害 |
| 卑贱之物 | 每回合造成伤害 |

卡面机制词已标黄，长按/选中卡牌可看到【机制说明】。

## 协作

- 提交 Issue 说明 bug / 建议；
- Fork → 分支 → PR（请勿直接推 `main`）；
- 详细规则见 [CONTRIBUTING.md](CONTRIBUTING.md)；
- 美术资源（`pkg/zhiji/art/`）与成品 zip **不入库**（版权原因），仓库只保留源码；
- 完整包（含美术）的下载请找咕咕。

## 版本约定

- `x.y.z`：x 大版本，y 功能批次，z 修复；
- 每次修复请在 `docs/实现参数.md` 追加一节，写清现象、原因、验证方式。

## 下载

本仓库只包含**源代码**。完整安装包（含美术资源）不在这里提供，**下载请找咕咕**。

## License

源码 MIT，见 [LICENSE](LICENSE)。
美术资源（`pkg/zhiji/art/`）不属于本仓库、也不在 MIT 许可范围内，版权归各自作者。

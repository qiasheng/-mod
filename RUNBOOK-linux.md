# Linux/PC 虚拟窗口运行手册

## 适用范围

> 适用于 PC/服务器（Linux）用 Xvfb 跑游戏本体做测试。

### 1 游戏与前置

```
游戏目录：/mnt/data/SteamLibrary/steamapps/common/Slay the Spire 2
```

- Steam 必须已登录并运行（离线模式需先启动过）；
- 无物理显示器时用 Xvfb（**实测请用 1920×1080**）：

```bash
Xvfb :99 -screen 0 1920x1080x24 &
export DISPLAY=:99
```

> 720p（`1280x720x24`）虽然 CPU 渲染更快，但游戏 UI 仍按 1920×1080 布局：
> 角色选择/模式选择的下半部分会被裁掉，按钮点不到（dead test 时踩过）。
> 只有在能确认按钮都在可视区内时才用 720p。

- 注意：Xvfb 被杀掉时，跑在它上面的 Steam 会一起退出；重启顺序必须是 Xvfb → Steam → 游戏。

### 2 启动游戏

```bash
cd "/mnt/data/SteamLibrary/steamapps/common/Slay the Spire 2"
DISPLAY=:99 SteamAppId=2868840 SteamGameId=2868840 \
  setsid nohup ./SlayTheSpire2 --rendering-driver opengl3 \
  > /tmp/sts_run.log 2>&1 < /dev/null &
sleep 55
```

- 启动约需 50–60 秒；
- 若报 `X11 Display is not available`，说明 Xvfb 没起来，先重启 Xvfb；
- 退出用 `pkill -9 -x SlayTheSpire2`。

### 3 窗口与输入（无窗口管理器）

```bash
W=$(xdotool search --name 'Slay the Spire 2' | tail -1)
xdotool windowfocus $W          # 不要用 windowactivate（无 WM 会失败）
xdotool mousemove 787 682 && xdotool click 1
import -window root /tmp/shot.png
```

- 坐标按 1920×1080 实测：
  | 位置 | 坐标 |
  |---|---|
  | 主菜单「继续游戏」 | `863 675` |
  | 主菜单「单人模式」 | `863 719` |
  | 「放弃」（有存档时在单人模式同一行下方） | `863 719`（有存档时先出现「放弃」） |
  | 标准模式卡片 | `558 421` |
  | 角色栏最后一位（吱咿） | `1270 972` |
  | 角色确认（右下勾） | `1853 807` |
  | 死亡结算「继续」 | `957 927` |
- 每次点击/输入后先截图确认，不要凭记忆连点；
- 无头环境下更稳的做法是写 `press:<按钮文字>` 调试命令，按标签点按钮，不依赖坐标。

### 4 Mod 部署

```bash
cd "/mnt/data/SteamLibrary/steamapps/common/Slay the Spire 2/mods"
rm -rf zhiji
tar xzf /tmp/zhiji_3.3.2.zip     # zip 内第一层必须是 zhiji/（当前版本 3.3.2）
```

### 5 日志与调试

- 引擎日志：`/tmp/sts_run.log`
- Mod 日志（3.3.2+）：`~/.local/share/SlayTheSpire2/zhiji_log.txt`
- 启动成功标志：日志出现 `entry build <版本>`
- 旧版本可能尝试写 Android 路径（Linux 上失败），不要据此判断崩溃

调试命令（写文件，**不要带换行**）：

```bash
printf 'give:FEAST_CALL' > ~/.local/share/SlayTheSpire2/zhiji_cmd
```

常用：`give:卡`、`play:卡`、`win`、`next`、`hunger:100`、`redgod`、`goldshroud`、`harvest`、`redheart`、`capsule`、`pulse:N`、`bleed:N`、`hurt:N`。

### 5.1 死亡结算回归（3.3.2 起）

```bash
# 战斗中点一下自伤即可
printf 'hurt:9999' > ~/.local/share/SlayTheSpire2/zhiji_cmd
# 期望日志（约 0.5s 内完成）
grep -E "death settle|ProcessPendingLoss|defeat:" ~/.local/share/SlayTheSpire2/zhiji_log.txt | tail -5
# 期望界面：死亡…… / 你的战斗结束了…… + 「继续」
```

一键脚本：

| 脚本 | 作用 |
|---|---|
| `/home/firefly/zhiji-start-combat.sh [--fresh]` | 启动游戏并进入战斗（自动判断菜单第一项：有档=继续 675 / 无档=单人 719） |
| `/home/firefly/zhiji-full-test.sh` | 新开一局 → 进战斗 → 备份存档 → 触发死亡 → 打印日志 |
| `/home/firefly/zhiji-red-test.sh` | 红神形态死亡贴图 |
| `/home/firefly/zhiji-shroud-test.sh` | 黄金寿衣「流血敌人无法回血」A/B/C 对照 |
| `/home/firefly/zhiji-gameover-test.sh` | 结算界面遮挡回归 |

段错误排查：`DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=4 DOTNET_DbgMiniDumpName=/tmp/crash.dmp` 启动游戏，
崩溃后会留下 dump；`dmesg -T | grep SlayTheSpire2` 可确认是否 segfault。

GPU 性能测试（一次性，会占前台）：本机 `DISPLAY=:1` + `--rendering-driver vulkan`，
窗口是全屏 3840×2160 且会**捕获鼠标**（xdotool 点不动）→ 用 `t:autoslay` 让引擎自己开局打牌；
测完 `pkill -9 -x SlayTheSpire2` 释放显卡。

> 踩坑记录：菜单第一项的位置随「是否有存档」变化（675=继续游戏 / 719=单人模式），
> 点错会点到「放弃」并弹确认框；脚本里已按存档存在性选择坐标。

### 6 构建

推荐 `zhiji.csproj`（3.3.2 起）：

```bash
# 一次性：把游戏程序集放进 refs/
mkdir -p refs
cp "<游戏>/data_sts2_linuxbsd_x86_64/"*.dll refs/
cp "<Godot 4.5.1 mono>/GodotSharp/Api/Release/GodotSharp.dll" refs/

dotnet build zhiji.csproj      # 需要 .NET SDK 9（游戏程序集是 net9.0）
```

- 产物 `bin/zhiji.dll` → 覆盖 `pkg/zhiji/zhiji.dll`，再打包 `pkg/zhiji`；
- 旧方式 `csc @build.rsp` 仍可参考，但 rsp 里是容器绝对路径，PC 上请用 csproj；
- 版本号两处同步：`src/ModEntry.cs` 的 `entry build x.y.z` 与 `pkg/zhiji/zhiji.json` 的 `version`。


### 7 注意事项

- 测试前先 `pkill` 旧游戏进程，避免多个实例抢 Steam；
- 不要直接在 `main` 改，先建分支；
- 改动前先读本文件与 `docs/实现参数.md`；
- 不要提交 `HANDOVER.md` 到公开仓库。

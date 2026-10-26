#!/usr/bin/env bash
# 标准测试入口：启动游戏 → 主菜单 → 单人 → 标准 → 探测选中吱咿 → 尝试多个确认坐标
# 成功判据：日志出现 RUN character=ZHIJI 或 room entered
set -uo pipefail
export DISPLAY=:99
GAME="/mnt/data/SteamLibrary/steamapps/common/Slay the Spire 2"
LOG=/home/firefly/.local/share/SlayTheSpire2/zhiji_log.txt
SAVEDIR="/home/firefly/.local/share/SlayTheSpire2/steam/76561199008656302/modded/profile1/saves"
CLICK() { xdotool mousemove "$1" "$2" click 1; sleep "${3:-4}"; }
last_select() { grep -oP 'SCREEN select \K[A-Z_]+' "$LOG" 2>/dev/null | tail -1; }
in_run() { grep -qE "RUN character=ZHIJI|room entered: MapRoom" "$LOG" 2>/dev/null; }

pkill -9 -x SlayTheSpire2 2>/dev/null
sleep 2
rm -f "$SAVEDIR/current_run.save" "$SAVEDIR/current_run.save.backup" "$LOG"
cd "$GAME"
SteamAppId=2868840 SteamGameId=2868840 setsid nohup ./SlayTheSpire2 \
  --rendering-driver opengl3 > /tmp/sts_start.log 2>&1 < /dev/null &
sleep 58
grep -m1 "entry build" /tmp/sts_start.log || { echo "启动失败"; exit 1; }

W=$(xdotool search --name 'Slay the Spire 2' | tail -1)
xdotool windowfocus "$W"

# 有存档时菜单第一项是「继续游戏」、第二项是「放弃」（会弹确认框）：
# 先点掉可能的确认框，再点第一项（有档=继续 / 无档=单人模式）
CLICK 800 722 2      # 不了（无弹框时无副作用）
CLICK 863 675 8      # 继续游戏 / 单人模式
if grep -qE "RUN character=ZHIJI|room entered" "$LOG" 2>/dev/null; then
  echo "直接进入已有存档：$(tail -1 "$LOG" | sed 's/^\[[0-9:.]*\] //')"
  exit 0
fi
CLICK 800 722 2
CLICK 863 719 6      # 单人模式（无档布局）
CLICK 558 421 7      # 标准模式

picked=""
for X in $(seq 580 55 1400); do
  CLICK "$X" 972 2
  S=$(last_select)
  if [ "$S" = "ZHIJI" ]; then picked=$X; echo "选中吱咿：x=$X"; break; fi
done
[ -z "$picked" ] && { echo "❌ 未能选中吱咿（最后=$S）"; exit 1; }

for pos in "1827 780" "1853 807" "1810 755" "1795 800" "1853 760"; do
  set -- $pos
  xdotool mousemove "$1" "$2" click 1
  sleep 5
  if in_run; then echo "确认成功：($1,$2)"; echo "状态：$(tail -1 "$LOG" | sed 's/^\[[0-9:.]*\] //')"; exit 0; fi
  echo "确认未生效：($1,$2) 最后选中=$(last_select)"
done

echo "❌ 所有确认坐标都未进入游戏"
tail -3 "$LOG" | sed 's/^\[[0-9:.]*\] //'
exit 1

#!/usr/bin/env bash
# 可靠开局：主菜单 → 单人 → 标准 → 逐个试点角色直到选中 ZHIJI → 确认
set -uo pipefail
export DISPLAY=:99
LOG=/home/firefly/.local/share/SlayTheSpire2/zhiji_log.txt
SAVEDIR="/home/firefly/.local/share/SlayTheSpire2/steam/76561199008656302/modded/profile1/saves"
CLICK() { xdotool mousemove "$1" "$2" click 1; sleep "${3:-4}"; }

pkill -9 -x SlayTheSpire2 2>/dev/null
sleep 2
rm -f "$SAVEDIR/current_run.save" "$SAVEDIR/current_run.save.backup"
rm -f "$LOG"
cd "/mnt/data/SteamLibrary/steamapps/common/Slay the Spire 2"
SteamAppId=2868840 SteamGameId=2868840 setsid nohup ./SlayTheSpire2 \
  --rendering-driver opengl3 > /tmp/sts_pick.log 2>&1 < /dev/null &
sleep 58
grep -m1 "entry build" /tmp/sts_pick.log

W=$(xdotool search --name 'Slay the Spire 2' | tail -1)
xdotool windowfocus "$W"
CLICK 863 719 6          # 单人模式
CLICK 558 421 7          # 标准模式

for X in 1270 1152 1034 913 795 666; do
  CLICK "$X" 972 3
  LAST=$(grep -oP 'SCREEN select \K[A-Z_]+' "$LOG" | tail -1)
  echo "try x=$X -> $LAST"
  [ "$LAST" = "ZHIJI" ] && break
done

if [ "$(grep -oP 'SCREEN select \K[A-Z_]+' "$LOG" | tail -1)" = "ZHIJI" ]; then
  CLICK 1853 807 14
  echo "confirmed ZHIJI run: $(grep -c 'RUN character=ZHIJI' "$LOG")"
else
  echo "❌ 没能选中 ZHIJI"
fi
tail -1 "$LOG" | sed 's/^\[[0-9:.]*\] //'

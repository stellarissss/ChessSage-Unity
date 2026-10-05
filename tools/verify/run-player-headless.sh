#!/usr/bin/env bash
# 在无 GPU 的 Linux 沙箱中运行已构建的播放器并截图（视觉验证）。
#
# 依赖：xvfb、mesa 软件渲染（libgl1-mesa-dri）、imagemagick（import）
#   apt-get install -y xvfb libgl1-mesa-dri mesa-utils imagemagick
#
# 说明：Unity 播放器不识别 -screenshot / -autoquit 这类自定义参数，
#       因此这里改为「Xvfb 固定分辨率 + 定时抓取整个 X 根窗口」。
#
# 用法：bash tools/verify/run-player-headless.sh [播放器路径] [输出图片] [等待秒数]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PLAYER="${1:-$ROOT/Builds/Linux/ChessSage.x86_64}"
SHOT="${2:-$ROOT/Logs/screenshot.png}"
WAIT="${3:-12}"

[ -x "$PLAYER" ] || { echo "找不到播放器：$PLAYER"; exit 1; }
mkdir -p "$(dirname "$SHOT")"

export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe

DISP=":99"
Xvfb "$DISP" -screen 0 1600x1000x24 >/dev/null 2>&1 &
XVFB_PID=$!

cleanup() {
  kill "${PLAYER_PID:-0}" 2>/dev/null || true
  kill "$XVFB_PID" 2>/dev/null || true
}
trap cleanup EXIT

export DISPLAY="$DISP"
sleep 1

echo "==> 启动播放器（软件渲染，DISPLAY=$DISP）"
"$PLAYER" -screen-fullscreen 0 -screen-width 1600 -screen-height 1000 \
  -logFile "$ROOT/Logs/player.log" &
PLAYER_PID=$!

sleep "$WAIT"
import -window root "$SHOT" 2>/dev/null || true

echo "==> 截图：$SHOT"
ls -la "$SHOT" 2>/dev/null || echo "（未生成截图，请检查 Logs/player.log）"
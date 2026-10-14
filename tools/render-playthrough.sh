#!/usr/bin/env bash
# ============================================================================
# 软件渲染试玩取证：在无独显沙箱里跑试玩并产出「有内容」的截图。
# ============================================================================
# 背景：本沙箱无独显，但 Xvfb + Mesa llvmpipe 软渲染可用，Unity 能创建
#       OpenGL 4.5 (Core) 上下文并正常离屏渲染。
#
# ⚠️ 绝对不要加 -nographics：那会禁用渲染，只会得到整屏纯色。
#
# 关键缺陷修复（D-04）：历史 URP 资产是用 ScriptableObject.CreateInstance 裸建的
#   空壳（renderer 未挂上、shader/postProcessData 全空引用），运行期
#   UniversalRenderPipelineAsset.CreatePipeline() 抛 NRE → 相机无内容 → 纯色截图。
#   ProjectBootstrapper.EnsureRenderPipeline() 现在会自愈重建，且 BatchRun 进播放模式前会调用它。
#
# 用法：
#   bash tools/render-playthrough.sh [场景]        # 场景 ∈ Main(默认) | Overworld | Placement | Cheat
#   SCENE=Overworld bash tools/render-playthrough.sh
#
# 环境变量：
#   UNITY         Unity 编辑器可执行文件（默认 /opt/unity/Editor/Unity）
#   SHOTS         截图输出目录（默认 <repo>/Logs/shots-sw）
#   XVFB_DISPLAY  X display（默认 :99）
#   SCREEN        分辨率（默认 1920x1080x24）
#   SKIP_CHECK=1  跳过截图判据校验（仅出图）
#
# 退出码：0 成功且截图达标；非 0 = Unity 失败或截图不达标（日志会打印定位线索）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/opt/unity/Editor/Unity}"
SCENE="${1:-${SCENE:-Main}}"
SHOTS="${SHOTS:-$ROOT/Logs/shots-sw}"
LOGDIR="$ROOT/Logs"
LOG="$LOGDIR/render-probe.log"
XVFB_LOG="$LOGDIR/xvfb.log"
XVFB_DISPLAY="${XVFB_DISPLAY:-:99}"
SCREEN="${SCREEN:-1920x1080x24}"

# 截图判据（见任务：唯一色 > 5 且方差 > 50）
MIN_UNIQUE_COLORS=5
MIN_VARIANCE=50

mkdir -p "$SHOTS" "$LOGDIR"

[ -x "$UNITY" ] || { echo "❌ 找不到 Unity 编辑器：$UNITY（可用 UNITY= 覆盖）" >&2; exit 1; }

XVFB_PID=""
cleanup() {
  if [ -n "$XVFB_PID" ]; then
    kill "$XVFB_PID" 2>/dev/null || true
    wait "$XVFB_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# ── 启动 Xvfb（若该 display 已有活的 X server 则复用，不重复拉起）──────────────
if xdpyinfo -display "$XVFB_DISPLAY" >/dev/null 2>&1; then
  echo "==> 复用已存在的 X display $XVFB_DISPLAY"
else
  echo "==> 启动 Xvfb $XVFB_DISPLAY ($SCREEN)"
  Xvfb "$XVFB_DISPLAY" -screen 0 "$SCREEN" -ac +extension GLX +render -noreset \
    >"$XVFB_LOG" 2>&1 &
  XVFB_PID=$!
  for _ in $(seq 1 50); do
    xdpyinfo -display "$XVFB_DISPLAY" >/dev/null 2>&1 && break
    sleep 0.1
  done
  xdpyinfo -display "$XVFB_DISPLAY" >/dev/null 2>&1 \
    || { echo "❌ Xvfb 启动失败，见 $XVFB_LOG" >&2; exit 1; }
fi
export DISPLAY="$XVFB_DISPLAY"

# ── 软渲染环境 ──────────────────────────────────────────────────────────────
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export MESA_GL_VERSION_OVERRIDE=4.5
export MESA_GLSL_VERSION_OVERRIDE=450

export CHESSSAGE_SCENE="$SCENE"
export CHESSSAGE_SHOTS="$SHOTS"

rm -f "$LOG"
echo "==> 软渲染试玩：场景=$SCENE  display=$DISPLAY  输出=$SHOTS"

set +e
"$UNITY" -batchmode -accept-apiupdate \
  -projectPath "$ROOT" \
  -logFile "$LOG" \
  -executeMethod ChessSage.EditorTools.PlaythroughCapture.BatchRun
RC=$?
set -e

dump_log_clues() {
  echo "──────── $LOG 关键线索 ────────"
  grep -nE "NullReferenceException|CreatePipeline|scriptableRendererData|Exception|Error|error CS|\[试玩\]" "$LOG" 2>/dev/null | tail -40 || true
  echo "──────── 日志尾部 ────────"
  tail -25 "$LOG" 2>/dev/null || true
  echo "（完整日志：$LOG）"
}

if [ "$RC" -ne 0 ]; then
  echo "❌ Unity 退出码 $RC，试玩失败。" >&2
  dump_log_clues
  exit "$RC"
fi

if ! grep -q "\[试玩\] 完成，正常退出" "$LOG" 2>/dev/null; then
  echo "❌ 未在日志中看到「试玩完成」，可能中途卡死/异常。" >&2
  dump_log_clues
  exit 4
fi

echo "==> 截图清单："
ls -la "$SHOTS"/*.png 2>/dev/null || { echo "❌ 未生成任何 PNG" >&2; dump_log_clues; exit 5; }

if [ "${SKIP_CHECK:-0}" = "1" ]; then
  echo "==> SKIP_CHECK=1，跳过截图判据校验"
  exit 0
fi

# ── 截图判据校验（PIL：唯一色数 / RGB 方差）──────────────────────────────────
python3 - "$SHOTS" "$MIN_UNIQUE_COLORS" "$MIN_VARIANCE" <<'PY'
import sys, glob, os
from PIL import Image

shots_dir, min_unique, min_var = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])

def sample_colors(img, step=16):
    px = img.convert("RGB").load()
    w, h = img.size
    colors, rgb = set(), []
    for y in range(0, h, step):
        for x in range(0, w, step):
            c = px[x, y]
            colors.add(c)
            rgb.append(c)
    return colors, rgb

def variance(rgb):
    n = len(rgb)
    if n == 0:
        return 0.0
    means = [sum(c[i] for c in rgb) / n for i in range(3)]
    var = sum((c[i] - means[i]) ** 2 for c in rgb for i in range(3)) / (n * 3)
    return var

pngs = sorted(glob.glob(os.path.join(shots_dir, "*.png")))
if not pngs:
    print("❌ 目录下没有 PNG"); sys.exit(5)

print(f"{'文件':<28}{'唯一色':>8}{'方差':>12}   判定")
failed = []
for p in pngs:
    img = Image.open(p)
    colors, rgb = sample_colors(img)
    u, v = len(colors), variance(rgb)
    ok = (u > min_unique) and (v > min_var)
    if not ok:
        failed.append(os.path.basename(p))
    print(f"{os.path.basename(p):<28}{u:>8}{v:>12.1f}   {'✅ 通过' if ok else '❌ 纯色/近纯色'}")
print(f"\n判据：唯一色 > {min_unique} 且 方差 > {min_var}")
if failed:
    print(f"❌ 未达标：{', '.join(failed)}")
    sys.exit(2)
print("✅ 全部截图达标")
PY

echo "==> 完成。截图证据：$SHOTS"

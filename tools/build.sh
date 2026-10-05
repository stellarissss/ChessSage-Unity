#!/usr/bin/env bash
# Unity 构建脚本（Linux Headless）：EditMode 测试 + Linux/Windows 播放器。
#
# 用法：
#   bash tools/build.sh test                 # 只跑 EditMode 测试
#   bash tools/build.sh linux                # 构建 Linux x64
#   bash tools/build.sh windows              # 交叉编译 Windows x64
#   bash tools/build.sh all                  # 测试 + 双平台
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/opt/unity/editor/Editor/Unity}"
OUT="${OUT:-$ROOT/Builds}"
LOGS="${LOGS:-$ROOT/Logs}"

mkdir -p "$OUT" "$LOGS"

run_unity() {
  "$UNITY" -batchmode -nographics -quit -accept-apiupdate \
    -projectPath "$ROOT" -logFile "$LOGS/$1.log" "${@:2}"
}

# 运行测试时不能带 -quit —— 否则编辑器在 TestRunner 执行前即退出，拿不到结果 XML。
run_tests() {
  "$UNITY" -batchmode -nographics -accept-apiupdate \
    -projectPath "$ROOT" -logFile "$LOGS/test.log" \
    -runTests -testPlatform EditMode -testResults "$LOGS/test-results.xml"
}

cmd="${1:-all}"

if [ "$cmd" = "test" ] || [ "$cmd" = "all" ]; then
  echo "==> EditMode 测试"
  run_tests || { echo "测试失败，见 $LOGS/test.log"; exit 1; }
  echo "==> 测试通过"
fi

if [ "$cmd" = "linux" ] || [ "$cmd" = "all" ]; then
  echo "==> 构建 Linux x64"
  run_unity linux -buildTarget StandaloneLinux64 -executeMethod ChessSage.Build.BuildScript.BuildLinux
  echo "==> 产物：$OUT/Linux"
fi

if [ "$cmd" = "windows" ] || [ "$cmd" = "all" ]; then
  echo "==> 交叉编译 Windows x64"
  run_unity windows -buildTarget StandaloneWindows64 -executeMethod ChessSage.Build.BuildScript.BuildWindows
  echo "==> 产物：$OUT/Windows"
fi
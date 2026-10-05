#!/usr/bin/env bash
# 把原网页版的 JSON 配置同步进 Unity 工程（StreamingAssets），
# 使运行时与编辑器测试都能读到与 legacy-web 完全一致的配置数据。
#
# 用法：bash tools/sync-configs.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/legacy-web"
DST="$ROOT/Assets/StreamingAssets/Configs"
SCHEMA_DST="$ROOT/Assets/StreamingAssets/Schemas"
VARIANTS=(xiangqi weiqi wuziqi heibaiqi tiaoqi dongwuqi)

rm -rf "$DST" "$SCHEMA_DST"
mkdir -p "$DST/global" "$SCHEMA_DST"

# 关卡/RPG 全局配置
cp "$SRC"/configs/*.json "$DST/global/" 2>/dev/null || true

# 各棋类配置
for v in "${VARIANTS[@]}"; do
  mkdir -p "$DST/$v"
  cp "$SRC/$v"/configs/*.json "$DST/$v/" 2>/dev/null || true
done

# JSON Schema（配置校验用）
cp "$SRC"/shared/schemas/*.json "$SCHEMA_DST/" 2>/dev/null || true

echo "==> 已同步配置到 $DST"
find "$DST" -name '*.json' | wc -l
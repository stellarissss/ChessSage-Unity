#!/usr/bin/env bash
# 纯逻辑校验 harness —— 不启动 Unity，用 Editor 自带的 Roslyn + Mono 直接编译
# Assets/ChessSage/Core 与 tools/verify/probes 下的探针，运行黄金等价性校验。
#
# 用法：
#   bash tools/verify/core-harness.sh            # 编译并运行全部探针
#   bash tools/verify/core-harness.sh XiangqiProbe  # 只运行名字包含该串的探针
#
# 依赖：Unity Editor 安装目录（提供 dotnet / csc / mono / net48 参考程序集）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY_ROOT="${UNITY_ROOT:-/opt/unity}"
EDITOR="$UNITY_ROOT/editor/Editor/Data"

DOTNET="$EDITOR/NetCoreRuntime/dotnet"
CSC="$EDITOR/DotNetSdkRoslyn/csc.dll"
REF="$EDITOR/UnityReferenceAssemblies/unity-4.8-api"
MONO="$EDITOR/MonoBleedingEdge/bin/mono"
NJS="$(find "$ROOT/Library/PackageCache" -maxdepth 3 -name Newtonsoft.Json.dll -path '*Runtime*' 2>/dev/null | head -1)"

OUT="${OUT:-$ROOT/Logs/harness}"
mkdir -p "$OUT"

if [ ! -x "$DOTNET" ] || [ ! -f "$CSC" ]; then
  echo "找不到 Unity 自带的 Roslyn/dotnet，请检查 UNITY_ROOT=$UNITY_ROOT" >&2
  exit 2
fi
if [ -z "$NJS" ]; then
  echo "找不到 Newtonsoft.Json.dll，请先在 Unity 中打开过工程（生成 Library/PackageCache）" >&2
  exit 2
fi

# 收集 Core + 探针源码。
# 并行开发某个棋类引擎时，可用 ISOLATE_VARIANTS=1 排除 Rules/Variants/ 下其他人的文件，
# 再用 EXTRA_SRC=/abs/path/XxxRuleEngine.cs 只纳入自己的实现。
FIND_EXCLUDE=()
if [ "${ISOLATE_VARIANTS:-0}" = "1" ]; then
  FIND_EXCLUDE=(-not -path '*/Rules/Variants/*')
fi
find "$ROOT/Assets/ChessSage/Core" -name '*.cs' "${FIND_EXCLUDE[@]}" > "$OUT/sources.rsp"
if [ -n "${EXTRA_SRC:-}" ]; then
  for f in $EXTRA_SRC; do echo "$f" >> "$OUT/sources.rsp"; done
fi
if [ "${ISOLATE_VARIANTS:-0}" = "1" ]; then
  # 只纳入基础探针文件 + ONLY_PROBE 指定的探针，避免其他人的探针引用未编译的引擎
  for f in "$ROOT/tools/verify/probes/Program.cs" "$ROOT/tools/verify/probes/ProbeEnv.cs"; do
    echo "$f" >> "$OUT/sources.rsp"
  done
  find "$ROOT/tools/verify/probes" -name "${ONLY_PROBE:-*Probe.cs}" >> "$OUT/sources.rsp"
else
  find "$ROOT/tools/verify/probes" -name '*.cs' >> "$OUT/sources.rsp"
fi

REFS=(
  "$REF/mscorlib.dll" "$REF/System.dll" "$REF/System.Core.dll"
  "$REF/System.Net.Http.dll" "$REF/System.Xml.dll" "$REF/System.Xml.Linq.dll"
  "$REF/Facades/netstandard.dll" "$NJS"
)
REF_ARGS=()
for r in "${REFS[@]}"; do REF_ARGS+=("-r:$r"); done

echo "==> 编译 Core + 探针"
"$DOTNET" "$CSC" -nologo -nostdlib -target:exe -langversion:9 \
  -out:"$OUT/harness.exe" "${REF_ARGS[@]}" @"$OUT/sources.rsp"

cp "$NJS" "$OUT/Newtonsoft.Json.dll"

echo "==> 运行探针"
"$MONO" "$OUT/harness.exe" "$@"
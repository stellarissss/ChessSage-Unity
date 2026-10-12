#!/usr/bin/env bash
# ============================================================================
# ChessSage 编译门禁（L2）——无需 Unity 许可证即可验证全工程编译正确性
# ============================================================================
# 原理：Unity 把用户脚本编译到 Library/ScriptAssemblies，但这需要启动编辑器
#       （因而需要许可证）。而编辑器自带的「参考程序集」与 Roslyn 编译器是
#       纯文件，无需许可证即可调用。于是我们可以在任意机器上：
#
#         1) 用 Unity 的 netstandard 参考程序集 + Newtonsoft.Json 编译 ChessSage.Core
#         2) 再叠加 UnityEngine.* / UnityEditor.* 参考程序集，编译 Presentation / App / Editor
#
#       从而在提交前捕获**全部编译错误**，把「无许可证环境下的不可验证风险」降到最低。
#
# 与另外两层的关系：
#   L1  dotnet test tools/verify/dotnet/ChessSage.Core.Verify.csproj   （逻辑回归）
#   L2  bash tools/verify/compile-check.sh                             （本脚本：编译期）
#   L3  bash tools/build.sh test / windows                              （需许可证：运行与出包）
#
# 用法：
#   bash tools/verify/compile-check.sh              # 编译全部程序集
#   VERBOSE=1 bash tools/verify/compile-check.sh    # 打印完整命令与全部警告
# ============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY_ROOT="${UNITY_ROOT:-/opt/unity}"

# Unity 安装目录里的 Editor 大小写在不同安装包/平台上不一致（China 包为 Editor，
# 官方 Linux 包历史上为 editor），此处自动探测，避免大小写导致的隐性失败。
if [ -d "$UNITY_ROOT/Editor/Data" ]; then
  EDATA="$UNITY_ROOT/Editor/Data"
elif [ -d "$UNITY_ROOT/editor/Data" ]; then
  EDATA="$UNITY_ROOT/editor/Data"
else
  echo "❌ 在 UNITY_ROOT=$UNITY_ROOT 下找不到 Editor/Data，请用 UNITY_ROOT 指定安装根目录" >&2
  exit 2
fi

DOTNET="$EDATA/NetCoreRuntime/dotnet"
CSC="$EDATA/DotNetSdkRoslyn/csc.dll"
NETSTD_DIR="$EDATA/NetStandard/ref/2.1.0"
NETSTD="$NETSTD_DIR/netstandard.dll"
SHIM_NETSTD="$EDATA/NetStandard/compat/2.1.0/shims/netstandard"
SHIM_NETFX="$EDATA/NetStandard/compat/2.1.0/shims/netfx"
UNITY_DLLS="$EDATA/Managed/UnityEngine"
NJSON="$EDATA/Managed/Newtonsoft.Json.dll"

OUT="${OUT:-$ROOT/Logs/compile-check}"
mkdir -p "$OUT"

fail() { echo "❌ $*" >&2; exit 2; }

[ -x "$DOTNET" ] || fail "找不到 Unity 自带 dotnet：$DOTNET"
[ -f "$CSC" ]     || fail "找不到 Unity 自带 Roslyn：$CSC"
[ -f "$NETSTD" ]  || fail "找不到 netstandard 参考程序集：$NETSTD"
[ -f "$NJSON" ]   || fail "找不到 Newtonsoft.Json：$NJSON"

ERRORS=0

# ── 组装引用列表 ────────────────────────────────────────────────
REFS=(-r:"$NETSTD" -r:"$NJSON")
for d in "$SHIM_NETSTD" "$SHIM_NETFX"; do
  [ -d "$d" ] || continue
  while IFS= read -r f; do REFS+=(-r:"$f"); done < <(find "$d" -name '*.dll' 2>/dev/null)
done

# UnityEngine / UnityEditor API 程序集（Presentation 及以上才需要）
UNITY_REFS=()
while IFS= read -r f; do UNITY_REFS+=(-r:"$f"); done < <(find "$UNITY_DLLS" -name '*.dll' 2>/dev/null | sort)

# uGUI（UnityEngine.UI）随 com.unity.ugui 包提供，**不在** Editor/Data/Managed/UnityEngine 下，
# 上面的通配扫描找不到它。M5-A4 的棋种 hub 用 Canvas + uGUI 构建界面，Presentation 依赖该程序集，
# 故需显式引用，否则 L2 会把「缺引用」误报成编译错误。
# 解析顺序：Editor Managed（若某些安装放在此）→ 工程 Library/ScriptAssemblies（L3 生成）→ 编辑器内置模板缓存。
UI_DLL="$EDATA/Managed/UnityEngine/UnityEngine.UI.dll"
[ -f "$UI_DLL" ] || UI_DLL="$ROOT/Library/ScriptAssemblies/UnityEngine.UI.dll"
if [ ! -f "$UI_DLL" ]; then
  UI_DLL="$(find "$UNITY_ROOT/Editor/Data/Resources/PackageManager/ProjectTemplates/libcache" \
    -iname 'UnityEngine.UI.dll' 2>/dev/null | sort | head -1)"
fi
if [ -f "$UI_DLL" ]; then
  UNITY_REFS+=(-r:"$UI_DLL")
  echo "    参考 uGUI：$UI_DLL"
else
  echo "⚠️  未找到 UnityEngine.UI.dll（棋种 hub 依赖），Presentation 可能编译失败" >&2
fi

COMMON=(-nologo -nostdlib+ -noconfig -langversion:9 -target:library -warn:0 -deterministic)

run_csc() {
  local name="$1"; shift
  local outdll="$OUT/$name.dll"
  local rsp="$OUT/$name.rsp"
  : > "$rsp"
  # 响应文件中每一行都是「源文件」，引用必须保留 -r: 前缀，否则会被当成源码解析。
  for r in "${REFS[@]}"; do echo "$r" >> "$rsp"; done
  for a in "$@"; do echo "$a" >> "$rsp"; done

  local cmd=("$DOTNET" "$CSC" "${COMMON[@]}" -out:"$outdll" "@$rsp")
  [ "${VERBOSE:-0}" = "1" ] && printf '  %s\n' "${cmd[*]}"

  local log="$OUT/$name.log"
  if "${cmd[@]}" > "$log" 2>&1; then
    local warns
    warns=$(grep -cE 'warning (CS|MSB)' "$log" 2>/dev/null || echo 0)
    echo "  ✅ $name  (警告 $warns)"
  else
    echo "  ❌ $name  编译失败"
    grep -E ': error ' "$log" | sed 's#^/workspace/ChessSage-Unity/##' | sort -u | head -25 | sed 's/^/       /'
    ERRORS=$((ERRORS + 1))
  fi
}

srcs() { find "$@" -name '*.cs' 2>/dev/null; }

echo "==> L2 编译门禁（Unity $("$DOTNET" --version 2>/dev/null | head -1) + Roslyn，无需许可证）"
echo "    参考程序集：$NETSTD_DIR"
echo

# ── 1) Core：纯逻辑，零 UnityEngine 依赖 ─────────────────────────
echo "==> [1/4] ChessSage.Core（纯逻辑，零引擎依赖）"
run_csc ChessSage.Core $(srcs "$ROOT/Assets/ChessSage/Core")

# ── 2) Presentation：依赖 Core + UnityEngine ─────────────────────
echo "==> [2/4] ChessSage.Presentation（依赖 Core + UnityEngine）"
run_csc ChessSage.Presentation \
  "${UNITY_REFS[@]}" \
  -r:"$OUT/ChessSage.Core.dll" \
  $(srcs "$ROOT/Assets/ChessSage/Presentation")

# ── 3) App：组合根 ─────────────────────────────────────────────
echo "==> [3/4] ChessSage.App（组合根）"
run_csc ChessSage.App \
  "${UNITY_REFS[@]}" \
  -r:"$OUT/ChessSage.Core.dll" \
  -r:"$OUT/ChessSage.Presentation.dll" \
  $(srcs "$ROOT/Assets/ChessSage/App")

# ── 4) Editor：Editor-only 工具 ─────────────────────────────────
echo "==> [4/4] ChessSage.Editor（Editor-only 工具链）"
run_csc ChessSage.Editor \
  "${UNITY_REFS[@]}" \
  -define:UNITY_EDITOR \
  -r:"$OUT/ChessSage.Core.dll" \
  -r:"$OUT/ChessSage.Presentation.dll" \
  -r:"$OUT/ChessSage.App.dll" \
  $(srcs "$ROOT/Assets/ChessSage/Editor")

echo
if [ "$ERRORS" -eq 0 ]; then
  echo "🎉 L2 编译门禁通过：4 个程序集全部零错误"
  exit 0
else
  echo "🚫 L2 编译门禁失败：$ERRORS 个程序集有编译错误（详见 $OUT/*.log）"
  exit 1
fi
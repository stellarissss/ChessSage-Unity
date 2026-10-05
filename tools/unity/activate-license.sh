#!/usr/bin/env bash
# 激活 Unity 许可证（Personal 授权，机器绑定）。
#
# 用法：
#   UNITY_EMAIL=you@example.com UNITY_PASSWORD=*** bash tools/unity/activate-license.sh
#
# 备注：凭据仅从环境变量读取，不落盘、不入库。许可文件写入
#   ~/.config/unity3d/Unity/licenses/UnityEntitlementLicense.xml
set -euo pipefail

UNITY_ROOT="${UNITY_ROOT:-/opt/unity}"
CLIENT_DIR="$UNITY_ROOT/editor/Editor/Data/Resources/Licensing/Client"

: "${UNITY_EMAIL:?需要设置 UNITY_EMAIL}"
: "${UNITY_PASSWORD:?需要设置 UNITY_PASSWORD}"

cd "$CLIENT_DIR"
./Unity.Licensing.Client \
  --username "$UNITY_EMAIL" \
  --password "$UNITY_PASSWORD" \
  --activate-all \
  --include-personal

echo "==> 许可证文件："
ls -la "$HOME/.config/unity3d/Unity/licenses/" 2>/dev/null || true
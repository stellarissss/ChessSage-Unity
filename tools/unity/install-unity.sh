#!/usr/bin/env bash
# 安装 Unity Editor（Linux Headless）与 Windows 交叉编译模块。
#
# 用法：
#   UNITY_VERSION=2022.3.62f3c1 UNITY_REVISION=1623fc0bbb97 \
#   UNITY_CDN=https://download.unitychina.cn/download_unity \
#   bash tools/unity/install-unity.sh
#
# 说明：
#   - 全球 CDN（download.unity3d.com）在部分网络环境不可达，默认使用 Unity China CDN。
#   - Editor 安装到 $UNITY_ROOT/editor，模块安装到 Editor/Data/PlaybackEngines。
set -euo pipefail

UNITY_VERSION="${UNITY_VERSION:-2022.3.62f3c1}"
UNITY_REVISION="${UNITY_REVISION:-1623fc0bbb97}"
UNITY_CDN="${UNITY_CDN:-https://download.unitychina.cn/download_unity}"
UNITY_ROOT="${UNITY_ROOT:-/opt/unity}"
DL_DIR="$UNITY_ROOT/downloads"
EDITOR_DIR="$UNITY_ROOT/editor"
PLAYBACK_DIR="$EDITOR_DIR/Editor/Data/PlaybackEngines"

mkdir -p "$DL_DIR" "$EDITOR_DIR"

echo "==> 下载 Unity Editor $UNITY_VERSION"
if [ ! -f "$DL_DIR/Unity-$UNITY_VERSION.tar.xz" ]; then
  curl -L --fail --retry 3 \
    -o "$DL_DIR/Unity-$UNITY_VERSION.tar.xz" \
    "$UNITY_CDN/$UNITY_REVISION/LinuxEditorInstaller/Unity.tar.xz"
fi

echo "==> 解压 Editor"
if [ ! -x "$EDITOR_DIR/Editor/Unity" ]; then
  tar xf "$DL_DIR/Unity-$UNITY_VERSION.tar.xz" -C "$EDITOR_DIR"
fi

echo "==> 下载 Windows Build Support (Mono) 模块"
if [ ! -f "$DL_DIR/UnitySetup-Windows-Mono-Support-for-Editor-$UNITY_VERSION.pkg" ]; then
  curl -L --fail --retry 3 \
    -o "$DL_DIR/UnitySetup-Windows-Mono-Support-for-Editor-$UNITY_VERSION.pkg" \
    "$UNITY_CDN/$UNITY_REVISION/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-$UNITY_VERSION.pkg"
fi

echo "==> 安装 Windows Build Support (Mono)"
# Windows 模块是 xar(gzip+cpio) 包：7z 可递归解开；payload 根目录即 WindowsStandaloneSupport。
if [ ! -d "$PLAYBACK_DIR/WindowsStandaloneSupport" ]; then
  command -v 7z >/dev/null || { echo "需要 p7zip-full（apt-get install -y p7zip-full）"; exit 1; }
  tmp="$(mktemp -d)"
  7z x "$DL_DIR/UnitySetup-Windows-Mono-Support-for-Editor-$UNITY_VERSION.pkg" -o"$tmp/pkg" -y >/dev/null
  payload="$tmp/pkg/TargetSupport.pkg.tmp/Payload"
  cpio_payload="$payload~"
  [ -f "$cpio_payload" ] || 7z x "$payload" -o"$tmp" -y >/dev/null
  mkdir -p "$PLAYBACK_DIR/WindowsStandaloneSupport"
  7z x "$cpio_payload" -o"$PLAYBACK_DIR/WindowsStandaloneSupport" -y >/dev/null
  rm -rf "$tmp"
fi

echo "==> 安装 Linux IL2CPP 模块（可选，本机自测用）"
if [ ! -d "$PLAYBACK_DIR/LinuxStandaloneSupport/Variations/il2cpp" ]; then
  curl -L --fail --retry 3 \
    -o "$DL_DIR/UnitySetup-Linux-IL2CPP-Support-for-Editor-$UNITY_VERSION.tar.xz" \
    "$UNITY_CDN/$UNITY_REVISION/LinuxEditorTargetInstaller/UnitySetup-Linux-IL2CPP-Support-for-Editor-$UNITY_VERSION.tar.xz"
  tar xf "$DL_DIR/UnitySetup-Linux-IL2CPP-Support-for-Editor-$UNITY_VERSION.tar.xz" -C "$EDITOR_DIR"
fi

echo "==> 完成。Editor: $EDITOR_DIR/Editor/Unity"
"$EDITOR_DIR/Editor/Unity" -version || true
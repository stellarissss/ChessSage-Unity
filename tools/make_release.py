#!/usr/bin/env python3
"""创建 GitHub Release v1.0.1 并上传 Linux 播放器 zip 资产。"""
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
REPO = os.environ.get("GH_REPO", "stellarissss/ChessSage-Unity")
TOKEN = os.environ.get("GH_TOKEN")
if not TOKEN:
    sys.exit("缺少 GH_TOKEN")

TAG = "v1.0.1"
NAME = "v1.0.1 —— 测评回应（缺陷修复轮）"
TARGET = os.environ.get("GH_TARGET", "5cb3154")  # 远程 main HEAD
ZIP = os.environ.get("GH_ZIP", "Builds/ChessSage-Linux-x64-v1.0.1.zip")
NOTES = os.environ.get("GH_NOTES", "docs/release-notes-v1.0.1.md")


def api(method, path, payload=None, binary=None, content_type=None):
    url = path if path.startswith("http") else f"{API}{path}"
    data = json.dumps(payload).encode() if payload is not None else binary
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {TOKEN}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "chesssage-release")
    if payload is not None:
        req.add_header("Content-Type", "application/json")
    if binary is not None and content_type:
        req.add_header("Content-Type", content_type)
    for attempt in range(12):
        try:
            with urllib.request.urlopen(req, timeout=300) as resp:
                body = resp.read().decode()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            detail = e.read().decode()[:500]
            if e.code >= 500 or e.code in (401, 403):
                if attempt == 11:
                    sys.exit(f"HTTP {e.code}: {detail}")
                wait = min(2 ** attempt, 20)
                print(f"  [重试 {attempt+1}/11] HTTP {e.code}，{wait}s", flush=True)
                time.sleep(wait)
            else:
                sys.exit(f"HTTP {e.code}: {detail}")
        except Exception as e:
            if attempt == 11:
                sys.exit(f"网络错误: {e}")
            wait = min(2 ** attempt, 20)
            print(f"  [重试 {attempt+1}/11] {type(e).__name__}，{wait}s", flush=True)
            time.sleep(wait)


def main():
    notes = open(NOTES, encoding="utf-8").read()

    # 先确保 tag 引用存在（指向目标提交），否则 Release 校验会拒绝
    try:
        api("POST", f"/repos/{REPO}/git/refs",
            {"ref": f"refs/tags/{TAG}", "sha": TARGET})
        print(f"创建 tag 引用 refs/tags/{TAG} -> {TARGET[:10]}")
    except SystemExit:
        # 409 = 已存在，可接受
        print(f"tag 引用 {TAG} 已存在（或创建冲突），继续")

    # 若已存在同名 tag/release 则跳过创建（404 = 不存在，正常继续）
    existing = None
    try:
        existing = api("GET", f"/repos/{REPO}/releases/tags/{TAG}")
    except SystemExit:
        existing = None
    if existing and "id" in existing:
        print(f"Release {TAG} 已存在 (id={existing['id']})，跳过创建")
        rel = existing
    else:
        rel = api("POST", f"/repos/{REPO}/releases", {
            "tag_name": TAG,
            "name": NAME,
            "body": notes,
            "target_commitish": TARGET,
            "draft": False,
            "prerelease": False,
        })
        print(f"创建 Release: {rel.get('html_url')}")
        print(f"创建 Release: {rel.get('html_url')}")

    upload_url = rel["upload_url"].split("{")[0]
    asset_name = os.path.basename(ZIP)
    with open(ZIP, "rb") as f:
        blob = f.read()
    print(f"上传资产 {asset_name} ({len(blob)} 字节) …")
    # 先查是否已有同名资产
    for a in rel.get("assets", []):
        if a["name"] == asset_name:
            api("DELETE", f"/repos/{REPO}/releases/assets/{a['id']}")
            print(f"  已删除旧资产 {asset_name}")
    r = api("POST", f"{upload_url}?name={asset_name}&label={asset_name}",
            binary=blob, content_type="application/zip")
    print(f"✅ 资产上传完成: {r.get('browser_download_url')}")


if __name__ == "__main__":
    main()

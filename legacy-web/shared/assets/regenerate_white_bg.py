#!/usr/bin/env python3
"""
白色背景立绘重制（Seedream 图生图版）
====================================
将 shared/assets/characters/*/*.jpg（不含动画帧 _f）的深色背景立绘，
用 Seedream 5.0 图生图重制为纯白背景版本（保持角色外观一致），
供 rembg 抠图时头发/衣服不再被误判为背景。

原理：以原黑色背景立绘为参考图（image 字段，base64 data URI），
提示词要求"same character, replace background with pure white"，
模型保持角色不变，仅替换背景为白色。

用法：
    python regenerate_white_bg.py                          # 处理全部静态立绘
    python regenerate_white_bg.py --only boy/boy_neutral   # 仅测试一张
    python regenerate_white_bg.py --parallel 4             # 并发
    python regenerate_white_bg.py --retry-missing           # 重试失败的

依赖：标准库（urllib/base64/json）+ httpx（下载）
环境变量：ARK_API_KEY
"""
import argparse
import base64
import json
import os
import shutil
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

CHAR_DIR = Path(__file__).resolve().parent / "characters"
BACKUP_SUFFIX = ".darkbg.jpg"   # 原黑色背景备份后缀
ARK_ENDPOINT = "https://ark.cn-beijing.volces.com/api/v3/images/generations"
ARK_MODEL = "doubao-seedream-5-0-260128"

# 图生图提示词：保持角色完全一致，仅把背景换成纯白
PROMPT = (
    "Same anime game character as the reference image, "
    "identical face, hair style and color, clothing, expression and pose, "
    "do NOT change the character in any way, "
    "replace the entire background with pure solid white (#FFFFFF), "
    "clean pure white backdrop for sprite cutout, "
    "anime style bust portrait half body, centered composition, "
    "soft studio lighting, high quality, detailed, no shadow on background"
)


def img_to_data_uri(path: Path) -> str:
    """读取本地图片，转为 base64 data URI"""
    b64 = base64.b64encode(path.read_bytes()).decode()
    return f"data:image/jpeg;base64,{b64}"


def list_targets(only: str = None) -> list:
    """列出需要重制的静态立绘（排除 _f 动画帧）"""
    jpgs = sorted(CHAR_DIR.glob("*/*.jpg"))
    # 排除动画帧 _f1-_f6、备份文件、已生成
    targets = []
    for p in jpgs:
        name = p.stem
        if "_f" in name and name.split("_f")[-1].isdigit():
            continue
        if name.endswith(BACKUP_SUFFIX.replace(".", "")):
            continue
        targets.append(p)
    if only:
        # only 形如 boy/boy_neutral
        targets = [p for p in targets if str(p.relative_to(CHAR_DIR)) == only + ".jpg"
                   or str(p.relative_to(CHAR_DIR).with_suffix("")) == only]
    return targets


def generate_one(src: Path, size: str = "2048x2048") -> dict:
    """图生图：原黑色背景立绘 → 白色背景立绘，覆盖原文件"""
    api_key = (os.getenv("ARK_API_KEY") or os.getenv("MODEL_IMAGE_API_KEY")
               or os.getenv("MODEL_AGENT_API_KEY"))
    if not api_key:
        return {"src": str(src), "ok": False, "error": "no ARK_API_KEY"}

    # 备份原文件（仅首次）
    backup = src.with_suffix(BACKUP_SUFFIX)
    if not backup.exists():
        shutil.copy2(src, backup)

    data_uri = img_to_data_uri(backup)  # 用原始黑色背景作为参考
    payload = {
        "model": ARK_MODEL,
        "prompt": PROMPT,
        "image": [data_uri],          # 图生图参考图（方舟 list[str] 格式）
        "size": size,
        "watermark": False,
        "response_format": "url",
        "output_format": "jpeg",
    }

    req_data = json.dumps(payload).encode("utf-8")
    req = __import__("urllib.request", fromlist=["Request"]).Request(
        ARK_ENDPOINT, data=req_data,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
        },
        method="POST",
    )
    try:
        with __import__("urllib.request", fromlist=["urlopen"]).urlopen(req, timeout=180) as resp:
            body = resp.read().decode("utf-8")
    except Exception as e:
        err = str(e)
        try:
            err = e.read().decode("utf-8", errors="replace")[:300]  # type: ignore
        except Exception:
            pass
        return {"src": str(src), "ok": False, "error": err}

    try:
        data = json.loads(body)
        if "error" in data:
            return {"src": str(src), "ok": False, "error": json.dumps(data["error"], ensure_ascii=False)[:300]}
        url = data["data"][0]["url"]
    except (KeyError, IndexError, json.JSONDecodeError) as e:
        return {"src": str(src), "ok": False, "error": f"parse: {e}; body={body[:300]}"}

    # 下载覆盖原文件
    import subprocess
    dl = subprocess.run(["curl", "-fsSL", "-o", str(src), url],
                        capture_output=True, text=True, timeout=120)
    if dl.returncode != 0:
        return {"src": str(src), "ok": False, "error": f"download: {dl.stderr.strip()[:200]}"}

    return {"src": str(src), "ok": True, "error": None, "url": url}


def main():
    ap = argparse.ArgumentParser(description="白色背景立绘重制（Seedream 图生图）")
    ap.add_argument("--only", help="仅处理指定立绘，如 boy/boy_neutral")
    ap.add_argument("--parallel", type=int, default=1, help="并发数")
    ap.add_argument("--retry-missing", action="store_true", help="跳过已备份的（已处理过）")
    ap.add_argument("--size", default="2048x2048")
    args = ap.parse_args()

    if not (os.getenv("ARK_API_KEY") or os.getenv("MODEL_IMAGE_API_KEY")
            or os.getenv("MODEL_AGENT_API_KEY")):
        print("❌ 缺少 ARK_API_KEY 环境变量"); sys.exit(1)

    targets = list_targets(args.only)
    if args.retry_missing:
        targets = [t for t in targets if not t.with_suffix(BACKUP_SUFFIX).exists()]
    if not targets:
        print("无待处理立绘"); return

    print(f"🎨 开始重制 {len(targets)} 张白色背景立绘（size={args.size}, parallel={args.parallel}）\n")
    ok = 0
    fail = []

    def _run(t):
        return generate_one(t, size=args.size)

    if args.parallel <= 1:
        for i, t in enumerate(targets, 1):
            print(f"[{i}/{len(targets)}] {t.relative_to(CHAR_DIR)} ...", flush=True)
            r = _run(t)
            if r["ok"]:
                ok += 1
                print(f"   ✓ -> 白色背景")
            else:
                fail.append(r)
                print(f"   ✗ {r['error']}")
    else:
        with ThreadPoolExecutor(max_workers=args.parallel) as ex:
            futs = {ex.submit(_run, t): t for t in targets}
            for i, fut in enumerate(as_completed(futs), 1):
                r = fut.result()
                print(f"[{i}/{len(targets)}] {Path(r['src']).relative_to(CHAR_DIR)}",
                      "✓" if r["ok"] else f"✗ {r['error']}")
                ok += 1 if r["ok"] else 0
                if not r["ok"]:
                    fail.append(r)

    print("\n" + "=" * 60)
    print(f"✅ 成功 {ok}/{len(targets)}（原黑色背景备份为 *{BACKUP_SUFFIX}）")
    if fail:
        print(f"❌ 失败 {len(fail)}：")
        for r in fail:
            print(f"   - {Path(r['src']).relative_to(CHAR_DIR)}: {r['error']}")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""
无限制象棋 - 过场动画/RPG 角色像素画资产生成器
==============================================

为「无限制象棋」游戏批量生成两个角色（黑客 AI 机器人 / 高中少年）的
128×128 像素画精灵图，共 36 张。

用法:
    # 1) 仅生成 manifest.json（不调用 API，无需 Key）
    python generate_all.py --manifest-only

    # 2) 正式生成全部 36 张图（需先设置 ARK_API_KEY）
    export ARK_API_KEY="你的火山引擎 ARK Key"
    python generate_all.py                  # 串行
    python generate_all.py --parallel 4     # 并发 4

    # 3) 只生成某一个角色
    python generate_all.py --character robot
    python generate_all.py --character boy

    # 4) 重试之前失败的
    python generate_all.py --retry-missing

依赖: httpx（被 seedream_image_generate.py 使用）、标准库
技能脚本: /data/user/skills/byted-seedream-image-generate/scripts/seedream_image_generate.py
"""

import argparse
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

# ──────────────────────────────────────────────────────────────
# 路径配置
# ──────────────────────────────────────────────────────────────
ASSETS_DIR = Path(__file__).resolve().parent                  # /workspace/assets
CHARACTERS_DIR = ASSETS_DIR / "characters"
SEEDREAM_SCRIPT = Path(
    "/data/user/skills/byted-seedream-image-generate/scripts/seedream_image_generate.py"
)

# ──────────────────────────────────────────────────────────────
# 风格前缀
# ──────────────────────────────────────────────────────────────
ROBOT_PREFIX = (
    "Pixel art, 128x128 resolution, clean crisp pixel art game sprite, "
    "a floating hacker AI robot character for a Chinese chess (Xiangqi) game, "
    "spherical dark metallic body with a glowing cyan digital eye-screen, "
    "body decorated with red and black Chinese chess piece glyphs (車馬象士將炮兵), "
    "holographic 楚河汉界 river ring hovering beneath, "
    "trailing green matrix code streams and JSON patch snippets, "
    "anti-gravity levitation no legs, cyberpunk tech aesthetic, centered, "
    "simple dark solid background for sprite cutout, high quality pixel art sprite sheet frame"
)

BOY_PREFIX = (
    "Pixel art, 128x128 resolution, clean crisp pixel art game sprite, "
    "a high school boy student character, short messy black hair, "
    "wearing white shirt and dark blue school uniform jacket with red collar tie, "
    "young RPG hero protagonist vibe, centered, "
    "simple dark solid background for sprite cutout, high quality pixel art sprite sheet frame"
)

# ──────────────────────────────────────────────────────────────
# 36 张图规格: (folder, filename, category, prompt_suffix)
# ──────────────────────────────────────────────────────────────
SPECS = [
    # ── 黑客 AI 机器人（18 张）──
    ("robot", "robot_front_idle.png", "角度",
     "front view, idle floating pose, calm glowing eyes, neutral, full body"),
    ("robot", "robot_3quarter_idle.png", "角度",
     "three-quarter front view, idle floating, slight turn, full body"),
    ("robot", "robot_side.png", "角度",
     "side profile view, floating, full body"),
    ("robot", "robot_back.png", "角度",
     "back view, floating, glowing back vents, full body"),
    ("robot", "robot_thinking.png", "姿态",
     "thinking pose, eye-screen showing parsing gears, hand-like appendage on chin, rules being analyzed"),
    ("robot", "robot_scheming.png", "姿态",
     "scheming evil grin on eye-screen, aggressive personality, glowing red accents, devious"),
    ("robot", "robot_casting.png", "动画",
     "casting spell, firing a beam of JSON patch code stream toward target, hexagon magic circle with chess grid, mid-action"),
    ("robot", "robot_surprised.png", "表情",
     "surprised, wide round eyes, exclamation, slight recoil, player did something unexpected"),
    ("robot", "robot_angry.png", "表情",
     "angry, red glowing angry eyes, sparks, aggressive stance"),
    ("robot", "robot_laughing.png", "表情",
     "laughing mocking, ^^ eyes, mischievous grin, E-class joke response vibe"),
    ("robot", "robot_float_up.png", "动画帧",
     "floating animation frame 1, body raised higher, energy ring expanded, idle bob up"),
    ("robot", "robot_float_down.png", "动画帧",
     "floating animation frame 2, body lowered, energy ring contracted, idle bob down"),
    ("robot", "robot_attack.png", "姿态",
     "aggressive attack pose, lunging forward, chess piece glyph projectiles, red energy burst"),
    ("robot", "robot_defend.png", "姿态",
     "defensive pose, glowing cyan hexagonal shield with 九宫 grid pattern, guarding"),
    ("robot", "robot_glitch.png", "动画",
     "glitching corrupted state, random personality, fragmented pixels, distorted body, error artifacts"),
    ("robot", "robot_victory.png", "状态",
     "victory celebration, arms raised, confetti of chess piece glyphs, triumphant glow"),
    ("robot", "robot_defeat.png", "状态",
     "defeated crashed, sparking, cracked screen eye, slumped low, smoke"),
    ("robot", "robot_portrait.png", "头像",
     "bust portrait close-up, head and shoulders, front view, for RPG dialogue box, clean"),

    # ── 高中少年（18 张）──
    ("boy", "boy_neutral.png", "表情",
     "neutral calm expression, relaxed face, bust portrait"),
    ("boy", "boy_happy.png", "表情",
     "happy smiling expression, bright eyes, bust portrait"),
    ("boy", "boy_thinking.png", "表情",
     "thinking expression, looking up, finger near chin, bust portrait"),
    ("boy", "boy_surprised.png", "表情",
     "surprised wide eyes, open mouth, bust portrait"),
    ("boy", "boy_determined.png", "表情",
     "determined fierce eyes, furrowed brow, confident smile, bust portrait"),
    ("boy", "boy_sad.png", "表情",
     "sad downcast expression, slight frown, bust portrait"),
    ("boy", "boy_idle.png", "姿态",
     "standing idle pose, arms relaxed, full body, facing front"),
    ("boy", "boy_think_pose.png", "姿态",
     "hand on chin thinking pose, full body, contemplating next move"),
    ("boy", "boy_confident.png", "姿态",
     "confident challenging pose, arms crossed, smirk, full body"),
    ("boy", "boy_command.png", "姿态",
     "typing on smartphone, entering natural language command, glowing screen, full body"),
    ("boy", "boy_victory.png", "状态",
     "victory fist pump, cheering, bright aura, full body"),
    ("boy", "boy_defeated.png", "状态",
     "defeated kneeling, head down, blue gloom aura, full body"),
    ("boy", "boy_focused.png", "姿态",
     "focused leaning forward, intense gaze, hand ready, full body"),
    ("boy", "boy_walk1.png", "动画帧",
     "walking animation frame 1, left leg forward, full body, side view"),
    ("boy", "boy_walk2.png", "动画帧",
     "walking animation frame 2, right leg forward, full body, side view"),
    ("boy", "boy_point.png", "姿态",
     "pointing forward, selecting a chess piece, determined, full body"),
    ("boy", "boy_cheer.png", "姿态",
     "both arms raised cheering, joyous, full body"),
    ("boy", "boy_portrait.png", "头像",
     "bust portrait close-up, head and shoulders, front view, for RPG dialogue box, clean"),
]


def build_prompt(folder: str, suffix: str) -> str:
    prefix = ROBOT_PREFIX if folder == "robot" else BOY_PREFIX
    return f"{prefix}, {suffix}"


def all_entries():
    """返回完整 entry 列表（含 full prompt 与目标路径）"""
    entries = []
    for folder, filename, category, suffix in SPECS:
        entries.append({
            "folder": folder,
            "filename": filename,
            "category": category,
            "prompt": build_prompt(folder, suffix),
            "target": str(CHARACTERS_DIR / folder / filename),
        })
    return entries


# ──────────────────────────────────────────────────────────────
# manifest 生成
# ──────────────────────────────────────────────────────────────
def write_manifests():
    """为每个角色文件夹写 manifest.json（不调用 API）"""
    by_folder = {"robot": [], "boy": []}
    for e in all_entries():
        by_folder[e["folder"]].append({
            "filename": e["filename"],
            "category": e["category"],
            "prompt": e["prompt"],
        })
    for folder, items in by_folder.items():
        out_dir = CHARACTERS_DIR / folder
        out_dir.mkdir(parents=True, exist_ok=True)
        out_path = out_dir / "manifest.json"
        manifest = {
            "character": folder,
            "description": ("黑客 AI 机器人（含中国象棋元素）像素画精灵图 18 张"
                            if folder == "robot"
                            else "高中少年（玩家形象）像素画精灵图 18 张"),
            "size": "128x128",
            "style": "pixel art, clean, dark background",
            "model": "doubao-seedream-5-0-260128",
            "count": len(items),
            "images": items,
        }
        out_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"  ✓ wrote {out_path} ({len(items)} entries)")


# ──────────────────────────────────────────────────────────────
# 单张生成（直接调用方舟 ARK API）
# ──────────────────────────────────────────────────────────────
ARK_ENDPOINT = "https://ark.cn-beijing.volces.com/api/v3/images/generations"
ARK_MODEL = "doubao-seedream-5-0-260128"


def generate_one(entry: dict, size: str = "2048x2048") -> dict:
    """直接调用方舟 ARK API 生成单张图，下载到 entry['target']。
    Seedream 5.0 要求图片至少 3,686,400 像素（约 1920x1920），
    故默认 size=2048x2048；像素画风格可无损缩放至 128x128 展示。
    """
    target = Path(entry["target"])
    target.parent.mkdir(parents=True, exist_ok=True)

    api_key = (os.getenv("ARK_API_KEY") or os.getenv("MODEL_IMAGE_API_KEY")
               or os.getenv("MODEL_AGENT_API_KEY"))
    if not api_key:
        return {"entry": entry, "ok": False, "url": None,
                "error": "no ARK_API_KEY in env"}

    payload = {
        "model": ARK_MODEL,
        "prompt": entry["prompt"],
        "size": size,
        "watermark": False,
        "response_format": "url",
    }

    # 调用 API
    try:
        import urllib.request
        req = urllib.request.Request(
            ARK_ENDPOINT,
            data=json.dumps(payload).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=180) as resp:
            body = resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")[:300]
        return {"entry": entry, "ok": False, "url": None,
                "error": f"HTTP {e.code}: {err_body}"}
    except Exception as e:
        return {"entry": entry, "ok": False, "url": None,
                "error": f"{type(e).__name__}: {str(e)[:200]}"}

    # 解析 URL
    try:
        data = json.loads(body)
        url = data["data"][0]["url"]
    except (KeyError, IndexError, json.JSONDecodeError) as e:
        return {"entry": entry, "ok": False, "url": None,
                "error": f"parse failed: {str(e)[:100]}; body={body[:200]}"}

    # 下载到目标路径
    try:
        dl = subprocess.run(["curl", "-fsSL", "-o", str(target), url],
                            capture_output=True, text=True, timeout=120)
        if dl.returncode != 0:
            return {"entry": entry, "ok": False, "url": url,
                    "error": f"download failed: {dl.stderr.strip()[:200]}"}
    except subprocess.TimeoutExpired:
        return {"entry": entry, "ok": False, "url": url, "error": "download timeout"}

    return {"entry": entry, "ok": True, "url": url, "error": None,
            "target": str(target)}


# ──────────────────────────────────────────────────────────────
# 主流程
# ──────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser(description="无限制象棋角色像素画批量生成器")
    ap.add_argument("--manifest-only", action="store_true",
                    help="只写 manifest.json，不调用 API")
    ap.add_argument("--character", choices=["robot", "boy"],
                    help="只生成指定角色")
    ap.add_argument("--parallel", type=int, default=1,
                    help="并发数（默认 1 串行）")
    ap.add_argument("--retry-missing", action="store_true",
                    help="只重试尚未生成的图片")
    ap.add_argument("--size", default="2048x2048",
                    help="图片尺寸（Seedream 5.0 最小需 3,686,400 像素 ≈1920x1920；"
                         "默认 2048x2048，像素画风格可缩放至 128x128 展示）")
    args = ap.parse_args()

    if args.manifest_only:
        print("📝 仅生成 manifest.json ...")
        write_manifests()
        print("\n✅ manifest 已写入。配置好 ARK_API_KEY 后运行：")
        print("   python generate_all.py            # 生成全部")
        print("   python generate_all.py --parallel 4  # 并发")
        return

    # 检查 Key
    if not (os.getenv("ARK_API_KEY") or os.getenv("MODEL_IMAGE_API_KEY")
            or os.getenv("MODEL_AGENT_API_KEY")):
        print("❌ 未检测到 ARK_API_KEY / MODEL_IMAGE_API_KEY / MODEL_AGENT_API_KEY")
        print("   请先: export ARK_API_KEY=\"你的火山引擎 ARK Key\"")
        sys.exit(1)

    # 先确保 manifest 存在
    write_manifests()

    entries = all_entries()
    if args.character:
        entries = [e for e in entries if e["folder"] == args.character]
    if args.retry_missing:
        entries = [e for e in entries if not Path(e["target"]).exists()]
        print(f"🔁 重试模式：剩余 {len(entries)} 张待生成")

    total = len(entries)
    print(f"\n🎨 开始生成 {total} 张图片（size={args.size}, parallel={args.parallel}）...\n")

    results = []
    ok_count = 0

    def _run(e):
        return generate_one(e, size=args.size)

    if args.parallel <= 1:
        for i, e in enumerate(entries, 1):
            print(f"[{i}/{total}] {e['folder']}/{e['filename']} ...", flush=True)
            r = _run(e)
            results.append(r)
            ok_count += 1 if r["ok"] else 0
            _print_result(r)
    else:
        with ThreadPoolExecutor(max_workers=args.parallel) as ex:
            future_map = {ex.submit(_run, e): e for e in entries}
            for i, fut in enumerate(as_completed(future_map), 1):
                r = fut.result()
                results.append(r)
                ok_count += 1 if r["ok"] else 0
                print(f"[{i}/{total}]", end=" ")
                _print_result(r)

    # 汇总
    print("\n" + "=" * 60)
    print(f"✅ 成功 {ok_count}/{total}")
    failed = [r for r in results if not r["ok"]]
    if failed:
        print(f"❌ 失败 {len(failed)} 张：")
        for r in failed:
            print(f"   - {r['entry']['folder']}/{r['entry']['filename']}: {r['error']}")
        print("\n可运行: python generate_all.py --retry-missing  重试")
    else:
        print("🎉 全部生成完成！")


def _print_result(r):
    e = r["entry"]
    if r["ok"]:
        print(f"   ✓ {e['folder']}/{e['filename']}  ->  {r.get('target')}")
    else:
        print(f"   ✗ {e['folder']}/{e['filename']}  ERROR: {r['error']}")


if __name__ == "__main__":
    main()

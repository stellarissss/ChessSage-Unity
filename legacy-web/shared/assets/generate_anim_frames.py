#!/usr/bin/env python3
"""
24FPS 立绘动画帧生成（Seedance 图生视频 + ffmpeg 抽帧）
=====================================================
对 boy/chenmo 的每个表情立绘，用 Seedance 从白色背景立绘生成
2 秒微动视频，再抽帧为 48 张连续动画帧（24FPS × 2s），
替换原 6 帧动画（_f1-_f6）为 48 帧（_f1-_f48）。

AI 关键帧插值法：Seedance 从单张图生成时序连续的微动视频，
抽帧后得到帧间过渡自然的动画序列（角色一致性最佳，同源图）。

用法：
    python generate_anim_frames.py                    # 全部 10 个动画
    python generate_anim_frames.py --only boy/neutral # 仅一个
    python generate_anim_frames.py --frames 48        # 抽帧数（默认 48）
依赖：httpx + ffmpeg + seedance skill 脚本
环境变量：ARK_API_KEY
"""
import argparse
import asyncio
import base64
import json
import os
import subprocess
import sys
from pathlib import Path

ASSETS_DIR = Path(__file__).resolve().parent
CHAR_DIR = ASSETS_DIR / "characters"
VIDEO_DIR = ASSETS_DIR / "characters" / "_anim_videos"
VIDEO_DIR.mkdir(parents=True, exist_ok=True)

# Seedance skill 脚本
SKILL_SCRIPT = Path("/data/user/skills/byted-seedance-video-generate/scripts/video_generate.py")
sys.path.insert(0, str(SKILL_SCRIPT.parent))

# 动画清单：(char_dir, emotion, source_jpg)
# boy 6 表情 + chenmo 4 表情 = 10 个动画
ANIMATIONS = [
    ("boy", "neutral",    "boy_neutral.jpg"),
    ("boy", "happy",      "boy_happy.jpg"),
    ("boy", "thinking",   "boy_thinking.jpg"),
    ("boy", "surprised",  "boy_surprised.jpg"),
    ("boy", "determined", "boy_determined.jpg"),
    ("boy", "sad",        "boy_sad.jpg"),
    ("chenmo", "awkward",   "chenmo_awkward.jpg"),
    ("chenmo", "smile",     "chenmo_smile.jpg"),
    ("chenmo", "surprised", "chenmo_surprised.jpg"),
    ("chenmo", "thinking",  "chenmo_thinking.jpg"),
]

# Seedance 提示词：微幅呼吸动画，镜头锁定，2 秒可循环
PROMPT = (
    "The character in the reference image comes alive with subtle natural "
    "breathing micro-movement, gentle chest rise and fall, very slight body sway, "
    "hair softly swaying, natural eye blinking, "
    "2 second seamless loop animation, camera completely fixed and static, "
    "idle breathing animation, no camera movement, no scene change, "
    "keep the pure white background unchanged"
)

MODEL = "doubao-seedance-1-0-pro-250528"


def img_to_data_uri(path: Path) -> str:
    b64 = base64.b64encode(path.read_bytes()).decode()
    return f"data:image/jpeg;base64,{b64}"


def extract_frames(video: Path, out_dir: Path, frames: int) -> int:
    """用 ffmpeg 从视频抽帧，输出 f001.png ... f{N}.png"""
    out_dir.mkdir(parents=True, exist_ok=True)
    # 清理旧帧
    for old in out_dir.glob("*.png"):
        old.unlink()
    # 抽帧：24fps，取前 N 帧（2s × 24fps = 48）
    cmd = [
        "ffmpeg", "-y", "-i", str(video),
        "-vf", f"fps=24",
        "-frames:v", str(frames),
        str(out_dir / "f%03d.png"),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        print(f"   ffmpeg error: {r.stderr[-300:]}")
        return 0
    return len(list(out_dir.glob("*.png")))


def cutout_frames(frames_dir: Path) -> int:
    """对目录下所有白色背景 PNG 抠图为透明背景（rembg + alpha 二值化）。
    复用 cutout_rembg.py 的二值化逻辑，确保头发/衣服完整。"""
    from rembg import remove
    from PIL import Image, ImageFilter
    import numpy as np

    BINARY_THRESHOLD = 128
    EDGE_BLUR = 1.2
    n = 0
    for png in sorted(frames_dir.glob("*.png")):
        img = Image.open(png).convert("RGB")
        out = remove(img)
        r, g, b, a = out.split()
        a_arr = np.array(a)
        binary = (a_arr > BINARY_THRESHOLD).astype(np.uint8) * 255
        a_final = Image.fromarray(binary, "L").filter(ImageFilter.GaussianBlur(EDGE_BLUR))
        a_arr2 = np.array(a_final)
        a_arr2 = np.where(a_arr2 > 200, 255, a_arr2)
        a_final = Image.fromarray(a_arr2.astype(np.uint8), "L")
        Image.merge("RGBA", (r, g, b, a_final)).save(png, "PNG")
        n += 1
    return n


def rename_to_legacy(out_dir: Path, char_dir: str, emotion: str):
    """将 f001.png 重命名为 {char}_{emotion}_f1.png ... 风格，覆盖旧 _f1-_f6"""
    char_prefix = char_dir  # boy / chenmo
    pngs = sorted(out_dir.glob("f*.png"))
    # 先清理旧 _f 帧
    target_dir = CHAR_DIR / char_dir
    for old in target_dir.glob(f"{char_prefix}_{emotion}_f*.png"):
        old.unlink()
    # 重命名新帧
    for i, f in enumerate(pngs, 1):
        # 同时清理旧 jpg 帧
        old_jpg = target_dir / f"{char_prefix}_{emotion}_f{i}.jpg"
        if old_jpg.exists():
            old_jpg.unlink()
        dst = target_dir / f"{char_prefix}_{emotion}_f{i}.png"
        f.rename(dst)


async def generate_one_anim(char_dir: str, emotion: str, src_jpg: str,
                             frames: int, skip_if_done: bool) -> dict:
    """生成单个动画：图生视频 → 抽帧 → 重命名"""
    src = CHAR_DIR / char_dir / src_jpg
    if not src.exists():
        return {"anim": f"{char_dir}/{emotion}", "ok": False, "error": f"src not found: {src}"}

    target_png = CHAR_DIR / char_dir / f"{char_dir}_{emotion}_f{frames}.png"
    if skip_if_done and target_png.exists():
        return {"anim": f"{char_dir}/{emotion}", "ok": True, "skipped": True}

    video_name = f"{char_dir}_{emotion}_anim"
    video_path = VIDEO_DIR / f"{video_name}.mp4"
    first_frame = img_to_data_uri(src)

    print(f"\n🎬 [{char_dir}/{emotion}] 生成视频（first_frame=base64, 2s, 1:1, 720p）...")
    from video_generate import video_generate

    params = [{
        "video_name": video_name,
        "prompt": PROMPT,
        "first_frame": first_frame,
        "ratio": "1:1",
        "duration": 2,
        "resolution": "720p",
        "camera_fixed": True,
        "watermark": False,
    }]
    result = await video_generate(
        params, batch_size=5, max_wait_seconds=900, model_name=MODEL
    )

    # 取视频 URL
    video_url = None
    for item in result.get("success_list", []):
        if video_name in item:
            video_url = item[video_name]
            break
    if not video_url:
        err = result.get("error_details", result.get("error_list", []))
        return {"anim": f"{char_dir}/{emotion}", "ok": False,
                "error": f"no video url: {json.dumps(err, ensure_ascii=False)[:200]}"}

    # 下载视频
    print(f"   ⬇ 下载视频...")
    import httpx
    try:
        with httpx.stream("GET", video_url, timeout=180.0) as r:
            r.raise_for_status()
            video_path.write_bytes(r.read())
    except Exception as e:
        return {"anim": f"{char_dir}/{emotion}", "ok": False, "error": f"download: {e}"}

    # 抽帧
    print(f"   🎞 抽帧 {frames} 张...")
    tmp_dir = CHAR_DIR / char_dir / "_tmp_frames"
    n = extract_frames(video_path, tmp_dir, frames)
    if n == 0:
        return {"anim": f"{char_dir}/{emotion}", "ok": False, "error": "extract 0 frames"}

    # 抠图（白色背景 → 透明，复用 rembg + alpha 二值化）
    print(f"   ✂ 抠图 {n} 帧...")
    cutout_frames(tmp_dir)

    # 重命名到目标
    rename_to_legacy(tmp_dir, char_dir, emotion)
    # 清理临时目录
    if tmp_dir.exists():
        tmp_dir.rmdir()

    return {"anim": f"{char_dir}/{emotion}", "ok": True, "frames": n, "video": str(video_path)}


async def main():
    ap = argparse.ArgumentParser(description="24FPS 立绘动画帧生成")
    ap.add_argument("--only", help="仅生成指定动画，如 boy/neutral")
    ap.add_argument("--frames", type=int, default=48, help="抽帧数（默认 48 = 24fps×2s）")
    ap.add_argument("--skip-if-done", action="store_true")
    args = ap.parse_args()

    anims = ANIMATIONS
    if args.only:
        # only 形如 boy/neutral
        parts = args.only.split("/")
        if len(parts) == 2:
            anims = [a for a in anims if a[0] == parts[0] and a[1] == parts[1]]

    print(f"🎨 生成 {len(anims)} 个动画，每个 {args.frames} 帧（24FPS × 2s）\n")
    results = []
    for char_dir, emotion, src_jpg in anims:
        r = await generate_one_anim(char_dir, emotion, src_jpg,
                                     args.frames, args.skip_if_done)
        results.append(r)
        if r["ok"]:
            print(f"   ✓ {r['anim']}: {r.get('frames', '?')} 帧")
        else:
            print(f"   ✗ {r['anim']}: {r['error']}")

    ok = sum(1 for r in results if r["ok"])
    print("\n" + "=" * 60)
    print(f"✅ 成功 {ok}/{len(results)}（视频保存于 {VIDEO_DIR}）")
    failed = [r for r in results if not r["ok"]]
    if failed:
        for r in failed:
            print(f"   ✗ {r['anim']}: {r['error']}")


if __name__ == "__main__":
    asyncio.run(main())

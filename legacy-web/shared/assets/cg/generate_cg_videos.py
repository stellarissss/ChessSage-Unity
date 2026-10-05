#!/usr/bin/env python3
"""
批量文生视频：将 11 张 CG 主题转为 5s 可循环视频（单场景、无情节、富有张力）。
输出：shared/assets/cg/videos/{cg_name}.mp4
用法：python generate_cg_videos.py
"""
import asyncio
import json
import sys
import os
from pathlib import Path

# 复用 skill 的 video_generate
SKILL_SCRIPT_DIR = "/data/user/skills/byted-seedance-video-generate/scripts"
sys.path.insert(0, SKILL_SCRIPT_DIR)
from video_generate import video_generate  # noqa

OUT_DIR = Path(__file__).resolve().parent / "videos"
OUT_DIR.mkdir(parents=True, exist_ok=True)

# 11 个 CG 主题：单场景、无情节、镜头静止、富有张力、符合物理规律
# 每个提示词：明确场景元素 + 物理动效(光尘/水波/呼吸式微动) + 情绪氛围，保证可循环
CG_PROMPTS = [
    ("cg_memory_hell",
     "教室场景单镜头，午后阳光从窗户斜射入室内，光尘在光柱中缓缓漂浮旋转，远处讲台与空课桌静默，色调冷峻压抑，镜头完全静止，物理真实的光影散射，5秒可循环，电影质感，无人物动作，富有压抑张力"),
    ("cg_memory_hungry",
     "教室夕阳场景单镜头，金色夕阳光线斜照在课桌上的中国象棋棋盘特写，棋子表面反光，空气中的金色光尘缓缓飘动，温暖怀旧色调，镜头静止，物理真实光线，5秒可循环，柔和唯美，无人物动作"),
    ("cg_memory_animal",
     "教室空座位单镜头，一束光从窗外照入空荡的课桌椅，尘埃在光柱中缓慢沉降旋转，冷清孤寂氛围，蓝灰色调，镜头静止，物理真实粒子运动，5秒可循环，无人物，富有失落张力"),
    ("cg_memory_human",
     "夜晚室内单镜头，一盏台灯的暖黄光照亮木桌上的中国象棋棋盘，棋子投下柔和阴影，台灯光晕微微呼吸式闪烁，窗外深蓝夜色，温馨宁静色调，镜头静止，物理真实光照，5秒可循环，无人物动作"),
    ("cg_memory_asura",
     "房间门缝光线单镜头，暖光从半开的门缝透出照在地板上形成光带，空气中光尘缓缓飘动，门外昏暗，温暖而压抑的张力氛围，镜头静止，物理真实光散射，5秒可循环，无人物"),
    ("cg_memory_heaven",
     "童年暖光场景单镜头，木质棋盘特写置于低矮小桌上，午后金色阳光洒落，光尘在暖光中缓缓飘浮，温馨童真氛围，柔和暖色调，镜头静止，物理真实光线，5秒可循环，无人物动作"),
    ("cg_ending_enlightenment",
     "悟道场景单镜头，圣洁金光从天穹缓缓降临如光柱，金色光尘与花瓣在光柱中缓缓飘落旋转，下方隐约莲花轮廓，空灵庄严氛围，金白色调，镜头静止，物理真实粒子下落，5秒可循环，无人物，神圣张力"),
    ("cg_ending_corruption",
     "堕落场景单镜头，巨大黑色漩涡在暗红色虚空中缓缓旋转，黑色碎片与余烬火星向漩涡中心螺旋吸入，压抑绝望氛围，暗红黑色调，镜头静止，物理真实螺旋粒子运动，5秒可循环，无人物，窒息张力"),
    ("cg_ending_samsara",
     "轮回场景单镜头，金色佛法法轮在虚空中缓缓自转，法轮周围环绕星光粒子缓缓流转，中心散发柔光，庄严永恒氛围，金暗色调，镜头静止，物理真实旋转与粒子，5秒可循环，无人物，静谧张力"),
    ("cg_ending_true_me",
     "真我场景单镜头，两面相对的镜子之间金色柔光缓缓流动，光尘在镜面间往复折射形成无限延伸的温暖光廊，和解治愈氛围，金白色调，镜头静止，物理真实镜面反射，5秒可循环，无人物，宁静张力"),
    ("cg_ending_exposed",
     "天道审判场景单镜头，巨大天平在暗色虚空中央缓缓倾斜，雷电影光在周围闪烁游走，红色警示光晕呼吸式明灭，审判压迫氛围，红暗色调，镜头静止，物理真实光影与雷电，5秒可循环，无人物，压迫张力"),
]


async def main():
    params = []
    for name, prompt in CG_PROMPTS:
        params.append({
            "video_name": name,
            "prompt": prompt,
            "ratio": "16:9",
            "duration": 5,
            "resolution": "720p",
            "camera_fixed": True,
            "watermark": False,
        })

    print(f"提交 {len(params)} 个文生视频任务（5s/720p/16:9/锁镜头）...\n")
    result = await video_generate(
        params,
        batch_size=10,
        max_wait_seconds=1500,
        model_name="doubao-seedance-1-0-pro-250528",
    )
    print("\n" + "=" * 60)
    print(json.dumps(result, indent=2, ensure_ascii=False))

    # 下载成功的视频
    import httpx
    ok = 0
    for item in result.get("success_list", []):
        for name, url in item.items():
            dst = OUT_DIR / f"{name}.mp4"
            try:
                with httpx.stream("GET", url, timeout=120.0) as r:
                    r.raise_for_status()
                    with open(dst, "wb") as f:
                        for chunk in r.iter_bytes():
                            f.write(chunk)
                print(f"✓ 下载 {name}.mp4 ({dst.stat().st_size//1024} KB)")
                ok += 1
            except Exception as e:
                print(f"✗ 下载失败 {name}: {e}")
    print(f"\n完成：{ok}/{len(params)} 视频已保存至 {OUT_DIR}")
    # 输出 URL 映射，便于手工补下
    urls = {k: v for it in result.get("success_list", []) for k, v in it.items()}
    (OUT_DIR / "_urls.json").write_text(json.dumps(urls, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())

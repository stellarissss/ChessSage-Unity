#!/usr/bin/env python3
"""
批量抠图：将 shared/assets/characters/*/*.jpg 的纯色暗底去除，
输出同名 .png（RGBA 透明背景），供对话页 galgame 半身像放大使用。

算法：四角采样背景色 → 颜色距离阈值 → 边界连通域（仅删与边框连通的背景，
保留角色内部暗部）→ alpha 通道轻度羽化（仅模糊 alpha，保持像素画 RGB 锐利）。
依赖：Pillow + numpy + scipy
"""
from pathlib import Path
import sys
import numpy as np
from scipy import ndimage
from PIL import Image, ImageFilter

CHAR_DIR = Path(__file__).resolve().parent / "characters"
THRESH = 42  # 颜色距离阈值；连通域策略下偏大些可更彻底去背景与边缘抗锯齿


def cutout(src: Path, dst: Path) -> tuple:
    img = Image.open(src).convert("RGB")
    arr = np.asarray(img).astype(np.int16)  # H,W,3
    H, W, _ = arr.shape

    # 四角采样 → 背景色（中位数）
    corners = np.array([arr[0, 0], arr[0, W - 1], arr[H - 1, 0], arr[H - 1, W - 1]])
    bg = np.median(corners, axis=0).astype(np.int16)

    # 颜色距离
    dist = np.sqrt(((arr - bg) ** 2).sum(axis=2))
    mask_bg = dist < THRESH  # True = 背景

    # 连通域：仅保留与图像边界连通的背景分量
    labeled, n = ndimage.label(mask_bg)
    if n > 0:
        border_labels = set()
        border_labels.update(labeled[0, :].tolist())
        border_labels.update(labeled[-1, :].tolist())
        border_labels.update(labeled[:, 0].tolist())
        border_labels.update(labeled[:, -1].tolist())
        border_labels.discard(0)
        bg_keep = np.isin(labeled, list(border_labels)) if border_labels else np.zeros_like(mask_bg)
    else:
        bg_keep = np.zeros_like(mask_bg)

    # 角色 mask = 非边界连通背景
    char_mask = ~bg_keep
    # 侵蚀 1px 去残留光晕，再膨胀 1px 复原
    char_mask = ndimage.binary_erosion(char_mask, iterations=1)
    char_mask = ndimage.binary_dilation(char_mask, iterations=1)

    # alpha：仅对 alpha 通道轻度羽化，RGB 保持锐利（像素画友好）
    alpha = (char_mask.astype(np.uint8)) * 255
    alpha_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.0))
    alpha = np.asarray(alpha_img)

    rgba = np.dstack([arr.astype(np.uint8), alpha])
    Image.fromarray(rgba, "RGBA").save(dst, "PNG")
    return H, W


def main():
    jpgs = sorted(CHAR_DIR.glob("*/*.jpg"))
    if not jpgs:
        print("未找到角色立绘 jpg。"); sys.exit(1)
    ok = 0
    fail = []
    for i, src in enumerate(jpgs, 1):
        dst = src.with_suffix(".png")
        try:
            h, w = cutout(src, dst)
            ok += 1
            print(f"[{i}/{len(jpgs)}] ✓ {src.parent.name}/{src.name} -> {dst.name} ({w}x{h})")
        except Exception as e:
            fail.append((src, str(e)))
            print(f"[{i}/{len(jpgs)}] ✗ {src.parent.name}/{src.name} : {e}")
    print("\n" + "=" * 50)
    print(f"✅ 成功 {ok}/{len(jpgs)}，输出 PNG 至各角色目录。")
    if fail:
        print(f"❌ 失败 {len(fail)} 张。")


if __name__ == "__main__":
    main()

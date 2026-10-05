#!/usr/bin/env python3
"""
批量抠图（rembg ML 语义分割版）：
将 shared/assets/characters/*/*.jpg 抠图为同名 .png（RGBA 透明背景），
供对话页 galgame 半身像放大使用。

相比 cutout_all.py（颜色距离+连通域），rembg 基于 U2Net 语义分割，
对头发、复杂边缘、非纯色背景效果显著更好。

依赖：Pillow + rembg（onnxruntime）+ numpy
首次运行 rembg 会下载 u2net 模型（~170MB），需联网。

用法：
    python cutout_rembg.py            # 处理全部
    python cutout_rembg.py --fallback # rembg 失败时回退到颜色距离算法
"""
from pathlib import Path
import sys
import argparse

import numpy as np
from PIL import Image, ImageFilter

CHAR_DIR = Path(__file__).resolve().parent / "characters"

# alpha 二值化后边缘羽化半径（px）。1.0-1.5 给出 1-2px 抗锯齿过渡。
EDGE_BLUR = 1.2


def cutout_rembg(src: Path, dst: Path) -> tuple:
    """用 rembg 去除背景，输出 RGBA PNG。

    修复半透明问题：rembg U2Net 输出的 alpha 蒙版在头发/衣服/皮肤等
    区域产生大量半透明像素（alpha 1-254）。通过 alpha 二值化 + 边缘羽化
    解决：
      1. alpha > BINARY_THRESHOLD → 255（完全不透明）
      2. alpha <= BINARY_THRESHOLD → 0（完全透明）
      3. 对二值化后的 mask 做轻度高斯模糊（EDGE_BLUR），保留抗锯齿边缘
    这样角色内部全部不透明，只有边缘 1-2px 过渡，头发/衣服不再半透明。
    """
    from rembg import remove  # 延迟导入，仅在实际抠图时加载模型
    img = Image.open(src).convert("RGB")
    # rembg 返回 RGBA（背景 alpha=0）
    out = remove(img)  # PIL Image RGBA

    r, g, b, a = out.split()
    a_arr = np.array(a)

    # ── alpha 二值化 ──
    # rembg 输出的半透明区域（头发/衣服等 alpha 1-254）全部归为不透明或透明
    BINARY_THRESHOLD = 128  # alpha > 128 → 不透明，否则 → 透明
    binary = (a_arr > BINARY_THRESHOLD).astype(np.uint8) * 255

    # ── 边缘羽化（抗锯齿）──
    # 对二值 mask 做轻度模糊，使边缘平滑（1-2px 过渡），内部保持 255
    a_final = Image.fromarray(binary, "L").filter(
        ImageFilter.GaussianBlur(EDGE_BLUR)
    )
    # 再次二值化确保模糊后内部仍为 255（防止模糊把边缘降低到半透明）
    a_arr2 = np.array(a_final)
    a_arr2 = np.where(a_arr2 > 200, 255, a_arr2)
    a_final = Image.fromarray(a_arr2.astype(np.uint8), "L")

    out = Image.merge("RGBA", (r, g, b, a_final))
    out.save(dst, "PNG")
    return out.height, out.width


def cutout_color_distance(src: Path, dst: Path) -> tuple:
    """回退算法：颜色距离 + 连通域（同 cutout_all.py）"""
    from scipy import ndimage
    import numpy as np
    from PIL import Image, ImageFilter

    THRESH = 42
    img = Image.open(src).convert("RGB")
    arr = np.asarray(img).astype(np.int16)
    H, W, _ = arr.shape
    corners = np.array([arr[0, 0], arr[0, W - 1], arr[H - 1, 0], arr[H - 1, W - 1]])
    bg = np.median(corners, axis=0).astype(np.int16)
    dist = np.sqrt(((arr - bg) ** 2).sum(axis=2))
    mask_bg = dist < THRESH
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
    char_mask = ~bg_keep
    char_mask = ndimage.binary_erosion(char_mask, iterations=1)
    char_mask = ndimage.binary_dilation(char_mask, iterations=1)
    alpha = (char_mask.astype(np.uint8)) * 255
    alpha_img = Image.fromarray(alpha, "L").filter(ImageFilter.GaussianBlur(1.0))
    alpha = np.asarray(alpha_img)
    rgba = np.dstack([arr.astype(np.uint8), alpha])
    Image.fromarray(rgba, "RGBA").save(dst, "PNG")
    return H, W


def main():
    ap = argparse.ArgumentParser(description="rembg ML 批量抠图")
    ap.add_argument("--fallback", action="store_true",
                    help="rembg 失败时回退到颜色距离算法")
    args = ap.parse_args()

    jpgs = sorted(CHAR_DIR.glob("*/*.jpg"))
    # 排除：动画帧 _f1-_f48（由 generate_anim_frames.py 单独生成+抠图）、
    # 黑色背景备份 .darkbg.jpg
    jpgs = [j for j in jpgs
            if not (f"_f" in j.stem and j.stem.split("_f")[-1].isdigit())
            and not j.stem.endswith("darkbg")]
    if not jpgs:
        print("未找到角色立绘 jpg。")
        sys.exit(1)

    ok = 0
    fail = []
    rembg_available = True
    try:
        import rembg  # noqa
    except ImportError:
        rembg_available = False
        print("⚠️  rembg 未安装，将使用回退算法。")

    for i, src in enumerate(jpgs, 1):
        dst = src.with_suffix(".png")
        try:
            if rembg_available:
                h, w = cutout_rembg(src, dst)
            elif args.fallback:
                h, w = cutout_color_distance(src, dst)
            else:
                raise RuntimeError("rembg unavailable and --fallback not set")
            ok += 1
            print(f"[{i}/{len(jpgs)}] ✓ {src.parent.name}/{src.name} -> {dst.name} ({w}x{h})")
        except Exception as e:
            # rembg 失败则尝试回退
            if args.fallback and rembg_available:
                try:
                    h, w = cutout_color_distance(src, dst)
                    ok += 1
                    print(f"[{i}/{len(jpgs)}] ~ {src.parent.name}/{src.name} -> {dst.name} (回退算法, {w}x{h})")
                    continue
                except Exception as e2:
                    fail.append((src, f"rembg:{e} | fallback:{e2}"))
            else:
                fail.append((src, str(e)))
            print(f"[{i}/{len(jpgs)}] ✗ {src.parent.name}/{src.name} : {e}")

    print("\n" + "=" * 50)
    print(f"✅ 成功 {ok}/{len(jpgs)}，输出 PNG 至各角色目录。")
    if fail:
        print(f"❌ 失败 {len(fail)} 张。")


if __name__ == "__main__":
    main()

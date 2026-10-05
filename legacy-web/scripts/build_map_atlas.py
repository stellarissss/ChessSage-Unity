#!/usr/bin/env python3
"""脚本：将 Kenney tiny-* 四包的 Tiles 按编号重排为 16px 网格 spritesheet。

输出：
  shared/assets/map/atlas/tiles_<pack>.png
  每 pack 一张，sprite index = 原 tile_XXXX 的编号，供 Phaser spritesheet 使用。
用法：python scripts/build_map_atlas.py
"""
from pathlib import Path
from PIL import Image

ATLAS_DIR = Path(__file__).resolve().parent.parent / "shared" / "assets" / "map" / "atlas"
PACKS_DIR = Path(__file__).resolve().parent.parent / "shared" / "assets" / "map"
PACKS = ["tiny-dungeon", "tiny-farm", "tiny-town", "tiny-battle"]
COLS = 12


def main():
    ATLAS_DIR.mkdir(parents=True, exist_ok=True)
    for pack in PACKS:
        tiles_dir = PACKS_DIR / pack / "Tiles"
        files = [f for f in tiles_dir.iterdir() if f.suffix == ".png"]
        files.sort(key=lambda f: int(f.stem.split("_")[1]))
        n = len(files)
        rows = (n + COLS - 1) // COLS
        sheet = Image.new("RGBA", (COLS * 16, rows * 16), (0, 0, 0, 0))
        for i, f in enumerate(files):
            im = Image.open(f).convert("RGBA")
            if im.size != (16, 16):
                im = im.resize((16, 16), Image.NEAREST)
            sheet.paste(im, ((i % COLS) * 16, (i // COLS) * 16), im)
        out = ATLAS_DIR / f"tiles_{pack}.png"
        sheet.save(out)
        print(f"{pack}: {n} tiles -> {out} ({COLS}x{rows} grid)")


if __name__ == "__main__":
    main()
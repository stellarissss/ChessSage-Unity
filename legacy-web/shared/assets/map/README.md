# 大地图游戏资产（Kenney · CC0）

本目录为“剧情模式 · 2.5D 六道大陆”提供全部瓦片美术。

## 来源（Kenney.nl，全部 CC0 授权，可商用不需署名）

| 名称 | 官网 | 用途 |
|------|------|------|
| Tiny Farm | https://kenney.nl/assets/tiny-farm | 草地/农田/牧场/作物/围栏（饿鬼·荒漠、畜生·牧场、人·沙漠） |
| Tiny Town | https://kenney.nl/assets/tiny-town | 城镇/石板路/房屋/树木/路灯（中央平原） |
| Tiny Battle | https://kenney.nl/assets/tiny-battle | 雪原/战场/军营/旗帜/水域（北境雪原、东南战场） |
| Tiny Dungeon | https://kenney.nl/assets/tiny-dungeon | 地牢石地/石墙/火把/碑石（西南地牢） |

授权说明：Kenney 资产均以 CC0（Public Domain）发布，允许任何人以任何目的自由使用、修改、再发布，无需署名。

## 目录结构

```
shared/assets/map/
├── tiny-farm/      原始解压包（Tiles/ 单一瓦片 PNG；Tiled 示例工程）
├── tiny-town/      原始解压包
├── tiny-battle/    原始解压包
├── tiny-dungeon/   原始解压包
├── atlas/          由 scripts/build_map_atlas.py 生成的普通网格 spritesheet
│   ├── tiles_tiny-farm.png     12×11 网格（132 瓦片，index=原编号）
│   ├── tiles_tiny-town.png     12×11 网格（132 瓦片）
│   ├── tiles_tiny-battle.png   12×17 网格（198 瓦片）
│   └── tiles_tiny-dungeon.png  12×11 网格（132 瓦片）
```

## 瓦片编号约定

`atlas/tiles_<pack>.png` 是 16px 均匀网格 spritesheet，**帧索引 = 原始瓦片编号**（`tile_0001.png` → index 1）。`configs/overworld.json` 中 `decor_anchors` / `regions.ground` / `decor_plant` 引用的瓦片均以此索引为准。可查阅各包 `Tiles/tile_XXXX.png` 对照。

## 重新生成

修改或补充瓦片后运行：

```bash
python scripts/build_map_atlas.py
```

## 常用瓦片速查（index）

| 用途 | tiny-town | tiny-farm | tiny-dungeon | tiny-battle |
|------|-----------|-----------|--------------|-------------|
| 地面 | 0/1/2 草地、48-51 石板 | 0/1 草地、48-51 耕地、60-63 旱土 | 50-52 地牢石地 | 0/1 草地、23 雪地 |
| 树/植被 | 3-11、15-20 | 3/15/27（树）、0/30 灌木 | — | 5/94 树 |
| 建筑/围栏 | 87 窗、30 灌木 | 14/2 栅栏 | 39 石墙、65 碑 | 30/31 绿色营帐、70/71 红旗 |
| 装饰 | 116 钥匙、30 花盆 | 9-11 袋 | 129 火把、66 桶 | 6/7 岩石 |
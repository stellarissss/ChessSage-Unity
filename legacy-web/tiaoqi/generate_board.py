#!/usr/bin/env python3
"""
中国跳棋六角星棋盘生成器
生成 board.json，包含121个位置、邻接表、营区定义

坐标系统（双倍列坐标）：
- 位置用 [row, col] 表示，col 为双倍值（偶数行col为偶数，奇数行col为奇数）
- 邻接偏移： (0,±2), (∓1,∓1), (∓1,±1) 共6方向
- 跳跃偏移（2倍邻接）： (0,±4), (∓2,∓2), (∓2,±2)
- col 偏移 +12 使所有列非负（范围 0-24）
"""
import json
from collections import defaultdict

# 每行的位置数
ROW_COUNTS = [1, 2, 3, 4, 13, 12, 11, 10, 9, 10, 11, 12, 13, 4, 3, 2, 1]
COL_SHIFT = 12  # 使所有列非负

# 邻接偏移（6方向）
ADJ_OFFSETS = [(0, -2), (0, 2), (-1, -1), (-1, 1), (1, -1), (1, 1)]


def generate_positions():
    """生成所有121个位置"""
    positions = set()
    for row, count in enumerate(ROW_COUNTS):
        # 该行列的起始值（双倍坐标，居中）
        # col 从 -(count-1) 到 (count-1)，步长2，然后 +COL_SHIFT
        start = -(count - 1)
        for i in range(count):
            col = start + i * 2 + COL_SHIFT
            positions.add((row, col))
    return positions


def compute_adjacency(positions):
    """计算邻接表"""
    adjacency = defaultdict(list)
    for (r, c) in positions:
        for (dr, dc) in ADJ_OFFSETS:
            neighbor = (r + dr, c + dc)
            if neighbor in positions:
                adjacency[(r, c)].append(list(neighbor))
    return adjacency


def get_camp_positions():
    """获取营区位置"""
    red_camp = []
    black_camp = []
    for row in range(0, 4):  # 红方营区 rows 0-3
        count = ROW_COUNTS[row]
        start = -(count - 1)
        for i in range(count):
            col = start + i * 2 + COL_SHIFT
            red_camp.append([row, col])
    for row in range(13, 17):  # 黑方营区 rows 13-16
        count = ROW_COUNTS[row]
        start = -(count - 1)
        for i in range(count):
            col = start + i * 2 + COL_SHIFT
            black_camp.append([row, col])
    return red_camp, black_camp


def main():
    positions = generate_positions()
    assert len(positions) == 121, f"Expected 121 positions, got {len(positions)}"

    adjacency = compute_adjacency(positions)

    red_camp, black_camp = get_camp_positions()
    assert len(red_camp) == 10, f"Red camp should have 10 positions, got {len(red_camp)}"
    assert len(black_camp) == 10, f"Black camp should have 10 positions, got {len(black_camp)}"

    # 构建 positions dict ("row,col": true)
    positions_dict = {}
    for (r, c) in sorted(positions):
        positions_dict[f"{r},{c}"] = True

    # 构建 adjacency dict ("row,col": [[r,c],...])
    adjacency_dict = {}
    for (r, c) in sorted(positions):
        key = f"{r},{c}"
        neighbors = adjacency.get((r, c), [])
        adjacency_dict[key] = sorted(neighbors)

    # 构建像素坐标（用于前端渲染参考）
    # real_x = col / 2, real_y = row
    pixel_coords = {}
    for (r, c) in sorted(positions):
        pixel_coords[f"{r},{c}"] = {"x": c / 2.0, "y": r}

    board_config = {
        "_metadata": {
            "version": "1.0",
            "description": "中国跳棋六角星棋盘 - 121位置双倍列坐标系统"
        },
        "geometry": {
            "board_type": "hexagonal_star",
            "total_positions": 121,
            "rows": 17,
            "max_col": 24,
            "coord_system": "doubled_column",
            "adjacency_offsets": ADJ_OFFSETS,
            "hop_offsets": [(2 * dr, 2 * dc) for (dr, dc) in ADJ_OFFSETS],
            "camps": {
                "red": {
                    "rows": [0, 3],
                    "direction": "down",
                    "positions": sorted(red_camp)
                },
                "black": {
                    "rows": [13, 16],
                    "direction": "up",
                    "positions": sorted(black_camp)
                }
            }
        },
        "positions": positions_dict,
        "adjacency": adjacency_dict,
        "pixel_coords": pixel_coords,
        "appearance": {
            "background_color": "#f5e6c8",
            "line_color": "#000000",
            "grid": {
                "line_thickness": 0.02,
                "show_lines": True
            },
            "layout": {
                "board_size": "90vmin",
                "star_ratio": 1.0,
                "real_x_range": [0, 12],
                "real_y_range": [0, 16]
            },
            "decorations": {
                "border": {"enabled": False}
            }
        }
    }

    with open("/workspace/checkers/configs/board.json", "w", encoding="utf-8") as f:
        json.dump(board_config, f, ensure_ascii=False, indent=2)

    print(f"生成完成: 121个位置, {len(adjacency_dict)}个邻接条目")
    print(f"红方营区: {len(red_camp)}个位置")
    print(f"黑方营区: {len(black_camp)}个位置")

    # 验证邻接
    total_neighbors = sum(len(v) for v in adjacency_dict.values())
    print(f"邻接总数: {total_neighbors} (平均 {total_neighbors/121:.1f} 邻居/位置)")


if __name__ == "__main__":
    main()

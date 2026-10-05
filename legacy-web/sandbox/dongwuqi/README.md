# 无限制动物棋（斗兽棋）

**利用 AI 大模型作弊的斗兽棋游戏**

一个基于大语言模型（LLM）的创新斗兽棋游戏平台。玩家可以通过自然语言与 AI 对战，并体验独特的"作弊"机制——让 AI 实时修改游戏规则。

---

## 核心玩法

与传统斗兽棋不同，**无限制动物棋**的核心乐趣在于"作弊"：

- **自然语言指令**：输入"让老鼠能斜着走"、"狮子跳河不用被阻挡"、"陷阱不降级了"等指令
- **AI 实时改规则**：AI 将自然语言解析为结构化的 JSON Patch，直接修改游戏配置
- **灵活编码（Flexible Coding）**：所有可变元素（动物等级、移动方式、吃子规则、棋盘区域）都抽象为 JSON 配置，AI 可随时重写

> 🎯 宗旨：**规则不是铁板一块，AI 让它随你心意而变。**

---

## 棋盘与基本规则

### 棋盘结构

7 列 × 9 行格子制棋盘：

```
    0   1   2   3   4   5   6
   ┌───┬───┬───┬───┬───┬───┬───┐
0  │   │ ✕ │   │ ★ │   │ ✕ │   │  ← 黑方兽穴★、陷阱✕
   ├───┼───┼───┼───┼───┼───┼───┤
1  │   │   │   │ ✕ │   │   │   │
   ├───┼───┼───┼───┼───┼───┼───┤
2  │ 🦁│ 🐶│ 🐆│ 🐺│ 🐭│   │ 🐘│  ← 黑方（上方）
   ├───┼───┼───┼───┼───┼───┼───┤
3  │ ≈ │ ≈ │   │   │   │ ≈ │ ≈ │  ← 水域（≈）
   ├───┼───┼───┼───┼───┼───┼───┤
4  │ ≈ │ ≈ │   │   │   │ ≈ │ ≈ │
   ├───┼───┼───┼───┼───┼───┼───┤
5  │ ≈ │ ≈ │   │   │   │ ≈ │ ≈ │
   ├───┼───┼───┼───┼───┼───┼───┤
6  │ 🐘│   │ 🐭│ 🐺│ 🐆│ 🐶│ 🦁│  ← 红方（下方）
   ├───┼───┼───┼───┼───┼───┼───┤
7  │   │   │   │ ✕ │   │   │   │
   ├───┼───┼───┼───┼───┼───┼───┤
8  │   │ ✕ │   │ ★ │   │ ✕ │   │  ← 红方兽穴★、陷阱✕
   └───┴───┴───┴───┴───┴───┴───┘
```

### 八种动物与等级

| 等级 | 动物 | Emoji | 别名 | 备注 |
|:----:|------|:-----:|------|------|
| 8 | 象 | 🐘 | Elephant | 除鼠外最高等级 |
| 7 | 狮 | 🦁 | Lion | **可跳河**（ray 移动） |
| 6 | 虎 | 🐯 | Tiger | **可跳河**（ray 移动） |
| 5 | 豹 | 🐆 | Leopard | |
| 4 | 狼 | 🐺 | Wolf | |
| 3 | 狗 | 🐶 | Dog | |
| 2 | 猫 | 🐱 | Cat | |
| 1 | 鼠 | 🐭 | Rat | **唯一可入水**，**可吃象** |

### 基础移动

每回合走一步，向上下左右四方向移动一格（jump `[1,0]` + `rotate4` + `sym` 对称展开）。

### 吃子规则（等级制）

- **默认规则**：高等级动物可以吃同等级或低等级动物（`rank_ge`）
- **鼠克象**：鼠（rank 1）可以吃象（rank 8），但象不能吃鼠
- **同归于尽**：同级动物相遇，双方都被吃掉

### 特殊地形

#### 🌊 水域（Water）
- 棋盘中央 2×3×2 共 12 格水域
- **只有鼠可以进入水域**，其他动物不能踏入
- 鼠在水中时**免疫一切攻击**
- 鼠在水中**不能吃岸上的象**

#### ✕ 陷阱（Trap）
- 每方 3 个陷阱，位于己方兽穴周围
- **敌方动物进入己方陷阱后，等级降为 0**
- 陷阱中的敌方动物可以被己方任何动物吃掉（无论己方动物等级多低）

#### ★ 兽穴（Den）
- 每方 1 个兽穴，位于底边中心
- **己方动物不能进入己方兽穴**
- **己方动物进入敌方兽穴即获胜**

### 狮虎跳河

- 狮（🦁）和虎（🐯）可以**横向或纵向跳过整片水域**
- 跳河为 ray 移动：沿直线跨越水域，落到对岸陆地
- **条件**：跳河路径上的所有中间格必须全是水域，且**水域中没有鼠阻挡**

---

## 胜负判定

按优先级顺序判定：

1. **攻入兽穴**（`enter_den`）：己方动物进入敌方兽穴 → 直接获胜
2. **全歼对手**（`annihilation`）：吃光对方所有存活动物 → 获胜
3. **困毙对手**（`stalemate`）：对方所有存活动物都无法移动 → 获胜

---

## 项目结构

```
dongwuqi/
├── main.py                  # FastAPI 服务入口（端口 8003）
├── rule_engine.py           # 规则引擎（jump/ray/sym/where + rank/capture）
├── chess_ai.py              # Minimax + Alpha-Beta 剪枝 AI
├── ai_orchestrator.py       # 二级 AI 流水线（意图解析 → JSON Patch → 校验 → 应用）
├── mechanism_engine.py      # 机制引擎（跳过回合/AI接管/额外回合等）
├── prompts.py               # AI 提示词（动物棋语义）
│
├── configs/                 # 游戏配置（灵活编码的核心）
│   ├── board.json           # 棋盘几何（7×9、水域/陷阱/兽穴坐标）
│   ├── pieces_red.json      # 红方 8 动物规则（rank/moves/capture）
│   ├── pieces_black.json    # 黑方 8 动物规则
│   ├── rules.json           # 胜负规则 + 5 条特殊规则 + AI 难度
│   ├── board_state.json     # 当前棋局状态（16 枚 emoji 棋子）
│   ├── ui_config.json       # UI 主题（emoji 字体族、颜色）
│   └── initial/             # 初始配置备份（用于"重置所有配置"）
│
├── schemas/                 # JSON Schema（AI 修改的校验标准）
│   ├── board.schema.json
│   ├── pieces.schema.json
│   ├── rules.schema.json
│   ├── board_state.schema.json
│   └── ui_config.schema.json
│
└── static/                  # 前端界面
    ├── index.html           # 入口页面
    ├── app.js               # Web Component（格子制渲染 + emoji 棋子）
    └── style.css            # 样式表
```

---

## 快速开始

### 安装依赖

```bash
cd dongwuqi
pip install -r requirements.txt   # fastapi, uvicorn, httpx, pydantic
```

### 启动服务

```bash
python main.py
```

服务运行在 **http://localhost:8003/**

### 统一启动（从项目根目录）

```bash
cd ..
python main.py
# 访问 http://localhost:8003/ 进入动物棋
```

---

## 灵活编码：JSON 配置体系

本项目践行**灵活编码（Flexible Coding）**理念——用 AI 现场生成代码实现大部分需求，所有可变元素抽象为 JSON。

### 动物移动原语（`moves`）

```json
{
  "kind": "jump",           // jump = 步移，ray = 直射线
  "to": [1, 0],             // 相对目标坐标
  "block": [],              // 阻挡检查（空 = 无阻挡要求）
  "land": "any",            // 落点类型
  "sym": "rotate4",         // 对称展开：上/下/左/右四方向
  "where": [                // 条件表达式
    {"not": {"in_water": {"pos": "$dest"}}},
    {"not": {"in_own_den": {"pos": "$dest"}}}
  ]
}
```

### 狮虎跳河原语（`ray` + `path_constraint`）

```json
{
  "kind": "ray",
  "dir": [1, 0],
  "max": 4,                 // 最大步数
  "screens": 0,
  "sym": "rotate4",
  "path_constraint": {      // 新增原语：路径约束
    "must_be": "water",     // 中间格必须全是水域
    "no_blocker": true      // 中间格不能有任何棋子
  },
  "where": [
    {"not": {"in_water": {"pos": "$dest"}}}
  ]
}
```

### 吃子规则（`capture`）

```json
{
  "mode": "rank_ge",         // 默认：rank >= 对方才能吃
  "exceptions": [            // 例外规则
    {"can_eat": "elephant"} // 鼠可以吃象
  ],
  "water_rules": {           // 水域特殊规则
    "invulnerable_in_water": true,
    "cannot_attack_from_water": ["elephant"]
  }
}
```

### 棋盘区域（`regions`）

```json
{
  "water": {
    "cells": [[1,3],[2,3],[4,3],[5,3],[1,4],[2,4],[4,4],[5,4],[1,5],[2,5],[4,5],[5,5]]
  },
  "trap_red": { "cells": [[2,8],[4,8],[3,7]] },
  "trap_black": { "cells": [[2,0],[4,0],[3,1]] },
  "den_red": { "cells": [[3,8]] },
  "den_black": { "cells": [[3,0]] }
}
```

---

## AI 作弊示例

在游戏中输入自然语言指令，AI 会解析并修改 JSON 配置：

| 玩家指令 | AI 修改内容 | 效果 |
|---------|------------|------|
| "让老鼠能斜着走" | 给 rat 的 moves 增加 `sym: "diag4"` 条目 | 鼠可沿对角线移动 |
| "狮子跳河不用被阻挡" | 移除 lion ray 的 `path_constraint.no_blocker` | 狮跳河无视水中鼠 |
| "陷阱不降级了" | `rules.special_rules.trap_neutralizes_rank.enabled → false` | 陷阱不再降低等级 |
| "让猫可以吃象" | 给 cat 的 capture.exceptions 增加 `{"can_eat": "elephant"}` | 猫获得吃象能力 |
| "增加一只凤凰" | `pieces_red.json` / `pieces_black.json` 新增 `phoenix` 条目 | 新动物加入游戏 |

---

## API 接口

### 获取配置

```bash
GET /api/config/all              # 获取所有配置
GET /api/config/board            # 棋盘配置
GET /api/config/board_state      # 当前棋局
GET /api/config/rules            # 游戏规则
GET /api/config/pieces_red       # 红方动物规则
GET /api/config/pieces_black     # 黑方动物规则
GET /api/config/ui_config        # UI 主题
```

### 走棋

```bash
POST /api/move
Content-Type: application/json

{"piece_id": "r_lion_1", "to": [5, 8]}
```

### 获取合法移动

```bash
POST /api/valid_moves
Content-Type: application/json

{"piece_id": "r_lion_1", "to": [6, 8]}
```

### 自然语言指令

```bash
POST /api/command
Content-Type: application/json

{"command": "让老鼠能斜着走"}
```

### AI 走棋

```bash
POST /api/ai/move
```

### 其他

```bash
POST /api/apikey                  # 设置 DeepSeek API Key
POST /api/difficulty              # 设置 AI 难度（easy/medium/hard）
POST /api/undo                    # 悔棋
POST /api/undo_config             # 撤回 AI 规则修改
POST /api/reset                   # 重置棋盘
GET  /api/logs                    # 获取 AI 对话日志
```

---

## 前端界面

- **格子制渲染**：7×9 格子，棋子在格子中心
- **emoji 棋子**：🐘🦁🐯🐆🐺🐶🐱🐭，红方/黑方以边框色区分
- **地形装饰**：水域蓝色填充、陷阱黄色对角十字、兽穴红色星标
- **暖纸 + 墨黑 + 霓虹高亮**：与象棋保持一致的视觉体系

---

## 技术架构

### 规则引擎（`rule_engine.py`）

- `jump` / `ray` / `sym` / `where` 四原子移动体系
- `path_constraint`：跳河路径约束（`must_be: water` + `no_blocker: true`）
- `_can_capture` 五层判定：水域免疫 → 水中攻击限制 → 陷阱降级 → 例外规则 → 默认 rank_ge
- 区域条件：`in_water` / `in_trap` / `in_enemy_trap` / `in_own_den` / `in_enemy_den`

### AI 引擎（`chess_ai.py`）

- Minimax + Alpha-Beta 剪枝
- 8 动物价值体系：elephant(800) → lion(700) → tiger(600) → leopard(500) → wolf(400) → dog(300) → cat(200) → rat(100)
- 评估函数：接近敌方兽穴奖励 + 中心控制 + 保护己方兽穴

### AI 编排器（`ai_orchestrator.py`）

- **二级流水线**：意图解析（A/B/C/C+/D/E/F 分类）→ 代码生成（RFC 6902 JSON Patch）→ Schema 校验 → 应用/重试
- 动物棋专用别名映射：小鼠→rat, 老虎→tiger 等
- 范围校验动态读取 board_config（7×9）

### 机制引擎（`mechanism_engine.py`）

5 种原子机制原语：
- `skip_turns`（冻结）
- `ai_control`（AI 接管）
- `player_control`（玩家控制）
- `random_moves`（随机走棋）
- `extra_turns`（额外回合）
- `move_limits`（多步行走）

---

## 开发指南

### 添加新动物

1. 在 `configs/pieces_red.json` 和 `configs/pieces_black.json` 中新增动物定义
2. 设置 `rank`、`moves`、`capture` 字段
3. 在 `configs/board_state.json` 中放置初始棋子
4. AI 自动识别新动物（无需改代码）

### 修改棋盘尺寸

1. 修改 `configs/board.json` 的 `geometry.width` / `geometry.height`
2. 更新 `regions` 中的水域/陷阱/兽穴坐标
3. 更新 `configs/board_state.json` 的棋子位置
4. 前端自动适配（动态读取 width/height）

### 自定义 AI 性格

修改 `configs/rules.json` 的 `ai_difficulty.personality`：

```json
{
  "type": "custom",
  "aggressiveness": 0.8,
  "conservatism": 0.3,
  "randomness_override": 0.2,
  "depth_override": 4,
  "value_biases": {"lion": 1000, "rat": 200}
}
```

---

## 许可证

MIT License

---

**无限制动物棋** — 规则随你而变，AI 听你号令 🐘🦁🐯🐆🐺🐶🐱🐭

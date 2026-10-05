# 无限制黑白棋 — Unlimited Reversi

**利用 AI 大模型作弊的黑白棋策略游戏**

> 经典的 Reversi/Othello，但你可以用自然语言实时修改一切规则。

---

## 核心玩法

| 阶段 | 描述 |
|---|---|
| **正常对弈** | 黑白双方轮流落子，沿 8 方向夹吃翻转对方棋子 |
| **输入指令** | 随时输入自然语言（"把棋盘改成 10×10"、"让 AI 接管白棋"、"创建一个能跳吃的棋子"） |
| **AI 解析** | 二级 AI 协作流水线：意图解析 → 代码生成（JSON Patch） |
| **校验执行** | Schema 校验 + 规则验证，通过后即时生效 |
| **可撤销** | 所有 AI 修改进入撤销栈，可随时撤回 |

### 黑白棋特有创新

- **flip 翻转原语**：第三原语（jump/ray 之外），沿方向夹吃翻转对方棋子，是一等公民的可编码原语
- **AI 自由修改**：棋盘大小、规则、棋子属性、机制、UI 均可通过自然语言修改
- **多子者胜**：游戏结束时棋子数多的一方获胜
- **Pass 机制**：一方无合法落子时自动跳过回合
- **机制引擎**：冻结回合 / AI 接管 / 随机走棋 / 额外回合 / 多步行走 / 玩家控制
- **AI 性格**：标准型 / 激进型 / 保守型 / 随机型 / 自定义

---

## 快速开始

### 环境要求

- Python 3.8+
- Node.js（前端开发可选）

### 安装依赖

```bash
pip install -r requirements.txt
```

### 启动服务

```bash
python main.py
```

访问 **http://localhost:8005/** 即可开始游戏。

### 配置 AI

首次打开页面会提示设置 DeepSeek API Key。在设置弹窗中填入密钥即可启用 AI 指令解析与走棋。

---

## 项目结构

```
heibaiqi/
├── main.py                # FastAPI 服务入口（端口 8005）
├── rule_engine.py         # 规则引擎（flip 原语 + 合法落子点 + 翻转逻辑）
├── chess_ai.py            # AI 引擎（Minimax + Alpha-Beta + 位置权重）
├── ai_orchestrator.py     # 二级 AI 编排器（意图解析 → 代码生成）
├── mechanism_engine.py    # 机制引擎（6 原语 + AI 性格）
├── prompts.py             # AI 提示词（分类示例 + flip 原语说明）
├── requirements.txt       # Python 依赖
│
├── configs/               # JSON 配置
│   ├── board.json         # 棋盘几何（8×8）+ 外观
│   ├── board_state.json   # 运行时状态（棋子/回合/历史）
│   ├── pieces_black.json  # 黑方棋子定义（disc + flip 原语）
│   ├── pieces_white.json  # 白方棋子定义（disc + flip 原语）
│   ├── rules.json         # 规则（胜利条件/AI 难度/特殊规则）
│   ├── ui_config.json     # UI 主题（配色/布局）
│   ├── token_stats.json   # Token 消耗统计
│   └── initial/           # 初始备份（重置用）
│
└── static/                # 前端
    ├── index.html         # 入口页
    └── app.js             # Shadow DOM Web Component（含样式 + 逻辑）
```

---

## 技术架构

### 前端

- **Shadow DOM Web Component**：`<heibaiqi-board>` 自定义元素
- **Playfair Display + DM Sans + JetBrains Mono** 字体栈
- **极简水墨美学**：纸色底 (#fafaf8) + 墨色文 (#1a1a1a) + 绿色棋盘 (#1a5d3a)
- **实心圆棋子**：黑子 (#000000) / 白子 (#ffffff + 黑边)
- **金色合法落子点指示器** + CSS 3D 翻转动画 (rotateY)
- 响应式布局，桌面端优先

### 后端

- **FastAPI** + Uvicorn，端口 8005
- 异步 LLM 请求（httpx）
- JSON Patch (RFC 6902) 优先策略
- 撤销栈（最多 10 层）

### AI 引擎

- **二级 AI 协作**：deepseek-flash（意图解析 + 代码生成，模型名统一取自 `config.json`）
- **对弈 AI**：Minimax + Alpha-Beta 剪枝，深度 1-7（easy/normal/hard/master）
- **评估函数**：角点权重 + 稳定棋子 + 行动力差 + 棋子数差 + 边缘控制 + AI 性格参数

### 规则引擎

- **flip 原语**：`{kind: "flip", dir: [1,0], max: 6, land: "enemy", sym: "rotate4_mirror"}`
- **对称展开**：rotate4_mirror 自动生成 8 方向
- **合法落子点**：`get_valid_placements(side, board_state)` — 聚合所有己方 disc 的 flip 支撑点
- **翻转执行**：`apply_flip_captures(pos, side, board_state)` — 沿 8 方向夹吃翻转

---

## API 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/config/all` | 获取所有配置 |
| `GET` | `/api/config/{name}` | 获取指定配置 |
| `POST` | `/api/valid_moves` | 获取合法落子点 `{side}` |
| `POST` | `/api/move` | 玩家落子 `{to: [x,y]}` |
| `POST` | `/api/ai_move` | AI 走棋 |
| `POST` | `/api/undo` | 悔棋 |
| `POST` | `/api/command` | 自然语言指令 `{command}` |
| `POST` | `/api/restart` | 重新开始 |
| `POST` | `/api/difficulty` | 设置 AI 难度 |
| `POST` | `/api/reset_configs` | 重置所有配置 |
| `POST` | `/api/apikey` | 设置 API Key |

---

## 游戏操作

### 落子

点击棋盘上的**金色圆点**（合法落子点指示器）直接落子。也可直接点击棋盘空格（如为合法位置则自动落子）。

### 自然语言指令

在底部输入框输入指令，例如：

- "把棋盘改成 10×10"
- "让 AI 接管白棋"
- "创建一个能跳吃的棋子"
- "悔一步棋"
- "把黑棋变成红色"
- "增加一个额外回合"

### 操作按钮

- **悔棋**：撤回上一步走棋
- **撤回 AI 修改**：撤回上一次 AI 配置修改
- **重新开始**：重置棋盘
- **重置所有配置**：恢复所有配置到初始状态

---

## 配置文件说明

### board.json

```json
{
  "geometry": { "width": 8, "height": 8, "river_line": null, "palace": null },
  "appearance": { "background_color": "#1a5d3a", "line_color": "#000000" }
}
```

### board_state.json

```json
{
  "pieces": [
    { "id": "white_disc_1", "type": "disc", "side": "white", "position": [3,3] },
    { "id": "black_disc_1", "type": "disc", "side": "black", "position": [4,3] },
    { "id": "black_disc_2", "type": "disc", "side": "black", "position": [3,4] },
    { "id": "white_disc_2", "type": "disc", "side": "white", "position": [4,4] }
  ],
  "current_turn": "black"
}
```

标准 Othello 开局：中心 2×2 区域交替放置 4 子，黑方先手。

### rules.json

```json
{
  "win_conditions": {
    "most_discs": { "enabled": true, "description": "多子者胜" },
    "no_valid_moves_both": { "enabled": true, "description": "双方无路可走" },
    "board_full": { "enabled": true, "description": "棋盘已满" }
  },
  "special_rules": {
    "pass_when_no_move": { "enabled": true }
  },
  "ai_difficulty": {
    "current": "normal",
    "levels": {
      "easy": { "depth": 1 },
      "normal": { "depth": 3 },
      "hard": { "depth": 5 },
      "master": { "depth": 7 }
    }
  }
}
```

---

## 设计哲学

**灵活编码（最高纲领）**：项目以"用 AI 现场生成代码实现大部分要求"为核心创新形式。除已完成的硬编码部分外，所有新功能皆不可以用硬编码实现，必须用 AI 编码实现。

为此可以牺牲稳定性、token 花销、速度等，因为**灵活编码是项目设立的初衷**。

---

## 许可证

MIT License

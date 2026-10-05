# 无限制跳棋

**利用AI大模型作弊的中国跳棋**

一个基于大语言模型(LLM)的创新跳棋游戏，玩家可以通过自然语言与AI对战，并体验独特的"作弊"机制——让AI自由修改游戏规则。

## 项目简介

无限制跳棋基于经典中国跳棋（六角星棋盘），核心玩法是**"利用AI大模型作弊"**。与传统跳棋不同，玩家可以通过自然语言指令让AI实时修改棋盘规则、棋子能力、胜利条件等。

### 核心特性

- **六角星棋盘**: 121个位置，17行双倍列坐标系统，6方向移动
- **自然语言交互**: 通过自然语言与AI对话，修改游戏规则
- **AI自由修改**: AI可以修改棋子移动方式、胜利条件、棋盘外观等
- **连跳机制**: 支持连续跳跃，一步跳过多个棋子
- **不吃子规则**: 跳过的棋子保留在原位，纯粹是位移竞速
- **营区胜利**: 将所有棋子移入对方营区即可获胜

## 跳棋规则

### 棋盘

- **形状**: 六角星形（两个对顶三角形 + 中间六边形）
- **位置数**: 121个
- **坐标系统**: 双倍列（doubled_column）
  - row范围: 0-16
  - col范围: 0-24（偶数行col为偶数，奇数行col为奇数）
- **红方营区**: 顶部三角（rows 0-3），10个位置
- **黑方营区**: 底部三角（rows 13-16），10个位置

### 棋子

- 每方10个棋子
- 红方棋子: 纯色圆形（#cc0000）
- 黑方棋子: 纯色圆形（#1a1a1a）

### 移动规则

1. **单步移动（step）**: 向相邻空位移动一格（6个方向）
2. **跳跃移动（hop）**: 隔一个棋子跳到对称位置
3. **连跳（chain）**: 跳跃后可以继续跳跃，整条路径算1步
4. **不吃子**: 被跳过的棋子保留在原位

### 胜利条件

- **全部入营**: 一方所有棋子都进入对方营区即获胜
- **困毙**: 一方无合法移动可走则判负

## 文件结构

```
checkers/
├── main.py                  # FastAPI 服务入口（端口8004）
├── rule_engine.py           # 跳棋规则引擎（step/hop原语）
├── chess_ai.py              # AI引擎（Minimax + Alpha-Beta + 推进度评估）
├── ai_orchestrator.py       # AI指令解析与配置修改编排器
├── mechanism_engine.py      # 游戏机制引擎（跳过回合/AI接管/随机走棋等）
├── prompts.py               # AI提示词模板
├── generate_board.py        # 六角星棋盘生成脚本
├── static/                  # 前端界面
│   ├── index.html           # 主页面
│   ├── app.js               # 前端逻辑（CheckersBoard组件）
│   └── style.css            # 样式（纯色棋子、六角星布局）
├── configs/                 # 配置文件
│   ├── board.json           # 棋盘定义（121位置 + 邻接表）
│   ├── board_state.json     # 棋盘状态（20棋子位置）
│   ├── pieces_red.json      # 红方棋子规则
│   ├── pieces_black.json    # 黑方棋子规则
│   ├── rules.json           # 游戏规则（胜利条件、AI难度）
│   ├── ui_config.json       # 界面配置（颜色主题）
│   ├── token_stats.json     # Token消耗统计
│   └── initial/             # 初始备份
└── README.md                # 本文档
```

## 快速开始

### 环境要求

- Python 3.8+
- pip 包管理器

### 安装依赖

```bash
pip install fastapi uvicorn httpx pydantic jsonschema jsonpatch
```

### 启动服务

```bash
cd checkers
python main.py
```

服务启动后访问: **http://localhost:8004/**

### 设置AI API密钥

首次使用需要在界面左侧面板设置LLM API密钥（支持OpenAI兼容接口）：
- 默认模型: `deepseek-flash`（统一取自 `config.json`）
- 默认接口: `https://api.deepseek.com/v1`

## API端点

### 游戏操作

| 端点 | 方法 | 说明 |
|------|------|------|
| `/` | GET | 返回前端页面 |
| `/api/move` | POST | 玩家走棋 `{piece_id, to: [row, col]}` |
| `/api/ai_move` | POST | AI走棋 |
| `/api/valid_moves` | POST | 获取棋子合法移动 `{piece_id}` |
| `/api/undo` | POST | 悔棋（回退玩家+AI各一步） |
| `/api/restart` | POST | 重新开始游戏 |
| `/api/difficulty` | POST | 设置AI难度 `{difficulty: easy/medium/hard}` |

### 配置与AI

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/config/all` | GET | 获取所有配置 |
| `/api/config/{name}` | GET | 获取指定配置 |
| `/api/command` | POST | 自然语言指令（AI修改规则） |
| `/api/undo_config` | POST | 撤回上次AI配置修改 |
| `/api/apikey` | POST | 设置API密钥 |
| `/api/token_stats` | GET | Token消耗统计 |
| `/api/logs` | GET | AI对话日志 |

## AI修改机制

### 自然语言指令

在界面左侧面板输入自然语言指令，AI会解析意图并修改游戏配置：

- **修改规则**: "让所有棋子可以斜着走"
- **变换棋子**: "把红方第一个棋子变成超级跳棋"
- **修改棋盘**: "把棋盘背景换成蓝色"
- **添加机制**: "让黑方跳过下一回合"

### 配置修改类型

AI修改通过 **JSON Patch (RFC 6902)** 实现，支持以下操作类型：

| 操作 | 说明 |
|------|------|
| **rule** | 修改游戏规则（胜利条件、难度等） |
| **transform** | 变换棋子类型（跳棋通常只有一种棋子） |
| **create** | 创建自定义棋子 |
| **board** | 修改棋盘外观（颜色、布局） |
| **mechanism** | 添加游戏机制（跳过回合/AI接管/随机走棋） |

### 移动原语

跳棋使用两种移动原语，AI可以通过修改 `pieces_red.json` / `pieces_black.json` 自定义棋子能力：

#### step（单步移动）

```json
{
  "kind": "step",
  "land": "empty",
  "sym": "hex6"
}
```

- `land`: `empty`（仅落到空位）或 `any`（可落到任何位置）
- `sym`: `hex6`（六向展开）或 `none`（不展开）

#### hop（跳跃移动）

```json
{
  "kind": "hop",
  "land": "empty",
  "chain": true,
  "sym": "hex6"
}
```

- `chain`: `true`（可连续跳跃）或 `false`（单次跳跃）
- 跳过的棋子不被吃掉，仍保留在原位

## 前端界面

### 操作方式

1. **点击棋子**: 选中棋子，显示合法移动位置（绿色圆点）
2. **点击目标位置**: 移动棋子
3. **点击空白处**: 取消选中
4. **AI走棋按钮**: 触发AI思考并走棋
5. **悔棋按钮**: 回退上一步（玩家+AI）
6. **重新开始**: 重置棋盘到初始状态

### 侧边栏面板

- **游戏状态**: 显示当前回合、游戏结果
- **机制状态**: 显示激活的特殊机制（跳过回合/AI接管等）
- **AI对话**: 输入自然语言指令修改规则
- **日志**: 查看AI对话历史
- **设置**: 配置API密钥和难度

## 技术架构

### 前端

- 纯HTML/CSS/JavaScript，无框架依赖
- SVG渲染六角星棋盘
- 纯色圆形棋子（无文字）
- 极简黑白线条风格设计

### 后端

- **FastAPI** 异步Web框架
- **RuleEngine**: 基于邻接表的规则计算引擎
- **ChessAI**: Minimax + Alpha-Beta剪枝 + 推进度评估函数
- **AIOrchestrator**: LLM意图解析 + JSON Patch生成
- **MechanismEngine**: 动态机制管理（跳过回合/随机走棋等）

### AI引擎

- 基于大语言模型（DeepSeek/OpenAI兼容）
- 推进度评估：棋子距离对方营区越近分值越高
- 支持自定义棋子价值动态评估
- 三级难度：easy（深度2）/ medium（深度3）/ hard（深度4）

## 配置说明

### board.json

棋盘几何定义，包含121个位置、邻接表、营区定义。由 `generate_board.py` 自动生成，不建议手动修改。

### pieces_red.json / pieces_black.json

棋子规则定义。标准棋子只有一种 `piece` 类型，包含 `step` 和 `hop` 两种移动方式。可通过AI指令添加 `custom_pieces` 自定义棋子。

### rules.json

游戏规则配置：

```json
{
  "win_conditions": {
    "all_in_camp": {
      "description": "全部棋子进入对方营区获胜",
      "priority": 1
    },
    "stalemate": {
      "description": "困毙判负",
      "priority": 2
    }
  },
  "ai_difficulty": {
    "current": "medium",
    "levels": {
      "easy": {"depth": 2, "randomness": 0.3},
      "medium": {"depth": 3, "randomness": 0.1},
      "hard": {"depth": 4, "randomness": 0.0}
    }
  }
}
```

### ui_config.json

界面主题配置，包含颜色、字体、布局参数等。

## 开发指南

### 添加新移动原语

1. 在 `rule_engine.py` 的 `_execute_move_def` 中添加新 kind 分支
2. 实现对应的 `_xxx_moves` 方法
3. 更新 `prompts.py` 中的 `PIECE_PRIMITIVE_PRIMER`
4. 更新 `ai_orchestrator.py` 中的类型校验逻辑

### 修改棋盘生成

编辑 `generate_board.py`，修改 `ROW_COUNTS` 或 `ADJ_OFFSETS`，然后运行：

```bash
python generate_board.py
```

## 许可证

MIT License

---

**无限制跳棋** - 重新定义跳棋游戏的AI体验

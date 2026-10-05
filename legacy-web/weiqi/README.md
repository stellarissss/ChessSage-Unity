# 无限制围棋 (Unlimited Go)

> 基于无限制象棋架构开发的AI驱动围棋游戏，支持通过自然语言指令自由修改规则和游戏机制。

## 🎮 项目简介

无限制围棋是一个融合多层AI的围棋游戏项目，基于"元引擎"架构设计。玩家不仅可以体验标准围棋玩法，还能通过自然语言指令实时修改游戏规则，实现无限可能的围棋变体。

### 核心特性

- **标准围棋规则**：完整实现围棋核心规则（气、提子、打劫、自杀、五连获胜）
- **AI自由修改规则**：通过自然语言指令实时修改游戏规则和机制
- **机制原语系统**：支持跳过回合、AI接管、随机走棋、额外回合等机制组合
- **多层AI架构**：意图解析AI + 代码生成AI + 游戏AI的三层架构
- **元引擎设计**：基于配置驱动的架构，易于扩展到其他棋类游戏

## 🏗️ 架构设计

```
┌─────────────────────────────────────────────────────────────┐
│        前端界面 (Web Component `<go-board>` + Shadow DOM)     │
│   - 9x9棋盘渲染          - 黑白棋子显示          - AI对话   │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                   FastAPI 后端服务                           │
│   - /api/move        玩家落子                               │
│   - /api/ai_move     AI落子                                 │
│   - /api/command     AI指令处理                             │
│   - /api/config/all  获取所有配置                           │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                      核心引擎层                              │
│   ┌─────────────┐  ┌─────────────┐  ┌─────────────┐        │
│   │ rule_engine │  │ chess_ai    │  │mechanism_   │        │
│   │ 规则引擎    │  │ 游戏AI      │  │engine       │        │
│   │ (围棋规则)  │  │ (Minimax)   │  │(机制原语)   │        │
│   └─────────────┘  └─────────────┘  └─────────────┘        │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                      AI编排层                                │
│   ┌─────────────────────────────────────────────────┐       │
│   │              ai_orchestrator.py                  │       │
│   │  - 意图解析AI (Intent Parser)                    │       │
│   │  - 规则修改AI (Rule Modifier)                    │       │
│   │  - 棋盘变换AI (Board Transformer)                │       │
│   │  - UI修改AI (UI Modifier)                       │       │
│   │  - 趣味响应AI (Fun Response)                    │       │
│   └─────────────────────────────────────────────────┘       │
└─────────────────────────────────────────────────────────────┘
```

## 📁 项目结构

```
go/
├── configs/                    # 配置文件目录
│   ├── initial/                # 初始配置备份
│   │   ├── board.json.initial
│   │   ├── board_state.json.initial
│   │   ├── pieces_black.json.initial
│   │   ├── pieces_red.json.initial
│   │   ├── rules.json.initial
│   │   └── ui_config.json.initial
│   ├── board.json              # 棋盘几何与外观配置
│   ├── board_state.json        # 棋盘运行时状态
│   ├── pieces_black.json       # 黑方棋子定义
│   ├── pieces_red.json         # 白方棋子定义
│   ├── rules.json              # 游戏规则配置
│   └── ui_config.json          # UI配置
├── static/                     # 前端静态文件
│   ├── index.html              # 主页面
│   ├── style.css               # 样式文件
│   └── app.js                  # 前端逻辑
├── ai_orchestrator.py          # AI编排器（复用）
├── mechanism_engine.py         # 机制引擎（复用）
├── chess_ai.py                 # 围棋AI引擎
├── rule_engine.py              # 围棋规则引擎
├── main.py                     # FastAPI入口
├── prompts.py                  # AI提示词定义
└── requirements.txt            # Python依赖
```

## 🚀 快速开始

### 环境要求

- Python 3.9+
- Node.js（仅用于开发，生产环境可直接通过FastAPI提供静态文件）

### 安装依赖

```bash
cd go
pip install -r requirements.txt
```

### 启动服务

```bash
uvicorn main:app --host 0.0.0.0 --port 8002 --reload
```

访问 http://localhost:8002 即可开始游戏。

> 前端已重构为 Web Component（`<go-board>`），通过 ES Module 导出，可被 RPG 外壳动态 `import()` 挂载，也可在 standalone 模式下直接访问。

## 🎯 游戏规则

### 标准规则

1. **落子**：黑方先行，双方轮流在棋盘交叉点上落子
2. **气**：棋子的气是指与其直接相邻的空交叉点
3. **提子**：当一方棋子的气全部被堵住时，该棋子被提走
4. **打劫**：禁止立即提回刚刚被提走的子（Ko规则）
5. **自杀**：禁止落子后自身无气的着法
6. **五连获胜**：在横、竖、斜方向连成五个棋子即获胜

### 可选规则

- **黑棋禁手**：禁止黑棋三三、四四、长连（默认关闭）

## 🔧 AI指令示例

通过底部输入框输入自然语言指令，AI会实时修改游戏规则：

```
禁用打劫规则
启用黑棋禁手
让AI激进一点
给我额外一回合
让白方跳过一回合
```

### 支持的机制原语

| 机制 | 说明 | 示例指令 |
|------|------|----------|
| skip_turns | 跳过指定方回合 | "让白方跳过一回合" |
| ai_control | AI接管指定方 | "让AI控制黑方" |
| random_moves | 随机走棋 | "让AI随机走两步" |
| extra_turns | 额外回合 | "给我额外一回合" |
| move_limits | 移动限制 | "限制黑方只能在中央落子" |

## 📝 API接口

### 落子

```
POST /api/move
Content-Type: application/json

{
  "to": [9, 9]
}
```

### AI落子

```
POST /api/ai_move
Content-Type: application/json

{}
```

### 执行命令

```
POST /api/command
Content-Type: application/json

{
  "message": "禁用打劫规则"
}
```

### 获取配置

```
GET /api/config/all
```

### 悔棋

```
POST /api/undo
```

### 重新开始

```
POST /api/restart
```

## 🧠 AI架构

### 层级设计

1. **第一级：意图解析AI**
   - 将自然语言转化为结构化JSON指令
   - 分类：机制修改、规则修改、界面修改、趣味操作

2. **第二级：代码生成AI**
   - 根据结构化指令生成Python代码
   - 通过JSON Patch修改配置文件

3. **第三级：游戏AI**
   - Minimax算法 + Alpha-Beta剪枝
   - 启发式评估：位置价值、气数、连通性、领地控制

### 提示词设计

提示词系统分为多个模块：
- `INTENT_PARSER_SYSTEM`：意图解析系统提示词
- `RULE_MODIFIER_SYSTEM`：规则修改系统提示词
- `BOARD_TRANSFORMER_SYSTEM`：棋盘变换系统提示词
- `UI_MODIFIER_SYSTEM`：UI修改系统提示词
- `FUN_RESPONSE_SYSTEM`：趣味响应系统提示词
- `MECHANISM_MODIFIER_SYSTEM`：机制修改系统提示词

## 🎨 前端设计

### 视觉风格

- **棋盘**：传统木纹风格，19x19网格，9个星位点
- **棋子**：黑白圆形棋子，带光影效果
- **界面**：编辑极简主义风格，Paper主题配色

### 响应式设计

- 桌面端：棋盘 + 侧边面板布局
- 平板端：棋盘在上，面板在下
- 移动端：面板折叠，按需展开

## 🤝 贡献指南

欢迎贡献代码！请遵循以下规范：

1. 代码风格：PEP 8（Python）、ESLint（JavaScript）
2. 提交信息：使用描述性的提交信息
3. 分支策略：基于main分支创建feature分支
4. 测试：确保新增功能有相应测试

## 📄 许可证

MIT License

## 🙏 致谢

- 感谢无限制象棋项目提供的架构基础
- 感谢DeepSeek提供的AI模型支持
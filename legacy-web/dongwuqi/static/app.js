class DongwuqiBoard extends HTMLElement {
    static get observedAttributes() {
        return ['api-base', 'rpg-mode', 'player-side'];
    }

    constructor() {
        super();
        this.configs = {};
        this.boardState = null;
        this.uiConfig = null;
        this.selectedPiece = null;
        this.validMoves = [];
        this.lastMove = null;
        this.aiThinking = false;
        this.coordInsertMode = false;
        this.selectMode = 'coord';
        this.regionPoints = [];
        this.coordDots = [];
        this.thinkingPollInterval = null;
        this._regionRectEl = null;
        this._keydownHandler = null;

        this._personalityInfo = {
            normal:     { icon: '🧠', name: '标准型', subtitle: 'Normal',    desc: '攻守平衡的标准AI',          agg: 0.5, def: 0.5 },
            aggressive: { icon: '⚔️', name: '激进型', subtitle: 'Aggressive', desc: '进攻至上，全力出击',        agg: 0.9, def: 0.2 },
            defensive:  { icon: '🛡️', name: '保守型', subtitle: 'Defensive',  desc: '稳扎稳打，防守反击',        agg: 0.2, def: 0.9 },
            random:     { icon: '🎲', name: '随机型', subtitle: 'Random',     desc: '天马行空，随心所欲',        agg: 0.5, def: 0.5 },
            custom:     { icon: '✨', name: '自定义', subtitle: 'Custom',     desc: '独一无二的神秘风格',        agg: 0.5, def: 0.5 },
        };

        this._mechanismMeta = {
            skip_turns:     { icon: '⏸️',  label: '冻结',     type: 'skip',   unit: '回合' },
            ai_control:     { icon: '🤖',  label: 'AI接管',   type: 'ai',     unit: '回合' },
            player_control: { icon: '🎮',  label: '玩家控制', type: 'player', unit: '' },
            random_moves:   { icon: '🎲',  label: '随机走棋', type: 'random', unit: '步'   },
            extra_turns:    { icon: '⚡',  label: '额外回合', type: 'extra',  unit: '回合' },
            move_limits:    { icon: '🚶',  label: '多步行走', type: 'limit',  unit: '步'   },
        };
    }

    get apiBase() {
        return this.getAttribute('api-base') || '';
    }

    get rpgMode() {
        return this.hasAttribute('rpg-mode');
    }

    get playerSide() {
        return this.getAttribute('player-side') || 'red';
    }

    attributeChangedCallback(name, oldVal, newVal) {
        if (name === 'rpg-mode') {
            this._updateRpgMode();
        }
    }

    _updateRpgMode() {
        const host = this.shadowRoot?.host;
        if (!host) return;
        const sidePanel = this.shadowRoot.querySelector('.side-panel');
        const inputSection = this.shadowRoot.querySelector('.input-section');
        if (this.rpgMode) {
            if (sidePanel) sidePanel.style.display = 'none';
            if (inputSection) inputSection.style.display = 'none';
        } else {
            if (sidePanel) sidePanel.style.display = '';
            if (inputSection) inputSection.style.display = '';
        }
    }

    connectedCallback() {
        this.attachShadow({ mode: 'open' });
        this._renderShadowDom();
        this._initTicTacToe();
        this._updateRpgMode();
    }

    disconnectedCallback() {
        this.destroy();
    }

    _renderShadowDom() {
        const style = document.createElement('style');
        style.textContent = this._getStyles();
        this.shadowRoot.appendChild(style);

        const container = document.createElement('div');
        container.id = 'app';
        container.innerHTML = this._getHtmlTemplate();
        this.shadowRoot.appendChild(container);
    }

    _getHtmlTemplate() {
        return `
        <div id="thinking-overlay" class="thinking-overlay">
            <div class="thinking-content">
                <div class="thinking-loading">
                    <div class="thinking-spinner"></div>
                    <div class="thinking-text" id="thinking-text">ChatAI 正在理解您的意图...</div>
                    <div class="thinking-stage" id="thinking-stage">阶段: 意图解析</div>
                </div>
                <div class="ttt-wrap">
                    <div class="ttt-title">井字棋 · 消遣一局（不保存）</div>
                    <div class="ttt-board" id="ttt-board">
                        <div class="ttt-cell" data-idx="0"></div>
                        <div class="ttt-cell" data-idx="1"></div>
                        <div class="ttt-cell" data-idx="2"></div>
                        <div class="ttt-cell" data-idx="3"></div>
                        <div class="ttt-cell" data-idx="4"></div>
                        <div class="ttt-cell" data-idx="5"></div>
                        <div class="ttt-cell" data-idx="6"></div>
                        <div class="ttt-cell" data-idx="7"></div>
                        <div class="ttt-cell" data-idx="8"></div>
                    </div>
                    <div class="ttt-status" id="ttt-status">你执 X · 随机先手</div>
                    <button type="button" class="ttt-restart" id="ttt-restart">重新开始</button>
                </div>
            </div>
        </div>

        <div id="settings-modal" class="modal">
            <div class="modal-content">
                <h3>设 置</h3>
                <div class="form-group">
                    <label>DeepSeek API Key</label>
                    <input type="password" id="api-key-input" placeholder="输入您的API密钥">
                    <small>您的API密钥仅保存在本地，不会发送到我们的服务器</small>
                </div>
                <div class="form-group">
                    <label>AI难度</label>
                    <select id="difficulty-select">
                        <option value="easy">简单</option>
                        <option value="medium" selected>中等</option>
                        <option value="hard">困难</option>
                    </select>
                </div>
                <div class="form-group">
                    <button id="btn-achievements" class="btn" style="width:100%;padding:12px;font-size:14px;">🏆 查看成就</button>
                </div>
                <div class="modal-buttons">
                    <button id="close-settings" class="btn">取消</button>
                    <button id="save-settings" class="btn-primary">保存</button>
                </div>
            </div>
        </div>

        <div id="logs-modal" class="modal">
            <div class="modal-content logs-content">
                <h3>AI 对话日志</h3>
                <div id="logs-container" class="logs-container"></div>
                <div class="modal-buttons">
                    <button id="btn-clear-logs" class="btn danger">清空日志</button>
                    <button id="btn-refresh-logs" class="btn">刷新</button>
                    <button id="close-logs" class="btn-primary">关闭</button>
                </div>
            </div>
        </div>

        <header class="header">
            <h1>无限制动物棋</h1>
            <div class="header-actions">
                <span id="turn-indicator">红方回合</span>
                <button id="btn-settings" class="btn-icon" title="设置">⚙</button>
            </div>
        </header>

        <div id="samsara-bar" class="samsara-bar">
            <div id="level-info-bar" class="level-info-bar"></div>
            <div class="samsara-item karma-item">
                <span class="samsara-icon">☯</span>
                <div class="samsara-info">
                    <span class="samsara-label">业力</span>
                    <div class="samsara-bar-container">
                        <div class="samsara-bar-fill karma-fill" id="karma-fill"></div>
                    </div>
                    <span class="samsara-value" id="karma-value">50/120</span>
                </div>
            </div>
            <div class="samsara-item detection-item">
                <span class="samsara-icon">👁️</span>
                <div class="samsara-info">
                    <span class="samsara-label">识破</span>
                    <div class="samsara-bar-container">
                        <div class="samsara-bar-fill detection-fill" id="detection-fill"></div>
                    </div>
                    <span class="samsara-value" id="detection-value">0%</span>
                </div>
            </div>
            <div class="samsara-item turn-item">
                <span class="samsara-icon">⏱️</span>
                <div class="samsara-info">
                    <span class="samsara-label">回合</span>
                    <div class="samsara-bar-container">
                        <div class="samsara-bar-fill turn-fill" id="turn-fill"></div>
                    </div>
                    <span class="samsara-value" id="turn-value">0/20</span>
                </div>
            </div>
            <div class="samsara-item objective-item">
                <span class="samsara-icon">🎯</span>
                <div class="samsara-info">
                    <span class="samsara-label" id="objective-label">目标</span>
                    <span class="samsara-value objective-text" id="objective-text">吃掉对方鼠</span>
                </div>
            </div>
        </div>

        <main class="main">
            <div class="board-section">
                <div id="board-container"></div>
            </div>

            <aside class="side-panel">
                <div class="panel-section">
                    <h3 data-num="01">AI 助手</h3>
                    <div id="ai-messages" class="messages"></div>
                </div>

                <div class="panel-section">
                    <h3 data-num="02">游戏目标</h3>
                    <div id="game-objectives" class="objectives-list">
                        <span class="empty">加载中...</span>
                    </div>
                </div>

                <div class="panel-section">
                    <h3 data-num="03">Token 消耗</h3>
                    <div id="token-stats" class="token-stats">
                        <div class="token-stat">
                            <span class="stat-label">总消耗</span>
                            <span class="stat-value" id="token-total">0</span>
                        </div>
                        <div class="token-stat">
                            <span class="stat-label">今日消耗</span>
                            <span class="stat-value" id="token-today">0</span>
                        </div>
                        <div class="token-stat">
                            <span class="stat-label">调用次数</span>
                            <span class="stat-value" id="token-calls">0</span>
                        </div>
                        <div class="token-stat cost">
                            <span class="stat-label">估算费用</span>
                            <span class="stat-value" id="token-cost">$0.00</span>
                        </div>
                    </div>
                </div>

                <div class="panel-section">
                    <h3 data-num="04">AI 性格</h3>
                    <div id="ai-personality" class="personality-card">
                        <div class="personality-header">
                            <span class="personality-icon" id="personality-icon">🧠</span>
                            <div class="personality-title">
                                <span class="personality-type" id="personality-type">标准型</span>
                                <span class="personality-subtitle" id="personality-subtitle">Normal</span>
                            </div>
                        </div>
                        <p class="personality-desc" id="personality-desc">攻守平衡的标准AI</p>
                        <div class="personality-bars">
                            <div class="personality-bar">
                                <span class="bar-label">进攻</span>
                                <span class="bar-chars aggressive" id="bar-agg">█████░░░░░</span>
                                <span class="bar-percent" id="bar-agg-pct">50%</span>
                            </div>
                            <div class="personality-bar">
                                <span class="bar-label">防守</span>
                                <span class="bar-chars defensive" id="bar-def">█████░░░░░</span>
                                <span class="bar-percent" id="bar-def-pct">50%</span>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="panel-section">
                    <h3 data-num="05">游戏机制</h3>
                    <div id="active-mechanisms" class="mechanisms-list">
                        <span class="empty">无激活机制</span>
                    </div>
                </div>

                <div class="panel-section">
                    <h3 data-num="06">已激活规则</h3>
                    <div id="active-rules" class="rules-list">
                        <span class="empty">暂无自定义规则</span>
                    </div>
                </div>

                <div class="panel-section controls">
                    <h3 data-num="07">操 作</h3>
                    <button id="btn-undo" class="btn">悔 棋</button>
                    <button id="btn-undo-config" class="btn">撤回AI修改</button>
                    <button id="btn-restart" class="btn">重新开始</button>
                    <button id="btn-logs" class="btn">查看日志</button>
                    <button id="btn-reset-configs" class="btn danger">重置所有配置</button>
                </div>
            </aside>
        </main>

        <footer class="input-section">
            <div class="input-wrapper">
                <input
                    type="text"
                    id="command-input"
                    placeholder="输入指令，例如：让我的马可以斜着走、悔一步棋..."
                    autocomplete="off"
                >
                <div class="coord-select-btn-wrapper">
                    <button id="btn-insert-coord" class="btn" title="点击后在棋盘上选择格子，自动插入坐标到输入框">📍 选坐标</button>
                    <button id="btn-toggle-coord-mode" class="btn-toggle-mode" title="切换选坐标/选区域">⇄</button>
                </div>
                <button id="btn-send" class="btn-primary">发送</button>
            </div>
            <div class="hints">
                试试："让老鼠能跳河"、"狮子可以斜着走"、"陷阱不降级了"、"把棋盘掀了"
            </div>
        </footer>
        `;
    }

    _getStyles() {
        return `
@font-face {
    font-family: 'Resource Han Rounded CN';
    src: url('../shared/assets/fonts/resource-han-rounded-cn.woff2');
    src: url('../shared/assets/fonts/resource-han-rounded-cn.woff2') format('woff2'),
         url('../shared/assets/fonts/resource-han-rounded-cn.woff2') format('woff2'),
         url('../shared/assets/fonts/resource-han-rounded-cn.woff2') format('woff2'),
         url('../shared/assets/fonts/resource-han-rounded-cn.woff2') format('woff2'),
         url('../shared/assets/fonts/resource-han-rounded-cn.woff2') format('woff2');
    font-weight: normal;
    font-style: normal;
    font-display: swap;
    unicode-range: U+3000-303F, U+3040-309F, U+30A0-30FF, U+3400-4DBF, U+4E00-9FFF, U+F900-FAFF, U+FF00-FFEF;
}

:host {
    --paper: #0d2818;
    --paper-warm: #16402a;
    --paper-dark: #0a1f12;
    --ink: #d4a83a;
    --ink-soft: #c99a32;
    --ink-medium: #a6822a;
    --ink-light: #8b6914;
    --ink-faint: #5c4a15;
    --line: #1f5a38;
    --line-strong: #2d7a4a;

    --board-bg: #5c3d2e;
    --board-line: #d4a83a;
    --red-piece: #c41e3a;
    --black-piece: #1a1a1a;

    --neon-cyan: #00ff88;
    --neon-magenta: #ff44aa;
    --neon-pink: #ff6b8a;
    --neon-green: #39ff14;
    --neon-gold: #ffd700;
    --jungle-glow: #22ff66;

    --highlight: #d4a83a;
    --valid-move: #2d6a4f;
    --last-move: #8b5a2b;
    --danger: #c41e3a;
    --success: #228b45;
    --warning: #d4a83a;
    --text-light: #8b7355;

    --muted-ink: #5c4a3a;
    --accent-green: #2d6a4f;

    --bark-dark: #3d2817;
    --bark-light: #6b4423;
    --leather: #5c3d2e;

    display: block;
    width: 100%;
    height: 100%;
}

* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}

#app {
    display: flex;
    flex-direction: column;
    height: 100%;
    width: 100%;
    position: relative;
    font-family: 'DM Sans', -apple-system, BlinkMacSystemFont, 'Resource Han Rounded CN', 'PingFang SC', 'Microsoft YaHei', sans-serif;
    color: var(--ink);
    background-color: var(--paper);
    background-image:
        url("data:image/svg+xml,%3Csvg viewBox='0 0 400 400' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='noiseFilter'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.8' numOctaves='5' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23noiseFilter)'/%3E%3C/svg%3E"),
        radial-gradient(ellipse at 50% 0%, rgba(22, 64, 42, 0.6) 0%, transparent 50%),
        radial-gradient(ellipse at 0% 50%, rgba(13, 40, 24, 0.8) 0%, transparent 50%);
    background-repeat: repeat;
    background-size: 150px 150px, 100% 50%, 50% 100%;
    background-blend-mode: multiply, normal, normal;
    overflow: hidden;
}

#app::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background-image:
        url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='60' height='60' viewBox='0 0 60 60'%3E%3Cpath d='M30 0L35 20L55 25L40 40L45 60L30 50L15 60L20 40L5 25L25 20Z' fill='%2316402a' fill-opacity='0.1'/%3E%3C/svg%3E");
    pointer-events: none;
    z-index: 0;
}

/* Header */
.header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 24px;
    background: linear-gradient(180deg, #16402a 0%, #0d2818 100%);
    border-bottom: 2px solid #d4a83a;
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.1s both;
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
}

.header::after {
    content: '';
    position: absolute;
    left: 24px; right: 24px; bottom: -2px;
    height: 2px;
    background: linear-gradient(90deg, transparent, #d4a83a, transparent);
    transform: scaleX(0);
    transform-origin: left;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.4s forwards;
}

.header h1 {
    font-family: 'Playfair Display', Georgia, 'Resource Han Rounded CN', 'PingFang SC', serif;
    font-weight: 500;
    font-size: 1.5rem;
    letter-spacing: 0.05em;
    color: var(--ink);
    font-style: italic;
    text-shadow: 0 0 10px rgba(212, 168, 58, 0.3);
}

.header h1::before {
    content: '畜生道';
    display: block;
    font-family: 'Playfair Display', serif;
    font-size: 0.65rem;
    font-weight: 400;
    letter-spacing: 0.4em;
    color: #8b6914;
    text-transform: uppercase;
    margin-bottom: 2px;
    font-style: normal;
}

.header-actions {
    display: flex;
    align-items: center;
    gap: 18px;
}

#turn-indicator {
    padding: 8px 20px;
    background: linear-gradient(135deg, #3d2817 0%, #2a1a10 100%);
    color: #d4a83a;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    font-weight: 500;
    letter-spacing: 0.15em;
    text-transform: uppercase;
    border: 2px solid #d4a83a;
    position: relative;
    overflow: hidden;
    transition: all 0.3s cubic-bezier(0.22, 1, 0.36, 1);
    border-radius: 4px;
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
}

#turn-indicator::before {
    content: '';
    position: absolute;
    top: 0; left: -100%;
    width: 100%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(212, 168, 58, 0.2), transparent);
    transition: left 0.5s ease;
}

#turn-indicator:hover::before {
    left: 100%;
}

#turn-indicator:hover {
    box-shadow: 0 0 15px rgba(212, 168, 58, 0.4), 0 2px 8px rgba(0, 0, 0, 0.3);
}

.samsara-bar {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 16px;
    padding: 6px 24px;
    background: linear-gradient(135deg, #0d2818 0%, #16402a 50%, #0d2818 100%);
    border-bottom: 2px solid #d4a83a;
    box-shadow: 0 4px 20px rgba(212, 168, 58, 0.2);
}

.level-info-bar {
    flex: 0 0 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    padding: 2px 0;
    margin-bottom: 2px;
    font-size: 0.8rem;
    color: #d4a83a;
    border-bottom: 1px solid rgba(212, 168, 58, 0.2);
}

.level-info-bar .level-realm {
    color: rgba(212, 168, 58, 0.7);
    font-size: 0.75rem;
}

.level-info-bar .level-name {
    font-weight: 600;
    text-shadow: 0 0 6px rgba(212, 168, 58, 0.4);
}

.level-info-bar .level-type-badge {
    display: inline-block;
    padding: 1px 8px;
    font-size: 0.65rem;
    background: rgba(212, 168, 58, 0.15);
    border: 1px solid rgba(212, 168, 58, 0.4);
    border-radius: 10px;
    color: #d4a83a;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.samsara-item {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 12px;
    background: rgba(61, 40, 23, 0.6);
    border-radius: 8px;
    border: 1px solid rgba(212, 168, 58, 0.3);
    transition: all 0.3s ease;
}

.samsara-item:hover {
    background: rgba(61, 40, 23, 0.8);
    border-color: rgba(212, 168, 58, 0.5);
    box-shadow: 0 0 10px rgba(212, 168, 58, 0.2);
}

.samsara-icon {
    font-size: 1.2rem;
    filter: drop-shadow(0 0 4px rgba(212, 168, 58, 0.3));
}

.samsara-info {
    display: flex;
    flex-direction: column;
    gap: 4px;
}

.samsara-label {
    font-size: 0.65rem;
    color: rgba(212, 168, 58, 0.7);
    text-transform: uppercase;
    letter-spacing: 0.1em;
}

.samsara-bar-container {
    width: 80px;
    height: 6px;
    background: rgba(61, 40, 23, 0.8);
    border-radius: 3px;
    overflow: hidden;
    border: 1px solid rgba(212, 168, 58, 0.3);
}

.samsara-bar-fill {
    height: 100%;
    border-radius: 3px;
    transition: width 0.3s ease;
}

.karma-fill {
    background: linear-gradient(90deg, #22c55e, #16a34a);
    box-shadow: 0 0 8px rgba(34, 197, 94, 0.5);
}

.detection-fill {
    background: linear-gradient(90deg, #d4a83a, #f97316, #c41e3a);
    box-shadow: 0 0 8px rgba(212, 168, 58, 0.5);
}

.turn-fill {
    background: linear-gradient(90deg, #00ff88, #00cc6a);
    box-shadow: 0 0 8px rgba(0, 255, 136, 0.5);
}

.samsara-value {
    font-size: 0.75rem;
    font-weight: 600;
    color: #d4a83a;
    font-family: 'JetBrains Mono', monospace;
    text-shadow: 0 0 6px rgba(212, 168, 58, 0.4);
}

.objective-text {
    font-family: inherit;
    font-weight: 500;
}

.btn-icon {
    padding: 8px 12px;
    border: none;
    background: transparent;
    color: var(--ink);
    cursor: pointer;
    font-size: 1.1rem;
    font-family: 'JetBrains Mono', monospace;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
}

.btn-icon::after {
    content: '';
    position: absolute;
    bottom: 4px;
    left: 50%;
    transform: translateX(-50%) scaleX(0);
    width: 60%;
    height: 1px;
    background: var(--ink);
    transition: transform 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.btn-icon:hover::after {
    transform: translateX(-50%) scaleX(1);
}

.btn-icon:hover {
    color: var(--ink-soft);
}

/* Main */
.main {
    display: flex;
    flex: 1;
    overflow: hidden;
    padding: 12px 24px;
    gap: 24px;
}

.board-section {
    flex: 1;
    display: flex;
    justify-content: center;
    align-items: center;
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.2s both;
}

#board-container {
    position: relative;
    width: calc(min(90vmin, 560px, calc((100vh - 180px) * 7 / 9)) * 0.75);
    height: calc(min(90vmin, 560px, calc((100vh - 180px) * 7 / 9)) * 0.75 * 9 / 7);
    background: linear-gradient(135deg, #5c3d2e 0%, #4a3022 50%, #5c3d2e 100%);
    border-radius: 8px;
    box-shadow:
        0 0 0 3px #d4a83a,
        0 0 0 6px #3d2817,
        0 8px 32px rgba(0, 0, 0, 0.5),
        0 0 40px rgba(212, 168, 58, 0.1);
    background-image:
        url("data:image/svg+xml,%3Csvg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='bark'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.04' numOctaves='5'/%3E%3CfeDiffuseLighting in='noise' lighting-color='%236b4423' surfaceScale='2'%3E%3CfeDistantLight azimuth='45' elevation='60'/%3E%3C/feDiffuseLighting%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23bark)'/%3E%3C/svg%3E"),
        repeating-linear-gradient(
            90deg,
            transparent,
            transparent 30px,
            rgba(212, 168, 58, 0.03) 30px,
            rgba(212, 168, 58, 0.03) 31px
        ),
        repeating-linear-gradient(
            0deg,
            transparent,
            transparent 30px,
            rgba(212, 168, 58, 0.02) 30px,
            rgba(212, 168, 58, 0.02) 31px
        );
    background-blend-mode: overlay, normal, normal;
}

#board-container::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: radial-gradient(ellipse at 30% 30%, rgba(255, 255, 255, 0.05) 0%, transparent 50%),
                radial-gradient(ellipse at 70% 70%, rgba(0, 0, 0, 0.3) 0%, transparent 50%);
    border-radius: 8px;
    pointer-events: none;
}

/* Side Panel */
.side-panel {
    width: 300px;
    display: flex;
    flex-direction: column;
    gap: 20px;
    overflow-y: auto;
    padding-right: 4px;
}

.side-panel::-webkit-scrollbar { width: 4px; }
.side-panel::-webkit-scrollbar-track { background: transparent; }
.side-panel::-webkit-scrollbar-thumb {
    background: var(--line-strong);
    border-radius: 2px;
}

.panel-section {
    position: relative;
    background: linear-gradient(135deg, rgba(22, 64, 42, 0.8) 0%, rgba(13, 40, 24, 0.9) 100%);
    border: 1px solid rgba(212, 168, 58, 0.3);
    padding: 12px 16px;
    animation: fadeInUp 0.5s cubic-bezier(0.22, 1, 0.36, 1) both;
    border-radius: 4px;
    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.2);
}

.panel-section::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 24px; height: 24px;
    border-top: 2px solid #d4a83a;
    border-left: 2px solid #d4a83a;
    pointer-events: none;
}

.panel-section::after {
    content: '';
    position: absolute;
    bottom: 0; right: 0;
    width: 24px; height: 24px;
    border-bottom: 2px solid #d4a83a;
    border-right: 2px solid #d4a83a;
    pointer-events: none;
}

.panel-section:nth-child(1) { animation-delay: 0.25s; }
.panel-section:nth-child(2) { animation-delay: 0.32s; }
.panel-section:nth-child(3) { animation-delay: 0.39s; }
.panel-section:nth-child(4) { animation-delay: 0.46s; }
.panel-section:nth-child(5) { animation-delay: 0.53s; }

.panel-section h3 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1rem;
    margin-bottom: 14px;
    color: var(--ink);
    letter-spacing: 0.02em;
    padding-bottom: 10px;
    border-bottom: 1px solid var(--line);
    position: relative;
    font-style: italic;
}

.panel-section h3::before {
    content: attr(data-num);
    display: inline-block;
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.7rem;
    font-weight: 400;
    color: var(--ink-light);
    margin-right: 10px;
    font-style: normal;
    vertical-align: middle;
}

/* Messages */
.messages {
    max-height: 220px;
    overflow-y: auto;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.85rem;
    line-height: 1.6;
    padding-right: 6px;
}

.messages::-webkit-scrollbar { width: 4px; }
.messages::-webkit-scrollbar-track { background: transparent; }
.messages::-webkit-scrollbar-thumb { background: var(--line-strong); border-radius: 2px; }

.message {
    padding: 10px 0 10px 16px;
    margin-bottom: 4px;
    border-left: 2px solid var(--ink-faint);
    color: var(--ink-soft);
    position: relative;
    animation: slideInLeft 0.4s cubic-bezier(0.22, 1, 0.36, 1);
    transition: all 0.2s ease;
}

.message:hover {
    border-left-color: var(--ink);
    background: rgba(26, 26, 26, 0.02);
    padding-left: 20px;
}

.message.success {
    border-left-color: var(--success);
    color: var(--ink-soft);
}

.message.success:hover {
    background: rgba(45, 106, 79, 0.04);
}

.message.error {
    border-left-color: var(--danger);
    color: var(--ink-soft);
}

.message.error:hover {
    background: rgba(155, 44, 44, 0.04);
}

.message.fun {
    border-left-color: var(--ink);
    color: var(--ink);
    font-style: italic;
}

.message.fun:hover {
    background: rgba(26, 26, 26, 0.03);
}

/* Rules list */
.rules-list {
    font-size: 0.82rem;
    font-family: 'DM Sans', sans-serif;
}

.rules-list .empty {
    color: var(--ink-light);
    font-style: italic;
    font-size: 0.8rem;
    text-align: center;
    padding: 16px 0;
    border: 1px dashed var(--line);
}

.rule-item {
    padding: 8px 0 8px 28px;
    margin-bottom: 2px;
    border-bottom: 1px dotted var(--line);
    color: var(--ink-soft);
    font-size: 0.8rem;
    position: relative;
    transition: all 0.2s ease;
    counter-increment: rule-counter;
}

.rules-list {
    counter-reset: rule-counter;
}

.rule-item::before {
    content: counter(rule-counter, decimal-leading-zero);
    position: absolute;
    left: 0;
    top: 8px;
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.7rem;
    font-weight: 500;
    color: var(--ink-light);
    letter-spacing: 0.05em;
}

.rule-item:hover {
    color: var(--ink);
    border-bottom-color: var(--ink-faint);
}

.rule-item:hover::before {
    color: var(--ink);
}

/* Token stats */
.token-stats {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1px;
    background: var(--line);
    border: 1px solid var(--line);
}

.token-stat {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    padding: 12px 14px;
    background: var(--paper);
    transition: background 0.2s ease;
}

.token-stat:hover {
    background: var(--paper-warm);
}

.token-stat .stat-label {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: var(--ink-light);
    margin-bottom: 4px;
    font-weight: 500;
}

.token-stat .stat-value {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1.3rem;
    font-weight: 500;
    color: var(--ink);
    font-style: italic;
}

.token-stat.cost {
    grid-column: span 2;
    background: var(--ink);
    color: var(--paper);
}

.token-stat.cost:hover {
    background: var(--ink-soft);
}

.token-stat.cost .stat-label {
    color: rgba(250, 250, 248, 0.6);
}

.token-stat.cost .stat-value {
    color: var(--paper);
    font-size: 1.5rem;
}

/* Controls */
.controls {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

/* Board elements */
.board-grid {
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
}

.river-text {
    position: absolute;
    left: 50%;
    top: 50%;
    transform: translate(-50%, -50%);
    font-size: 1.2rem;
    color: var(--board-line);
    font-family: 'KaiTi', serif;
    letter-spacing: 0.5em;
    writing-mode: horizontal-tb;
    opacity: 0.55;
}

/* Pieces */
.piece {
    position: absolute;
    width: var(--piece-width, 10%);
    height: var(--piece-height, 8%);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: var(--piece-font-size, 1.2rem);
    font-weight: bold;
    font-family: 'Apple Color Emoji', 'Segoe UI Emoji', 'Noto Color Emoji', 'Twemoji Mozilla', sans-serif;
    cursor: pointer;
    user-select: none;
    transition: transform 0.2s, box-shadow 0.2s, filter 0.2s;
    z-index: 10;
    box-shadow: 
        0 3px 12px rgba(0, 0, 0, 0.5), 
        0 0 0 2px #d4a83a,
        inset 0 1px 2px rgba(255, 255, 255, 0.1);
    background: linear-gradient(135deg, #3d2817 0%, #2a1a10 100%);
}

.piece:hover {
    transform: translate(-50%, -50%) scale(1.08);
    filter: brightness(1.2);
    box-shadow: 
        0 4px 16px rgba(0, 0, 0, 0.6), 
        0 0 0 2px #d4a83a,
        0 0 20px rgba(212, 168, 58, 0.4),
        inset 0 1px 2px rgba(255, 255, 255, 0.1);
}

.piece.red {
    background: linear-gradient(135deg, #5c2a2a 0%, #3d1a1a 50%, #2a1010 100%);
    color: #ff6b6b;
    border: 3px solid #d4a83a;
    box-shadow:
        0 3px 12px rgba(0, 0, 0, 0.6),
        0 0 0 3px #d4a83a,
        0 0 12px rgba(212, 168, 58, 0.3),
        inset 0 1px 3px rgba(255, 107, 107, 0.3);
}

.piece.black {
    background: linear-gradient(135deg, #2a2a2a 0%, #1a1a1a 50%, #0a0a0a 100%);
    color: #a8a8a8;
    border: 3px solid #d4a83a;
    box-shadow:
        0 3px 12px rgba(0, 0, 0, 0.7),
        0 0 0 3px #d4a83a,
        0 0 12px rgba(212, 168, 58, 0.2),
        inset 0 1px 3px rgba(168, 168, 168, 0.2);
}

.piece.selected {
    box-shadow:
        0 0 0 4px #00ff88,
        0 0 20px rgba(0, 255, 136, 0.7),
        0 0 40px rgba(0, 255, 136, 0.4),
        0 4px 16px rgba(0, 0, 0, 0.7);
    z-index: 20;
    animation: eyeGlow 1.5s ease-in-out infinite;
}

.piece.last-moved {
    box-shadow:
        0 0 0 4px #ff6b8a,
        0 0 18px rgba(255, 107, 138, 0.6),
        0 0 36px rgba(255, 107, 138, 0.3),
        0 3px 12px rgba(0, 0, 0, 0.6);
}

.piece.ai-moved {
    border: 4px solid #ffd700;
    box-shadow: 
        0 0 20px rgba(255, 215, 0, 0.9), 
        0 0 40px rgba(255, 215, 0, 0.5),
        0 4px 16px rgba(0, 0, 0, 0.7);
    z-index: 100;
    animation: goldenPulse 1s ease-in-out infinite;
}

@keyframes eyeGlow {
    0%, 100% { filter: brightness(1.2) drop-shadow(0 0 8px rgba(0, 255, 136, 0.5)); }
    50% { filter: brightness(1.4) drop-shadow(0 0 16px rgba(0, 255, 136, 0.8)); }
}

@keyframes goldenPulse {
    0%, 100% { filter: brightness(1.3) drop-shadow(0 0 10px rgba(255, 215, 0, 0.6)); }
    50% { filter: brightness(1.5) drop-shadow(0 0 20px rgba(255, 215, 0, 0.9)); }
}

.valid-move-indicator {
    position: absolute;
    width: 4%;
    height: 3.6%;
    border-radius: 50%;
    background: radial-gradient(circle, var(--neon-green) 0%, rgba(57, 255, 20, 0.4) 60%, transparent 100%);
    box-shadow: 0 0 8px var(--neon-green), 0 0 16px rgba(57, 255, 20, 0.5);
    opacity: 0.85;
    pointer-events: none;
    z-index: 5;
}

.valid-move-indicator.capture-move-indicator {
    background: radial-gradient(circle, var(--neon-red, #ff3b3b) 0%, rgba(255, 59, 59, 0.55) 60%, transparent 100%);
    box-shadow: 0 0 10px var(--neon-red, #ff3b3b), 0 0 20px rgba(255, 59, 59, 0.6);
}

/* Input section */
.input-section {
    padding: 10px 24px;
    background: var(--paper);
    border-top: 1px solid var(--line);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.5s both;
}

.input-section::before {
    content: '';
    position: absolute;
    left: 24px; right: 24px; top: -1px;
    height: 1px;
    background: var(--ink);
    transform: scaleX(0);
    transform-origin: right;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.6s forwards;
}

.input-wrapper {
    display: flex;
    gap: 12px;
    align-items: stretch;
}

#command-input {
    flex: 1;
    padding: 10px 14px;
    border: 1px solid var(--line-strong);
    background: var(--paper-warm);
    color: var(--ink);
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.88rem;
    letter-spacing: 0.01em;
    border-radius: 0;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

#command-input::placeholder {
    color: var(--ink-faint);
    font-style: italic;
    font-family: 'DM Sans', sans-serif;
}

#command-input:focus {
    outline: none;
    border-color: var(--ink);
    background: var(--paper);
    box-shadow: 0 2px 0 var(--ink);
}

.hints {
    margin-top: 10px;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.72rem;
    color: var(--ink-light);
    letter-spacing: 0.03em;
    font-style: italic;
}

.hints::before {
    content: '—  ';
    color: var(--ink-faint);
    font-style: normal;
}

/* Buttons */
.btn {
    padding: 12px 20px;
    border: 1px solid #d4a83a;
    background: rgba(61, 40, 23, 0.5);
    color: #d4a83a;
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.8rem;
    font-weight: 500;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    border-radius: 4px;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
    overflow: hidden;
}

.btn::before {
    content: '';
    position: absolute;
    bottom: 0; left: 0;
    width: 100%;
    height: 0;
    background: linear-gradient(135deg, #d4a83a, #b8860b);
    transition: height 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    z-index: -1;
}

.btn:hover {
    color: #0d2818;
    box-shadow: 0 0 15px rgba(212, 168, 58, 0.3);
}

.btn:hover::before {
    height: 100%;
}

.btn-primary {
    padding: 12px 24px;
    border: none;
    background: linear-gradient(135deg, #d4a83a, #b8860b);
    color: #0d2818;
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.82rem;
    font-weight: 600;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    border-radius: 4px;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
    overflow: hidden;
    box-shadow: 0 2px 8px rgba(212, 168, 58, 0.3);
}

.btn-primary::after {
    content: '→';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.btn-primary:hover {
    background: linear-gradient(135deg, #e4b84a, #d4a83a);
    transform: translateY(-1px);
    box-shadow: 0 4px 16px rgba(212, 168, 58, 0.4);
}

.btn-primary:hover::after {
    transform: translateX(4px);
}

.btn.danger {
    border-color: #c41e3a;
    color: #c41e3a;
    background: rgba(196, 30, 58, 0.1);
}

.btn.danger::before {
    background: linear-gradient(135deg, #c41e3a, #9a152a);
}

.btn.danger:hover {
    color: #fff;
    box-shadow: 0 0 15px rgba(196, 30, 58, 0.4);
}

/* Modal */
.modal {
    display: none;
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(26, 26, 26, 0.5);
    backdrop-filter: blur(2px);
    -webkit-backdrop-filter: blur(2px);
    z-index: 100;
    justify-content: center;
    align-items: center;
    animation: fadeIn 0.3s ease;
}

.modal.show {
    display: flex;
}

.modal-content {
    position: relative;
    background: var(--paper);
    padding: 36px 32px;
    width: 90%;
    max-width: 440px;
    border: 1px solid var(--line-strong);
    box-shadow:
        0 20px 60px rgba(0, 0, 0, 0.15),
        0 2px 0 var(--ink);
    animation: modalIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.modal-content::before {
    content: '';
    position: absolute;
    top: 12px; left: 12px; right: 12px; bottom: 12px;
    border: 1px solid var(--line);
    pointer-events: none;
}

.modal-content h3 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1.6rem;
    margin-bottom: 24px;
    color: var(--ink);
    letter-spacing: 0.01em;
    font-style: italic;
    text-align: center;
    padding-bottom: 16px;
    border-bottom: 1px solid var(--line);
    position: relative;
}

.modal-content h3::after {
    content: '';
    position: absolute;
    bottom: -1px; left: 50%;
    transform: translateX(-50%);
    width: 40px;
    height: 1px;
    background: var(--ink);
}

.form-group {
    margin-bottom: 18px;
}

.form-group label {
    display: block;
    margin-bottom: 8px;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: var(--ink-medium);
    font-weight: 500;
}

.form-group input,
.form-group select {
    width: 100%;
    padding: 12px 14px;
    border: 1px solid var(--line-strong);
    border-radius: 0;
    background: var(--paper-warm);
    color: var(--ink);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.88rem;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.form-group input:focus,
.form-group select:focus {
    outline: none;
    border-color: var(--ink);
    background: var(--paper);
    box-shadow: 0 2px 0 var(--ink);
}

.form-group small {
    display: block;
    margin-top: 6px;
    color: var(--ink-light);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.72rem;
    font-style: italic;
}

.modal-buttons {
    display: flex;
    gap: 12px;
    justify-content: center;
    margin-top: 28px;
}

.modal-buttons .btn {
    min-width: 100px;
}

/* Responsive */
@media (max-width: 900px) {
    .main {
        flex-direction: column;
        padding: 10px 16px;
        gap: 12px;
    }

    .side-panel {
        width: 100%;
        flex-direction: row;
        flex-wrap: wrap;
        max-height: 160px;
    }

    .panel-section {
        flex: 1;
        min-width: 220px;
    }

    .messages {
        max-height: 100px;
    }

    .header {
        padding: 6px 16px;
    }

    .header::after {
        left: 20px; right: 20px;
    }

    .input-section {
        padding: 8px 16px;
    }

    .input-section::before {
        left: 20px; right: 20px;
    }
}

@media (max-width: 600px) {
    .header h1 {
        font-size: 1.2rem;
    }

    .header-actions {
        gap: 10px;
    }

    #turn-indicator {
        padding: 6px 14px;
        font-size: 0.7rem;
    }

    .side-panel {
        flex-direction: column;
        max-height: none;
    }

    .panel-section {
        min-width: auto;
    }

    .controls {
        flex-direction: row;
        flex-wrap: wrap;
    }

    .controls .btn {
        flex: 1;
        min-width: 120px;
    }

    .modal-content {
        padding: 24px 20px;
    }
}

/* Animations */
@keyframes fadeIn {
    from { opacity: 0; }
    to { opacity: 1; }
}

@keyframes fadeInUp {
    from {
        opacity: 0;
        transform: translateY(16px);
    }
    to {
        opacity: 1;
        transform: translateY(0);
    }
}

@keyframes slideInLeft {
    from {
        opacity: 0;
        transform: translateX(-12px);
    }
    to {
        opacity: 1;
        transform: translateX(0);
    }
}

@keyframes scaleIn {
    from { transform: scaleX(0); }
    to { transform: scaleX(1); }
}

@keyframes spin {
    to { transform: rotate(360deg); }
}

@keyframes modalIn {
    from {
        opacity: 0;
        transform: translateY(20px) scale(0.98);
    }
    to {
        opacity: 1;
        transform: translateY(0) scale(1);
    }
}

@keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
}

/* Game Over Overlay */
.game-over-overlay {
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(250, 250, 248, 0.95);
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    z-index: 50;
    border-radius: 4px;
    backdrop-filter: blur(4px);
    -webkit-backdrop-filter: blur(4px);
    animation: fadeIn 0.4s ease;
}

.game-over-overlay::before {
    content: '';
    position: absolute;
    top: 24px; left: 24px; right: 24px; bottom: 24px;
    border: 1px solid var(--line-strong);
    pointer-events: none;
}

.game-over-overlay::after {
    content: '';
    position: absolute;
    top: 32px; left: 32px; right: 32px; bottom: 32px;
    border: 1px solid var(--line);
    pointer-events: none;
}

.game-over-overlay h2 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 2.8rem;
    color: var(--ink);
    margin-bottom: 12px;
    letter-spacing: 0.02em;
    font-style: italic;
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.2s both;
}

.game-over-overlay h2::before,
.game-over-overlay h2::after {
    content: '—';
    display: inline-block;
    margin: 0 16px;
    font-weight: 300;
    color: var(--ink-faint);
    font-style: normal;
    vertical-align: middle;
    font-size: 0.5em;
}

.game-over-overlay p {
    font-family: 'DM Sans', sans-serif;
    color: var(--ink-medium);
    margin-bottom: 32px;
    font-size: 0.95rem;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    text-align: center;
    padding: 0 20px;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.3s both;
}

.game-over-overlay button {
    padding: 14px 32px;
    border: 1px solid var(--ink);
    background: var(--ink);
    color: var(--paper);
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-weight: 500;
    font-size: 0.85rem;
    letter-spacing: 0.15em;
    text-transform: uppercase;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.4s both;
}

.game-over-overlay button::after {
    content: ' ↻';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.3s ease;
}

.game-over-overlay button:hover {
    background: var(--ink-soft);
    transform: translateY(-2px);
    box-shadow: 0 6px 20px rgba(26, 26, 26, 0.25);
}

.game-over-overlay button:hover::after {
    transform: rotate(-180deg);
}

/* Victory Reward Overlay */
.victory-reward-overlay {
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(7, 7, 8, 0.92);
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    z-index: 60;
    border-radius: 4px;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
    animation: fadeIn 0.5s ease;
}

.victory-reward-overlay .reward-card {
    background: linear-gradient(135deg, rgba(212, 175, 55, 0.15), rgba(18, 18, 22, 0.8));
    border: 1px solid rgba(212, 175, 55, 0.4);
    border-radius: 12px;
    padding: 48px 56px;
    text-align: center;
    color: #f3e9d2;
    max-width: 420px;
    box-shadow: 0 24px 80px rgba(0, 0, 0, 0.6), 0 0 60px rgba(212, 175, 55, 0.2);
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1);
}

.victory-reward-overlay h2 {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 2.2rem;
    color: #d4af37;
    margin: 0 0 24px;
    letter-spacing: 0.05em;
}

.victory-reward-overlay .reward-skill-points {
    font-size: 1.6rem;
    color: #d4af37;
    margin-bottom: 16px;
    font-weight: 600;
}

.victory-reward-overlay .reward-sandbox {
    color: #0d7377;
    background: rgba(13, 115, 119, 0.15);
    padding: 10px 20px;
    border-radius: 8px;
    margin-bottom: 16px;
    font-weight: 600;
}

.victory-reward-overlay .reward-reasons {
    list-style: none;
    padding: 0;
    margin: 0 0 24px;
    color: #99948a;
    font-size: 0.9rem;
}

.victory-reward-overlay .reward-reasons li {
    padding: 4px 0;
}

.victory-reward-overlay button {
    padding: 12px 32px;
    border: 1px solid #d4af37;
    background: #d4af37;
    color: #070708;
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    font-size: 0.85rem;
    border-radius: 4px;
    transition: all 0.2s ease;
}

.victory-reward-overlay button:hover {
    background: transparent;
    color: #d4af37;
}

/* Detection Reset Overlay */
.detection-reset-overlay {
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(7, 7, 8, 0.95);
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    z-index: 70;
    border-radius: 4px;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
    animation: fadeIn 0.5s ease;
}

.detection-reset-overlay .detection-reset-card {
    background: linear-gradient(135deg, rgba(155, 35, 53, 0.2), rgba(18, 18, 22, 0.9));
    border: 1px solid rgba(155, 35, 53, 0.5);
    border-radius: 12px;
    padding: 48px 56px;
    text-align: center;
    color: #f3e9d2;
    max-width: 420px;
    box-shadow: 0 24px 80px rgba(0, 0, 0, 0.7), 0 0 60px rgba(155, 35, 53, 0.25);
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1);
}

.detection-reset-overlay h2 {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 2.2rem;
    color: #9b2335;
    margin: 0 0 24px;
    letter-spacing: 0.05em;
}

.detection-reset-overlay .detection-message {
    color: #f3e9d2;
    font-size: 1.1rem;
    margin-bottom: 12px;
}

.detection-reset-overlay .detection-detail {
    color: #99948a;
    font-size: 0.9rem;
    margin-bottom: 32px;
}

.detection-reset-overlay button {
    padding: 12px 32px;
    border: 1px solid #9b2335;
    background: #9b2335;
    color: #f3e9d2;
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    font-size: 0.85rem;
    border-radius: 4px;
    transition: all 0.2s ease;
}

.detection-reset-overlay button:hover {
    background: transparent;
    color: #9b2335;
}

/* AI Thinking Overlay */
.thinking-overlay {
    display: none;
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(250, 250, 248, 0.9);
    backdrop-filter: blur(6px);
    -webkit-backdrop-filter: blur(6px);
    z-index: 200;
    justify-content: center;
    align-items: center;
    animation: fadeIn 0.3s ease;
}

.thinking-overlay.show {
    display: flex;
}

.thinking-content {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 48px;
    flex-wrap: wrap;
    color: var(--ink);
    z-index: 1;
}

.thinking-loading {
    display: flex;
    flex-direction: column;
    align-items: center;
    text-align: center;
}

/* 井字棋 mini game */
.ttt-wrap {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 12px;
    padding: 18px;
    border: 1px solid var(--line-strong);
    border-radius: 14px;
    background: rgba(128, 128, 128, 0.06);
    box-shadow: 0 0 24px rgba(0, 0, 0, 0.3);
}

.ttt-title {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink);
    opacity: 0.7;
}

.ttt-board {
    display: grid;
    grid-template-columns: repeat(3, 52px);
    grid-template-rows: repeat(3, 52px);
    gap: 6px;
}

.ttt-cell {
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--line-strong);
    border-radius: 8px;
    background: rgba(128, 128, 128, 0.08);
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1.9rem;
    font-weight: 700;
    color: var(--ink);
    cursor: pointer;
    user-select: none;
    transition: all 0.15s ease;
}

.ttt-cell:hover:not(.taken):not(.over) {
    border-color: var(--neon-green, var(--ghost-green));
    box-shadow: 0 0 10px var(--neon-green, var(--ghost-green));
}

.ttt-cell.taken { cursor: default; }
.ttt-cell.x { color: var(--neon-green, var(--ghost-green)); text-shadow: 0 0 8px var(--neon-green, var(--ghost-green)); }
.ttt-cell.o { color: #f87171; text-shadow: 0 0 8px rgba(248, 113, 113, 0.6); }
.ttt-cell.win { background: rgba(248, 113, 113, 0.18); box-shadow: inset 0 0 12px rgba(248, 113, 113, 0.4); }

.ttt-status {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ink-light, var(--ghost-gray));
    min-height: 1.2em;
    text-align: center;
    opacity: 0.85;
}

.ttt-restart {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    letter-spacing: 0.05em;
    padding: 6px 18px;
    border: 1px solid var(--line-strong);
    border-radius: 20px;
    background: transparent;
    color: var(--ghost-green, var(--ink));
    cursor: pointer;
    transition: all 0.15s ease;
}

.ttt-restart:hover {
    border-color: var(--neon-green, var(--ghost-green));
    color: var(--neon-green, var(--ghost-green));
    box-shadow: 0 0 10px var(--neon-green, var(--ghost-green));
}

.thinking-spinner {
    position: relative;
    width: 64px;
    height: 64px;
    margin: 0 auto 28px;
    border-radius: 50%;
}

.thinking-spinner::before,
.thinking-spinner::after {
    content: '';
    position: absolute;
    border-radius: 50%;
    border: 1px solid transparent;
}

.thinking-spinner::before {
    inset: 0;
    border-top-color: var(--ink);
    border-right-color: var(--ink);
    animation: spin 1.2s cubic-bezier(0.4, 0, 0.2, 1) infinite;
}

.thinking-spinner::after {
    inset: 12px;
    border-bottom-color: var(--ink-faint);
    border-left-color: var(--ink-faint);
    animation: spin 0.9s cubic-bezier(0.4, 0, 0.2, 1) infinite reverse;
}

.thinking-text {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1.4rem;
    margin-bottom: 8px;
    color: var(--ink);
    letter-spacing: 0.02em;
    font-style: italic;
}

.thinking-stage {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.8rem;
    color: var(--ink-light);
    letter-spacing: 0.1em;
    text-transform: uppercase;
}

.thinking-stage::after {
    content: '';
    display: inline-block;
    width: 20px;
    text-align: left;
    animation: dots 1.5s steps(4, end) infinite;
}

@keyframes dots {
    0% { content: ''; }
    25% { content: '.'; }
    50% { content: '..'; }
    75% { content: '...'; }
    100% { content: ''; }
}

/* Logs Modal */
.logs-content {
    width: 90%;
    max-width: 780px;
    max-height: 85vh;
    display: flex;
    flex-direction: column;
    background: var(--paper);
    border: 1px solid var(--line-strong);
    padding: 28px;
    box-shadow:
        0 20px 60px rgba(0, 0, 0, 0.15),
        0 2px 0 var(--ink);
    position: relative;
    animation: modalIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.logs-content::before {
    content: '';
    position: absolute;
    top: 10px; left: 10px; right: 10px; bottom: 10px;
    border: 1px solid var(--line);
    pointer-events: none;
}

.logs-container {
    flex: 1;
    overflow-y: auto;
    background: var(--paper-warm);
    padding: 20px 24px;
    margin: 16px 0;
    max-height: 65vh;
    border: 1px solid var(--line);
    position: relative;
}

.logs-container::before {
    content: '';
    position: absolute;
    top: 0; left: 50px;
    width: 1px;
    height: 100%;
    background: var(--line-strong);
    opacity: 0.5;
}

.logs-container::-webkit-scrollbar { width: 5px; }
.logs-container::-webkit-scrollbar-track { background: transparent; }
.logs-container::-webkit-scrollbar-thumb { background: var(--line-strong); border-radius: 2px; }

.log-entry {
    margin-bottom: 20px;
    padding-bottom: 20px;
    border-bottom: 1px dotted var(--line-strong);
    position: relative;
}

.log-entry:last-child {
    margin-bottom: 0;
    padding-bottom: 0;
    border-bottom: none;
}

.log-entry.error {
    border-left: 2px solid var(--danger);
    padding-left: 16px;
}

.log-entry.success {
    border-left: 2px solid var(--success);
    padding-left: 16px;
}

.log-header {
    display: flex;
    justify-content: space-between;
    margin-bottom: 10px;
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.72rem;
    color: var(--ink-light);
    letter-spacing: 0.03em;
}

.log-user-input {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-style: italic;
    color: var(--ink);
    margin-bottom: 12px;
    padding: 8px 0;
    font-size: 0.95rem;
    border-bottom: 1px solid var(--line);
}

.log-user-input::before {
    content: '"';
    font-family: 'Playfair Display', serif;
    font-size: 1.2em;
    color: var(--ink-faint);
    line-height: 0;
    vertical-align: -0.2em;
    margin-right: 2px;
}

.log-user-input::after {
    content: '"';
    font-family: 'Playfair Display', serif;
    font-size: 1.2em;
    color: var(--ink-faint);
    line-height: 0;
    vertical-align: -0.2em;
    margin-left: 2px;
}

.log-section {
    margin-top: 12px;
    padding: 12px 14px;
    background: var(--paper);
    border: 1px solid var(--line);
}

.log-section-title {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.72rem;
    color: var(--ink-medium);
    margin-bottom: 8px;
    font-weight: 500;
    text-transform: uppercase;
    letter-spacing: 0.12em;
}

.log-content {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.78rem;
    white-space: pre-wrap;
    word-break: break-all;
    max-height: 180px;
    overflow-y: auto;
    background: var(--paper-warm);
    padding: 10px 12px;
    color: var(--ink-soft);
    border: 1px solid var(--line);
    line-height: 1.6;
}

.log-json {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.72rem;
    color: var(--ink-medium);
}

/* Locked states */
.board-locked {
    pointer-events: none;
    opacity: 0.65;
    filter: grayscale(0.3) brightness(0.85);
}

.input-locked {
    pointer-events: none;
    opacity: 0.45;
    filter: grayscale(0.4);
}

/* Toast */
.toast-container {
    position: absolute;
    top: 80px;
    right: 32px;
    z-index: 300;
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.toast {
    padding: 14px 20px;
    background: var(--ink);
    color: var(--paper);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.85rem;
    border-left: 3px solid var(--paper);
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15);
    animation: toastIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
    max-width: 320px;
    position: relative;
}

.toast.success {
    border-left-color: var(--success);
}

.toast.error {
    border-left-color: var(--danger);
}

.toast.fade-out {
    animation: toastOut 0.3s ease forwards;
}

@keyframes toastIn {
    from {
        opacity: 0;
        transform: translateX(20px);
    }
    to {
        opacity: 1;
        transform: translateX(0);
    }
}

@keyframes toastOut {
    from {
        opacity: 1;
        transform: translateX(0);
    }
    to {
        opacity: 0;
        transform: translateX(20px);
    }
}

#board-container.coord-insert-mode {
    cursor: crosshair;
}

#board-container.coord-insert-mode .piece {
    cursor: crosshair;
}

#board-container.coord-insert-mode .valid-move-indicator {
    cursor: crosshair;
}

.btn.active {
    background: var(--ink);
    color: var(--paper);
}

.btn.active::before {
    height: 100%;
}

/* Coord dots */
.coord-dot {
    cursor: pointer;
    pointer-events: auto;
    transition: all 0.15s ease;
}

.coord-dot:hover {
    r: 0.35;
    filter: drop-shadow(0 0 4px rgba(34, 197, 94, 0.9));
}

.coord-dot.selected {
    filter: drop-shadow(0 0 8px rgba(34, 197, 94, 1));
}

.coord-dot.in-region {
    fill: #86efac;
}

/* Region rect */
.region-rect {
    pointer-events: none;
    fill: rgba(34, 197, 94, 0.15);
    stroke: #22c55e;
    stroke-width: 0.08;
    filter: drop-shadow(0 0 6px rgba(34, 197, 94, 0.3));
}

/* Coord select button */
.coord-select-btn-wrapper {
    position: relative;
    display: inline-flex;
}

.coord-select-btn-wrapper .btn {
    padding-right: 32px;
}

.btn-toggle-mode {
    position: absolute;
    right: 4px;
    top: 50%;
    transform: translateY(-50%);
    width: 22px;
    height: 22px;
    border: 1px solid var(--ink);
    background: transparent;
    color: var(--ink);
    cursor: pointer;
    font-size: 12px;
    border-radius: 3px;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: all 0.2s ease;
    z-index: 2;
    line-height: 1;
    padding: 0;
}

.btn-toggle-mode:hover {
    background: var(--ink);
    color: var(--paper);
}

/* AI Personality Card */
.personality-card {
    position: relative;
    padding: 14px 12px 12px;
    background: var(--paper);
    border: 1px solid var(--line);
    transition: all 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.personality-card::before {
    content: '';
    position: absolute;
    top: 6px; left: 6px; right: 6px; bottom: 6px;
    border: 1px solid var(--line);
    pointer-events: none;
    opacity: 0.6;
}

.personality-header {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 10px;
    padding-bottom: 10px;
    border-bottom: 1px dashed var(--line);
}

.personality-icon {
    font-size: 1.6rem;
    width: 40px;
    height: 40px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--paper-warm);
    border: 1px solid var(--line);
    animation: float 3s ease-in-out infinite;
}

.personality-title {
    display: flex;
    flex-direction: column;
}

.personality-type {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1rem;
    font-weight: 500;
    font-style: italic;
    color: var(--ink);
    letter-spacing: 0.02em;
}

.personality-subtitle {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.65rem;
    color: var(--ink-light);
    letter-spacing: 0.1em;
    text-transform: uppercase;
}

.personality-desc {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ink-medium);
    line-height: 1.5;
    margin-bottom: 12px;
    font-style: italic;
    padding-left: 4px;
}

.personality-bars {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 0 4px;
}

.personality-bar {
    display: flex;
    align-items: center;
    gap: 8px;
}

.bar-label {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.65rem;
    color: var(--ink-light);
    letter-spacing: 0.1em;
    text-transform: uppercase;
    width: 36px;
    flex-shrink: 0;
}

.bar-chars {
    flex: 1;
    font-family: 'JetBrains Mono', 'Courier New', monospace;
    font-size: 0.7rem;
    letter-spacing: 0.05em;
    transition: all 0.4s ease;
}

.bar-chars.aggressive {
    color: var(--neon-pink);
    text-shadow: 0 0 6px rgba(255, 45, 111, 0.4);
}

.bar-chars.defensive {
    color: var(--neon-cyan);
    text-shadow: 0 0 6px rgba(0, 240, 255, 0.4);
}

.bar-percent {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.65rem;
    color: var(--ink-soft);
    min-width: 32px;
    text-align: right;
    flex-shrink: 0;
}

.personality-card.type-aggressive {
    border-color: rgba(255, 45, 111, 0.3);
}
.personality-card.type-aggressive .personality-icon {
    border-color: rgba(255, 45, 111, 0.4);
    box-shadow: 0 0 12px rgba(255, 45, 111, 0.2);
}

.personality-card.type-defensive {
    border-color: rgba(0, 240, 255, 0.3);
}
.personality-card.type-defensive .personality-icon {
    border-color: rgba(0, 240, 255, 0.4);
    box-shadow: 0 0 12px rgba(0, 240, 255, 0.2);
}

.personality-card.type-random {
    border-color: rgba(255, 215, 0, 0.4);
}
.personality-card.type-random .personality-icon {
    border-color: rgba(255, 215, 0, 0.5);
    box-shadow: 0 0 12px rgba(255, 215, 0, 0.25);
    animation: spin-slow 4s linear infinite;
}

.personality-card.personality-changed {
    animation: personalityPulse 0.6s cubic-bezier(0.22, 1, 0.36, 1);
}

@keyframes personalityPulse {
    0% { transform: scale(1); }
    30% { transform: scale(1.03); box-shadow: 0 4px 20px rgba(0,0,0,0.1); }
    100% { transform: scale(1); }
}

@keyframes float {
    0%, 100% { transform: translateY(0); }
    50% { transform: translateY(-3px); }
}

@keyframes spin-slow {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
}

/* Mechanisms List */
.mechanisms-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    max-height: 160px;
    overflow-y: auto;
    padding-right: 4px;
}

.mechanisms-list::-webkit-scrollbar { width: 4px; }
.mechanisms-list::-webkit-scrollbar-track { background: transparent; }
.mechanisms-list::-webkit-scrollbar-thumb {
    background: var(--line-strong);
    border-radius: 2px;
}

.mechanism-badge {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 10px;
    background: var(--paper);
    border: 1px solid var(--line);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ink-soft);
    position: relative;
    overflow: hidden;
    animation: mechanismSlideIn 0.4s cubic-bezier(0.22, 1, 0.36, 1) both;
    transition: all 0.25s ease;
}

.mechanism-badge:hover {
    border-color: var(--ink-faint);
    background: var(--paper-warm);
}

.mechanism-badge.leaving {
    animation: mechanismSlideOut 0.3s ease forwards;
}

.mechanism-icon {
    font-size: 1rem;
    flex-shrink: 0;
}

.mechanism-text {
    flex: 1;
    line-height: 1.3;
}

.mechanism-count {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.7rem;
    font-weight: 500;
    padding: 2px 6px;
    background: var(--ink);
    color: var(--paper);
    flex-shrink: 0;
    min-width: 24px;
    text-align: center;
}

.mechanism-stop {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.65rem;
    font-weight: 600;
    padding: 2px 8px;
    border: 1px solid var(--danger);
    background: transparent;
    color: var(--danger);
    cursor: pointer;
    flex-shrink: 0;
    transition: all 0.2s ease;
    letter-spacing: 0.05em;
}

.mechanism-stop:hover {
    background: var(--danger);
    color: var(--paper);
}

.mechanism-badge.type-skip {
    border-left: 3px solid var(--warning);
}
.mechanism-badge.type-skip .mechanism-count {
    background: var(--warning);
    color: var(--paper);
}

.mechanism-badge.type-ai {
    border-left: 3px solid var(--neon-cyan);
}
.mechanism-badge.type-ai .mechanism-count {
    background: var(--neon-cyan);
    color: var(--ink);
}

.mechanism-badge.type-random {
    border-left: 3px solid var(--neon-gold);
}
.mechanism-badge.type-random .mechanism-count {
    background: var(--neon-gold);
    color: var(--ink);
}

.mechanism-badge.type-extra {
    border-left: 3px solid var(--neon-green);
}
.mechanism-badge.type-extra .mechanism-count {
    background: var(--neon-green);
    color: var(--ink);
}

.mechanism-badge.type-limit {
    border-left: 3px solid var(--neon-magenta);
}
.mechanism-badge.type-limit .mechanism-count {
    background: var(--neon-magenta);
    color: var(--paper);
}

@keyframes mechanismSlideIn {
    from {
        opacity: 0;
        transform: translateX(16px);
    }
    to {
        opacity: 1;
        transform: translateX(0);
    }
}

@keyframes mechanismSlideOut {
    from {
        opacity: 1;
        transform: translateX(0);
        max-height: 60px;
        margin-bottom: 6px;
        padding-top: 8px;
        padding-bottom: 8px;
    }
    to {
        opacity: 0;
        transform: translateX(-16px);
        max-height: 0;
        margin-bottom: 0;
        padding-top: 0;
        padding-bottom: 0;
    }
}

/* Board mechanism effects */
#board-container.freeze-effect::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: linear-gradient(135deg,
        rgba(0, 240, 255, 0.08) 0%,
        rgba(0, 240, 255, 0.02) 50%,
        rgba(0, 240, 255, 0.08) 100%);
    pointer-events: none;
    z-index: 30;
    border-radius: 4px;
    animation: frostPulse 2s ease-in-out infinite;
}

#board-container.freeze-effect::after {
    content: '❄ 冻结中';
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1.5rem;
    font-style: italic;
    color: var(--neon-cyan);
    text-shadow: 0 0 20px rgba(0, 240, 255, 0.5);
    z-index: 31;
    pointer-events: none;
    opacity: 0.8;
    animation: frostText 2s ease-in-out infinite;
}

@keyframes frostPulse {
    0%, 100% { opacity: 0.6; }
    50% { opacity: 1; }
}

@keyframes frostText {
    0%, 100% { opacity: 0.6; transform: translate(-50%, -50%) scale(1); }
    50% { opacity: 0.9; transform: translate(-50%, -50%) scale(1.05); }
}

.piece.random-move {
    animation: randomGlow 0.6s ease;
}

@keyframes randomGlow {
    0% { filter: brightness(1); }
    50% { filter: brightness(1.4) hue-rotate(30deg); }
    100% { filter: brightness(1); }
}

#turn-indicator.ai-control {
    background: linear-gradient(90deg, var(--ink), #333);
}

#turn-indicator.ai-control::after {
    content: '🤖';
    display: inline-block;
    margin-left: 8px;
    font-size: 0.9em;
    animation: robotPulse 1s ease-in-out infinite;
}

@keyframes robotPulse {
    0%, 100% { transform: scale(1); }
    50% { transform: scale(1.15); }
}

.panel-section:nth-child(3) { animation-delay: 0.32s; }
.panel-section:nth-child(4) { animation-delay: 0.39s; }
.panel-section:nth-child(5) { animation-delay: 0.46s; }
.panel-section:nth-child(6) { animation-delay: 0.53s; }
.panel-section:nth-child(7) { animation-delay: 0.60s; }
.panel-section:nth-child(8) { animation-delay: 0.67s; }

.coord-insert-mode {
    cursor: default;
}

/* Game Objectives Panel */
.objectives-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.objectives-list .empty {
    color: var(--muted-ink);
    font-style: italic;
    font-size: 0.85rem;
    opacity: 0.5;
}

.objective-item {
    display: flex;
    align-items: flex-start;
    gap: 10px;
    padding: 10px 12px;
    border: 1px solid var(--paper-dark);
    border-radius: 3px;
    background: var(--paper);
    transition: all 0.3s ease;
}

.objective-item.enabled {
    border-color: var(--ink);
}

.objective-item.achieved {
    border-color: #b8860b;
    background: linear-gradient(135deg,
        rgba(184, 134, 11, 0.06) 0%,
        rgba(184, 134, 11, 0.02) 100%);
}

.objective-item.disabled {
    opacity: 0.35;
    border-style: dashed;
}

.objective-icon {
    font-size: 1.4rem;
    line-height: 1;
    flex-shrink: 0;
    margin-top: 2px;
}

.objective-item.achieved .objective-icon {
    filter: drop-shadow(0 0 4px rgba(184, 134, 11, 0.5));
}

.objective-content {
    flex: 1;
    min-width: 0;
}

.objective-title {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 0.92rem;
    font-weight: 600;
    color: var(--ink);
    margin-bottom: 3px;
    letter-spacing: 0.02em;
}

.objective-item.achieved .objective-title {
    color: #8b6914;
}

.objective-item.disabled .objective-title {
    text-decoration: line-through;
    text-decoration-thickness: 1px;
}

.objective-desc {
    font-family: 'DM Sans', -apple-system, sans-serif;
    font-size: 0.78rem;
    color: var(--muted-ink);
    line-height: 1.4;
}

.objective-badge {
    display: inline-block;
    font-size: 0.65rem;
    font-family: 'DM Mono', monospace;
    text-transform: uppercase;
    letter-spacing: 0.1em;
    padding: 2px 6px;
    border-radius: 2px;
    margin-bottom: 4px;
}

.objective-badge.victory {
    background: rgba(184, 134, 11, 0.1);
    color: #8b6914;
}

.objective-badge.draw {
    background: rgba(100, 100, 100, 0.1);
    color: #555;
}

.objective-badge.special {
    background: rgba(88, 166, 135, 0.1);
    color: #3a7a5e;
}

.objective-status {
    font-family: 'DM Mono', monospace;
    font-size: 0.65rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    margin-top: 5px;
    padding-top: 5px;
    border-top: 1px solid var(--paper-dark);
}

.objective-item.enabled .objective-status {
    color: var(--accent-green);
}

.objective-item.achieved .objective-status {
    color: #b8860b;
}

.objective-item.disabled .objective-status {
    color: var(--muted-ink);
}

@keyframes objectiveAchieved {
    0% { transform: scale(1); }
    30% { transform: scale(1.05); }
    100% { transform: scale(1); }
}

.objective-item.achieved {
    animation: objectiveAchieved 0.6s ease;
}

/* Jungle Effects - Falling Leaves */
#board-container::after {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    pointer-events: none;
    z-index: 25;
    overflow: hidden;
    background-image:
        url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'%3E%3Cpath d='M10 0C5 5 0 10 10 20C20 10 15 5 10 0Z' fill='%232d6a4f'/%3E%3C/svg%3E"),
        url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 15 15'%3E%3Cpath d='M7.5 0C3 4 0 8 7.5 15C12 8 9 4 7.5 0Z' fill='%233d7a5e'/%3E%3C/svg%3E"),
        url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 12'%3E%3Cpath d='M6 0C2 3 0 6 6 12C10 6 8 3 6 0Z' fill='%23228b45'/%3E%3C/svg%3E");
    background-repeat: no-repeat;
    background-position: 
        10% -20px,
        60% -15px,
        85% -10px;
    animation: leafFall1 8s linear infinite, leafFall2 10s linear infinite, leafFall3 12s linear infinite;
}

@keyframes leafFall1 {
    0% { 
        background-position: 10% -20px; 
        opacity: 0;
    }
    10% { opacity: 0.6; }
    90% { opacity: 0.6; }
    100% { 
        background-position: 30% 120%; 
        opacity: 0;
    }
}

@keyframes leafFall2 {
    0% { 
        background-position: 60% -15px; 
        opacity: 0;
    }
    20% { opacity: 0.5; }
    80% { opacity: 0.5; }
    100% { 
        background-position: 40% 120%; 
        opacity: 0;
    }
}

@keyframes leafFall3 {
    0% { 
        background-position: 85% -10px; 
        opacity: 0;
    }
    15% { opacity: 0.4; }
    85% { opacity: 0.4; }
    100% { 
        background-position: 95% 120%; 
        opacity: 0;
    }
}

/* Jungle Fog Effect */
.board-section::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    pointer-events: none;
    z-index: 15;
    background: radial-gradient(ellipse at 50% 50%, 
        rgba(22, 64, 42, 0.15) 0%, 
        transparent 60%);
    animation: fogDrift 15s ease-in-out infinite;
}

@keyframes fogDrift {
    0%, 100% { 
        opacity: 0.4; 
        transform: translateX(0) scale(1);
    }
    50% { 
        opacity: 0.6; 
        transform: translateX(10px) scale(1.05);
    }
}

/* Glowing Eyes Effect for Selected Pieces */
.piece.selected::after {
    content: '';
    position: absolute;
    width: 30%;
    height: 30%;
    background: radial-gradient(circle, #00ff88 0%, transparent 70%);
    border-radius: 50%;
    opacity: 0;
    animation: eyeGlowPulse 1s ease-in-out infinite;
    box-shadow: 0 0 10px #00ff88, 0 0 20px #00ff88;
}

@keyframes eyeGlowPulse {
    0%, 100% { opacity: 0; transform: scale(0.5); }
    50% { opacity: 0.8; transform: scale(1.2); }
}

/* River styling for jungle theme */
.river-text {
    color: #228b45;
    text-shadow: 0 0 10px rgba(34, 139, 69, 0.5);
    font-family: 'KaiTi', serif;
    letter-spacing: 0.3em;
}

/* Valid move indicator jungle style */
.valid-move-indicator {
    background: radial-gradient(circle, #00ff88 0%, rgba(0, 255, 136, 0.3) 60%, transparent 100%);
    box-shadow: 0 0 10px #00ff88, 0 0 20px rgba(0, 255, 136, 0.4);
}

/* Thinking overlay jungle theme */
.thinking-overlay {
    background: rgba(13, 40, 24, 0.95);
}

.thinking-text {
    color: #d4a83a;
    text-shadow: 0 0 10px rgba(212, 168, 58, 0.4);
}

.thinking-spinner::before {
    border-top-color: #d4a83a;
    border-right-color: #d4a83a;
}

.thinking-spinner::after {
    border-bottom-color: rgba(212, 168, 58, 0.5);
    border-left-color: rgba(212, 168, 58, 0.5);
}

/* Game over overlay jungle theme */
.game-over-overlay {
    background: rgba(13, 40, 24, 0.98);
}

.game-over-overlay h2 {
    color: #d4a83a;
    text-shadow: 0 0 20px rgba(212, 168, 58, 0.5);
}

.game-over-overlay button {
    background: linear-gradient(135deg, #d4a83a, #b8860b);
    color: #0d2818;
    border-color: #d4a83a;
}

.game-over-overlay button:hover {
    background: linear-gradient(135deg, #e4b84a, #d4a83a);
}

/* Input section jungle theme */
.input-section {
    background: linear-gradient(180deg, #0d2818 0%, #16402a 100%);
    border-top: 2px solid #d4a83a;
}

#command-input {
    background: rgba(61, 40, 23, 0.6);
    border-color: rgba(212, 168, 58, 0.3);
    color: #d4a83a;
}

#command-input:focus {
    border-color: #d4a83a;
    background: rgba(61, 40, 23, 0.8);
    box-shadow: 0 2px 0 #d4a83a;
}

#command-input::placeholder {
    color: rgba(212, 168, 58, 0.4);
}

.hints {
    color: rgba(212, 168, 58, 0.6);
}

/* Modal jungle theme */
.modal-content {
    background: linear-gradient(135deg, #16402a 0%, #0d2818 100%);
    border-color: rgba(212, 168, 58, 0.4);
}

.modal-content h3 {
    color: #d4a83a;
    border-bottom-color: rgba(212, 168, 58, 0.3);
}

.modal-content h3::after {
    background: #d4a83a;
}

.form-group label {
    color: rgba(212, 168, 58, 0.8);
}

.form-group input,
.form-group select {
    background: rgba(61, 40, 23, 0.6);
    border-color: rgba(212, 168, 58, 0.3);
    color: #d4a83a;
}

.form-group input:focus,
.form-group select:focus {
    border-color: #d4a83a;
    background: rgba(61, 40, 23, 0.8);
    box-shadow: 0 2px 0 #d4a83a;
}

/* Token stats jungle theme */
.token-stat {
    background: rgba(61, 40, 23, 0.5);
}

.token-stat:hover {
    background: rgba(61, 40, 23, 0.7);
}

.token-stat .stat-value {
    color: #d4a83a;
}

.token-stat.cost {
    background: rgba(212, 168, 58, 0.2);
}

.token-stat.cost:hover {
    background: rgba(212, 168, 58, 0.3);
}

.token-stat.cost .stat-label {
    color: rgba(212, 168, 58, 0.7);
}

.token-stat.cost .stat-value {
    color: #d4a83a;
}

/* Messages jungle theme */
.message {
    border-left-color: rgba(212, 168, 58, 0.4);
    color: rgba(212, 168, 58, 0.9);
}

.message:hover {
    border-left-color: #d4a83a;
    background: rgba(212, 168, 58, 0.05);
}

.message.success {
    border-left-color: #228b45;
}

.message.error {
    border-left-color: #c41e3a;
}

/* Personality card jungle theme */
.personality-card {
    background: rgba(61, 40, 23, 0.5);
    border-color: rgba(212, 168, 58, 0.3);
}

.personality-icon {
    background: rgba(212, 168, 58, 0.1);
    border-color: rgba(212, 168, 58, 0.4);
}

.personality-type {
    color: #d4a83a;
}

.personality-subtitle {
    color: rgba(212, 168, 58, 0.6);
}

.personality-desc {
    color: rgba(212, 168, 58, 0.8);
}

/* Toast jungle theme */
.toast {
    background: linear-gradient(135deg, #3d2817 0%, #2a1a10 100%);
    color: #d4a83a;
    border-left-color: #d4a83a;
}

.toast.success {
    border-left-color: #228b45;
}

.toast.error {
    border-left-color: #c41e3a;
}

/* Logs modal jungle theme */
.logs-content {
    background: linear-gradient(135deg, #16402a 0%, #0d2818 100%);
    border-color: rgba(212, 168, 58, 0.4);
}

.logs-container {
    background: rgba(61, 40, 23, 0.5);
    border-color: rgba(212, 168, 58, 0.3);
}

.log-content {
    background: rgba(61, 40, 23, 0.6);
    border-color: rgba(212, 168, 58, 0.2);
    color: rgba(212, 168, 58, 0.9);
}

/* Mechanism badges jungle theme */
.mechanism-badge {
    background: rgba(61, 40, 23, 0.5);
    border-color: rgba(212, 168, 58, 0.3);
    color: rgba(212, 168, 58, 0.9);
}

.mechanism-badge:hover {
    background: rgba(61, 40, 23, 0.7);
    border-color: rgba(212, 168, 58, 0.5);
}

.mechanism-count {
    background: rgba(212, 168, 58, 0.3);
    color: #d4a83a;
}

/* Objectives jungle theme */
.objective-item {
    background: rgba(61, 40, 23, 0.4);
    border-color: rgba(212, 168, 58, 0.2);
}

.objective-item.enabled {
    border-color: #d4a83a;
}

.objective-item.achieved {
    border-color: #d4a83a;
    background: linear-gradient(135deg, rgba(212, 168, 58, 0.15) 0%, rgba(212, 168, 58, 0.05) 100%);
}

.objective-title {
    color: #d4a83a;
}

.objective-item.achieved .objective-title {
    color: #d4a83a;
}

.objective-desc {
    color: rgba(212, 168, 58, 0.7);
}

.objective-badge.victory {
    background: rgba(212, 168, 58, 0.15);
    color: #d4a83a;
}

.objective-badge.special {
    background: rgba(34, 139, 69, 0.15);
    color: #228b45;
}

.objective-status {
    border-top-color: rgba(212, 168, 58, 0.2);
}

.objective-item.enabled .objective-status {
    color: #228b45;
}

.objective-item.achieved .objective-status {
    color: #d4a83a;
}

/* Rules list jungle theme */
.rule-item {
    border-bottom-color: rgba(212, 168, 58, 0.2);
    color: rgba(212, 168, 58, 0.8);
}

.rule-item:hover {
    color: #d4a83a;
    border-bottom-color: rgba(212, 168, 58, 0.4);
}

.rules-list .empty {
    color: rgba(212, 168, 58, 0.4);
}

/* Coord dots jungle theme */
.coord-dot:hover {
    filter: drop-shadow(0 0 4px rgba(212, 168, 58, 0.9));
}

.coord-dot.selected {
    filter: drop-shadow(0 0 8px rgba(212, 168, 58, 1));
}

.coord-dot.in-region {
    fill: #228b45;
}

/* Region rect jungle theme */
.region-rect {
    fill: rgba(34, 139, 69, 0.2);
    stroke: #228b45;
    filter: drop-shadow(0 0 6px rgba(34, 139, 69, 0.4));
}

/* Toggle mode button jungle theme */
.btn-toggle-mode {
    border-color: #d4a83a;
    color: #d4a83a;
}

.btn-toggle-mode:hover {
    background: #d4a83a;
    color: #0d2818;
}

/* AI personality bars jungle theme */
.bar-chars.aggressive {
    color: #c41e3a;
    text-shadow: 0 0 6px rgba(196, 30, 58, 0.5);
}

.bar-chars.defensive {
    color: #00ff88;
    text-shadow: 0 0 6px rgba(0, 255, 136, 0.5);
}

.bar-percent {
    color: rgba(212, 168, 58, 0.8);
}

/* Mechanism stop button jungle theme */
.mechanism-stop {
    border-color: #c41e3a;
    color: #c41e3a;
}

.mechanism-stop:hover {
    background: #c41e3a;
    color: #fff;
}

/* Freeze effect jungle theme */
#board-container.freeze-effect::before {
    background: linear-gradient(135deg,
        rgba(0, 255, 136, 0.08) 0%,
        rgba(0, 255, 136, 0.02) 50%,
        rgba(0, 255, 136, 0.08) 100%);
}

#board-container.freeze-effect::after {
    color: #00ff88;
    text-shadow: 0 0 20px rgba(0, 255, 136, 0.5);
}

/* AI control turn indicator */
#turn-indicator.ai-control {
    background: linear-gradient(90deg, #3d2817, #5c3d2e);
}
`;
    }

    async init() {
        if (this._initialized) return;
        await this.loadConfigs();
        this.renderBoard();
        this.renderPieces();
        this.updateTurnIndicator();
        this.updateActiveRules();
        this.updateGameObjectives();
        this.updateAIPersonality();
        this.updateMechanisms();
        this.bindEvents();
        this.checkApiKey();
        this.loadTokenStats();

        // === 六道 RPG 接入（共享模块） ===
        try {
            if (window.GameSharedRPG) {
                window.GameSharedRPG.install(this, {
                    isSandbox: false,
                    rerender: async (instance) => {
                    instance.clearSelection();
                    instance.renderBoard();
                    instance.renderPieces();
                    instance.updateTurnIndicator();
                    instance.updateActiveRules();
                    instance.updateGameObjectives();
                    instance.updateAIPersonality();
                    instance.updateMechanisms();
                    },
                });
                // 开局三件套
                await this.rpgResetBattleAndApply({ doSamsaraResetLevel: false, doBroadcast: false });
                // 事件驱动刷新
                this.initRpgEventListeners();
            } else {
                // fallback：老流程
                this.loadSamsaraState();
                this.startKarmaPolling();
            }
        } catch (e) {
            console.warn('[RPG] 开局三件套失败，继续走默认 loadSamsaraState：', e);
            try { await this.loadSamsaraState(); } catch (e2) {}
            try { await this.loadLocalKarmaDetection(); } catch (e2) {}
        }
        // 兜底：若三件套失败，仍以 Samsara 服务器为准刷一次
        try { await this.loadSamsaraState(); } catch (e) {}
        try { await this.loadLocalKarmaDetection(); } catch (e) {}
        // 禁用轮询（事件驱动替代）
        this.stopKarmaPolling();

        this._initialized = true;
        this.dispatchEvent(new CustomEvent('ready', { bubbles: true, composed: true }));
    }

    async loadTokenStats() {
        try {
            const resp = await fetch(`${this.apiBase}/api/token_stats`);
            const data = await resp.json();

            this.shadowRoot.getElementById('token-total').textContent = data.total_tokens.toLocaleString();
            this.shadowRoot.getElementById('token-today').textContent = data.today_tokens.toLocaleString();
            this.shadowRoot.getElementById('token-calls').textContent = data.total_calls.toLocaleString();
            this.shadowRoot.getElementById('token-cost').textContent = `$${data.estimated_cost_usd.toFixed(4)}`;
        } catch (e) {
            console.error('Failed to load token stats:', e);
        }
    }

    async loadSamsaraState() {
        try {
            const resp = await fetch('/samsara/api/state');
            const data = await resp.json();
            this.samsaraState = data;
            this.updateSamsaraUI();
            await this.loadLevelInfo();
        } catch (e) {
            console.error('Failed to load samsara state:', e);
            this.samsaraState = {
                karma: 50,
                karma_max: 120,
                detection: 0,
                current_turn: 0,
                turn_limit: 20,
                objective: { type: 'capture_rat', description: '吃掉对方鼠' }
            };
            this.updateSamsaraUI();
        }
    }

    async loadLocalKarmaDetection() {
        try {
            const resp = await fetch(`${this.apiBase}/api/karma_detection`);
            const data = await resp.json();
            if (data.success) {
                // 合并而非覆盖，避免丢失其他字段导致 UI 闪烁
                this.samsaraState = {
                    ...this.samsaraState,
                    karma: data.karma?.current ?? this.samsaraState?.karma ?? 50,
                    karma_max: data.karma?.max ?? this.samsaraState?.karma_max ?? 120,
                    detection: data.detection ?? this.samsaraState?.detection ?? 0,
                };
                this.updateSamsaraUI();
            }
        } catch (e) {
            // 本地API可能不可用，忽略错误
        }
    }

    startKarmaPolling() {
        if (this._karmaPollingTimer) return;
        this._karmaPollingTimer = setInterval(() => {
            this.loadLocalKarmaDetection();
        }, 5000);
    }

    stopKarmaPolling() {
        if (this._karmaPollingTimer) {
            clearInterval(this._karmaPollingTimer);
            this._karmaPollingTimer = null;
        }
    }

    async loadLevelInfo() {
        try {
            const resp = await fetch('/samsara/api/levels');
            const data = await resp.json();
            this.levelInfo = data.current_level || null;
            this.updateLevelDisplay();
        } catch (e) {
            console.error('Failed to load level info:', e);
            this.levelInfo = null;
        }
    }

    updateLevelDisplay() {
        const levelBar = this.shadowRoot.getElementById('level-info-bar');
        if (!levelBar || !this.levelInfo) return;
        const typeLabels = { standard: '对弈', puzzle: '残局', objective: '目标', boss: 'Boss', sandbox: '沙盒' };
        const typeLabel = typeLabels[this.levelInfo.type] || this.levelInfo.type || '';
        const name = this.levelInfo.name || '';
        const desc = this.levelInfo.description || this.levelInfo.objective?.description || '';
        levelBar.innerHTML = `<span class="level-realm">${this.levelInfo.realm_name || ''}</span> > <span class="level-name">${name}</span> <span class="level-type-badge">${typeLabel}</span>`;
        if (desc) levelBar.title = desc;
    }

    updateSamsaraUI() {
        const state = this.samsaraState || {};
        const karma = state.karma ?? 50;
        const maxKarma = state.karma_max ?? 120;
        const detection = state.detection ?? 0;
        const currentTurn = state.current_turn ?? 0;
        const maxTurns = (this.levelInfo && this.levelInfo.turn_limit) || state.level_turn_limit || 20;
        const objectiveDesc = (this.levelInfo && this.levelInfo.objective && (this.levelInfo.objective.description || this.levelInfo.objective.text)) || (this.levelInfo && this.levelInfo.description) || (state.objective && state.objective.description) || '将死对方';
        const objective = { description: objectiveDesc };

        const karmaFill = this.shadowRoot.getElementById('karma-fill');
        const karmaValue = this.shadowRoot.getElementById('karma-value');
        const detectionFill = this.shadowRoot.getElementById('detection-fill');
        const detectionValue = this.shadowRoot.getElementById('detection-value');
        const turnFill = this.shadowRoot.getElementById('turn-fill');
        const turnValue = this.shadowRoot.getElementById('turn-value');
        const objectiveText = this.shadowRoot.getElementById('objective-text');

        if (karmaFill) karmaFill.style.width = `${(karma / maxKarma) * 100}%`;
        if (karmaValue) karmaValue.textContent = `${karma}/${maxKarma}`;
        if (detectionFill) detectionFill.style.width = `${detection}%`;
        if (detectionValue) detectionValue.textContent = `${Math.round(detection)}%`;
        if (turnFill) turnFill.style.width = `${(currentTurn / maxTurns) * 100}%`;
        if (turnValue) turnValue.textContent = `${currentTurn}/${maxTurns}`;
        if (objectiveText) objectiveText.textContent = objective.description || '吃掉对方鼠';
    }

    async consumeKarma(amount) {
        try {
            const resp = await fetch('/samsara/api/karma/consume', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ amount })
            });
            const data = await resp.json();
            this.samsaraState = data.state;
            this.updateSamsaraUI();
            // 检查是否被识破
            if (data.detection?.detected) {
                this.showDetectionReset(data.detection.message);
            }
            return data;
        } catch (e) {
            console.error('Failed to consume karma:', e);
            return { success: false };
        }
    }

    showDetectionReset(message) {
        const container = this.shadowRoot.getElementById('board-container');
        const existing = container.querySelector('.detection-reset-overlay');
        if (existing) existing.remove();

        const overlay = document.createElement('div');
        overlay.className = 'detection-reset-overlay';
        overlay.innerHTML = `
            <div class="detection-reset-card">
                <h2>👁️ 天道识破</h2>
                <p class="detection-message">${message || '妄改天规者，罚入轮回'}</p>
                <p class="detection-detail">存档已重置, 但技能与成就得以保留。</p>
                <button class="btn-primary">重新开始</button>
            </div>
        `;
        const restartBtn = overlay.querySelector('button');
        restartBtn.addEventListener('click', () => {
            overlay.remove();
            window.location.reload();
        });
        container.appendChild(overlay);
    }

    async reportKarmaEvent(eventType, details = {}) {
        try {
            const resp = await fetch('/samsara/api/karma/event', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ event_type: eventType, game_type: 'dongwuqi', details })
            });
            const data = await resp.json();
            // 合并而非覆盖，保留 objective / turn_limit 等字段
            if (data.state) {
                this.samsaraState = { ...this.samsaraState, ...data.state };
            }
            this.updateSamsaraUI();
            return data;
        } catch (e) {
            console.error('Failed to report karma event:', e);
        }
    }

    async incrementTurn() {
        try {
            const resp = await fetch('/samsara/api/turn/increment', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ game_type: 'dongwuqi' })
            });
            const data = await resp.json();
            if (data.state) {
                this.samsaraState = { ...this.samsaraState, ...data.state };
            }
            this.updateSamsaraUI();
            return data;
        } catch (e) {
            console.error('Failed to increment turn:', e);
        }
    }

    async loadConfigs() {
        const resp = await fetch(`${this.apiBase}/api/config/all`, { cache: 'no-store' });
        this.configs = await resp.json();
        this.boardState = this.configs.board_state;
        this.uiConfig = this.configs.ui_config;
        if (window.AchievementChecker) {
            AchievementChecker.checkAfterConfigLoad(this.configs, this.boardState, 'dongwuqi');
        }
    }
    _getBoardLayoutConfig() {
        const defaults = {
            grid: {
                line_thickness: 0.03,
                show_horizontal: true,
                show_vertical: true,
                river_gap: false
            },
            water: {
                enabled: true,
                fill_color: '#7fb3d5',
                fill_opacity: 0.45,
                wave_color: '#5499c7',
                text: '',
                text_size: 0.4
            },
            traps: {
                enabled: true,
                mark: 'cross',
                color: '#8b4513',
                opacity: 0.5
            },
            dens: {
                enabled: true,
                mark: 'star',
                red_color: '#cc0000',
                black_color: '#1a1a1a'
            },
            appearance: {
                background_color: '#f0d9b5',
                line_color: '#5c3a1e'
            },
            layout: {
                viewbox_padding_left: 0.5,
                viewbox_padding_right: 0.5,
                viewbox_padding_top: 0.5,
                viewbox_padding_bottom: 0.5,
                board_size: '90vmin'
            },
            decorations: {
                border: {
                    enabled: false,
                    thickness: 0.1,
                    color: null
                },
                custom_lines: [],
                background_pattern: null
            }
        };

        const user = this.configs.board?.appearance || {};

        const merge = (def, usr) => {
            if (!usr || typeof usr !== 'object') return def;
            const result = {};
            for (const key in def) {
                if (def[key] && typeof def[key] === 'object' && !Array.isArray(def[key])) {
                    result[key] = merge(def[key], usr[key]);
                } else {
                    result[key] = (usr[key] !== undefined) ? usr[key] : def[key];
                }
            }
            return result;
        };

        try {
            const merged = merge(defaults, user);
            merged.appearance = {
                background_color: user.background_color !== undefined
                    ? user.background_color : defaults.appearance.background_color,
                line_color: user.line_color !== undefined
                    ? user.line_color : defaults.appearance.line_color,
            };
            return merged;
        } catch (e) {
            console.error('Failed to merge board layout config:', e);
            return defaults;
        }
    }

    renderBoard() {
        const container = this.shadowRoot.getElementById('board-container');
        container.innerHTML = '';

        const layoutConfig = this._getBoardLayoutConfig();

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 7;
        const height = geometry.height || 9;
        const regions = geometry.regions || {};

        const bgColor = layoutConfig.appearance.background_color;
        container.style.backgroundColor = bgColor;
        this.style.setProperty('--board-bg', bgColor);

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.classList.add('board-grid');
        const padLeft = layoutConfig.layout.viewbox_padding_left;
        const padRight = layoutConfig.layout.viewbox_padding_right;
        const padTop = layoutConfig.layout.viewbox_padding_top;
        const padBottom = layoutConfig.layout.viewbox_padding_bottom;
        const viewBoxWidth = width + padLeft + padRight;
        const viewBoxHeight = height + padTop + padBottom;
        svg.setAttribute('viewBox', `-${padLeft} -${padTop} ${viewBoxWidth} ${viewBoxHeight}`);
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.style.width = '100%';
        svg.style.height = '100%';

        const lineColor = layoutConfig.appearance.line_color;
        const sw = String(layoutConfig.grid.line_thickness);
        const showHorizontal = layoutConfig.grid.show_horizontal;
        const showVertical = layoutConfig.grid.show_vertical;

        // 格子制网格线：水平线 y=0..height，垂直线 x=0..width
        if (showHorizontal) {
            for (let i = 0; i <= height; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', '0');
                line.setAttribute('y1', i);
                line.setAttribute('x2', width);
                line.setAttribute('y2', i);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }

        if (showVertical) {
            for (let i = 0; i <= width; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', i);
                line.setAttribute('y1', '0');
                line.setAttribute('x2', i);
                line.setAttribute('y2', height);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }

        // 水域装饰：蓝色半透明填充 + 波浪线
        if (layoutConfig.water?.enabled && regions.water?.cells) {
            const waterCfg = layoutConfig.water;
            regions.water.cells.forEach(([cx, cy]) => {
                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                rect.setAttribute('x', cx);
                rect.setAttribute('y', cy);
                rect.setAttribute('width', 1);
                rect.setAttribute('height', 1);
                rect.setAttribute('fill', waterCfg.fill_color);
                rect.setAttribute('fill-opacity', waterCfg.fill_opacity);
                svg.appendChild(rect);
                const wave = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                wave.setAttribute('d', `M ${cx + 0.15} ${cy + 0.5} q 0.175 -0.12 0.35 0 q 0.175 0.12 0.35 0`);
                wave.setAttribute('stroke', waterCfg.wave_color);
                wave.setAttribute('stroke-width', '0.03');
                wave.setAttribute('fill', 'none');
                wave.setAttribute('opacity', '0.6');
                svg.appendChild(wave);
            });
        }

        // 陷阱装饰：半透明填充 + 对角十字（陷阱已重构为棋子原语，从 board_state.pieces 读取）
        if (layoutConfig.traps?.enabled) {
            const trapsCfg = layoutConfig.traps;
            // 陷阱现在作为 category=terrain 的棋子存在，从棋子数组动态渲染
            // 已被吞噬的一次性陷阱（consumed_traps）不再显示，即使该格没有独立陷阱棋子也一并消失
            const consumedTrapKeys = new Set(
                (this.boardState?.consumed_traps || []).map(c => `${c[0]},${c[1]}`)
            );
            const trapPieces = (this.boardState?.pieces || []).filter(
                p => (p.type === 'trap' || p.category === 'terrain')
                    && p.is_alive
                    && !consumedTrapKeys.has(`${p.position[0]},${p.position[1]}`)
            );
            trapPieces.forEach(piece => {
                const [cx, cy] = piece.position;
                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                rect.setAttribute('x', cx);
                rect.setAttribute('y', cy);
                rect.setAttribute('width', 1);
                rect.setAttribute('height', 1);
                rect.setAttribute('fill', trapsCfg.color);
                rect.setAttribute('fill-opacity', trapsCfg.opacity);
                svg.appendChild(rect);
                const d1 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                d1.setAttribute('x1', cx + 0.2);
                d1.setAttribute('y1', cy + 0.2);
                d1.setAttribute('x2', cx + 0.8);
                d1.setAttribute('y2', cy + 0.8);
                d1.setAttribute('stroke', trapsCfg.color);
                d1.setAttribute('stroke-width', '0.04');
                svg.appendChild(d1);
                const d2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                d2.setAttribute('x1', cx + 0.8);
                d2.setAttribute('y1', cy + 0.2);
                d2.setAttribute('x2', cx + 0.2);
                d2.setAttribute('y2', cy + 0.8);
                d2.setAttribute('stroke', trapsCfg.color);
                d2.setAttribute('stroke-width', '0.04');
                svg.appendChild(d2);
            });
        }

        // 兽穴装饰：半透明填充 + 星标
        if (layoutConfig.dens?.enabled) {
            const densCfg = layoutConfig.dens;
            const denCells = [
                ...(regions.den_red?.cells || []).map(c => [...c, densCfg.red_color]),
                ...(regions.den_black?.cells || []).map(c => [...c, densCfg.black_color])
            ];
            denCells.forEach(([cx, cy, color]) => {
                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                rect.setAttribute('x', cx);
                rect.setAttribute('y', cy);
                rect.setAttribute('width', 1);
                rect.setAttribute('height', 1);
                rect.setAttribute('fill', color);
                rect.setAttribute('fill-opacity', '0.3');
                svg.appendChild(rect);
                const star = document.createElementNS('http://www.w3.org/2000/svg', 'text');
                star.setAttribute('x', cx + 0.5);
                star.setAttribute('y', cy + 0.5);
                star.setAttribute('text-anchor', 'middle');
                star.setAttribute('dominant-baseline', 'central');
                star.setAttribute('font-size', '0.6');
                star.setAttribute('fill', color);
                star.textContent = '★';
                svg.appendChild(star);
            });
        }

        if (layoutConfig.decorations.border.enabled) {
            const borderThickness = layoutConfig.decorations.border.thickness;
            const borderColor = layoutConfig.decorations.border.color || lineColor;
            const borderRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            const halfThick = borderThickness / 2;
            borderRect.setAttribute('x', -halfThick);
            borderRect.setAttribute('y', -halfThick);
            borderRect.setAttribute('width', width + borderThickness);
            borderRect.setAttribute('height', height + borderThickness);
            borderRect.setAttribute('fill', 'none');
            borderRect.setAttribute('stroke', borderColor);
            borderRect.setAttribute('stroke-width', String(borderThickness));
            svg.appendChild(borderRect);
        }

        container.appendChild(svg);

        this.applyUiConfig();
    }

    applyUiConfig() {
        const rotation = this.uiConfig?.layout?.rotation || 0;
        const container = this.shadowRoot.getElementById('board-container');
        if (container) {
            container.style.transform = `rotate(${rotation}deg)`;
            container.style.transition = 'transform 0.3s ease';
        }

        this.shadowRoot.querySelectorAll('.piece-text').forEach(el => {
            el.style.transform = `rotate(${-rotation}deg)`;
            el.style.transition = 'transform 0.3s ease';
        });

        const customStyleId = 'custom-ui-css';
        let styleEl = this.shadowRoot.getElementById(customStyleId);
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = customStyleId;
            this.shadowRoot.appendChild(styleEl);
        }
        styleEl.textContent = this.uiConfig?.custom_css || '';
    }

    renderPieces() {
        const container = this.shadowRoot.getElementById('board-container');
        container.querySelectorAll('.piece').forEach(el => el.remove());

        const piecesTheme = this.uiConfig?.theme?.pieces || {};
        const fontFamily = piecesTheme.font_family || "'KaiTi', serif";

        const pieces = this.boardState?.pieces || [];
        pieces.forEach(piece => {
            if (!piece.is_alive) return;
            this.createPieceElement(piece, piecesTheme, fontFamily);
        });

        if (this.lastMove) {
            const el = container.querySelector(`[data-piece-id="${this.lastMove.piece_id}"]`);
            if (el) el.classList.add('last-moved');
        }
    }

    createPieceElement(piece, theme, fontFamily) {
        const container = this.shadowRoot.getElementById('board-container');
        const el = document.createElement('div');
        el.className = `piece ${piece.side}`;
        el.dataset.pieceId = piece.id;

        const textSpan = document.createElement('span');
        textSpan.className = 'piece-text';
        textSpan.textContent = piece.name;
        el.appendChild(textSpan);

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 7;
        const height = geometry.height || 9;

        const [x, y] = piece.position;
        const pad = 0.5;
        const viewBoxW = width + pad * 2;
        const viewBoxH = height + pad * 2;
        const leftPct = ((x + 0.5 + pad) / viewBoxW) * 100;
        const topPct = ((y + 0.5 + pad) / viewBoxH) * 100;

        el.style.left = `${leftPct}%`;
        el.style.top = `${topPct}%`;
        el.style.transform = 'translate(-50%, -50%)';
        el.style.fontFamily = fontFamily;

        const pieceWidth = theme.width_pct || 10;
        const pieceHeight = theme.height_pct || 8;
        const fontSize = theme.font_size || '1.2rem';
        el.style.setProperty('--piece-width', `${pieceWidth}%`);
        el.style.setProperty('--piece-height', `${pieceHeight}%`);
        el.style.setProperty('--piece-font-size', fontSize);

        if (piece.side === 'red') {
            el.style.color = theme.red_color || '#cc0000';
            el.style.background = theme.red_bg || '#fff5e6';
            el.style.borderColor = theme.red_color || '#cc0000';
        } else {
            el.style.color = theme.black_color || '#1a1a1a';
            el.style.background = theme.black_bg || '#e6e6e6';
            el.style.borderColor = theme.black_color || '#1a1a1a';
        }

        if (piece.custom_properties) {
            const cp = piece.custom_properties;
            if (cp.color) el.style.color = cp.color;
            if (cp.bg) el.style.background = cp.bg;
            if (cp.font_size) el.style.fontSize = cp.font_size;
        }

        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (this.coordInsertMode) {
                const [gx, gy] = piece.position;
                this.onCoordDotClick(gx, gy);
                return;
            }
            this.onPieceClick(piece);
        });

        container.appendChild(el);
    }

    async onPieceClick(piece) {
        if (this.aiThinking) return;
        if (this.boardState?.game_status?.state === 'ended') return;

        if (this.selectedPiece && piece.side !== this.selectedPiece.side) {
            if (this.validMoves.some(m => m[0] === piece.position[0] && m[1] === piece.position[1])) {
                await this.executeMove(this.selectedPiece.id, piece.position);
                return;
            }
        }

        if (!this._isCurrentTurnPlayerControlled()) {
            if (this.selectedPiece) {
                this.clearSelection();
            }
            return;
        }

        if (piece.side !== this.boardState?.current_turn) {
            if (this.selectedPiece) {
                this.clearSelection();
            }
            return;
        }

        this.selectedPiece = piece;
        this.highlightPiece(piece);

        try {
            const resp = await fetch(`${this.apiBase}/api/valid_moves`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ piece_id: piece.id, to: piece.position })
            });
            const data = await resp.json();
            this.validMoves = data.moves || [];
            this.showValidMoves();
        } catch (e) {
            console.error('获取合法移动失败:', e);
            this._dispatchError('获取合法移动失败', e);
        }
    }

    highlightPiece(piece) {
        this.shadowRoot.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('selected');
        });
        const el = this.shadowRoot.querySelector(`[data-piece-id="${piece.id}"]`);
        if (el) el.classList.add('selected');
    }

    // 目标格是否为吃子（目标上站着敌方棋子）
    isCaptureTarget(x, y) {
        const pieces = this.boardState?.pieces || [];
        const movingSide = this.selectedPiece?.side;
        return pieces.some(p => p.is_alive !== false &&
            p.position[0] === x && p.position[1] === y &&
            p.side !== movingSide);
    }

    showValidMoves() {
        this.clearValidMoves();
        const container = this.shadowRoot.getElementById('board-container');
        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 7;
        const height = geometry.height || 9;
        const pad = 0.5;
        const viewBoxW = width + pad * 2;
        const viewBoxH = height + pad * 2;
        this.validMoves.forEach(([x, y]) => {
            const isCapture = this.isCaptureTarget(x, y);
            const indicator = document.createElement('div');
            indicator.className = isCapture ? 'valid-move-indicator capture-move-indicator' : 'valid-move-indicator';
            const leftPct = ((x + 0.5 + pad) / viewBoxW) * 100;
            const topPct = ((y + 0.5 + pad) / viewBoxH) * 100;
            indicator.style.left = `${leftPct}%`;
            indicator.style.top = `${topPct}%`;
            indicator.style.transform = 'translate(-50%, -50%)';
            indicator.style.cursor = 'pointer';
            indicator.style.pointerEvents = 'auto';

            const highlight = this.uiConfig?.theme?.highlight;
            if (highlight?.valid_move && !isCapture) {
                indicator.style.background = highlight.valid_move;
            }
            if (isCapture && highlight?.capture) {
                indicator.style.background = highlight.capture;
            }

            indicator.addEventListener('click', (e) => {
                e.stopPropagation();
                if (this.coordInsertMode) {
                    this.onCoordDotClick(x, y);
                    return;
                }
                if (this.selectedPiece) {
                    this.executeMove(this.selectedPiece.id, [x, y]);
                }
            });

            container.appendChild(indicator);
        });
    }

    clearValidMoves() {
        this.shadowRoot.querySelectorAll('.valid-move-indicator').forEach(el => el.remove());
    }

    clearSelection() {
        this.selectedPiece = null;
        this.validMoves = [];
        this.shadowRoot.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('selected');
        });
        this.clearValidMoves();
    }

    async executeMove(pieceId, toPosition) {
        this.clearSelection();
        this.shadowRoot.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('ai-moved');
        });
        this.aiThinking = true;

        try {
            const resp = await fetch(`${this.apiBase}/api/move`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ piece_id: pieceId, to: toPosition })
            });
            const data = await resp.json();

            if (data.success) {
                this.boardState = data.board_state;
                this.lastMove = this.boardState.move_history.slice(-1)[0];
                this.renderPieces();
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateMechanisms();
                this.loadTokenStats();
                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'dongwuqi');
                }

                await this.incrementTurn();

                const lastMoveData = this.boardState.move_history.slice(-1)[0];
                if (lastMoveData && lastMoveData.captured_piece) {
                    const captured = lastMoveData.captured_piece;
                    const movedPiece = this.boardState.pieces.find(p => p.id === lastMoveData.piece_id);
                    let eventType = 'capture_normal';
                    if (movedPiece && captured) {
                        const rankOrder = ['rat', 'cat', 'dog', 'wolf', 'leopard', 'tiger', 'lion', 'elephant'];
                        const movedRank = rankOrder.indexOf(movedPiece.type);
                        const capturedRank = rankOrder.indexOf(captured.type);
                        if (movedRank < capturedRank) {
                            eventType = 'capture_overrank';
                        }
                    }
                    await this.reportKarmaEvent(eventType, { piece: captured.type });
                } else {
                    await this.reportKarmaEvent('approach', {});
                }

                this._dispatchMoveEvent();

                if (this.boardState.game_status.state === 'ended') {
                    if (this.boardState.game_status.winner === 'red') {
                        await this.reportKarmaEvent('win');
                    }
                    this.showGameOver();
                    this._dispatchGameEndEvent();
                    this.aiThinking = false;
                    return;
                }

                // 仅当回合落到 AI 方时才请求 AI 走棋（跳过回合时回合不会交给 AI）。
                if (this._isCurrentTurnAITurn()) {
                    await this.sleep(800);
                    await this.makeAIMove();
                } else {
                    this.aiThinking = false;
                }
            } else {
                this.addMessage(data.message || '移动失败', 'error');
                if (data.ai_controlled && !this.aiThinking) {
                    this.aiThinking = true;
                    await this.sleep(500);
                    await this.makeAIMove();
                    this.aiThinking = false;
                }
            }
        } catch (e) {
            this.addMessage(`网络错误: ${e.message}`, 'error');
            this._dispatchError('移动失败', e);
        }

        this.aiThinking = false;
    }

    async makeAIMove(depth = 0) {
        if (depth > 10) return;

        this.addMessage('AI思考中...', 'info');
        if (typeof this.rpgShowThinking === 'function') this.rpgShowThinking();

        try {
            const resp = await fetch(`${this.apiBase}/api/ai_move`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({})
            });
            const data = await resp.json();
            if (typeof this.rpgHideThinking === 'function') this.rpgHideThinking();

            if (data.success) {
                this.boardState = data.board_state;
                this.lastMove = data.ai_move;
                this.renderPieces();
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateMechanisms();
                    if (window.AchievementChecker) {
                        AchievementChecker.checkAfterMove(this.boardState, this.configs, 'dongwuqi');
                    }

                this.highlightAIMovedPiece(data.ai_move.piece_id);

                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }

                if (data.ai_move?.captured) {
                    this.addMessage(`AI走了${this.getPieceName(data.ai_move.piece_id)}`, 'info');
                }

                this._dispatchMoveEvent();

                if (this.boardState.game_status.state === 'ended') {
                    this.showGameOver();
                    this._dispatchGameEndEvent();
                    return;
                }

                if (this._isCurrentTurnAITurn()) {
                    await this.sleep(600);
                    await this.makeAIMove(depth + 1);
                }
            } else {
                this.addMessage(data.message || 'AI移动失败', 'error');
            }
        } catch (e) {
            this.addMessage(`AI错误: ${e.message}`, 'error');
            if (typeof this.rpgHideThinking === 'function') this.rpgHideThinking();
            this._dispatchError('AI移动失败', e);
        }
    }

    _dispatchMoveEvent() {
        const lastMove = this.boardState?.move_history?.slice(-1)[0];
        const gameStatus = this.boardState?.game_status || {};
        const mover = this.boardState?.current_turn === 'red' ? 'black' : 'red';

        this.dispatchEvent(new CustomEvent('move', {
            bubbles: true,
            composed: true,
            detail: {
                captured: lastMove?.captured || null,
                mover: mover,
                is_check: gameStatus.is_check || false,
                game_ended: gameStatus.state === 'ended',
                winner: gameStatus.winner || null,
                is_five_in_a_row: false,
                go_captures: 0
            }
        }));
    }

    _dispatchGameEndEvent() {
        const gameStatus = this.boardState?.game_status || {};
        this.dispatchEvent(new CustomEvent('gameend', {
            bubbles: true,
            composed: true,
            detail: {
                winner: gameStatus.winner || null,
                win_condition: gameStatus.win_condition || null
            }
        }));
    }

    _dispatchError(message, error) {
        this.dispatchEvent(new CustomEvent('error', {
            bubbles: true,
            composed: true,
            detail: { message, error: error?.message || error }
        }));
    }

    _isCurrentTurnAITurn() {
        const currentTurn = this.boardState?.current_turn || 'red';
        if (this._isCurrentTurnAIControlled()) return true;
        if (!this._isCurrentTurnPlayerControlled()) return true;
        return false;
    }

    _isCurrentTurnAIControlled() {
        const mechanisms = this.boardState?.mechanisms || {};
        const currentTurn = this.boardState?.current_turn || 'red';
        const aiControl = mechanisms.ai_control || [];
        return aiControl.some(item => item.side === currentTurn && item.remaining !== 0);
    }

    _isCurrentTurnPlayerControlled() {
        const currentTurn = this.boardState?.current_turn || 'red';
        const mechanisms = this.boardState?.mechanisms || {};
        const playerControl = mechanisms.player_control || [];

        if (!playerControl || playerControl.length === 0) {
            return currentTurn === this.playerSide;
        }

        for (const item of playerControl) {
            const side = item.side;
            if (side === 'both') return true;
            if (side === currentTurn) return true;
        }

        return false;
    }

    highlightAIMovedPiece(pieceId) {
        this.shadowRoot.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('ai-moved');
        });
        const el = this.shadowRoot.querySelector(`[data-piece-id="${pieceId}"]`);
        if (el) {
            el.classList.add('ai-moved');
        }
    }

    async sendCommand(command) {
        if (!command.trim()) return;

        this.addMessage(`📝 你: ${command}`, 'user');

        this.showThinking('ChatAI 正在理解您的意图...', '意图解析');

        try {
            const resp = await fetch(`${this.apiBase}/api/command`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ command: command })
            });
            const data = await resp.json();

            this.hideThinking();

            // 主模式：把本次指令的「最终结果」写入 AI 面板
            this._appendCommandResult(data);

            if (data.success) {
                if (data.type === 'applied') {
                    // 显示实际消耗的业力（从后端返回）
                    if (data.karma_consumed) {
                        const overdraftMsg = data.is_overdraft ? ' (透支!)' : '';
                        this.addMessage(`✅ ${data.message} - 业力消耗: ${data.karma_consumed}${overdraftMsg}`, 'success');
                    } else if (data.estimated_karma_cost) {
                        this.addMessage(`✅ ${data.message} (估算业力: ${data.estimated_karma_cost})`, 'success');
                    } else {
                        this.addMessage(`✅ ${data.message}`, 'success');
                    }

                                        // 使用后端返回的状态更新 UI
                    if (data.karma_detection_state) {
                        const kd = data.karma_detection_state;
                        this.samsaraState = {
                            ...this.samsaraState,
                            karma: kd.karma?.current ?? kd.karma ?? this.samsaraState?.karma ?? 50,
                            karma_max: kd.karma?.max ?? kd.karma_max ?? this.samsaraState?.karma_max ?? 120,
                            detection: kd.detection?.current ?? kd.detection ?? this.samsaraState?.detection ?? 0,
                        };
                        this.updateSamsaraUI();
                        
                        if (data.detection?.detected) {
                            this.showDetectionReset(data.detection.message || '你被天道识破了！');
                        }
                    } else if (data.karma_state) {
                        this.samsaraState = {
                            ...this.samsaraState,
                            karma: data.karma_state.current ?? this.samsaraState?.karma ?? 50,
                            karma_max: data.karma_state.max ?? this.samsaraState?.karma_max ?? 120
                        };
                        if (data.detection !== undefined) {
                            this.samsaraState.detection = data.detection.current ?? data.detection ?? this.samsaraState?.detection ?? 0;
                        }
                        this.updateSamsaraUI();
                        
                        if (data.detection?.detected) {
                            this.showDetectionReset(data.detection.message || '你被天道识破了！');
                        }
                    } else {
                        // 刷新状态
                        await this.loadSamsaraState();
                    }
                    if (data.refresh_page) {
                        await this.sleep(500);
                        window.location.reload();
                        return;
                    }
                    await this.loadConfigs();
                    // AI 改规则后当前选中棋子的 validMoves 缓存已失效，必须清空选中态，
                    // 否则下一次点击会拿旧缓存判定合法性（规则改了却仍能走）。
                    this.clearSelection();
                    this.renderBoard();
                    this.renderPieces();
                    this.updateTurnIndicator();
                    this.updateActiveRules();
                    this.updateGameObjectives();
                    this.updateAIPersonality();
                    this.updateMechanisms();
                    this.loadTokenStats();

                    if (data.classification === 'A') {
                        this.triggerPersonalityChangeAnimation();
                    }

                    const gameStatus = this.boardState?.game_status;
                    if (gameStatus && gameStatus.state === 'ended') {
                        this._gameOverTimer = setTimeout(() => this.showGameOver(), 100);
                    }

                    if (this._isCurrentTurnAITurn() && !this.aiThinking) {
                        this.aiThinking = true;
                        await this.sleep(500);
                        await this.makeAIMove();
                        this.aiThinking = false;
                    }
                } else if (data.type === 'fun') {
                    this.addMessage(data.message, 'fun');
                    this.loadTokenStats();
                }
                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterCommand(data, command, this.configs, this.boardState, 'dongwuqi');
                }
            } else {
                if (data.type === 'rejected') {
                    this.addMessage(`❌ ${data.message}`, 'error');
                } else {
                    this.addMessage(`⚠️ ${data.message}`, 'error');
                }
            }
        } catch (e) {
            this.hideThinking();
            this.addMessage(`网络错误: ${e.message}`, 'error');
            this._dispatchError('发送指令失败', e);
        }
    }

    // ── 井字棋 mini game（不保存数据）──
    _initTicTacToe() {
        const board = this.shadowRoot.getElementById('ttt-board');
        if (!board) return;
        this.ttt = { cells: Array(9).fill(null), over: false };
        this.ttt.playerFirst = Math.random() < 0.5; // 随机先手
        this.ttt.turn = 'X'; // 玩家 X，AI O
        board.querySelectorAll('.ttt-cell').forEach(cell => {
            cell.addEventListener('click', () => this._tttCellClick(cell));
        });
        this.shadowRoot.getElementById('ttt-restart').addEventListener('click', () => this._tttReset());
        this._tttRender();
        if (!this.ttt.playerFirst) {
            this.shadowRoot.getElementById('ttt-status').textContent = 'AI 先手（O）';
            setTimeout(() => this._tttAiMove(), 500);
        } else {
            this.shadowRoot.getElementById('ttt-status').textContent = '你先手（X）';
        }
    }

    _tttReset() {
        this.ttt = { cells: Array(9).fill(null), over: false };
        this.ttt.playerFirst = Math.random() < 0.5;
        this.ttt.turn = 'X';
        this.shadowRoot.getElementById('ttt-status').textContent = '新一局 · 随机先手';
        this._tttRender();
        if (!this.ttt.playerFirst) setTimeout(() => this._tttAiMove(), 400);
    }

    _tttRender() {
        const board = this.shadowRoot.getElementById('ttt-board');
        if (!board) return;
        board.querySelectorAll('.ttt-cell').forEach((cell, i) => {
            cell.textContent = this.ttt.cells[i] || '';
            cell.classList.toggle('x', this.ttt.cells[i] === 'X');
            cell.classList.toggle('o', this.ttt.cells[i] === 'O');
            cell.classList.toggle('taken', !!this.ttt.cells[i]);
            cell.classList.toggle('over', this.ttt.over);
            cell.classList.remove('win');
        });
        if (this.ttt.winLine) {
            this.ttt.winLine.forEach(i => board.querySelectorAll('.ttt-cell')[i].classList.add('win'));
        }
    }

    _tttCellClick(cell) {
        if (this.ttt.over || this.ttt.turn !== 'X') return;
        const idx = Array.prototype.indexOf.call(this.shadowRoot.querySelectorAll('#ttt-board .ttt-cell'), cell);
        if (this.ttt.cells[idx]) return;
        this.ttt.cells[idx] = 'X';
        this.shadowRoot.getElementById('ttt-status').textContent = 'AI 思考中…';
        this._tttRender();
        if (!this._tttCheckGame()) {
            this.ttt.turn = 'O';
            setTimeout(() => this._tttAiMove(), 450);
        }
    }

    _tttAiMove() {
        if (this.ttt.over) return;
        const empty = this.ttt.cells.map((v, i) => v ? null : i).filter(i => i !== null);
        if (!empty.length) { this._tttCheckGame(); return; }
        let move = null;
        for (const i of empty) {
            const c = this.ttt.cells.slice(); c[i] = 'O';
            if (this._tttWinner(c)) { move = i; break; }
        }
        if (move === null) {
            for (const i of empty) {
                const c = this.ttt.cells.slice(); c[i] = 'X';
                if (this._tttWinner(c)) { move = i; break; }
            }
        }
        if (move === null) move = empty[Math.floor(Math.random() * empty.length)];
        this.ttt.cells[move] = 'O';
        this.ttt.turn = 'X';
        this.shadowRoot.getElementById('ttt-status').textContent = '轮到你（X）';
        this._tttRender();
        this._tttCheckGame();
    }

    _tttWinner(cells) {
        const lines = [[0,1,2],[3,4,5],[6,7,8],[0,3,6],[1,4,7],[2,5,8],[0,4,8],[2,4,6]];
        for (const [a,b,c] of lines) {
            if (cells[a] && cells[a] === cells[b] && cells[a] === cells[c]) return cells[a];
        }
        return null;
    }

    _tttCheckGame() {
        const w = this._tttWinner(this.ttt.cells);
        if (w) {
            this.ttt.over = true;
            const lines = [[0,1,2],[3,4,5],[6,7,8],[0,3,6],[1,4,7],[2,5,8],[0,4,8],[2,4,6]];
            for (const [a,b,c] of lines) {
                if (this.ttt.cells[a] === w && this.ttt.cells[a] === this.ttt.cells[b] && this.ttt.cells[a] === this.ttt.cells[c]) {
                    this.ttt.winLine = [a,b,c];
                }
            }
            this._tttRender();
            this.shadowRoot.getElementById('ttt-status').textContent = w === 'X' ? '你赢了！点击重新开始再来一局' : 'AI 获胜，点击重新开始再来一局';
            return true;
        }
        if (this.ttt.cells.every(v => v)) {
            this.ttt.over = true;
            this.ttt.winLine = null;
            this._tttRender();
            this.shadowRoot.getElementById('ttt-status').textContent = '平局！点击重新开始再来一局';
            return true;
        }
        return false;
    }

    showThinking(text, stage) {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        const textEl = this.shadowRoot.getElementById('thinking-text');
        const stageEl = this.shadowRoot.getElementById('thinking-stage');

        textEl.textContent = text;
        stageEl.textContent = `阶段: ${stage}`;
        overlay.classList.add('show');

        this.shadowRoot.getElementById('board-container').classList.add('board-locked');
        this.shadowRoot.getElementById('input-section')?.classList.add('input-locked');
        this.shadowRoot.getElementById('command-input').disabled = true;

        this.pollThinkingStatus();
    }

    hideThinking() {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        overlay.classList.remove('show');

        this.shadowRoot.getElementById('board-container').classList.remove('board-locked');
        this.shadowRoot.getElementById('input-section')?.classList.remove('input-locked');
        this.shadowRoot.getElementById('command-input').disabled = false;

        if (this.thinkingPollInterval) {
            clearInterval(this.thinkingPollInterval);
            this.thinkingPollInterval = null;
        }
    }

    async pollThinkingStatus() {
        this.thinkingPollInterval = setInterval(async () => {
            try {
                const resp = await fetch(`${this.apiBase}/api/thinking_status`);
                const data = await resp.json();

                if (data.thinking) {
                    const textEl = this.shadowRoot.getElementById('thinking-text');
                    const stageEl = this.shadowRoot.getElementById('thinking-stage');

                    if (data.stage === 'intent') {
                        textEl.textContent = 'ChatAI 正在理解您的意图...';
                        stageEl.textContent = '阶段: 意图解析';
                    } else if (data.stage === 'code') {
                        textEl.textContent = 'CodeAI 正在生成代码...';
                        stageEl.textContent = '阶段: 代码生成';
                    }
                }
            } catch (e) {
                console.error('轮询思考状态失败:', e);
            }
        }, 500);
    }

    async showLogs() {
        const modal = this.shadowRoot.getElementById('logs-modal');
        modal.classList.add('show');
        await this.refreshLogs();
    }

    hideLogs() {
        this.shadowRoot.getElementById('logs-modal').classList.remove('show');
    }

    async refreshLogs() {
        const container = this.shadowRoot.getElementById('logs-container');

        try {
            const resp = await fetch(`${this.apiBase}/api/logs?count=10`);
            const data = await resp.json();

            if (data.logs && data.logs.length > 0) {
                container.innerHTML = data.logs.map((log, index) => this.renderLogEntry(log, index)).join('');
            } else {
                container.innerHTML = '<div class="empty">暂无日志</div>';
            }
        } catch (e) {
            container.innerHTML = `<div class="error">加载日志失败: ${e.message}</div>`;
        }
    }

    renderLogEntry(log, index) {
        const intent = log.intent_analysis || {};
        const code = log.code_generation || {};
        const final = log.final_result || {};

        let statusClass = '';
        if (final.type === 'error' || log.errors?.length > 0) statusClass = 'error';
        else if (final.type === 'applied') statusClass = 'success';

        return `
            <div class="log-entry ${statusClass}">
                <div class="log-header">
                    <span>#${index + 1} ${log.timestamp || '未知时间'}</span>
                    <span>分类: ${log.classification || 'N/A'}</span>
                </div>
                <div class="log-user-input">👤 用户: ${this.escapeHtml(log.user_input || '')}</div>

                ${intent.success ? `
                <div class="log-section">
                    <div class="log-section-title">🤖 ChatAI 意图解析 (${intent.elapsed_time?.toFixed(2) || '?'}s)</div>
                    <div class="log-content">${this.escapeHtml(JSON.stringify(intent.parsed_result || {}, null, 2))}</div>
                </div>
                ` : intent.error ? `
                <div class="log-section">
                    <div class="log-section-title">🤖 ChatAI 意图解析 - 错误</div>
                    <div class="log-content" style="color: var(--danger)">${this.escapeHtml(intent.error)}</div>
                </div>
                ` : ''}

                ${code.success ? `
                <div class="log-section">
                    <div class="log-section-title">💻 CodeAI 代码生成 (${code.elapsed_time?.toFixed(2) || '?'}s)${code.patch_mode ? ` · ${code.patch_mode}` : ''}${code.patch_operations != null ? ` · ${code.patch_operations}项操作` : (code.diff_operations != null ? ` · ${code.diff_operations}项差异` : '')}</div>
                    <div class="log-content log-json">${this.renderCodeDiff(code)}</div>
                </div>
                ` : code.error ? `
                <div class="log-section">
                    <div class="log-section-title">💻 CodeAI 代码生成 - 错误</div>
                    <div class="log-content" style="color: var(--danger)">${this.escapeHtml(code.error)}</div>
                </div>
                ` : ''}

                ${code.validation ? (code.validation.success ? `
                <div class="log-section">
                    <div class="log-section-title">✅ 检查通过 (${code.validation.elapsed_time?.toFixed(2) || '?'}s)${code.validation.retry_count ? ` · 重试${code.validation.retry_count}次` : ''}</div>
                    ${code.validation.warnings?.length > 0 ? `
                    <div class="log-content" style="color: var(--warning)">
                        <strong>警告:</strong><br>
                        ${code.validation.warnings.map(w => this.escapeHtml(w)).join('<br>')}
                    </div>
                    ` : ''}
                </div>
                ` : `
                <div class="log-section">
                    <div class="log-section-title">⚠️ 检查失败 (${code.validation.elapsed_time?.toFixed(2) || '?'}s)${code.validation.retry_count ? ` · 重试${code.validation.retry_count}次` : ''}</div>
                    ${code.validation.errors?.length > 0 ? `
                    <div class="log-content" style="color: var(--danger)">
                        <strong>错误:</strong><br>
                        ${code.validation.errors.map(e => this.escapeHtml(e.message || e)).join('<br>')}
                    </div>
                    ` : ''}
                    ${code.validation.warnings?.length > 0 ? `
                    <div class="log-content" style="color: var(--warning)">
                        <strong>警告:</strong><br>
                        ${code.validation.warnings.map(w => this.escapeHtml(w)).join('<br>')}
                    </div>
                    ` : ''}
                </div>
                `) : ''}

                ${log.errors?.length > 0 ? `
                <div class="log-section">
                    <div class="log-section-title">⚠️ 错误</div>
                    <div class="log-content" style="color: var(--danger)">${log.errors.map(e => this.escapeHtml(e)).join('<br>')}</div>
                </div>
                ` : ''}

                <div class="log-section">
                    <div class="log-section-title">✅ 最终结果: ${final.type || 'unknown'}</div>
                    <div class="log-content">${this.escapeHtml(final.message || final.response || JSON.stringify(final, null, 2))}</div>
                </div>
            </div>
        `;
    }

    renderCodeDiff(code) {
        if (code.patch_operations_detail && Array.isArray(code.patch_operations_detail) && code.patch_operations_detail.length > 0) {
            return code.patch_operations_detail.map(op => {
                const opStr = op.op || '?';
                const pathStr = op.path || '';
                let valStr = '';
                if (op.value !== undefined) {
                    valStr = JSON.stringify(op.value);
                    if (valStr.length > 200) valStr = valStr.substring(0, 200) + '...';
                }
                return `<span style="color: var(--neon-magenta)">${this.escapeHtml(opStr)}</span> <span style="color: var(--neon-cyan)">${this.escapeHtml(pathStr)}</span>${valStr ? ` → ${this.escapeHtml(valStr)}` : ''}`;
            }).join('<br>');
        }
        if (code.diff_operations_detail && Array.isArray(code.diff_operations_detail) && code.diff_operations_detail.length > 0) {
            return code.diff_operations_detail.map(op => {
                const opStr = op.op || '?';
                const pathStr = op.path || '';
                let valStr = '';
                if (op.value !== undefined) {
                    valStr = JSON.stringify(op.value);
                    if (valStr.length > 200) valStr = valStr.substring(0, 200) + '...';
                }
                return `<span style="color: var(--neon-magenta)">${this.escapeHtml(opStr)}</span> <span style="color: var(--neon-cyan)">${this.escapeHtml(pathStr)}</span>${valStr ? ` → ${this.escapeHtml(valStr)}` : ''}`;
            }).join('<br>');
        }
        if (code.modified_sections_detail && typeof code.modified_sections_detail === 'object' && Object.keys(code.modified_sections_detail).length > 0) {
            return Object.entries(code.modified_sections_detail).map(([section, content]) => {
                let contentStr = typeof content === 'string' ? content : JSON.stringify(content);
                if (contentStr.length > 300) contentStr = contentStr.substring(0, 300) + '...';
                return `<span style="color: var(--neon-cyan)">[${this.escapeHtml(section)}]</span><br>${this.escapeHtml(contentStr)}`;
            }).join('<br><br>');
        }
        if (code.patch_apply_error) return `<span style="color: var(--danger)">Patch应用失败: ${this.escapeHtml(code.patch_apply_error)}</span>`;
        if (code.diff_apply_error) return `<span style="color: var(--danger)">Diff应用失败: ${this.escapeHtml(code.diff_apply_error)}</span>`;
        if (code.parse_error) return `<span style="color: var(--danger)">${this.escapeHtml(code.parse_error)}</span>`;
        if (code.raw_output) {
            let raw = code.raw_output;
            if (raw.length > 500) raw = raw.substring(0, 500) + '...';
            return `<span style="color: var(--text-light); opacity: 0.7">${this.escapeHtml(raw)}</span>`;
        }
        return '<span style="color: var(--text-light); opacity: 0.5">无代码修改片段</span>';
    }

    escapeHtml(text) {
        if (!text) return '';
        return text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    async clearLogs() {
        if (!confirm('确定要清空所有日志吗？')) return;

        try {
            const resp = await fetch(`${this.apiBase}/api/clear_logs`, { method: 'POST' });
            const data = await resp.json();
            if (data.success) {
                await this.refreshLogs();
            }
        } catch (e) {
            console.error('清空日志失败:', e);
        }
    }

    _appendCommandResult(data) {
        if (!data) return;
        let text = '';
        const fr = data.final_result;
        if (typeof fr === 'string' && fr) {
            text = fr;
        } else if (fr && typeof fr === 'object') {
            text = fr.message || fr.response || fr.reason || '';
            if (!text) text = JSON.stringify(fr);
        }
        if (!text && data.message) text = data.message;
        if (!text) return;
        const type = (data.type === 'applied' || data.type === 'fun' || data.success) ? 'success' : 'error';
        this.addMessage(`🔚 最终结果: ${text}`, type);
    }

    addMessage(text, type = 'info') {
        const container = this.shadowRoot.getElementById('ai-messages');
        const msg = document.createElement('div');
        msg.className = `message ${type}`;
        msg.textContent = text;
        container.appendChild(msg);
        container.scrollTop = container.scrollHeight;

        while (container.children.length > 50) {
            container.removeChild(container.firstChild);
        }
    }

    updateTurnIndicator() {
        const indicator = this.shadowRoot.getElementById('turn-indicator');
        const turn = this.boardState?.current_turn;
        const state = this.boardState?.game_status;

        if (state?.state === 'ended') {
            const winner = state.winner === 'red' ? '红方' : '黑方';
            indicator.textContent = `${winner}获胜!`;
            indicator.style.background = 'var(--success)';
        } else {
            indicator.textContent = turn === 'red' ? '红方回合' : '黑方回合';
            indicator.style.background = turn === 'red' ? '#8B0000' : '#333';
        }
    }

    updateActiveRules() {
        const container = this.shadowRoot.getElementById('active-rules');
        const rules = this.boardState?.game_status?.custom_rules_active || [];

        if (rules.length === 0) {
            container.innerHTML = '<span class="empty">暂无自定义规则</span>';
        } else {
            container.innerHTML = rules.map(r => `<div class="rule-item">✨ ${r}</div>`).join('');
        }
    }

    updateGameObjectives() {
        const container = this.shadowRoot.getElementById('game-objectives');
        if (!container) return;

        const winConditions = this.configs?.rules?.win_conditions || {};
        const gameStatus = this.boardState?.game_status;
        const achievedCondition = gameStatus?.win_condition;
        const isGameEnded = gameStatus?.state === 'ended';

        const entries = Object.entries(winConditions);
        if (entries.length === 0) {
            container.innerHTML = '<span class="empty">无游戏目标</span>';
            return;
        }

        const sorted = entries.sort((a, b) => {
            const pa = a[1].priority ?? 999;
            const pb = b[1].priority ?? 999;
            return pa - pb;
        });

        container.innerHTML = sorted.map(([key, cond]) => {
            const enabled = cond.enabled !== false;
            const achieved = isGameEnded && achievedCondition === key;
            const name = cond.display_name || key;
            const icon = cond.icon || '🎯';
            const desc = cond.description || '';
            const category = cond.category || 'victory';

            const statusText = achieved ? '已达成' : (enabled ? '进行中' : '未启用');
            const classes = ['objective-item'];
            if (enabled) classes.push('enabled');
            if (achieved) classes.push('achieved');
            if (!enabled) classes.push('disabled');

            return `
                <div class="${classes.join(' ')}">
                    <div class="objective-icon">${icon}</div>
                    <div class="objective-content">
                        ${category ? `<span class="objective-badge ${category}">${category === 'victory' ? '胜利' : category === 'draw' ? '平局' : '特殊'}</span>` : ''}
                        <div class="objective-title">${name}</div>
                        <div class="objective-desc">${desc}</div>
                        <div class="objective-status">${statusText}</div>
                    </div>
                </div>
            `;
        }).join('');
    }

    updateAIPersonality() {
        const card = this.shadowRoot.getElementById('ai-personality');
        if (!card) return;

        const personality = this.configs?.rules?.ai_difficulty?.personality || {};
        const type = personality.type || 'normal';
        const info = this._personalityInfo[type] || this._personalityInfo.custom;

        card.className = `personality-card type-${type}`;

        this.shadowRoot.getElementById('personality-icon').textContent = info.icon;
        this.shadowRoot.getElementById('personality-type').textContent = info.name;
        this.shadowRoot.getElementById('personality-subtitle').textContent = info.subtitle;
        this.shadowRoot.getElementById('personality-desc').textContent = info.desc;

        let agg = personality.aggressiveness ?? info.agg;
        let def = personality.conservatism ?? info.def;
        if (type === 'custom') {
            agg = personality.aggressiveness ?? 0.5;
            def = personality.conservatism ?? 0.5;
        }

        const totalBars = 10;
        const aggBars = Math.round(agg * totalBars);
        const defBars = Math.round(def * totalBars);

        this.shadowRoot.getElementById('bar-agg').textContent = '█'.repeat(aggBars) + '░'.repeat(totalBars - aggBars);
        this.shadowRoot.getElementById('bar-def').textContent = '█'.repeat(defBars) + '░'.repeat(totalBars - defBars);
        this.shadowRoot.getElementById('bar-agg-pct').textContent = `${Math.round(agg * 100)}%`;
        this.shadowRoot.getElementById('bar-def-pct').textContent = `${Math.round(def * 100)}%`;
    }

    triggerPersonalityChangeAnimation() {
        const card = this.shadowRoot.getElementById('ai-personality');
        if (!card) return;
        card.classList.remove('personality-changed');
        void card.offsetWidth;
        card.classList.add('personality-changed');
    }

    updateMechanisms() {
        const container = this.shadowRoot.getElementById('active-mechanisms');
        if (!container) return;

        const mechanisms = this.boardState?.mechanisms || {};
        const items = [];

        for (const [key, meta] of Object.entries(this._mechanismMeta)) {
            const list = mechanisms[key] || [];
            for (const item of list) {
                let sideLabel = item.side === 'red' ? '红方' : '黑方';
                if (item.side === 'both') sideLabel = '双方';

                const remaining = item.remaining ?? item.limit ?? 0;
                const reason = item.reason ? ` — ${item.reason}` : '';
                const isInfinite = remaining < 0;

                let isActive = remaining !== 0;
                if (key === 'player_control') {
                    isActive = true;
                }

                items.push({
                    type: meta.type,
                    icon: meta.icon,
                    text: `${sideLabel}${meta.label}${reason}`,
                    count: isInfinite ? '∞' : remaining,
                    unit: isInfinite ? '' : meta.unit,
                    mechanismKey: key,
                    side: item.side,
                    canStop: isActive,
                });
            }
        }

        if (items.length === 0) {
            container.innerHTML = '<span class="empty">无激活机制</span>';
        } else {
            container.innerHTML = items.map((item, idx) => `
                <div class="mechanism-badge type-${item.type}">
                    <span class="mechanism-icon">${item.icon}</span>
                    <span class="mechanism-text">${item.text}</span>
                    <span class="mechanism-count">${item.count}${item.unit}</span>
                    ${item.canStop ? `<button class="mechanism-stop" data-mechanism="${item.mechanismKey}" data-side="${item.side}">截停</button>` : ''}
                </div>
            `).join('');

            container.querySelectorAll('.mechanism-stop').forEach(btn => {
                btn.addEventListener('click', () => {
                    const mechanismType = btn.dataset.mechanism;
                    const side = btn.dataset.side;
                    this.stopMechanism(mechanismType, side);
                });
            });
        }

        const boardContainer = this.shadowRoot.getElementById('board-container');
        const hasFreeze = (mechanisms.skip_turns || []).length > 0;
        boardContainer.classList.toggle('freeze-effect', hasFreeze);

        const turnIndicator = this.shadowRoot.getElementById('turn-indicator');
        const currentTurn = this.boardState?.current_turn;
        const hasAIControl = (mechanisms.ai_control || []).some(m => m.side === currentTurn);
        turnIndicator.classList.toggle('ai-control', hasAIControl);
    }

    async stopMechanism(mechanismType, side) {
        try {
            const resp = await fetch(`${this.apiBase}/api/stop_mechanism`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ mechanism_type: mechanismType, side: side })
            });
            const data = await resp.json();
            if (data.success) {
                this.boardState = data.board_state;
                this.aiThinking = false;
                this.updateMechanisms();
                this.updateTurnIndicator();
                this.addMessage('⏹️ 机制已截停', 'success');
            }
        } catch (e) {
            this.addMessage(`截停失败: ${e.message}`, 'error');
        }
    }

    async refreshMechanisms() {
        try {
            const resp = await fetch(`${this.apiBase}/api/mechanisms`);
            const data = await resp.json();
            if (data.success && data.raw) {
                if (!this.boardState.mechanisms) {
                    this.boardState.mechanisms = {};
                }
                Object.assign(this.boardState.mechanisms, data.raw);
                this.updateMechanisms();
            }
        } catch (e) {
            console.error('Failed to refresh mechanisms:', e);
        }
    }

    async showGameOver() {
        // Bug2 修复：统一使用共享 RPG 胜负页三按钮 + resolve 奖励
        return this.showRpgGameOver();
    }

    showVictoryReward(rewards) {
        const container = this.shadowRoot.getElementById('board-container');
        const existing = container.querySelector('.victory-reward-overlay');
        if (existing) existing.remove();

        const skillPoints = rewards?.skill_points || 0;
        const reasons = rewards?.bonus_reasons || [];
        const sandboxUnlocked = rewards?.sandbox_unlocked;

        const reasonsHtml = reasons.map(r => `<li>${r}</li>`).join('');
        const sandboxHtml = sandboxUnlocked ? '<div class="reward-sandbox">🔓 沙盒模式已解锁！</div>' : '';

        const overlay = document.createElement('div');
        overlay.className = 'victory-reward-overlay';
        overlay.innerHTML = `
            <div class="reward-card">
                <h2>🏆 通关胜利</h2>
                <div class="reward-skill-points">⭐ +${skillPoints} 技能点</div>
                ${sandboxHtml}
                ${reasonsHtml ? `<ul class="reward-reasons">${reasonsHtml}</ul>` : ''}
                <button class="btn-primary">继续</button>
            </div>
        `;
        const continueBtn = overlay.querySelector('button');
        continueBtn.addEventListener('click', () => overlay.remove());
        container.appendChild(overlay);
    }

    async restart() {
        // Bug3 修复：重玩调用 RPG 三件套（apply_level_config + reset_battle + UI 重绘）
        return this.rpgRestart();
    }


    getPieceName(pieceId) {
        const piece = this.boardState?.pieces?.find(p => p.id === pieceId);
        return piece ? piece.name : pieceId;
    }

    sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    async checkApiKey() {
        const resp = await fetch(`${this.apiBase}/api/apikey/status`);
        const data = await resp.json();
        if (!data.has_key) {
            this.showSettings();
            this.addMessage('⚠️ 请先设置DeepSeek API Key', 'error');
        }
    }

    showSettings() {
        this.shadowRoot.getElementById('settings-modal').classList.add('show');
    }

    hideSettings() {
        this.shadowRoot.getElementById('settings-modal').classList.remove('show');
    }

    async saveSettings() {
        const apiKey = this.shadowRoot.getElementById('api-key-input').value;
        const difficulty = this.shadowRoot.getElementById('difficulty-select').value;

        if (apiKey) {
            const resp = await fetch(`${this.apiBase}/api/apikey`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ api_key: apiKey })
            });
            const data = await resp.json();
            if (data.success) {
                this.addMessage('✅ API Key已设置', 'success');
            }
        }

        const resp2 = await fetch(`${this.apiBase}/api/difficulty`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ difficulty: difficulty })
        });
        const data2 = await resp2.json();
        if (data2.success) {
            this.addMessage(`✅ 难度已设置为: ${difficulty}`, 'success');
        }

        this.hideSettings();
    }

    bindEvents() {
        const input = this.shadowRoot.getElementById('command-input');
        const sendBtn = this.shadowRoot.getElementById('btn-send');

        const sendCommand = () => {
            const cmd = input.value;
            if (cmd.trim()) {
                this.sendCommand(cmd);
                input.value = '';
            }
        };

        sendBtn.addEventListener('click', sendCommand);
        input.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') sendCommand();
        });

        this.shadowRoot.getElementById('btn-settings').addEventListener('click', () => {
            this.showSettings();
        });
        this.shadowRoot.getElementById('save-settings').addEventListener('click', () => {
            this.saveSettings();
        });
        this.shadowRoot.getElementById('close-settings').addEventListener('click', () => {
            this.hideSettings();
        });

        const btnAchievements = this.shadowRoot.getElementById('btn-achievements');
        if (btnAchievements) {
            btnAchievements.addEventListener('click', () => {
                window.open('/overworld', '_blank', 'noopener,noreferrer');
            });
        }

        this.shadowRoot.getElementById('btn-undo').addEventListener('click', async () => {
            const resp = await fetch(`${this.apiBase}/api/undo`, { method: 'POST' });
            const data = await resp.json();
            if (data.success) {
                this.boardState = data.board_state;
                this.lastMove = this.boardState.move_history.slice(-1)[0] || null;
                this.clearSelection();
                this.renderPieces();
                this.updateTurnIndicator();
                this.updateGameObjectives();
                this.addMessage(data.message, 'success');
            } else {
                this.addMessage(data.message, 'error');
            }
        });

        this.shadowRoot.getElementById('btn-undo-config').addEventListener('click', async () => {
            const resp = await fetch(`${this.apiBase}/api/undo_config`, { method: 'POST' });
            const data = await resp.json();
            if (data.success) {
                await this.loadConfigs();
                this.renderBoard();
                this.renderPieces();
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateAIPersonality();
                this.updateMechanisms();
                this.addMessage(data.message, 'success');
            } else {
                this.addMessage(data.message, 'error');
            }
        });

        this.shadowRoot.getElementById('btn-restart').addEventListener('click', () => {
            this.restart();
        });

        this.shadowRoot.getElementById('btn-reset-configs').addEventListener('click', async () => {
            if (!confirm('确定要重置所有配置吗？会同步重置六道关卡/业力（软重置：保留成就/技能），规则配置恢复默认。')) return;
            await this.rpgResetConfigsHandler('soft');
        });


        this.shadowRoot.getElementById('btn-logs').addEventListener('click', () => {
            this.showLogs();
        });
        this.shadowRoot.getElementById('close-logs').addEventListener('click', () => {
            this.hideLogs();
        });
        this.shadowRoot.getElementById('btn-refresh-logs').addEventListener('click', () => {
            this.refreshLogs();
        });
        this.shadowRoot.getElementById('btn-clear-logs').addEventListener('click', () => {
            this.clearLogs();
        });

        this.shadowRoot.getElementById('btn-insert-coord').addEventListener('click', () => {
            this.toggleCoordInsertMode();
        });

        this.shadowRoot.getElementById('btn-toggle-coord-mode').addEventListener('click', (e) => {
            e.stopPropagation();
            this.toggleSelectMode();
        });

        this._keydownHandler = (e) => {
            if (e.key === 'Escape' && this.coordInsertMode) {
                this.exitCoordInsertMode();
            }
        };
        document.addEventListener('keydown', this._keydownHandler);

        this.shadowRoot.getElementById('board-container').addEventListener('click', async (e) => {
            if (this.coordInsertMode) {
                return;
            }

            if (this.selectedPiece && this.validMoves.length > 0) {
                const [gridX, gridY] = this._getGridCoordsFromEvent(e);
                if (gridX === null) return;

                let isValidMove = this.validMoves.some(m => m[0] === gridX && m[1] === gridY);
                // 防御：AI 可能在选子期间改了规则，本地 validMoves 已过期。
                // 落子前向后端实时复核一次，避免用陈旧缓存放行已被规则禁止的走法。
                if (isValidMove) {
                    try {
                        const resp = await fetch(`${this.apiBase}/api/valid_moves`, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ piece_id: this.selectedPiece.id,
                                                   to: this.selectedPiece.position })
                        });
                        const data = await resp.json();
                        if (data.success && Array.isArray(data.moves)) {
                            this.validMoves = data.moves;
                            isValidMove = this.validMoves.some(m => m[0] === gridX && m[1] === gridY);
                        }
                    } catch (err) {
                        console.warn('落子前复核合法走法失败，沿用本地缓存:', err);
                    }
                }
                if (isValidMove) {
                    this.executeMove(this.selectedPiece.id, [gridX, gridY]);
                    return;
                }
            }
            this.clearSelection();
        });
    
        // 六道 RPG：跨页事件驱动刷新（替代轮询）
        if (typeof this.initRpgEventListeners === 'function') this.initRpgEventListeners();
}

    _getGridCoordsFromEvent(e) {
        const container = this.shadowRoot.getElementById('board-container');
        const rect = container.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 7;
        const height = geometry.height || 9;

        const boardX = (x / rect.width) * width;
        const boardY = (y / rect.height) * height;

        const gridX = Math.floor(boardX);
        const gridY = Math.floor(boardY);

        if (gridX < 0 || gridX >= width || gridY < 0 || gridY >= height) {
            return [null, null];
        }
        return [gridX, gridY];
    }

    toggleCoordInsertMode() {
        if (this.coordInsertMode) {
            this.exitCoordInsertMode();
        } else {
            this.enterCoordInsertMode();
        }
    }

    enterCoordInsertMode() {
        this.coordInsertMode = true;
        this.regionPoints = [];
        this.shadowRoot.getElementById('btn-insert-coord').classList.add('active');
        this.shadowRoot.getElementById('board-container').classList.add('coord-insert-mode');
        this.updateCoordButtonText();
        this.showCoordDots();
    }

    exitCoordInsertMode() {
        this.coordInsertMode = false;
        this.regionPoints = [];
        const btn = this.shadowRoot.getElementById('btn-insert-coord');
        if (btn) btn.classList.remove('active');
        const board = this.shadowRoot.getElementById('board-container');
        if (board) board.classList.remove('coord-insert-mode');
        this.hideCoordDots();
        this.clearRegionSelection();
    }

    updateCoordButtonText() {
        const btn = this.shadowRoot.getElementById('btn-insert-coord');
        if (!btn) return;
        if (this.selectMode === 'region') {
            btn.innerHTML = '🔲 选区域';
        } else {
            btn.innerHTML = '📍 选坐标';
        }
    }

    toggleSelectMode() {
        if (this.selectMode === 'coord') {
            this.selectMode = 'region';
        } else {
            this.selectMode = 'coord';
        }
        this.regionPoints = [];
        this.clearRegionSelection();
        this.updateCoordButtonText();
        if (this.coordInsertMode) {
            this.showCoordDots();
        }
    }

    showCoordDots() {
        this.hideCoordDots();
        const svg = this.shadowRoot.querySelector('#board-container svg.board-grid');
        if (!svg) return;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 7;
        const height = geometry.height || 9;

        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('id', 'coord-dots-group');
        g.style.pointerEvents = 'all';

        for (let x = 0; x < width; x++) {
            for (let y = 0; y < height; y++) {
                const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                dot.setAttribute('class', 'coord-dot');
                dot.setAttribute('cx', x + 0.5);
                dot.setAttribute('cy', y + 0.5);
                dot.setAttribute('r', 0.2);
                dot.setAttribute('fill', '#22c55e');
                dot.setAttribute('data-x', x);
                dot.setAttribute('data-y', y);
                dot.style.cursor = 'pointer';
                dot.style.pointerEvents = 'auto';

                dot.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const gx = parseInt(dot.getAttribute('data-x'));
                    const gy = parseInt(dot.getAttribute('data-y'));
                    this.onCoordDotClick(gx, gy);
                });

                g.appendChild(dot);
                this.coordDots.push(dot);
            }
        }

        svg.appendChild(g);
    }

    hideCoordDots() {
        this.coordDots.forEach(dot => dot.remove());
        this.coordDots = [];
    }

    onCoordDotClick(x, y) {
        if (this.selectMode === 'coord') {
            this.insertCoordToInput(x, y);
            this.exitCoordInsertMode();
        } else if (this.selectMode === 'region') {
            this.onRegionPointClick(x, y);
        }
    }

    onRegionPointClick(x, y) {
        if (this.regionPoints.length === 0) {
            this.regionPoints.push([x, y]);
            this.updateRegionVisual();
        } else if (this.regionPoints.length === 1) {
            const [px, py] = this.regionPoints[0];
            if (px === x && py === y) {
                return;
            }
            this.regionPoints.push([x, y]);
            this.updateRegionVisual();
            this.insertRegionToInput();
            setTimeout(() => {
                this.exitCoordInsertMode();
            }, 500);
        } else {
            this.regionPoints = [[x, y]];
            this.clearRegionSelection();
            this.updateRegionVisual();
        }
    }

    updateRegionVisual() {
        this.clearRegionVisual();
        if (this.regionPoints.length === 0) return;

        const svg = this.shadowRoot.querySelector('#board-container svg.board-grid');
        if (!svg) return;

        this.coordDots.forEach(dot => {
            const dx = parseInt(dot.getAttribute('data-x'));
            const dy = parseInt(dot.getAttribute('data-y'));
            const isSelected = this.regionPoints.some(p => p[0] === dx && p[1] === dy);
            if (isSelected) {
                dot.classList.add('selected');
                dot.setAttribute('r', 0.28);
                dot.setAttribute('fill', '#16a34a');
            } else {
                dot.classList.remove('selected');
                dot.setAttribute('r', 0.2);
                dot.setAttribute('fill', '#22c55e');
            }

            if (this.regionPoints.length === 2) {
                const [p1, p2] = this.regionPoints;
                const minX = Math.min(p1[0], p2[0]);
                const maxX = Math.max(p1[0], p2[0]);
                const minY = Math.min(p1[1], p2[1]);
                const maxY = Math.max(p1[1], p2[1]);
                if (dx >= minX && dx <= maxX && dy >= minY && dy <= maxY) {
                    dot.classList.add('in-region');
                }
            }
        });

        if (this.regionPoints.length === 2) {
            const [p1, p2] = this.regionPoints;
            const minX = Math.min(p1[0], p2[0]);
            const maxX = Math.max(p1[0], p2[0]);
            const minY = Math.min(p1[1], p2[1]);
            const maxY = Math.max(p1[1], p2[1]);

            const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            rect.setAttribute('class', 'region-rect');
            rect.setAttribute('x', minX - 0.4);
            rect.setAttribute('y', minY - 0.4);
            rect.setAttribute('width', (maxX - minX) + 0.8);
            rect.setAttribute('height', (maxY - minY) + 0.8);
            rect.setAttribute('rx', 0.1);
            svg.appendChild(rect);
            this._regionRectEl = rect;
        }
    }

    clearRegionVisual() {
        if (this._regionRectEl) {
            this._regionRectEl.remove();
            this._regionRectEl = null;
        }
        this.coordDots.forEach(dot => {
            dot.classList.remove('selected', 'in-region');
            dot.setAttribute('r', 0.2);
            dot.setAttribute('fill', '#22c55e');
        });
    }

    clearRegionSelection() {
        this.clearRegionVisual();
        this.regionPoints = [];
    }

    insertCoordToInput(x, y) {
        const input = this.shadowRoot.getElementById('command-input');
        const coordStr = `[${x}, ${y}]`;

        const start = input.selectionStart;
        const end = input.selectionEnd;
        const value = input.value;

        input.value = value.substring(0, start) + coordStr + value.substring(end);
        const newPos = start + coordStr.length;
        input.setSelectionRange(newPos, newPos);
        input.focus();
    }

    insertRegionToInput() {
        if (this.regionPoints.length < 2) return;
        const [p1, p2] = this.regionPoints;
        const minX = Math.min(p1[0], p2[0]);
        const maxX = Math.max(p1[0], p2[0]);
        const minY = Math.min(p1[1], p2[1]);
        const maxY = Math.max(p1[1], p2[1]);

        const input = this.shadowRoot.getElementById('command-input');
        const regionStr = `[${minX}, ${minY}]-[${maxX}, ${maxY}]`;

        const start = input.selectionStart;
        const end = input.selectionEnd;
        const value = input.value;

        input.value = value.substring(0, start) + regionStr + value.substring(end);
        const newPos = start + regionStr.length;
        input.setSelectionRange(newPos, newPos);
        input.focus();
    }

    async applyCheatPatch(modifiedConfigs) {
        try {
            // RPG 后端 cheat_use 已通过 /api/command 将配置应用到棋类后端
            // 这里只需重新拉取最新配置并重渲染
            await this.loadConfigs();
            this.renderBoard();
            this.renderPieces();
            this.updateTurnIndicator();
            this.updateActiveRules();
            this.updateGameObjectives();
            this.updateAIPersonality();
            this.updateMechanisms();
            return { success: true };
        } catch (e) {
            this._dispatchError('应用补丁失败', e);
            throw e;
        }
    }

    getBoardSnapshot() {
        return {
            boardState: this.boardState,
            configs: this.configs
        };
    }

    async resetBoard() {
        const resp = await fetch(`${this.apiBase}/api/reset_configs`, { method: 'POST' });
        const data = await resp.json();
        if (data.success) {
            await this.loadConfigs();
            this.lastMove = null;
            this.clearSelection();
            this.renderBoard();
            this.renderPieces();
            this.updateTurnIndicator();
            this.updateActiveRules();
            this.updateGameObjectives();
            this.updateAIPersonality();
            this.updateMechanisms();
        }
        return data;
    }

    destroy() {
        if (this._gameOverTimer) {
            clearTimeout(this._gameOverTimer);
            this._gameOverTimer = null;
        }
        if (this.thinkingPollInterval) {
            clearInterval(this.thinkingPollInterval);
            this.thinkingPollInterval = null;
        }
        if (this._keydownHandler) {
            document.removeEventListener('keydown', this._keydownHandler);
            this._keydownHandler = null;
        }
        this._initialized = false;
    }
}

if (!customElements.get('dongwuqi-board')) customElements.define('dongwuqi-board', DongwuqiBoard);

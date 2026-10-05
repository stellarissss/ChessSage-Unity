// ═══════════════════════════════════════════════════════════════
// 无限制跳棋 — 中国跳棋前端
// 基于六角星形SVG棋盘渲染，纯色圆形棋子
// ═══════════════════════════════════════════════════════════════

class CheckersBoard extends HTMLElement {
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
        this._pixelCoords = null;
        this._coordBounds = null;
        this._svgEl = null;

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
        return this.getAttribute('player-side') || 'black';
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
        this._updateRpgMode();
        this._initTicTacToe();
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
            <h1>无限制跳棋</h1>
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
                    <span class="samsara-value" id="karma-value">0/120</span>
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
                    <span class="samsara-value objective-text" id="objective-text">将所有棋子送入对方营区</span>
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
                    placeholder="输入指令，例如：让棋子可以跳过两个棋子、禁止连跳..."
                    autocomplete="off"
                >
                <div class="coord-select-btn-wrapper">
                    <button id="btn-insert-coord" class="btn" title="点击后在棋盘上选择位置，自动插入坐标到输入框">📍 选坐标</button>
                    <button id="btn-toggle-coord-mode" class="btn-toggle-mode" title="切换选坐标/选区域">⇄</button>
                </div>
                <button id="btn-send" class="btn-primary">发送</button>
            </div>
            <div class="hints">
                试试："让棋子可以跳过两个棋子"、"禁止连跳"、"棋子可以后退"、"把棋盘掀了"
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
    --paper: #0d0515;
    --paper-warm: #12081f;
    --paper-dark: #1a0a2e;
    --ink: #4ade80;
    --ink-soft: #2d4a1f;
    --ink-medium: #1a3210;
    --ink-light: #6b7280;
    --ink-faint: #374151;
    --line: #1a3210;
    --line-strong: #2d4a1f;

    --board-bg: #0d0515;
    --board-line: #2d4a1f;
    --red-piece: #ef4444;
    --black-piece: #4ade80;

    --neon-cyan: #4ade80;
    --neon-magenta: #a855f7;
    --neon-pink: #ef4444;
    --neon-green: #4ade80;
    --neon-gold: #fbbf24;

    --ghost-green: #4ade80;
    --ghost-green-dark: #2d4a1f;
    --ghost-purple: #1a0a2e;
    --ghost-gray: #6b7280;

    --highlight: #4ade80;
    --valid-move: #4ade80;
    --last-move: #a855f7;
    --danger: #ef4444;
    --success: #4ade80;
    --warning: #fbbf24;
    --text-light: #6b7280;

    --muted-ink: #6b7280;
    --accent-green: #4ade80;

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
        radial-gradient(ellipse at 20% 20%, rgba(26, 10, 46, 0.6) 0%, transparent 50%),
        radial-gradient(ellipse at 80% 80%, rgba(45, 74, 31, 0.3) 0%, transparent 50%),
        radial-gradient(ellipse at 50% 50%, rgba(74, 222, 128, 0.05) 0%, transparent 70%),
        url("data:image/svg+xml,%3Csvg viewBox='0 0 400 400' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='noiseFilter'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.8' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23noiseFilter)'/%3E%3C/svg%3E");
    background-repeat: repeat;
    background-size: cover, cover, cover, 200px 200px;
    background-blend-mode: normal, normal, normal, multiply;
    overflow: hidden;
    animation: ambientGlow 8s ease-in-out infinite;
}

/* Header */
.header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 24px;
    background: linear-gradient(180deg, rgba(13, 5, 21, 0.95) 0%, rgba(26, 10, 46, 0.8) 100%);
    border-bottom: 1px solid var(--line-strong);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.1s both;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
}

.header::after {
    content: '';
    position: absolute;
    left: 24px; right: 24px; bottom: -1px;
    height: 1px;
    background: linear-gradient(90deg, transparent, var(--ghost-green), transparent);
    transform: scaleX(0);
    transform-origin: left;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.4s forwards;
    box-shadow: 0 0 8px var(--ghost-green);
}

.header h1 {
    font-family: 'Playfair Display', Georgia, 'Resource Han Rounded CN', 'PingFang SC', serif;
    font-weight: 500;
    font-size: 1.5rem;
    letter-spacing: 0.02em;
    color: var(--ghost-green);
    font-style: italic;
    text-shadow: 0 0 10px rgba(74, 222, 128, 0.4);
}

.header h1::before {
    content: '饿 鬼 道';
    display: block;
    font-family: 'Playfair Display', serif;
    font-size: 0.65rem;
    font-weight: 400;
    letter-spacing: 0.3em;
    color: var(--ghost-gray);
    text-transform: uppercase;
    margin-bottom: 2px;
    font-style: normal;
    text-shadow: none;
}

.header-actions {
    display: flex;
    align-items: center;
    gap: 18px;
}

#turn-indicator {
    padding: 8px 20px;
    background: linear-gradient(135deg, var(--ghost-purple) 0%, var(--ghost-green-dark) 100%);
    color: var(--ghost-green);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    font-weight: 500;
    letter-spacing: 0.15em;
    text-transform: uppercase;
    border: 1px solid var(--ghost-green);
    position: relative;
    overflow: hidden;
    transition: all 0.3s cubic-bezier(0.22, 1, 0.36, 1);
    box-shadow: 0 0 15px rgba(74, 222, 128, 0.2);
}

#turn-indicator::before {
    content: '';
    position: absolute;
    top: 0; left: -100%;
    width: 100%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(74, 222, 128, 0.2), transparent);
    transition: left 0.5s ease;
}

#turn-indicator:hover::before {
    left: 100%;
}

#turn-indicator::after {
    content: '';
    position: absolute;
    inset: 0;
    background: radial-gradient(circle at 50% 50%, rgba(74, 222, 128, 0.1) 0%, transparent 70%);
    animation: ghostPulse 3s ease-in-out infinite;
}

.samsara-bar {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 16px;
    padding: 6px 24px;
    background: linear-gradient(135deg, #1a1a2e 0%, #16213e 50%, #0f3460 100%);
    border-bottom: 2px solid #e94560;
    box-shadow: 0 4px 20px rgba(233, 69, 96, 0.3);
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
    color: #fff;
    border-bottom: 1px solid rgba(233, 69, 96, 0.2);
}

.level-info-bar .level-realm {
    color: rgba(255, 255, 255, 0.6);
    font-size: 0.75rem;
}

.level-info-bar .level-name {
    font-weight: 600;
}

.level-info-bar .level-type-badge {
    display: inline-block;
    padding: 1px 8px;
    font-size: 0.65rem;
    background: rgba(233, 69, 96, 0.15);
    border: 1px solid rgba(233, 69, 96, 0.4);
    border-radius: 10px;
    color: #e94560;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.samsara-item {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 12px;
    background: rgba(255, 255, 255, 0.05);
    border-radius: 8px;
    border: 1px solid rgba(255, 255, 255, 0.1);
}

.samsara-icon {
    font-size: 1.2rem;
}

.samsara-info {
    display: flex;
    flex-direction: column;
    gap: 4px;
}

.samsara-label {
    font-size: 0.65rem;
    color: rgba(255, 255, 255, 0.6);
    text-transform: uppercase;
    letter-spacing: 0.1em;
}

.samsara-bar-container {
    width: 80px;
    height: 6px;
    background: rgba(255, 255, 255, 0.1);
    border-radius: 3px;
    overflow: hidden;
}

.samsara-bar-fill {
    height: 100%;
    border-radius: 3px;
    transition: width 0.3s ease;
}

.karma-fill {
    background: linear-gradient(90deg, #4ade80, #22c55e);
}

.detection-fill {
    background: linear-gradient(90deg, #fbbf24, #f97316, #ef4444);
}

.turn-fill {
    background: linear-gradient(90deg, #60a5fa, #3b82f6);
}

.samsara-value {
    font-size: 0.75rem;
    font-weight: 600;
    color: #fff;
    font-family: 'JetBrains Mono', monospace;
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

/* 跳棋棋盘容器 — 正方形比例，适配六角星形 */
#board-container {
    position: relative;
    width: calc(min(85vmin, 560px, calc(100vh - 200px)) * 0.75);
    height: calc(min(85vmin, 560px, calc(100vh - 200px)) * 0.75);
    background: linear-gradient(145deg, #0d0515 0%, #1a0a2e 50%, #0d0515 100%);
    border-radius: 8px;
    box-shadow:
        0 0 0 1px rgba(74, 222, 128, 0.1),
        0 0 30px rgba(74, 222, 128, 0.05),
        0 4px 20px rgba(0, 0, 0, 0.4),
        0 20px 60px rgba(0, 0, 0, 0.6),
        inset 0 1px 0 rgba(74, 222, 128, 0.05);
    border: 2px solid var(--ghost-green-dark);
}

#board-container::before {
    content: '';
    position: absolute;
    inset: 0;
    background-image:
        radial-gradient(ellipse at 30% 20%, rgba(45, 74, 31, 0.08) 0%, transparent 40%),
        radial-gradient(ellipse at 70% 80%, rgba(26, 10, 46, 0.12) 0%, transparent 40%),
        radial-gradient(ellipse at 50% 50%, rgba(74, 222, 128, 0.03) 0%, transparent 50%);
    border-radius: 8px;
    pointer-events: none;
}

#board-container::after {
    content: '';
    position: absolute;
    inset: 4px;
    border: 1px solid rgba(74, 222, 128, 0.08);
    border-radius: 6px;
    pointer-events: none;
    animation: boardGlow 6s ease-in-out infinite;
}

/* SVG棋盘 */
.board-grid {
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
}

/* 营区背景 */
.camp {
    opacity: 0.15;
}
.camp.red-camp {
    fill: #ef4444;
}
.camp.black-camp {
    fill: #4ade80;
}

/* 棋盘连接线 */
.board-line {
    stroke: var(--board-line);
    stroke-width: 0.025;
    opacity: 0.6;
    filter: drop-shadow(0 0 0.5px rgba(74, 222, 128, 0.3));
}

/* 位置圆点 */
.position-dot {
    fill: var(--board-line);
    opacity: 0.5;
    filter: drop-shadow(0 0 1px rgba(74, 222, 128, 0.2));
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
.side-panel::-webkit-scrollbar-track { background: rgba(26, 10, 46, 0.3); }
.side-panel::-webkit-scrollbar-thumb {
    background: var(--ghost-green-dark);
    border-radius: 2px;
}

.panel-section {
    position: relative;
    background: linear-gradient(145deg, rgba(26, 10, 46, 0.6) 0%, rgba(13, 5, 21, 0.8) 100%);
    border: 1px solid var(--line-strong);
    padding: 12px 16px;
    animation: fadeInUp 0.5s cubic-bezier(0.22, 1, 0.36, 1) both;
    backdrop-filter: blur(4px);
    -webkit-backdrop-filter: blur(4px);
}

.panel-section::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 24px; height: 24px;
    border-top: 1px solid var(--ghost-green);
    border-left: 1px solid var(--ghost-green);
    pointer-events: none;
    opacity: 0.5;
}

.panel-section::after {
    content: '';
    position: absolute;
    bottom: 0; right: 0;
    width: 24px; height: 24px;
    border-bottom: 1px solid var(--ghost-green);
    border-right: 1px solid var(--ghost-green);
    pointer-events: none;
    opacity: 0.5;
}

.panel-section:nth-child(1) { animation-delay: 0.25s; }
.panel-section:nth-child(2) { animation-delay: 0.32s; }
.panel-section:nth-child(3) { animation-delay: 0.39s; }
.panel-section:nth-child(4) { animation-delay: 0.46s; }
.panel-section:nth-child(5) { animation-delay: 0.53s; }
.panel-section:nth-child(6) { animation-delay: 0.60s; }
.panel-section:nth-child(7) { animation-delay: 0.67s; }

.panel-section h3 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1rem;
    margin-bottom: 14px;
    color: var(--ghost-green);
    letter-spacing: 0.02em;
    padding-bottom: 10px;
    border-bottom: 1px solid var(--line-strong);
    position: relative;
    font-style: italic;
    text-shadow: 0 0 8px rgba(74, 222, 128, 0.3);
}

.panel-section h3::before {
    content: attr(data-num);
    display: inline-block;
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.7rem;
    font-weight: 400;
    color: var(--ghost-gray);
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
.messages::-webkit-scrollbar-track { background: rgba(26, 10, 46, 0.3); }
.messages::-webkit-scrollbar-thumb { background: var(--ghost-green-dark); border-radius: 2px; }

.message {
    padding: 10px 0 10px 16px;
    margin-bottom: 4px;
    border-left: 2px solid var(--ghost-gray);
    color: var(--ghost-gray);
    position: relative;
    animation: slideInLeft 0.4s cubic-bezier(0.22, 1, 0.36, 1);
    transition: all 0.2s ease;
}

.message:hover {
    border-left-color: var(--ghost-green);
    background: rgba(74, 222, 128, 0.05);
    padding-left: 20px;
}

.message.success {
    border-left-color: var(--success);
    color: var(--ghost-green);
}

.message.success:hover {
    background: rgba(74, 222, 128, 0.1);
}

.message.error {
    border-left-color: var(--danger);
    color: #ef4444;
}

.message.error:hover {
    background: rgba(239, 68, 68, 0.1);
}

.message.fun {
    border-left-color: var(--ghost-green);
    color: var(--ghost-green);
    font-style: italic;
}

.message.fun:hover {
    background: rgba(74, 222, 128, 0.08);
}

/* Rules list */
.rules-list {
    font-size: 0.82rem;
    font-family: 'DM Sans', sans-serif;
    counter-reset: rule-counter;
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
    background: var(--line-strong);
    border: 1px solid var(--line-strong);
}

.token-stat {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    padding: 12px 14px;
    background: rgba(13, 5, 21, 0.8);
    transition: background 0.2s ease;
}

.token-stat:hover {
    background: rgba(45, 74, 31, 0.2);
}

.token-stat .stat-label {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: var(--ghost-gray);
    margin-bottom: 4px;
    font-weight: 500;
}

.token-stat .stat-value {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1.3rem;
    font-weight: 500;
    color: var(--ghost-green);
    font-style: italic;
    text-shadow: 0 0 8px rgba(74, 222, 128, 0.3);
}

.token-stat.cost {
    grid-column: span 2;
    background: var(--ghost-purple);
    color: var(--ghost-green);
}

.token-stat.cost:hover {
    background: rgba(45, 74, 31, 0.3);
}

.token-stat.cost .stat-label {
    color: rgba(107, 114, 128, 0.6);
}

.token-stat.cost .stat-value {
    color: var(--ghost-green);
    font-size: 1.5rem;
}

/* Controls */
.controls {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

/* 棋子 — SVG圆形纯色棋子 */
.piece {
    cursor: pointer;
    transition: all 0.2s ease;
    filter: drop-shadow(0 0.05px 0.05px rgba(0, 0, 0, 0.3));
}

.piece.red {
    fill: #cc0000;
    stroke: #660000;
    stroke-width: 0.04;
}

.piece.black {
    fill: #1a1a1a;
    stroke: #000000;
    stroke-width: 0.04;
}

.piece:hover {
    filter: drop-shadow(0 0.1px 0.1px rgba(0, 0, 0, 0.5)) brightness(1.12);
}

.piece.selected {
    stroke: var(--neon-cyan);
    stroke-width: 0.12;
    filter: drop-shadow(0 0 0.4px var(--neon-cyan)) drop-shadow(0 0 0.2px var(--neon-cyan));
}

.piece.last-moved {
    stroke: var(--neon-pink);
    stroke-width: 0.1;
    filter: drop-shadow(0 0 0.3px var(--neon-pink));
}

.piece.ai-moved {
    stroke: var(--neon-gold);
    stroke-width: 0.12;
    filter: drop-shadow(0 0 0.4px var(--neon-gold));
}

/* 合法移动标记 — SVG圆形 */
.valid-move-indicator {
    fill: rgba(74, 222, 128, 0.4);
    stroke: var(--ghost-green);
    stroke-width: 0.04;
    cursor: pointer;
    pointer-events: auto;
    transition: all 0.2s ease;
    filter: drop-shadow(0 0 0.2px rgba(74, 222, 128, 0.6));
}

.valid-move-indicator:hover {
    fill: rgba(74, 222, 128, 0.7);
    stroke-width: 0.06;
    filter: drop-shadow(0 0 0.4px rgba(74, 222, 128, 0.8));
}

/* Input section */
.input-section {
    padding: 10px 24px;
    background: linear-gradient(180deg, rgba(26, 10, 46, 0.8) 0%, rgba(13, 5, 21, 0.95) 100%);
    border-top: 1px solid var(--line-strong);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.5s both;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
}

.input-section::before {
    content: '';
    position: absolute;
    left: 24px; right: 24px; top: -1px;
    height: 1px;
    background: linear-gradient(90deg, transparent, var(--ghost-green), transparent);
    transform: scaleX(0);
    transform-origin: right;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.6s forwards;
    box-shadow: 0 0 8px var(--ghost-green);
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
    background: rgba(13, 5, 21, 0.8);
    color: var(--ghost-green);
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.88rem;
    letter-spacing: 0.01em;
    border-radius: 0;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

#command-input::placeholder {
    color: var(--ghost-gray);
    font-style: italic;
    font-family: 'DM Sans', sans-serif;
}

#command-input:focus {
    outline: none;
    border-color: var(--ghost-green);
    background: rgba(26, 10, 46, 0.8);
    box-shadow: 0 0 10px rgba(74, 222, 128, 0.2);
}

.hints {
    margin-top: 10px;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.72rem;
    color: var(--ghost-gray);
    letter-spacing: 0.03em;
    font-style: italic;
}

.hints::before {
    content: '☠  ';
    color: var(--ghost-gray);
    font-style: normal;
}

/* Buttons */
.btn {
    padding: 12px 20px;
    border: 1px solid var(--ghost-green);
    background: transparent;
    color: var(--ghost-green);
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.8rem;
    font-weight: 500;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    border-radius: 0;
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
    background: var(--ghost-green);
    transition: height 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    z-index: -1;
}

.btn:hover {
    color: var(--paper);
    box-shadow: 0 0 15px rgba(74, 222, 128, 0.3);
}

.btn:hover::before {
    height: 100%;
}

.btn-primary {
    padding: 12px 24px;
    border: none;
    background: var(--ghost-green);
    color: var(--paper);
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-size: 0.82rem;
    font-weight: 500;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    border-radius: 0;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
    overflow: hidden;
    box-shadow: 0 0 10px rgba(74, 222, 128, 0.3);
}

.btn-primary::after {
    content: '→';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.btn-primary:hover {
    background: rgba(74, 222, 128, 0.8);
    transform: translateY(-1px);
    box-shadow: 0 0 20px rgba(74, 222, 128, 0.5);
}

.btn-primary:hover::after {
    transform: translateX(4px);
}

.btn.danger {
    border-color: var(--danger);
    color: var(--danger);
}

.btn.danger::before {
    background: var(--danger);
}

.btn.danger:hover {
    color: var(--paper);
    box-shadow: 0 0 15px rgba(239, 68, 68, 0.3);
}

/* Modal */
.modal {
    display: none;
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(13, 5, 21, 0.8);
    backdrop-filter: blur(4px);
    -webkit-backdrop-filter: blur(4px);
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
    background: linear-gradient(145deg, rgba(26, 10, 46, 0.95) 0%, rgba(13, 5, 21, 0.98) 100%);
    padding: 36px 32px;
    width: 90%;
    max-width: 440px;
    border: 1px solid var(--ghost-green-dark);
    box-shadow:
        0 20px 60px rgba(0, 0, 0, 0.5),
        0 0 30px rgba(74, 222, 128, 0.1);
    animation: modalIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.modal-content::before {
    content: '';
    position: absolute;
    top: 12px; left: 12px; right: 12px; bottom: 12px;
    border: 1px solid rgba(74, 222, 128, 0.2);
    pointer-events: none;
}

.modal-content h3 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1.6rem;
    margin-bottom: 24px;
    color: var(--ghost-green);
    letter-spacing: 0.01em;
    font-style: italic;
    text-align: center;
    padding-bottom: 16px;
    border-bottom: 1px solid var(--line-strong);
    position: relative;
    text-shadow: 0 0 10px rgba(74, 222, 128, 0.4);
}

.modal-content h3::after {
    content: '';
    position: absolute;
    bottom: -1px; left: 50%;
    transform: translateX(-50%);
    width: 40px;
    height: 1px;
    background: var(--ghost-green);
    box-shadow: 0 0 8px var(--ghost-green);
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
    color: var(--ghost-gray);
    font-weight: 500;
}

.form-group input,
.form-group select {
    width: 100%;
    padding: 12px 14px;
    border: 1px solid var(--line-strong);
    border-radius: 0;
    background: rgba(13, 5, 21, 0.8);
    color: var(--ghost-green);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.88rem;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.form-group input:focus,
.form-group select:focus {
    outline: none;
    border-color: var(--ghost-green);
    background: rgba(26, 10, 46, 0.8);
    box-shadow: 0 0 10px rgba(74, 222, 128, 0.2);
}

.form-group small {
    display: block;
    margin-top: 6px;
    color: var(--ghost-gray);
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

@keyframes ambientGlow {
    0%, 100% { 
        filter: brightness(1);
    }
    50% { 
        filter: brightness(1.05);
    }
}

@keyframes ghostFloat {
    0%, 100% { 
        transform: translateY(0);
        filter: drop-shadow(0 0 0.1px rgba(74, 222, 128, 0.4));
    }
    50% { 
        transform: translateY(-2px);
        filter: drop-shadow(0 0 0.2px rgba(74, 222, 128, 0.6));
    }
}

@keyframes ghostPulse {
    0%, 100% { 
        opacity: 0.8;
        filter: drop-shadow(0 0 0.3px var(--ghost-green));
    }
    50% { 
        opacity: 1;
        filter: drop-shadow(0 0 0.6px var(--ghost-green)) drop-shadow(0 0 1px rgba(74, 222, 128, 0.5));
    }
}

@keyframes ghostGlow {
    0%, 100% { 
        filter: brightness(1) drop-shadow(0 0 0.2px rgba(74, 222, 128, 0.6));
    }
    50% { 
        filter: brightness(1.2) drop-shadow(0 0 0.4px rgba(74, 222, 128, 0.9));
    }
}

@keyframes boardGlow {
    0%, 100% { 
        opacity: 0.3;
        border-color: rgba(74, 222, 128, 0.08);
    }
    50% { 
        opacity: 0.6;
        border-color: rgba(74, 222, 128, 0.15);
    }
}

/* Game Over Overlay */
.game-over-overlay {
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(13, 5, 21, 0.95);
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    z-index: 50;
    border-radius: 8px;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
    animation: fadeIn 0.4s ease;
}

.game-over-overlay::before {
    content: '';
    position: absolute;
    top: 24px; left: 24px; right: 24px; bottom: 24px;
    border: 1px solid var(--ghost-green);
    pointer-events: none;
    opacity: 0.3;
}

.game-over-overlay::after {
    content: '';
    position: absolute;
    top: 32px; left: 32px; right: 32px; bottom: 32px;
    border: 1px solid var(--line-strong);
    pointer-events: none;
}

.game-over-overlay h2 {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 2.8rem;
    color: var(--ghost-green);
    margin-bottom: 12px;
    letter-spacing: 0.02em;
    font-style: italic;
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.2s both;
    text-shadow: 0 0 20px rgba(74, 222, 128, 0.5);
}

.game-over-overlay h2::before,
.game-over-overlay h2::after {
    content: '☠';
    display: inline-block;
    margin: 0 16px;
    font-weight: 300;
    color: var(--ghost-gray);
    font-style: normal;
    vertical-align: middle;
    font-size: 0.5em;
}

.game-over-overlay p {
    font-family: 'DM Sans', sans-serif;
    color: var(--ghost-gray);
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
    border: 1px solid var(--ghost-green);
    background: var(--ghost-green);
    color: var(--paper);
    cursor: pointer;
    font-family: 'DM Sans', sans-serif;
    font-weight: 500;
    font-size: 0.85rem;
    letter-spacing: 0.15em;
    text-transform: uppercase;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.4s both;
    box-shadow: 0 0 15px rgba(74, 222, 128, 0.3);
}

.game-over-overlay button::after {
    content: ' ↻';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.3s ease;
}

.game-over-overlay button:hover {
    background: rgba(74, 222, 128, 0.8);
    transform: translateY(-2px);
    box-shadow: 0 0 25px rgba(74, 222, 128, 0.5);
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
    background: rgba(13, 5, 21, 0.9);
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
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
    color: var(--ghost-green);
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
    color: var(--ghost-gray);
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
    border-color: var(--ghost-green);
    box-shadow: 0 0 10px var(--ghost-green);
}

.ttt-cell.taken { cursor: default; }
.ttt-cell.x { color: var(--ghost-green); text-shadow: 0 0 8px var(--ghost-green); }
.ttt-cell.o { color: #f87171; text-shadow: 0 0 8px rgba(248, 113, 113, 0.6); }
.ttt-cell.win { background: rgba(248, 113, 113, 0.18); box-shadow: inset 0 0 12px rgba(248, 113, 113, 0.4); }

.ttt-status {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ghost-gray);
    min-height: 1.2em;
    text-align: center;
}

.ttt-restart {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    letter-spacing: 0.05em;
    padding: 6px 18px;
    border: 1px solid var(--line-strong);
    border-radius: 20px;
    background: transparent;
    color: var(--ink);
    cursor: pointer;
    transition: all 0.15s ease;
}

.ttt-restart:hover {
    border-color: var(--ghost-green);
    color: var(--ghost-green);
    box-shadow: 0 0 10px var(--ghost-green);
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
    border-top-color: var(--ghost-green);
    border-right-color: var(--ghost-green);
    animation: spin 1.2s cubic-bezier(0.4, 0, 0.2, 1) infinite;
    box-shadow: 0 0 10px rgba(74, 222, 128, 0.5);
}

.thinking-spinner::after {
    inset: 12px;
    border-bottom-color: var(--ghost-gray);
    border-left-color: var(--ghost-gray);
    animation: spin 0.9s cubic-bezier(0.4, 0, 0.2, 1) infinite reverse;
}

.thinking-text {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-size: 1.4rem;
    margin-bottom: 8px;
    color: var(--ghost-green);
    letter-spacing: 0.02em;
    font-style: italic;
    text-shadow: 0 0 10px rgba(74, 222, 128, 0.4);
}

.thinking-stage {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.8rem;
    color: var(--ghost-gray);
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
    background: linear-gradient(145deg, rgba(26, 10, 46, 0.95) 0%, rgba(13, 5, 21, 0.98) 100%);
    border: 1px solid var(--ghost-green-dark);
    padding: 28px;
    box-shadow:
        0 20px 60px rgba(0, 0, 0, 0.5),
        0 0 30px rgba(74, 222, 128, 0.1);
    position: relative;
    animation: modalIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.logs-content::before {
    content: '';
    position: absolute;
    top: 10px; left: 10px; right: 10px; bottom: 10px;
    border: 1px solid rgba(74, 222, 128, 0.2);
    pointer-events: none;
}

.logs-container {
    flex: 1;
    overflow-y: auto;
    background: rgba(13, 5, 21, 0.8);
    padding: 20px 24px;
    margin: 16px 0;
    max-height: 65vh;
    border: 1px solid var(--line-strong);
    position: relative;
}

.logs-container::before {
    content: '';
    position: absolute;
    top: 0; left: 50px;
    width: 1px;
    height: 100%;
    background: var(--ghost-green-dark);
    opacity: 0.5;
}

.logs-container::-webkit-scrollbar { width: 5px; }
.logs-container::-webkit-scrollbar-track { background: rgba(26, 10, 46, 0.3); }
.logs-container::-webkit-scrollbar-thumb { background: var(--ghost-green-dark); border-radius: 2px; }

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
    color: var(--ghost-gray);
    letter-spacing: 0.03em;
}

.log-user-input {
    font-family: 'Playfair Display', Georgia, serif;
    font-weight: 500;
    font-style: italic;
    color: var(--ghost-green);
    margin-bottom: 12px;
    padding: 8px 0;
    font-size: 0.95rem;
    border-bottom: 1px solid var(--line-strong);
}

.log-user-input::before {
    content: '"';
    font-family: 'Playfair Display', serif;
    font-size: 1.2em;
    color: var(--ghost-gray);
    line-height: 0;
    vertical-align: -0.2em;
    margin-right: 2px;
}

.log-user-input::after {
    content: '"';
    font-family: 'Playfair Display', serif;
    font-size: 1.2em;
    color: var(--ghost-gray);
    line-height: 0;
    vertical-align: -0.2em;
    margin-left: 2px;
}

.log-section {
    margin-top: 12px;
    padding: 12px 14px;
    background: rgba(26, 10, 46, 0.6);
    border: 1px solid var(--line-strong);
}

.log-section-title {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.72rem;
    color: var(--ghost-gray);
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
    background: rgba(13, 5, 21, 0.9);
    padding: 10px 12px;
    color: var(--ghost-green);
    border: 1px solid var(--line-strong);
    line-height: 1.6;
}

.log-json {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.72rem;
    color: var(--ghost-gray);
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
    background: linear-gradient(135deg, var(--ghost-purple) 0%, rgba(13, 5, 21, 0.95) 100%);
    color: var(--ghost-green);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.85rem;
    border-left: 3px solid var(--ghost-green);
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3), 0 0 10px rgba(74, 222, 128, 0.1);
    animation: toastIn 0.4s cubic-bezier(0.22, 1, 0.36, 1);
    max-width: 320px;
    position: relative;
    border: 1px solid var(--ghost-green-dark);
}

.toast.success {
    border-left-color: var(--success);
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3), 0 0 15px rgba(74, 222, 128, 0.2);
}

.toast.error {
    border-left-color: var(--danger);
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3), 0 0 15px rgba(239, 68, 68, 0.2);
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

.btn.active {
    background: var(--ink);
    color: var(--paper);
}

.btn.active::before {
    height: 100%;
}

/* 坐标选择圆点 — SVG */
.coord-dot {
    cursor: pointer;
    pointer-events: auto;
    transition: all 0.15s ease;
    fill: #22c55e;
    opacity: 0.7;
}

.coord-dot:hover {
    opacity: 1;
    filter: drop-shadow(0 0 0.15px rgba(34, 197, 94, 0.9));
}

.coord-dot.selected {
    fill: #16a34a;
    opacity: 1;
    filter: drop-shadow(0 0 0.3px rgba(34, 197, 94, 1));
}

.coord-dot.in-region {
    fill: #86efac;
}

/* 区域矩形 — SVG */
.region-rect {
    pointer-events: none;
    fill: rgba(34, 197, 94, 0.12);
    stroke: #22c55e;
    stroke-width: 0.06;
}

/* 坐标选择按钮 */
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
    background: rgba(26, 10, 46, 0.6);
    border: 1px solid var(--line-strong);
    transition: all 0.4s cubic-bezier(0.22, 1, 0.36, 1);
    backdrop-filter: blur(4px);
    -webkit-backdrop-filter: blur(4px);
}

.personality-card::before {
    content: '';
    position: absolute;
    top: 6px; left: 6px; right: 6px; bottom: 6px;
    border: 1px solid rgba(74, 222, 128, 0.2);
    pointer-events: none;
    opacity: 0.6;
}

.personality-header {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 10px;
    padding-bottom: 10px;
    border-bottom: 1px dashed var(--line-strong);
}

.personality-icon {
    font-size: 1.6rem;
    width: 40px;
    height: 40px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(45, 74, 31, 0.2);
    border: 1px solid var(--ghost-green-dark);
    animation: ghostFloat 3s ease-in-out infinite;
    box-shadow: 0 0 10px rgba(74, 222, 128, 0.1);
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
    color: var(--ghost-green);
    letter-spacing: 0.02em;
    text-shadow: 0 0 8px rgba(74, 222, 128, 0.3);
}

.personality-subtitle {
    font-family: 'JetBrains Mono', monospace;
    font-size: 0.65rem;
    color: var(--ghost-gray);
    letter-spacing: 0.1em;
    text-transform: uppercase;
}

.personality-desc {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ghost-gray);
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
    color: var(--danger);
    text-shadow: 0 0 6px rgba(239, 68, 68, 0.4);
}

.bar-chars.defensive {
    color: var(--ghost-green);
    text-shadow: 0 0 6px rgba(74, 222, 128, 0.4);
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
                objective: { type: 'home', description: '将所有棋子送入对方营区' }
            };
            this.updateSamsaraUI();
        }
    }

    async loadLocalKarmaDetection() {
        try {
            const resp = await fetch(`${this.apiBase}/api/karma_detection`);
            const data = await resp.json();
            if (data.success) {
                this.samsaraState = {
                    ...this.samsaraState,
                    karma: data.karma?.current ?? 0,
                    karma_max: data.karma?.max ?? 120,
                    detection: data.detection ?? 0,
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
        const karma = state.karma || 0;
        const maxKarma = state.karma_max || 120;
        const detection = state.detection || 0;
        const currentTurn = state.current_turn || 0;
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
        if (objectiveText) objectiveText.textContent = objective.description || '将所有棋子送入对方营区';
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
                body: JSON.stringify({ event_type: eventType, game_type: 'tiaoqi', details })
            });
            const data = await resp.json();
            this.samsaraState = data.state;
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
                body: JSON.stringify({ game_type: 'tiaoqi' })
            });
            const data = await resp.json();
            this.samsaraState = data.state;
            this.updateSamsaraUI();
            return data;
        } catch (e) {
            console.error('Failed to increment turn:', e);
        }
    }

    async _reportJumpEvents(move) {
        if (!move) return;
        const startPos = move.from_position || [];
        const endPos = move.to_position || [];
        if (!startPos.length || !endPos.length) return;

        const jumpCount = move.jump_count || 0;
        const jumpedPieces = move.jumped_pieces || [];

        if (jumpCount >= 5) {
            await this.reportKarmaEvent('jump_5', { jump_count: jumpCount });
        } else if (jumpCount >= 3) {
            await this.reportKarmaEvent('jump_3', { jump_count: jumpCount });
        } else if (jumpCount >= 1) {
            await this.reportKarmaEvent('jump', { jump_count: jumpCount });
        }

        if (jumpedPieces.length >= 1) {
            await this.reportKarmaEvent('capture', { captured_count: jumpedPieces.length });
        }

        const playerSide = this.playerSide;
        const opponentCamp = playerSide === 'red' ? 'black-camp' : 'red-camp';
        const camps = this.configs.board?.geometry?.camps || {};

        for (const [side, campData] of Object.entries(camps)) {
            if ((playerSide === 'red' && side === 'black') || (playerSide === 'black' && side === 'red')) {
                const campPositions = campData.positions || [];
                const endKey = `${endPos[0]},${endPos[1]}`;
                const isInCamp = campPositions.some(p => `${p[0]},${p[1]}` === endKey);
                if (isInCamp) {
                    await this.reportKarmaEvent('home', { piece_id: move.piece_id });
                    break;
                }
            }
        }
    }

    async loadConfigs() {
        const resp = await fetch(`${this.apiBase}/api/config/all`, { cache: 'no-store' });
        this.configs = await resp.json();
        this.boardState = this.configs.board_state;
        this.uiConfig = this.configs.ui_config;
        if (window.AchievementChecker) {
            AchievementChecker.checkAfterConfigLoad(this.configs, this.boardState, 'tiaoqi');
        }
    }

    // 计算六角星棋盘所有位置的像素坐标
    // x = col / 2.0, y = row，保持横纵等距
    _calculatePixelCoords() {
        const boardConfig = this.configs.board || {};
        const positions = boardConfig.positions || {};
        const coords = {};

        let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;

        for (const key in positions) {
            const [row, col] = key.split(',').map(Number);
            const x = col / 2.0;
            const y = row;
            coords[key] = { x, y };
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        this._pixelCoords = coords;
        this._coordBounds = { minX, maxX, minY, maxY };
    }

    _getBoardLayoutConfig() {
        const boardConfig = this.configs.board || {};
        const appearance = boardConfig.appearance || {};
        return {
            appearance: {
                background_color: appearance.background_color || '#f5e6c8',
                line_color: appearance.line_color || '#5c3a1e',
            },
            grid: {
                line_thickness: appearance.grid?.line_thickness ?? 0.025,
                show_lines: appearance.grid?.show_lines !== false,
            }
        };
    }

    // 渲染六角星形SVG棋盘
    renderBoard() {
        const container = this.shadowRoot.getElementById('board-container');
        container.innerHTML = '';

        this._calculatePixelCoords();

        if (!this._pixelCoords || Object.keys(this._pixelCoords).length === 0) {
            console.error('无法计算棋盘坐标');
            return;
        }

        const layoutConfig = this._getBoardLayoutConfig();
        const boardConfig = this.configs.board || {};
        const bgColor = layoutConfig.appearance.background_color;

        container.style.backgroundColor = bgColor;
        this.style.setProperty('--board-bg', bgColor);

        const { minX, maxX, minY, maxY } = this._coordBounds;
        const padding = 0.8;
        const vbX = minX - padding;
        const vbY = minY - padding;
        const vbW = (maxX - minX) + 2 * padding;
        const vbH = (maxY - minY) + 2 * padding;

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.classList.add('board-grid');
        svg.setAttribute('viewBox', `${vbX} ${vbY} ${vbW} ${vbH}`);
        svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
        svg.style.width = '100%';
        svg.style.height = '100%';
        this._svgEl = svg;

        // 1. 渲染营区背景多边形
        this._renderCampPolygons(svg);

        // 2. 渲染连接线（基于邻接表）
        if (layoutConfig.grid.show_lines) {
            this._renderConnectionLines(svg);
        }

        // 3. 渲染位置圆点
        this._renderPositionDots(svg);

        container.appendChild(svg);
        this.applyUiConfig();
    }

    // 渲染营区背景三角形
    _renderCampPolygons(svg) {
        const camps = this.configs.board?.geometry?.camps || {};
        const lineColor = this._getBoardLayoutConfig().appearance.line_color;

        for (const [side, campData] of Object.entries(camps)) {
            const positions = campData.positions || [];
            if (positions.length === 0) continue;

            const points = this._getCampTrianglePoints(positions, side === 'red');
            if (!points) continue;

            const polygon = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
            polygon.setAttribute('class', `camp ${side}-camp`);
            polygon.setAttribute('points', points);
            svg.appendChild(polygon);
        }
    }

    // 获取营区三角形的顶点坐标字符串
    _getCampTrianglePoints(campPositions, isTopCamp) {
        if (campPositions.length === 0) return '';

        // 按行排序：上方营区从小到大，下方营区从大到小
        const sorted = [...campPositions].sort((a, b) => isTopCamp ? a[0] - b[0] : b[0] - a[0]);

        // 尖端是第一个位置（上方营区的最顶行或下方营区的最底行）
        const apex = sorted[0];

        // 底边行是最后一个位置所在行
        const baseRow = sorted[sorted.length - 1][0];
        const basePositions = sorted.filter(p => p[0] === baseRow);

        // 底边左右端点
        basePositions.sort((a, b) => a[1] - b[1]);
        const baseLeft = basePositions[0];
        const baseRight = basePositions[basePositions.length - 1];

        const apexCoord = this._pixelCoords[`${apex[0]},${apex[1]}`];
        const leftCoord = this._pixelCoords[`${baseLeft[0]},${baseLeft[1]}`];
        const rightCoord = this._pixelCoords[`${baseRight[0]},${baseRight[1]}`];

        if (!apexCoord || !leftCoord || !rightCoord) return '';

        return `${apexCoord.x},${apexCoord.y} ${leftCoord.x},${leftCoord.y} ${rightCoord.x},${rightCoord.y}`;
    }

    // 渲染连接线（基于邻接表，避免重复绘制）
    _renderConnectionLines(svg) {
        const adjacency = this.configs.board?.adjacency || {};
        const drawn = new Set();

        for (const key in adjacency) {
            const coord1 = this._pixelCoords[key];
            if (!coord1) continue;

            for (const neighbor of adjacency[key]) {
                const nkey = `${neighbor[0]},${neighbor[1]}`;
                const coord2 = this._pixelCoords[nkey];
                if (!coord2) continue;

                // 避免重复绘制（A-B 和 B-A）
                const edgeKey = [key, nkey].sort().join('|');
                if (drawn.has(edgeKey)) continue;
                drawn.add(edgeKey);

                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('class', 'board-line');
                line.setAttribute('x1', coord1.x);
                line.setAttribute('y1', coord1.y);
                line.setAttribute('x2', coord2.x);
                line.setAttribute('y2', coord2.y);
                svg.appendChild(line);
            }
        }
    }

    // 渲染位置圆点
    _renderPositionDots(svg) {
        for (const key in this._pixelCoords) {
            const coord = this._pixelCoords[key];
            const [row, col] = key.split(',').map(Number);

            const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            dot.setAttribute('class', 'position-dot');
            dot.setAttribute('cx', coord.x);
            dot.setAttribute('cy', coord.y);
            dot.setAttribute('r', '0.08');
            dot.setAttribute('data-row', row);
            dot.setAttribute('data-col', col);
            svg.appendChild(dot);
        }
    }

    applyUiConfig() {
        const rotation = this.uiConfig?.layout?.rotation || 0;
        const container = this.shadowRoot.getElementById('board-container');
        if (container) {
            container.style.transform = `rotate(${rotation}deg)`;
            container.style.transition = 'transform 0.3s ease';
        }

        const customStyleId = 'custom-ui-css';
        let styleEl = this.shadowRoot.getElementById(customStyleId);
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = customStyleId;
            this.shadowRoot.appendChild(styleEl);
        }
        styleEl.textContent = this.uiConfig?.custom_css || '';
    }

    // 渲染棋子（SVG纯色圆形）
    renderPieces() {
        const svg = this._svgEl;
        if (!svg) return;

        // 移除旧棋子
        svg.querySelectorAll('.piece').forEach(el => el.remove());

        const pieces = this.boardState?.pieces || [];
        pieces.forEach(piece => {
            if (!piece.is_alive) return;
            this.createPieceElement(piece);
        });

        // 标记最后移动的棋子
        if (this.lastMove) {
            const el = svg.querySelector(`[data-piece-id="${this.lastMove.piece_id}"]`);
            if (el) el.classList.add('last-moved');
        }
    }

    createPieceElement(piece) {
        const svg = this._svgEl;
        if (!svg) return;

        const [row, col] = piece.position;
        const key = `${row},${col}`;
        const coord = this._pixelCoords[key];
        if (!coord) return;

        const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        circle.setAttribute('class', `piece ${piece.side}`);
        circle.setAttribute('cx', coord.x);
        circle.setAttribute('cy', coord.y);
        circle.setAttribute('r', '0.38');
        circle.setAttribute('data-piece-id', piece.id);
        circle.setAttribute('data-row', row);
        circle.setAttribute('data-col', col);

        // 应用自定义属性
        if (piece.custom_properties) {
            const cp = piece.custom_properties;
            if (cp.color) circle.style.fill = cp.color;
        }

        circle.addEventListener('click', (e) => {
            e.stopPropagation();
            if (this.coordInsertMode) {
                this.onCoordDotClick(row, col);
                return;
            }
            this.onPieceClick(piece);
        });

        svg.appendChild(circle);
    }

    async onPieceClick(piece) {
        if (this.aiThinking) return;
        if (this.boardState?.game_status?.state === 'ended') return;

        // 如果已选中棋子且点击的是对方棋子（在跳棋中通常不会这样移动，但保持兼容）
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
                body: JSON.stringify({ piece_id: piece.id })
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
        const svg = this._svgEl;
        if (!svg) return;
        svg.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('selected');
        });
        const el = svg.querySelector(`[data-piece-id="${piece.id}"]`);
        if (el) el.classList.add('selected');
    }

    // 显示合法移动位置（SVG圆形标记）
    showValidMoves() {
        this.clearValidMoves();
        const svg = this._svgEl;
        if (!svg) return;

        this.validMoves.forEach(move => {
            const [row, col] = move;
            const key = `${row},${col}`;
            const coord = this._pixelCoords[key];
            if (!coord) return;

            const indicator = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            indicator.setAttribute('class', 'valid-move-indicator');
            indicator.setAttribute('cx', coord.x);
            indicator.setAttribute('cy', coord.y);
            indicator.setAttribute('r', '0.2');
            indicator.setAttribute('data-row', row);
            indicator.setAttribute('data-col', col);

            indicator.addEventListener('click', (e) => {
                e.stopPropagation();
                if (this.coordInsertMode) {
                    this.onCoordDotClick(row, col);
                    return;
                }
                if (this.selectedPiece) {
                    this.executeMove(this.selectedPiece.id, [row, col]);
                }
            });

            svg.appendChild(indicator);
        });
    }

    clearValidMoves() {
        const svg = this._svgEl;
        if (!svg) return;
        svg.querySelectorAll('.valid-move-indicator').forEach(el => el.remove());
    }

    clearSelection() {
        this.selectedPiece = null;
        this.validMoves = [];
        const svg = this._svgEl;
        if (svg) {
            svg.querySelectorAll('.piece').forEach(el => {
                el.classList.remove('selected');
            });
        }
        this.clearValidMoves();
    }

    async executeMove(pieceId, toPosition) {
        this.clearSelection();
        const svg = this._svgEl;
        if (svg) {
            svg.querySelectorAll('.piece').forEach(el => {
                el.classList.remove('ai-moved');
            });
        }
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

                this._dispatchMoveEvent();

                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'tiaoqi');
                }

                await this.incrementTurn();

                if (this.lastMove) {
                    this._reportJumpEvents(this.lastMove);
                }

                if (this.boardState.game_status.state === 'ended') {
                    if (this.boardState.game_status.winner === this.playerSide) {
                        await this.reportKarmaEvent('victory');
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

                this.highlightAIMovedPiece(data.ai_move.piece_id);

                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }

                if (data.ai_move?.captured) {
                    this.addMessage(`AI移动了棋子`, 'info');
                }

                this._dispatchMoveEvent();

                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'tiaoqi');
                }

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
        const svg = this._svgEl;
        if (!svg) return;
        svg.querySelectorAll('.piece').forEach(el => {
            el.classList.remove('ai-moved');
        });
        const el = svg.querySelector(`[data-piece-id="${pieceId}"]`);
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
                            karma: kd.karma?.current ?? kd.karma ?? 0,
                            karma_max: kd.karma?.max ?? kd.karma_max ?? 120,
                            detection: kd.detection?.current ?? kd.detection ?? 0,
                        };
                        this.updateSamsaraUI();
                        
                        if (data.detection?.detected) {
                            this.showDetectionReset(data.detection.message || '你被天道识破了！');
                        }
                    } else if (data.karma_state) {
                        this.samsaraState = {
                            ...this.samsaraState,
                            karma: data.karma_state.current,
                            karma_max: data.karma_state.max
                        };
                        if (data.detection !== undefined) {
                            this.samsaraState.detection = data.detection.current ?? data.detection;
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
                    AchievementChecker.checkAfterCommand(data, command, this.configs, this.boardState, 'tiaoqi');
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
        this._tttRender();
        if (!this.ttt.playerFirst) {
            this.shadowRoot.getElementById('ttt-status').textContent = '新一局 · AI 先手（O）';
            setTimeout(() => this._tttAiMove(), 400);
        } else {
            this.shadowRoot.getElementById('ttt-status').textContent = '新一局 · 你先手（X）';
        }
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
        // 轻量 AI：优先取胜/拦截，否则随机。
        const empty = this.ttt.cells.map((v, i) => v ? null : i).filter(i => i !== null);
        if (!empty.length) { this._tttCheckGame(); return; }
        let move = null;
        // 取胜
        for (const i of empty) {
            const c = this.ttt.cells.slice(); c[i] = 'O';
            if (this._tttWinner(c)) { move = i; break; }
        }
        // 拦截玩家
        if (move === null) {
            for (const i of empty) {
                const c = this.ttt.cells.slice(); c[i] = 'X';
                if (this._tttWinner(c)) { move = i; break; }
            }
        }
        // 随机
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
        this.shadowRoot.querySelector('.input-section')?.classList.add('input-locked');
        this.shadowRoot.getElementById('command-input').disabled = true;

        this.pollThinkingStatus();
    }

    hideThinking() {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        overlay.classList.remove('show');

        this.shadowRoot.getElementById('board-container').classList.remove('board-locked');
        this.shadowRoot.querySelector('.input-section')?.classList.remove('input-locked');
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

        // 棋盘点击：执行移动或清除选择
        this.shadowRoot.getElementById('board-container').addEventListener('click', async (e) => {
            if (this.coordInsertMode) {
                return;
            }

            if (this.selectedPiece && this.validMoves.length > 0) {
                const [row, col] = this._getGridCoordsFromEvent(e);
                if (row === null) {
                    this.clearSelection();
                    return;
                }

                let isValidMove = this.validMoves.some(m => m[0] === row && m[1] === col);
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
                            isValidMove = this.validMoves.some(m => m[0] === row && m[1] === col);
                        }
                    } catch (err) {
                        console.warn('落子前复核合法走法失败，沿用本地缓存:', err);
                    }
                }
                if (isValidMove) {
                    this.executeMove(this.selectedPiece.id, [row, col]);
                    return;
                }
            }
            this.clearSelection();
        });
    
        // 六道 RPG：跨页事件驱动刷新（替代轮询）
        if (typeof this.initRpgEventListeners === 'function') this.initRpgEventListeners();
}

    // 将鼠标事件转换为棋盘坐标 [row, col]
    // 使用SVG坐标变换，然后找最近的有效位置
    _getGridCoordsFromEvent(e) {
        const svg = this._svgEl;
        if (!svg || !this._pixelCoords) return [null, null];

        try {
            const pt = svg.createSVGPoint();
            pt.x = e.clientX;
            pt.y = e.clientY;
            const ctm = svg.getScreenCTM();
            if (!ctm) return [null, null];
            const svgP = pt.matrixTransform(ctm.inverse());

            // 找最近的有效位置
            let nearestKey = null;
            let minDist = Infinity;
            for (const key in this._pixelCoords) {
                const coord = this._pixelCoords[key];
                const dist = Math.sqrt((coord.x - svgP.x) ** 2 + (coord.y - svgP.y) ** 2);
                if (dist < minDist) {
                    minDist = dist;
                    nearestKey = key;
                }
            }

            // 只接受距离足够近的点击（0.5单位以内）
            if (minDist > 0.5) return [null, null];

            const [row, col] = nearestKey.split(',').map(Number);
            return [row, col];
        } catch (err) {
            return [null, null];
        }
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

    // 在所有有效位置上显示坐标选择圆点
    showCoordDots() {
        this.hideCoordDots();
        const svg = this._svgEl;
        if (!svg || !this._pixelCoords) return;

        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('id', 'coord-dots-group');
        g.style.pointerEvents = 'all';

        for (const key in this._pixelCoords) {
            const coord = this._pixelCoords[key];
            const [row, col] = key.split(',').map(Number);

            const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            dot.setAttribute('class', 'coord-dot');
            dot.setAttribute('cx', coord.x);
            dot.setAttribute('cy', coord.y);
            dot.setAttribute('r', '0.15');
            dot.setAttribute('data-row', row);
            dot.setAttribute('data-col', col);
            dot.style.cursor = 'pointer';
            dot.style.pointerEvents = 'auto';

            dot.addEventListener('click', (e) => {
                e.stopPropagation();
                this.onCoordDotClick(row, col);
            });

            g.appendChild(dot);
            this.coordDots.push(dot);
        }

        svg.appendChild(g);
    }

    hideCoordDots() {
        this.coordDots.forEach(dot => dot.remove());
        this.coordDots = [];
    }

    onCoordDotClick(row, col) {
        if (this.selectMode === 'coord') {
            this.insertCoordToInput(row, col);
            this.exitCoordInsertMode();
        } else if (this.selectMode === 'region') {
            this.onRegionPointClick(row, col);
        }
    }

    onRegionPointClick(row, col) {
        if (this.regionPoints.length === 0) {
            this.regionPoints.push([row, col]);
            this.updateRegionVisual();
        } else if (this.regionPoints.length === 1) {
            const [pr, pc] = this.regionPoints[0];
            if (pr === row && pc === col) {
                return;
            }
            this.regionPoints.push([row, col]);
            this.updateRegionVisual();
            this.insertRegionToInput();
            setTimeout(() => {
                this.exitCoordInsertMode();
            }, 500);
        } else {
            this.regionPoints = [[row, col]];
            this.clearRegionSelection();
            this.updateRegionVisual();
        }
    }

    updateRegionVisual() {
        this.clearRegionVisual();
        if (this.regionPoints.length === 0) return;

        const svg = this._svgEl;
        if (!svg) return;

        this.coordDots.forEach(dot => {
            const dr = parseInt(dot.getAttribute('data-row'));
            const dc = parseInt(dot.getAttribute('data-col'));
            const isSelected = this.regionPoints.some(p => p[0] === dr && p[1] === dc);
            if (isSelected) {
                dot.classList.add('selected');
                dot.setAttribute('r', '0.22');
            } else {
                dot.classList.remove('selected');
                dot.setAttribute('r', '0.15');
            }

            if (this.regionPoints.length === 2) {
                const [p1, p2] = this.regionPoints;
                const minR = Math.min(p1[0], p2[0]);
                const maxR = Math.max(p1[0], p2[0]);
                const minC = Math.min(p1[1], p2[1]);
                const maxC = Math.max(p1[1], p2[1]);
                if (dr >= minR && dr <= maxR && dc >= minC && dc <= maxC) {
                    dot.classList.add('in-region');
                }
            }
        });

        if (this.regionPoints.length === 2) {
            const [p1, p2] = this.regionPoints;
            const c1 = this._pixelCoords[`${p1[0]},${p1[1]}`];
            const c2 = this._pixelCoords[`${p2[0]},${p2[1]}`];
            if (c1 && c2) {
                const minX = Math.min(c1.x, c2.x);
                const maxX = Math.max(c1.x, c2.x);
                const minY = Math.min(c1.y, c2.y);
                const maxY = Math.max(c1.y, c2.y);

                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                rect.setAttribute('class', 'region-rect');
                rect.setAttribute('x', minX - 0.3);
                rect.setAttribute('y', minY - 0.3);
                rect.setAttribute('width', (maxX - minX) + 0.6);
                rect.setAttribute('height', (maxY - minY) + 0.6);
                rect.setAttribute('rx', '0.1');
                svg.appendChild(rect);
                this._regionRectEl = rect;
            }
        }
    }

    clearRegionVisual() {
        if (this._regionRectEl) {
            this._regionRectEl.remove();
            this._regionRectEl = null;
        }
        this.coordDots.forEach(dot => {
            dot.classList.remove('selected', 'in-region');
            dot.setAttribute('r', '0.15');
        });
    }

    clearRegionSelection() {
        this.clearRegionVisual();
        this.regionPoints = [];
    }

    // 插入坐标 [row, col] 到输入框
    insertCoordToInput(row, col) {
        const input = this.shadowRoot.getElementById('command-input');
        const coordStr = `[${row}, ${col}]`;

        const start = input.selectionStart;
        const end = input.selectionEnd;
        const value = input.value;

        input.value = value.substring(0, start) + coordStr + value.substring(end);
        const newPos = start + coordStr.length;
        input.setSelectionRange(newPos, newPos);
        input.focus();
    }

    // 插入区域 [row1, col1]-[row2, col2] 到输入框
    insertRegionToInput() {
        if (this.regionPoints.length < 2) return;
        const [p1, p2] = this.regionPoints;
        const minR = Math.min(p1[0], p2[0]);
        const maxR = Math.max(p1[0], p2[0]);
        const minC = Math.min(p1[1], p2[1]);
        const maxC = Math.max(p1[1], p2[1]);

        const input = this.shadowRoot.getElementById('command-input');
        const regionStr = `[${minR}, ${minC}]-[${maxR}, ${maxC}]`;

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

// 注册自定义元素
if (!customElements.get('checkers-board')) {
    customElements.define('checkers-board', CheckersBoard);
}

export class GoBoard extends HTMLElement {
    static get observedAttributes() {
        return ['api-base', 'rpg-mode', 'player-side'];
    }

    constructor() {
        super();
        this.configs = {};
        this.boardState = null;
        this.uiConfig = null;
        this.validMoves = [];
        this.lastMove = null;
        this.aiThinking = false;
        this.coordInsertMode = false;
        this.selectMode = 'coord';
        this.regionPoints = [];
        this.coordDots = [];
        this.thinkingPollInterval = null;
        this._outsideClickListener = null;
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
        const sidePanel = this.shadowRoot?.querySelector('.side-panel');
        const inputSection = this.shadowRoot?.querySelector('.input-section');
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
            <h1>无限制围棋</h1>
            <div class="header-actions">
                <span id="turn-indicator">黑方回合</span>
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
                    <span class="samsara-value objective-text" id="objective-text">击败对手</span>
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
                    <h3 data-num="03">提子统计</h3>
                    <div id="capture-stats" class="capture-stats">
                        <div class="capture-stat black">
                            <span class="stat-label">黑方提子</span>
                            <span class="stat-value" id="capture-black">0</span>
                        </div>
                        <div class="capture-stat white">
                            <span class="stat-label">白方提子</span>
                            <span class="stat-value" id="capture-white">0</span>
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
                    placeholder="输入指令，例如：禁用打劫规则、让AI激进一点..."
                    autocomplete="off"
                >
                <div class="coord-select-btn-wrapper">
                    <button id="btn-insert-coord" class="btn" title="点击后在棋盘上选择格子，自动插入坐标到输入框">📍 选坐标</button>
                    <button id="btn-toggle-coord-mode" class="btn-toggle-mode" title="切换选坐标/选区域">⇄</button>
                </div>
                <button id="btn-send" class="btn-primary">发送</button>
            </div>
            <div class="hints">
                试试："禁用打劫"、"启用黑棋禁手"、"让AI防守"、"给我额外一回合"
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
    --paper: #0f0a1e;
    --paper-warm: #1a1033;
    --paper-dark: #150d28;
    --ink: #e0e0ff;
    --ink-soft: #a0a0d0;
    --ink-medium: #7070a0;
    --ink-light: #505080;
    --ink-faint: #303050;
    --line: #2a2a4a;
    --line-strong: #3a3a5a;

    --board-bg: #0a0515;
    --board-line: #6366f1;
    --black-stone: #0a0a0a;
    --white-stone: #ffffff;

    --space-black: #0f0a1e;
    --cosmic-purple: #6366f1;
    --battle-red: #ef4444;
    --energy-blue: #06b6d4;

    --neon-cyan: #06b6d4;
    --neon-magenta: #a855f7;
    --neon-pink: #ec4899;
    --neon-green: #22d3ee;
    --neon-gold: #fbbf24;

    --highlight: #6366f1;
    --valid-move: #06b6d4;
    --last-move: #ef4444;
    --danger: #ef4444;
    --success: #06b6d4;
    --warning: #fbbf24;
    --text-light: #a0a0d0;

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
    background-color: var(--space-black);
    background-image:
        radial-gradient(ellipse at 20% 20%, rgba(99, 102, 241, 0.08) 0%, transparent 50%),
        radial-gradient(ellipse at 80% 80%, rgba(6, 182, 212, 0.06) 0%, transparent 50%),
        radial-gradient(ellipse at 50% 50%, rgba(239, 68, 68, 0.04) 0%, transparent 60%),
        url("data:image/svg+xml,%3Csvg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'%3E%3Ccircle cx='25' cy='25' r='0.5' fill='%23ffffff' opacity='0.3'/%3E%3Ccircle cx='75' cy='75' r='0.3' fill='%23ffffff' opacity='0.2'/%3E%3Ccircle cx='50' cy='30' r='0.4' fill='%236366f1' opacity='0.4'/%3E%3Ccircle cx='80' cy='20' r='0.2' fill='%2306b6d4' opacity='0.3'/%3E%3Ccircle cx='30' cy='80' r='0.3' fill='%23ef4444' opacity='0.2'/%3E%3Ccircle cx='60' cy='60' r='0.25' fill='%23ffffff' opacity='0.25'/%3E%3Ccircle cx='10' cy='50' r='0.35' fill='%23a855f7' opacity='0.3'/%3E%3Ccircle cx='90' cy='40' r='0.2' fill='%23ffffff' opacity='0.2'/%3E%3C/svg%3E");
    background-repeat: repeat;
    background-size: cover, cover, cover, 100px 100px;
    overflow: hidden;
}

#app::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: 
        linear-gradient(135deg, transparent 0%, rgba(99, 102, 241, 0.03) 50%, transparent 100%);
    animation: cosmicPulse 8s ease-in-out infinite;
    pointer-events: none;
    z-index: 0;
}

.header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 24px;
    background: rgba(15, 10, 30, 0.9);
    border-bottom: 1px solid rgba(99, 102, 241, 0.3);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.1s both;
    backdrop-filter: blur(8px);
}

.header::after {
    content: '';
    position: absolute;
    left: 24px; right: 24px; bottom: -1px;
    height: 1px;
    background: var(--cosmic-purple);
    transform: scaleX(0);
    transform-origin: left;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.4s forwards;
}

.header h1 {
    font-family: 'Playfair Display', Georgia, 'Resource Han Rounded CN', 'PingFang SC', serif;
    font-weight: 500;
    font-size: 1.5rem;
    letter-spacing: 0.02em;
    color: var(--ink);
    font-style: italic;
}

.header h1::before {
    content: '围 棋';
    display: block;
    font-family: 'Playfair Display', serif;
    font-size: 0.65rem;
    font-weight: 400;
    letter-spacing: 0.3em;
    color: var(--ink-light);
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
    background: var(--ink);
    color: var(--paper);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.75rem;
    font-weight: 500;
    letter-spacing: 0.15em;
    text-transform: uppercase;
    border: none;
    position: relative;
    overflow: hidden;
    transition: all 0.3s cubic-bezier(0.22, 1, 0.36, 1);
}

#turn-indicator::before {
    content: '';
    position: absolute;
    top: 0; left: -100%;
    width: 100%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(255,255,255,0.15), transparent);
    transition: left 0.5s ease;
}

#turn-indicator:hover::before {
    left: 100%;
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

.samsara-bar {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 16px;
    padding: 6px 24px;
    background: linear-gradient(135deg, #0f0a1e 0%, #1a1033 50%, #150d28 100%);
    border-bottom: 2px solid var(--cosmic-purple);
    box-shadow:
        0 4px 20px rgba(99, 102, 241, 0.3),
        0 0 30px rgba(99, 102, 241, 0.1);
}

.level-info-bar {
    flex-basis: 100%;
    width: 100%;
    text-align: center;
    font-size: 0.8rem;
    color: var(--ink-soft);
    padding: 2px 0;
}

.level-info-bar:empty {
    display: none;
}

.level-realm {
    color: var(--neon-magenta);
    font-weight: 600;
}

.level-name {
    color: var(--ink);
    font-weight: 600;
}

.level-type-badge {
    display: inline-block;
    padding: 1px 8px;
    background: rgba(99, 102, 241, 0.2);
    color: var(--neon-cyan);
    border-radius: 10px;
    font-size: 0.7rem;
    margin-left: 6px;
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
    width: calc(min(90vmin, 560px, calc(100vh - 180px)) * 0.75);
    height: calc(min(90vmin, 560px, calc(100vh - 180px)) * 0.75);
    background: var(--board-bg);
    border-radius: 4px;
    box-shadow:
        0 0 0 2px rgba(99, 102, 241, 0.3),
        0 0 30px rgba(99, 102, 241, 0.15),
        0 0 60px rgba(6, 182, 212, 0.08),
        0 4px 20px rgba(0, 0, 0, 0.3);
    overflow: hidden;
}

#board-container::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: 
        radial-gradient(circle at 50% 50%, rgba(99, 102, 241, 0.05) 0%, transparent 70%),
        url("data:image/svg+xml,%3Csvg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'%3E%3Ccircle cx='50' cy='50' r='0.8' fill='%23ffffff' opacity='0.6'/%3E%3Ccircle cx='150' cy='150' r='0.5' fill='%23ffffff' opacity='0.4'/%3E%3Ccircle cx='100' cy='80' r='0.6' fill='%236366f1' opacity='0.5'/%3E%3Ccircle cx='180' cy='40' r='0.4' fill='%2306b6d4' opacity='0.4'/%3E%3Ccircle cx='30' cy='160' r='0.5' fill='%23ef4444' opacity='0.3'/%3E%3Ccircle cx='120' cy='120' r='0.3' fill='%23ffffff' opacity='0.3'/%3E%3C/svg%3E");
    background-repeat: repeat;
    background-size: cover, 50px 50px;
    pointer-events: none;
    z-index: 1;
}

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
    background: rgba(15, 10, 30, 0.8);
    border: 1px solid rgba(99, 102, 241, 0.3);
    padding: 12px 16px;
    animation: fadeInUp 0.5s cubic-bezier(0.22, 1, 0.36, 1) both;
    backdrop-filter: blur(8px);
}

.panel-section::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 24px; height: 24px;
    border-top: 1px solid var(--cosmic-purple);
    border-left: 1px solid var(--cosmic-purple);
    pointer-events: none;
}

.panel-section::after {
    content: '';
    position: absolute;
    bottom: 0; right: 0;
    width: 24px; height: 24px;
    border-bottom: 1px solid var(--cosmic-purple);
    border-right: 1px solid var(--cosmic-purple);
    pointer-events: none;
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

.objectives-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.objectives-list .empty {
    color: var(--ink-light);
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
    color: var(--ink-light);
    line-height: 1.4;
}

.capture-stats {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1px;
    background: var(--line);
    border: 1px solid var(--line);
}

.capture-stat {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    padding: 12px 14px;
    background: rgba(99, 102, 241, 0.05);
    transition: background 0.2s ease;
}

.capture-stat:hover {
    background: rgba(99, 102, 241, 0.15);
}

.capture-stat.black {
    border-right: 1px solid rgba(99, 102, 241, 0.3);
}

.capture-stat .stat-label {
    font-family: 'DM Sans', sans-serif;
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: var(--ink-light);
    margin-bottom: 4px;
    font-weight: 500;
}

.capture-stat .stat-value {
    font-family: 'Playfair Display', Georgia, serif;
    font-size: 1.3rem;
    font-weight: 500;
    color: var(--ink);
    font-style: italic;
}

.controls {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.board-grid {
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
}

.stone {
    position: absolute;
    width: 4.2%;
    height: 4.2%;
    border-radius: 50%;
    cursor: pointer;
    user-select: none;
    transition: transform 0.2s, box-shadow 0.2s, filter 0.2s;
    z-index: 10;
}

.stone.black {
    background: radial-gradient(circle at 30% 30%, #2a2a4a, #0a0a0a, #000000);
    box-shadow:
        0 0 10px rgba(99, 102, 241, 0.3),
        0 0 20px rgba(99, 102, 241, 0.15),
        inset 0 0 8px rgba(99, 102, 241, 0.2),
        inset -2px -2px 4px rgba(0, 0, 0, 0.8);
    animation: blackHoleGlow 3s ease-in-out infinite;
}

.stone.black::after {
    content: '';
    position: absolute;
    top: 20%; left: 20%;
    width: 30%; height: 30%;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.6) 0%, transparent 70%);
    animation: darkMatterPulse 2s ease-in-out infinite;
}

.stone.white {
    background: radial-gradient(circle at 30% 30%, #ffffff, #e0f2fe, #a5f3fc);
    box-shadow:
        0 0 12px rgba(6, 182, 212, 0.6),
        0 0 24px rgba(6, 182, 212, 0.3),
        0 0 40px rgba(6, 182, 212, 0.15),
        inset 0 0 10px rgba(255, 255, 255, 0.9);
    animation: starGlow 2s ease-in-out infinite;
}

.stone.white::after {
    content: '';
    position: absolute;
    top: 15%; left: 15%;
    width: 25%; height: 25%;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(255, 255, 255, 0.9) 0%, transparent 60%);
    animation: energyPulse 1.5s ease-in-out infinite;
}

.stone:hover {
    transform: translate(-50%, -50%) scale(1.15);
    filter: brightness(1.2);
}

.stone.last-moved {
    box-shadow:
        0 0 0 3px var(--battle-red),
        0 0 20px rgba(239, 68, 68, 0.8),
        0 0 40px rgba(239, 68, 68, 0.4),
        0 0 60px rgba(239, 68, 68, 0.2);
    animation: battlePulse 0.8s ease-in-out infinite;
}

.valid-move-indicator {
    position: absolute;
    width: 3%;
    height: 3%;
    border-radius: 50%;
    background: radial-gradient(circle, var(--energy-blue) 0%, rgba(6, 182, 212, 0.5) 50%, transparent 100%);
    box-shadow: 
        0 0 10px var(--energy-blue), 
        0 0 20px rgba(6, 182, 212, 0.6),
        0 0 30px rgba(6, 182, 212, 0.3);
    opacity: 0.85;
    pointer-events: none;
    z-index: 5;
    animation: energyOrb 1.5s ease-in-out infinite;
}

.valid-move-indicator::before {
    content: '';
    position: absolute;
    top: 50%; left: 50%;
    transform: translate(-50%, -50%);
    width: 100%; height: 100%;
    border-radius: 50%;
    border: 1px solid var(--energy-blue);
    animation: energyRing 1.5s ease-out infinite;
}

.input-section {
    padding: 10px 24px;
    background: rgba(15, 10, 30, 0.9);
    border-top: 1px solid rgba(99, 102, 241, 0.3);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.5s both;
    backdrop-filter: blur(8px);
}

.input-section::before {
    content: '';
    position: absolute;
    left: 24px; right: 24px; top: -1px;
    height: 1px;
    background: var(--cosmic-purple);
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
    border: 1px solid rgba(99, 102, 241, 0.4);
    background: rgba(15, 10, 30, 0.6);
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
    border-color: var(--cosmic-purple);
    background: rgba(15, 10, 30, 0.8);
    box-shadow: 0 0 15px rgba(99, 102, 241, 0.3);
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

.btn {
    padding: 12px 20px;
    border: 1px solid var(--cosmic-purple);
    background: rgba(99, 102, 241, 0.1);
    color: var(--ink);
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
    background: var(--cosmic-purple);
    transition: height 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    z-index: -1;
}

.btn:hover {
    color: #ffffff;
    box-shadow: 0 0 15px rgba(99, 102, 241, 0.4);
}

.btn:hover::before {
    height: 100%;
}

.btn-primary {
    padding: 12px 24px;
    border: none;
    background: linear-gradient(135deg, var(--cosmic-purple), var(--energy-blue));
    color: #ffffff;
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
}

.btn-primary::after {
    content: '→';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.25s cubic-bezier(0.22, 1, 0.36, 1);
}

.btn-primary:hover {
    box-shadow: 
        0 0 20px rgba(99, 102, 241, 0.5),
        0 0 40px rgba(6, 182, 212, 0.3);
    transform: translateY(-1px);
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
}

.modal {
    display: none;
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(15, 10, 30, 0.8);
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
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
    background: rgba(15, 10, 30, 0.95);
    padding: 36px 32px;
    width: 90%;
    max-width: 440px;
    border: 1px solid rgba(99, 102, 241, 0.4);
    box-shadow:
        0 0 40px rgba(99, 102, 241, 0.2),
        0 20px 60px rgba(0, 0, 0, 0.3);
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

.game-over-overlay {
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(15, 10, 30, 0.95);
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    z-index: 50;
    border-radius: 4px;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
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

.thinking-overlay {
    display: none;
    position: absolute;
    top: 0; left: 0;
    width: 100%; height: 100%;
    background: rgba(15, 10, 30, 0.9);
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

@keyframes cosmicPulse {
    0%, 100% { opacity: 0.3; }
    50% { opacity: 0.8; }
}

@keyframes blackHoleGlow {
    0%, 100% { 
        box-shadow:
            0 0 10px rgba(99, 102, 241, 0.3),
            0 0 20px rgba(99, 102, 241, 0.15),
            inset 0 0 8px rgba(99, 102, 241, 0.2),
            inset -2px -2px 4px rgba(0, 0, 0, 0.8);
    }
    50% { 
        box-shadow:
            0 0 15px rgba(99, 102, 241, 0.5),
            0 0 30px rgba(99, 102, 241, 0.25),
            inset 0 0 12px rgba(99, 102, 241, 0.3),
            inset -2px -2px 4px rgba(0, 0, 0, 0.8);
    }
}

@keyframes darkMatterPulse {
    0%, 100% { 
        opacity: 0.6;
        transform: scale(0.8);
    }
    50% { 
        opacity: 1;
        transform: scale(1.2);
    }
}

@keyframes starGlow {
    0%, 100% { 
        box-shadow:
            0 0 12px rgba(6, 182, 212, 0.6),
            0 0 24px rgba(6, 182, 212, 0.3),
            0 0 40px rgba(6, 182, 212, 0.15),
            inset 0 0 10px rgba(255, 255, 255, 0.9);
    }
    50% { 
        box-shadow:
            0 0 18px rgba(6, 182, 212, 0.8),
            0 0 36px rgba(6, 182, 212, 0.4),
            0 0 60px rgba(6, 182, 212, 0.2),
            inset 0 0 12px rgba(255, 255, 255, 1);
    }
}

@keyframes energyPulse {
    0%, 100% { 
        opacity: 0.8;
        transform: scale(0.9);
    }
    50% { 
        opacity: 1;
        transform: scale(1.1);
    }
}

@keyframes battlePulse {
    0%, 100% { 
        box-shadow:
            0 0 0 3px var(--battle-red),
            0 0 20px rgba(239, 68, 68, 0.8),
            0 0 40px rgba(239, 68, 68, 0.4),
            0 0 60px rgba(239, 68, 68, 0.2);
    }
    50% { 
        box-shadow:
            0 0 0 3px var(--battle-red),
            0 0 30px rgba(239, 68, 68, 1),
            0 0 60px rgba(239, 68, 68, 0.6),
            0 0 90px rgba(239, 68, 68, 0.3);
    }
}

@keyframes energyOrb {
    0%, 100% { 
        opacity: 0.6;
        transform: scale(0.9);
    }
    50% { 
        opacity: 1;
        transform: scale(1.1);
    }
}

@keyframes energyRing {
    0% { 
        width: 100%; 
        height: 100%; 
        opacity: 1; 
        border-width: 1px;
    }
    100% { 
        width: 300%; 
        height: 300%; 
        opacity: 0; 
        border-width: 0;
    }
}

@keyframes starField {
    0% { transform: translateY(0); }
    100% { transform: translateY(100px); }
}

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

#board-container.coord-insert-mode .stone {
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

.region-rect {
    pointer-events: none;
    fill: rgba(34, 197, 94, 0.15);
    stroke: #22c55e;
    stroke-width: 0.08;
    filter: drop-shadow(0 0 6px rgba(34, 197, 94, 0.3));
}

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

.personality-card {
    position: relative;
    padding: 14px 12px 12px;
    background: rgba(15, 10, 30, 0.6);
    border: 1px solid rgba(99, 102, 241, 0.3);
    transition: all 0.4s cubic-bezier(0.22, 1, 0.36, 1);
}

.personality-card::before {
    content: '';
    position: absolute;
    top: 6px; left: 6px; right: 6px; bottom: 6px;
    border: 1px solid rgba(99, 102, 241, 0.3);
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
    background: rgba(99, 102, 241, 0.1);
    border: 1px solid rgba(99, 102, 241, 0.4);
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
    background: rgba(15, 10, 30, 0.6);
    border: 1px solid rgba(99, 102, 241, 0.3);
    font-family: 'DM Sans', sans-serif;
    font-size: 0.78rem;
    color: var(--ink-soft);
    position: relative;
    overflow: hidden;
    animation: mechanismSlideIn 0.4s cubic-bezier(0.22, 1, 0.36, 1) both;
    transition: all 0.25s ease;
}

.mechanism-badge:hover {
    border-color: var(--cosmic-purple);
    background: rgba(99, 102, 241, 0.15);
}

.mechanism-badge.leaving {
    animation: mechanismSlideOut 0.3s ease forwards;
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
    background: var(--cosmic-purple);
    color: #ffffff;
    flex-shrink: 0;
    min-width: 24px;
    text-align: center;
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
`;
    }

    async init() {
        if (this._initialized) return;
        await this.loadConfigs();
        this.renderBoard();
        this.renderStones();
        this.updateTurnIndicator();
        this.updateActiveRules();
        this.updateGameObjectives();
        this.updateAIPersonality();
        this.updateMechanisms();
        this.updateCaptureStats();
        this.bindEvents();
        this.checkApiKey();

        // === 六道 RPG 接入（共享模块） ===
        try {
            if (window.GameSharedRPG) {
                window.GameSharedRPG.install(this, {
                    isSandbox: false,
                    rerender: async (instance) => {
                    instance.renderBoard();
                    instance.renderStones();
                    instance.updateTurnIndicator();
                    instance.updateCaptureStats();
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

    async loadConfigs() {
        const resp = await fetch(`${this.apiBase}/api/config/all`, { cache: 'no-store' });
        this.configs = await resp.json();
        this.boardState = this.configs.board_state;
        this.uiConfig = this.configs.ui_config;

        if (window.AchievementChecker) {
            AchievementChecker.checkAfterConfigLoad(this.configs, this.boardState, 'weiqi');
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
                objective: { type: 'win', description: '击败对手' }
            };
            this.updateSamsaraUI();
            await this.loadLevelInfo();
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
        if (objectiveText) objectiveText.textContent = objective.description || '击败对手';
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
                body: JSON.stringify({ event_type: eventType, game_type: 'weiqi', details })
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
                body: JSON.stringify({ game_type: 'weiqi' })
            });
            const data = await resp.json();
            this.samsaraState = data.state;
            this.updateSamsaraUI();
            return data;
        } catch (e) {
            console.error('Failed to increment turn:', e);
        }
    }

    _getBoardLayoutConfig() {
        const defaults = {
            grid: {
                line_thickness: 0.02,
                show_horizontal: true,
                show_vertical: true,
                river_gap: false
            },
            palace: {
                enabled: false
            },
            river: {
                enabled: false
            },
            appearance: {
                background_color: '#dcb35c',
                line_color: '#5c3a1e',
                star_point_color: '#5c3a1e'
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
                    enabled: true,
                    thickness: 0.15,
                    color: '#5c3a1e'
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
                star_point_color: user.star_point_color !== undefined
                    ? user.star_point_color : defaults.appearance.star_point_color,
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
        const width = geometry.width || 19;
        const height = geometry.height || 19;
        const starPoints = geometry.star_points || [];

        const bgColor = layoutConfig.appearance.background_color;
        container.style.backgroundColor = bgColor;
        this.style.setProperty('--board-bg', bgColor);

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.classList.add('board-grid');
        const padLeft = layoutConfig.layout.viewbox_padding_left;
        const padRight = layoutConfig.layout.viewbox_padding_right;
        const padTop = layoutConfig.layout.viewbox_padding_top;
        const padBottom = layoutConfig.layout.viewbox_padding_bottom;
        const viewBoxWidth = (width - 1) + padLeft + padRight;
        const viewBoxHeight = (height - 1) + padTop + padBottom;
        svg.setAttribute('viewBox', `-${padLeft} -${padTop} ${viewBoxWidth} ${viewBoxHeight}`);
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.style.width = '100%';
        svg.style.height = '100%';

        const lineColor = layoutConfig.appearance.line_color;
        const sw = String(layoutConfig.grid.line_thickness);

        if (layoutConfig.grid.show_horizontal) {
            for (let i = 0; i < height; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', '0');
                line.setAttribute('y1', i);
                line.setAttribute('x2', width - 1);
                line.setAttribute('y2', i);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }

        if (layoutConfig.grid.show_vertical) {
            for (let i = 0; i < width; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', i);
                line.setAttribute('y1', '0');
                line.setAttribute('x2', i);
                line.setAttribute('y2', height - 1);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }

        const starPointColor = layoutConfig.appearance.star_point_color || lineColor;
        starPoints.forEach(([x, y]) => {
            const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('cx', x);
            circle.setAttribute('cy', y);
            circle.setAttribute('r', '0.15');
            circle.setAttribute('fill', starPointColor);
            svg.appendChild(circle);
        });

        if (layoutConfig.decorations.border.enabled) {
            const borderThickness = layoutConfig.decorations.border.thickness;
            const borderColor = layoutConfig.decorations.border.color || lineColor;
            const borderRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            const halfThick = borderThickness / 2;
            borderRect.setAttribute('x', -halfThick);
            borderRect.setAttribute('y', -halfThick);
            borderRect.setAttribute('width', (width - 1) + borderThickness);
            borderRect.setAttribute('height', (height - 1) + borderThickness);
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

        const customStyleId = 'custom-ui-css';
        let styleEl = this.shadowRoot.getElementById(customStyleId);
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = customStyleId;
            this.shadowRoot.appendChild(styleEl);
        }
        styleEl.textContent = this.uiConfig?.custom_css || '';
    }

    renderStones() {
        const container = this.shadowRoot.getElementById('board-container');
        container.querySelectorAll('.stone').forEach(el => el.remove());

        const pieces = this.boardState?.pieces || [];
        pieces.forEach(piece => {
            if (!piece.is_alive) return;
            this.createStoneElement(piece);
        });

        if (this.lastMove) {
            const el = container.querySelector(`[data-piece-id="${this.lastMove.piece_id}"]`);
            if (el) el.classList.add('last-moved');
        }
    }

    createStoneElement(piece) {
        const container = this.shadowRoot.getElementById('board-container');
        const el = document.createElement('div');
        el.className = `stone ${piece.side}`;
        el.dataset.pieceId = piece.id;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 19;
        const height = geometry.height || 19;

        const layoutConfig = this._getBoardLayoutConfig();
        const padLeft = layoutConfig.layout.viewbox_padding_left;
        const padTop = layoutConfig.layout.viewbox_padding_top;
        const viewBoxWidth = (width - 1) + padLeft + layoutConfig.layout.viewbox_padding_right;
        const viewBoxHeight = (height - 1) + padTop + layoutConfig.layout.viewbox_padding_bottom;

        const [x, y] = piece.position;
        const leftPct = ((x + padLeft) / viewBoxWidth) * 100;
        const topPct = ((y + padTop) / viewBoxHeight) * 100;

        el.style.left = `${leftPct}%`;
        el.style.top = `${topPct}%`;
        el.style.transform = 'translate(-50%, -50%)';

        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (this.coordInsertMode) {
                this.onCoordDotClick(x, y);
                return;
            }
        });

        container.appendChild(el);
    }

    async onBoardClick(e) {
        if (this.aiThinking) return;
        if (this.boardState?.game_status?.state === 'ended') return;

        const container = this.shadowRoot.getElementById('board-container');
        const rect = container.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 19;
        const height = geometry.height || 19;

        const layoutConfig = this._getBoardLayoutConfig();
        const padLeft = layoutConfig.layout.viewbox_padding_left;
        const padTop = layoutConfig.layout.viewbox_padding_top;
        const viewBoxWidth = (width - 1) + padLeft + layoutConfig.layout.viewbox_padding_right;
        const viewBoxHeight = (height - 1) + padTop + layoutConfig.layout.viewbox_padding_bottom;

        const gridX = Math.round((x / rect.width) * viewBoxWidth - padLeft);
        const gridY = Math.round((y / rect.height) * viewBoxHeight - padTop);

        if (gridX < 0 || gridX >= width || gridY < 0 || gridY >= height) return;

        if (this.coordInsertMode) {
            this.onCoordDotClick(gridX, gridY);
            return;
        }

        const pieces = this.boardState?.pieces || [];
        const existingPiece = pieces.find(p => p.is_alive && p.position[0] === gridX && p.position[1] === gridY);
        if (existingPiece) return;

        if (!this._isCurrentTurnPlayerControlled()) {
            this.showToast('本回合由AI控制', 'error');
            return;
        }

        await this.executeMove(gridX, gridY);
    }

    async executeMove(x, y) {
        this.aiThinking = true;

        try {
            const resp = await fetch(`${this.apiBase}/api/move`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ to: [x, y] })
            });

            const data = await resp.json();

            if (data.success) {
                await this.loadConfigs();
                this.renderStones();
                this.updateTurnIndicator();
                this.updateCaptureStats();
                this.updateMechanisms();

                await this.incrementTurn();

                const captures = this.boardState?.captures || { black: 0, white: 0 };
                const prevCaptures = this._prevCaptures || { black: 0, white: 0 };
                const playerSide = this.boardState?.current_turn === 'black' ? 'white' : 'black';
                const capturedCount = playerSide === 'black' 
                    ? (captures.black || 0) - (prevCaptures.black || 0)
                    : (captures.white || 0) - (prevCaptures.white || 0);

                if (capturedCount > 0) {
                    if (capturedCount >= 3) {
                        await this.reportKarmaEvent('capture_large', { count: capturedCount });
                    } else {
                        await this.reportKarmaEvent('capture_small', { count: capturedCount });
                    }
                }

                if (x <= 1 || x >= 17 || y <= 1 || y >= 17) {
                    await this.reportKarmaEvent('corner', { position: [x, y] });
                }

                this._prevCaptures = { ...captures };

                this._dispatchMoveEvent();

                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'weiqi');
                }

                if (data.game_ended) {
                    await this.reportKarmaEvent('endgame', { winner: data.winner });
                    this.showGameOver(data.winner, data.win_condition);
                    this._dispatchGameEndEvent();
                    this.aiThinking = false;
                    return;
                }

                await this.sleep(800);
                await this.makeAIMove();
            } else {
                this.addMessage(data.message || '落子失败', 'error');
                if (data.ai_controlled && !this.aiThinking) {
                    this.aiThinking = true;
                    await this.sleep(500);
                    await this.makeAIMove();
                }
            }
        } catch (error) {
            console.error('Move failed:', error);
            this.addMessage(`网络错误: ${error.message}`, 'error');
            this._dispatchError('落子失败', error);
        }

        this.aiThinking = false;
    }

    async makeAIMove(depth = 0) {
        if (depth > 10) return;

        this.addMessage('AI思考中...', 'info');
        if (typeof this.rpgShowThinking === 'function') this.rpgShowThinking();
        this.aiThinking = true;

        try {
            const resp = await fetch(`${this.apiBase}/api/ai_move`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({})
            });

            const data = await resp.json();
            if (typeof this.rpgHideThinking === 'function') this.rpgHideThinking();

            if (data.success) {
                this.lastMove = data.ai_move;
                await this.loadConfigs();
                this.renderStones();
                this.updateTurnIndicator();
                this.updateCaptureStats();
                this.updateMechanisms();

                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }

                if (data.ai_move && data.ai_move.to) {
                    const [ax, ay] = data.ai_move.to;
                    const aiSide = this.boardState?.current_turn === 'black' ? '白方' : '黑方';
                    this.addMessage(`${aiSide}AI落子(${ax},${ay})`, 'info');
                }

                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'weiqi');
                }

                this._dispatchMoveEvent();

                if (data.game_ended) {
                    this.showGameOver(data.winner, data.win_condition);
                    this._dispatchGameEndEvent();
                    this.aiThinking = false;
                    return;
                }

                if (this._isCurrentTurnAITurn()) {
                    await this.sleep(600);
                    await this.makeAIMove(depth + 1);
                }
            } else {
                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }
                this.addMessage(data.message || 'AI落子失败', 'error');
            }
        } catch (error) {
            const messages = this.shadowRoot.getElementById('ai-messages');
            const lastMsg = messages.lastElementChild;
            if (lastMsg && lastMsg.textContent.includes('思考中')) {
                lastMsg.remove();
            }
            this.addMessage(`AI错误: ${error.message}`, 'error');
            if (typeof this.rpgHideThinking === 'function') this.rpgHideThinking();
            this._dispatchError('AI落子失败', error);
        }

        this.aiThinking = false;
    }

    _dispatchMoveEvent() {
        const captures = this.boardState?.captures || { black: 0, white: 0 };
        const gameStatus = this.boardState?.game_status || {};
        const mover = this.boardState?.current_turn === 'black' ? 'white' : 'black';
        const goCaptures = (captures.black || 0) + (captures.white || 0);

        this.dispatchEvent(new CustomEvent('move', {
            bubbles: true,
            composed: true,
            detail: {
                captured: null,
                mover: mover,
                is_check: false,
                game_ended: gameStatus.state === 'ended',
                winner: gameStatus.winner || null,
                is_five_in_a_row: false,
                go_captures: goCaptures
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

    updateTurnIndicator() {
        const turn = this.boardState?.current_turn;
        const indicator = this.shadowRoot.getElementById('turn-indicator');
        if (indicator) {
            indicator.textContent = turn === 'black' ? '黑方回合' : '白方回合';
        }
    }

    updateCaptureStats() {
        const captures = this.boardState?.captures || { black: 0, white: 0 };
        const blackEl = this.shadowRoot.getElementById('capture-black');
        const whiteEl = this.shadowRoot.getElementById('capture-white');
        if (blackEl) blackEl.textContent = captures.black || 0;
        if (whiteEl) whiteEl.textContent = captures.white || 0;
    }

    _isCurrentTurnPlayerControlled() {
        const currentTurn = this.boardState?.current_turn || 'black';
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

    _isCurrentTurnAIControlled() {
        const mechanisms = this.boardState?.mechanisms || {};
        const currentTurn = this.boardState?.current_turn || 'black';
        const aiControl = mechanisms.ai_control || [];
        return aiControl.some(item => item.side === currentTurn && item.remaining !== 0);
    }

    _isCurrentTurnAITurn() {
        const currentTurn = this.boardState?.current_turn || 'black';
        if (this._isCurrentTurnAIControlled()) return true;
        if (!this._isCurrentTurnPlayerControlled()) return true;
        return false;
    }

    sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
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
        this.shadowRoot.querySelector('.input-section')?.classList.add('input-locked');
        const input = this.shadowRoot.getElementById('command-input');
        if (input) input.disabled = true;

        this.pollThinkingStatus();
    }

    hideThinking() {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        overlay.classList.remove('show');

        this.shadowRoot.getElementById('board-container').classList.remove('board-locked');
        this.shadowRoot.querySelector('.input-section')?.classList.remove('input-locked');
        const input = this.shadowRoot.getElementById('command-input');
        if (input) input.disabled = false;

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
                        if (textEl) textEl.textContent = 'ChatAI 正在理解您的意图...';
                        if (stageEl) stageEl.textContent = '阶段: 意图解析';
                    } else if (data.stage === 'code') {
                        if (textEl) textEl.textContent = 'CodeAI 正在生成代码...';
                        if (stageEl) stageEl.textContent = '阶段: 代码生成';
                    }
                }
            } catch (e) {
            }
        }, 500);
    }

    showToast(message, type = 'success') {
        const app = this.shadowRoot.getElementById('app');
        let container = this.shadowRoot.querySelector('.toast-container');
        if (!container) {
            container = document.createElement('div');
            container.className = 'toast-container';
            app.appendChild(container);
        }

        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        toast.textContent = message;
        container.appendChild(toast);

        setTimeout(() => {
            toast.classList.add('fade-out');
            setTimeout(() => toast.remove(), 300);
        }, 3000);
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

    async restartGame() {
        // Bug3 修复：重玩调用 RPG 三件套（apply_level_config + reset_battle + UI 重绘）
        return this.rpgRestart();
    }


    async undoMove() {
        try {
            const resp = await fetch(`${this.apiBase}/api/undo`, { method: 'POST' });
            const data = await resp.json();

            if (data.success) {
                await this.loadConfigs();
                this.renderStones();
                this.updateTurnIndicator();
                this.updateCaptureStats();
                this.showToast('已悔棋');
            } else {
                this.showToast(data.message, 'error');
            }
        } catch (error) {
            console.error('Undo failed:', error);
            this.showToast('悔棋失败', 'error');
        }
    }

    async undoConfig() {
        try {
            const resp = await fetch(`${this.apiBase}/api/undo_config`, { method: 'POST' });
            const data = await resp.json();

            if (data.success) {
                await this.loadConfigs();
                this.renderBoard();
                this.renderStones();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateAIPersonality();
                this.showToast('已撤回AI修改');
            } else {
                this.showToast(data.message, 'error');
            }
        } catch (error) {
            console.error('Undo config failed:', error);
        }
    }

    async resetConfigs() {
        if (!confirm('确定要重置所有配置吗？')) return;
        try {
            const resp = await fetch(`${this.apiBase}/api/reset_configs`, { method: 'POST' });
            const data = await resp.json();

            if (data.success) {
                await this.loadConfigs();
                this.renderBoard();
                this.renderStones();
                this.updateTurnIndicator();
                this.updateCaptureStats();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateAIPersonality();
                this.updateMechanisms();
                this.showToast('已重置所有配置');
            } else {
                this.showToast(data.message, 'error');
            }
        } catch (error) {
            console.error('Reset configs failed:', error);
        }
    }

    updateActiveRules() {
        const rulesContainer = this.shadowRoot.getElementById('active-rules');
        if (!rulesContainer) return;

        const customRules = this.boardState?.game_status?.custom_rules_active || [];

        if (customRules.length === 0) {
            rulesContainer.innerHTML = '<span class="empty">暂无自定义规则</span>';
            return;
        }

        rulesContainer.innerHTML = customRules.map((rule, index) => `
            <div class="rule-item" style="counter-increment: rule-counter ${index + 1}">
                ${rule}
            </div>
        `).join('');
    }

    updateGameObjectives() {
        const objectivesContainer = this.shadowRoot.getElementById('game-objectives');
        if (!objectivesContainer) return;

        const rules = this.configs.rules || {};
        const winConditions = rules.win_conditions || {};
        const objectives = [];

        Object.entries(winConditions).forEach(([key, condition]) => {
            if (condition.enabled) {
                objectives.push({
                    icon: condition.icon || '⚫',
                    name: condition.display_name || key,
                    desc: condition.description
                });
            }
        });

        if (objectives.length === 0) {
            objectivesContainer.innerHTML = '<span class="empty">加载中...</span>';
            return;
        }

        objectivesContainer.innerHTML = objectives.map(obj => `
            <div style="display: flex; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px dotted #e0ddd7;">
                <span style="font-size: 1.2rem;">${obj.icon}</span>
                <div>
                    <div style="font-size: 0.85rem; color: #2d2d2d;">${obj.name}</div>
                    <div style="font-size: 0.75rem; color: #7a7a7a;">${obj.desc}</div>
                </div>
            </div>
        `).join('');
    }

    updateAIPersonality() {
        const rules = this.configs.rules || {};
        const personality = rules.ai_difficulty?.personality || {
            type: 'normal',
            aggressiveness: 0.5,
            conservatism: 0.5
        };

        const typeMap = {
            'normal': { name: '标准型', subtitle: 'Normal', desc: '攻守平衡的标准AI', icon: '🧠' },
            'aggressive': { name: '进攻型', subtitle: 'Aggressive', desc: '主动出击，积极进攻', icon: '⚔️' },
            'defensive': { name: '防守型', subtitle: 'Defensive', desc: '稳扎稳打，注重防守', icon: '🛡️' },
            'random': { name: '随机型', subtitle: 'Random', desc: '随心所欲，快乐下棋', icon: '🎲' }
        };

        const info = typeMap[personality.type] || typeMap.normal;

        this.shadowRoot.getElementById('personality-icon').textContent = info.icon;
        this.shadowRoot.getElementById('personality-type').textContent = info.name;
        this.shadowRoot.getElementById('personality-subtitle').textContent = info.subtitle;
        this.shadowRoot.getElementById('personality-desc').textContent = info.desc;

        const aggPct = Math.round((personality.aggressiveness || 0.5) * 100);
        const defPct = Math.round((personality.conservatism || 0.5) * 100);

        this.shadowRoot.getElementById('bar-agg').textContent = '█'.repeat(Math.round(aggPct / 10)) + '░'.repeat(10 - Math.round(aggPct / 10));
        this.shadowRoot.getElementById('bar-agg-pct').textContent = `${aggPct}%`;
        this.shadowRoot.getElementById('bar-def').textContent = '█'.repeat(Math.round(defPct / 10)) + '░'.repeat(10 - Math.round(defPct / 10));
        this.shadowRoot.getElementById('bar-def-pct').textContent = `${defPct}%`;

        const card = this.shadowRoot.getElementById('ai-personality');
        card.className = `personality-card type-${personality.type}`;
    }

    updateMechanisms() {
        const container = this.shadowRoot.getElementById('active-mechanisms');
        if (!container) return;

        const mechanisms = this.boardState?.mechanisms || {};
        const activeMechanisms = [];

        if (mechanisms.skip_turns?.length > 0) {
            activeMechanisms.push({ type: 'skip', text: `跳过回合`, count: mechanisms.skip_turns.length });
        }
        if (mechanisms.ai_control?.length > 0) {
            activeMechanisms.push({ type: 'ai', text: `AI控制`, count: mechanisms.ai_control.length });
        }
        if (mechanisms.random_moves?.length > 0) {
            activeMechanisms.push({ type: 'random', text: `随机走棋`, count: mechanisms.random_moves.length });
        }
        if (mechanisms.extra_turns?.length > 0) {
            activeMechanisms.push({ type: 'extra', text: `额外回合`, count: mechanisms.extra_turns.length });
        }
        if (mechanisms.move_limits?.length > 0) {
            activeMechanisms.push({ type: 'limit', text: `移动限制`, count: mechanisms.move_limits.length });
        }

        if (activeMechanisms.length === 0) {
            container.innerHTML = '<span class="empty">无激活机制</span>';
            return;
        }

        container.innerHTML = activeMechanisms.map(m => `
            <div class="mechanism-badge type-${m.type}">
                <span class="mechanism-count">${m.count}</span>
                <span class="mechanism-text">${m.text}</span>
            </div>
        `).join('');
    }

    async sendCommand() {
        const input = this.shadowRoot.getElementById('command-input');
        const message = input.value.trim();
        if (!message) return;

        input.value = '';
        this.addMessage(`📝 你: ${message}`, 'user');

        this.showThinking('ChatAI 正在理解您的意图...', '意图解析');

        try {
            const resp = await fetch(`${this.apiBase}/api/command`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ command: message })
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

                    if (data.modified_configs && Object.keys(data.modified_configs).length > 0) {
                        await this.loadConfigs();
                        this.renderBoard();
                        this.renderStones();
                        this.updateTurnIndicator();
                        this.updateActiveRules();
                        this.updateGameObjectives();
                        this.updateAIPersonality();
                        this.updateMechanisms();
                    }

                    const gameStatus = this.boardState?.game_status;
                    if (gameStatus && gameStatus.state === 'ended') {
                        this._gameOverTimer = setTimeout(() => this.showGameOver(gameStatus.winner, gameStatus.win_condition), 100);
                    }

                    if (this._isCurrentTurnAITurn() && !this.aiThinking) {
                        this.aiThinking = true;
                        await this.sleep(500);
                        await this.makeAIMove();
                    }
                } else if (data.type === 'fun') {
                    this.addMessage(data.message, 'fun');
                } else if (data.message) {
                    this.addMessage(data.message, 'info');
                }

                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterCommand(data, message, this.configs, this.boardState, 'weiqi');
                }
            } else {
                if (data.type === 'rejected') {
                    this.addMessage(`❌ ${data.message}`, 'error');
                } else {
                    this.addMessage(`⚠️ ${data.message}`, 'error');
                }
            }
        } catch (error) {
            this.hideThinking();
            this.addMessage(`网络错误: ${error.message}`, 'error');
            this._dispatchError('发送指令失败', error);
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
        const messagesContainer = this.shadowRoot.getElementById('ai-messages');
        if (!messagesContainer) return;

        const messageEl = document.createElement('div');
        messageEl.className = `message ${type}`;
        messageEl.textContent = text;
        messagesContainer.appendChild(messageEl);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;

        while (messagesContainer.children.length > 50) {
            messagesContainer.removeChild(messagesContainer.firstChild);
        }
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
                    <span>#${index + 1} ${this.escapeHtml(log.timestamp || '未知时间')}</span>
                    <span>分类: ${this.escapeHtml(log.classification || 'N/A')}</span>
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
                    <div class="log-section-title">✅ 最终结果: ${this.escapeHtml(final.type || 'unknown')}</div>
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
        if (text === null || text === undefined) return '';
        return String(text)
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

    onCoordDotClick(x, y) {
        const input = this.shadowRoot.getElementById('command-input');
        const coordStr = `[${x},${y}]`;

        if (input.selectionStart !== undefined) {
            const start = input.selectionStart;
            const end = input.selectionEnd;
            input.value = input.value.substring(0, start) + coordStr + input.value.substring(end);
            input.focus();
            input.setSelectionRange(start + coordStr.length, start + coordStr.length);
        } else {
            input.value += coordStr;
        }

        if (this.selectMode === 'coord') {
            this.coordInsertMode = false;
            this.shadowRoot.getElementById('btn-insert-coord').classList.remove('active');
            this.shadowRoot.getElementById('board-container').classList.remove('coord-insert-mode');
        }
    }

    async checkApiKey() {
        const savedKey = localStorage.getItem('deepseek_api_key');
        if (savedKey) {
            const inputEl = this.shadowRoot.getElementById('api-key-input');
            if (inputEl) inputEl.value = savedKey;
        }

        try {
            const resp = await fetch(`${this.apiBase}/api/apikey/status`);
            const data = await resp.json();
            if (!data.has_key) {
                this.shadowRoot.getElementById('settings-modal').classList.add('show');
                this.addMessage('⚠️ 请先设置 DeepSeek API Key', 'error');
            }
        } catch (e) {
            console.error('Check API key status failed:', e);
        }
    }

    saveSettings() {
        const apiKey = this.shadowRoot.getElementById('api-key-input').value;
        const difficulty = this.shadowRoot.getElementById('difficulty-select').value;

        localStorage.setItem('deepseek_api_key', apiKey);

        Promise.all([
            fetch(`${this.apiBase}/api/apikey`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ api_key: apiKey })
            }),
            fetch(`${this.apiBase}/api/difficulty`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ difficulty })
            })
        ]).then(() => {
            this.showToast('设置已保存');
            this.shadowRoot.getElementById('settings-modal').classList.remove('show');
        }).catch(err => {
            console.error('Save settings failed:', err);
            this.showToast('设置保存失败', 'error');
        });
    }

    bindEvents() {
        this.shadowRoot.getElementById('board-container').addEventListener('click', (e) => {
            if (!e.target.classList.contains('stone')) {
                this.onBoardClick(e);
            }
        });

        this.shadowRoot.getElementById('btn-send').addEventListener('click', () => this.sendCommand());
        this.shadowRoot.getElementById('command-input').addEventListener('keypress', (e) => {
            if (e.key === 'Enter') this.sendCommand();
        });

        this.shadowRoot.getElementById('btn-undo').addEventListener('click', () => this.undoMove());
        this.shadowRoot.getElementById('btn-undo-config').addEventListener('click', () => this.undoConfig());
        this.shadowRoot.getElementById('btn-restart').addEventListener('click', () => this.restartGame());
        this.shadowRoot.getElementById('btn-reset-configs').addEventListener('click', async () => {
            if (!confirm('确定要重置所有配置吗？会同步重置六道关卡/业力（软重置：保留成就/技能），规则配置恢复默认。')) return;
            await this.rpgResetConfigsHandler('soft');
        });

        this.shadowRoot.getElementById('close-settings').addEventListener('click', () => {
            this.shadowRoot.getElementById('settings-modal').classList.remove('show');
        });

        const btnAchievements = this.shadowRoot.getElementById('btn-achievements');
        if (btnAchievements) {
            btnAchievements.addEventListener('click', () => {
                window.open('/overworld', '_blank', 'noopener,noreferrer');
            });
        }

        this.shadowRoot.getElementById('save-settings').addEventListener('click', () => this.saveSettings());

        this.shadowRoot.getElementById('btn-logs').addEventListener('click', () => {
            this.showLogs();
        });
        this.shadowRoot.getElementById('close-logs').addEventListener('click', () => {
            this.hideLogs();
        });
        this.shadowRoot.getElementById('btn-clear-logs').addEventListener('click', () => this.clearLogs());
        this.shadowRoot.getElementById('btn-refresh-logs').addEventListener('click', () => this.refreshLogs());

        this.shadowRoot.getElementById('btn-insert-coord').addEventListener('click', () => {
            this.coordInsertMode = !this.coordInsertMode;
            const btn = this.shadowRoot.getElementById('btn-insert-coord');
            const board = this.shadowRoot.getElementById('board-container');

            if (this.coordInsertMode) {
                btn.classList.add('active');
                board.classList.add('coord-insert-mode');
            } else {
                btn.classList.remove('active');
                board.classList.remove('coord-insert-mode');
            }
        });

        this.shadowRoot.getElementById('btn-toggle-coord-mode').addEventListener('click', () => {
            this.selectMode = this.selectMode === 'coord' ? 'region' : 'coord';
            this.showToast(`已切换为${this.selectMode === 'coord' ? '选坐标' : '选区域'}模式`);
        });

        this._outsideClickListener = (e) => {
            if (!e.target.closest('#settings-modal') && !e.target.closest('#btn-settings')) {
                this.shadowRoot.getElementById('settings-modal').classList.remove('show');
            }
            if (!e.target.closest('#logs-modal') && !e.target.closest('#btn-logs')) {
                this.shadowRoot.getElementById('logs-modal').classList.remove('show');
            }
        };
        this.shadowRoot.addEventListener('click', this._outsideClickListener);
    
        // 六道 RPG：跨页事件驱动刷新（替代轮询）
        if (typeof this.initRpgEventListeners === 'function') this.initRpgEventListeners();
}

    async applyCheatPatch(modifiedConfigs) {
        try {
            // RPG 后端 cheat_use 已通过 /api/command 将配置应用到棋类后端
            // 这里只需重新拉取最新配置并重渲染
            await this.loadConfigs();
            this.renderBoard();
            this.renderStones();
            this.updateTurnIndicator();
            this.updateCaptureStats();
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
            boardState: JSON.parse(JSON.stringify(this.boardState)),
            configs: JSON.parse(JSON.stringify(this.configs)),
            uiConfig: JSON.parse(JSON.stringify(this.uiConfig))
        };
    }

    resetBoard() {
        this.restartGame();
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
        if (this._outsideClickListener && this.shadowRoot) {
            this.shadowRoot.removeEventListener('click', this._outsideClickListener);
            this._outsideClickListener = null;
        }
        this.aiThinking = false;
        this._initialized = false;
    }
}

if (!customElements.get('go-board')) customElements.define('go-board', GoBoard);

class XiangqiBoard extends HTMLElement {
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

        // ── 第二阶段 Bug 修复：事件驱动刷新 & 沙盒/主模式标记 ──
        this._karmaPollingTimer = null;  // 轮询定时器（现已禁用，保留字段防旧代码崩溃）
        this._broadcastChannel = null;   // BroadcastChannel("game-events")
        this._bcBound = false;           // 是否已绑定跨页监听
        this._visibilityBound = false;   // 是否已绑定 visibility / focus 兜底
        this.isSandbox = false;          // 是否沙盒模式（主模式默认 false）
        this._lastResolveRewards = null; // 最近一次胜负结算奖励（胜负弹窗显示用）
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
            <h1>无限制象棋</h1>
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
                    <span class="samsara-value objective-text" id="objective-text">将死对方</span>
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
                试试："車变成两个"、"兵可以后退"、"象可以过河"、"把棋盘掀了"
            </div>
        </footer>
        `;
    }

    _getStyles() {
        return `
@font-face {
    font-family: 'KaiTi';
    src: local('KaiTi'), local('STKaiti'), local('AR PL UKai CN'), local('AR PL UKai HK');
    font-weight: normal;
    font-style: normal;
    font-display: swap;
}

@font-face {
    font-family: 'KaiTi';
    src: local('KaiTi Bold'), local('STKaiti');
    font-weight: bold;
    font-style: normal;
    font-display: swap;
}

:host {
    --paper: #f5f0e6;
    --paper-warm: #ebe3d4;
    --paper-dark: #dcd2b8;
    --ink: #3d2914;
    --ink-soft: #5a4025;
    --ink-medium: #7a5a3a;
    --ink-light: #9a7a5a;
    --ink-faint: #c9b898;
    --line: #d4c8a8;
    --line-strong: #c4b490;

    --board-bg: #8b5a2b;
    --board-line: #2d1a08;
    --board-border: #5c3a1e;
    --red-piece: #8b2500;
    --black-piece: #1a0f05;
    --gold: #c9a227;
    --gold-light: #e6c84a;
    --gold-dark: #a68522;
    --red-lacquer: #8b2500;
    --red-lacquer-light: #a53010;

    --neon-cyan: #4a90a4;
    --neon-magenta: #8b4a6b;
    --neon-pink: #a54a5a;
    --neon-green: #5a7a4a;
    --neon-gold: #c9a227;

    --highlight: #3d2914;
    --valid-move: #5a7a3a;
    --last-move: #c9a227;
    --danger: #8b2500;
    --success: #5a7a4a;
    --warning: #c9a227;
    --text-light: #5a4025;

    --muted-ink: #7a5a3a;
    --accent-green: #5a7a4a;

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
    font-family: 'KaiTi', 'STKaiti', 'SimSun', 'Songti SC', serif;
    color: var(--ink);
    background-color: var(--paper);
    background-image:
        url("data:image/svg+xml,%3Csvg viewBox='0 0 600 600' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='inkFilter'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.04' numOctaves='5' stitchTiles='stitch'/%3E%3CfeDiffuseLighting in='noise' lighting-color='%233d2914' surfaceScale='2'%3E%3CfeDistantLight azimuth='45' elevation='60'/%3E%3C/feDiffuseLighting%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23inkFilter)' opacity='0.03'/%3E%3C/svg%3E"),
        url("data:image/svg+xml,%3Csvg viewBox='0 0 400 400' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='paperFilter'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.8' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23paperFilter)'/%3E%3C/svg%3E");
    background-repeat: repeat;
    background-size: 600px 600px, 200px 200px;
    background-blend-mode: overlay, multiply;
    overflow: hidden;
}

/* Header */
.header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 24px;
    background: var(--paper);
    border-bottom: 1px solid var(--line);
    position: relative;
    animation: fadeInUp 0.6s cubic-bezier(0.22, 1, 0.36, 1) 0.1s both;
}

.header::after {
    content: '';
    position: absolute;
    left: 24px; right: 24px; bottom: -1px;
    height: 1px;
    background: var(--ink);
    transform: scaleX(0);
    transform-origin: left;
    animation: scaleIn 0.8s cubic-bezier(0.22, 1, 0.36, 1) 0.4s forwards;
}

.header h1 {
    font-family: 'KaiTi', 'STKaiti', 'SimSun', serif;
    font-weight: bold;
    font-size: 2rem;
    letter-spacing: 0.2em;
    color: var(--ink);
    font-style: normal;
    position: relative;
}

.header h1::before {
    content: '象 棋';
    display: block;
    font-family: 'KaiTi', 'STKaiti', serif;
    font-size: 0.8rem;
    font-weight: normal;
    letter-spacing: 0.5em;
    color: var(--ink-light);
    margin-bottom: 4px;
    font-style: normal;
}

.header h1::after {
    content: '';
    position: absolute;
    bottom: -4px;
    left: 0;
    width: 100%;
    height: 2px;
    background: linear-gradient(90deg, transparent, var(--gold), transparent);
}

.header-actions {
    display: flex;
    align-items: center;
    gap: 18px;
}

#turn-indicator {
    padding: 8px 20px;
    background: linear-gradient(145deg, var(--ink) 0%, var(--ink-soft) 100%);
    color: var(--paper);
    font-family: 'KaiTi', 'STKaiti', serif;
    font-size: 0.85rem;
    font-weight: bold;
    letter-spacing: 0.2em;
    border: 2px solid var(--gold);
    position: relative;
    overflow: hidden;
    transition: all 0.3s cubic-bezier(0.22, 1, 0.36, 1);
}

#turn-indicator::before {
    content: '';
    position: absolute;
    top: 0; left: -100%;
    width: 100%; height: 100%;
    background: linear-gradient(90deg, transparent, rgba(201, 162, 39, 0.3), transparent);
    transition: left 0.5s ease;
}

#turn-indicator:hover::before {
    left: 100%;
}

#turn-indicator:hover {
    border-color: var(--gold-light);
    box-shadow: 0 0 12px rgba(201, 162, 39, 0.4);
}

.samsara-bar {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 16px;
    padding: 6px 24px;
    background: linear-gradient(135deg, #3d2914 0%, #5a4025 50%, #3d2914 100%);
    border-bottom: 3px solid var(--gold);
    box-shadow: 0 4px 20px rgba(61, 41, 20, 0.3);
}

.level-info-bar {
    width: 100%;
    text-align: center;
    font-size: 0.8rem;
    color: #fff;
    padding: 2px 0;
    letter-spacing: 0.05em;
}

.level-realm {
    color: var(--gold-light);
    font-weight: 600;
}

.level-name {
    color: #fff;
    font-weight: 500;
}

.level-type-badge {
    display: inline-block;
    margin-left: 6px;
    padding: 1px 8px;
    font-size: 0.65rem;
    border-radius: 8px;
    background: rgba(201, 162, 39, 0.25);
    border: 1px solid var(--gold);
    color: var(--gold-light);
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

#board-container {
    position: relative;
    width: calc(min(90vmin, 560px, calc((100vh - 180px) * 0.9)) * 0.75);
    height: calc(min(90vmin, 560px, calc((100vh - 180px) * 0.9)) * 0.75 * 10 / 9);
    background: linear-gradient(135deg, #9b6a3a 0%, #8b5a2b 25%, #7a4a25 50%, #8b5a2b 75%, #9b6a3a 100%);
    background-image:
        repeating-linear-gradient(
            90deg,
            transparent,
            transparent 30px,
            rgba(139, 90, 43, 0.3) 30px,
            rgba(139, 90, 43, 0.3) 31px
        ),
        repeating-linear-gradient(
            0deg,
            transparent,
            transparent 30px,
            rgba(139, 90, 43, 0.2) 30px,
            rgba(139, 90, 43, 0.2) 31px
        ),
        url("data:image/svg+xml,%3Csvg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='woodFilter'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.02' numOctaves='3'/%3E%3CfeDisplacementMap in='SourceGraphic' scale='5'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23woodFilter)'/%3E%3C/svg%3E");
    border-radius: 8px;
    border: 4px solid #5c3a1e;
    box-shadow:
        0 0 0 2px #3d2914,
        0 8px 32px rgba(61, 41, 20, 0.4),
        0 24px 80px rgba(61, 41, 20, 0.3),
        inset 0 1px 0 rgba(255, 255, 255, 0.1);
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
    background: linear-gradient(145deg, var(--paper-warm) 0%, var(--paper) 100%);
    border: 2px solid var(--ink);
    padding: 12px 16px;
    animation: fadeInUp 0.5s cubic-bezier(0.22, 1, 0.36, 1) both;
    box-shadow:
        2px 2px 0 var(--ink),
        inset 0 0 20px rgba(61, 41, 20, 0.05);
}

.panel-section::before {
    content: '';
    position: absolute;
    top: 4px; left: 4px;
    width: 20px; height: 20px;
    border-top: 2px solid var(--gold);
    border-left: 2px solid var(--gold);
    pointer-events: none;
}

.panel-section::after {
    content: '';
    position: absolute;
    bottom: 4px; right: 4px;
    width: 20px; height: 20px;
    border-bottom: 2px solid var(--gold);
    border-right: 2px solid var(--gold);
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
    width: 10%;
    height: 9%;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.4rem;
    font-weight: bold;
    font-family: 'KaiTi', 'STKaiti', 'SimSun', serif;
    cursor: pointer;
    user-select: none;
    transition: transform 0.2s, box-shadow 0.2s, filter 0.2s;
    z-index: 10;
    border: 3px solid var(--gold);
    box-shadow:
        0 3px 12px rgba(61, 41, 20, 0.5),
        0 0 0 1px rgba(61, 41, 20, 0.3),
        inset 0 -2px 4px rgba(61, 41, 20, 0.2),
        inset 0 2px 4px rgba(255, 255, 255, 0.1);
}

.piece:hover {
    transform: translate(-50%, -50%) scale(1.08);
    filter: brightness(1.15);
}

.piece.red {
    background: linear-gradient(145deg, #a53010 0%, #8b2500 50%, #6b1a00 100%);
    color: #fff5e6;
    border: 3px solid var(--gold);
    box-shadow:
        0 4px 14px rgba(61, 41, 20, 0.6),
        0 0 0 1px rgba(139, 37, 0, 0.5),
        inset 0 -3px 6px rgba(61, 41, 20, 0.3),
        inset 0 3px 6px rgba(255, 200, 180, 0.2);
}

.piece.black {
    background: linear-gradient(145deg, #3a2510 0%, #1a0f05 50%, #0d0803 100%);
    color: #f5f0e6;
    border: 3px solid var(--gold);
    box-shadow:
        0 4px 14px rgba(61, 41, 20, 0.7),
        0 0 0 1px rgba(26, 15, 5, 0.5),
        inset 0 -3px 6px rgba(0, 0, 0, 0.4),
        inset 0 3px 6px rgba(200, 180, 160, 0.1);
}

.piece.selected {
    border-color: var(--gold-light);
    box-shadow:
        0 0 0 4px var(--gold),
        0 0 20px rgba(201, 162, 39, 0.7),
        0 0 40px rgba(201, 162, 39, 0.4),
        inset 0 0 10px rgba(201, 162, 39, 0.3);
    z-index: 20;
}

.piece.last-moved {
    border-color: var(--red-lacquer-light);
    box-shadow:
        0 0 0 4px var(--red-lacquer),
        0 0 16px rgba(139, 37, 0, 0.6),
        0 0 32px rgba(139, 37, 0, 0.3);
}

.piece.ai-moved {
    border: 4px solid var(--gold-light);
    box-shadow:
        0 0 20px rgba(201, 162, 39, 0.8),
        0 0 40px rgba(201, 162, 39, 0.5),
        inset 0 0 15px rgba(201, 162, 39, 0.4);
    z-index: 100;
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
    padding: 10px 18px;
    border: 2px solid var(--ink);
    background: linear-gradient(145deg, var(--paper) 0%, var(--paper-warm) 100%);
    color: var(--ink);
    cursor: pointer;
    font-family: 'KaiTi', 'STKaiti', serif;
    font-size: 0.85rem;
    font-weight: bold;
    letter-spacing: 0.15em;
    border-radius: 0;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
    box-shadow: 2px 2px 0 var(--ink);
}

.btn:hover {
    background: var(--ink);
    color: var(--paper);
    box-shadow: 1px 1px 0 var(--ink);
    transform: translate(1px, 1px);
}

.btn:active {
    box-shadow: none;
    transform: translate(2px, 2px);
}

.btn-primary {
    padding: 10px 22px;
    border: 2px solid var(--gold);
    background: linear-gradient(145deg, var(--ink) 0%, var(--ink-soft) 100%);
    color: var(--paper);
    cursor: pointer;
    font-family: 'KaiTi', 'STKaiti', serif;
    font-size: 0.85rem;
    font-weight: bold;
    letter-spacing: 0.15em;
    border-radius: 0;
    transition: all 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    position: relative;
    box-shadow: 2px 2px 0 var(--gold);
}

.btn-primary::after {
    content: '→';
    display: inline-block;
    margin-left: 8px;
    transition: transform 0.25s cubic-bezier(0.22, 1, 0.36, 1);
    color: var(--gold);
}

.btn-primary:hover {
    background: var(--ink-soft);
    box-shadow: 1px 1px 0 var(--gold);
    transform: translate(1px, 1px);
    border-color: var(--gold-light);
}

.btn-primary:active {
    box-shadow: none;
    transform: translate(2px, 2px);
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
        left: 16px; right: 16px;
    }

    .input-section {
        padding: 8px 16px;
    }

    .input-section::before {
        left: 16px; right: 16px;
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

/* Seal Stamp Decoration */
.seal-stamp {
    position: absolute;
    width: 40px;
    height: 40px;
    border: 3px solid var(--red-lacquer);
    background: rgba(139, 37, 0, 0.05);
    display: flex;
    align-items: center;
    justify-content: center;
    font-family: 'KaiTi', 'STKaiti', serif;
    font-weight: bold;
    font-size: 0.9rem;
    color: var(--red-lacquer);
    letter-spacing: 0.1em;
    transform: rotate(-15deg);
    pointer-events: none;
    opacity: 0.8;
}

.seal-stamp::before {
    content: '';
    position: absolute;
    inset: 3px;
    border: 1px solid var(--red-lacquer);
}

.header::before {
    content: '';
    position: absolute;
    top: 8px;
    right: 100px;
    width: 36px;
    height: 36px;
    border: 2px solid var(--red-lacquer);
    background: rgba(139, 37, 0, 0.03);
    display: flex;
    align-items: center;
    justify-content: center;
    font-family: 'KaiTi', 'STKaiti', serif;
    font-weight: bold;
    font-size: 0.8rem;
    color: var(--red-lacquer);
    letter-spacing: 0.1em;
    transform: rotate(-10deg);
}

.header::before {
    content: '象';
}

/* Ink Wash Effect */
.ink-wash {
    position: absolute;
    border-radius: 50%;
    filter: blur(40px);
    opacity: 0.03;
    pointer-events: none;
}

#app::before {
    content: '';
    position: absolute;
    top: -100px;
    right: -100px;
    width: 400px;
    height: 400px;
    background: var(--ink);
    border-radius: 50%;
    filter: blur(80px);
    opacity: 0.05;
    pointer-events: none;
}

#app::after {
    content: '';
    position: absolute;
    bottom: -100px;
    left: -100px;
    width: 300px;
    height: 300px;
    background: var(--ink);
    border-radius: 50%;
    filter: blur(60px);
    opacity: 0.04;
    pointer-events: none;
}
`;
    }

    async init() {
        if (this._initialized) return;
        // 第一步：常规棋盘+UI初始化
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

        // 第二步：初始化跨页广播 + 缓存兜底（事件驱动刷新基础）
        this._initEventDrivenRefresh();

        // 第三步：关卡/业力三件套（Bug 2 修复：开局必调用，第二关开始不残留）
        try {
            await this.rpgResetBattleAndApply({ doSamsaraResetLevel: false, doBroadcast: false });
        } catch (e) {
            console.warn('[RPG] 开局三件套失败，继续走默认 loadSamsaraState：', e);
        }
        // 兜底：若三件套失败，仍以 Samsara 服务器为准刷一次
        try { await this.loadSamsaraState(); } catch (e) {}
        try { await this.loadLocalKarmaDetection(); } catch (e) {}

        this._initialized = true;
        this.dispatchEvent(new CustomEvent('ready', { bubbles: true, composed: true }));
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 2 新增：统一 HTTP fetch（no-cache；返回 Response）
    // ═══════════════════════════════════════════════════════════════
    _fetchRaw(url, options = {}) {
        const headers = Object.assign({ 'Pragma': 'no-cache' }, (options.headers || {}));
        const opts = Object.assign({}, options, {
            cache: 'no-store',
            headers,
        });
        return fetch(url, opts);
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 2 新增：事件驱动刷新（BroadcastChannel + visibility/focus）
    // ═══════════════════════════════════════════════════════════════
    _getBroadcastChannel() {
        if (this._broadcastChannel) return this._broadcastChannel;
        try {
            if (typeof BroadcastChannel !== 'undefined') {
                this._broadcastChannel = new BroadcastChannel('game-events');
            }
        } catch (e) { this._broadcastChannel = null; }
        return this._broadcastChannel;
    }
    _fireLocalAndBroadcast(type, payload) {
        const ch = this._getBroadcastChannel();
        if (ch) {
            try { ch.postMessage({ type: type, ...(payload || {}) }); } catch (e) {}
        }
        try {
            localStorage.setItem('ge_' + type, JSON.stringify({ value: payload || {}, ts: Date.now() }));
        } catch (e) {}
    }
    _initEventDrivenRefresh() {
        const self = this;

        // 1) BroadcastChannel 跨页广播
        if (!this._bcBound) {
            const ch = this._getBroadcastChannel();
            if (ch) {
                ch.addEventListener('message', (e) => {
                    const t = e.data && e.data.type;
                    if (!t) return;
                    self._handleGameEvent(t, e.data);
                });
            }
            // localStorage 兜底（同 tab 或老浏览器）
            try {
                window.addEventListener('storage', (ev) => {
                    if (!ev || !ev.key || !ev.key.startsWith('ge_')) return;
                    const t = ev.key.slice(3);
                    let payload = {};
                    try { payload = JSON.parse(ev.newValue || '{}').value || {}; } catch (e) {}
                    self._handleGameEvent(t, payload);
                });
            } catch (e) {}
            this._bcBound = true;
        }

        // 2) visibility / focus 兜底 1 次同步
        if (!this._visibilityBound) {
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'visible') self._syncFromServers();
            });
            window.addEventListener('focus', () => self._syncFromServers());
            this._visibilityBound = true;
        }

        // 3) 禁用轮询（旧 startKarmaPolling 现在为 no-op）
        this.stopKarmaPolling();
    }
    _handleGameEvent(type, payload) {
        const self = this;
        switch (type) {
            case 'achievement-unlocked':
                this._showAchievementToast((payload && payload.achievement) || payload || {});
                this._syncFromServers();
                break;
            case 'karma-updated':
            case 'reset-issued':
            case 'level-advanced':
            case 'level-started':
                this._syncFromServers();
                break;
        }
        // 兜底：不区分的事件也会尝试 apply payload 里的 karma_detection_state
        if (payload && payload.karma_detection_state) this._applyKarmaDetectionState(payload.karma_detection_state);
        if (payload && payload.state) { this.samsaraState = payload.state; this.updateSamsaraUI(); }
    }
    async _syncFromServers() {
        try { await this.loadSamsaraState(); } catch (e) {}
        try { await this.loadLocalKarmaDetection(); } catch (e) {}
    }
    _applyKarmaDetectionState(kd) {
        if (!kd) return;
        // 对齐 karma_detection 响应结构：karma.{current,max,single_max,initial} / detection
        const karma = kd.karma || {};
        this.samsaraState = Object.assign({}, (this.samsaraState || {}), {
            karma: (typeof karma.current === 'number') ? karma.current : (this.samsaraState || {}).karma,
            karma_max: (typeof karma.max === 'number') ? karma.max : (this.samsaraState || {}).karma_max,
            karma_single_max: karma.single_max,
            karma_initial: karma.initial,
            detection: (typeof kd.detection === 'number') ? kd.detection : (this.samsaraState || {}).detection,
        });
        this.updateSamsaraUI();
    }
    _showAchievementToast(ach) {
        if (!ach || !ach.id) return;
        const host = this.shadowRoot.getElementById('board-container');
        if (!host) return;
        const el = document.createElement('div');
        el.className = 'achievement-toast-float';
        el.style.cssText = 'position:absolute;top:12px;right:12px;max-width:300px;z-index:9999;background:linear-gradient(135deg,#fef3c7,#fde68a);border:2px solid #b45309;border-radius:14px;padding:12px 16px;box-shadow:0 10px 30px rgba(0,0,0,.35);color:#422006;animation:ach-slide-in .4s ease-out both;pointer-events:none;font-family:system-ui,sans-serif;';
        el.innerHTML = `
            <div style="font-size:12px;opacity:.8;">🏆 成就解锁</div>
            <div style="display:flex;align-items:center;gap:8px;margin-top:4px;">
                <div style="font-size:28px;">${ach.icon || '🎖️'}</div>
                <div>
                    <div style="font-weight:700;font-size:14px;">${this._escapeHtml(ach.name || ach.id)}</div>
                    <div style="font-size:12px;opacity:.9;">${this._escapeHtml(ach.desc || '')}</div>
                </div>
            </div>`;
        host.style.position = host.style.position || 'relative';
        host.appendChild(el);
        setTimeout(() => {
            el.style.animation = 'ach-slide-out .4s ease-in both';
            setTimeout(() => el.remove(), 500);
        }, 4200);
    }
    _escapeHtml(str) {
        if (str == null) return '';
        return String(str).replace(/[&<>"']/g, (m) => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
        }[m]));
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 2 新增：RPG 关卡重置三件套（apply_level → reset_battle → 刷新 UI）
    // ═══════════════════════════════════════════════════════════════
    async rpgResetBattleAndApply(opts = {}) {
        const options = Object.assign({
            doSamsaraResetLevel: false,   // 只重置当前关变量（不切关不换道）
            doBroadcast: true,
        }, opts || {});

        // Step 0（可选）：六道当前关变量软重清 —— 用于「重置所有配置」
        if (options.doSamsaraResetLevel && !this.isSandbox) {
            try {
                const rr = await this._fetchRaw('/samsara/api/levels/reset_level', { method: 'POST' });
                const rj = await rr.json();
                if (rj && rj.state) { this.samsaraState = rj.state; this.updateSamsaraUI(); }
            } catch (e) { console.warn('samsara reset_level 失败', e); }
        }

        // Step 1：apply_level_config（Samsara → 本地难度/回合 并 reset_board）
        let applyResp = null;
        try {
            if (!this.isSandbox) {
                const r = await this._fetchRaw(`${this.apiBase}/api/level/apply`, { method: 'POST' });
                applyResp = await r.json();
                if (applyResp && applyResp.karma_detection_state) this._applyKarmaDetectionState(applyResp.karma_detection_state);
                if (applyResp && applyResp.state) { this.samsaraState = applyResp.state; this.updateSamsaraUI(); }
            }
        } catch (e) {
            console.warn('[RPG] apply_level_config 失败：', e);
            applyResp = null;
        }

        // Step 2：rpg_reset_battle（主模式）→ 或 沙盒 fallback 到 /api/restart
        let resetResp = null;
        try {
            const r = await this._fetchRaw(`${this.apiBase}/api/rpg/reset_battle`, { method: 'POST' });
            resetResp = await r.json();
            if (resetResp && resetResp.karma_detection_state) this._applyKarmaDetectionState(resetResp.karma_detection_state);
            if (resetResp && resetResp.state) { this.samsaraState = resetResp.state; this.updateSamsaraUI(); }
            if (resetResp && resetResp.board_state) { this.boardState = resetResp.board_state; }
        } catch (e) {
            // 沙盒 main.py 未实现 rpg_reset_battle → fallback /api/restart
            try {
                const r = await this._fetchRaw(`${this.apiBase}/api/restart`, { method: 'POST' });
                resetResp = await r.json();
            } catch (e2) {
                console.warn('[RPG] reset_battle + restart 全部失败：', e, e2);
            }
        }

        // Step 3：前端全量重绘（apply / reset 后需要棋盘/棋子刷新）
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

        // Step 4：刷新 Samsara state + level info（用于顶部业力/关卡条）
        try { await this.loadSamsaraState(); } catch (e) {}
        try { await this.loadLocalKarmaDetection(); } catch (e) {}

        if (options.doBroadcast) {
            this._fireLocalAndBroadcast('level-started', {
                from_rpg_reset: true,
                sandbox: this.isSandbox,
                apply_success: !!(applyResp && applyResp.success),
                reset_success: !!(resetResp && resetResp.success),
            });
        }
        return { applyResp, resetResp };
    }

    /** 「重置所有配置」处理器：调用 /api/reset_configs + Samsara 存档重置 + 三件套 */
    async rpgResetConfigsHandler(hardOrSoft) {
        const mode = hardOrSoft === 'hard' ? 'hard' : 'soft';
        // 1. 重置本地棋类配置
        try {
            const r = await this._fetchRaw(`${this.apiBase}/api/reset_configs`, { method: 'POST' });
            const data = (r && r.ok) ? await r.json() : null;
            if (data && !data.success) console.warn('[RPG] reset_configs failed');
        } catch (e) { console.warn('[RPG] reset_configs failed', e); }

        // 2. 非沙盒: 重置 Samsara 业力/关卡
        if (!this.isSandbox) {
            try {
                const r = await fetch('/samsara/api/reset', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ mode }),
                });
                if (r.ok) {
                    try {
                        const d = await r.json();
                        if (d && d.state) { this.samsaraState = d.state; this.updateSamsaraUI(); }
                    } catch (e) {}
                }
            } catch (e) { console.warn('[RPG] samsara reset failed', e); }

            if (mode === 'hard') {
                try {
                    await fetch('/api/achievements/reset', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ mode: 'all' }),
                    });
                } catch (e) {}
            }
            this._fireLocalAndBroadcast('reset-issued', { mode });
        }

        // 3. 三件套 + 全重绘
        await this.rpgResetBattleAndApply({ doSamsaraResetLevel: true, doBroadcast: true });
        if (typeof this.loadConfigs === 'function') await this.loadConfigs();
        if (this.lastMove !== undefined) this.lastMove = null;
        if (typeof this._rpgRerender === 'function') await this._rpgRerender(this);
        this.addMessage('✅ 所有配置已重置' + (mode === 'hard' ? '（含存档）' : ''), 'success');
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
                objective: { type: 'checkmate', description: '将死对方' }
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
        // Phase 2: 已迁移到事件驱动刷新（BroadcastChannel + visibility/focus 兜底）。
        // 此方法保留为空 no-op，以免旧代码 / 外部调用崩溃。所有实际刷新由 _initEventDrivenRefresh 管理。
        if (this._karmaPollingTimer) {
            clearInterval(this._karmaPollingTimer);
            this._karmaPollingTimer = null;
        }
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
        if (objectiveText) objectiveText.textContent = objective.description || '将死对方';
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
                body: JSON.stringify({ event_type: eventType, game_type: 'xiangqi', details })
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
                body: JSON.stringify({ game_type: 'xiangqi' })
            });
            const data = await resp.json();
            this.samsaraState = data.state;
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
            AchievementChecker.checkAfterConfigLoad(this.configs, this.boardState, 'xiangqi');
        }
    }
    _getBoardLayoutConfig() {
        const defaults = {
            grid: {
                line_thickness: 0.03,
                show_horizontal: true,
                show_vertical: true,
                river_gap: true
            },
            palace: {
                enabled: true,
                show_diagonals: true,
                line_thickness: 0.03
            },
            river: {
                enabled: true,
                text: '楚 河          漢 界',
                text_size: 0.5,
                gap_ratio: 1.0
            },
            appearance: {
                background_color: '#f0d9b5',
                line_color: '#5c3a1e',
                palace_line_color: null
            },
            layout: {
                viewbox_padding_left: 0.444,
                viewbox_padding_right: 0.444,
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
                palace_line_color: user.palace_line_color !== undefined
                    ? user.palace_line_color : defaults.appearance.palace_line_color,
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
        const width = geometry.width || 9;
        const height = geometry.height || 10;
        const riverLine = geometry.river_line || 5;
        const palace = geometry.palace || {
            black: { top_left: [3, 0], bottom_right: [5, 2] },
            red: { top_left: [3, 7], bottom_right: [5, 9] }
        };

        let orientation = geometry.orientation;
        if (!orientation) {
            orientation = width > height ? 'vertical' : 'horizontal';
        }

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
        const riverGap = layoutConfig.grid.river_gap;
        const showHorizontal = layoutConfig.grid.show_horizontal;
        const showVertical = layoutConfig.grid.show_vertical;

        if (orientation === 'horizontal') {
            if (showHorizontal) {
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

            if (showVertical) {
                for (let i = 0; i < width; i++) {
                    const x = i;
                    if (riverGap) {
                        const line1 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line1.setAttribute('x1', x);
                        line1.setAttribute('y1', '0');
                        line1.setAttribute('x2', x);
                        line1.setAttribute('y2', riverLine - 1);
                        line1.setAttribute('stroke', lineColor);
                        line1.setAttribute('stroke-width', sw);
                        svg.appendChild(line1);

                        const line2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line2.setAttribute('x1', x);
                        line2.setAttribute('y1', riverLine);
                        line2.setAttribute('x2', x);
                        line2.setAttribute('y2', height - 1);
                        line2.setAttribute('stroke', lineColor);
                        line2.setAttribute('stroke-width', sw);
                        svg.appendChild(line2);
                    } else {
                        const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line.setAttribute('x1', x);
                        line.setAttribute('y1', '0');
                        line.setAttribute('x2', x);
                        line.setAttribute('y2', height - 1);
                        line.setAttribute('stroke', lineColor);
                        line.setAttribute('stroke-width', sw);
                        svg.appendChild(line);
                    }
                }
            }
        } else {
            if (showVertical) {
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

            if (showHorizontal) {
                for (let i = 0; i < height; i++) {
                    const y = i;
                    if (riverGap) {
                        const line1 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line1.setAttribute('x1', '0');
                        line1.setAttribute('y1', y);
                        line1.setAttribute('x2', riverLine - 1);
                        line1.setAttribute('y2', y);
                        line1.setAttribute('stroke', lineColor);
                        line1.setAttribute('stroke-width', sw);
                        svg.appendChild(line1);

                        const line2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line2.setAttribute('x1', riverLine);
                        line2.setAttribute('y1', y);
                        line2.setAttribute('x2', width - 1);
                        line2.setAttribute('y2', y);
                        line2.setAttribute('stroke', lineColor);
                        line2.setAttribute('stroke-width', sw);
                        svg.appendChild(line2);
                    } else {
                        const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                        line.setAttribute('x1', '0');
                        line.setAttribute('y1', y);
                        line.setAttribute('x2', width - 1);
                        line.setAttribute('y2', y);
                        line.setAttribute('stroke', lineColor);
                        line.setAttribute('stroke-width', sw);
                        svg.appendChild(line);
                    }
                }
            }
        }

        if (layoutConfig.palace.enabled && layoutConfig.palace.show_diagonals) {
            const palaceLines = [];
            if (palace.black) {
                const tl = palace.black.top_left;
                const br = palace.black.bottom_right;
                palaceLines.push([tl, br]);
                palaceLines.push([[br[0], tl[1]], [tl[0], br[1]]]);
            }
            if (palace.red) {
                const tl = palace.red.top_left;
                const br = palace.red.bottom_right;
                palaceLines.push([tl, br]);
                palaceLines.push([[br[0], tl[1]], [tl[0], br[1]]]);
            }
            const palaceLineColor = layoutConfig.appearance.palace_line_color || lineColor;
            const palaceSw = String(layoutConfig.palace.line_thickness);
            palaceLines.forEach(([start, end]) => {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', start[0]);
                line.setAttribute('y1', start[1]);
                line.setAttribute('x2', end[0]);
                line.setAttribute('y2', end[1]);
                line.setAttribute('stroke', palaceLineColor);
                line.setAttribute('stroke-width', palaceSw);
                svg.appendChild(line);
            });
        }

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

        if (layoutConfig.river.enabled) {
            const riverText = document.createElement('div');
            riverText.className = 'river-text';
            riverText.textContent = layoutConfig.river.text;
            riverText.style.color = lineColor;

            if (orientation === 'horizontal') {
                const riverTopPct = 5 + ((riverLine - 0.5) / (height - 1)) * 90;
                riverText.style.top = `${riverTopPct}%`;
                riverText.style.left = '50%';
                riverText.style.transform = 'translate(-50%, -50%)';
                riverText.style.writingMode = 'horizontal-tb';
            } else {
                const riverLeftPct = 5 + ((riverLine - 0.5) / (width - 1)) * 90;
                riverText.style.left = `${riverLeftPct}%`;
                riverText.style.top = '50%';
                riverText.style.transform = 'translate(-50%, -50%)';
                riverText.style.writingMode = 'vertical-rl';
            }

            container.appendChild(riverText);
        }

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
        const width = geometry.width || 9;
        const height = geometry.height || 10;

        const [x, y] = piece.position;
        const leftPct = 5 + (x / (width - 1)) * 90;
        const topPct = 5 + (y / (height - 1)) * 90;

        el.style.left = `${leftPct}%`;
        el.style.top = `${topPct}%`;
        el.style.transform = 'translate(-50%, -50%)';
        el.style.fontFamily = fontFamily;

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
        const width = geometry.width || 9;
        const height = geometry.height || 10;
        this.validMoves.forEach(([x, y]) => {
            const isCapture = this.isCaptureTarget(x, y);
            const indicator = document.createElement('div');
            indicator.className = isCapture ? 'valid-move-indicator capture-move-indicator' : 'valid-move-indicator';
            const leftPct = 5 + (x / (width - 1)) * 90;
            const topPct = 5 + (y / (height - 1)) * 90;
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
                const prevTurn = this.boardState?.current_turn;
                this.boardState = data.board_state;
                this.lastMove = this.boardState.move_history.slice(-1)[0];
                this.renderPieces();
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateMechanisms();
                this.loadTokenStats();
                if (window.AchievementChecker) {
                    AchievementChecker.checkAfterMove(this.boardState, this.configs, 'xiangqi');
                }
                // 若对方被冻结跳过，给出可见提示（此时回合未交替）
                this._notifySkippedTurns(prevTurn);

                await this.incrementTurn();

                const lastMoveData = this.boardState.move_history.slice(-1)[0];
                if (lastMoveData && lastMoveData.captured_piece) {
                    const captured = lastMoveData.captured_piece;
                    let eventType = 'captured';
                    if (captured.type === 'pawn') eventType = 'capture_pawn';
                    else if (['knight', 'bishop', 'cannon'].includes(captured.type)) eventType = 'capture_medium';
                    else if (captured.type === 'rook') eventType = 'capture_rook';
                    await this.reportKarmaEvent(eventType, { piece: captured.type });
                }

                this._dispatchMoveEvent();

                if (this.boardState.game_status.state === 'ended') {
                    if (this.boardState.game_status.winner === 'red') {
                        await this.reportKarmaEvent('checkmate');
                    }
                    this.showGameOver();
                    this._dispatchGameEndEvent();
                    this.aiThinking = false;
                    return;
                }

                // 仅当回合同样落到 AI 方时才请求 AI 走棋。
                // 若对方被冻结跳过、回合已让回玩家，则不应再调 AI，
                // 否则后端会返回「不是AI回合」，前端误报为错误。
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
                        AchievementChecker.checkAfterMove(this.boardState, this.configs, 'xiangqi');
                    }

                this.highlightAIMovedPiece(data.ai_move.piece_id);

                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }

                if (data.ai_move?.captured) {
                    this.addMessage(`AI走了${this.getPieceName(data.ai_move.piece_id)}`, 'info');
                    await this.reportKarmaEvent('captured', { piece: data.ai_move.captured.type });
                }

                if (this.boardState.game_status.is_check) {
                    await this.reportKarmaEvent('check');
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

    // 检测并播报「跳过回合」：某方被冻结时后端会直接把回合让回对方，
    // 前端需要给出可见反馈，否则玩家只看到回合数没变、以为指令没生效。
    _notifySkippedTurns(prevTurn) {
        const mechanisms = this.boardState?.mechanisms || {};
        const skips = mechanisms.skip_turns || [];
        if (skips.length === 0) return;
        const currentTurn = this.boardState?.current_turn;
        // 若本次走棋后回合并未交替（prevTurn 与 currentTurn 相同），
        // 说明中间被冻结的一方被跳过了。
        if (prevTurn && currentTurn === prevTurn) {
            const frozen = skips
                .map(s => `${s.side === 'red' ? '红方' : '黑方'}${s.reason ? `（${s.reason}）` : ''}`)
                .join('、');
            this.addMessage(`⏸️ ${frozen} 被冻结，本回合直接跳过，继续由你行动`, 'info');
        }
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
                    // 重新拉取最新配置并重绘棋盘棋子（含 codeAI 新增/改名后的棋子名称）
                    await this.refreshPiecesFromLatest();
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
                    AchievementChecker.checkAfterCommand(data, command, this.configs, this.boardState, 'xiangqi');
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

    // 把指令返回的「最终结果」写入 AI 面板（含成功 / 拒绝 / 拦截 / 校验等多种状态）
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

    // 重新拉取最新配置并重绘棋盘与棋子，确保 codeAI 等命令最新写入的棋子 type/name 生效
    async refreshPiecesFromLatest() {
        await this.loadConfigs();
        // 关键：AI 改规则/改走法后，当前选中棋子的 validMoves 缓存已失效。
        // 必须清空选中态与走法缓存，否则下一次点击棋盘会拿旧缓存判定合法性，
        // 出现「规则明明改了、旧走法高亮仍在、点了才被后端拒绝」的假象。
        this.clearSelection();
        this.renderBoard();
        this.renderPieces();
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
        const gs = this.boardState?.game_status;
        if (!gs || gs.state !== 'ended') return;

        const isPlayerWin = gs.winner === 'red';
        const winner = gs.winner === 'red' ? '红方（我方）' : '黑方（AI）';
        const container = this.shadowRoot.getElementById('board-container');

        // 清旧弹窗
        for (const sel of ['.game-over-overlay', '.victory-reward-overlay']) {
            const e = container.querySelector(sel);
            if (e) e.remove();
        }

        // ── (1) 若胜，先拿到 progression / next_level 数据 ──
        let rewards = null;
        let next_level = null;
        let noCheatThisLevel = !!(this.samsaraState && this.samsaraState.no_cheat_this_level);
        if (isPlayerWin) {
            try {
                // 使用 body 方式（旧 query 参数也保留向后兼容）
                const r = await this._fetchRaw(`${this.apiBase}/api/level/complete`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ won: true, no_cheat: noCheatThisLevel, boss_defeated: false }),
                });
                const data = await r.json();
                rewards = data && data.rewards ? data.rewards : data;
                // next_level 可能在 rewards.next_level 或 data.next_level 或顶层 rewards
                next_level = (rewards && rewards.next_level) || (data && data.next_level) || null;
                this._lastResolveRewards = { rewards, next_level, isPlayerWin: true };
                // 胜负结算完成：广播 level-advanced 给总坛刷新进度
                this._fireLocalAndBroadcast('level-advanced', { isPlayerWin: true, next_level });
                try {
                    if (data && data.state) { this.samsaraState = data.state; this.updateSamsaraUI(); }
                    else await this.loadSamsaraState();
                } catch (e) {}
            } catch (e) {
                console.error('Failed to resolve level rewards:', e);
            }
        } else {
            // 败北：也上报 progression 拿失败奖励/惩罚（若有）
            try {
                const r = await this._fetchRaw(`${this.apiBase}/api/level/complete`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ won: false, no_cheat: noCheatThisLevel, boss_defeated: false }),
                });
                const data = await r.json();
                rewards = data && data.rewards ? data.rewards : data;
                this._lastResolveRewards = { rewards, next_level: null, isPlayerWin: false };
            } catch (e) { console.warn('Lose resolve failed', e); }
        }

        // ── (2) 取最终业力 / 识破 供弹窗显示 ──
        const karmaNow = (this.samsaraState && typeof this.samsaraState.karma === 'number')
            ? this.samsaraState.karma
            : null;
        const karmaMax = (this.samsaraState && typeof this.samsaraState.karma_max === 'number')
            ? this.samsaraState.karma_max
            : null;
        const detNow = (this.samsaraState && typeof this.samsaraState.detection === 'number')
            ? Math.round(this.samsaraState.detection * 1000) / 10
            : null;
        const skillPointsEarned = (rewards && typeof rewards.skill_points === 'number')
            ? rewards.skill_points : 0;
        const bonusReasons = (rewards && Array.isArray(rewards.bonus_reasons)) ? rewards.bonus_reasons : [];
        const sandboxUnlocked = rewards && rewards.sandbox_unlocked;
        const realmAdvance = rewards && rewards.realm_advance;
        const nextLvInfo = next_level && next_level.level ? next_level.level : null;

        // ── (3) 构造三按钮可见性（主模式 vs 沙盒）──
        // 沙盒：只显示「再来一次」+「返回沙盒总坛」
        // 主模式胜：下一关（next_level 有才启用）/ 再来一次 / 返回总坛
        // 主模式负：下一关灰 / 再来一次 / 返回总坛
        const isSandbox = !!this.isSandbox;

        let nextBtnLabel = '➡️ 下一关';
        let nextBtnDisabled = true;
        let showNextBtn = !isSandbox;
        if (isSandbox) {
            nextBtnLabel = '➡️ 下一关（沙盒无关卡）';
            showNextBtn = false; // 沙盒完全遮罩
        } else if (isPlayerWin) {
            nextBtnDisabled = !next_level;
        } else {
            nextBtnLabel = '➡️ 下一关（需胜利才可推进）';
            nextBtnDisabled = true;
        }

        // 返回大陆文案
        let returnLabel = '🏠 返回大陆';
        let returnHref = '/overworld';
        if (isSandbox) {
            returnLabel = '🏠 返回大地图';
            returnHref = '/overworld';
        }

        // ── (4) HTML 内容组装 ──
        const titleHtml = isPlayerWin
            ? '<h2>🏆 通关胜利</h2>'
            : '<h2>💀 本局败北</h2>';
        const summaryHtml = `
            <div style="margin:10px 0 14px;padding:10px 14px;border-radius:10px;background:rgba(255,255,255,.06);color:#e5e7eb;">
                <div style="margin-bottom:6px;"><b>${winner}</b> 获胜</div>
                <div style="display:grid;grid-template-columns:1fr 1fr;gap:6px 14px;font-size:13px;opacity:.95;">
                    <div>业力：<b>${karmaNow !== null ? `${karmaNow}${karmaMax !== null ? ` / ${karmaMax}` : ''}` : '—'}</b></div>
                    <div>识破：<b>${detNow !== null ? `${detNow}%` : '—'}</b></div>
                    <div>无作弊通关：<b>${noCheatThisLevel ? '✅ 是' : '❌ 否'}</b></div>
                    <div>技能点：<b style="color:#fde047;">${skillPointsEarned >= 0 ? '+' : ''}${skillPointsEarned}</b></div>
                </div>
                ${nextLvInfo ? `<div style="margin-top:10px;font-size:13px;">下一关：<b>${this._escapeHtml(nextLvInfo.name || '第' + (next_level.level_index + 1) + '关')}</b>${next_level && next_level.realm_name ? `（${this._escapeHtml(next_level.realm_name)}）` : ''}</div>` : ''}
                ${realmAdvance && realmAdvance.realm_switched ? `<div style="margin-top:6px;font-size:13px;color:#a7f3d0;">🆙 道切换成功：进入「${this._escapeHtml(realmAdvance.new_realm_name || realmAdvance.new_realm || '')}」</div>` : ''}
                ${sandboxUnlocked ? `<div style="margin-top:8px;font-size:13px;color:#a5f3fc;">🔓 沙盒模式已解锁</div>` : ''}
                ${bonusReasons && bonusReasons.length ? `<ul style="margin:10px 0 0;padding-left:18px;font-size:13px;line-height:1.6;">${bonusReasons.map(r => `<li>${this._escapeHtml(String(r))}</li>`).join('')}</ul>` : ''}
            </div>
        `;
        const buttonsHtml = `
            <div style="display:flex;flex-wrap:wrap;gap:8px;justify-content:center;">
                ${showNextBtn ? `<button class="btn-primary rpg-nextbtn" ${nextBtnDisabled ? 'disabled style="opacity:.55;cursor:not-allowed;"' : ''}>${nextBtnLabel}</button>` : ''}
                <button class="btn-primary rpg-retrybtn">🔁 再来一次</button>
                <button class="btn-primary rpg-returnbtn">${returnLabel}</button>
            </div>
        `;

        const overlay = document.createElement('div');
        overlay.className = 'game-over-overlay';
        overlay.innerHTML = `${titleHtml}${summaryHtml}${buttonsHtml}`;
        // 绑定按钮
        const nextBtn = overlay.querySelector('.rpg-nextbtn');
        const retryBtn = overlay.querySelector('.rpg-retrybtn');
        const returnBtn = overlay.querySelector('.rpg-returnbtn');
        if (nextBtn && !nextBtnDisabled) {
            nextBtn.addEventListener('click', () => {
                overlay.querySelector('.rpg-nextbtn').disabled = true;
                overlay.querySelector('.rpg-nextbtn').textContent = '⏳ 进入下一关...';
                this.advanceNextLevel(next_level).finally(() => {});
            });
        } else if (nextBtn) {
            nextBtn.addEventListener('click', () => this.addMessage('⚠️ 下一关不可用（先赢得本局或已是最后一关）', 'info'));
        }
        retryBtn.addEventListener('click', () => this.restart());
        returnBtn.addEventListener('click', () => {
            window.location.href = returnHref;
        });
        container.appendChild(overlay);
    }

    /** 胜负页「下一关」按钮：推进关卡并进入下一局初始化。 */
    async advanceNextLevel(prevNextLevel) {
        if (this.isSandbox) {
            this.addMessage('⚠️ 沙盒模式不支持推进关卡', 'info');
            return;
        }
        try {
            // 推进：用 Samsara /api/levels/advance（已返回 next_level + state）
            const r = await this._fetchRaw('/samsara/api/levels/advance', { method: 'POST' });
            const data = await r.json();
            let nl = prevNextLevel;
            if (data && data.next_level) nl = data.next_level;
            if (data && data.state) { this.samsaraState = data.state; this.updateSamsaraUI(); }
            this._fireLocalAndBroadcast('level-advanced', { next_level: nl });
            // 清胜负弹窗
            const bc = this.shadowRoot.getElementById('board-container');
            for (const sel of ['.game-over-overlay', '.victory-reward-overlay']) {
                const e = bc && bc.querySelector(sel); if (e) e.remove();
            }
            // 下一关三件套
            await this.rpgResetBattleAndApply({ doSamsaraResetLevel: false, doBroadcast: true });
            this.addMessage('➡️ 已进入下一关', 'success');
        } catch (e) {
            console.error('advanceNextLevel failed:', e);
            this.addMessage('❌ 进入下一关失败，请从总坛重试', 'error');
        }
    }

    showVictoryReward(rewards) {
        // Phase 2 兼容：showVictoryReward 已在 showGameOver 合并显示。
        // 外部调用仍允许显示一张独立奖励卡片，同时点击「继续」不关闭胜负弹窗。
        const container = this.shadowRoot.getElementById('board-container');
        const existing = container.querySelector('.victory-reward-overlay');
        if (existing) existing.remove();

        const skillPoints = rewards?.skill_points || 0;
        const reasons = rewards?.bonus_reasons || [];
        const sandboxUnlocked = rewards?.sandbox_unlocked;

        const reasonsHtml = reasons.map(r => `<li>${this._escapeHtml(String(r))}</li>`).join('');
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
        const overlay = this.shadowRoot.querySelector('.game-over-overlay');
        if (overlay) overlay.remove();
        const rewardOverlay = this.shadowRoot.querySelector('.victory-reward-overlay');
        if (rewardOverlay) rewardOverlay.remove();

        // Phase 2 修复：restart 不再仅 /api/restart，而是 RPG 三件套（apply_level + reset_battle + 全量重绘）
        try {
            await this.rpgResetBattleAndApply({ doSamsaraResetLevel: false, doBroadcast: true });
            this.addMessage('🔄 游戏已重新开始', 'info');
        } catch (e) {
            // 极端 fallback
            try {
                const resp = await this._fetchRaw(`${this.apiBase}/api/restart`, { method: 'POST' });
                const data = await resp.json();
                if (data && data.success) {
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
                    this.addMessage('🔄 游戏已重新开始', 'info');
                }
            } catch (e2) {
                this.addMessage('❌ 重新开始失败', 'error');
                console.error(e, e2);
            }
        }
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
                window.open('http://localhost:8080/achievements', '_blank');
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
            if (!confirm('确定要重置所有配置吗？所有自定义规则将被清除。')) return;
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
                // 防御：AI 可能在选中棋子期间改了规则，本地 validMoves 已过期。
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
    }

    _getGridCoordsFromEvent(e) {
        const container = this.shadowRoot.getElementById('board-container');
        const rect = container.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 9;
        const height = geometry.height || 10;

        const boardX = (x / rect.width - 0.05) / 0.9 * (width - 1);
        const boardY = (y / rect.height - 0.05) / 0.9 * (height - 1);

        const gridX = Math.round(boardX);
        const gridY = Math.round(boardY);

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
        const width = geometry.width || 9;
        const height = geometry.height || 10;

        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('id', 'coord-dots-group');
        g.style.pointerEvents = 'all';

        for (let x = 0; x < width; x++) {
            for (let y = 0; y < height; y++) {
                const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                dot.setAttribute('class', 'coord-dot');
                dot.setAttribute('cx', x);
                dot.setAttribute('cy', y);
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

if (!customElements.get('xiangqi-board')) customElements.define('xiangqi-board', XiangqiBoard);

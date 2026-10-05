class HeibaiqiBoard extends HTMLElement {
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
        this._karmaPollingTimer = null;  // 轮询定时器（事件驱动替代，保留字段防旧代码崩溃）

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
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = '/static/style.css?v=20260806a';
        this.shadowRoot.appendChild(link);

        const container = document.createElement('div');
        container.id = 'app';
        container.innerHTML = this._getHtmlTemplate();
        this.shadowRoot.appendChild(container);
    }

    async loadConfigs() {
        const resp = await fetch(`${this.apiBase}/api/config/all`, { cache: 'no-store' });
        this.configs = await resp.json();
        this.boardState = this.configs.board_state;
        this.uiConfig = this.configs.ui_config;
    }

    _getBoardLayoutConfig() {
        const defaults = {
            grid: {
                line_thickness: 0.03,
                show_horizontal: true,
                show_vertical: true,
                river_gap: false
            },
            palace: {
                enabled: false,
                show_diagonals: false,
                line_thickness: 0.03
            },
            river: {
                enabled: false,
                text: '',
                text_size: 0.5,
                gap_ratio: 1.0
            },
            appearance: {
                background_color: '#1a5d3a',
                line_color: '#000000',
                palace_line_color: null
            },
            layout: {
                viewbox_padding_left: 0,
                viewbox_padding_right: 0,
                viewbox_padding_top: 0,
                viewbox_padding_bottom: 0,
                board_size: '90vmin'
            },
            decorations: {
                border: {
                    enabled: true,
                    thickness: 0.06,
                    color: '#000000'
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
        const width = geometry.width || 8;
        const height = geometry.height || 8;

        const bgColor = layoutConfig.appearance.background_color;
        container.style.backgroundColor = bgColor;
        this.style.setProperty('--board-bg', bgColor);

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.classList.add('board-grid');
        // 单元格模型：viewBox 0 0 width height，线条落格边界
        svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.style.width = '100%';
        svg.style.height = '100%';

        const lineColor = layoutConfig.appearance.line_color;
        const sw = String(layoutConfig.grid.line_thickness);

        // 9 条竖线 x=0..width
        if (layoutConfig.grid.show_vertical) {
            for (let i = 0; i <= width; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', i);
                line.setAttribute('y1', 0);
                line.setAttribute('x2', i);
                line.setAttribute('y2', height);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }
        // 9 条横线 y=0..height
        if (layoutConfig.grid.show_horizontal) {
            for (let i = 0; i <= height; i++) {
                const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
                line.setAttribute('x1', 0);
                line.setAttribute('y1', i);
                line.setAttribute('x2', width);
                line.setAttribute('y2', i);
                line.setAttribute('stroke', lineColor);
                line.setAttribute('stroke-width', sw);
                svg.appendChild(line);
            }
        }

        // 边框
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

        const pieces = this.boardState?.pieces || [];
        pieces.forEach(piece => {
            if (!piece.is_alive) return;
            this.createPieceElement(piece, piecesTheme);
        });

        // 高亮上一步落子点
        if (this.lastMove?.to) {
            const el = container.querySelector(`[data-pos="${this.lastMove.to[0]},${this.lastMove.to[1]}"]`);
            if (el) el.classList.add('last-moved');
        }
    }

    createPieceElement(piece, theme) {
        const container = this.shadowRoot.getElementById('board-container');
        const el = document.createElement('div');
        el.className = `piece ${piece.side}`;
        el.dataset.pieceId = piece.id;
        el.dataset.pos = `${piece.position[0]},${piece.position[1]}`;

        const geometry = this.configs.board?.geometry || {};
        const width = geometry.width || 8;
        const height = geometry.height || 8;

        const [x, y] = piece.position;
        // 单元格中心定位
        const leftPct = ((x + 0.5) / width) * 100;
        const topPct = ((y + 0.5) / height) * 100;

        el.style.left = `${leftPct}%`;
        el.style.top = `${topPct}%`;
        el.style.transform = 'translate(-50%, -50%)';

        // 按 side 上色（支持 ui_config 与 custom_properties 覆盖）
        if (piece.side === 'black') {
            el.style.background = theme.black || '#000000';
            el.style.borderColor = theme.black || '#000000';
        } else {
            el.style.background = theme.white || '#ffffff';
            el.style.borderColor = '#1a1a1a';
        }

        if (piece.custom_properties) {
            const cp = piece.custom_properties;
            if (cp.color) el.style.background = cp.color;
            if (cp.bg) el.style.background = cp.bg;
        }

        // 仅 coordInsertMode 下可点（选坐标）；正常落子由 valid-move-indicator 指示器处理
        el.addEventListener('click', (e) => {
            e.stopPropagation();
            if (this.coordInsertMode) {
                this.onCoordDotClick(x, y);
            }
            // 非选坐标模式：点击已有棋子无操作（黑白棋点空格落子）
        });

        container.appendChild(el);
    }

    clearValidMoves() {
        this.shadowRoot.querySelectorAll('.valid-move-indicator').forEach(el => el.remove());
    }

    clearSelection() {
        // 黑白棋无选子状态
        this.clearValidMoves();
    }

    async renderValidPlacements() {
        this.clearValidMoves();
        if (this.boardState?.game_status?.state === 'ended') return;
        // 仅玩家自己可控的回合才显示可落点；AI 回合 / AI 思考时不显示
        if (!this._isCurrentTurnPlayerControlled()) return;
        const side = this.boardState?.current_turn || 'black';
        try {
            const resp = await fetch(`${this.apiBase}/api/valid_moves`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ side })
            });
            const data = await resp.json();
            this.validMoves = data.moves || [];
            const container = this.shadowRoot.getElementById('board-container');
            const geometry = this.configs.board?.geometry || {};
            const width = geometry.width || 8;
            const height = geometry.height || 8;
            this.validMoves.forEach(([x, y]) => {
                const indicator = document.createElement('div');
                indicator.className = 'valid-move-indicator';
                indicator.dataset.pos = `${x},${y}`;
                indicator.style.left = `${((x + 0.5) / width) * 100}%`;
                indicator.style.top = `${((y + 0.5) / height) * 100}%`;
                indicator.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (this.coordInsertMode) {
                        this.onCoordDotClick(x, y);
                        return;
                    }
                    this.placeStone(x, y);
                });
                container.appendChild(indicator);
            });
        } catch (e) {
            console.error('获取合法落子点失败:', e);
        }
    }

    highlightPiece(piece) {
        // 黑白棋无选子高亮
    }

    showValidMoves() {
        // 已被 renderValidPlacements 取代
    }

    async onPieceClick(piece) {
        // 黑白棋：点击已有棋子无操作（落子点由 valid-move-indicator 处理）
    }

    async placeStone(x, y) {
        if (this.aiThinking) return;
        if (this.boardState?.game_status?.state === 'ended') return;
        if (!this._isCurrentTurnPlayerControlled()) return;

        this.clearValidMoves();
        this.aiThinking = true;

        try {
            const resp = await fetch(`${this.apiBase}/api/move`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ to: [x, y] })
            });
            const data = await resp.json();

            if (data.success) {
                this.boardState = data.board_state;
                this.lastMove = this.boardState.move_history?.slice(-1)[0] || null;
                this.renderPieces();
                await this._animateFlips(data.flipped || []);
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateMechanisms();
                this.loadTokenStats();
                await this.loadSamsaraState();
                this._dispatchMoveEvent();

                if (this.boardState.game_status.state === 'ended') {
                    this.showGameOver();
                    this._dispatchGameEndEvent();
                    this.aiThinking = false;
                    return;
                }
                await this.renderValidPlacements();
                await this.sleep(800);
                if (this._isCurrentTurnAITurn()) {
                    await this.makeAIMove();
                }
            } else {
                this.addMessage(data.message || '落子失败', 'error');
                await this.renderValidPlacements();
            }
        } catch (e) {
            this.addMessage(`网络错误: ${e.message}`, 'error');
            this._dispatchError('落子失败', e);
            await this.renderValidPlacements();
        }
        this.aiThinking = false;
    }

    _animateFlips(flippedIds) {
        return new Promise(resolve => {
            if (!flippedIds || flippedIds.length === 0) { resolve(); return; }
            let pending = flippedIds.length;
            flippedIds.forEach(id => {
                const el = this.shadowRoot.querySelector(`[data-piece-id="${id}"]`);
                if (el) {
                    el.classList.add('flipping');
                    const onEnd = () => {
                        el.classList.remove('flipping');
                        el.removeEventListener('animationend', onEnd);
                        pending--;
                        if (pending <= 0) resolve();
                    };
                    el.addEventListener('animationend', onEnd);
                    setTimeout(() => {
                        if (pending > 0) {
                            el.classList.remove('flipping');
                            pending--;
                            if (pending <= 0) resolve();
                        }
                    }, 500);
                } else {
                    pending--;
                    if (pending <= 0) resolve();
                }
            });
        });
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
                this.lastMove = data.ai_move || null;
                this.renderPieces();
                await this._animateFlips(data.ai_move?.flipped || []);
                if (data.ai_move?.to) {
                    const el = this.shadowRoot.querySelector(`[data-pos="${data.ai_move.to[0]},${data.ai_move.to[1]}"]`);
                    if (el) {
                        this.shadowRoot.querySelectorAll('.piece').forEach(p => p.classList.remove('ai-moved'));
                        el.classList.add('ai-moved');
                    }
                }
                this.updateTurnIndicator();
                this.updateActiveRules();
                this.updateGameObjectives();
                this.updateMechanisms();
                await this.loadSamsaraState();

                const messages = this.shadowRoot.getElementById('ai-messages');
                const lastMsg = messages.lastElementChild;
                if (lastMsg && lastMsg.textContent.includes('思考中')) {
                    lastMsg.remove();
                }
                this.addMessage(`AI落子于 (${data.ai_move.to[0]},${data.ai_move.to[1]})，翻转 ${data.ai_move.flipped?.length || 0} 子`, 'info');

                this._dispatchMoveEvent();

                if (this.boardState.game_status.state === 'ended') {
                    this.showGameOver();
                    this._dispatchGameEndEvent();
                    return;
                }
                await this.renderValidPlacements();
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
        const mover = this.boardState?.current_turn === 'black' ? 'white' : 'black';

        this.dispatchEvent(new CustomEvent('move', {
            bubbles: true,
            composed: true,
            detail: {
                captured: lastMove?.flipped || null,
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
        const currentTurn = this.boardState?.current_turn || 'black';
        if (this._isCurrentTurnAIControlled()) return true;
        if (!this._isCurrentTurnPlayerControlled()) return true;
        return false;
    }

    _isCurrentTurnAIControlled() {
        const mechanisms = this.boardState?.mechanisms || {};
        const currentTurn = this.boardState?.current_turn || 'black';
        const aiControl = mechanisms.ai_control || [];
        return aiControl.some(item => item.side === currentTurn && item.remaining !== 0);
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
                    this.addMessage(`✅ ${data.message}`, 'success');
                    if (data.refresh_page) {
                        await this.sleep(500);
                        window.location.reload();
                        return;
                    }
                    await this.loadConfigs();
                    this.renderBoard();
                    this.renderPieces();
                    this.updateTurnIndicator();
                    await this.renderValidPlacements();
                    this.updateActiveRules();
                    this.updateGameObjectives();
                    this.updateAIPersonality();
                    this.updateMechanisms();
                    this.loadTokenStats();
                    await this.loadSamsaraState();

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

    showThinking(text, stage) {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        const textEl = this.shadowRoot.getElementById('thinking-text');
        const stageEl = this.shadowRoot.getElementById('thinking-stage');

        if (textEl) textEl.textContent = text;
        if (stageEl) stageEl.textContent = `阶段: ${stage}`;
        if (overlay) overlay.classList.add('show');

        const boardContainer = this.shadowRoot.getElementById('board-container');
        if (boardContainer) boardContainer.classList.add('board-locked');
        const commandInput = this.shadowRoot.getElementById('command-input');
        if (commandInput) commandInput.disabled = true;

        this.pollThinkingStatus();
    }

    hideThinking() {
        const overlay = this.shadowRoot.getElementById('thinking-overlay');
        if (overlay) overlay.classList.remove('show');

        const boardContainer = this.shadowRoot.getElementById('board-container');
        if (boardContainer) boardContainer.classList.remove('board-locked');
        const commandInput = this.shadowRoot.getElementById('command-input');
        if (commandInput) commandInput.disabled = false;

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
                console.error('轮询思考状态失败:', e);
            }
        }, 500);
    }

    async showLogs() {
        const modal = this.shadowRoot.getElementById('logs-modal');
        if (modal) modal.classList.add('show');
        await this.refreshLogs();
    }

    hideLogs() {
        const modal = this.shadowRoot.getElementById('logs-modal');
        if (modal) modal.classList.remove('show');
    }

    async refreshLogs() {
        const container = this.shadowRoot.getElementById('logs-container');
        if (!container) return;

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

    async init() {
        if (this._initialized) return;
        await this.loadConfigs();
        this.renderBoard();
        this.renderPieces();
        this.updateTurnIndicator();
        await this.renderValidPlacements();
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
                    await instance.renderValidPlacements();
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

    startKarmaPolling() {
        // 已迁移到事件驱动刷新（BroadcastChannel + visibility/focus 兜底）。
        // 此方法保留为空 no-op，以免旧代码 / 外部调用崩溃。
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

    async loadSamsaraState() {
        try {
            const resp = await fetch(`${this.apiBase}/api/karma_detection`, { cache: 'no-store' });
            const data = await resp.json();
            if (data.success) {
                this.samsaraState = {
                    karma: data.karma?.current ?? 0,
                    karma_max: data.karma?.max ?? 120,
                    detection: data.detection ?? 0,
                    current_turn: this.boardState?.game_status?.turn_count || 0,
                    turn_limit: 20,
                    objective: { description: this._getObjectiveDescription() }
                };
            } else {
                throw new Error(data.message || 'failed');
            }
        } catch (e) {
            this.samsaraState = {
                karma: 50,
                karma_max: 120,
                detection: 0,
                current_turn: this.boardState?.game_status?.turn_count || 0,
                turn_limit: 20,
                objective: { description: this._getObjectiveDescription() }
            };
        }
        this.updateSamsaraUI();
        await this.loadLevelInfo();
    }

    _getObjectiveDescription() {
        const objs = this.configs?.board?.objectives || [];
        const primary = objs.find(o => o.category === 'victory') || objs[0];
        return primary?.description || primary?.name || '占领更多棋子';
    }

    async loadLevelInfo() {
        try {
            const resp = await fetch(`${this.apiBase}/api/level/info`, { cache: 'no-store' });
            const data = await resp.json();
            this.levelInfo = data.current_level || null;
            this.updateLevelDisplay();
        } catch (e) {
            this.levelInfo = null;
        }
    }

    updateLevelDisplay() {
        const levelBar = this.shadowRoot.getElementById('level-info-bar');
        if (!levelBar) return;
        if (!this.levelInfo) { levelBar.innerHTML = ''; return; }
        const typeLabels = { standard: '对弈', puzzle: '残局', objective: '目标', boss: 'Boss', sandbox: '沙盒' };
        const typeLabel = typeLabels[this.levelInfo.type] || this.levelInfo.type || '';
        const name = this.levelInfo.name || '';
        const desc = this.levelInfo.description || this.levelInfo.objective?.description || '';
        levelBar.innerHTML = `<span class="level-realm">${this.levelInfo.realm_name || '地狱道'}</span> › <span class="level-name">${name}</span> <span class="level-type-badge">${typeLabel}</span>`;
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

        if (karmaFill) karmaFill.style.width = `${Math.min(100, (karma / maxKarma) * 100)}%`;
        if (karmaValue) karmaValue.textContent = `${karma}/${maxKarma}`;
        if (detectionFill) detectionFill.style.width = `${Math.min(100, detection)}%`;
        if (detectionValue) detectionValue.textContent = `${Math.round(detection)}%`;
        if (turnFill) turnFill.style.width = `${Math.min(100, (currentTurn / maxTurns) * 100)}%`;
        if (turnValue) turnValue.textContent = `${currentTurn}/${maxTurns}`;
        if (objectiveText) objectiveText.textContent = objective.description || '占领更多棋子';
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

    _getHtmlTemplate() {
        return `
        <div class="ambient-effects">
            <div class="fire-glow fire-glow-1"></div>
            <div class="fire-glow fire-glow-2"></div>
            <div class="fire-glow fire-glow-3"></div>
            <div class="smoke-particles"></div>
        </div>

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
                <div class="modal-header">
                    <h3>⚙ 设 置</h3>
                    <button id="close-settings" class="modal-close">✕</button>
                </div>
                <div class="modal-body">
                    <div class="form-group">
                        <label>DeepSeek API Key</label>
                        <input type="password" id="api-key-input" placeholder="输入您的API密钥">
                        <small>您的API密钥仅保存在本地，不会发送到我们的服务器</small>
                    </div>
                    <div class="form-group">
                        <label>AI难度</label>
                        <select id="difficulty-select">
                            <option value="easy">新手</option>
                            <option value="normal" selected>标准</option>
                            <option value="hard">挑战</option>
                            <option value="master">大师</option>
                        </select>
                    </div>
                </div>
                <div class="modal-footer">
                    <button id="save-settings" class="btn-primary">保存设置</button>
                </div>
            </div>
        </div>

        <div id="logs-modal" class="modal">
            <div class="modal-content logs-content">
                <div class="modal-header">
                    <h3>📜 AI 对话日志</h3>
                    <button id="close-logs" class="modal-close">✕</button>
                </div>
                <div class="modal-body">
                    <div id="logs-container" class="logs-container"></div>
                </div>
                <div class="modal-footer">
                    <button id="btn-clear-logs" class="btn danger">清空日志</button>
                    <button id="btn-refresh-logs" class="btn">刷新</button>
                </div>
            </div>
        </div>

        <header class="header">
            <div class="header-left">
                <div class="title-section">
                    <span class="realm-tag">地狱道</span>
                    <h1>无限制黑白棋</h1>
                </div>
            </div>
            <div class="header-center">
                <div id="turn-indicator" class="turn-indicator">
                    <span class="turn-icon">⚫</span>
                    <span class="turn-text">黑方回合</span>
                </div>
            </div>
            <div class="header-right">
                <button id="btn-settings" class="btn-icon" title="设置">⚙</button>
            </div>
        </header>

        <div id="samsara-bar" class="samsara-bar">
            <div class="samsara-row">
                <div id="level-info-bar" class="level-info-bar"></div>
            </div>
            <div class="samsara-row samsara-stats">
                <div class="samsara-stat karma-stat">
                    <span class="stat-icon">☯</span>
                    <div class="stat-content">
                        <span class="stat-label">业力</span>
                        <div class="stat-bar-wrapper">
                            <div class="stat-bar">
                                <div class="stat-fill karma-fill" id="karma-fill"></div>
                            </div>
                        </div>
                        <span class="stat-value" id="karma-value">0/120</span>
                    </div>
                </div>
                <div class="samsara-stat detection-stat">
                    <span class="stat-icon">👁️</span>
                    <div class="stat-content">
                        <span class="stat-label">识破</span>
                        <div class="stat-bar-wrapper">
                            <div class="stat-bar">
                                <div class="stat-fill detection-fill" id="detection-fill"></div>
                            </div>
                        </div>
                        <span class="stat-value" id="detection-value">0%</span>
                    </div>
                </div>
                <div class="samsara-stat turn-stat">
                    <span class="stat-icon">⏱️</span>
                    <div class="stat-content">
                        <span class="stat-label">回合</span>
                        <div class="stat-bar-wrapper">
                            <div class="stat-bar">
                                <div class="stat-fill turn-fill" id="turn-fill"></div>
                            </div>
                        </div>
                        <span class="stat-value" id="turn-value">0/20</span>
                    </div>
                </div>
                <div class="samsara-stat objective-stat">
                    <span class="stat-icon">🎯</span>
                    <div class="stat-content">
                        <span class="stat-label" id="objective-label">目标</span>
                        <span class="stat-value objective-text" id="objective-text">占领更多棋子</span>
                    </div>
                </div>
            </div>
        </div>

        <main class="main">
            <div class="board-section">
                <div class="board-frame">
                    <div class="board-frame-inner">
                        <div id="board-container"></div>
                    </div>
                </div>
                <div class="board-hints">
                    <span class="hint-item player-hint">
                        <span class="hint-dot player-dot"></span>
                        <span>玩家可落子</span>
                    </span>
                    <span class="hint-item ai-hint">
                        <span class="hint-dot ai-dot"></span>
                        <span>AI可落子</span>
                    </span>
                </div>
            </div>

            <aside class="side-panel">
                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">💬</span>
                        <h3>AI 助手</h3>
                    </div>
                    <div class="panel-card-body">
                        <div id="ai-messages" class="messages"></div>
                    </div>
                </div>

                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">🎯</span>
                        <h3>游戏目标</h3>
                    </div>
                    <div class="panel-card-body">
                        <div id="game-objectives" class="objectives-list">
                            <span class="empty">加载中...</span>
                        </div>
                    </div>
                </div>

                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">💎</span>
                        <h3>Token 消耗</h3>
                    </div>
                    <div class="panel-card-body">
                        <div id="token-stats" class="token-stats">
                            <div class="token-row">
                                <span class="token-label">总消耗</span>
                                <span class="token-value" id="token-total">0</span>
                            </div>
                            <div class="token-row">
                                <span class="token-label">今日消耗</span>
                                <span class="token-value" id="token-today">0</span>
                            </div>
                            <div class="token-row">
                                <span class="token-label">调用次数</span>
                                <span class="token-value" id="token-calls">0</span>
                            </div>
                            <div class="token-row token-cost">
                                <span class="token-label">估算费用</span>
                                <span class="token-value" id="token-cost">$0.00</span>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">🧠</span>
                        <h3>AI 性格</h3>
                    </div>
                    <div class="panel-card-body">
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
                                    <div class="bar-track">
                                        <div class="bar-fill aggressive" id="bar-agg-fill"></div>
                                    </div>
                                    <span class="bar-percent" id="bar-agg-pct">50%</span>
                                </div>
                                <div class="personality-bar">
                                    <span class="bar-label">防守</span>
                                    <div class="bar-track">
                                        <div class="bar-fill defensive" id="bar-def-fill"></div>
                                    </div>
                                    <span class="bar-percent" id="bar-def-pct">50%</span>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">⚡</span>
                        <h3>游戏机制</h3>
                    </div>
                    <div class="panel-card-body">
                        <div id="active-mechanisms" class="mechanisms-list">
                            <span class="empty">无激活机制</span>
                        </div>
                    </div>
                </div>

                <div class="panel-card">
                    <div class="panel-card-header">
                        <span class="card-icon">📜</span>
                        <h3>已激活规则</h3>
                    </div>
                    <div class="panel-card-body">
                        <div id="active-rules" class="rules-list">
                            <span class="empty">暂无自定义规则</span>
                        </div>
                    </div>
                </div>

                <div class="panel-card controls-card">
                    <div class="panel-card-header">
                        <span class="card-icon">🎮</span>
                        <h3>操 作</h3>
                    </div>
                    <div class="panel-card-body">
                        <div class="controls-grid">
                            <button id="btn-undo" class="control-btn">悔棋</button>
                            <button id="btn-undo-config" class="control-btn">撤回AI修改</button>
                            <button id="btn-restart" class="control-btn">重新开始</button>
                            <button id="btn-logs" class="control-btn">查看日志</button>
                            <button id="btn-reset-configs" class="control-btn danger">重置配置</button>
                        </div>
                    </div>
                </div>
            </aside>
        </main>

        <footer class="input-section">
            <div class="input-container">
                <div class="input-left">
                    <button id="btn-insert-coord" class="coord-btn" title="点击后在棋盘上选择格子">📍 选坐标</button>
                    <button id="btn-toggle-coord-mode" class="coord-btn" title="切换选坐标/选区域">⇄</button>
                </div>
                <div class="input-center">
                    <input
                        type="text"
                        id="command-input"
                        placeholder="输入指令，例如：把棋盘改成10×10、让AI接管白棋、悔一步棋..."
                        autocomplete="off"
                    >
                </div>
                <div class="input-right">
                    <button id="btn-send" class="send-btn">发送</button>
                </div>
            </div>
            <div class="hints">
                试试："把棋盘改成10×10"、"让AI接管白棋"、"创建一个能跳吃的棋子"、"悔一步棋"
            </div>
        </footer>
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
            const winner = state.winner === 'black' ? '黑方' : '白方';
            indicator.textContent = `${winner}获胜!`;
            indicator.style.background = 'var(--success)';
        } else {
            indicator.textContent = turn === 'black' ? '黑方回合' : '白方回合';
            indicator.style.background = turn === 'black' ? '#1a1a1a' : '#fafaf8';
            indicator.style.color = turn === 'black' ? '#fafaf8' : '#1a1a1a';
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

        const aggPct = Math.round(agg * 100);
        const defPct = Math.round(def * 100);

        const aggFill = this.shadowRoot.getElementById('bar-agg-fill');
        const defFill = this.shadowRoot.getElementById('bar-def-fill');
        if (aggFill) aggFill.style.width = `${aggPct}%`;
        if (defFill) defFill.style.width = `${defPct}%`;
        this.shadowRoot.getElementById('bar-agg-pct').textContent = `${aggPct}%`;
        this.shadowRoot.getElementById('bar-def-pct').textContent = `${defPct}%`;
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
                let sideLabel = item.side === 'black' ? '黑方' : '白方';
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

        this.shadowRoot.getElementById('btn-undo').addEventListener('click', async () => {
            const resp = await fetch(`${this.apiBase}/api/undo`, { method: 'POST' });
            const data = await resp.json();
            if (data.success) {
                this.boardState = data.board_state;
                this.lastMove = this.boardState.move_history?.slice(-1)[0] || null;
                this.clearSelection();
                this.renderPieces();
                this.updateTurnIndicator();
                await this.renderValidPlacements();
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
                await this.renderValidPlacements();
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
            // 黑白棋：点击合法空格直接落子（valid-move-indicator 已处理，此处为更大点击区域兜底）
            if (this.aiThinking) return;
            if (this.boardState?.game_status?.state === 'ended') return;
            if (!this._isCurrentTurnPlayerControlled()) return;
            const [gridX, gridY] = this._getGridCoordsFromEvent(e);
            if (gridX === null) return;
            let isValid = this.validMoves.some(m => m[0] === gridX && m[1] === gridY);
            // 防御：AI 可能在指令处理后改了规则，本地 validMoves 已过期。
            // 落子前向后端实时复核一次，避免用陈旧缓存放行已被规则禁止的落点。
            if (isValid) {
                try {
                    const side = this.boardState?.current_turn || 'black';
                    const resp = await fetch(`${this.apiBase}/api/valid_moves`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ side })
                    });
                    const data = await resp.json();
                    if (data.success && Array.isArray(data.moves)) {
                        this.validMoves = data.moves;
                        isValid = this.validMoves.some(m => m[0] === gridX && m[1] === gridY);
                    }
                } catch (err) {
                    console.warn('落子前复核合法落点失败，沿用本地缓存:', err);
                }
            }
            if (isValid) {
                this.placeStone(gridX, gridY);
            }
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
        const width = geometry.width || 8;
        const height = geometry.height || 8;

        // 单元格模型：点击位置落在哪个格子里
        const gridX = Math.floor((x / rect.width) * width);
        const gridY = Math.floor((y / rect.height) * height);

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
        const width = geometry.width || 8;
        const height = geometry.height || 8;

        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('id', 'coord-dots-group');
        g.style.pointerEvents = 'all';

        for (let x = 0; x < width; x++) {
            for (let y = 0; y < height; y++) {
                const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                dot.setAttribute('class', 'coord-dot');
                // 单元格中心
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
        if (!this.ttt.cells.includes(null)) {
            this.ttt.over = true;
            this._tttRender();
            this.shadowRoot.getElementById('ttt-status').textContent = '平局！点击重新开始再来一局';
            return true;
        }
        return false;
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

if (!customElements.get('heibaiqi-board')) customElements.define('heibaiqi-board', HeibaiqiBoard);

/* shared/game_shared_rpg.js
 * ------------------------------------------------------------
 * 六道（Samsara / RPG）前端通用逻辑：
 *   1) _escapeHtml / _fireLocalAndBroadcast / _applyKarmaDetectionState
 *   2) rpgResetBattleAndApply        关卡/重玩三件套
 *   3) advanceNextLevel              胜负页「下一关」推进
 *   4) initRpgEventListeners         跨页 BroadcastChannel + storage + 可见性
 *   5) showRpgGameOver               胜负页三按钮 + resolve 奖励
 *   6) rpgResetConfigsHandler        「重置所有配置」联动
 *
 * 使用方式（某棋类组件）：
 *   在 init() 中调用
 *     window.GameSharedRPG.install(this, {
 *       isSandbox: false,
 *       // UI 完全重绘：棋盘/棋子/回合/规则/目标/AI/机制
 *       rerender: async (instance) => {
 *         instance.clearSelection();
 *         instance.renderBoard();
 *         instance.renderPieces();     // 或 renderStones
 *         instance.updateTurnIndicator();
 *         instance.updateActiveRules();
 *         instance.updateGameObjectives();
 *         instance.updateAIPersonality();
 *         instance.updateMechanisms();
 *         // 黑白棋/部分棋：await instance.renderValidPlacements();
 *         // 围棋：instance.updateCaptureStats();
 *       }
 *     });
 *
 *   胜负判断触发时调用：this.showRpgGameOver();
 * ------------------------------------------------------------ */
(function () {
    'use strict';

    if (window.GameSharedRPG) return;

    const BroadcastChannel = (typeof window !== 'undefined') ? window.BroadcastChannel : null;
    let _sharedChannel = null;
    /* 大厅 API 源：供棋类进程心跳与主动回收使用（进程保活/回收见 main.py /api/lazy/*） */
    const _HUB_ORIGIN = 'http://localhost:8080';   // 六道大厅默认端口（与 main.py HUB_PORT 一致）

    /* 当前棋类服务端口：由本页 origin 推导（页面由某棋类自己的端口服务） */
    function _gamePort() {
        try {
            const p = parseInt(window.location.port, 10);
            return Number.isFinite(p) && p ? p : 0;
        } catch (e) { return 0; }
    }

    /* 对局进程心跳：只要页面打开就周期刷新最近使用时间，防止空闲回收器在对局中误杀 */
    function startHeartbeat(target) {
        if (target._hbStarted) return;
        target._hbStarted = true;
        const port = _gamePort();
        if (!port || port === 8080) return;   // 大厅自身无需心跳
        target._hbPort = port;
        target._hbTimer = setInterval(function () {
            try {
                fetch(`${_HUB_ORIGIN}/api/lazy/ping?port=${port}`, { cache: 'no-store' }).catch(() => {});
            } catch (e) {}
        }, 40000);
        // 立即补一次，确保进程已被拉起
        try { fetch(`${_HUB_ORIGIN}/api/lazy/ping?port=${port}`, { cache: 'no-store' }).catch(() => {}); } catch (e) {}
    }

    /* 玩家主动关闭界面（返回地图/回标题）时回收进程 */
    function closeGameProcess(target) {
        try {
            if (target._hbTimer) { clearInterval(target._hbTimer); target._hbTimer = null; }
            const port = target._hbPort || _gamePort();
            if (port && port !== 8080) {
                fetch(`${_HUB_ORIGIN}/api/lazy/stop?port=${port}`, { method: 'POST', cache: 'no-store' }).catch(() => {});
            }
        } catch (e) {}
    }

    /* 关闭网页/浏览器退出时回收：pagehide 仅在真实离开/关闭页面时触发，
       切标签页、休眠、焦点移开走 visibilitychange（不触发）→ 不会在后台误回收。
       用 sendBeacon 保证页面卸载瞬间请求仍被发出。 */
    let _pageHideBound = false;
    function bindPageHideStop() {
        if (_pageHideBound) return;
        _pageHideBound = true;
        try {
            window.addEventListener('pagehide', function () {
                const port = _gamePort();
                if (!port || port === 8080) return;
                try { navigator.sendBeacon(`${_HUB_ORIGIN}/api/lazy/stop?port=${port}`); } catch (e) {}
            });
        } catch (e) {}
    }

    function _getChannel() {
        if (_sharedChannel) return _sharedChannel;
        try {
            if (BroadcastChannel) _sharedChannel = new BroadcastChannel('game-events');
        } catch (e) { _sharedChannel = null; }
        return _sharedChannel;
    }

    function _escapeHtml(str) {
        if (str == null) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function _fireLocalAndBroadcast(type, payload) {
        const data = { type: type, ...(payload || {}) };
        // 1) 本页直接派发，用于 self 监听
        try {
            const ch = _getChannel();
            if (ch) ch.postMessage(data);
        } catch (e) {}
        // 2) 跨 tab 兜底：localStorage
        try {
            localStorage.setItem('ge_' + type, JSON.stringify({ value: payload || {}, ts: Date.now() }));
        } catch (e) {}
    }

    function _applyKarmaDetectionState(target, kd) {
        if (!kd) return;
        const karma = kd.karma || {};
        target.samsaraState = Object.assign({}, (target.samsaraState || {}), {
            karma: (typeof karma.current === 'number') ? karma.current : (target.samsaraState || {}).karma,
            karma_max: (typeof karma.max === 'number') ? karma.max : (target.samsaraState || {}).karma_max,
            karma_single_max: karma.single_max,
            karma_initial: karma.initial,
            detection: (typeof kd.detection === 'number') ? kd.detection : (target.samsaraState || {}).detection,
        });
        if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI();
    }

    function _removeOverlays(target) {
        const bc = target.shadowRoot && target.shadowRoot.getElementById('board-container');
        if (!bc) return;
        for (const sel of ['.game-over-overlay', '.victory-reward-overlay']) {
            const el = bc.querySelector(sel);
            if (el) el.remove();
        }
    }

    /* 三件套：apply_level_config → rpg_reset_battle → UI 全重绘 */
    async function rpgResetBattleAndApply(target, options) {
        options = options || {};
        const doSamsaraResetLevel = !!options.doSamsaraResetLevel;
        const doBroadcast = !!options.doBroadcast;
        const isSandbox = !!target.isSandbox;

        // Step 0（可选）：六道当前关变量软重清 —— 用于「重置所有配置」
        if (doSamsaraResetLevel && !isSandbox) {
            try {
                const r = await target._fetchRaw('/samsara/api/levels/reset_level', { method: 'POST' });
                if (r && r.ok) {
                    try {
                        const rj = await r.json();
                        if (rj && rj.state) { target.samsaraState = rj.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
                    } catch (e) {}
                }
            } catch (e) { console.warn('[RPG] samsara reset_level 失败', e); }
        }

        // Step 1：apply_level_config（Samsara → 本地难度/回合 并 reset_board）
        let applyResp = null;
        try {
            const r = await target._fetchRaw(`${target.apiBase}/api/level/apply`, { method: 'POST' });
            if (r && r.ok) applyResp = await r.json();
            if (applyResp && applyResp.karma_detection_state) _applyKarmaDetectionState(target, applyResp.karma_detection_state);
            if (applyResp && applyResp.state) { target.samsaraState = applyResp.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
        } catch (e) { console.warn('[RPG] apply_level_config 失败：', e); }

        // Step 2：rpg_reset_battle（主模式）→ 或 沙盒 fallback 到 /api/restart
        let resetResp = null;
        try {
            const r = await target._fetchRaw(`${target.apiBase}/api/rpg/reset_battle`, { method: 'POST' });
            if (r && r.ok) resetResp = await r.json();
            if (resetResp && resetResp.karma_detection_state) _applyKarmaDetectionState(target, resetResp.karma_detection_state);
            if (resetResp && resetResp.state) { target.samsaraState = resetResp.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
            if (resetResp && resetResp.board_state) target.boardState = resetResp.board_state;
        } catch (e) {
            // 沙盒 main.py 未实现 rpg_reset_battle → fallback /api/restart
            try {
                const r2 = await target._fetchRaw(`${target.apiBase}/api/restart`, { method: 'POST' });
                if (r2 && r2.ok) resetResp = await r2.json();
                if (resetResp && resetResp.karma_detection_state) _applyKarmaDetectionState(target, resetResp.karma_detection_state);
                if (resetResp && resetResp.board_state) target.boardState = resetResp.board_state;
            } catch (e2) {
                console.warn('[RPG] reset_battle + restart 全部失败：', e, e2);
            }
        }

        // Step 3：UI 全量重绘
        if (typeof target.loadConfigs === 'function') await target.loadConfigs();
        if (target.lastMove !== undefined) target.lastMove = null;
        if (typeof target._rpgRerender === 'function') await target._rpgRerender(target);
        if (typeof target.renderBoard === 'function' && !target._rpgRerender) {
            // 兜底最小重绘（若用户未传 rerender）
            if (typeof target.clearSelection === 'function') target.clearSelection();
            target.renderBoard();
            if (typeof target.renderPieces === 'function') target.renderPieces();
            else if (typeof target.renderStones === 'function') target.renderStones();
            if (typeof target.updateTurnIndicator === 'function') target.updateTurnIndicator();
            if (typeof target.updateActiveRules === 'function') target.updateActiveRules();
            if (typeof target.updateGameObjectives === 'function') target.updateGameObjectives();
            if (typeof target.updateAIPersonality === 'function') target.updateAIPersonality();
            if (typeof target.updateMechanisms === 'function') target.updateMechanisms();
        }

        // Step 4：刷新 Samsara state + level info（用于顶部业力/关卡条）
        try { if (typeof target.loadSamsaraState === 'function') await target.loadSamsaraState(); } catch (e) {}
        try { if (typeof target.loadLocalKarmaDetection === 'function') await target.loadLocalKarmaDetection(); } catch (e) {}

        if (doBroadcast) {
            _fireLocalAndBroadcast('level-started', { from_rpg_reset: true });
        }
    }

    /* 下一关按钮：推进关卡并进入下一局初始化 */
    async function advanceNextLevel(target, prevNextLevel) {
        if (target.isSandbox) {
            if (typeof target.addMessage === 'function') target.addMessage('⚠️ 沙盒模式不支持推进关卡', 'info');
            return;
        }
        try {
            const r = await target._fetchRaw('/samsara/api/levels/advance', { method: 'POST' });
            let data = null;
            if (r && r.ok) data = await r.json();
            let nl = prevNextLevel;
            if (data && data.next_level) nl = data.next_level;
            if (data && data.state) { target.samsaraState = data.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
            _fireLocalAndBroadcast('level-advanced', { next_level: nl });
            _removeOverlays(target);
            await rpgResetBattleAndApply(target, { doSamsaraResetLevel: false, doBroadcast: true });
            if (typeof target.addMessage === 'function') target.addMessage('➡️ 已进入下一关', 'success');
        } catch (e) {
            console.error('advanceNextLevel failed:', e);
            if (typeof target.addMessage === 'function') target.addMessage('❌ 进入下一关失败，请从总坛重试', 'error');
        }
    }

    /* 胜负页三按钮 + resolve level 奖励 */
    async function showRpgGameOver(target, extras) {
        extras = extras || {};
        const gs = target.boardState && target.boardState.game_status;
        if (!gs || gs.state !== 'ended') return;

        // 玩家方判断
        const playerSide = target.playerSide || 'red';
        const winner = gs.winner;
        const isPlayerWin = winner === playerSide;

        const sideMap = { red: '红方', black: '黑方', white: '白方' };
        const winLabel = (sideMap[winner] || winner) + (isPlayerWin ? '（我方）' : '（AI）');

        // 六道小世界地图模式（内嵌对局容器嵌入 iframe）：胜负页提示“返回地图”
        const isEmbed = !!target.isEmbed;

        // 清旧弹窗
        _removeOverlays(target);
        const container = target.shadowRoot && target.shadowRoot.getElementById('board-container');
        if (!container) return;

        // 胜负结算 resolve
        let rewards = null;
        let next_level = null;
        const isSandbox = !!target.isSandbox;
        const noCheatThisLevel = !!(target.samsaraState && target.samsaraState.no_cheat_this_level);
        if (isSandbox) {
            // 沙盒无关卡，跳过 progression resolve，避免无效 API 调用
            target._lastResolveRewards = { rewards: null, next_level: null, isPlayerWin };
        } else if (isPlayerWin) {
            try {
                const r = await target._fetchRaw(`${target.apiBase}/api/level/complete`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ won: true, no_cheat: noCheatThisLevel, boss_defeated: false }),
                });
                const data = (r && r.ok) ? await r.json() : null;
                rewards = data && data.rewards ? data.rewards : data;
                next_level = (rewards && rewards.next_level) || (data && data.next_level) || null;
                target._lastResolveRewards = { rewards, next_level, isPlayerWin: true };
                _fireLocalAndBroadcast('level-advanced', { isPlayerWin: true, next_level });
                try {
                    if (data && data.state) { target.samsaraState = data.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
                    else if (typeof target.loadSamsaraState === 'function') await target.loadSamsaraState();
                } catch (e) {}
            } catch (e) {
                console.error('Failed to resolve level rewards:', e);
            }
        } else {
            try {
                const r = await target._fetchRaw(`${target.apiBase}/api/level/complete`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ won: false, no_cheat: noCheatThisLevel, boss_defeated: false }),
                });
                const data = (r && r.ok) ? await r.json() : null;
                rewards = data && data.rewards ? data.rewards : data;
                target._lastResolveRewards = { rewards, next_level: null, isPlayerWin: false };
            } catch (e) { console.warn('Lose resolve failed', e); }
        }

        // 取最终业力 / 识破 供弹窗显示
        const karmaNow = (target.samsaraState && typeof target.samsaraState.karma === 'number') ? target.samsaraState.karma : null;
        const karmaMax = (target.samsaraState && typeof target.samsaraState.karma_max === 'number') ? target.samsaraState.karma_max : null;
        const detNow = (target.samsaraState && typeof target.samsaraState.detection === 'number')
            ? Math.round(target.samsaraState.detection * 1000) / 10 : null;
        const skillPointsEarned = (rewards && typeof rewards.skill_points === 'number') ? rewards.skill_points : 0;
        const bonusReasons = (rewards && Array.isArray(rewards.bonus_reasons)) ? rewards.bonus_reasons : [];
        const sandboxUnlocked = rewards && rewards.sandbox_unlocked;
        const realmAdvance = rewards && rewards.realm_advance;
        const nextLvInfo = next_level && next_level.level ? next_level.level : null;

        // 主/沙盒 按钮可见性
        let nextBtnLabel = '➡️ 下一关';
        let nextBtnDisabled = true;
        let showNextBtn = !isSandbox && !isEmbed;  // 地图模式无“下一关”，改走“返回地图”
        if (isSandbox) {
            nextBtnLabel = '➡️ 下一关（沙盒无关卡）';
            showNextBtn = false;
        } else if (isPlayerWin) {
            nextBtnDisabled = !next_level;
        } else {
            nextBtnLabel = '➡️ 下一关（需胜利才可推进）';
            nextBtnDisabled = true;
        }

        let returnLabel = '🏠 返回大陆';
        let returnHref = '/overworld';
        if (isSandbox) {
            returnLabel = '🏠 返回大地图';
            returnHref = '/overworld';
        }

        // 内嵌大陆大地图模式：胜负皆可“返回地图”（回到 2.5D 大地图）
        const mapReturnHref = '/overworld';
        const mapSummaryHtml = '';

        const titleHtml = isPlayerWin
            ? '<h2>🏆 通关胜利</h2>'
            : '<h2>💀 本局败北</h2>';
        const rpgStatsHtml = isSandbox ? '' : `
            <div style="display:grid;grid-template-columns:1fr 1fr;gap:6px 14px;font-size:13px;opacity:.95;">
                <div>业力：<b>${karmaNow !== null ? `${karmaNow}${karmaMax !== null ? ` / ${karmaMax}` : ''}` : '—'}</b></div>
                <div>识破：<b>${detNow !== null ? `${detNow}%` : '—'}</b></div>
                <div>无作弊通关：<b>${noCheatThisLevel ? '✅ 是' : '❌ 否'}</b></div>
                <div>技能点：<b style="color:#fde047;">${skillPointsEarned >= 0 ? '+' : ''}${skillPointsEarned}</b></div>
            </div>
        `;
        const summaryHtml = `
            <div style="margin:10px 0 14px;padding:10px 14px;border-radius:10px;background:rgba(255,255,255,.06);color:#e5e7eb;">
                <div style="margin-bottom:6px;"><b>${winLabel}</b> 获胜</div>
                ${rpgStatsHtml}
                ${nextLvInfo ? `<div style="margin-top:10px;font-size:13px;">下一关：<b>${_escapeHtml(nextLvInfo.name || ('第' + (next_level.level_index + 1) + '关'))}</b>${next_level && next_level.realm_name ? `（${_escapeHtml(next_level.realm_name)}）` : ''}</div>` : ''}
                ${realmAdvance && realmAdvance.realm_switched ? `<div style="margin-top:6px;font-size:13px;color:#a7f3d0;">🆙 道切换成功：进入「${_escapeHtml(realmAdvance.new_realm_name || realmAdvance.new_realm || '')}」</div>` : ''}
                ${sandboxUnlocked ? `<div style="margin-top:8px;font-size:13px;color:#a5f3fc;">🔓 沙盒模式已解锁</div>` : ''}
                ${bonusReasons && bonusReasons.length ? `<ul style="margin:10px 0 0;padding-left:18px;font-size:13px;line-height:1.6;">${bonusReasons.map(r => `<li>${_escapeHtml(String(r))}</li>`).join('')}</ul>` : ''}
                ${mapSummaryHtml}
            </div>
        `;
        // 大地图模式：以「返回地图」为主按钮（胜负皆可返回，复盘或改道）
        const mapBtnHtml = isEmbed && mapReturnHref
            ? `<button class="btn-primary rpg-mapbtn">🗺️ 返回地图</button>` : '';
        const buttonsHtml = `
            <div style="display:flex;flex-wrap:wrap;gap:8px;justify-content:center;">
                ${mapBtnHtml}
                ${showNextBtn ? `<button class="btn-primary rpg-nextbtn" ${nextBtnDisabled ? 'disabled style="opacity:.55;cursor:not-allowed;"' : ''}>${nextBtnLabel}</button>` : ''}
                <button class="btn-primary rpg-retrybtn">🔁 再来一次</button>
                ${isEmbed ? '' : `<button class="btn-primary rpg-returnbtn">${returnLabel}</button>`}
            </div>
        `;

        const overlay = document.createElement('div');
        overlay.className = 'game-over-overlay';
        overlay.innerHTML = `${titleHtml}${summaryHtml}${buttonsHtml}`;

        const nextBtn = overlay.querySelector('.rpg-nextbtn');
        const retryBtn = overlay.querySelector('.rpg-retrybtn');
        const returnBtn = overlay.querySelector('.rpg-returnbtn');
        const mapBtn = overlay.querySelector('.rpg-mapbtn');
        if (mapBtn) {
            mapBtn.addEventListener('click', () => { if (typeof target.closeGameProcess === 'function') target.closeGameProcess(); window.location.href = mapReturnHref; });
        }
        if (nextBtn && !nextBtnDisabled) {
            nextBtn.addEventListener('click', () => {
                nextBtn.disabled = true;
                nextBtn.textContent = '⏳ 进入下一关...';
                advanceNextLevel(target, next_level).finally(() => {});
            });
        } else if (nextBtn) {
            nextBtn.addEventListener('click', () => {
                if (typeof target.addMessage === 'function')
                    target.addMessage('⚠️ 下一关不可用（先赢得本局或已是最后一关）', 'info');
            });
        }
        if (retryBtn) {
            retryBtn.addEventListener('click', () => {
                if (typeof target.rpgRestart === 'function') target.rpgRestart();
            });
        }
        if (returnBtn) {
            returnBtn.addEventListener('click', () => { if (typeof target.closeGameProcess === 'function') target.closeGameProcess(); window.location.href = returnHref; });
        }

        container.appendChild(overlay);
    }

    /* 新 restart：RPG 三件套（向后兼容 fallback 到 /api/restart） */
    async function rpgRestart(target) {
        _removeOverlays(target);
        try {
            await rpgResetBattleAndApply(target, { doSamsaraResetLevel: false, doBroadcast: true });
            if (typeof target.addMessage === 'function') target.addMessage('🔄 游戏已重新开始', 'info');
        } catch (e) {
            try {
                const resp = await target._fetchRaw(`${target.apiBase}/api/restart`, { method: 'POST' });
                const data = (resp && resp.ok) ? await resp.json() : null;
                if (data && data.success) {
                    if (typeof target.loadConfigs === 'function') await target.loadConfigs();
                    if (target.lastMove !== undefined) target.lastMove = null;
                    if (typeof target._rpgRerender === 'function') await target._rpgRerender(target);
                    if (typeof target.addMessage === 'function') target.addMessage('🔄 游戏已重新开始', 'info');
                }
            } catch (e2) {
                if (typeof target.addMessage === 'function') target.addMessage('❌ 重新开始失败', 'error');
                console.error(e, e2);
            }
        }
    }

    /* 「重置所有配置」处理器：调用 /api/reset_configs + 可选 Samsara 存档重置 */
    async function rpgResetConfigsHandler(target, hardOrSoft) {
        const mode = hardOrSoft === 'hard' ? 'hard' : 'soft';
        try {
            const r = await target._fetchRaw(`${target.apiBase}/api/reset_configs`, { method: 'POST' });
            const data = (r && r.ok) ? await r.json() : null;
            if (data && !data.success) console.warn('[RPG] reset_configs failed');
        } catch (e) { console.warn('[RPG] reset_configs failed', e); }

        if (!target.isSandbox) {
            try {
                const r = await fetch('/samsara/api/reset', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ mode }),
                });
                if (r.ok) {
                    try {
                        const d = await r.json();
                        if (d && d.state) { target.samsaraState = d.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
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
            _fireLocalAndBroadcast('reset-issued', { mode });
        }
        // 关卡三件套 + 全重绘
        await rpgResetBattleAndApply(target, { doSamsaraResetLevel: true, doBroadcast: true });
        if (typeof target.addMessage === 'function') target.addMessage('✅ 所有配置已重置', 'success');
    }

    /* 跨页事件监听（BroadcastChannel + storage + visibility/focus） */
    function initRpgEventListeners(target) {
        const ch = _getChannel();
        if (ch) {
            ch.addEventListener('message', (e) => {
                const t = e.data && e.data.type;
                if (!t) return;
                switch (t) {
                    case 'achievement-unlocked':
                        if (typeof target._showAchievementToast === 'function') {
                            target._showAchievementToast(e.data.achievement || { id: e.data.id, name: e.data.name, desc: e.data.desc, icon: e.data.icon });
                        }
                        if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
                        break;
                    case 'karma-updated':
                    case 'reset-issued':
                    case 'level-advanced':
                    case 'level-started':
                        if (e.data && e.data.karma_detection_state) _applyKarmaDetectionState(target, e.data.karma_detection_state);
                        if (e.data && e.data.state) { target.samsaraState = e.data.state; if (typeof target.updateSamsaraUI === 'function') target.updateSamsaraUI(); }
                        if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
                        if (typeof target.loadLocalKarmaDetection === 'function') try { target.loadLocalKarmaDetection(); } catch (e) {}
                        break;
                }
            });
        }

        // storage 兜底
        window.addEventListener('storage', (ev) => {
            if (!ev || !ev.key || !ev.key.startsWith('ge_')) return;
            const t = ev.key.slice(3);
            let payload = {};
            try { payload = JSON.parse(ev.newValue || '{}').value || {}; } catch (e) {}
            if (t === 'achievement-unlocked') {
                if (typeof target._showAchievementToast === 'function') target._showAchievementToast(payload.achievement || payload);
                if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
            } else if (['karma-updated', 'reset-issued', 'level-advanced', 'level-started'].includes(t)) {
                if (payload && payload.karma_detection_state) _applyKarmaDetectionState(target, payload.karma_detection_state);
                if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
                if (typeof target.loadLocalKarmaDetection === 'function') try { target.loadLocalKarmaDetection(); } catch (e) {}
            }
        });

        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') {
                if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
                if (typeof target.loadLocalKarmaDetection === 'function') try { target.loadLocalKarmaDetection(); } catch (e) {}
            }
        });
        window.addEventListener('focus', () => {
            if (typeof target.loadSamsaraState === 'function') try { target.loadSamsaraState(); } catch (e) {}
            if (typeof target.loadLocalKarmaDetection === 'function') try { target.loadLocalKarmaDetection(); } catch (e) {}
        });
    }

    /* ── 六道统一 UX 打磨（对全部棋类生效，含沙盒） ──────────────────
     * 1) AI 思考浮层：AI 走子/推演期间给出明确"正在思考"反馈，消除"假死/卡顿"观感。
     * 2) 走子落点高亮：上一步落子做一次柔和高亮闪现，缓解棋子整体重建导致的"瞬移突兀"。
     * 3) 注入统一 CSS：随 install() 注入到各游戏 shadowRoot，保证样式一致且随组件共存亡。 */
    const _UX_POLISH_CSS = [
        '#rpg-thinking{position:fixed;inset:0;z-index:2147483000;display:none;align-items:center;justify-content:center;background:rgba(4,6,12,.24);backdrop-filter:blur(1px);-webkit-backdrop-filter:blur(1px);}',
        '.rpg-think-card{display:flex;align-items:center;gap:14px;padding:15px 22px;border-radius:14px;background:linear-gradient(135deg,#1f2937,#111827);border:1px solid rgba(212,175,55,.45);color:#f5ead0;box-shadow:0 14px 44px rgba(0,0,0,.5);font-family:\'DM Sans\',\'PingFang SC\',\'Microsoft YaHei\',sans-serif;animation:rpg-think-in .18s ease-out both;}',
        '.rpg-think-spinner{width:24px;height:24px;flex:none;border-radius:50%;border:3px solid rgba(212,175,55,.28);border-top-color:#e6c465;animation:rpg-spin .72s linear infinite;}',
        '.rpg-think-text{font-size:14px;letter-spacing:.05em;}',
        '@keyframes rpg-spin{to{transform:rotate(360deg)}}',
        '@keyframes rpg-think-in{from{opacity:0;transform:translateY(6px) scale(.97);}to{opacity:1;transform:none;}}',
        /* 上一步走子：落点闪亮后回落，柔和地"落"到位；仅命中 .piece.last-moved
           （各游戏该选择器均为静态高亮，无 animation），不会与 .ai-moved 的常驻脉动冲突；
           围棋 .stone 走子本已带 battlePulse 常驻动画，故不覆盖。 */
        '#board-container .piece.last-moved{animation:rpg-emerge .45s ease-out;}',
        '@keyframes rpg-emerge{0%{filter:brightness(2.5) drop-shadow(0 0 10px rgba(255,235,170,.95));}70%{filter:brightness(1.15) drop-shadow(0 0 4px rgba(255,235,170,.6));}100%{filter:brightness(1) drop-shadow(0 0 0 rgba(255,235,170,0));}}',
        /* ── 4) 界面紧凑化（桌面端）：侧边栏 / 上栏 / 下栏缩放至约 60%，
           让棋盘占据更大视觉主体。仅 min-width:901px 生效，不触碰 1200/900/600 的
           响应式换行规则（手机/小窗仍用各游戏自身的横向布局）。 */
        '@media (min-width:901px){',
        '.side-panel{width:277px;gap:16px;}',
        '.side-panel .panel-section,.side-panel .panel-card{padding:12px 16px;}',
        '.side-panel .panel-section h3,.side-panel .panel-card h3{font-size:1.05rem;margin-bottom:6px;}',
        '.side-panel .messages,.side-panel .rules-list,.side-panel .mechanisms-list,.side-panel .objectives-list{font-size:0.92rem;}',
        '.side-panel .personality-desc{font-size:0.9rem;}',
        '.side-panel .personality-type{font-size:0.98rem;}',
        '.side-panel .token-stat .stat-label{font-size:0.8rem;}',
        '.side-panel .token-stat .stat-value{font-size:0.95rem;}',
        '.main{padding:8px 16px;gap:16px;}',
        '.header{padding:6px 16px;}',
        '.header h1{font-size:1.35rem;}',
        '.header-actions{gap:10px;}',
        '#turn-indicator{padding:6px 14px;font-size:0.75rem;}',
        '.samsara-bar{gap:10px;padding:4px 16px;}',
        '.samsara-item{gap:5px;padding:3px 9px;}',
        '.samsara-icon{font-size:1rem;}',
        '.samsara-label{font-size:0.56rem;}',
        '.samsara-bar-container{width:56px;height:5px;}',
        '.samsara-value{font-size:0.66rem;}',
        '.input-section{padding:7px 16px;}',
        '#command-input{padding:8px 12px;font-size:0.8rem;}',
        '.hints{margin-top:8px;font-size:0.66rem;}',
        '.btn{padding:7px 12px;font-size:0.85rem;}',
        '.btn-primary{padding:8px 16px;font-size:0.9rem;}',
        '.level-info-bar{font-size:0.72rem;}',
        '}',
    ].join('\n');

    function _injectUxPolish(target) {
        if (!target || !target.shadowRoot) return;
        try {
            if (target.shadowRoot.getElementById('rpg-ux-polish')) return;
            const style = document.createElement('style');
            style.id = 'rpg-ux-polish';
            style.textContent = _UX_POLISH_CSS;
            target.shadowRoot.appendChild(style);
        } catch (e) {}
    }

    /* AI 思考浮层：非侵入、可复用，仅显示文字+转圈 */
    function _showAIThinking(target, text) {
        const root = target && target.shadowRoot;
        if (!root) return;
        let ov = root.getElementById('rpg-thinking');
        if (!ov) {
            try {
                ov = document.createElement('div');
                ov.id = 'rpg-thinking';
                ov.innerHTML = '<div class="rpg-think-card"><div class="rpg-think-spinner"></div><div class="rpg-think-text"></div></div>';
                root.appendChild(ov);
            } catch (e) { ov = null; }
        }
        if (!ov) return;
        const t = ov.querySelector('.rpg-think-text');
        if (t) t.textContent = text || 'AI 正在思考…';
        ov.style.display = 'flex';
    }

    function _hideAIThinking(target) {
        const root = target && target.shadowRoot;
        if (!root) return;
        const ov = root.getElementById('rpg-thinking');
        if (ov) ov.style.display = 'none';
    }

    /* 成就弹条（轻量版） */
    function _showAchievementToast(target, ach) {
        if (!ach) return;
        const root = target.shadowRoot;
        if (!root) return;
        let box = root.getElementById('achievement-toast-box');
        if (!box) {
            box = document.createElement('div');
            box.id = 'achievement-toast-box';
            box.style.cssText = 'position:fixed;top:16px;right:16px;z-index:99999;display:flex;flex-direction:column;gap:8px;pointer-events:none;';
            root.appendChild(box);
        }
        const card = document.createElement('div');
        card.style.cssText = 'width:280px;padding:12px 14px;border-radius:12px;background:linear-gradient(135deg,#1f2937,#111827);border:1px solid #f59e0b;color:#fff;box-shadow:0 10px 30px rgba(0,0,0,.4);animation:ach-toast-in .25s ease forwards;font-size:13px;';
        card.innerHTML = `
            <div style="display:flex;align-items:center;gap:10px;">
                <div style="font-size:22px;">${_escapeHtml(ach.icon || '🏆')}</div>
                <div style="flex:1;">
                    <div style="font-size:11px;opacity:.8;">🏆 成就解锁</div>
                    <div style="font-weight:700;font-size:14px;">${_escapeHtml(ach.name || ach.id || '')}</div>
                    <div style="font-size:12px;opacity:.9;">${_escapeHtml(ach.desc || '')}</div>
                </div>
            </div>
        `;
        box.appendChild(card);
        setTimeout(() => {
            card.style.animation = 'ach-toast-out .35s ease forwards';
            setTimeout(() => card.remove(), 420);
        }, 3800);
    }

    /* 安装到指定 ChessGame 实例 */
    function install(instance, opts) {
        opts = opts || {};
        instance.isSandbox = !!opts.isSandbox;
        // 六道小世界地图模式（内嵌对局容器嵌入 iframe）：胜负页提示“返回地图”
        instance.isEmbed = !!opts.isEmbed
            || (typeof document !== 'undefined' && document.documentElement.classList.contains('embed'));
        if (typeof opts.rerender === 'function') instance._rpgRerender = opts.rerender;

        // 通用工具：_fetchRaw（若未定义则提供 fetch 封装）
        if (typeof instance._fetchRaw !== 'function') {
            instance._fetchRaw = function (url, init) { return fetch(url, init || {}); };
        }
        if (typeof instance._escapeHtml !== 'function') {
            instance._escapeHtml = function (s) { return _escapeHtml(s); };
        }
        if (typeof instance._fireLocalAndBroadcast !== 'function') {
            instance._fireLocalAndBroadcast = function (type, payload) { return _fireLocalAndBroadcast(type, payload); };
        }
        if (typeof instance._applyKarmaDetectionState !== 'function') {
            instance._applyKarmaDetectionState = function (kd) { return _applyKarmaDetectionState(instance, kd); };
        }
        if (typeof instance._showAchievementToast !== 'function') {
            instance._showAchievementToast = function (a) { return _showAchievementToast(instance, a); };
        }

        _injectUxPolish(instance);
        instance.rpgShowThinking = function (text) { return _showAIThinking(instance, text); };
        instance.rpgHideThinking = function () { return _hideAIThinking(instance); };

        instance.rpgResetBattleAndApply = function (options) { return rpgResetBattleAndApply(instance, options); };
        instance.advanceNextLevel = function (prevNextLevel) { return advanceNextLevel(instance, prevNextLevel); };
        instance.rpgRestart = function () { return rpgRestart(instance); };
        instance.rpgResetConfigsHandler = function (mode) { return rpgResetConfigsHandler(instance, mode); };
        instance.showRpgGameOver = function (extras) { return showRpgGameOver(instance, extras); };
        instance.initRpgEventListeners = function () { return initRpgEventListeners(instance); };
        instance.startHeartbeat = function () { return startHeartbeat(instance); };
        instance.closeGameProcess = function () { return closeGameProcess(instance); };
        instance.startHeartbeat();   // 对局进程保活
        bindPageHideStop();          // 关闭网页时回收（pagehide 触发）
    }

    window.GameSharedRPG = {
        install,
        _escapeHtml,
        _fireLocalAndBroadcast,
        _applyKarmaDetectionState,
        _showAchievementToast,
        rpgResetBattleAndApply,
        advanceNextLevel,
        rpgRestart,
        rpgResetConfigsHandler,
        showRpgGameOver,
        initRpgEventListeners,
    };
})();

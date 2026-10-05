/**
 * 剧情对话系统（v1.7）
 * 功能：打字机效果、立绘切换、选择面板、祈求低语、识破警告
 *
 * v1.7 变更：对话界面对标柚子社 galgame 重制（上部圆角+顶部金线+挂名牌+右上
 * 工具栏），新增「履历（Backlog）」「自动播放」「隐藏窗口」。剧情与立绘不变。
 *
 * v1.6 变更：立绘动画改回 AI 关键帧插值连续动画帧（24FPS，48帧/2秒循环）。
 * 白色背景立绘经 Seedance 图生视频生成 2 秒微动视频，ffmpeg 抽帧为 48 张
 * 连续帧（boy/chenmo 全表情），rembg 抠图为透明 PNG。帧间过渡自然、
 * 角色一致性最佳（同源图），幅度由 AI 生成自然微动控制。
 *
 * Boss 关卡流程：Boss 关卡（type=boss，含 dialogues_before
 * + dialogues_after + choices）按以下顺序执行：
 *   dialogues_before → 进入棋局（新 tab）→ 棋局胜利后点击"继续"
 *   → dialogues_after → 显示道选择 → 完成
 */
(function() {
    'use strict';

    const API_BASE = '/samsara/story';
    let currentRealm = null;
    let currentLevel = null;
    let levelData = null;
    let realmData = null;        // 当前道的完整数据（含 game_type）
    let dialogueQueue = [];
    let dialogueIndex = 0;
    let isTyping = false;
    let typeTimer = null;
    let currentPhase = 'dialogues'; // dialogues / dialogues_before / dialogues_after / choice_response
    let choicesShown = false;
    let storyData = null;

    // 对话履历（Backlog）与自动播放 / 隐藏窗口（v1.7）
    const dialogueLog = [];   // { speaker, text, isNarrator }
    let autoMode = false;
    let autoTimer = null;
    let windowHidden = false;

    // 立绘动画：24FPS 帧序列（48帧/2秒循环），由 Seedance 图生视频抽帧生成。
    // updatePortraits 探测 _f1.png 存在则启动 24FPS 循环，否则用静态抠图 PNG。
    const ANIM_FPS = 24;
    const ANIM_FRAME_COUNT = 48;
    let portraitAnimTimer = null;
    const portraitFrameCache = {};

    // ── URL 参数解析 ──
    function getParams() {
        const params = new URLSearchParams(window.location.search);
        return {
            realm: params.get('realm') || 'hell',
            level: params.get('level') || '1',
            mode: params.get('mode') || 'level', // level / prologue
            port: params.get('port') || '',
        };
    }

    // ── 进入对局使用的端口：优先用 overworld 传入的 port，否则按棋种映射 ──
    function resolvePort() {
        const port = getParams().port;
        if (port) return port;
        const gameType = (levelData && levelData.game_type) || (realmData && realmData.game_type) || currentRealm;
        const gamePorts = {
            heibaiqi: 8005, tiaoqi: 8004, dongwuqi: 8003,
            xiangqi: 8000, weiqi: 8002, wuziqi: 8001,
        };
        return gamePorts[gameType] || '';
    }

    // ── 是否 Boss 关卡 ──
    function isBossLevel() {
        return !!(levelData && levelData.type === 'boss');
    }

    // ── 任务面板（本关背景 / 目标） ──
    function isMissionOpen() {
        const p = document.getElementById('mission-panel');
        return !!(p && p.classList.contains('active'));
    }
    function showMission() {
        document.getElementById('mission-kicker').textContent =
            `${realmData.name || currentRealm} · ${levelData.title || ''}`;
        document.getElementById('mission-desc').textContent = levelData.description || '';
        document.getElementById('mission-objective').textContent = levelData.objective || '';
        document.getElementById('mission-panel').classList.add('active');
        document.getElementById('dialogue-box').style.display = 'none';
        setAuto(false);   // 任务确认不属对话流，关掉自动播放
        // P3-⑦ 增补结构化目标：胜利条件 + 回合限制（与选关弹窗口径一致）
        fillStructuredGoal();
    }

    /* 由关卡配置的结构化 objective / turn_limit 生成一行简洁机制目标 */
    function fillStructuredGoal() {
        const el = document.getElementById('mission-struct');
        if (!el) return;
        el.textContent = '';
        // 本关索引（story 接口的 currentLevel 为 1 起，levels 数组以 0 起）
        const idx = (parseInt(currentLevel, 10) || 1) - 1;
        fetch(`${API_BASE}/samsara/api/levels/realm/${encodeURIComponent(currentRealm)}`)
            .then(r => r.json())
            .then(data => {
                const levels = (data && data.levels) || [];
                const lv = levels[idx] || levels.find(l => (l.id || '') === (levelData && levelData.id)) || {};
                const parts = [];
                const obj = lv.objective || {};
                const type = obj.type;
                if (type === 'checkmate') parts.push('胜利条件：将死对方');
                else if (type === 'capture_count') parts.push(`胜利条件：累计吃掉对方 ${obj.target || 15} 枚子`);
                else if (type === 'stalemate') parts.push('胜利条件：逼和/困毙对方');
                else if (type === 'five_in_a_row') parts.push('胜利条件：连成五子');
                else if (type === 'no_moves') parts.push('胜利条件：对方无子可走');
                else if (obj) parts.push('胜利条件：按对局规则取胜');
                if (lv.turn_limit) parts.push(`回合限制：${lv.turn_limit} 手内`);
                el.textContent = parts.join('  ·  ');
                if (parts.length) el.parentNode && el.parentNode.classList.remove('hidden');
            })
            .catch(() => {});
    }
    function closeMission() {
        document.getElementById('mission-panel').classList.remove('active');
        showNextDialogue();
    }

    // ── 初始化 ──
    async function init() {
        const params = getParams();
        if (params.mode === 'prologue') {
            await loadPrologue();
        } else {
            currentRealm = params.realm;
            currentLevel = params.level;
            await loadLevelDialogue(currentRealm, currentLevel);
        }
    }

    // ── 加载序章 ──
    async function loadPrologue() {
        try {
            const resp = await fetch(`${API_BASE}/api/story/prologue`);
            const data = await resp.json();
            document.getElementById('realm-name').textContent = '序章';
            document.getElementById('level-title').textContent = data.title || '';
            // 序章有多个 act，按顺序串联
            dialogueQueue = [];
            for (const act of data.acts || []) {
                for (const d of act.dialogues || []) {
                    dialogueQueue.push(d);
                }
            }
            setBackground(data.background);
            dialogueIndex = 0;
            showNextDialogue();
        } catch (e) {
            console.error('加载序章失败:', e);
        }
    }

    // ── 加载关卡对话 ──
    async function loadLevelDialogue(realm, level) {
        try {
            const resp = await fetch(`${API_BASE}/api/story/realm/${realm}/level/${level}`);
            const data = await resp.json();
            if (!data.success) {
                console.error('加载失败:', data.message);
                return;
            }
            levelData = data.data;
            storyData = data.state;

            // 更新顶部信息（同时获取当前道完整数据，含 game_type）
            const realmResp = await fetch(`${API_BASE}/api/story/realm/${realm}`);
            const realmJson = await realmResp.json();
            realmData = realmJson.data || {};
            document.getElementById('realm-name').textContent = realmData.name || realm;
            document.getElementById('level-title').textContent = `· ${levelData.title || ''}`;

            setBackground(realmData.background);

            // 根据关卡类型决定对话流
            dialogueQueue = [];
            if (levelData.dialogues) {
                currentPhase = 'dialogues';
                dialogueQueue = [...levelData.dialogues];
            } else if (levelData.dialogues_before) {
                currentPhase = 'dialogues_before';
                dialogueQueue = [...levelData.dialogues_before];
            }

            dialogueIndex = 0;
            // 每关先展示本关背景/目标任务面板，玩家确认后再进入剧情对白（PvZ 式）
            if (levelData.description || levelData.objective) {
                showMission();
            } else {
                showNextDialogue();
            }
        } catch (e) {
            console.error('加载对话失败:', e);
        }
    }

    // ── 设置背景 ──
    function setBackground(bg) {
        if (!bg) return;
        const scene = document.getElementById('dialogue-scene');
        scene.style.backgroundImage = `url(/shared/assets/backgrounds/${bg})`;
    }

    // ── 显示下一条对话 ──
    function showNextDialogue() {
        if (dialogueIndex >= dialogueQueue.length) {
            // 当前队列结束
            onDialogueQueueEnd();
            return;
        }

        const dialogue = dialogueQueue[dialogueIndex];
        const isNarrator = dialogue.speaker === 'narrator' || !dialogue.portrait;

        // 更新立绘
        updatePortraits(dialogue);

        // 显示对话框
        const box = document.getElementById('dialogue-box');
        box.classList.remove('hidden');
        windowHidden = false;
        box.style.display = 'block';

        // 说话者名称（名牌：旁白用 narrator 变体）
        const nameplate = document.getElementById('nameplate');
        const nameEl = document.getElementById('speaker-name');
        if (isNarrator) {
            nameplate.classList.add('narrator');
            nameEl.textContent = '旁白';
        } else {
            nameplate.classList.remove('narrator');
            nameEl.textContent = dialogue.speaker;
        }

        // 记录到对话履历（Backlog）
        dialogueLog.push({
            speaker: isNarrator ? '旁白' : (dialogue.speaker || ''),
            text: dialogue.text || '',
            isNarrator,
        });
        if (dialogueLog.length > 300) dialogueLog.splice(0, dialogueLog.length - 300);

        // 打字机效果
        typewriter(dialogue.text || '');

        dialogueIndex++;
    }

    // ── 打字机效果 ──
    function typewriter(text) {
        isTyping = true;
        const textEl = document.getElementById('dialogue-text');
        const hintEl = document.getElementById('dialogue-hint');
        const nextBtn = document.getElementById('dialogue-next-btn');
        hintEl.textContent = '点击加速...';
        nextBtn.disabled = true;

        let charIndex = 0;
        if (typeTimer) clearInterval(typeTimer);

        textEl.innerHTML = '<span class="cursor"></span>';

        typeTimer = setInterval(() => {
            if (charIndex < text.length) {
                textEl.innerHTML = text.substring(0, charIndex + 1) + '<span class="cursor"></span>';
                charIndex++;
                playBlip();
            } else {
                clearInterval(typeTimer);
                typeTimer = null;
                isTyping = false;
                textEl.innerHTML = text;
                hintEl.textContent = '点击或空格继续';
                nextBtn.disabled = false;
                if (autoMode) scheduleAuto();
            }
        }, 40);
    }

    // ── 跳过打字机 ──
    function skipTypewriter() {
        if (!isTyping) return;
        if (typeTimer) {
            clearInterval(typeTimer);
            typeTimer = null;
        }
        const dialogue = dialogueQueue[dialogueIndex - 1];
        if (dialogue) {
            document.getElementById('dialogue-text').textContent = dialogue.text || '';
        }
        isTyping = false;
        document.getElementById('dialogue-hint').textContent = '点击或空格继续';
        document.getElementById('dialogue-next-btn').disabled = false;
        if (autoMode) scheduleAuto();
    }

    // ── 播放对话音效 ──
    function playBlip() {
        // 轻量文字音效（可静默）
    }

    // ── 工具栏交互（v1.7）：自动播放 / 隐藏窗口 / 对话履历 ──

    // 自动播放：打完一行后延时自动进入下一行
    function scheduleAuto() {
        if (autoTimer) clearTimeout(autoTimer);
        autoTimer = setTimeout(() => {
            autoTimer = null;
            if (!autoMode) return;
            if (isMissionOpen() || choicesShown || !isVisibleBox()) return;
            showNextDialogue();
        }, 1100);
    }
    function clearAuto() {
        if (autoTimer) { clearTimeout(autoTimer); autoTimer = null; }
    }
    function setAuto(on) {
        autoMode = !!on;
        const btn = document.getElementById('auto-btn');
        if (btn) btn.classList.toggle('on', autoMode);
        const chip = document.getElementById('auto-chip');
        if (chip) chip.classList.toggle('show', autoMode);
        if (!autoMode) clearAuto();
    }
    function toggleAuto() {
        if (autoMode) { setAuto(false); return; }
        setIsTypingDone();   // 自动打开时若正在打字，立即补全当前行
        setAuto(true);
        if (!isTyping) scheduleAuto();
    }

    // 隐藏窗口：临时隐藏消息窗（再点或按空格恢复）
    function isVisibleBox() {
        const box = document.getElementById('dialogue-box');
        return !!(box && box.style.display !== 'none' && !box.classList.contains('hidden'));
    }
    function toggleBoxHidden() {
        const box = document.getElementById('dialogue-box');
        if (!box || box.style.display === 'none') return;
        windowHidden = !windowHidden;
        box.classList.toggle('hidden', windowHidden);
        // 恢复时给玩家提示
        const hint = document.getElementById('dialogue-hint');
        if (hint && windowHidden) hint.textContent = '按空格或点击恢复消息窗';
    }

    // 对话履历（Backlog）overlay
    function isLogOpen() {
        const ov = document.getElementById('log-overlay');
        return !!(ov && ov.classList.contains('active'));
    }
    function toggleLog() {
        if (isLogOpen()) { closeLog(); return; }
        renderLog();
        document.getElementById('log-overlay').classList.add('active');
    }
    function renderLog() {
        const list = document.getElementById('log-list');
        list.innerHTML = '';
        for (const line of dialogueLog) {
            const entry = document.createElement('div');
            entry.className = 'log-line';
            const sp = document.createElement('div');
            sp.className = 'log-speaker' + (line.isNarrator ? ' narrator' : '');
            sp.textContent = line.speaker;
            const tx = document.createElement('div');
            tx.className = 'log-text';
            tx.textContent = line.text;
            entry.appendChild(sp);
            entry.appendChild(tx);
            list.appendChild(entry);
        }
        if (list.scrollHeight > list.clientHeight) {
            list.scrollTop = list.scrollHeight;
        }
    }
    function closeLog() {
        document.getElementById('log-overlay').classList.remove('active');
    }

    // 若当前行正在打字，立即完整打出（供开启自动前的状态对齐）
    function setIsTypingDone() {
        if (!isTyping) return;
        if (typeTimer) { clearInterval(typeTimer); typeTimer = null; }
        const dialogue = dialogueQueue[dialogueIndex - 1];
        if (dialogue) {
            document.getElementById('dialogue-text').textContent = dialogue.text || '';
        }
        isTyping = false;
        document.getElementById('dialogue-hint').textContent = '点击或空格继续';
        document.getElementById('dialogue-next-btn').disabled = false;
    }

    // ── 更新立绘 ──
    function updatePortraits(dialogue) {
        // 停止上一轮立绘动画
        stopPortraitAnim();

        const container = document.getElementById('dialogue-characters');
        container.innerHTML = '';

        if (!dialogue.portrait || dialogue.speaker === 'narrator') {
            return;
        }

        const folder = getCharacterPath(dialogue).replace(/\/[^/]+$/, '');
        const jpgName = dialogue.portrait;
        // 优先使用抠图后的透明 PNG，回退到 JPG
        const pngName = jpgName.replace(/\.jpg$/i, '.png');
        const stem = jpgName.replace(/\.jpg$/i, '');  // 如 boy_happy

        const img = document.createElement('img');
        img.className = 'character-portrait speaking';
        img.alt = dialogue.speaker;
        const baseUrl = `/shared/assets/characters/${folder}`;
        img.dataset.pngUrl = `${baseUrl}/${pngName}`;
        img.dataset.jpgUrl = `${baseUrl}/${jpgName}`;
        img.src = img.dataset.pngUrl;

        // PNG 加载失败 → 回退到 JPG
        img.addEventListener('error', function onError() {
            if (this.src === this.dataset.pngUrl) {
                this.src = this.dataset.jpgUrl;
            } else if (this.src === this.dataset.jpgUrl) {
                // JPG 也失败，隐藏
                this.style.visibility = 'hidden';
                this.removeEventListener('error', onError);
            }
        });

        container.appendChild(img);
        // 启动 24FPS 帧动画（探测 _f1.png 存在则循环播放 48 帧，否则静态）
        startPortraitAnimation(img, baseUrl, stem);
    }

    // ── 停止立绘动画 ──
    function stopPortraitAnim() {
        if (portraitAnimTimer) {
            clearInterval(portraitAnimTimer);
            portraitAnimTimer = null;
        }
    }

    // ── 立绘 24FPS 帧动画 ──
    // 探测首帧 _f1.png：存在则预加载全部帧，等所有帧 settle（load/error）后，
    // 仅循环已成功加载的帧。这样 48 帧动画全速 24FPS；仅 6 帧的旧动画也能
    // 平滑循环（而非在第 6 帧后卡住）。无动画帧则保持静态抠图 PNG。
    function startPortraitAnimation(img, baseUrl, stem) {
        const firstFrameUrl = `${baseUrl}/${stem}_f1.png`;
        const probe = new Image();
        probe.onload = async () => {
            let frames = portraitFrameCache[stem];
            if (!frames) {
                frames = [];
                for (let i = 1; i <= ANIM_FRAME_COUNT; i++) {
                    const f = new Image();
                    f.src = `${baseUrl}/${stem}_f${i}.png`;
                    frames.push(f);
                }
                portraitFrameCache[stem] = frames;
            }
            img.src = probe.src;  // 切到动画首帧
            // 等所有帧 settle（成功加载或失败）
            await Promise.all(frames.map(f =>
                f.complete ? Promise.resolve() : new Promise(res => {
                    f.addEventListener('load', res, { once: true });
                    f.addEventListener('error', res, { once: true });
                })
            ));
            // 仅循环已成功加载的帧（≥2 帧才启动动画，否则保持静态首帧）
            const loaded = frames.filter(f => f.naturalWidth > 0);
            if (loaded.length < 2) return;
            let frameIdx = 0;
            portraitAnimTimer = setInterval(() => {
                frameIdx = (frameIdx + 1) % loaded.length;
                img.src = loaded[frameIdx].src;
            }, 1000 / ANIM_FPS);
        };
        // probe.onerror：无动画帧，保持静态抠图 PNG（img.src 已设为 pngUrl）
        probe.src = firstFrameUrl;
    }

    // ── 获取角色立绘路径 ──
    function getCharacterPath(dialogue) {
        const speaker = dialogue.speaker;
        const portrait = dialogue.portrait;

        // 根据说话者映射到对应文件夹
        const charMap = {
            '林夜': 'boy',
            '小林夜': 'boy',
            'guide': 'guide',
            '引路人': 'guide',
            '翻覆者': 'flipper',
            '饕餮者': 'glutton',
            '秩序者': 'orderer',
            '算计者': 'calculator',
            '狂乱者': 'chaos',
            '禅定者': 'zen',
            '天道': 'tiandao',
            '陈默': 'chenmo',
            '林父': 'father',
            '班主任': 'teacher',
            '初中班主任': 'teacher',
            '小宇': 'xiaoyu',
        };

        const folder = charMap[speaker] || 'boy';
        return `${folder}/${portrait}`;
    }

    // ── 对话队列结束处理 ──
    function onDialogueQueueEnd() {
        setAuto(false);   // 对话流结束，停止自动播放
        if (currentPhase === 'dialogues_before' && levelData) {
            // Boss 战前对话结束 → 显示"进入棋局"控制面板
            // 不直接显示 choices（choices 应在 dialogues_after 之后显示）
            showBossBattleEntry();
            return;
        }

        if (currentPhase === 'dialogues' && levelData) {
            // 普通对话结束
            if (levelData.choices && levelData.choices.length > 0) {
                showChoices();
            } else if (levelData.guide_whisper) {
                // 检查是否有 guide_whisper
                showWhisper(levelData.guide_whisper, () => onLevelComplete());
            } else {
                onLevelComplete();
            }
            return;
        }

        if (currentPhase === 'dialogues_after') {
            // Boss 战后对话结束 → 显示道选择（若有）或完成
            if (levelData.choices && levelData.choices.length > 0) {
                showChoices();
            } else {
                onLevelComplete();
            }
            return;
        }

        if (currentPhase === 'choice_response') {
            // 选择后的响应对话结束 → 完成（不再回到 choices，避免循环）
            onLevelComplete();
            return;
        }

        // 序章结束
        if (!currentRealm) {
            markPrologueSeen();
        }
    }

    // ── Boss 战入口面板（dialogues_before 播完后显示） ──
    function showBossBattleEntry() {
        document.getElementById('dialogue-box').style.display = 'none';

        // 移除可能已存在的旧面板
        const oldPanel = document.getElementById('boss-entry-panel');
        if (oldPanel) oldPanel.remove();

        const panel = document.createElement('div');
        panel.id = 'boss-entry-panel';
        panel.className = 'choice-panel active';
        panel.style.margin = '0 40px 20px';
        panel.innerHTML = `
            <div class="choice-prompt">Boss 战 · 准备就绪</div>
            <div class="choice-options">
                <div class="choice-option" id="boss-start-btn">
                    <span class="choice-option-text">⚔ 进入棋局（新标签页）</span>
                    <span class="choice-option-hint">点击开始 Boss 战</span>
                </div>
                <div class="choice-option" id="boss-continue-btn" style="border-left:3px solid var(--accent-gold);">
                    <span class="choice-option-text">✓ 棋局已胜利，继续剧情</span>
                    <span class="choice-option-hint">Boss 战胜利后点击</span>
                </div>
            </div>
        `;
        document.querySelector('.dialogue-scene').appendChild(panel);

        document.getElementById('boss-start-btn').addEventListener('click', () => {
            // 在新 tab 打开棋类游戏，保留当前 dialogue 页面以继续 dialogues_after
            enterGame(true);
        });
        document.getElementById('boss-continue-btn').addEventListener('click', () => {
            panel.remove();
            // 切换到 dialogues_after 阶段
            if (levelData.dialogues_after && levelData.dialogues_after.length > 0) {
                dialogueQueue = [...levelData.dialogues_after];
                currentPhase = 'dialogues_after';
                dialogueIndex = 0;
                showNextDialogue();
            } else if (levelData.choices && levelData.choices.length > 0) {
                // 无 dialogues_after，直接显示 choices
                showChoices();
            } else {
                onLevelComplete();
            }
        });
    }

    // ── 显示选择面板 ──
    function showChoices() {
        if (!levelData || !levelData.choices) return;
        setAuto(false);   // 选择面板需玩家手动操作，停止自动播放

        const choices = levelData.choices;
        let choiceIndex = 0;
        const choicePanel = choices[choiceIndex];

        // 检查是否应跳过（识破路径下跳过最终选择）
        if (choicePanel.should_skip || (choicePanel.skip_if_exposure_path && storyData?.detection_state?.exposure_path_triggered)) {
            // 跳过选择，直接进入下一阶段
            onChoicesComplete();
            return;
        }

        const panel = document.getElementById('choice-panel');
        const promptEl = document.getElementById('choice-prompt');
        const optionsEl = document.getElementById('choice-options');

        promptEl.textContent = choicePanel.prompt || '请选择';
        optionsEl.innerHTML = '';

        const options = choicePanel.options || [];
        options.forEach((opt, idx) => {
            const optEl = document.createElement('div');
            optEl.className = 'choice-option';

            // 根据 effect 添加颜色标识
            const effect = opt.effect || {};
            if (effect.enlightenment) optEl.classList.add('enlightenment');
            if (effect.corruption) optEl.classList.add('corruption');
            if (effect.rationality) optEl.classList.add('rationality');
            if (effect.emotion) optEl.classList.add('emotion');

            optEl.innerHTML = `
                <span class="choice-option-text">${opt.text}</span>
                ${opt.hint ? `<span class="choice-option-hint">${opt.hint}</span>` : ''}
            `;

            optEl.addEventListener('click', () => onChoiceSelected(choiceIndex, idx));
            optionsEl.appendChild(optEl);
        });

        // 隐藏对话框，显示选择面板
        document.getElementById('dialogue-box').style.display = 'none';
        panel.classList.add('active');
        choicesShown = true;
    }

    // ── 选择被选中 ──
    async function onChoiceSelected(choiceIndex, optionIndex) {
        try {
            const resp = await fetch(`${API_BASE}/api/choices/apply`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    realm: currentRealm,
                    level: currentLevel,
                    choice_index: choiceIndex,
                    option_index: optionIndex,
                }),
            });
            const result = await resp.json();

            // 隐藏选择面板
            document.getElementById('choice-panel').classList.remove('active');
            choicesShown = false;

            // 如果直接触发结局
            if (result.ending) {
                window.location.href = `/ending?ending=${result.ending}`;
                return;
            }

            // 显示选择后的响应对话（若有）。
            // 使用 choice_response 阶段：响应对话结束后直接完成，
            // 不再回到 choices（否则含 choices 的关卡会循环重显选择）。
            // Boss 关卡的 dialogues_after 已在 choices 之前播放，此处不再重播。
            if (result.response) {
                dialogueQueue = [result.response];
                currentPhase = 'choice_response';
                dialogueIndex = 0;
                showNextDialogue();
                return;
            }

            onChoicesComplete();
        } catch (e) {
            console.error('选择失败:', e);
        }
    }

    // ── 选择完成 ──
    function onChoicesComplete() {
        // Boss 关卡的 dialogues_after 已在 choices 之前播放，普通关卡无 dialogues_after，
        // 因此选择完成后直接进入关卡完成流程。
        onLevelComplete();
    }

    // ── 进入游戏（棋局） ──
    // useNewTab=true → 在新 tab 打开（Boss 关卡用，保留当前 dialogue 页面）
    // useNewTab=false / 默认 → 当前页面跳转（普通关卡用）
    function enterGame(useNewTab = false) {
        if (!levelData) return;
        const port = resolvePort();
        if (!port) {
            console.error('未知的 game_type / 端口:', levelData.game_type);
            return;
        }
        const gameUrl = `/play?realm=${encodeURIComponent(currentRealm)}&port=${port}`;
        if (useNewTab) {
            window.open(gameUrl, '_blank', 'noopener');
        } else {
            window.location.href = gameUrl;
        }
    }

    // ── 关卡完成 ──
    function onLevelComplete() {
        // 检查 guide_whisper
        if (levelData && levelData.guide_whisper) {
            showWhisper(levelData.guide_whisper, () => {
                checkNextStep();
            });
        } else {
            checkNextStep();
        }
    }

    // ── 检查下一步 ──
    function checkNextStep() {
        // 如果是最终关，检查是否需要进入天道 Boss 战
        if (storyData?.should_trigger_boss || storyData?.tiandao_boss_state?.can_enter) {
            window.location.href = '/heaven-boss';
            return;
        }

        // 非剧情语言专属：以下仅适用于进入剧情模式的关卡
        if (!currentRealm) {
            return;
        }

        // Boss 关卡：整场剧情（战前/战后/选择）已走完，完成框即可（战斗发生在独立窗口）
        if (isBossLevel()) {
            showLevelComplete();
            return;
        }

        // 普通关卡（PvZ 式）：剧情讲完后进入棋局对局，再返回大地图
        const port = resolvePort();
        if (!port) {
            showLevelComplete();
            return;
        }
        window.location.href = `/play?realm=${encodeURIComponent(currentRealm)}&port=${port}`;
    }

    // ── 完成框（Boss / 无法进入对局时的兜底） ──
    function showLevelComplete() {
        const box = document.getElementById('dialogue-box');
        box.innerHTML = `
            <div style="text-align:center; padding:20px;">
                <div style="font-size:20px; color:var(--accent-gold); margin-bottom:12px;">剧情完成</div>
                <div style="font-size:14px; color:var(--text-dim); margin-bottom:20px;">点击返回大地图继续下一境</div>
                <a href="/overworld?r=${Date.now()}&backrealm=${encodeURIComponent(currentRealm || 'hell')}" style="color:var(--accent-gold); text-decoration:none; border:1px solid var(--accent-gold); padding:8px 24px; border-radius:4px; display:inline-block;">返回大地图 ▶</a>
            </div>
        `;
        box.style.display = 'block';
    }

    // ── 显示低语 ──
    function showWhisper(text, callback) {
        const overlay = document.getElementById('prayer-overlay');
        const whisperEl = document.getElementById('whisper-text');
        whisperEl.textContent = text;
        overlay.classList.add('active');
        whisperEl.classList.add('show');

        setTimeout(() => {
            overlay.classList.remove('active');
            whisperEl.classList.remove('show');
            if (callback) callback();
        }, 3000);
    }

    // ── 标记序章已看 ──
    async function markPrologueSeen() {
        try {
            await fetch(`${API_BASE}/api/story/mark-prologue-seen`, { method: 'POST' });
        } catch (e) {}
        const box = document.getElementById('dialogue-box');
        box.innerHTML = `
            <div style="text-align:center; padding:20px;">
                <div style="font-size:20px; color:var(--accent-gold); margin-bottom:12px;">序章完</div>
                <div style="font-size:14px; color:var(--text-dim); margin-bottom:16px;">轮回之门已开启，踏入六道大陆开启修行</div>
                <a href="/overworld" style="color:var(--accent-gold); text-decoration:none; border:1px solid var(--accent-gold); padding:8px 24px; border-radius:4px; display:inline-block; margin-top:12px;">进入大陆 ▶</a>
            </div>
        `;
        box.style.display = 'block';
        // 序章结束自动跳转大地图（+ 短暂停留让玩家看到"序章完"）
        setTimeout(() => {
            try { window.location.href = '/overworld?r=' + Date.now(); } catch (e) {}
        }, 1600);
    }

    // ── 事件绑定 ──
    function bindEvents() {
        const nextBtn = document.getElementById('dialogue-next-btn');
        const box = document.getElementById('dialogue-box');

        function isBoxHidden() {
            return windowHidden || box.classList.contains('hidden');
        }
        function unHideBox() {
            windowHidden = false;
            box.classList.remove('hidden');
            document.getElementById('dialogue-hint').textContent = '点击或空格继续';
        }

        function handleNext() {
            // 履历打开时，空格/点击优先关闭履历
            if (isLogOpen()) { closeLog(); return; }
            // 隐藏窗口后，空格/点击优先恢复消息窗
            if (isBoxHidden()) { unHideBox(); return; }
            if (isMissionOpen()) { closeMission(); return; }
            if (isTyping) {
                skipTypewriter();
            } else if (!choicesShown) {
                showNextDialogue();
            }
        }

        document.getElementById('mission-start').addEventListener('click', handleNext);
        nextBtn.addEventListener('click', handleNext);
        box.addEventListener('click', (e) => {
            // 点工具栏按钮不触发翻页
            if (e.target.closest('.window-toolbar')) return;
            handleNext();
        });

        // v1.7 工具栏
        document.getElementById('log-btn').addEventListener('click', toggleLog);
        document.getElementById('auto-btn').addEventListener('click', toggleAuto);
        document.getElementById('hide-btn').addEventListener('click', toggleBoxHidden);
        document.getElementById('log-close').addEventListener('click', closeLog);

        document.addEventListener('keydown', (e) => {
            if (e.code === 'Space' || e.code === 'Enter') {
                e.preventDefault();
                handleNext();
            }
            if (e.code === 'KeyL') { toggleLog(); }
            if (e.code === 'KeyH') { toggleBoxHidden(); }
        });
    }

    // ── 启动 ──
    document.addEventListener('DOMContentLoaded', () => {
        bindEvents();
        init();
    });

})();

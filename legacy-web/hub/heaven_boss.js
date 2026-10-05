/**
 * 天道 Boss 战前端逻辑（v1.4）
 * 流程：检查进入条件 → 播放入场对话 → 展示规则 → 进入象棋对局
 *
 * 对白源从 configs/tiandao_boss.json.dialogues 迁移到
 * configs/story.json.tiandao.boss_dialogues。后端 HeavenBossSystem
 * 通过 dialogues_on_enter / dialogues_mid / dialogues_on_win / dialogues_on_lose
 * 字段返回，前端直接消费这些字段（不再读 config.dialogues）。
 */
(function() {
    'use strict';
    const API_BASE = '/samsara/story';

    let dialogueQueue = [];
    let dialogueIndex = 0;
    let isTyping = false;
    let typeTimer = null;
    let bossInfo = null;

    // ── 初始化 ──
    async function init() {
        try {
            const resp = await fetch(`${API_BASE}/api/heaven-boss`);
            const data = await resp.json();
            bossInfo = data;

            // 检查是否可以进入
            const status = data.status;
            const canEnter = status?.can_enter;

            if (!canEnter?.can_enter) {
                showBlocked(canEnter?.reason || '无法进入天道Boss战');
                return;
            }

            // 显示尝试次数
            const attempts = status?.attempt_count || 0;
            if (attempts > 0) {
                document.getElementById('attempts').textContent = `已尝试 ${attempts} 次`;
            }

            // 播放入场对话（优先使用 story.json.tiandao.boss_dialogues.on_enter）
            const dialogues = Array.isArray(data.dialogues_on_enter)
                ? data.dialogues_on_enter
                : [];
            if (dialogues.length > 0) {
                dialogueQueue = dialogues;
                dialogueIndex = 0;
                showNextDialogue();
            } else {
                showRulesAndEnter();
            }
        } catch (e) {
            console.error('加载天道Boss战信息失败:', e);
            showBlocked('无法连接到天道审判殿');
        }
    }

    // ── 显示下一条对话 ──
    function showNextDialogue() {
        if (dialogueIndex >= dialogueQueue.length) {
            showRulesAndEnter();
            return;
        }

        const d = dialogueQueue[dialogueIndex];
        const box = document.getElementById('dialogue-box');
        const speakerEl = document.getElementById('speaker-name');
        const textEl = document.getElementById('dialogue-text');
        const nextBtn = document.getElementById('next-btn');

        box.style.display = 'block';

        const isNarrator = d.speaker === 'narrator' || !d.portrait;
        if (isNarrator) {
            speakerEl.className = 'boss-speaker narrator';
            speakerEl.textContent = '旁白';
        } else {
            speakerEl.className = 'boss-speaker';
            speakerEl.textContent = d.speaker;
        }

        nextBtn.disabled = true;
        nextBtn.textContent = '继续 ▶';

        // 打字机
        typewriter(d.text || '', () => {
            nextBtn.disabled = false;
        });

        dialogueIndex++;
    }

    // ── 打字机效果 ──
    function typewriter(text, onComplete) {
        isTyping = true;
        const textEl = document.getElementById('dialogue-text');
        let i = 0;
        if (typeTimer) clearInterval(typeTimer);
        textEl.innerHTML = '<span style="border-right:2px solid #d4af37;animation:blink 0.8s infinite;">&nbsp;</span>';
        typeTimer = setInterval(() => {
            if (i < text.length) {
                textEl.innerHTML = text.substring(0, i + 1) + '<span style="border-right:2px solid #d4af37;animation:blink 0.8s infinite;">&nbsp;</span>';
                i++;
            } else {
                clearInterval(typeTimer);
                typeTimer = null;
                isTyping = false;
                textEl.textContent = text;
                if (onComplete) onComplete();
            }
        }, 45);
    }

    // ── 跳过打字机 ──
    function skipTypewriter() {
        if (!isTyping) return;
        if (typeTimer) {
            clearInterval(typeTimer);
            typeTimer = null;
        }
        const d = dialogueQueue[dialogueIndex - 1];
        if (d) {
            document.getElementById('dialogue-text').textContent = d.text || '';
        }
        isTyping = false;
        document.getElementById('next-btn').disabled = false;
    }

    // ── 显示规则与进入按钮 ──
    function showRulesAndEnter() {
        document.getElementById('dialogue-box').style.display = 'none';
        document.getElementById('chess-notice').style.display = 'block';
        document.getElementById('enter-section').style.display = 'block';
    }

    // ── 进入 Boss 战 ──
    async function enterBattle() {
        try {
            const resp = await fetch(`${API_BASE}/api/heaven-boss/enter`, { method: 'POST' });
            const data = await resp.json();

            if (!data.success) {
                showBlocked(data.reason || '进入失败');
                return;
            }

            // 跳转到象棋游戏（天道Boss战使用象棋棋盘，端口 8000）
            // 通过 URL 参数标识 Boss 战模式
            const bossPort = 8000;
            window.location.href = `http://${window.location.hostname}:${bossPort}/?boss=tiandao&cheat=locked`;
        } catch (e) {
            console.error('进入Boss战失败:', e);
            alert('进入失败，请重试');
        }
    }

    // ── 显示阻止界面 ──
    function showBlocked(message) {
        const overlay = document.getElementById('blocked-overlay');
        document.getElementById('blocked-message').textContent = message;
        overlay.classList.add('active');
    }

    // ── 事件绑定 ──
    function bindEvents() {
        document.getElementById('next-btn').addEventListener('click', () => {
            if (isTyping) {
                skipTypewriter();
            } else {
                showNextDialogue();
            }
        });

        document.getElementById('dialogue-box').addEventListener('click', () => {
            if (isTyping) {
                skipTypewriter();
            } else if (dialogueIndex < dialogueQueue.length) {
                showNextDialogue();
            }
        });

        document.getElementById('enter-btn').addEventListener('click', enterBattle);

        document.addEventListener('keydown', (e) => {
            if (e.code === 'Space' || e.code === 'Enter') {
                e.preventDefault();
                if (isTyping) {
                    skipTypewriter();
                } else if (document.getElementById('dialogue-box').style.display !== 'none') {
                    showNextDialogue();
                }
            }
        });
    }

    // ── 启动 ──
    document.addEventListener('DOMContentLoaded', () => {
        bindEvents();
        init();
    });

    // 添加 blink 动画样式（如果不存在）
    if (!document.getElementById('blink-style')) {
        const style = document.createElement('style');
        style.id = 'blink-style';
        style.textContent = '@keyframes blink { 0%,50%{opacity:1;} 51%,100%{opacity:0;} }';
        document.head.appendChild(style);
    }
})();

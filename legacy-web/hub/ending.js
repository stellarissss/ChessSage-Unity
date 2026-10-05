/**
 * 结局展示系统（v1.4）
 */
(function() {
    'use strict';
    const API_BASE = '/samsara/story';

    function getEndingId() {
        const params = new URLSearchParams(window.location.search);
        return params.get('ending') || params.get('id');
    }

    async function init() {
        let endingId = getEndingId();
        if (!endingId) {
            // 自动判定结局
            const resp = await fetch(`${API_BASE}/api/endings/determine`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({}),
            });
            const result = await resp.json();
            endingId = result.ending_id;
        }
        if (endingId) {
            await loadEnding(endingId);
        } else {
            showError();
        }
        loadEndingsList();
    }

    async function loadEnding(endingId) {
        try {
            const resp = await fetch(`${API_BASE}/api/story/ending/${endingId}`);
            const data = await resp.json();
            if (!data.success) { showError(); return; }
            renderEnding(data.data, endingId);
        } catch (e) {
            console.error('加载结局失败:', e);
            showError();
        }
    }

    function renderEnding(ending, endingId) {
        const typeMap = { good:'GOOD ENDING', bad:'BAD ENDING', neutral:'NEUTRAL ENDING', true:'TRUE ENDING', worst:'WORST ENDING' };
        document.getElementById('ending-type').textContent = typeMap[ending.type] || 'ENDING';
        const nameEl = document.getElementById('ending-name');
        nameEl.textContent = ending.name || endingId;
        nameEl.className = 'ending-name';
        if (ending.type === 'worst') nameEl.classList.add('worst');
        if (ending.type === 'true') nameEl.classList.add('true');

        document.getElementById('ending-description').textContent = ending.description || '';

        // 合并 dialogues_before + dialogues + epilogue_dialogues
        const allDialogues = [
            ...(ending.dialogues_before || []),
            ...(ending.dialogues || []),
            ...(ending.epilogue_dialogues || []),
        ];
        const dialoguesEl = document.getElementById('ending-dialogues');
        dialoguesEl.innerHTML = '';
        for (const d of allDialogues) {
            const speaker = d.speaker === 'narrator' ? '旁白' : d.speaker;
            const text = (d.text || '').replace('{prayer_count}', '<span style="color:var(--accent-gold);font-weight:bold;">' + (ending.prayer_count || '?') + '</span>');
            dialoguesEl.innerHTML += `
                <div class="ending-dialogue">
                    <div class="ending-speaker">${speaker}</div>
                    <div class="ending-text">${text}</div>
                </div>
            `;
        }

        const epilogueEl = document.getElementById('ending-epilogue');
        if (ending.epilogue) {
            epilogueEl.textContent = ending.epilogue;
            epilogueEl.style.display = 'block';
        } else {
            epilogueEl.style.display = 'none';
        }

        // 背景（静态图作为视频缺失时的回退）
        if (ending.background) {
            document.getElementById('ending-scene').style.backgroundImage = `url(/shared/assets/backgrounds/${ending.background})`;
        }
        // CG 视频循环播放（优先）；无视频时回退到静态 CG 图
        const video = document.getElementById('ending-cg-video');
        if (ending.cg) {
            const cgBase = ending.cg.replace(/\.(jpg|jpeg|png)$/i, '');
            const videoSrc = `/shared/assets/cg/videos/${cgBase}.mp4`;
            video.src = videoSrc;
            video.play().catch(() => {});
        }
    }

    async function loadEndingsList() {
        try {
            const resp = await fetch(`${API_BASE}/api/endings`);
            const data = await resp.json();
            const listEl = document.getElementById('endings-list');
            listEl.innerHTML = '<div style="width:100%; text-align:center; color:var(--text-dim); font-size:12px; margin-bottom:8px;">结局图鉴</div>';
            for (const [id, info] of Object.entries(data.endings || {})) {
                const badge = document.createElement('span');
                badge.className = 'ending-badge ' + (info.unlocked ? 'unlocked' : 'locked');
                badge.textContent = info.unlocked ? info.name : '???';
                listEl.appendChild(badge);
            }
        } catch (e) {}
    }

    function showError() {
        document.getElementById('ending-name').textContent = '无法判定结局';
        document.getElementById('ending-description').textContent = '可能需要先完成天道Boss战。';
    }

    document.getElementById('replay-btn').addEventListener('click', () => {
        if (confirm('确定要重新游玩吗？当前进度将保留，但会从头开始六道试炼。')) {
            window.location.href = '/overworld';
        }
    });

    document.addEventListener('DOMContentLoaded', init);
})();

/**
 * 记忆相册系统（v1.4）
 */
(function() {
    'use strict';
    const API_BASE = '/samsara/story';
    const REALM_ICONS = { hell:'☯', hungry:'⬢', animal:'🐅', human:'♜', asura:'⚪', heaven:'☸️' };
    const REALM_NAMES = { hell:'地狱道', hungry:'饿鬼道', animal:'畜生道', human:'人道', asura:'阿修罗道', heaven:'天道' };

    async function loadFragments() {
        try {
            const resp = await fetch(`${API_BASE}/api/memory`);
            const data = await resp.json();
            renderFragments(data);
        } catch (e) {
            console.error('加载记忆碎片失败:', e);
        }
    }

    function renderFragments(data) {
        const grid = document.getElementById('fragments-grid');
        const counter = document.getElementById('album-counter');
        const banner = document.getElementById('all-collected-banner');

        const unlocked = data.unlock_count || 0;
        const total = data.total || 6;
        counter.textContent = `${unlocked} / ${total}`;

        if (data.all_collected) {
            banner.style.display = 'block';
        }

        grid.innerHTML = '';
        for (const frag of data.fragments) {
            const card = document.createElement('div');
            card.className = 'fragment-card' + (frag.unlocked ? '' : ' locked');
            card.innerHTML = `
                <div class="fragment-icon">${REALM_ICONS[frag.realm] || '?'}</div>
                <div class="fragment-realm">${REALM_NAMES[frag.realm] || frag.realm}</div>
                <div class="fragment-title">${frag.unlocked ? frag.title : '???'}</div>
                <div class="fragment-status ${frag.unlocked ? 'unlocked' : 'locked'}">
                    ${frag.unlocked ? '已解锁' : (frag.condition_met ? '可解锁' : '未解锁')}
                </div>
            `;
            if (frag.unlocked) {
                card.addEventListener('click', () => showDetail(frag.realm));
            } else if (frag.condition_met) {
                card.addEventListener('click', () => tryUnlock(frag.realm));
            }
            grid.appendChild(card);
        }
    }

    async function tryUnlock(realm) {
        try {
            const resp = await fetch(`${API_BASE}/api/memory/${realm}/unlock`, { method: 'POST' });
            const data = await resp.json();
            if (data.success) {
                loadFragments();
                showDetail(realm);
            } else if (data.already_unlocked) {
                showDetail(realm);
            } else {
                alert(data.reason || '解锁失败');
            }
        } catch (e) {
            console.error('解锁失败:', e);
        }
    }

    async function showDetail(realm) {
        try {
            const resp = await fetch(`${API_BASE}/api/memory/${realm}`);
            const data = await resp.json();
            if (!data.fragment) return;

            const frag = data.fragment;
            const content = document.getElementById('detail-content');
            let dialoguesHtml = '';
            for (const d of frag.dialogues || []) {
                const speaker = d.speaker === 'narrator' ? '旁白' : d.speaker;
                dialoguesHtml += `
                    <div class="detail-dialogue">
                        <div class="detail-speaker">${speaker}</div>
                        <div class="detail-text">${d.text}</div>
                    </div>
                `;
            }
            content.innerHTML = `
                <div class="detail-title">${frag.title}</div>
                <div class="detail-narration">${frag.narration}</div>
                <div class="detail-dialogues">${dialoguesHtml}</div>
            `;
            // 顶部插入 CG 视频循环播放（若有 cg 字段）
            if (frag.cg) {
                const cgBase = frag.cg.replace(/\.(jpg|jpeg|png)$/i, '');
                const video = document.createElement('video');
                video.className = 'detail-cg-video';
                video.src = `/shared/assets/cg/videos/${cgBase}.mp4`;
                video.autoplay = true;
                video.muted = true;
                video.loop = true;
                video.playsInline = true;
                video.preload = 'auto';
                content.insertBefore(video, content.firstChild);
                video.play().catch(() => {});
            }
            document.getElementById('detail-overlay').classList.add('active');
        } catch (e) {
            console.error('加载详情失败:', e);
        }
    }

    document.getElementById('detail-close').addEventListener('click', () => {
        document.getElementById('detail-overlay').classList.remove('active');
    });
    document.getElementById('detail-overlay').addEventListener('click', (e) => {
        if (e.target.id === 'detail-overlay') {
            e.currentTarget.classList.remove('active');
        }
    });

    document.addEventListener('DOMContentLoaded', loadFragments);
})();

/**
 * 沙盒总坛前端（纯净模式）
 * 与 RPG 总坛完全隔离：不加载 samsara API、不显示业力/识破/技能树/成就
 * 仅渲染 6 个纯净棋类卡片并直连对应端口
 */

// 防泄漏：模块级定时器ID，render() 中复用
let _statusIntervalId = null;

// 棋类定位标签：象棋=主推金标，跳棋=不推荐的测试红标
const GAME_TAGS = {
    xiangqi: { text: "★ 主推", cls: "recommended" },
    tiaoqi:  { text: "⚠ 不推荐的测试", cls: "notrec" },
};

const DEFAULT_SANDBOX_GAMES = [
    {
        id: "xiangqi", name: "纯净象棋", icon: "♜",
        sub: "楚河汉界 · 自由对弈",
        description: "纯净象棋，AI 改规无业力束缚。", port: 8010,
    },
    {
        id: "wuziqi", name: "纯净五子棋", icon: "⚫",
        sub: "五连登仙 · 自由对弈",
        description: "纯净五子棋，连珠成线无拘束。", port: 8011,
    },
    {
        id: "weiqi", name: "纯净围棋", icon: "⚪",
        sub: "混沌气局 · 自由对弈",
        description: "纯净围棋，十九路自由改写。", port: 8012,
    },
    {
        id: "dongwuqi", name: "纯净动物棋", icon: "🐘",
        sub: "斗兽丛林 · 自由对弈",
        description: "纯净动物棋，鼠象狮各显神通。", port: 8013,
    },
    {
        id: "tiaoqi", name: "纯净跳棋", icon: "⬢",
        sub: "六角星途 · 自由对弈",
        description: "纯净跳棋，连跳奔袭无止境。", port: 8014,
    },
    {
        id: "heibaiqi", name: "纯净黑白棋", icon: "☯",
        sub: "阴阳翻转 · 自由对弈",
        description: "纯净黑白棋，夹吃翻转自定义。", port: 8015,
    },
];

function buildUrl(port) {
    return `http://localhost:${port}/`;
}

function render(games) {
    const grid = document.getElementById("game-grid");
    grid.innerHTML = "";

    games.forEach((game, index) => {
        const url = game.url || buildUrl(game.port);
        const card = document.createElement("article");
        card.className = "card";
        card.style.animationDelay = `${0.15 + index * 0.12}s`;
        card.setAttribute("role", "button");
        card.setAttribute("tabindex", "0");
        card.setAttribute("aria-label", `进入${game.name}`);

        const tag = GAME_TAGS[game.id];
        const tagHtml = tag ? `<span class="card-tag ${tag.cls}">${tag.text}</span>` : "";

        card.innerHTML = `
            <div class="realm-label">纯净对弈</div>
            ${tagHtml}
            <div class="icon">${game.icon}</div>
            <h2 class="card-title">${game.name}</h2>
            <div class="card-sub">${game.sub}</div>
            <p class="card-desc">${game.description}</p>
            <div class="card-footer">
                <div style="display:flex;align-items:center;">
                    <span class="status-dot loading" id="status-${game.id}"></span>
                    <span class="port-badge">PORT ${game.port}</span>
                </div>
                <button class="enter-btn">进入</button>
            </div>
        `;

        const open = () => {
            // 按需启动对应纯净棋类进程后再打开
            fetch(`/api/lazy/start?mode=sandbox&game=${encodeURIComponent(game.id)}`, { cache: "no-store" })
                .catch(() => {})
                .finally(() => {
                    const target = game.url || buildUrl(game.port);
                    window.open(target, "_blank", "noopener,noreferrer");
                });
        };
        card.addEventListener("click", open);
        card.addEventListener("keydown", (e) => {
            if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                open();
            }
        });

        grid.appendChild(card);
    });

    updateStatuses(games);
    // 防泄漏：清理旧定时器后再启动新的（避免 render 重复调用导致多定时器叠加）
    if (_statusIntervalId) clearInterval(_statusIntervalId);
    _statusIntervalId = setInterval(() => updateStatuses(games), 6000);
}

async function updateStatuses(games) {
    // 并发检测6个服务状态（串行→并发，每轮检测从 ~18s 降到 ~3s）
    await Promise.all(games.map(async (game) => {
        const dot = document.getElementById(`status-${game.id}`);
        if (!dot) return;
        dot.className = "status-dot loading";
        try {
            const url = game.url || buildUrl(game.port);
            await fetch(url, {
                method: "GET",
                mode: "no-cors",
                signal: AbortSignal.timeout(3000),
            });
            dot.className = "status-dot";
        } catch (err) {
            dot.className = "status-dot offline";
        }
    }));
}

async function init() {
    const meta = document.getElementById("sandbox-meta");
    try {
        const res = await fetch("/api/sandbox/games", { signal: AbortSignal.timeout(5000) });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const games = await res.json();
        meta.textContent = `沙盒在线 · 已发现 ${games.length} 重纯净棋境`;
        render(games);
    } catch (err) {
        console.warn("无法从 /api/sandbox/games 获取服务列表，使用默认配置", err);
        meta.textContent = "沙盒在线 · 使用默认端口配置";
        render(DEFAULT_SANDBOX_GAMES);
    }
}

init();

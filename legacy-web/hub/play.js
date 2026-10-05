/**
 * 六道小世界 · 对局容器
 * 同窗口内嵌棋类服务 iframe（无边框），顶部悬浮返回条。
 * 返回时重新拉取地图状态，正确显示通关/解锁后的节点。
 */

const params = new URLSearchParams(location.search);
const realm = params.get("realm") || "hell";
const port = params.get("port") || "8000";

const REALM_NAMES = {
    hell: "地狱道", hungry: "饿鬼道", animal: "畜生道",
    human: "人道", asura: "阿修罗道", heaven: "天道",
};

const frame = document.getElementById("game-frame");
const railTitle = document.getElementById("play-title");
const backBtn = document.getElementById("btn-back-map");

// iframe 指向对应棋类服务，带 embed=1 内嵌模式（隐藏返回/新窗口等外部元素）+ realm（供胜负页“返回地图”）
const gameUrl = `http://localhost:${port}/?embed=1&realm=${encodeURIComponent(realm)}`;
frame.src = gameUrl;
railTitle.textContent = `${REALM_NAMES[realm] || realm} · 对局`;

// ── 进程保活与主动回收 ──
// 只要该对局容器页仍打开，就周期心跳刷新服务最近使用时间，避免空闲回收器在对局中误杀。
// （玩家切走焦点/切标签页时此页仍在，心跳继续，进程保活；关闭页面后由兜底超时回收。）
function hubPing() {
    try { fetch(`/api/lazy/ping?port=${encodeURIComponent(port)}`, { cache: "no-store" }).catch(() => {}); } catch (e) {}
}
hubPing();
const pingTimer = setInterval(hubPing, 40000);

// 玩家主动返回地图：立即回收该棋类进程（而不是等空闲超时）
function stopGameProcess() {
    clearInterval(pingTimer);
    try { fetch(`/api/lazy/stop?port=${encodeURIComponent(port)}`, { method: "POST", cache: "no-store" }).catch(() => {}); } catch (e) {}
}

// 关闭网页/浏览器退出时回收：pagehide 仅在真实离开/关闭页面时触发，
// 切标签页、休眠、焦点移开走 visibilitychange（不触发），因此不会在后台误回收。
// 用 sendBeacon 保证页面卸载瞬间请求仍被发出。
function stopOnPageHide() {
    try { navigator.sendBeacon(`/api/lazy/stop?port=${encodeURIComponent(port)}`); } catch (e) {}
}
window.addEventListener("pagehide", stopOnPageHide);

// 返回 2.5D 大陆大地图并强制刷新状态（?r=时间戳），并按当前道（realm）回填导航焦点
backBtn.addEventListener("click", (e) => {
    e.preventDefault();
    stopGameProcess();
    location.href = `/overworld?r=${Date.now()}&backrealm=${encodeURIComponent(realm)}`;
});

// 服务就绪前显示加载态（iframe 正常加载即可，不阻塞）
frame.addEventListener("error", () => {
    railTitle.textContent = "对局加载失败";
});
/* ═══════════════════════════════════════════════════════════════
   大地图渲染器智能分发器（v3 · WebGPU 优先 + 智能画质 + melonJS 兜底）

   启动流程：
     1. 设备能力检测（WebGPU 适配器 / 硬件信号 / 用户覆盖）
     2. 智能选择画质档位：ultra / high / balanced / software
     3. 有 WebGPU → 加载次世代 WebGPU 渲染器（SSAO+Bloom+体积光+ACES）
        └ 启动后呈现自检：管线成功但画面不出（驱动/合成器缺陷）→ 自动降级
     4. 无 WebGPU / 初始化失败 / 呈现异常 → melonJS 兜底（WebGL 优先 Canvas 兜底，
        任何设备黑屏不可能）

   画质档位覆盖方式（优先级从高到低）：
     - URL 参数  ?owq=ultra|high|balanced|software|auto
     - localStorage chesssage_ow_quality
     - 自动检测（默认）
   ═══════════════════════════════════════════════════════════════ */
(function () {
  'use strict';

  var QUALITY_KEY = 'chesssage_ow_quality';
  var VALID_Q = ['ultra', 'high', 'balanced', 'software', 'auto'];

  /* ── 角标 UI ── */
  function ensureBadge() {
    var b = document.getElementById('renderer-badge');
    if (b) return b;
    b = document.createElement('div');
    b.id = 'renderer-badge';
    b.style.cssText = 'position:fixed;left:12px;top:8px;z-index:120;pointer-events:none;' +
      'font:11px "Noto Serif SC",serif;color:#8f8a7d;letter-spacing:.06em;opacity:.8;' +
      'text-shadow:0 1px 3px rgba(0,0,0,.8);user-select:none;';
    document.body.appendChild(b);
    return b;
  }
  function setBadge(text) {
    try { ensureBadge().textContent = text; } catch (e) {}
  }

  /* ── 兜底路径：melonJS（保留 __owFallbackToIso 旧名兼容） ── */
  function describeIssue(reason) {
    var r = String(reason || '');
    if (r.indexOf('WebGPU') !== -1 || r.indexOf('webgpu') !== -1) return r;
    return 'WebGPU 渲染不可用：' + (r || '未知原因') + '。已切换到兼容渲染器。';
  }
  var melonLoading = false;
  function loadMelon(reason) {
    if (window.__owMelonBooted || melonLoading) return;
    melonLoading = true;
    console.warn('[dispatcher] 降级 melonJS 渲染器：', reason || '');
    setBadge('渲染：melonJS · 加载中…');
    Promise.resolve().then(function () {
      return import('/static/vendor/melonjs/index.js');
    }).then(function () {
      return import('/static/overworld-melonjs.js'); // 模块自动 melonBoot()
    }).then(function () {
      setTimeout(function () {
        try {
          var g = window.OverworldGame;
          var r = g && g.renderer;
          var isGL = r && typeof r.isWebGL === 'function' ? r.isWebGL() : (r && r.constructor && /WebGL/i.test(r.constructor.name));
          setBadge(isGL ? '渲染：melonJS · WebGL' : '渲染：melonJS · 内置画布');
        } catch (e) { setBadge('渲染：melonJS'); }
      }, 1200);
    }).catch(function (err) {
      console.error('[dispatcher] melonJS 加载失败：', err);
      var UIw = window.OverworldUI;
      if (UIw) {
        if (typeof UIw.removeLoading === 'function') UIw.removeLoading();
        if (typeof UIw.showError === 'function') UIw.showError('渲染器加载失败：' + (err && err.message ? err.message : err));
      }
    });
  }
  window.__owFallbackToIso = function (reason) {
    console.error('[dispatcher] 渲染器初始化失败：', reason || '');
    var UIw = window.OverworldUI;
    if (UIw) {
      if (typeof UIw.removeLoading === 'function') UIw.removeLoading();
      if (typeof UIw.showError === 'function') UIw.showError(describeIssue(reason));
    }
    loadMelon(reason);
    return Promise.resolve(null);
  };

  /* ── 加载遮罩兜底：渲染器就绪 promise 悬挂时最多 12s 强制移除 ── */
  setTimeout(function () {
    try {
      var ov = document.getElementById('loading-overlay');
      if (ov && !/\bhidden\b/.test(ov.className)) {
        ov.classList.add('hidden');
        var UIw = window.OverworldUI;
        if (UIw && typeof UIw.removeLoading === 'function') UIw.removeLoading();
      }
    } catch (e) {}
  }, 12000);

  /* ══ 设备能力检测与智能画质分级 ═══════════════════════════════ */
  function userOverride() {
    try {
      var m = location.search.match(/[?&]owq=(ultra|high|balanced|software|auto)\b/);
      if (m) return m[1];
      var v = localStorage.getItem(QUALITY_KEY);
      if (v && VALID_Q.indexOf(v) !== -1) return v;
    } catch (e) {}
    return 'auto';
  }

  function detectDevice() {
    // 返回 { supported, tier, adapterInfo } —— tier ∈ ultra/high/balanced/software
    var result = { supported: false, tier: 'balanced', adapterInfo: '' };
    if (!navigator.gpu) return Promise.resolve(result);
    // 兜底超时：requestAdapter 的**首次调用**在软件光栅器（SwiftShader / lavapipe）与
    // 冷启动驱动下可能极慢——实测同一浏览器内首次 34~37s、后续仅 ~1s（Dawn 首次初始化
    // 要建立 GPU 进程/编译管线）。给足 45s 上限：既避免误杀慢速软渲染（否则会被误判成
    // 「无 WebGPU」直接落 melonJS，用户体感就是「WebGPU 用不了」），
    // 也保证绝不会永久卡死——超时即落 melonJS 兜底，玩家始终有可玩画面。
    var ADAPTER_TIMEOUT_MS = 45000;
    var to = new Promise(function (res) {
      setTimeout(function () { res({ __timeout: true }); }, ADAPTER_TIMEOUT_MS);
    });
    return Promise.race([
      navigator.gpu.requestAdapter().then(function (adapter) { return { __adapter: adapter }; }),
      to
    ]).then(function (r) {
      if (!r || r.__timeout) {
        console.warn('[dispatcher] WebGPU requestAdapter 超时（' + (ADAPTER_TIMEOUT_MS / 1000) + 's），按不可用处理');
        return result;
      }
      var adapter = r.__adapter;
      if (!adapter) return result;
      result.supported = true;
      var info = {};
      try { info = adapter.info || {}; } catch (e) {}
      var desc = [info.vendor, info.architecture, info.device, info.description]
        .filter(Boolean).join(' ').toLowerCase();
      result.adapterInfo = desc || 'webgpu';
      var isFallback = false;
      try { isFallback = !!adapter.isFallbackAdapter; } catch (e) {}
      var soft = isFallback || /swiftshader|llvmpipe|lavapipe|software|angle \(google/.test(desc);
      // 硬件信号：移动设备 / 核显少核 / 低内存 → balanced；多核大内存桌面 → ultra
      var cores = navigator.hardwareConcurrency || 4;
      var mem = navigator.deviceMemory || 8;
      var mobile = /android|iphone|ipad|mobile/i.test(navigator.userAgent);
      if (soft) {
        result.tier = 'software';
      } else if (mobile || cores <= 4 || mem <= 4) {
        result.tier = 'balanced';
      } else if (cores >= 12 && mem >= 16) {
        result.tier = 'ultra';
      } else {
        result.tier = 'high';
      }
      /* ── 关键：释放探测用适配器，并把 detect 结果作为「首次适配器」复用 ──
         浏览器对未释放的 GPUAdapter 持有独占引用（Chrome 默认只允许 1 个存活适配器）。
         此前该适配器无任何引用（既不 return 也不 close），在检测与渲染器 _initGPU
         再次 requestAdapter + requestDevice 时会耗尽适配器配额，导致
         requestDevice() 永久挂起 → _initGPU 永不 resolve → WebGPU 通道停在
         "检测设备能力" 后再也起不来（表现为画布全黑、帧计数为 null），
         最终被 12s 兜底遮罩/降级吞掉，用户体感就是「WebGPU 用不了」。
         这里改为：把探测得到的 adapter 透传给渲染器复用（overworld-wgpu.js 优先取用），
         并在确实用不上时 close() 释放。 */
      result.adapter = adapter;
      return result;
    }).catch(function () {
      return result;
    });
  }

  /* ══ 主流程 ═══════════════════════════════════════════════════ */
  function boot() {
    setBadge('渲染：检测设备能力…');
    var wantOverride = userOverride();
    var detectP = detectDevice();

    // 并行预热 melonJS vendor（若需要兜底可省一次网络往返；WebGPU 成功则无副作用）
    var vendorP = import('/static/vendor/melonjs/index.js').catch(function () { return null; });

    detectP.then(function (dev) {
      var tier = dev.tier;
      var quality = wantOverride !== 'auto' ? wantOverride : tier;
      if (!dev.supported) {
        console.info('[dispatcher] WebGPU 不可用，走 melonJS 兜底');
        setBadge('渲染：melonJS（无 WebGPU）');
        return Promise.resolve(vendorP).then(function () { loadMelon('no webgpu'); return null; });
      }
      console.info('[dispatcher] WebGPU 适配器: ' + dev.adapterInfo + ' → 画质档位 ' + quality +
        (wantOverride !== 'auto' ? '（用户指定）' : '（自动检测）'));
      setBadge('渲染：WebGPU · ' + quality);
      // 把设备检测阶段已取得的 adapter 交给渲染器复用（避免二次 requestAdapter 耗尽
      // 适配器配额导致 requestDevice 挂起；用不上时由渲染器负责 close）。
      window.__owDetectedAdapter = dev.adapter || null;
      return import('/static/overworld-wgpu.js').then(function () {
        var g = window.OverworldGame;
        if (!g || typeof g.setQuality !== 'function') throw new Error('WebGPU 渲染器异常');
        g.setQuality(quality);
        // 呈现自检：等待渲染器就绪 + 数帧输出后，采样画面亮度。
        // 管线成功但画面全黑/全白（驱动/合成器缺陷）→ 自动降级 melonJS。
        return (g._ready || Promise.resolve()).then(function () {
          return new Promise(function (res) { setTimeout(res, 3600); });
        }).then(function () {
          if (!g.presentSelfCheck || window.__owMelonBooted) return null;
          // WebGPU 优先级永远高于回退：呈现自检默认【非破坏】——
          // 即便检测到画面异常（极端驱动/合成器缺陷），也保持 WebGPU 通道，
          // 仅在「明确报错」（初始化异常 / 适配器缺失 / 设备丢失）时才回退（见上方 catch 与 device.lost）。
          // 可用 ?owselfcheck=1 手动启用破坏性降级，便于在缺陷硬件上诊断。
          var allowDestructive = /[?&]owselfcheck=1\b/.test(location.search);
          return g.presentSelfCheck().then(function (ok) {
            if (ok === true) {
              setBadge('渲染：WebGPU · ' + quality);
            } else if (ok === false && allowDestructive) {
              try { if (g.destroy) g.destroy(); } catch (e) {}
              window.__owWgpuBooted = false;
              loadMelon('WebGPU 呈现异常（自检全黑/全白 · 已手动启用降级）');
              setBadge('渲染：melonJS（WebGPU 呈现异常已降级）');
            } else {
              console.warn('[dispatcher] WebGPU 呈现自检异常，但保持 WebGPU 通道（默认不主动降级；?owselfcheck=1 可启用破坏性降级）');
              setBadge('渲染：WebGPU · ' + quality + '（自检告警）');
            }
            return null;
          });
        });
      }).catch(function (err) {
        console.error('[dispatcher] WebGPU 渲染器加载/初始化失败：', err);
        window.__owFallbackToIso(err);
      });
    }).catch(function (err) {
      console.error('[dispatcher] 设备检测异常：', err);
      loadMelon('检测异常: ' + err);
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();

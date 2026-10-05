#!/usr/bin/env python3
"""
Bulk-patch 5 主模式 + heibaiqi 前端接入 shared/game_shared_rpg.js
并更新各 index.html 加载该脚本。
"""
import pathlib, re, sys

ROOT = pathlib.Path('/workspace/CHessGAme')

# 每种棋类的配置：
# - install_rerender 用于 _rpgRerender（UI 全量重绘）
# - restart_fn_name 原始重新开始函数名
# - winner_label_map: (isPlayerWin, winner) 文本（用于非 RRG 覆盖前的兼容，本 patch 统一使用 RRG，所以可省略）
# - reset_button: (button_id, restart_fn_bind_name, reset_configs_bind_name)
PER_GAME = {
    'dongwuqi': {
        'path': ROOT / 'dongwuqi/static/app.js',
        'install_rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn_name': 'restart',
        'showGameOver_async': True,
        'reset_configs_has_prompt': True,
    },
    'tiaoqi': {
        'path': ROOT / 'tiaoqi/static/app.js',
        'install_rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn_name': 'restart',
        'showGameOver_async': True,
        'reset_configs_has_prompt': True,
    },
    'wuziqi': {
        'path': ROOT / 'wuziqi/static/app.js',
        'install_rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn_name': 'restart',
        'showGameOver_async': True,
        'reset_configs_has_prompt': True,
    },
    'weiqi': {
        'path': ROOT / 'weiqi/static/app.js',
        'install_rerender': (
            "instance.renderBoard();\n"
            "instance.renderStones();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateCaptureStats();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn_name': 'restartGame',
        'reset_fn_name': 'resetConfigs',
        'showGameOver_async': True,
        'reset_configs_has_prompt': True,
    },
    'heibaiqi': {
        'path': ROOT / 'heibaiqi/static/app.js',
        'install_rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "await instance.renderValidPlacements();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn_name': 'restart',
        'showGameOver_async': True,
        'reset_configs_has_prompt': True,
    },
}

INSTALL_BLOCK_TEMPLATE = """
        // === 六道 RPG 接入（共享模块） ===
        try {{
            if (window.GameSharedRPG) {{
                window.GameSharedRPG.install(this, {{
                    isSandbox: false,
                    rerender: async (instance) => {{
{rerender}
                    }},
                }});
                // 开局三件套
                await this.rpgResetBattleAndApply({{ doSamsaraResetLevel: false, doBroadcast: false }});
                // 事件驱动刷新
                this.initRpgEventListeners();
            }} else {{
                // fallback：老流程
                this.loadSamsaraState();
                this.startKarmaPolling();
            }}
        }} catch (e) {{
            console.warn('[RPG] 开局三件套失败，继续走默认 loadSamsaraState：', e);
            try {{ await this.loadSamsaraState(); }} catch (e2) {{}}
            try {{ await this.loadLocalKarmaDetection(); }} catch (e2) {{}}
        }}
        // 兜底：若三件套失败，仍以 Samsara 服务器为准刷一次
        try {{ await this.loadSamsaraState(); }} catch (e) {{}}
        try {{ await this.loadLocalKarmaDetection(); }} catch (e) {{}}
        // 禁用轮询（事件驱动替代）
        this.stopKarmaPolling();
"""

def indent_block(block: str, n: int = 8) -> str:
    pad = ' ' * n
    return '\n'.join(pad + line if line else line for line in block.rstrip().split('\n'))

def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"[ERROR] {label}: expected 1 match, got {count}. len_old={len(old)}")
    return text.replace(old, new, 1)

def patch_js(game: str, cfg: dict) -> None:
    path = cfg['path']
    text = path.read_text(encoding='utf-8')

    # ---------------------------------------------------------------
    # (1) init() 尾：找到  this.loadSamsaraState(); this.startKarmaPolling();
    #     替换为 install + 三件套 + 事件监听
    # ---------------------------------------------------------------
    old_init_tail = None
    if game == 'weiqi':
        # weiqi 中 init() 结尾并非 loadSamsaraState / startKarmaPolling 组合，需重新找
        # 先确认：
        pass
    # 默认搜索：
    common_old = (
        "        this.loadSamsaraState();\n"
        "        this.startKarmaPolling();\n"
        "        this._initialized = true;\n"
    )
    await_old = (
        "        await this.loadSamsaraState();\n"
        "        this.startKarmaPolling();\n"
        "        this._initialized = true;\n"
    )
    # heibaiqi: 无 startKarmaPolling/loadLocalKarmaDetection，init 只有 await loadSamsaraState + _initialized=true
    hb_old = (
        "        this.checkApiKey();\n"
        "        this.loadTokenStats();\n"
        "        await this.loadSamsaraState();\n"
        "        this._initialized = true;\n"
        "        this.dispatchEvent(new CustomEvent('ready', { bubbles: true, composed: true }));\n"
    )
    fallback_old = (
        "this.loadSamsaraState();\n"
        "this.startKarmaPolling();\n"
        "this._initialized = true;\n"
    )
    install = INSTALL_BLOCK_TEMPLATE.format(rerender=indent_block(cfg['install_rerender'], 20))
    new_init_tail = install + "\n        this._initialized = true;\n"
    if common_old in text:
        text = replace_once(text, common_old, new_init_tail, f"{game}: common init tail")
    elif await_old in text:
        text = replace_once(text, await_old, new_init_tail, f"{game}: await init tail")
    elif hb_old in text:
        new_hb = (
            "        this.checkApiKey();\n"
            "        this.loadTokenStats();\n"
            + install
            + "\n        this._initialized = true;\n"
            "        this.dispatchEvent(new CustomEvent('ready', { bubbles: true, composed: true }));\n"
        )
        text = replace_once(text, hb_old, new_hb, f"{game}: heibaiqi init tail")
    elif fallback_old in text:
        text = replace_once(text, fallback_old, new_init_tail, f"{game}: fallback init tail")
    else:
        raise SystemExit(f"[ERROR] {game}: 找不到 init 尾模式")

    # ---------------------------------------------------------------
    # (2) showGameOver() 整段：用 showRpgGameOver() 替代旧版
    #     原始函数范围是从 "async showGameOver" 到下一个 showVictoryReward 前
    # ---------------------------------------------------------------
    # 策略：重写 showGameOver 函数体为直接调用 showRpgGameOver
    # 正则：抓 "async showGameOver(" ... "showVictoryReward" 前的所有内容
    sgo_pattern = re.compile(
        r'(async showGameOver\([^)]*\)\s*\{[\s\S]*?\n\s*\}\n)(?=\n\s*showVictoryReward\()'
    )
    m = sgo_pattern.search(text)
    if not m:
        # heibaiqi / weiqi 可能签名不同，尝试更宽松：showGameOver
        sgo2 = re.compile(
            r'(async showGameOver\([^)]*\)\s*\{[\s\S]*?\n\s*\}\n)(\n\s*showVictoryReward\()'
        )
        m = sgo2.search(text)
        if not m:
            raise SystemExit(f"[ERROR] {game}: 找不到 async showGameOver → showVictoryReward 段")
    new_sgo = (
        f"async showGameOver() {{\n"
        f"        // Bug2 修复：统一使用共享 RPG 胜负页三按钮 + resolve 奖励\n"
        f"        return this.showRpgGameOver();\n"
        f"    }}\n"
    )
    start, end = m.span(1)
    text = text[:start] + new_sgo + text[end:]

    # ---------------------------------------------------------------
    # (3) restart() 替换为 this.rpgRestart()
    # ---------------------------------------------------------------
    restart_fn = cfg['restart_fn_name']
    # 匹配 async restartFn() { ... } 直到下一个方法（下一个 "async " 或 " showVictoryReward" 之后
    # 简化：匹配 "async restartFn() {" 开始，到下一个与该层相同缩进的 "}\n"
    # 以函数开始定缩进，用简单括号计数
    marker = f"    async {restart_fn}() {{"
    idx = text.find(marker)
    if idx == -1:
        marker2 = f"async {restart_fn}() {{"
        idx = text.find(marker2)
        if idx == -1:
            raise SystemExit(f"[ERROR] {game}: 找不到 {restart_fn}() 起点")
        marker = marker2
    depth = 0
    i = idx
    started = False
    while i < len(text):
        ch = text[i]
        if ch == '{':
            depth += 1
            started = True
        elif ch == '}':
            depth -= 1
            if started and depth == 0:
                break
        i += 1
    if depth != 0 or not started:
        raise SystemExit(f"[ERROR] {game}: {restart_fn}() 括号未匹配")
    end = i + 1  # 含 }
    new_restart = (
        f"    async {restart_fn}() {{\n"
        f"        // Bug3 修复：重玩调用 RPG 三件套（apply_level_config + reset_battle + UI 重绘）\n"
        f"        return this.rpgRestart();\n"
        f"    }}\n"
    )
    text = text[:idx] + new_restart + text[end:]

    # ---------------------------------------------------------------
    # (4) btn-restart 点击：保持绑定新 restartFn 即可（前面已改函数体）
    #     但确认：weiqi 有时用 restartGame，所以无需改绑定
    # ---------------------------------------------------------------

    # ---------------------------------------------------------------
    # (5) btn-reset-configs：改为调用 this.rpgResetConfigsHandler('soft')
    #     先定位 bindEvents 内 getElementById('btn-reset-configs').addEventListener('click', async () => { ... });
    #     需要找到该段从 click handler 开始到 }); 结束的全段
    # ---------------------------------------------------------------
    rc_start = text.find("getElementById('btn-reset-configs').addEventListener('click'")
    if rc_start == -1:
        rc_start = text.find('getElementById("btn-reset-configs").addEventListener("click"')
    if rc_start == -1:
        raise SystemExit(f"[ERROR] {game}: 找不到 btn-reset-configs click handler")
    # 向前定位整句开始（ this.shadowRoot.getElementById... ）
    line_start = text.rfind('\n', 0, rc_start) + 1
    # 从 line_start 开始找匹配的 "async () => { ... });"
    # 找第一个 '{' 然后匹配括号
    brace_start = text.find('{', rc_start)
    if brace_start == -1:
        raise SystemExit(f"[ERROR] {game}: reset-configs handler 找不到函数体 '{{'")
    depth = 0
    i = brace_start
    started = False
    while i < len(text):
        ch = text[i]
        if ch == '{':
            depth += 1
            started = True
        elif ch == '}':
            depth -= 1
            if started and depth == 0:
                break
        i += 1
    if not started or depth != 0:
        raise SystemExit(f"[ERROR] {game}: reset-configs handler 括号未匹配")
    # handler 结束在 i（含 }），下一个字符可能是 "\n        });"
    # 找 }); 关闭 addEventListener
    end_mark = text.find('});', i)
    if end_mark == -1:
        raise SystemExit(f"[ERROR] {game}: reset-configs handler 找不到 closing end-marker")
    end_mark += 3
    # 获取缩进
    leading = text[line_start:rc_start]
    indent_ = leading[:len(leading) - len(leading.lstrip())] or "        "
    new_handler = (
        f"{indent_}getElementById('btn-reset-configs').addEventListener('click', async () => {{\n"
        f"{indent_}    if (!confirm('确定要重置所有配置吗？会同步重置六道关卡/业力（软重置：保留成就/技能），规则配置恢复默认。')) return;\n"
        f"{indent_}    await this.rpgResetConfigsHandler('soft');\n"
        f"{indent_}}});\n"
    )
    text = text[:line_start] + new_handler + text[end_mark:]

    # ---------------------------------------------------------------
    # (6) bindEvents() 末尾添加 RPG 事件监听（已有则跳过）
    #     找 bindEvents() 函数结束：利用 marker，weiqi 里 bindEvents 末尾常常有
    #     "this.shadowRoot.getElementById('btn-restart').addEventListener('click', ..."
    #     我们在 bindEvents 函数最后一个 } 之前插入 initRpgEventListeners 调用
    # ---------------------------------------------------------------
    be_marker = "    bindEvents() {"
    be_idx = text.find(be_marker)
    if be_idx == -1:
        be_marker2 = "bindEvents() {"
        be_idx = text.find(be_marker2)
        if be_idx == -1:
            raise SystemExit(f"[ERROR] {game}: 找不到 bindEvents()")
    brace_start = text.find('{', be_idx)
    depth = 0; i = brace_start; started = False
    while i < len(text):
        ch = text[i]
        if ch == '{':
            depth += 1; started = True
        elif ch == '}':
            depth -= 1
            if started and depth == 0:
                break
        i += 1
    if not started or depth != 0:
        raise SystemExit(f"[ERROR] {game}: bindEvents 括号未匹配")
    insert_point = i  # 插入到 } 前
    if 'initRpgEventListeners' not in text[be_idx:insert_point]:
        text = text[:insert_point] + "\n        // 六道 RPG：跨页事件驱动刷新（替代轮询）\n        if (typeof this.initRpgEventListeners === 'function') this.initRpgEventListeners();\n" + text[insert_point:]

    # ---------------------------------------------------------------
    # 写回
    # ---------------------------------------------------------------
    path.write_text(text, encoding='utf-8')
    print(f"[OK] {game}: {path}")


def patch_index_html(game: str) -> None:
    html_path = ROOT / game / 'static' / 'index.html'
    if not html_path.exists():
        print(f"[SKIP] {game}: index.html 不存在")
        return
    text = html_path.read_text(encoding='utf-8')
    marker = '/shared/achievement_checker.js"></script>'
    if marker not in text:
        raise SystemExit(f"[ERROR] {game} index.html: 找不到 achievement_checker.js 行")
    inject_line = '\n    <script src="http://localhost:8080/shared/game_shared_rpg.js"></script>'
    if 'game_shared_rpg.js' not in text:
        text = text.replace(marker, marker + inject_line, 1)
        html_path.write_text(text, encoding='utf-8')
        print(f"[OK] {game} index.html: 注入 game_shared_rpg.js")
    else:
        print(f"[SKIP] {game} index.html: 已存在 game_shared_rpg.js")


def main() -> None:
    for g, cfg in PER_GAME.items():
        print(f"\n=== Patching {g} ===")
        patch_js(g, cfg)
        patch_index_html(g)
    print("\nAll patches done.")


if __name__ == '__main__':
    main()

#!/usr/bin/env python3
"""为 6 沙盒前端注入 shared_rpg + showRpgGameOver 两按钮 + 重玩三件套 fallback。"""
import pathlib, re, sys, json

ROOT = pathlib.Path('/workspace/CHessGAme')
SANDBOX_DIR = ROOT / 'sandbox'

# 每种棋类配置：渲染函数重绘序列
GAMES = {
    'xiangqi': {
        'rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn': 'restart',
        'player_side_default': 'red',
    },
    'dongwuqi': {
        'rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn': 'restart',
        'player_side_default': 'red',
    },
    'tiaoqi': {
        'rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn': 'restart',
        'player_side_default': 'black',
    },
    'wuziqi': {
        'rerender': (
            "instance.clearSelection();\n"
            "instance.renderBoard();\n"
            "instance.renderPieces();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn': 'restart',
        'player_side_default': 'black',
    },
    'weiqi': {
        'rerender': (
            "instance.renderBoard();\n"
            "instance.renderStones();\n"
            "instance.updateTurnIndicator();\n"
            "instance.updateCaptureStats();\n"
            "instance.updateActiveRules();\n"
            "instance.updateGameObjectives();\n"
            "instance.updateAIPersonality();\n"
            "instance.updateMechanisms();\n"
        ),
        'restart_fn': 'restartGame',
        'reset_fn': 'resetConfigs',
        'player_side_default': 'black',
    },
    'heibaiqi': {
        'rerender': (
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
        'restart_fn': 'restart',
        'player_side_default': 'black',
    },
}

def indent(s, n=20):
    p = ' ' * n
    return '\n'.join(p + l if l else l for l in s.rstrip().split('\n'))

def patch_html(g: str) -> None:
    html = SANDBOX_DIR / g / 'static' / 'index.html'
    if not html.exists():
        print(f'[SKIP] {g}/index.html 不存在')
        return
    t = html.read_text(encoding='utf-8')
    marker = '/shared/achievement_checker.js"></script>'
    if marker not in t:
        print(f'[WARN] {g}/index.html 无 achievement marker')
        return
    inject = '\n    <script src="http://localhost:8080/shared/game_shared_rpg.js"></script>'
    if 'game_shared_rpg.js' not in t:
        t = t.replace(marker, marker + inject, 1)
        html.write_text(t, encoding='utf-8')
        print(f'[OK] {g}/index.html 注入 shared_rpg')
    else:
        print(f'[SKIP] {g}/index.html 已注入 shared_rpg')

def patch_js(g: str, cfg: dict) -> None:
    path = SANDBOX_DIR / g / 'static' / 'app.js'
    if not path.exists():
        print(f'[SKIP] {g}: app.js 不存在')
        return
    text = path.read_text(encoding='utf-8')

    # --- (1) init 尾部安装 RPG 模块（使用 isSandbox=true） ---
    # 找到 async init() { ... } 的最后一段特征：通常含 bindEvents()
    # 简化：定位 init() 函数的结束前，插入 install 调用（如果尚未出现）
    install_code = (
        "\n        // === 六道 RPG（沙盒遮罩版，无关卡不推进） ===\n"
        "        try {\n"
        "            if (window.GameSharedRPG) {\n"
        "                window.GameSharedRPG.install(this, {\n"
        "                    isSandbox: true,\n"
        "                    rerender: async (instance) => {\n"
        f"{indent(cfg['rerender'], 20)}\n"
        "                    },\n"
        "                });\n"
        "                // 沙盒不切关，只触发 reset_battle fallback + 事件监听兜底\n"
        "                try { await this.rpgResetBattleAndApply({ doSamsaraResetLevel: false, doBroadcast: false }); } catch(e) {}\n"
        "                if (typeof this.initRpgEventListeners === 'function') this.initRpgEventListeners();\n"
        "            }\n"
        "        } catch (e) { console.warn('[Sandbox RPG] init failed', e); }\n"
    )

    # 插入位置：在 bindEvents() 调用之后
    be_in_init = text.find("        this.bindEvents();\n        this.checkApiKey();")
    if be_in_init == -1:
        be_in_init = text.find("this.bindEvents();\n        this.checkApiKey();")
    if be_in_init == -1:
        # 尝试 heibaiqi 结构
        be_in_init = text.find("this.bindEvents();\n        this.checkApiKey")
    if be_in_init == -1:
        # 弱匹配：在 this.bindEvents(); 之后第一个 this.checkApiKey(); 前
        m = re.search(r'this\.bindEvents\(\);\n\s*this\.checkApiKey\(\);', text)
        if not m:
            raise SystemExit(f'[ERROR] sandbox/{g}: 找不到 bindEvents→checkApiKey 段')
        be_in_init = m.start()
    line_start = text.rfind('\n', 0, be_in_init) + 1
    text = text[:line_start] + install_code + "\n" + text[line_start:]

    # --- (2) showGameOver 改为 showRpgGameOver（沙盒两按钮） ---
    # 匹配："    showGameOver(" 或 "    async showGameOver(" 直到下一个方法
    sgo = re.search(r'(\s+async\s+showGameOver\([^)]*\)\s*\{[\s\S]*?\n\s*\}\n)', text)
    if not sgo:
        sgo = re.search(r'(\s+showGameOver\([^)]*\)\s*\{[\s\S]*?\n\s*\}\n)', text)
    if not sgo:
        raise SystemExit(f'[ERROR] sandbox/{g}: 找不到 showGameOver')
    # 取缩进
    ind = '    '
    new_sgo = (
        f"\n{ind}async showGameOver() {{\n"
        f"{ind}    // 沙盒版：通用胜负弹窗 + 两按钮（无下一关）\n"
        f"{ind}    return this.showRpgGameOver();\n"
        f"{ind}}}\n"
    )
    text = text[:sgo.start(1)] + new_sgo + text[sgo.end(1):]

    # --- (3) restart 函数替换 ---
    restart_fn = cfg['restart_fn']
    marker = f"async {restart_fn}() {{"
    idx = text.find(marker)
    if idx == -1:
        marker2 = f"    async {restart_fn}() {{"
        idx = text.find(marker2)
    if idx == -1:
        print(f'[WARN] sandbox/{g}: 未找到 {restart_fn}() 标记')
    else:
        brace_start = text.find('{', idx)
        depth = 0; i = brace_start; started = False
        while i < len(text):
            ch = text[i]
            if ch == '{': depth += 1; started = True
            elif ch == '}':
                depth -= 1
                if started and depth == 0: break
            i += 1
        if started and depth == 0:
            ind = '    '
            new_restart = (
                f"\n{ind}async {restart_fn}() {{\n"
                f"{ind}    // 沙盒：RPG 重玩（fallback /api/restart + 全重绘）\n"
                f"{ind}    return this.rpgRestart();\n"
                f"{ind}}}\n"
            )
            text = text[:idx] + new_restart + text[i+1:]
        else:
            print(f'[WARN] sandbox/{g}: {restart_fn}() 括号未匹配')

    # --- (4) btn-reset-configs 改为 rpgResetConfigsHandler ---
    rc = re.search(r"(\s*this\.shadowRoot\.getElementById\(['\"]btn-reset-configs['\"]\)\.addEventListener\(['\"]click['\"],\s*async\s*\([^)]*\)\s*=>\s*\{[\s\S]*?\n\s*\}\);\n)", text)
    if not rc:
        rc = re.search(r"(\s*this\.shadowRoot\.getElementById\(['\"]btn-reset-configs['\"]\)\.addEventListener\(['\"]click['\"],\s*\([^)]*\)\s*=>\s*\{[\s\S]*?\n\s*\}\);\n)", text)
    if rc:
        line_start = text.rfind('\n', 0, rc.start(1)) + 1
        ind = text[line_start:rc.start(1)]
        ind = ind[:len(ind) - len(ind.lstrip())]
        new = (
            f"\n{ind}this.shadowRoot.getElementById('btn-reset-configs').addEventListener('click', async () => {{\n"
            f"{ind}    if (!confirm('确定要重置当前沙盒所有配置为默认值吗？')) return;\n"
            f"{ind}    await this.rpgResetConfigsHandler('soft');\n"
            f"{ind}}});\n"
        )
        text = text[:rc.start(1)] + new + text[rc.end(1):]
    else:
        print(f'[WARN] sandbox/{g}: 未替换 btn-reset-configs handler')

    path.write_text(text, encoding='utf-8')
    print(f'[OK] sandbox/{g}: app.js')


def main():
    for g, cfg in GAMES.items():
        print(f"\n=== sandbox/{g} ===")
        patch_html(g)
        patch_js(g, cfg)
    print("\nSandbox patches done.")

if __name__ == '__main__':
    main()

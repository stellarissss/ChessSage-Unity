#!/usr/bin/env python3
"""为沙盒 6 棋 index.html 注入 shared_rpg.js（前置加载）。"""
import pathlib, re

ROOT = pathlib.Path('/workspace/CHessGAme/sandbox')

JS_BEFORE = "http://localhost:8080/shared/game_shared_rpg.js"

for game in ['xiangqi', 'dongwuqi', 'tiaoqi', 'wuziqi', 'weiqi', 'heibaiqi']:
    html = ROOT / game / 'static' / 'index.html'
    if not html.exists():
        print(f"[SKIP] {game}/index.html")
        continue
    t = html.read_text(encoding='utf-8')
    if 'game_shared_rpg.js' in t:
        print(f"[SKIP] {game}: already")
        continue
    # 找到第一个 <script src="/static/app.js 或 <script type="module" ... import ... app.js
    # 优先在 <script src="/static/app.js 之前插入"
    m1 = re.search(r'\n\s*<script src="(/static/app\.js[^"]*)"', t)
    if m1:
        insert_at = m1.start() + 1
        new_t = t[:insert_at] + f'\n    <script src="{JS_BEFORE}"></script>\n' + t[insert_at:]
        html.write_text(new_t, encoding='utf-8')
        print(f"[OK] {game}: injected before classic script")
        continue
    # 对 weiqi：<script type="module"> import ... app.js
    m2 = re.search(r'\n\s*<script type="module">', t)
    if m2:
        insert_at = m2.start() + 1
        new_t = t[:insert_at] + f'\n    <script src="{JS_BEFORE}"></script>\n' + t[insert_at:]
        html.write_text(new_t, encoding='utf-8')
        print(f"[OK] {game}: injected before module block")
        continue
    print(f"[WARN] {game}: 找不到插入点")

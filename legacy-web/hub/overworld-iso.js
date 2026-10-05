/* ═══════════════════════════════════════════════════════════════
   六道大陆 · 等距大地图（iso-engine 版）
   用 iso-engine 以 CSS 3D Transform 渲染整片大陆：
     · 地面以"色块"区分景观（绿/黄绿/黄/红/黑/白/灰…）
     · 水域 / 道路 / 山脉叠色
     · 依区域散布岩石 / 草丛 / 各类树木 / 雪堆 / 沙丘等景观物体
     · 全局光照渐变 + 立方体三面明暗 + 场景暗角 = 光影层次
   玩家 / POI 标点采用"DOM Overlay + 仿射投影"叠在 3D 场景之上，
   与等距底面完全对齐，且始终保持正面朝向。
   兼容 overworld-ui.js（HUD / 互动提示 / 选关 / 技能树）契约。
   ═══════════════════════════════════════════════════════════════ */
import './vendor/iso-engine/isometric-engine.js';

(function () {
    'use strict';

    var UI = null;
    var reduced = false;
    try { reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { reduced = false; }

    var REALM_NAMES = {
        hell: '地狱道', hungry: '饿鬼道', animal: '畜生道',
        human: '人道', asura: '阿修罗道', heaven: '天道'
    };

    /* ── 等距投影常量（rotateX=60°→cosX=0.5；rotateZ=45°→cosZ=√2/2）── */
    var COS_Z = Math.SQRT1_2;
    var SIN_ZX = Math.SQRT1_2 * 0.5;
    var CELL = 46;          // 每格等距边长（像素）——放大，让大陆更辽阔
    var MARGIN = 10 * CELL; // 地图四边留白
    var CHUNK = 16;         // 分块渲染：每块 N×N 格，视口外整块隐藏（大幅降低绘制开销）
    var WALL = 6;           // 边缘群山外障厚度（格），不可通行
    var EXPLORED_KEY = 'chesssage_ow_explored'; // 本地探索存档键

    var OverworldGame = {
        _levelTotals: {},

        /* ── overworld-ui.js 契约接口 ── */
        getSamsara: function () { return this.samsara; },
        getGames: function () { return this.games; },
        syncSamsara: function (state) {
            if (!state) return;
            this.samsara = state;
            this._syncBadges((state.realm_progress || {}));
            if (this._refreshBillboard) this._refreshBillboard();
            if (UI) UI.refreshHUD();
        },
        refreshSamsara: function () {
            var self = this;
            return fetch('/samsara/api/state')
                .then(function (r) { return r.json(); })
                .then(function (s) { self.syncSamsara(s); return s; })
                .catch(function () { return null; });
        },
        refreshFromUI: function () { return this.refreshSamsara(); },
        startPolling: function () {
            var self = this;
            this.pollTimer = setInterval(function () { self.refreshSamsara(); }, 8000);
        },
        stopPolling: function () {
            if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
        },
        isOpen: function () { return UI ? UI.isModalOpen() : false; },

        _fetchGamesAndState: function () {
            var self = this;
            self.games = [];
            fetch('/api/games')
                .then(function (r) { return r.json(); })
                .then(function (g) { self.games = g || []; if (UI) UI.setGames(self.games); })
                .catch(function () { });
            self.refreshSamsara();
        },

        /* ── 网格构建 ── */
        _fillRectGrid: function (grid, list) {
            if (!Array.isArray(list)) return;
            for (var i = 0; i < list.length; i++) {
                var it = list[i];
                if (!Array.isArray(it) || it[0] !== 'rect') continue;
                var x0 = it[2], y0 = it[3], x1 = it[4], y1 = it[5];
                if (x0 > x1) { var t = x0; x0 = x1; x1 = t; }
                if (y0 > y1) { var s = y0; y0 = y1; y1 = s; }
                for (var y = y0; y <= y1; y++) {
                    for (var x = x0; x <= x1; x++) {
                        if (y >= 0 && y < this.H && x >= 0 && x < this.W) grid[y][x] = true;
                    }
                }
            }
        },

        bootstrapGeometry: function (ow) {
            var self = this;
            this.W = ow.world.width;
            this.H = ow.world.height;
            this.ow = ow;

            this.regionByTile = [];
            for (var i = 0; i < this.H; i++) this.regionByTile.push(new Array(this.W));
            (ow.regions || []).forEach(function (rg) {
                var rects = rg.rects && rg.rects.length ? rg.rects : (rg.rect ? [rg.rect] : []);
                for (var k = 0; k < rects.length; k++) {
                    var r = rects[k];
                    var x0 = r[0], x1 = r[2], y0 = r[1], y1 = r[3];
                    if (x0 > x1) { var t = x0; x0 = x1; x1 = t; }
                    if (y0 > y1) { var s = y0; y0 = y1; y1 = s; }
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                            if (y >= 0 && y < self.H && x >= 0 && x < self.W) self.regionByTile[y][x] = rg;
                }
            });

            this.waterGrid = [];
            this.mountainGrid = [];
            this.roadGrid = [];
            for (var wy = 0; wy < this.H; wy++) {
                this.waterGrid.push(new Array(this.W));
                this.mountainGrid.push(new Array(this.W));
                this.roadGrid.push(new Array(this.W));
                for (var wx = 0; wx < this.W; wx++) {
                    this.waterGrid[wy][wx] = false;
                    this.mountainGrid[wy][wx] = false;
                    this.roadGrid[wy][wx] = false;
                }
            }
            this._fillRectGrid(this.waterGrid, ow.water_overlays);
            this._fillRectGrid(this.waterGrid, ow.river_snow);
            this._fillRectGrid(this.waterGrid, ow.river_ridge);
            this._fillRectGrid(this.mountainGrid, ow.mountain_overlays);
            (ow.roads || []).forEach(function (r) {
                if (r.rect) {
                    for (var i = r.rect[0]; i <= r.rect[2]; i++)
                        for (var j = r.rect[1]; j <= r.rect[3]; j++)
                            if (i >= 0 && i < self.W && j >= 0 && j < self.H) self.roadGrid[j][i] = true;
                } else if (r.x !== undefined) {
                    for (var j2 = r.y0; j2 <= r.y1; j2++)
                        if (j2 >= 0 && j2 < self.H && r.x >= 0 && r.x < self.W) self.roadGrid[j2][r.x] = true;
                } else if (r.y !== undefined) {
                    for (var i2 = r.x0; i2 <= r.x1; i2++)
                        if (i2 >= 0 && i2 < self.W && r.y >= 0 && r.y < self.H) self.roadGrid[r.y][i2] = true;
                }
            });

            this.solid = [];
            for (var yy = 0; yy < this.H; yy++) {
                var row = new Array(this.W);
                for (var xx = 0; xx < this.W; xx++)
                    row[xx] = !!(this.waterGrid[yy][xx] || this.mountainGrid[yy][xx]);
                this.solid.push(row);
            }
            (ow.solid_regions || []).forEach(function (s) {
                var r = s.rect;
                for (var j3 = r[1]; j3 <= r[3]; j3++)
                    for (var i3 = r[0]; i3 <= r[2]; i3++)
                        if (j3 >= 0 && j3 < self.H && i3 >= 0 && i3 < self.W) self.solid[j3][i3] = true;
            });

            /* ── 边缘群山外障：环绕大陆一圈，标记为不可通行（玩家看不到地图之外） ── */
            this.wallGrid = [];
            for (var wy2 = 0; wy2 < this.H; wy2++) {
                var wrow = new Array(this.W);
                for (var wx2 = 0; wx2 < this.W; wx2++) {
                    var inWall = wx2 < WALL || wx2 >= this.W - WALL || wy2 < WALL || wy2 >= this.H - WALL;
                    // 外障包围整片大陆：边缘格全部为山区（并阻断通行）
                    if (inWall) {
                        wrow[wx2] = true;
                        this.solid[wy2][wx2] = true;
                    } else {
                        wrow[wx2] = false;
                    }
                }
                this.wallGrid.push(wrow);
            }

            /* ── 调色板：绿 / 黄绿 / 黄 / 红 / 黑 / 白 / 灰 …… ── */
            this.C_WATER = '#1E6A96';
            this.C_ROAD = '#C9B37E';
            this.C_BASE = '#5f9e4e';
            this.REG_COLOR = {
                nw_forest:  '#2e6e3b',  /* 深绿 · 密林 */
                n_snow:     '#dce6ef',  /* 白 · 雪原 */
                ne_pasture: '#8fbf54',  /* 黄绿 · 牧场 */
                w_waste:    '#b8a06a',  /* 黄褐 · 荒原 */
                c_plain:    '#6ca659',  /* 绿 · 平原 */
                east_ridge: '#7d9384',  /* 灰绿 · 丘陵 */
                sw_dungeon: '#241c30',  /* 黑 · 地牢 */
                s_desert:   '#d8b25a',  /* 黄 · 沙漠 */
                se_battle:  '#a4443f'   /* 红 · 战场 */
            };
        },

        _colorAt: function (x, y) {
            if (this.waterGrid[y][x]) return this.C_WATER;
            if (this.roadGrid[y][x]) return this.C_ROAD;
            var rg = this.regionByTile[y] ? this.regionByTile[y][x] : null;
            if (rg) return this.REG_COLOR[rg.id] || this.C_BASE;
            return this.C_BASE;
        },

        /* 山地屏障：仅地图边缘的外障不可通行，水/障碍物均可自由穿越 */
        _isBarrier: function (tx, ty) {
            if (tx < 0 || ty < 0 || tx >= this.W || ty >= this.H) return true;
            return !!(this.wallGrid && this.wallGrid[ty] && this.wallGrid[ty][tx]);
        },

        isSolid: function (tx, ty) {
            if (tx < 0 || ty < 0 || tx >= this.W || ty >= this.H) return true;
            return this.solid[ty][tx];
        },

        /* ── 元素工厂（写入分块容器，视口外整块隐藏以大幅降载）── */
        _chunkFor: function (tx, ty) {
            if (tx < 0) tx = 0; if (ty < 0) ty = 0;
            var cx = (tx / CHUNK) | 0;
            var cy = (ty / CHUNK) | 0;
            var key = cy * 1000 + cx;
            var c = this.chunks[key];
            if (!c) {
                c = document.createElement('div');
                c.className = 'ow-chunk';
                c.style.position = 'absolute';
                c.style.left = '0'; c.style.top = '0';
                c.style.width = '0'; c.style.height = '0';
                c.style.transformStyle = 'preserve-3d';
                c.style.pointerEvents = 'none';
                this.sceneEl.appendChild(c);
                c.setAttribute('data-cx', cx); c.setAttribute('data-cy', cy);
                this.chunks[key] = c;
                this.chunkList.push(c);
            }
            return c;
        },

        _plane: function (x, y, w, h, color, z, chunkable) {
            var p = document.createElement('iso-plane');
            p.setAttribute('no-pointer', '');
            p.setAttribute('x', String(x));
            p.setAttribute('y', String(y));
            p.setAttribute('z', String(z || 0));
            p.setAttribute('width', String(w));
            p.setAttribute('height', String(h));
            p.setAttribute('color', color);
            if (chunkable !== false) {
                var tx = Math.round(x / CELL - 0.5);
                var ty = Math.round(y / CELL - 0.5);
                this._chunkFor(tx, ty).appendChild(p);
            } else {
                this.sceneEl.appendChild(p);
            }
            return p;
        },

        _cube: function (x, y, w, h, depth, top, front, right, z, chunkable) {
            var c = document.createElement('iso-cube');
            c.setAttribute('no-pointer', '');
            c.setAttribute('x', String(x));
            c.setAttribute('y', String(y));
            c.setAttribute('z', String(z || 0));
            c.setAttribute('width', String(w));
            c.setAttribute('height', String(h));
            c.setAttribute('depth', String(depth));
            c.setAttribute('top-color', top);
            c.setAttribute('front-color', front);
            c.setAttribute('right-color', right);
            if (chunkable !== false) {
                var tx = Math.round(x / CELL);
                var ty = Math.round(y / CELL);
                this._chunkFor(tx, ty).appendChild(c);
            } else {
                this.sceneEl.appendChild(c);
            }
            return c;
        },

        /* ── 仿射投影：格中心 → iso-scene 屏幕坐标 ── */
        _project: function (gx, gy) {
            var ix = gx * CELL, iy = gy * CELL;
            return {
                x: this.origin.x + (ix - iy) * COS_Z,
                y: this.origin.y + (ix + iy) * SIN_ZX
            };
        },

        /* ── 构建地面色块（逐行合并同色连续段，减少元素数） ── */
        buildGround: function () {
            for (var y = 0; y < this.H; y++) {
                var x = 0;
                while (x < this.W) {
                    var color = this._colorAt(x, y);
                    var x0 = x;
                    x++;
                    while (x < this.W && this._colorAt(x, y) === color) x++;
                    var len = x - x0;
                    /* 略微外扩避免相邻色块在 3D 变换下产生黑色缝隙/线条。
                       地面始终常显（分块隐藏会导致大片色块出现空洞）。 */
                    var over = 3;
                    this._plane((x0 + len / 2) * CELL, (y + 0.5) * CELL,
                        len * CELL + over, CELL + over, color, 0, false);
                }
            }
        },

        _rng: function (x, y, salt) {
            var h = (x * 374761393 + y * 668265263 + salt * 2246822519) | 0;
            h = (h ^ (h >>> 13)) * 1274126177;
            h = h ^ (h >>> 16);
            return (h >>> 0) / 4294967295;
        },

        _regionIdAt: function (x, y) {
            var rg = this.regionByTile[y] && this.regionByTile[y][x];
            return rg ? rg.id : null;
        },

        /* ── 自然边界：沿区域分界散落灌木/岩石，把生硬的直线化为有机过渡 ── */
        buildBoundaries: function () {
            for (var y = 0; y < this.H; y++) {
                for (var x = 0; x < this.W; x++) {
                    if (this.wallGrid[y][x] || this.waterGrid[y][x] || this.mountainGrid[y][x]) continue;
                    var rid = this._regionIdAt(x, y);
                    if (!rid) continue;
                    /* 仅探查紧邻的一欧式邻居是否属于不同景观 */
                    var diff = false;
                    if (x > 0 && this._regionIdAt(x - 1, y) !== rid) diff = true;
                    else if (x < this.W - 1 && this._regionIdAt(x + 1, y) !== rid) diff = true;
                    else if (y > 0 && this._regionIdAt(x, y - 1) !== rid) diff = true;
                    else if (y < this.H - 1 && this._regionIdAt(x, y + 1) !== rid) diff = true;
                    if (!diff) continue;
                    /* 在边界带上以摇散概率落灌木/草/石，密度向内随机淡出 */
                    var prob = this._rng(x, y, 21);
                    if (prob > (0.6 - this._rng(x, y, 22) * 0.35)) continue;
                    var kind = 'grass';
                    var r2 = this._rng(x, y, 23);
                    if (r2 < 0.3) kind = 'rock';
                    else if (r2 < 0.5) kind = 'tree';
                    this._placeObject(kind, x + 0.5, y + 0.5, this._rng(x, y, 24));
                }
            }
        },

        /* ── 景观物体（散点，控制密度）── */
        buildObjects: function () {
            var density = 0.05;
            var kindsBy = {
                nw_forest:  ['pine', 'tree', 'grass', 'rock'],
                n_snow:     ['snow', 'pine', 'snow', 'rock'],
                ne_pasture: ['grass', 'tree', 'grass', 'rock'],
                w_waste:    ['dead', 'rock', 'dune'],
                c_plain:    ['tree', 'grass', 'rock', 'tree'],
                east_ridge: ['rock', 'pine', 'rock'],
                sw_dungeon: ['ruin', 'rock', 'dead'],
                s_desert:   ['cacti', 'dune', 'rock'],
                se_battle:  ['dead', 'rock', 'ruin']
            };
            for (var y = 0; y < this.H; y++) {
                for (var x = 0; x < this.W; x++) {
                    if (this.waterGrid[y][x] || this.mountainGrid[y][x] || this.solid[y][x]) continue;
                    var rid = this._regionIdAt(x, y);
                    var kinds = kindsBy[rid];
                    if (!kinds) continue;
                    if (this._rng(x, y, 1) > density) continue;
                    var kind = kinds[Math.floor(this._rng(x, y, 2) * kinds.length)];
                    var gx = x + 0.5, gy = y + 0.5;
                    this._placeObject(kind, gx, gy, this._rng(x, y, 3));
                }
            }
        },

        _placeObject: function (kind, gx, gy, s) {
            var CELL = this.CELL;
            var cx = gx * CELL, cy = gy * CELL;
            var scale = 0.6 + s * 0.7;
            switch (kind) {
                case 'tree':
                case 'pine': {
                    var trunkW = CELL * 0.22;
                    this._cube(cx, cy, trunkW, trunkW, CELL * 0.30, '#6b4a2f', '#5a3d26', '#49321e', 0);
                    this._cube(cx, cy, CELL * 0.6 * scale, CELL * 0.6 * scale, CELL * 0.6 * scale,
                        kind === 'tree' ? '#4a8f3a' : '#2f6b3a',
                        kind === 'tree' ? '#3a7430' : '#25592f',
                        kind === 'tree' ? '#2c5c24' : '#1a4422', CELL * 0.28);
                    break;
                }
                case 'snow': {
                    var w = CELL * 0.5 * scale, d = CELL * 0.38 * scale;
                    this._cube(cx, cy, w, w, d, '#ffffff', '#dfe9f2', '#b8ccdc', 0);
                    if (this._rng(Math.round(gx * 10), Math.round(gy * 10), 9) < 0.4)
                        this._cube(cx + CELL * 0.12, cy + CELL * 0.12, w * 0.7, w * 0.7, d * 0.6, '#ffffff', '#dfe9f2', '#b8ccdc', d * 0.4);
                    break;
                }
                case 'rock':
                case 'ruin': {
                    var rw = CELL * 0.5 * scale, rd = CELL * 0.4 * scale;
                    this._cube(cx, cy, rw, rw, rd, kind === 'ruin' ? '#7b6f64' : '#9a948c',
                        kind === 'ruin' ? '#5f554c' : '#7e7870',
                        kind === 'ruin' ? '#443d36' : '#615c55', 0);
                    if (this._rng(Math.round(gx * 10), Math.round(gy * 10), 7) < 0.45)
                        this._cube(cx + CELL * 0.28, cy + CELL * 0.16, rw * 0.6, rw * 0.6, rd * 0.7,
                            kind === 'ruin' ? '#7b6f64' : '#9a948c',
                            kind === 'ruin' ? '#5f554c' : '#7e7870',
                            kind === 'ruin' ? '#443d36' : '#615c55', rd * 0.5);
                    break;
                }
                case 'grass': {
                    var gd = CELL * 0.18 * scale;
                    this._cube(cx, cy, CELL * 0.5 * scale, CELL * 0.5 * scale, gd, '#7fc04e', '#66a53e', '#4f8330', 0);
                    break;
                }
                case 'dead': {
                    var dd = CELL * 0.14 * scale;
                    this._cube(cx, cy, CELL * 0.5 * scale, CELL * 0.5 * scale, dd, '#9b8b5a', '#7d7047', '#5f5636', 0);
                    break;
                }
                case 'cacti': {
                    this._cube(cx, cy, CELL * 0.26, CELL * 0.26, CELL * 0.9 * scale, '#5a9040', '#4a7a35', '#3a6429', 0);
                    this._cube(cx + CELL * 0.18, cy - CELL * 0.1, CELL * 0.34, CELL * 0.2, CELL * 0.16 * scale, '#5a9040', '#4a7a35', '#3a6429', CELL * 0.5 * scale);
                    break;
                }
                case 'dune': {
                    this._cube(cx, cy, CELL * 0.55 * scale, CELL * 0.55 * scale, CELL * 0.22 * scale, '#e0bb6a', '#c8a052', '#a98a40', 0);
                    break;
                }
            }
        },

        /* ── 沙盒训练场：聚合多面体（CSS 3D）──
           阶梯塔式演武平台，矗立四柱，顶缀金灯。沙漠色调呼应纯净棋境。 */
        _placeSandboxStructure: function (gx, gy) {
            var CELL = this.CELL;
            var cx = gx * CELL, cy = gy * CELL;

            /* 地面金光涟漪（decal） */
            this._plane(cx, cy, CELL * 3.4, CELL * 3.4, 'rgba(212,175,55,0.18)', 1);

            /* 基座平台：沙岩 */
            var pw = CELL * 2.8, pd = CELL * 0.3;
            this._cube(cx, cy, pw, pw, pd, '#d8b25a', '#c8a052', '#a98a40', 0);

            /* 四角立柱 */
            var pill = 0.28;
            var offs = CELL * 1.2;
            var corners = [
                [cx - offs, cy - offs], [cx + offs, cy - offs],
                [cx - offs, cy + offs], [cx + offs, cy + offs]
            ];
            var self = this;
            corners.forEach(function (c) {
                self._cube(c[0], c[1], CELL * pill, CELL * pill, CELL * 1.35,
                    '#8a5a2b', '#75491f', '#5f3918', pd);
            });

            /* 中央阶梯塔（下 上 中 三层，逐级收拢升顶） */
            var z = pd;
            var l1 = CELL * 1.7, h1 = CELL * 0.42;
            this._cube(cx, cy, l1, l1, h1, '#d7ccc8', '#bcaaa4', '#a1887f', z); z += h1;
            var l2 = CELL * 1.2, h2 = CELL * 0.4;
            this._cube(cx, cy, l2, l2, h2, '#d7ccc8', '#bcaaa4', '#a1887f', z); z += h2;
            var l3 = CELL * 0.72, h3 = CELL * 0.4;
            this._cube(cx, cy, l3, l3, h3, '#d9c9a7', '#bda77f', '#9a875f', z); z += h3;

            /* 顶灯：金珠小塔 */
            this._cube(cx, cy, CELL * 0.34, CELL * 0.34, CELL * 0.34,
                '#d4af37', '#b38e2c', '#8f6f24', z);
            /* 立柱顶部金珠 */
            corners.forEach(function (c) {
                self._cube(c[0], c[1], CELL * 0.2, CELL * 0.2, CELL * 0.2,
                    '#d4af37', '#b38e2c', '#8f6f24', pd + CELL * 1.35);
            });
        },

        /* ── 山脉叠岩（含环绕大陆的群山外障） ── */
        buildMountains: function () {
            for (var y = 0; y < this.H; y++) {
                for (var x = 0; x < this.W; x++) {
                    if (this.wallGrid && this.wallGrid[y] && this.wallGrid[y][x]) {
                        /* 外障：密布高大岩石，形成不可逾越的山墙 */
                        if (this._rng(x, y, 5) > 0.82) continue;
                        var wx0 = (x + 0.5) * this.CELL, wy0 = (y + 0.5) * this.CELL;
                        var wd = this.CELL * (0.8 + this._rng(x, y, 6) * 1.3);
                        this._cube(wx0, wy0, this.CELL * 0.85, this.CELL * 0.85, wd, '#6f6a62', '#575249', '#3e3a34', 0);
                        if (this._rng(x, y, 8) < 0.4)
                            this._cube(wx0 + this.CELL * 0.2, wy0 - this.CELL * 0.2, this.CELL * 0.5, this.CELL * 0.5, wd * 0.7, '#7a756c', '#5f5a50', '#45403a', wd * 0.5);
                        continue;
                    }
                    if (!this.mountainGrid[y][x]) continue;
                    if (this._rng(x, y, 5) > 0.55) continue;
                    var cx = (x + 0.5) * this.CELL, cy = (y + 0.5) * this.CELL;
                    var d = this.CELL * (0.55 + this._rng(x, y, 6) * 0.9);
                    this._cube(cx, cy, this.CELL * 0.62, this.CELL * 0.62, d, '#8f8a82', '#726d65', '#57524a', 0);
                }
            }
        },

        /* ── 由地形网格计算地图包围盒（iso-scene 坐标）── */
        _terrainRect: function () {
            var o = this.origin;
            /* 地面平面角度（z=0）下九宫旋转后的四角 */
            var pts = [
                { ix: 0, iy: 0 }, { ix: this.W * this.CELL, iy: 0 },
                { ix: 0, iy: this.H * this.CELL }, { ix: this.W * this.CELL, iy: this.H * this.CELL }
            ];
            var l = 1e9, t = 1e9, r = -1e9, b = -1e9;
            pts.forEach(function (p) {
                var x = o.x + (p.ix - p.iy) * COS_Z;
                var y = o.y + (p.ix + p.iy) * SIN_ZX;
                if (x < l) l = x; if (x > r) r = x;
                if (y < t) t = y; if (y > b) b = y;
            });
            return { l: l - this.CELL, t: t - this.CELL, r: r + this.CELL, b: b + this.CELL * 3,
                     cx: (l + r) / 2, cy: (t + b) / 2 };
        },

        /* ── 3D 场景装配 ── */
        buildScene: function () {
            var vp = document.getElementById('iso-viewport');
            var stage = document.getElementById('iso-stage');
            var sceneEl = document.createElement('iso-scene');
            sceneEl.setAttribute('perspective', '0');

            this.CELL = CELL;
            var minSx = -83 * this.CELL * COS_Z;
            var maxSx = 111 * this.CELL * COS_Z;
            var maxSy = (111 + 83) * this.CELL * SIN_ZX;
            var Wp = (maxSx - minSx) + MARGIN * 2;
            var Hp = maxSy + MARGIN * 2 + this.CELL * 2;

            this.origin = { x: MARGIN - minSx, y: MARGIN };
            sceneEl.setAttribute('origin-x', String(this.origin.x));
            sceneEl.setAttribute('origin-y', String(this.origin.y));
            sceneEl.setAttribute('width', String(Math.max(400, Wp)));
            sceneEl.setAttribute('height', String(Math.max(320, Hp)));
            sceneEl.style.width = Wp + 'px';
            sceneEl.style.height = Hp + 'px';
            sceneEl.id = 'iso-scene-el';

            stage.style.width = Wp + 'px';
            stage.style.height = Hp + 'px';
            stage.innerHTML = '';
            sceneEl.setAttribute('no-pointer', '');
            stage.appendChild(sceneEl);

            /* 标点图层（与 iso-scene 同坐标原点，平叠其上） */
            var overlay = document.createElement('div');
            overlay.id = 'iso-overlay';
            overlay.style.width = Wp + 'px';
            overlay.style.height = Hp + 'px';
            stage.appendChild(overlay);

            if (vp && vp.firstElementChild !== stage) {
                while (vp.firstChild) vp.removeChild(vp.firstChild);
                vp.appendChild(stage);
            }

            this.sceneEl = sceneEl;
            this.stageEl = stage;
            this.overlayEl = overlay;
            this.containerW = Math.ceil(Wp);
            this.containerH = Math.ceil(Hp);

            this.chunks = {};
            this.chunkList = [];
            this._culledKey = '';

            this.buildGround();
            this.buildBoundaries();
            this.buildObjects();
            this.buildMountains();
            this.buildPois();
            this.buildPlayer();
            this.buildClouds();
        },

        /* ── 大陆周围点缀云朵：填满外圈黑色区域的观感 ── */
        buildClouds: function () {
            var self = this;
            if (!this.overlayEl) return;
            var r;
            try { r = this._terrainRect(); } catch (e) { return; }
            var spots = [];
            var add = function (x, y, w, spd) { spots.push({ x: x, y: y, w: w, spd: spd }); };
            var i;
            /* 左右两侧 */
            for (i = 0; i < 7; i++) {
                add(r.l - (70 + this._rng(3, i, 1) * 150), r.t + this._rng(4, i, 2) * (r.b - r.t), 150 + this._rng(5, i, 3) * 130, 14 + (i % 4) * 5);
                add(r.r + (70 + this._rng(6, i, 1) * 150), r.t + this._rng(7, i, 2) * (r.b - r.t), 150 + this._rng(8, i, 3) * 130, 12 + (i % 3) * 6);
            }
            /* 上下两侧 */
            for (i = 0; i < 5; i++) {
                add(r.l + this._rng(9, i, 1) * (r.r - r.l), r.t - (55 + this._rng(10, i, 2) * 110), 160 + this._rng(11, i, 3) * 120, 16 + (i % 4) * 4);
                add(r.l + this._rng(12, i, 1) * (r.r - r.l), r.b + (55 + this._rng(13, i, 2) * 110), 160 + this._rng(14, i, 3) * 120, 13 + (i % 3) * 5);
            }
            spots.forEach(function (c) {
                var d = document.createElement('div');
                d.className = 'ow-cloud';
                d.style.width = c.w + 'px';
                d.style.height = (c.w * 0.42).toFixed(1) + 'px';
                d.style.left = c.x + 'px';
                d.style.top = c.y + 'px';
                var delay = (c.spd * 1.08) % 12;
                d.style.animationDuration = c.spd + 's';
                d.style.animationDelay = delay.toFixed(1) + 's';
                self.overlayEl.appendChild(d);
            });
        },

        /* ── 巨型告示牌：轮回修行立牌（CSS 渲染，随地图缩放移动） ── */
        _buildBillboard: function (px, py, p) {
            var bb = document.createElement('div');
            bb.className = 'ow-billboard glow' + (p.active ? ' active' : '');
            bb.id = 'billboard-' + (p.id || 'billboard');
            bb.setAttribute('data-poi', p.id || '');
            bb.innerHTML =
                '<div class="bb-post">' +
                '<div class="bb-title">轮回修行</div>' +
                '<div class="bb-sub">SAMSARA</div>' +
                '<div class="bb-line"></div>' +
                '<div class="bb-rows" id="bb-rows-' + (p.id || 'billboard') + '">' +
                '<div class="bb-row"><span>悟道</span><b id="bb-enlight">—</b></div>' +
                '<div class="bb-row"><span>堕落</span><b id="bb-corrupt">—</b></div>' +
                '<div class="bb-row"><span>祈求</span><b id="bb-prayer">—</b></div>' +
                '<div class="bb-row"><span>记忆碎片</span><b id="bb-frags">—</b></div>' +
                '</div>' +
                '<div class="bb-line"></div>' +
                '<div class="bb-legend">靠近按 [E] 查看详情</div>' +
                '</div>' +
                '<div class="bb-pole"></div>' +
                '<div class="bb-base"></div>';
            var SCALE = (this.CELL || CELL);
            bb.style.left = (px - SCALE * 2.05) + 'px';
            bb.style.top = (py - SCALE * 3.2) + 'px';
            this.overlayEl.appendChild(bb);
            (this.billboards = this.billboards || []).push({
                id: p.id || 'billboard', el: bb,
                enlight: bb.querySelector('#bb-enlight'),
                corrupt: bb.querySelector('#bb-corrupt'),
                prayer: bb.querySelector('#bb-prayer'),
                frags: bb.querySelector('#bb-frags')
            });
            this._refreshBillboard();
            return bb;
        },

        _refreshBillboard: function () {
            if (!this.billboards) return;
            var s = this.samsara || {};
            var ra = s.alignment || {};
            var frags = s.memory_fragments || {};
            this.billboards.forEach(function (b) {
                if (b.enlight) b.enlight.textContent = ra.enlightenment || 0;
                if (b.corrupt) b.corrupt.textContent = ra.corruption || 0;
                if (b.prayer) b.prayer.textContent = s.prayer_count || 0;
                if (b.frags) b.frags.textContent = ((frags.unlocked_count || 0) + ' / ' + (frags.total || 6));
            });
            /* 若游戏状态未含修行字段，则从 Rpg 总览接口补齐一次 */
            if (!s._rpgLoaded) {
                s._rpgLoaded = true;
                var self = this;
                fetch('/samsara/story/api/rpg/overview', { cache: 'no-store' })
                    .then(function (r) { return r.json(); })
                    .then(function (d) {
                        if (!d) return;
                        self.samsara = self.samsara || {};
                        var a = (self.samsara.alignment = d.alignment || {});
                        self.samsara.prayer_count = d.prayer_count || 0;
                        self.samsara.memory_fragments = d.memory_fragments || {};
                        (self.billboards || []).forEach(function (b) {
                            if (b.enlight) b.enlight.textContent = a.enlightenment || 0;
                            if (b.corrupt) b.corrupt.textContent = a.corruption || 0;
                            if (b.prayer) b.prayer.textContent = d.prayer_count || 0;
                            if (b.frags) {
                                var fr = d.memory_fragments || {};
                                b.frags.textContent = ((fr.unlocked_count || 0) + ' / ' + (fr.total || 6));
                            }
                        });
                    })
                    .catch(function () { /* 保持占位 */ });
            }
        },

        /* ── POI 标点（光环=地面 decal，文字/emoji=叠加层）── */
        buildPois: function () {
            var self = this;
            this.pois = [];
            (this.ow.pois || []).forEach(function (p) {
                var pt = self._project(p.x + 0.5, p.y + 0.5);
                if (p.type === 'spawn') {
                    self._plane((p.x + 0.5) * CELL, (p.y + 0.5) * CELL, CELL * 1.7, CELL * 1.7,
                        'rgba(212,175,55,0.32)', 1);
                    return;
                }
                /* 巨型告示牌：轮回修行数据立牌（CSS 渲染，靠近按 E） */
                if (p.type === 'billboard') {
                    self._plane((p.x + 0.5) * CELL, (p.y + 0.5) * CELL, CELL * 4.4, CELL * 4.4,
                        'rgba(212,175,55,0.22)', 1, false);
                    var bbEl = self._buildBillboard(pt.x, pt.y, p);
                    self.pois.push({ poi: p, x: pt.x, y: pt.y, emoji: null, billboard: true, el: bbEl });
                    return;
                }
                /* 成就纪念碑：常驻立牌，靠近按 E */
                if (p.type === 'achievements') {
                    self._plane((p.x + 0.5) * CELL, (p.y + 0.5) * CELL, CELL * 3.0, CELL * 3.0,
                        'rgba(255,215,0,0.22)', 1, false);
                }
                if (p.type === 'sandbox') {
                    self._placeSandboxStructure(p.x + 0.5, p.y + 0.5);
                }
                var spine = p.type === 'realm' ? self._realmSpine(p.realm)
                    : (p.type === 'sandbox' ? '#d4af37'
                    : (p.type === 'achievements' ? '#ffd700' : '#4ecdc4'));
                /* 地面光环（3D decal，平铺地面上） */
                self._plane((p.x + 0.5) * CELL, (p.y + 0.5) * CELL, CELL * 2.6, CELL * 2.6,
                    hexA(spine, 0.30), 1);
                if (p.type === 'realm' || p.type === 'sandbox')
                    self._plane((p.x + 0.5) * CELL, (p.y + 0.5) * CELL, CELL * 1.4, CELL * 1.4,
                        hexA(spine, 0.4), 3.2);

                /* emoji 立牌（叠加层） */
                var emoji = document.createElement('div');
                emoji.className = 'poi-emoji' + (p.type === 'realm' ? ' realm' : ' npc');
                emoji.style.fontSize = (CELL * 1.15) + 'px';
                emoji.style.lineHeight = '1';
                emoji.style.left = (pt.x - CELL * 0.9) + 'px';
                emoji.style.top = (pt.y - CELL * 1.35) + 'px';
                emoji.style.width = (CELL * 1.8) + 'px';
                emoji.style.height = (CELL * 1.8) + 'px';
                emoji.textContent = p.emoji;
                emoji.setAttribute('data-poi', p.id || '');
                self.overlayEl.appendChild(emoji);

                var entry = { poi: p, x: pt.x, y: pt.y, emoji: emoji };
                if (p.type === 'realm') {
                    var badge = document.createElement('div');
                    badge.className = 'realm-badge';
                    var bt = document.createElement('span');
                    bt.textContent = '—';
                    badge.appendChild(bt);
                    badge.style.left = (pt.x - CELL * 0.9) + 'px';
                    badge.style.top = (pt.y - CELL * 2.05) + 'px';
                    badge.style.width = (CELL * 1.8) + 'px';
                    self.overlayEl.appendChild(badge);
                    entry.badge = badge;
                    (self.badgePool = self.badgePool || []).push({
                        realm: p.realm, el: badge, text: bt, done: false, sandbox: false
                    });
                }
                self.pois.push(entry);
            });
            this._syncBadges((this.samsara || {}).realm_progress || {});
        },

        _realmSpine: function (realm) {
            switch (realm) {
                case 'heaven': return '#fff9c5';
                case 'human':  return '#a8e6cf';
                case 'animal': return '#ffd3b6';
                case 'asura':  return '#ff6b6b';
                case 'hungry': return '#795548';
                case 'hell':   return '#7d3b86';
                default:       return '#d4af37';
            }
        },

        _syncBadges: function (realmProgress) {
            var self = this;
            var s = this.samsara || {};
            var unlocked = s.sandbox_unlocked || [];
            (this.badgePool || []).forEach(function (b) {
                var rp = (realmProgress && realmProgress[b.realm]) || {};
                var total = self._levelTotals && self._levelTotals[b.realm] !== undefined ? self._levelTotals[b.realm] : 5;
                var passed = rp.levels_passed || 0;
                b.done = !!rp.completed;
                b.sandbox = unlocked.indexOf(b.realm) >= 0;
                b.text.textContent = b.done ? ('✓ 已通关' + (b.sandbox ? ' 🔒' : '')) : (passed + ' / ' + total);
                b.text.style.color = b.done ? '#0a9396' : '#f4c542';
                b.el.classList.toggle('realm-done', !!b.done);
                /* 叠加棋类定位标签（主推 / 不推荐的测试） */
                var t = (window.OverworldUI && window.OverworldUI.realmTag) ? window.OverworldUI.realmTag(b.realm) : null;
                if (!b.tagEl) { b.tagEl = document.createElement('span'); b.tagEl.className = 'rec-tag'; b.el.appendChild(b.tagEl); }
                if (t) { b.tagEl.textContent = t.text; b.tagEl.className = 'rec-tag ' + t.cls; b.tagEl.style.display = ''; }
                else { b.tagEl.textContent = ''; b.tagEl.style.display = 'none'; }
            }, this);
        },

        /* ── 玩家 ── */
        buildPlayer: function () {
            var init = (this.ow.player && this.ow.player.initial) || { x: 58, y: 46 };
            this.playerPos = { x: init.x + 0.5, y: init.y + 0.5 };
            this.facing = 1;
            this.moving = false;
            this.walkPhase = 0;
            this.closestPoi = null;
            this.playerSpeed = ((this.ow.player && this.ow.player.speed) || 160) / 5;  // 移速降到 1/5

            var pd = document.createElement('div');
            pd.className = 'player-marker';
            pd.innerHTML = '<span class="player-shade"></span><span class="player-avatar">☯</span>';
            this.overlayEl.appendChild(pd);
            this.playerEl = pd;
            this.updatePlayerMarker(true);
        },

        updatePlayerMarker: function (fast) {
            var pt = this._project(this.playerPos.x, this.playerPos.y);
            var el = this.playerEl;
            el.style.left = (pt.x - CELL * 0.8) + 'px';
            el.style.top = (pt.y - CELL * 1.15) + 'px';
            var av = el.querySelector('.player-avatar');
            if (av) {
                if (this.facing < 0) av.style.transform = 'scaleX(-1)';
                if (this.moving) { this.walkPhase += 0.6; }
                av.style.marginTop = (this.moving ? Math.abs(Math.sin(this.walkPhase)) * -6 : 0) + 'px';
            }
            this._refreshMinimapPlayer();
        },

        /* ── 小地图：生成大地图缩略图 + 玩家/入口标点 ── */
        _minimapColor: function (x, y) {
            if (this.waterGrid[y] && this.waterGrid[y][x]) return hexToRGB(this.C_WATER);
            if (this.roadGrid[y] && this.roadGrid[y][x]) return hexToRGB(this.C_ROAD);
            var rg = this.regionByTile[y] ? this.regionByTile[y][x] : null;
            var col = rg ? (this.REG_COLOR[rg.id] || this.C_BASE) : this.C_BASE;
            return hexToRGB(col);
        },

        initMinimap: function () {
            var c = document.getElementById('minimap-base');
            if (!c || !this.W) return;
            var W = this.W, H = this.H;
            c.width = W; c.height = H;
            var ctx = c.getContext('2d');
            var img = ctx.createImageData(W, H);
            var d = img.data;
            for (var y = 0; y < H; y++) {
                for (var x = 0; x < W; x++) {
                    var col = this._minimapColor(x, y);
                    var i = (y * W + x) * 4;
                    d[i] = col[0]; d[i + 1] = col[1]; d[i + 2] = col[2]; d[i + 3] = 255;
                }
            }
            ctx.putImageData(img, 0, 0);

            /* 各入口标点：未探索 ❓ / 已探索 对应 emoji */
            var poisEl = document.getElementById('minimap-pois');
            if (!poisEl) return;
            poisEl.innerHTML = '';
            this.minimapPois = {};
            var self = this;
            (this.pois || []).forEach(function (entry) {
                var p = entry.poi;
                if (p.type === 'spawn') return;
                var div = document.createElement('div');
                var isSandbox = p.type === 'sandbox';
                div.className = 'minimap-poi' + (isSandbox ? ' sandbox' : '');
                var left = (p.x + 0.5) / W * 100;
                var top = (p.y + 0.5) / H * 100;
                div.style.left = left + '%';
                div.style.top = top + '%';
                if (p.label) div.setAttribute('title', p.label);
                poisEl.appendChild(div);
                self.minimapPois[p.id] = { div: div, base: p.emoji };
                if (isSandbox || self._isExplored(p.id)) {
                    div.classList.add('explored');
                    div.textContent = p.emoji;
                } else {
                    div.classList.add('unexplored');
                    div.textContent = '❓';
                }
            });
            this._refreshMinimapPlayer();
        },

        _refreshMinimapPlayer: function () {
            var p = document.getElementById('minimap-player');
            if (!p || !this.playerPos || !this.W) return;
            p.style.left = (this.playerPos.x / this.W * 100) + '%';
            p.style.top = (this.playerPos.y / this.H * 100) + '%';
        },

        /* ── 探索状态：本地存档持久化 ── */
        _isExplored: function (poiId) {
            try {
                var raw = localStorage.getItem(EXPLORED_KEY);
                var arr = raw ? JSON.parse(raw) : [];
                return arr.indexOf(poiId) >= 0;
            } catch (e) { return false; }
        },

        _markExplored: function (poiId) {
            if (!poiId) return;
            try {
                var raw = localStorage.getItem(EXPLORED_KEY);
                var arr = raw ? JSON.parse(raw) : [];
                if (arr.indexOf(poiId) < 0) {
                    arr.push(poiId);
                    localStorage.setItem(EXPLORED_KEY, JSON.stringify(arr));
                }
            } catch (e) { /* 忽略存档写入失败 */ }
            var rec = this.minimapPois && this.minimapPois[poiId];
            if (rec) {
                rec.div.classList.add('explored');
                rec.div.classList.remove('unexplored');
                rec.div.textContent = rec.base;
            }
        },

        /* ── 相机 ── */
        setupCamera: function () {
            var oldTz;
            var c = this.cam = {
                zoom: 0.6, vw: window.innerWidth, vh: window.innerHeight,
                tx: 0, ty: 0, ttx: 0, tty: 0, tz: 0.6
            };
            var vp = document.getElementById('iso-viewport');
            if (vp) { c.vw = vp.clientWidth; c.vh = vp.clientHeight; }
            this._onResize = (function () {
                var cc = this.cam;
                cc.vw = document.getElementById('iso-viewport').clientWidth;
                cc.vh = document.getElementById('iso-viewport').clientHeight;
                this._clampCam();
                this.applyCamera(true);
                this._updateCulling(true);
            }).bind(this);
            window.addEventListener('resize', this._onResize);
            /* 初始化：保持当前视角（由 start() 的 centerOn 接管，无需全景 fit） */
            c.zoom = 0.6; c.tz = 0.6; c.tx = 0; c.ty = 0; c.ttx = 0; c.tty = 0;
        },

        cameraFit: function () {
            var c = this.cam;
            var r = this._terrainRect();
            var pad = 70;
            var w = r.r - r.l, h = r.b - r.t;
            var zoom = Math.min((c.vw - pad * 2) / w, (c.vh - pad * 2) / h);
            zoom = Math.max(0.1, Math.min(zoom, 1.1));
            c.tz = zoom; c.zoom = zoom;
            c.ttx = c.vw / 2 - r.cx * c.zoom;
            c.tty = c.vh / 2 - r.cy * c.zoom;
            c.tx = c.ttx; c.ty = c.tty;
            this.applyCamera(true);
        },

        applyCamera: function (instant) {
            var c = this.cam;
            var stage = this.stageEl;
            if (!stage) return;
            var tx = instant ? c.ttx : c.tx;
            var ty = instant ? c.tty : c.ty;
            var z = instant ? c.zoom : c.zoom;
            stage.style.transformOrigin = '0 0';
            stage.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + z + ')';
        },

        /* 将相机平移夹在地形包围盒内，确保玩家永远看不到地图（群山外障）之外 */
        _clampCam: function () {
            var c = this.cam;
            var r = this._terrainRect();
            var z = c.zoom;
            var minTx = c.vw - r.r * z;
            var maxTx = -r.l * z;
            var minTy = c.vh - r.b * z;
            var maxTy = -r.t * z;
            if (minTx > maxTx) { var t = minTx; minTx = maxTx; maxTx = t; }
            if (minTy > maxTy) { var s = minTy; minTy = maxTy; maxTy = s; }
            c.ttx = Math.max(minTx, Math.min(maxTx, c.ttx));
            c.tty = Math.max(minTy, Math.min(maxTy, c.tty));
        },

        zoomBy: function (f) {
            var c = this.cam;
            var target = Math.max(0.4, Math.min(4.5, c.zoom * f));
            var cx = c.vw / 2, cy = c.vh / 2;
            var rx = (cx - c.tx) / c.zoom, ry = (cy - c.ty) / c.zoom;
            c.zoom = target; c.tz = target;
            cx = c.vw / 2; cy = c.vh / 2;
            c.ttx = cx - rx * target;
            c.tty = cy - ry * target;
            this._clampCam();
            c.tx = c.ttx; c.ty = c.tty;
            this.applyCamera(true);
        },

        /* 镜头拉近：仅覆盖大陆的一小部分，并完全随角色移动 */
        centerOn: function (gx, gy, zoom) {
            var c = this.cam;
            var p = this._project(gx, gy);
            var z = zoom;
            if (!z) {
                z = Math.max(1.1, Math.min(c.vw, c.vh) / 360);
                z = Math.max(1.1, Math.min(z, 2.2));
            }
            c.zoom = z; c.tz = z;
            c.ttx = c.vw / 2 - p.x * z;
            c.tty = c.vh / 2 - p.y * z - 20;
            this._clampCam();
            c.tx = c.ttx; c.ty = c.tty;
            this.applyCamera(true);
            this._updateCulling(true);
        },

        /* 分块视野剔除：只显示相机视口可见（含边距）的分块，其余整块隐藏 */
        _updateCulling: function (force) {
            if (!this.chunkList || this.chunkList.length === 0) return;
            var c = this.cam;
            var z = c.zoom;
            var o = this.origin;
            /* 视口四角 → 等距格范围 */
            var corners = [[0, 0], [c.vw, 0], [0, c.vh], [c.vw, c.vh]];
            var minTx2 = 1e9, minTy2 = 1e9, maxTx2 = -1e9, maxTy2 = -1e9;
            for (var i = 0; i < corners.length; i++) {
                var sx = corners[i][0], sy = corners[i][1];
                var xs = (sx - c.tx) / z - o.x;
                var ys = (sy - c.ty) / z - o.y;
                var a = xs / COS_Z;
                var b = ys / SIN_ZX;
                var ix = (a + b) / 2 / CELL;
                var iy = (b - a) / 2 / CELL;
                if (ix < minTx2) minTx2 = ix; if (ix > maxTx2) maxTx2 = ix;
                if (iy < minTy2) minTy2 = iy; if (iy > maxTy2) maxTy2 = iy;
            }
            var pad = 2; // 分块边距（格）
            var kx0 = Math.max(0, (Math.floor(minTx2 - pad) / CHUNK) | 0);
            var ky0 = Math.max(0, (Math.floor(minTy2 - pad) / CHUNK) | 0);
            var kx1 = Math.max(0, (Math.floor(maxTx2 + pad) / CHUNK) | 0);
            var ky1 = Math.max(0, (Math.floor(maxTy2 + pad) / CHUNK) | 0);
            var key = kx0 + ',' + ky0 + ',' + kx1 + ',' + ky1 + '|' + Math.round(z * 100);
            if (!force && key === this._culledKey) return;
            this._culledKey = key;
            var list = this.chunkList;
            for (var n = 0; n < list.length; n++) {
                var ch = list[n];
                var cx = Number(ch.getAttribute('data-cx'));
                var cy = Number(ch.getAttribute('data-cy'));
                var on = cx >= kx0 && cx <= kx1 && cy >= ky0 && cy <= ky1;
                ch.style.display = on ? '' : 'none';
            }
        },

        /* 返回地图时按 realm 回填导航：推近相机到该道入口并短暂高亮 */
        focusRealm: function (realm) {
            if (!realm || !this.pois) return;
            var target = null;
            this.pois.forEach(function (e) {
                if (!target && e.poi.type === 'realm' && e.poi.realm === realm) target = e;
            });
            if (!target) return;
            this.centerOn(target.poi.x + 0.5, target.poi.y + 0.5, 1.9);
            var emoji = target.emoji;
            if (emoji) {
                emoji.style.transition = 'filter .4s, transform .4s';
                emoji.classList.add('realm-focus');
                setTimeout(function () {
                    emoji.classList.remove('realm-focus');
                    emoji.style.transition = '';
                }, 1600);
            }
        },

        /* ── 主循环：移动 / 互动 / 相机跟随 ── */
        step: function (dt) {
            if (!this.playerEl) return;
            var paused = !!(UI && UI.isModalOpen());
            var keys = this.keys || {};
            /* 以屏幕方向为准（WASD = 上/下/左/右），再换算为等距格位移 */
            var sx = 0, sy = 0;
            if (!paused) {
                if (keys['ArrowLeft'] || keys['a']) sx -= 1;
                if (keys['ArrowRight'] || keys['d']) sx += 1;
                if (keys['ArrowUp'] || keys['w']) sy -= 1;
                if (keys['ArrowDown'] || keys['s']) sy += 1;
            }
            if (sx !== 0 || sy !== 0) {
                var inv = Math.hypot(sx, sy);
                this.moving = true;
                if (sx !== 0) this.facing = sx > 0 ? 1 : -1;
                var spd = this.playerSpeed * dt;
                /* W/S/A/D 严格映射世界轴向（W 北/-y、S 南/+y、A 西/-x、D 东/+x），
                   使俯视北向小地图与小地图移动方向一致（避免 W/S 呈 45° 斜移）。 */
                this._moveAxis((sx / inv) * spd, (sy / inv) * spd);
            } else {
                this.moving = false;
            }
            this.updatePlayerMarker();
            if (!paused) this._updateInteraction();
            if (!paused) this._updateRealmGuide();
            if (!paused && this.ePressed) { this._onInteract(); this.ePressed = false; }
        },

        _moveAxis: function (mx, my) {
            var nx = this.playerPos.x + mx;
            if (!this._willCollide(nx, this.playerPos.y)) this.playerPos.x = nx;
            var ny = this.playerPos.y + my;
            if (!this._willCollide(this.playerPos.x, ny)) this.playerPos.y = ny;
        },

        _willCollide: function (gx, gy) {
            var r = 0.28;
            var r0 = Math.floor(gx - r), r1 = Math.floor(gx + r);
            var c0 = Math.floor(gy - r), c1 = Math.floor(gy + r);
            return this._isBarrier(r0, c0) || this._isBarrier(r1, c0) ||
                   this._isBarrier(r0, c1) || this._isBarrier(r1, c1);
        },

        _updateInteraction: function () {
            var self = this;
            var reach = (this.ow.player && this.ow.player.interact_tiles) || 1.5;
            reach = Math.max(reach, 3.2); /* 扩大交互范围，方便点击/靠近 */
            var closest = null, minD = reach + 1;
            this.pois.forEach(function (entry) {
                var d = Math.hypot(entry.poi.x - self.playerPos.x, entry.poi.y - self.playerPos.y);
                if (d <= reach && d < minD) { minD = d; closest = entry; }
            });
            this.closestPoi = closest;
            var prev = this.activePoi;
            var activeId = closest ? closest.poi.id : null;
            (this.pois).forEach(function (e) {
                if (e.emoji) e.emoji.classList.toggle('active', e.poi.id === activeId);
                else if (e.billboard && e.el) e.el.classList.toggle('active', e.poi.id === activeId);
            });
            this.activePoi = activeId;
            if (closest && UI) {
                var lbl;
                if (closest.poi.type === 'realm') lbl = '前往 ' + (REALM_NAMES[closest.poi.realm] || closest.poi.realm);
                else if (closest.poi.type === 'sandbox') lbl = '进入 ' + (closest.poi.label || '沙盒训练场');
                else if (closest.poi.type === 'billboard') lbl = '查看 ' + (closest.poi.label || '轮回修行告示牌');
                else if (closest.poi.type === 'achievements') lbl = '查看 ' + (closest.poi.label || '成就殿堂');
                else lbl = closest.poi.label || '互动';
                UI.setInteractHint(lbl);
            } else if (UI) {
                UI.setInteractHint(null);
            }
        },

        /* 最近入口引导：计算距玩家最近的六道入口，给出方向箭头 + 距离 */
        _updateRealmGuide: function () {
            if (!UI || !UI.setRealmGuide || !this.pois) return;
            var self = this;
            var best = null, bestD = Infinity;
            this.pois.forEach(function (entry) {
                if (entry.poi.type !== 'realm') return;
                var d = Math.max(
                    Math.abs((entry.poi.x + 0.5) - self.playerPos.x),
                    Math.abs((entry.poi.y + 0.5) - self.playerPos.y)
                );
                if (d < bestD) { bestD = d; best = entry; }
            });
            /* 已站在某个道门前（互动提示条接管）则隐藏罗盘 */
            if (this.closestPoi && this.closestPoi.poi.type === 'realm') {
                UI.setRealmGuide(null);
                return;
            }
            if (!best) { UI.setRealmGuide(null); return; }

            /* 屏幕空间方向：以玩家到入口的投影差换算箭头指向 */
            var c = this.cam;
            var pp = this._project(this.playerPos.x, this.playerPos.y);
            var px = pp.x * c.zoom + c.tx, py = pp.y * c.zoom + c.ty;
            var ex = best.x * c.zoom + c.tx, ey = best.y * c.zoom + c.ty;
            var deg = Math.round(Math.atan2(ey - py, ex - px) * 180 / Math.PI);
            var name = REALM_NAMES[best.poi.realm] || best.poi.realm;
            UI.setRealmGuide({ name: name, dist: Math.round(bestD), arrow: '➤', angle: deg });
        },

        _onInteract: function () {
            var entry = this.closestPoi;
            if (!entry || !UI) return;
            if (entry.poi.type === 'realm') { this._markExplored(entry.poi.id); UI.openRealmSelect(entry.poi.realm); }
            else if (entry.poi.type === 'sandbox') {
                this._markExplored(entry.poi.id);
                location.href = '/sandbox?r=' + Date.now();
            }
            else if (entry.poi.type === 'npc') { this._markExplored(entry.poi.id); UI.openSkillTree(); }
            else if (entry.poi.type === 'billboard') { this._markExplored(entry.poi.id); UI.openRpgStats(); }
            else if (entry.poi.type === 'achievements') { this._markExplored(entry.poi.id); UI.openAchievements(); }
            else if (entry.poi.type === 'spawn') { if (UI.toast) UI.toast('生灭台：这里是旅途的起点。'); }
        },

        bindInput: function () {
            var self = this;
            this.keys = {};
            window.addEventListener('keydown', function (e) {
                var k = e.key, lk = k.toLowerCase();
                if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].indexOf(k) >= 0) e.preventDefault();
                if (lk === 'e') self.ePressed = true;
                self.keys[lk] = true;
                if (k === '+' || k === '=') { self.zoomBy(1.35); e.preventDefault(); }
                else if (k === '-' || k === '_') { self.zoomBy(1 / 1.35); e.preventDefault(); }
            });
            window.addEventListener('keyup', function (e) {
                self.keys[e.key.toLowerCase()] = false;
            });
            /* 鼠标滚轮缩放镜头高度 */
            window.addEventListener('wheel', function (e) {
                if (UI && UI.isModalOpen()) return;
                var f = 1.18;
                if (e.deltaY > 0) self.zoomBy(1 / f);   // 下滚 → 拉远
                else if (e.deltaY < 0) self.zoomBy(f);  // 上滚 → 拉近
                e.preventDefault();
            }, { passive: false });
            var zin = document.getElementById('btn-zoom-in');
            var zout = document.getElementById('btn-zoom-out');
            if (zin) zin.addEventListener('click', function () { self.zoomBy(1.35); });
            if (zout) zout.addEventListener('click', function () { self.zoomBy(1 / 1.35); });
        },

        start: function () {
            var self = this;
            this.setupCamera();
            // 镜头拉近到出生点：仅覆盖大陆的一小部分，随角色移动
            this.centerOn(this.playerPos.x, this.playerPos.y);
            this.bindInput();
            var last = performance.now();
            function loop(now) {
                var dt = Math.min(0.05, (now - last) / 1000);
                last = now;
                self.step(dt);
                /* 相机完全随角色移动：每帧以角色为中心计算目标平移 */
                var c = self.cam;
                var p = self._project(self.playerPos.x, self.playerPos.y);
                c.ttx = c.vw / 2 - p.x * c.zoom;
                c.tty = c.vh / 2 - p.y * c.zoom - 20;
                self._clampCam();   // 夹在地形包围盒内，绝不露出地图（群山外障）之外
                /* 帧率无关的平滑跟随：角色居中，镜头紧贴 */
                var k = 1 - Math.pow(0.002, dt);
                c.tx += (c.ttx - c.tx) * k;
                c.ty += (c.tty - c.ty) * k;
                self.applyCamera(false);
                self._updateCulling(false); // 只渲染视口内的分块
                requestAnimationFrame(loop);
            }
            requestAnimationFrame(loop);
        }
    };

    /* ── 工具：hex → rgba 字符串 ── */
    function hexA(hex, a) {
        var n = parseInt(hex.replace('#', ''), 16);
        var r = n >> 16 & 255, g = n >> 8 & 255, b = n & 255;
        return 'rgba(' + r + ',' + g + ',' + b + ',' + a + ')';
    }

    /* ── 工具：hex → [r,g,b] ── */
    function hexToRGB(hex) {
        var n = parseInt(String(hex).replace('#', ''), 16) || 0;
        return [n >> 16 & 255, n >> 8 & 255, n & 255];
    }

    function boot() {
        var game = window.OverworldGame = OverworldGame;
        if (window.OverworldUI) {
            window.OverworldUI.init(game);
            UI = window.OverworldUI;
        }
        if (UI) UI.showLoading();
        fetch('/api/overworld/config')
            .then(function (r) { return r.json(); })
            .then(function (ow) {
                if (!ow || !ow.world) { if (UI) UI.showError('大陆配置加载失败，请重试'); return; }
                if (UI) UI.setLoadingProgress(30);           // 已取得大陆配置
                game.bootstrapGeometry(ow);
                game._fetchGamesAndState();
                if (UI) UI.setLoadingProgress(55);           // 地理几何搭建完成
                // 对局返回大地图时按 realm 回填导航（推近到该道入口）
                var backRealm = (function () {
                    try { return new URLSearchParams(location.search).get('backrealm'); } catch (e) { return null; }
                })();
                requestAnimationFrame(function () {
                    requestAnimationFrame(function () {
                        if (UI) UI.setLoadingProgress(78);   // 场景铺陈中
                        game.buildScene();
                        game.initMinimap();
                        game.start();
                        if (UI) UI.setLoadingProgress(100);  // 大陆就绪
                        if (backRealm) {
                            setTimeout(function () { game.focusRealm(backRealm); }, 60);
                        }
                    });
                });
                if (UI) UI.refreshHUD();
                setTimeout(function () { if (UI) UI.removeLoading(); }, 600);
                game.startPolling();
            })
            .catch(function () { if (UI) UI.showError('大陆配置加载失败，请重试'); });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
    else boot();
})();
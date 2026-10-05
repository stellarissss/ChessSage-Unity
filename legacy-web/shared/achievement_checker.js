/**
 * 成就检测共享模块 · 六道众生
 * 各游戏通过 <script src="http://localhost:8080/shared/achievement_checker.js"> 加载
 * 暴露 window.AchievementChecker 全局对象
 */
(function () {
    "use strict";

    var HUB_URL = "http://localhost:8080";

    // ── 跨页事件广播：BroadcastChannel + localStorage 双通道 ──
    var _ch = null;
    function _getChannel() {
        if (_ch) return _ch;
        try {
            if (typeof BroadcastChannel !== "undefined") {
                _ch = new BroadcastChannel("game-events");
            }
        } catch (e) { _ch = null; }
        return _ch;
    }
    function _broadcast(type, payload) {
        var ch = _getChannel();
        if (ch) { try { ch.postMessage({ type: type, ...(payload || {}) }); } catch (e) {} }
        // localStorage 兜底
        try { localStorage.setItem("ge_" + type, JSON.stringify({ value: payload || {}, ts: Date.now() })); }
        catch (e) {}
    }

    // 成就定义（与 main.py ACHIEVEMENT_DEFINITIONS 同步）
    var ACHIEVEMENTS = {
        ambush:            { name: "十面埋伏",      icon: "♟",  desc: "象棋中你的棋子数量≥20" },
        sea_of_pieces:     { name: "人海战术",      icon: "👥", desc: "棋盘上总棋子数≥40" },
        last_man_standing: { name: "孤勇者",        icon: "🦸", desc: "你只剩1个棋子且游戏未结束" },
        palette:           { name: "调色板",        icon: "🎨", desc: "让棋盘变色" },
        reality_stone:     { name: "现实宝石",      icon: "💎", desc: "改变棋盘线条/框架" },
        bigger_picture:    { name: "格局打开",      icon: "📐", desc: "修改棋盘尺寸" },
        lawn_party:        { name: "草坪派对",      icon: "🌱", desc: "棋盘变成绿色系" },
        genshin:           { name: "我超，原",      icon: "✨", desc: "棋盘变成紫色系" },
        clone_wars:        { name: "克隆战争",      icon: "🧬", desc: "创建了自定义棋子" },
        zoo:               { name: "动物园",        icon: "🦁", desc: "在斗兽棋中创建新的动物棋子" },
        alchemist:         { name: "炼金术士",      icon: "⚗️", desc: "AI成功创建了自定义棋子" },
        architect:         { name: "建筑师",        icon: "🏗️", desc: "AI成功修改了棋盘外观" },
        lawmaker:          { name: "立法者",        icon: "📜", desc: "AI成功修改了规则" },
        what_a_guy:        { name: "好家伙",        icon: "😱", desc: "创建了名字超过10字的棋子" },
        outlaw:            { name: "无法无天",      icon: "🏴", desc: "激活10条以上自定义规则" },
        rewrite_fate:      { name: "改写命运",      icon: "✏️", desc: "成功修改了棋子走法" },
        god_hand:          { name: "上帝之手",      icon: "👁️", desc: "直接设置游戏胜负" },
        what_are_you_doing:{ name: "你在干嘛",      icon: "🤔", desc: "和ChatAI谈论无关的事情" },
        id_revealed:       { name: "报身份证号",    icon: "🪪", desc: "AI拒绝了你的指令" },
        chatterbox:        { name: "话痨",          icon: "💬", desc: "累计发送50条AI指令" },
        all_in_one:        { name: "这波是肉身开团", icon: "🌊", desc: "一步触发3种以上修改类型" },
        ceasefire:         { name: "停战协议",      icon: "⏸️", desc: "使用了跳过回合机制" },
        body_snatch:       { name: "夺舍",          icon: "🤖", desc: "使用了AI接管机制" },
        giving_up:         { name: "开摆",          icon: "🎲", desc: "使用了随机走棋机制" },
        clone_jutsu:       { name: "分身术",        icon: "⚡", desc: "使用了额外回合机制" },
        no_longer_human:   { name: "我不做人了",    icon: "🎭", desc: "使用了玩家控制切换机制" },
        speedrun:          { name: "就这？",        icon: "⚡", desc: "5步内获胜" },
        suffering:         { name: "受苦",          icon: "💀", desc: "连续被AI吃5子" },
        got_cketched:      { name: "我大意了啊",    icon: "😅", desc: "被AI获胜" },
        winner:            { name: "胜利者",        icon: "🏆", desc: "赢得一场比赛" },
        go_five:           { name: "五子棋？",      icon: "🔗", desc: "在围棋中把自己方棋子连成五子" },
        broke:             { name: "你币没了",      icon: "🪙", desc: "Token消耗超过10000" },
        money_power:       { name: "钞能力",        icon: "💸", desc: "Token消耗超过50000" },
        samsara:           { name: "六道轮回",      icon: "♻️", desc: "在所有6种棋类中各触发至少1个成就" },
        collector:         { name: "收藏家",        icon: "📦", desc: "解锁10个成就" },
        completionist:     { name: "成就党",        icon: "🎖️", desc: "解锁25个成就" },
        pokedex:           { name: "全图鉴",        icon: "📖", desc: "解锁所有成就" },
    };

    var checker = {
        _baseline: null,
        _consecutiveLosses: 0,
        _commandCount: 0,
        _lastPieceCount: null,
        _popupContainer: null,
        _popupStyleInjected: false,

        // ── 解锁成就（调用 Hub API）──
        unlock: function (achievementId, gameId, context) {
            var def = ACHIEVEMENTS[achievementId];
            if (!def) return;

            fetch(HUB_URL + "/api/achievements/unlock", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ achievement_id: achievementId, game: gameId, context: context || "" }),
            })
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    if (data.newly_unlocked) {
                        checker._showPopup(def);
                        // 通知所有页面（总坛 / 其它棋类）有成就解锁，含元成就信息
                        var achPayload = {
                            achievement: { id: achievementId, name: def.name, icon: def.icon, desc: def.desc, category: def.category, rarity: def.rarity, games: def.games }
                        };
                        _broadcast("achievement-unlocked", achPayload);
                        // 如果有元成就也解锁了
                        if (data.meta_unlocked && data.meta_unlocked.length > 0) {
                            data.meta_unlocked.forEach(function (mid, idx) {
                                var mdef = ACHIEVEMENTS[mid];
                                if (mdef) {
                                    setTimeout(function () {
                                        checker._showPopup(mdef);
                                        _broadcast("achievement-unlocked", {
                                            achievement: { id: mid, name: mdef.name, icon: mdef.icon, desc: mdef.desc, category: mdef.category, rarity: mdef.rarity, games: mdef.games }
                                        });
                                    }, (idx + 1) * 1200);
                                }
                            });
                        }
                    }
                })
                .catch(function (e) {
                    console.warn("[AchievementChecker] unlock failed:", e);
                });
        },

        // ── 更新统计 ──
        updateStats: function (field, increment, gameId) {
            var body = { field: field, increment: increment };
            if (gameId) body.game_id = gameId;
            fetch(HUB_URL + "/api/achievements/stats", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body),
            }).catch(function () {});
        },

        // ── 配置加载后检查 ──
        checkAfterConfigLoad: function (configs, boardState, gameId) {
            if (!configs) return;

            // 缓存基线（首次调用时）
            if (!checker._baseline) {
                checker._baseline = {
                    bgColor: configs.board ? _deepGet(configs.board, "appearance.background_color") : null,
                    lineColor: configs.board ? _deepGet(configs.board, "appearance.line_color") : null,
                    boardWidth: configs.board ? _deepGet(configs.board, "geometry.width") : null,
                    boardHeight: configs.board ? _deepGet(configs.board, "geometry.height") : null,
                };
            }

            // 棋盘变色
            var currentBg = configs.board ? _deepGet(configs.board, "appearance.background_color") : null;
            if (currentBg && checker._baseline.bgColor && currentBg !== checker._baseline.bgColor) {
                checker.unlock("palette", gameId, "棋盘背景色: " + currentBg);
                // 绿色系
                var rgb = _parseColor(currentBg);
                if (rgb && rgb.g > rgb.r && rgb.g > rgb.b) {
                    checker.unlock("lawn_party", gameId, "绿色系棋盘: " + currentBg);
                }
                // 紫色系
                if (rgb && rgb.b > rgb.r && rgb.b > rgb.g) {
                    checker.unlock("genshin", gameId, "紫色系棋盘: " + currentBg);
                }
            }

            // 线条变色
            var currentLine = configs.board ? _deepGet(configs.board, "appearance.line_color") : null;
            if (currentLine && checker._baseline.lineColor && currentLine !== checker._baseline.lineColor) {
                checker.unlock("reality_stone", gameId, "线条色: " + currentLine);
            }

            // 棋盘尺寸变化
            var currentW = configs.board ? _deepGet(configs.board, "geometry.width") : null;
            var currentH = configs.board ? _deepGet(configs.board, "geometry.height") : null;
            if (currentW && checker._baseline.boardWidth && currentW !== checker._baseline.boardWidth) {
                checker.unlock("bigger_picture", gameId, "宽度: " + checker._baseline.boardWidth + "→" + currentW);
            }
            if (currentH && checker._baseline.boardHeight && currentH !== checker._baseline.boardHeight) {
                checker.unlock("bigger_picture", gameId, "高度: " + checker._baseline.boardHeight + "→" + currentH);
            }

            // 自定义棋子
            var hasCustom = false;
            var longName = false;
            ["pieces_red", "pieces_black", "pieces_white"].forEach(function (key) {
                var pieces = configs[key];
                if (pieces && pieces.custom_pieces) {
                    var cp = pieces.custom_pieces;
                    if (Array.isArray(cp) && cp.length > 0) {
                        hasCustom = true;
                        cp.forEach(function (p) {
                            if (p.name && p.name.length > 10) longName = true;
                        });
                    } else if (typeof cp === "object" && Object.keys(cp).length > 0) {
                        hasCustom = true;
                        Object.keys(cp).forEach(function (k) {
                            var p = cp[k];
                            if (p && p.name && p.name.length > 10) longName = true;
                        });
                    }
                }
            });
            if (hasCustom) {
                checker.unlock("clone_wars", gameId, "检测到自定义棋子");
                if (gameId === "dongwuqi") {
                    checker.unlock("zoo", gameId, "动物棋中创建了新棋子");
                }
            }
            if (longName) {
                checker.unlock("what_a_guy", gameId, "创建了超长名字的棋子");
            }

            // 激活规则数量
            if (boardState && boardState.game_status && boardState.game_status.custom_rules_active) {
                if (boardState.game_status.custom_rules_active.length >= 10) {
                    checker.unlock("outlaw", gameId, "已激活" + boardState.game_status.custom_rules_active.length + "条规则");
                }
            }

            // 机制检测
            if (boardState && boardState.mechanisms) {
                var mech = boardState.mechanisms;
                if (_hasActiveMech(mech.skip_turns)) checker.unlock("ceasefire", gameId, "使用了跳过回合机制");
                if (_hasActiveMech(mech.ai_control)) checker.unlock("body_snatch", gameId, "使用了AI接管机制");
                if (_hasActiveMech(mech.random_moves)) checker.unlock("giving_up", gameId, "使用了随机走棋机制");
                if (_hasActiveMech(mech.extra_turns)) checker.unlock("clone_jutsu", gameId, "使用了额外回合机制");
                if (_hasActiveMech(mech.player_control)) checker.unlock("no_longer_human", gameId, "使用了玩家控制切换机制");
            }

            // 规则修改检测（对比 pieces 的 moves 与基线）
            if (checker._baseline && checker._baseline.piecesSnapshot) {
                if (_checkRulesModified(configs, checker._baseline.piecesSnapshot)) {
                    checker.unlock("rewrite_fate", gameId, "棋子走法已被修改");
                }
            } else if (configs.pieces_red || configs.pieces_black) {
                checker._baseline.piecesSnapshot = JSON.parse(JSON.stringify({
                    pieces_red: configs.pieces_red,
                    pieces_black: configs.pieces_black,
                    pieces_white: configs.pieces_white,
                }));
            }
        },

        // ── 走棋后检查 ──
        checkAfterMove: function (boardState, configs, gameId) {
            if (!boardState) return;

            var pieces = boardState.pieces || [];
            var alivePieces = pieces.filter(function (p) { return p.is_alive !== false; });
            var gameStatus = boardState.game_status || {};
            var moveHistory = boardState.move_history || [];

            // 确定 playerSide
            var playerSide = boardState.player_side || (configs.board_state ? configs.board_state.player_side : null) || "red";
            // 象棋/动物棋/跳棋默认 red，五子棋默认 black，围棋默认 black，黑白棋无固定
            if (!boardState.player_side) {
                if (gameId === "wuziqi" || gameId === "weiqi") playerSide = "black";
                else if (gameId === "heibaiqi") playerSide = "black";
                else playerSide = "red";
            }

            var myPieces = alivePieces.filter(function (p) { return p.side === playerSide; });

            // 十面埋伏
            if (gameId === "xiangqi" && myPieces.length >= 20) {
                checker.unlock("ambush", gameId, "你方棋子数量: " + myPieces.length);
            }

            // 人海战术
            if (alivePieces.length >= 40) {
                checker.unlock("sea_of_pieces", gameId, "总棋子数: " + alivePieces.length);
            }

            // 孤勇者
            if (myPieces.length === 1 && gameStatus.state === "playing") {
                checker.unlock("last_man_standing", gameId, "你只剩1个棋子");
            }

            // 受苦：连续被吃5子
            if (checker._lastPieceCount !== null) {
                if (myPieces.length < checker._lastPieceCount) {
                    var lost = checker._lastPieceCount - myPieces.length;
                    checker._consecutiveLosses += lost;
                    if (checker._consecutiveLosses >= 5) {
                        checker.unlock("suffering", gameId, "连续被吃" + checker._consecutiveLosses + "子");
                    }
                } else if (myPieces.length > checker._lastPieceCount) {
                    checker._consecutiveLosses = 0;
                }
            }
            checker._lastPieceCount = myPieces.length;

            // 游戏结束
            if (gameStatus.state === "ended") {
                if (gameStatus.winner === playerSide) {
                    checker.unlock("winner", gameId, "获胜: " + (gameStatus.win_condition || ""));
                    // 5步内获胜
                    if (moveHistory.length <= 5) {
                        checker.unlock("speedrun", gameId, moveHistory.length + "步获胜");
                    }
                } else if (gameStatus.winner && gameStatus.winner !== playerSide) {
                    checker.unlock("got_cketched", gameId, "被" + gameStatus.winner + "方击败");
                }
            }

            // 围棋五子连珠
            if (gameId === "weiqi") {
                if (_checkFiveInRow(alivePieces, playerSide)) {
                    checker.unlock("go_five", gameId, "围棋中五子连珠");
                }
            }

            // 更新走棋统计
            checker.updateStats("total_moves", 1, gameId);
        },

        // ── AI 指令后检查 ──
        checkAfterCommand: function (response, command, configs, boardState, gameId) {
            if (!response) return;

            checker._commandCount++;
            checker.updateStats("total_commands", 1, gameId);

            // 话痨
            if (checker._commandCount >= 50) {
                checker.unlock("chatterbox", gameId, "累计" + checker._commandCount + "条指令");
            }

            if (!response.success) return;

            // 你在干嘛 (fun 类型)
            if (response.type === "fun") {
                checker.unlock("what_are_you_doing", gameId, "指令: " + (command || "").substring(0, 30));
            }

            // 报身份证号 (rejected 类型)
            if (response.type === "rejected") {
                checker.unlock("id_revealed", gameId, "指令被拒: " + (command || "").substring(0, 30));
            }

            if (response.type === "applied") {
                var modifiedConfigs = response.modified_configs || {};
                var configKeys = Object.keys(modifiedConfigs);
                var classification = response.classification || "";

                // 这波是肉身开团 (3种以上修改)
                if (configKeys.length >= 3) {
                    checker.unlock("all_in_one", gameId, "同时修改了: " + configKeys.join(", "));
                }

                // 建筑师 (修改了 board 或 ui_config)
                if (modifiedConfigs.board || modifiedConfigs.ui_config) {
                    checker.unlock("architect", gameId, "AI修改了棋盘外观");
                }

                // 立法者 (修改了 rules 或 pieces，分类为 C)
                if ((modifiedConfigs.rules || modifiedConfigs.pieces_red || modifiedConfigs.pieces_black || modifiedConfigs.pieces_white) && classification.indexOf("C") === 0) {
                    checker.unlock("lawmaker", gameId, "AI修改了规则");
                }

                // 炼金术士 (分类 C+，创建了自定义棋子)
                if (classification === "C+") {
                    checker.unlock("alchemist", gameId, "AI创建了自定义棋子");
                }

                // 上帝之手 (分类 A1，设置了胜负)
                if (classification.indexOf("A1") === 0) {
                    checker.unlock("god_hand", gameId, "AI直接设置了游戏胜负");
                }
            }
        },

        // ── Token 统计检查 ──
        checkTokenStats: function (tokenStats, gameId) {
            if (!tokenStats) return;
            var total = tokenStats.total_tokens || 0;
            if (total >= 10000) {
                checker.unlock("broke", gameId, "Token消耗: " + total);
            }
            if (total >= 50000) {
                checker.unlock("money_power", gameId, "Token消耗: " + total);
            }
        },

        // ── 弹窗显示 ──
        _showPopup: function (achievement) {
            if (!checker._popupStyleInjected) {
                checker._injectPopupStyle();
            }

            var popup = document.createElement("div");
            popup.className = "ach-popup";
            popup.innerHTML =
                '<div class="ach-popup-icon">' + (achievement.icon || "🏆") + "</div>" +
                '<div class="ach-popup-body">' +
                '<div class="ach-popup-label">🏆 成就解锁！</div>' +
                '<div class="ach-popup-name">' + (achievement.name || "未知成就") + "</div>" +
                '<div class="ach-popup-desc">' + (achievement.desc || "") + "</div>" +
                "</div>";

            document.body.appendChild(popup);

            // 入场动画
            requestAnimationFrame(function () {
                popup.classList.add("show");
            });

            // 3.5秒后移除
            setTimeout(function () {
                popup.classList.remove("show");
                setTimeout(function () {
                    if (popup.parentNode) popup.parentNode.removeChild(popup);
                }, 400);
            }, 3500);
        },

        _injectPopupStyle: function () {
            checker._popupStyleInjected = true;
            var style = document.createElement("style");
            style.textContent = [
                ".ach-popup{position:fixed;top:24px;right:24px;z-index:999999;display:flex;align-items:center;gap:14px;padding:16px 22px;min-width:280px;max-width:380px;background:linear-gradient(135deg,rgba(20,20,24,0.96),rgba(30,28,18,0.96));border:1.5px solid #d4af37;border-radius:14px;box-shadow:0 8px 40px rgba(212,175,55,0.25),0 0 0 1px rgba(212,175,55,0.1);backdrop-filter:blur(12px);opacity:0;transform:translateX(120%) scale(0.9);transition:all .4s cubic-bezier(.22,1,.36,1);font-family:'Noto Serif SC',serif;color:#eceae4;}",
                ".ach-popup.show{opacity:1;transform:translateX(0) scale(1);}",
                ".ach-popup-icon{font-size:36px;line-height:1;filter:drop-shadow(0 0 8px rgba(212,175,55,0.5));}",
                ".ach-popup-label{font-size:11px;color:#d4af37;letter-spacing:.15em;text-transform:uppercase;font-family:Orbitron,monospace;margin-bottom:2px;}",
                ".ach-popup-name{font-size:18px;font-weight:700;color:#f3e9d2;margin-bottom:2px;}",
                ".ach-popup-desc{font-size:12px;color:#99948a;}",
            ].join("\n");
            document.head.appendChild(style);
        },
    };

    // ── 辅助函数 ──

    function _deepGet(obj, path) {
        if (!obj) return null;
        var parts = path.split(".");
        var cur = obj;
        for (var i = 0; i < parts.length; i++) {
            if (cur == null) return null;
            cur = cur[parts[i]];
        }
        return cur;
    }

    function _parseColor(color) {
        if (!color || typeof color !== "string") return null;
        // hex
        var m = color.match(/^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
        if (m) {
            return { r: parseInt(m[1], 16), g: parseInt(m[2], 16), b: parseInt(m[3], 16) };
        }
        m = color.match(/^#([0-9a-f])([0-9a-f])([0-9a-f])$/i);
        if (m) {
            return { r: parseInt(m[1] + m[1], 16), g: parseInt(m[2] + m[2], 16), b: parseInt(m[3] + m[3], 16) };
        }
        // rgb
        m = color.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
        if (m) {
            return { r: parseInt(m[1]), g: parseInt(m[2]), b: parseInt(m[3]) };
        }
        return null;
    }

    function _hasActiveMech(arr) {
        if (!arr) return false;
        if (!Array.isArray(arr)) return false;
        return arr.some(function (m) {
            if (!m) return false;
            if (m.remaining !== undefined) return m.remaining > 0;
            return true;
        });
    }

    function _checkRulesModified(configs, snapshot) {
        if (!snapshot) return false;
        try {
            var current = JSON.stringify({
                pieces_red: configs.pieces_red ? configs.pieces_red.pieces : null,
                pieces_black: configs.pieces_black ? configs.pieces_black.pieces : null,
                pieces_white: configs.pieces_white ? configs.pieces_white.pieces : null,
            });
            var original = JSON.stringify({
                pieces_red: snapshot.pieces_red ? snapshot.pieces_red.pieces : null,
                pieces_black: snapshot.pieces_black ? snapshot.pieces_black.pieces : null,
                pieces_white: snapshot.pieces_white ? snapshot.pieces_white.pieces : null,
            });
            return current !== original;
        } catch (e) {
            return false;
        }
    }

    function _checkFiveInRow(pieces, side) {
        var myPieces = pieces.filter(function (p) { return p.side === side && p.is_alive !== false; });
        if (myPieces.length < 5) return false;

        var positions = {};
        myPieces.forEach(function (p) {
            if (p.position) {
                positions[p.position[0] + "," + p.position[1]] = true;
            }
        });

        var dirs = [[1, 0], [0, 1], [1, 1], [1, -1]];
        for (var i = 0; i < myPieces.length; i++) {
            var p = myPieces[i];
            if (!p.position) continue;
            for (var d = 0; d < dirs.length; d++) {
                var count = 1;
                for (var step = 1; step < 5; step++) {
                    var nx = p.position[0] + dirs[d][0] * step;
                    var ny = p.position[1] + dirs[d][1] * step;
                    if (positions[nx + "," + ny]) {
                        count++;
                    } else {
                        break;
                    }
                }
                if (count >= 5) return true;
            }
        }
        return false;
    }

    window.AchievementChecker = checker;
})();

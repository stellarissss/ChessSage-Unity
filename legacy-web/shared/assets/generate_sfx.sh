#!/bin/bash
# ============================================================
# 《棋圣·六道轮回》游戏音效生成脚本
# 使用 sox 合成 28 个 .wav 音效文件
# ============================================================

set -u

OUT="/workspace/shared/assets/audio/sfx"
TMP="/tmp/sfx_build_$$"
mkdir -p "$OUT" "$TMP"

# 计数器
OK=0
FAIL=0
FAILED_LIST=""

# 生成函数：成功时 OK++，失败时记录并继续
gen() {
    local name="$1"; shift
    if "$@" 2>/dev/null; then
        if [ -s "$OUT/$name" ]; then
            OK=$((OK+1))
            return 0
        fi
    fi
    FAIL=$((FAIL+1))
    FAILED_LIST="$FAILED_LIST $name"
    echo "  [FAIL] $name"
    return 1
}

echo "==> 开始生成 28 个音效，输出目录: $OUT"
echo

# ============== 通用音效 (10) ==============

# 1. 对话打字音 - 短促方波 800Hz, 50ms
gen sfx_dialog_blip.wav sox -n "$OUT/sfx_dialog_blip.wav" synth 0.05 square 800 fade q 0.005 0.05 0.01

# 2. 选项出现 - 上行正弦 400->800Hz, 200ms
gen sfx_choice_appear.wav sox -n "$OUT/sfx_choice_appear.wav" synth 0.2 sine 400-800 fade q 0.01 0.2 0.05

# 3. 选项确认 - 短促正弦 1000Hz, 100ms
gen sfx_choice_select.wav sox -n "$OUT/sfx_choice_select.wav" synth 0.1 sine 1000 fade q 0.005 0.1 0.02

# 4. 悟道向选择 - 温和上扬和弦(正弦 600+800Hz), 400ms
sox -n "$TMP/e1.wav" synth 0.4 sine 600 fade q 0.02 0.4 0.1 2>/dev/null
sox -n "$TMP/e2.wav" synth 0.4 sine 800 fade q 0.02 0.4 0.1 2>/dev/null
gen sfx_choice_enlightenment.wav sox -m "$TMP/e1.wav" "$TMP/e2.wav" "$OUT/sfx_choice_enlightenment.wav" gain -3

# 5. 堕落向选择 - 低沉下落(方波 400->200Hz), 400ms
gen sfx_choice_corruption.wav sox -n "$OUT/sfx_choice_corruption.wav" synth 0.4 square 400-200 fade q 0.01 0.4 0.1

# 6. 记忆碎片解锁 - 空灵铃声(正弦 1200Hz+颤音), 800ms
gen sfx_memory_unlock.wav sox -n "$OUT/sfx_memory_unlock.wav" synth 0.8 sine 1200 tremolo 20 fade q 0.02 0.8 0.2

# 7. 关卡胜利 - 上行琶音 C-E-G-C (523,659,784,1047Hz)
sox -n "$TMP/w1.wav" synth 0.15 sine 523 fade q 0.005 0.15 0.03 2>/dev/null
sox -n "$TMP/w2.wav" synth 0.15 sine 659 fade q 0.005 0.15 0.03 2>/dev/null
sox -n "$TMP/w3.wav" synth 0.15 sine 784 fade q 0.005 0.15 0.03 2>/dev/null
sox -n "$TMP/w4.wav" synth 0.3 sine 1047 fade q 0.005 0.3 0.1 2>/dev/null
gen sfx_level_win.wav sox "$TMP/w1.wav" "$TMP/w2.wav" "$TMP/w3.wav" "$TMP/w4.wav" "$OUT/sfx_level_win.wav"

# 8. 关卡失败 - 下行音 (方波 440->220->110Hz)
sox -n "$TMP/l1.wav" synth 0.2 square 440 fade q 0.005 0.2 0.05 2>/dev/null
sox -n "$TMP/l2.wav" synth 0.2 square 220 fade q 0.005 0.2 0.05 2>/dev/null
sox -n "$TMP/l3.wav" synth 0.3 square 110 fade q 0.005 0.3 0.1 2>/dev/null
gen sfx_level_lose.wav sox "$TMP/l1.wav" "$TMP/l2.wav" "$TMP/l3.wav" "$OUT/sfx_level_lose.wav"

# 9. 识破概率警告 - 不和谐音(方波+噪声), 600ms
sox -n "$TMP/dw1.wav" synth 0.6 square 300 fade q 0.01 0.6 0.1 2>/dev/null
sox -n "$TMP/dw2.wav" synth 0.6 brownnoise fade q 0.01 0.6 0.1 2>/dev/null
gen sfx_detection_warning.wav sox -m "$TMP/dw1.wav" "$TMP/dw2.wav" "$OUT/sfx_detection_warning.wav" gain -5

# 10. 被识破 - 重击低音 (低频 80Hz + 噪声爆发), 1000ms
sox -n "$TMP/dt1.wav" synth 1.0 sine 80 2>/dev/null
sox -n "$TMP/dt2.wav" synth 1.0 brownnoise fade q 0.005 0.3 0.3 2>/dev/null
gen sfx_detection_triggered.wav sox -m "$TMP/dt1.wav" "$TMP/dt2.wav" "$OUT/sfx_detection_triggered.wav" fade q 0.005 1.0 0.3 gain -3

# ============== 棋类专属音效 (6) ==============

# 11. 黑白棋翻转 - 频率扫描 200->1200Hz
gen sfx_heibaiqi_flip.wav sox -n "$OUT/sfx_heibaiqi_flip.wav" synth 0.4 sine 200-1200 fade q 0.01 0.4 0.1

# 12. 跳棋连跳 - 三次短促(递增 800->1000->1200Hz)
sox -n "$TMP/tj1.wav" synth 0.06 sine 800 fade q 0.002 0.06 0.01 2>/dev/null
sox -n "$TMP/tj2.wav" synth 0.06 sine 1000 fade q 0.002 0.06 0.01 2>/dev/null
sox -n "$TMP/tj3.wav" synth 0.06 sine 1200 fade q 0.002 0.06 0.01 2>/dev/null
gen sfx_tiaoqi_jump.wav sox "$TMP/tj1.wav" "$TMP/tj2.wav" "$TMP/tj3.wav" "$OUT/sfx_tiaoqi_jump.wav"

# 13. 动物棋吃子 - 咬合声(噪声+低频 100Hz), 200ms
sox -n "$TMP/dc1.wav" synth 0.2 brownnoise fade q 0.002 0.2 0.05 2>/dev/null
sox -n "$TMP/dc2.wav" synth 0.2 square 100 fade q 0.002 0.2 0.05 2>/dev/null
gen sfx_dongwuqi_capture.wav sox -m "$TMP/dc1.wav" "$TMP/dc2.wav" "$OUT/sfx_dongwuqi_capture.wav" gain -3

# 14. 象棋将军 - 警报(方波 1000Hz 间歇三次)
sox -n "$TMP/xc1.wav" synth 0.12 square 1000 fade q 0.005 0.12 0.02 2>/dev/null
gen sfx_xiangqi_check.wav sox "$TMP/xc1.wav" "$TMP/xc1.wav" "$TMP/xc1.wav" "$OUT/sfx_xiangqi_check.wav"

# 15. 围棋提子 - 落子+消散(正弦 600Hz + 衰减)
gen sfx_weiqi_capture.wav sox -n "$OUT/sfx_weiqi_capture.wav" synth 0.4 sine 600 fade q 0.005 0.4 0.35

# 16. 五子棋连珠 - 闪光(上行 600->1200Hz, 300ms)
gen sfx_wuziqi_connect.wav sox -n "$OUT/sfx_wuziqi_connect.wav" synth 0.3 sine 600-1200 fade q 0.01 0.3 0.08

# ============== 剧情音效 (12) ==============

# 17. 换道过场 - 长渐强正弦 200->800Hz, 1500ms
gen sfx_realm_transition.wav sox -n "$OUT/sfx_realm_transition.wav" synth 1.5 sine 200-800 fade 0.6 1.5 0.3

# 18. 守道者登场 - 低沉鼓声(低频 60Hz + 颤音), 800ms
gen sfx_boss_appear.wav sox -n "$OUT/sfx_boss_appear.wav" synth 0.8 sine 60 tremolo 15 fade q 0.005 0.8 0.3

# 19. Boss技能触发 - 尖锐上扫(方波 800->1600Hz)
gen sfx_boss_skill.wav sox -n "$OUT/sfx_boss_skill.wav" synth 0.4 square 800-1600 fade q 0.005 0.4 0.1

# 20. 守道者消散 - 渐弱正弦 800->100Hz
gen sfx_boss_fade.wav sox -n "$OUT/sfx_boss_fade.wav" synth 0.8 sine 800-100 fade q 0.005 0.8 0.5

# 21. 引路人低语 - 极轻柔正弦 400Hz+回声
gen sfx_guide_whisper.wav sox -n "$OUT/sfx_guide_whisper.wav" synth 0.6 sine 400 echo 0.8 0.7 80 0.3 fade q 0.02 0.6 0.2 gain -10

# 22. 陈默沉默 - 极轻短促(200Hz, 100ms)
gen sfx_chenmo_silent.wav sox -n "$OUT/sfx_chenmo_silent.wav" synth 0.1 sine 200 fade q 0.005 0.1 0.03 gain -15

# 23. 记忆碎片浮现 - 柔和铃声(1200Hz+颤音), 600ms
gen sfx_memory_photo.wav sox -n "$OUT/sfx_memory_photo.wav" synth 0.6 sine 1200 tremolo 15 fade q 0.02 0.6 0.2 gain -5

# 24. 悟道结局高潮 - 钢琴风格和弦 C-E-G-C 持续
sox -n "$TMP/ee1.wav" synth 1.5 sine 523 fade q 0.02 1.5 0.5 2>/dev/null
sox -n "$TMP/ee2.wav" synth 1.5 sine 659 fade q 0.02 1.5 0.5 2>/dev/null
sox -n "$TMP/ee3.wav" synth 1.5 sine 784 fade q 0.02 1.5 0.5 2>/dev/null
sox -n "$TMP/ee4.wav" synth 1.5 sine 1047 fade q 0.02 1.5 0.5 2>/dev/null
gen sfx_ending_enlightenment.wav sox -m "$TMP/ee1.wav" "$TMP/ee2.wav" "$TMP/ee3.wav" "$TMP/ee4.wav" "$OUT/sfx_ending_enlightenment.wav" gain -6

# 25. 堕落结局低沉崩塌 - 低频下扫+噪声
sox -n "$TMP/ec1.wav" synth 1.2 square 200-50 fade q 0.005 1.2 0.4 2>/dev/null
sox -n "$TMP/ec2.wav" synth 1.2 brownnoise fade q 0.005 1.2 0.4 2>/dev/null
gen sfx_ending_corruption.wav sox -m "$TMP/ec1.wav" "$TMP/ec2.wav" "$OUT/sfx_ending_corruption.wav" gain -5

# 26. 轮回结局钟声 - 基频 200Hz+泛音
sox -n "$TMP/es1.wav" synth 1.5 sine 200 fade q 0.005 1.5 1.4 2>/dev/null
sox -n "$TMP/es2.wav" synth 1.5 sine 400 fade q 0.005 1.5 1.4 2>/dev/null
sox -n "$TMP/es3.wav" synth 1.5 sine 600 fade q 0.005 1.5 1.4 2>/dev/null
sox -n "$TMP/es4.wav" synth 1.5 sine 800 fade q 0.005 1.5 1.4 2>/dev/null
gen sfx_ending_samsara.wav sox -m "$TMP/es1.wav" "$TMP/es2.wav" "$TMP/es3.wav" "$TMP/es4.wav" "$OUT/sfx_ending_samsara.wav" gain -6

# 27. 真结局星光汇聚 - 多层上行正弦叠加
sox -n "$TMP/em1.wav" synth 1.2 sine 400-800 fade q 0.02 1.2 0.3 2>/dev/null
sox -n "$TMP/em2.wav" synth 1.2 sine 600-1200 fade q 0.02 1.2 0.3 2>/dev/null
sox -n "$TMP/em3.wav" synth 1.2 sine 800-1600 fade q 0.02 1.2 0.3 2>/dev/null
gen sfx_ending_true_me.wav sox -m "$TMP/em1.wav" "$TMP/em2.wav" "$TMP/em3.wav" "$OUT/sfx_ending_true_me.wav" gain -6

# 28. 第一次作弊 - 神秘音(方波+回声 600Hz)
gen sfx_cheat_first.wav sox -n "$OUT/sfx_cheat_first.wav" synth 0.5 square 600 echo 0.8 0.7 80 0.4 fade q 0.01 0.5 0.2 gain -5

# ============== 清理与汇总 ==============
rm -rf "$TMP"

echo
echo "==> 生成完成"
echo "    成功: $OK / 28"
echo "    失败: $FAIL"
if [ -n "$FAILED_LIST" ]; then
    echo "    失败文件:$FAILED_LIST"
fi
echo
echo "==> 输出文件列表:"
ls -la "$OUT"/

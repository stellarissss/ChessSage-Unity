#!/usr/bin/env python3
"""本地 LLM 桩服务（E3 取证用，零生产依赖）。

用途：无 API key 的沙箱里，把 ChessSage 的 `DEEPSEEK_BASE_URL` 指向本机桩，
让 **真实** 的 LlmClient / AgentEngine / CheatCommandPanel 链路跑起来，
从而截取「处理态 / 顶格警示 / 20s 到点」这些只有真实推演才会出现的 UI 状态。

设计纪律：
  · 只实现 OpenAI 兼容的 `POST /chat/completions`（LlmClient 唯一出口）；
  · 场景由 `STUB_SCENARIO` 选择，响应可带延迟（模拟真实推演耗时）；
  · 不写入任何游戏文件——纯粹的 HTTP 应答器。

场景：
  cap             第 1 轮返回大 usage 的写工具调用（投射触顶 120）→ 第 2 轮延迟 6s 后文字收尾
  timeout_partial 第 1 轮写工具调用 → 第 2 轮延迟 40s（客户端 20s 到点取消）⇒ 半成品预览
  timeout_fail    第 1 轮只读工具 → 第 2 轮延迟 40s ⇒ 到点零写入 ⇒ 失败卡

用法：
  STUB_SCENARIO=cap PORT=8931 python3 tools/verify/llm-stub.py
  DEEPSEEK_BASE_URL=http://127.0.0.1:8931 DEEPSEEK_API_KEY=stub-key bash tools/render-playthrough.sh Cheat
"""

import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ.get("PORT", "8931"))
SCENARIO = os.environ.get("STUB_SCENARIO", "cap")

_counter_lock = threading.Lock()
_counter = {"n": 0}


def _next_index():
    with _counter_lock:
        _counter["n"] += 1
        return _counter["n"]


PATCH_HORSE = {
    "operations": [
        {
            "target": "pieces_red",
            "patch": [
                {
                    "op": "add",
                    "path": "/pieces/horse/moves/-",
                    "value": {"kind": "jump", "to": [1, 0], "block": [], "land": "any", "sym": "rotate4"},
                }
            ],
        }
    ]
}


def _tool_response(name, arguments, prompt, completion, index, reasoning=0, cached=0):
    return {
        "id": "chatcmpl-stub",
        "object": "chat.completion",
        "model": "deepseek-flash",
        "choices": [
            {
                "index": 0,
                "finish_reason": "tool_calls",
                "message": {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [
                        {
                            "id": f"call_{index}",
                            "type": "function",
                            "function": {"name": name, "arguments": json.dumps(arguments, ensure_ascii=False)},
                        }
                    ],
                },
            }
        ],
        "usage": {
            "prompt_tokens": prompt,
            "completion_tokens": completion,
            "completion_tokens_details": {"reasoning_tokens": reasoning},
            "prompt_tokens_details": {"cached_tokens": cached},
        },
    }


def _text_response(text, prompt, completion):
    return {
        "id": "chatcmpl-stub",
        "object": "chat.completion",
        "model": "deepseek-flash",
        "choices": [
            {
                "index": 0,
                "finish_reason": "stop",
                "message": {"role": "assistant", "content": text},
            }
        ],
        "usage": {"prompt_tokens": prompt, "completion_tokens": completion},
    }


def build_response(index):
    """按场景与轮次序产出响应（延迟在调用方 sleep）。"""
    if SCENARIO == "cap":
        if index == 1:
            # T_eff = 40000+8000 = 48000 ⇒ f ≈ 91 + 27000×0.0072 ≈ 285 ⇒ 投射 ≥ 120（顶格警示）
            return _tool_response("apply_config_patch", PATCH_HORSE, 40000, 8000, index, reasoning=3000, cached=1200)
        return _text_response("大手笔改完了：马可以横着走。", 6000, 300)

    if SCENARIO == "timeout_partial":
        if index == 1:
            return _tool_response("apply_config_patch", PATCH_HORSE, 1600, 400, index, cached=600)
        return _text_response("（这一轮永远到不了：客户端会先到点）", 2000, 100)

    if SCENARIO == "timeout_fail":
        if index == 1:
            return _tool_response("get_board_state", {"include": ["pieces"]}, 1500, 300, index, cached=600)
        return _text_response("（同上，零写入路径）", 2000, 100)

    # 默认：正常单轮闲聊
    return _text_response("天道听着呢。", 900, 80)


def delay_for(index):
    if SCENARIO == "cap":
        return 0.2 if index == 1 else 8.0      # 第 1 轮秒回（出顶格警示），第 2 轮慢慢收尾 → 留出截图窗口
    if SCENARIO in ("timeout_partial", "timeout_fail"):
        return 0.2 if index == 1 else 40.0     # 第 2 轮必然越过 20s 硬顶
    return 0.3


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self):  # noqa: N802（http.server 约定）
        length = int(self.headers.get("Content-Length", "0") or 0)
        _ = self.rfile.read(length)  # 协议保真：吃掉请求体（内容不参与应答）
        index = _next_index()
        time.sleep(delay_for(index))
        body = json.dumps(build_response(index), ensure_ascii=False).encode("utf-8")
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass  # 客户端已 20s 取消

    def log_message(self, fmt, *args):  # 安静一点：只留一行简洁日志
        sys.stderr.write("[stub] " + fmt % args + "\n")


if __name__ == "__main__":
    sys.stderr.write(f"[stub] 监听 127.0.0.1:{PORT} 场景={SCENARIO}\n")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()

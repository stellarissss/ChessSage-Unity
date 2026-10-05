#!/usr/bin/env python3
"""轻量开发服务器：仅用于在沙盒中验证 iso-engine 大地图。
仅供开发调测，不属于成品代码。端口默认 8099。"""
import json
import mimetypes
import os
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent            # hub/
CONFIGS = ROOT.parent / "configs"                  # configs/
WORKSPACE = ROOT.parent

def json_resp(handler, payload, status=200):
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Cache-Control", "no-store")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)

class Handler(BaseHTTPRequestHandler):
    def _serve_file(self, path: Path):
        if not path.exists() or not path.is_file():
            self.send_error(404)
            return
        body = path.read_bytes()
        ctype = mimetypes.guess_type(str(path))[0] or "application/octet-stream"
        self.send_response(200)
        self.send_header("Content-Type", ctype + ("; charset=utf-8" if ctype.startswith("text/") else ""))
        # 开发调测：禁用一切缓存，避免浏览器/CDP 复用旧 JS（改源码后必须生效）
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")
        self.send_header("Expires", "0")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        base = parsed.path
        if base == "/api/overworld/config":
            json_resp(self, json.loads((CONFIGS / "overworld.json").read_text(encoding="utf-8")))
            return
        if base == "/api/games":
            json_resp(self, [])
            return
        if base == "/samsara/api/state":
            json_resp(self, {})
            return
        if base in ("/", "/overworld", "/hub"):
            self._serve_file(ROOT / "overworld.html")
            return
        if base == "/tutorial":
            # 独立玩法教程页（与 main.py 的 /tutorial 路由对齐）
            self._serve_file(ROOT / "tutorial.html")
            return
        if base.startswith("/static/"):
            rel = base[len("/static/"):]
            self._serve_file((ROOT / rel).resolve())
            return
        self.send_error(404)

def main():
    port = int(os.environ.get("DEV_PORT", 8099))
    srv = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    print(f"_dev_server listening on {port}", flush=True)
    srv.serve_forever()

if __name__ == "__main__":
    main()
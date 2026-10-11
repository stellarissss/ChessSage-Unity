#!/usr/bin/env python3
"""
通过 GitHub REST Git Data API 推送提交。

背景：本环境到 github.com 的 git 协议（smart HTTP）被阻断——`git push` 静默失败、
返回 0 但远端未更新；而第三方镜像站是只读代理，无法写入。
但 `api.github.com` 可用，故改用 REST 流程：

    Blob (逐文件) → Tree → Commit → Ref 更新

用法：
    GH_TOKEN=<personal_access_token> python3 tools/push_via_rest.py
"""
import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
REPO = os.environ.get("GH_REPO", "stellarissss/ChessSage-Unity")
BRANCH = os.environ.get("GH_BRANCH", "main")
TOKEN = os.environ.get("GH_TOKEN")
if not TOKEN:
    sys.exit("缺少环境变量 GH_TOKEN")

DRY = os.environ.get("DRY", "") == "1"


def api(method, path, payload=None):
    """调用 GitHub API，返回解析后的 JSON。"""
    url = path if path.startswith("http") else f"{API}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {TOKEN}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "chesssage-push")
    if data:
        req.add_header("Content-Type", "application/json")

    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=90) as resp:
                body = resp.read().decode()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            detail = e.read().decode()[:400]
            # 5xx 可重试；4xx 直接失败（重试无意义）
            if e.code < 500:
                sys.exit(f"API {method} {url} 失败 {e.code}: {detail}")
            if attempt == 4:
                sys.exit(f"API {method} {url} 重试耗尽: {detail}")
            time.sleep(2 ** attempt)
        except Exception as e:  # 网络抖动
            if attempt == 4:
                sys.exit(f"API {method} {url} 网络错误: {e}")
            time.sleep(2 ** attempt)


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout.strip()


def upload_blobs(files):
    """把 {path: bytes} 上传为 Git blobs，返回 {path: sha}。"""
    shas = {}
    for i, (path, data) in enumerate(files.items(), 1):
        print(f"  [{i}/{len(files)}] {path}", flush=True)
        blob = api("POST", f"/repos/{REPO}/git/blobs",
                   {"content": base64.b64encode(data).decode(), "encoding": "base64"})
        shas[path] = blob["sha"]
    return shas


def main():
    # 1) 本地相对远端领先的提交
    local = git("rev-parse", "HEAD")
    remote = api("GET", f"/repos/{REPO}/git/ref/heads/{BRANCH}")["object"]["sha"]
    print(f"本地 HEAD : {local}")
    print(f"远端 {BRANCH} : {remote}")

    if local == remote:
        print("远端已是最新，无需推送。")
        return

    base_tree = api("GET", f"/repos/{REPO}/git/commits/{remote}")["tree"]["sha"]

    # 2) 收集要推送的文件（复用 git 的 NUL 分隔输出，正确处理特殊字符名）
    changed = git("diff", "--name-only", "-z", f"{remote}..{local}").split("\0")
    changed = [p for p in changed if p]

    # 需要重建的 blob：本次推送涉及的全部文件
    files = {}
    all_paths = [p for p in git("ls-tree", "-r", "--name-only", "-z", local).split("\0") if p]
    text_files = set(git("diff", "--name-only", "-z", "--diff-filter=ACMRT",
                         f"{remote}..{local}").split("\0")) - {""}

    for p in all_paths:
        if p not in text_files:
            continue
        with open(p, "rb") as f:
            files[p] = f.read()

    # 被删除的文件
    deleted = [p for p in git("diff", "--name-only", "-z", "--diff-filter=D",
                              f"{remote}..{local}").split("\0") if p]

    print(f"待推送 {len(changed)} 个变更文件，其中 {len(files)} 个走 blob 上传"
          + (f"，删除 {len(deleted)} 个" if deleted else ""))
    if DRY:
        print("DRY 模式，停止。")
        return

    shas = upload_blobs(files)

    # 3) 建 tree：以远端 tree 为基底做增量
    tree_entries = []
    for path, sha in shas.items():
        mode = "100755" if os.access(path, os.X_OK) or path.endswith(".sh") else "100644"
        tree_entries.append({"path": path, "mode": mode, "type": "blob", "sha": sha})
    # 删除用全零 sha 表示
    null_sha = "0" * 40
    for p in deleted:
        tree_entries.append({"path": p, "mode": "100644", "type": "blob", "sha": null_sha})

    tree = api("POST", f"/repos/{REPO}/git/trees", {"base_tree": base_tree, "tree": tree_entries})
    print(f"tree: {tree['sha']}")

    # 4) 逐个重建本地领先的所有提交，保持提交历史与 message 完整。
    #    GitHub API 的 CreateCommit 以 parent 的 tree 为基底做增量，
    #    因此每个中间提交都必须有对应的 tree，只能按序重建。
    ahead = git("rev-list", "--reverse", f"{remote}..{local}").split()
    print(f"待重建提交 {len(ahead)} 个")

    # local_parent 用于本地 diff（必须是本地存在的 sha）
    # remote_parent 用于 API 的 parent 链（是 GitHub 上的 sha）
    local_parent = remote
    remote_parent = remote
    for sha in ahead:
        raw = git("show", "-s", "--pretty=format:%B", sha)
        an = git("show", "-s", "--pretty=format:%an", sha)
        ae = git("show", "-s", "--pretty=format:%ae", sha)
        ad = git("show", "-s", "--pretty=format:%aI", sha)

        # 该提交的 tree 需基于上一提交的 tree 做增量
        parent_tree = api("GET", f"/repos/{REPO}/git/commits/{remote_parent}")["tree"]["sha"]
        entries = []
        sub_files = [p for p in git("diff", "--name-only", "-z",
                                    f"{local_parent}..{sha}").split("\0") if p]
        sub_deleted = [p for p in git("diff", "--name-only", "-z", "--diff-filter=D",
                                      f"{local_parent}..{sha}").split("\0") if p]
        # 复用已上传的 blob；本提交内新增的文件需补上传
        for p in sub_files:
            if p in sub_deleted:
                entries.append({"path": p, "mode": "100644", "type": "blob", "sha": null_sha})
                continue
            if p in shas:
                s = shas[p]
            else:
                with open(p, "rb") as f:
                    s = api("POST", f"/repos/{REPO}/git/blobs",
                            {"content": base64.b64encode(f.read()).decode(),
                             "encoding": "base64"})["sha"]
                shas[p] = s
            mode = "100755" if p.endswith(".sh") or os.access(p, os.X_OK) else "100644"
            entries.append({"path": p, "mode": mode, "type": "blob", "sha": s})

        t = api("POST", f"/repos/{REPO}/git/trees", {"base_tree": parent_tree, "tree": entries})
        c = api("POST", f"/repos/{REPO}/git/commits", {
            "message": raw.strip(),
            "tree": t["sha"],
            "parents": [remote_parent],
            "author": {"name": an, "email": ae, "date": ad},
        })
        print(f"  commit {sha[:7]} -> {c['sha'][:7]}  {raw.strip().splitlines()[0][:50]}")
        local_parent = sha
        remote_parent = c["sha"]

    # 5) 更新 ref
    api("PATCH", f"/repos/{REPO}/git/refs/heads/{BRANCH}",
        {"sha": remote_parent, "force": False})
    print(f"✅ 推送完成：{REPO}@{BRANCH} = {remote_parent[:7]}（{len(ahead)} 个提交）")


if __name__ == "__main__":
    main()
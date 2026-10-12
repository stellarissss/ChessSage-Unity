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

    # 重试策略：GitHub 的大响应体（tree?recursive=1 可达数百 KB）在本机网络上
    # 较易触发 IncompleteRead / 连接重置，且这类失败具有瞬时性。
    # 因此给足重试次数（12次）并使用更长的退避（上限 20s），总容忍时间约 2 分钟。
    for attempt in range(12):
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                body = resp.read().decode()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            detail = e.read().decode()[:400]
            # 401/403：令牌本身已用 curl 验证有效，此处多为 GitHub 的
            # 瞬时凭据拒绝 / 二级速率限制（推送涉及数百次 blob POST，易触发）。
            # 故与 5xx 同样走退避重试，而不是立即放弃。
            retryable = e.code >= 500 or e.code in (401, 403)
            if not retryable:
                sys.exit(f"API {method} {url} 失败 {e.code}: {detail}")
            if attempt == 11:
                sys.exit(f"API {method} {url} 重试耗尽: {detail}")
            wait = min(2 ** attempt, 20)
            print(f"  [重试 {attempt + 1}/11] {method} {url.split('?')[0].split('/')[-1]} "
                  f"HTTP {e.code}，{wait}s 后重试", flush=True)
            time.sleep(wait)
        except Exception as e:  # 网络抖动 / 大响应体传输中断
            if attempt == 11:
                sys.exit(f"API {method} {url} 网络错误: {e}")
            wait = min(2 ** attempt, 20)
            print(f"  [重试 {attempt + 1}/11] {method} {url.split('?')[0].split('/')[-1]} "
                  f"传输中断（{type(e).__name__}），{wait}s 后重试", flush=True)
            time.sleep(wait)


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout.strip()


def remote_path_exists(commit_sha, path):
    """指定提交（的 tree）里是否存在该路径。

    用 Contents API 查单个对象，而不是拉整棵 tree?recursive=1——
    本仓库 Assets 下文件众多，递归 tree 响应体可达数百 KB，
    在不稳定网络上极易触发 IncompleteRead（实测多次复现）。
    """
    try:
        api("GET", f"/repos/{REPO}/contents/{path}?ref={commit_sha}")
        return True
    except SystemExit:
        return False        # 4xx（不存在）已被 api() 转为 SystemExit


def upload_blobs(files):
    """把 {path: bytes} 上传为 Git blobs，返回 {path: sha}。"""
    shas = {}
    for i, (path, data) in enumerate(files.items(), 1):
        print(f"  [{i}/{len(files)}] {path}", flush=True)
        blob = api("POST", f"/repos/{REPO}/git/blobs",
                   {"content": base64.b64encode(data).decode(), "encoding": "base64"})
        shas[path] = blob["sha"]
    return shas


def local_snapshot(sha):
    """本地提交的内容快照：{路径: blob sha}。"""
    out = subprocess.run(["git", "ls-tree", "-r", "-z", sha],
                         capture_output=True, check=True).stdout.decode("utf-8", "surrogateescape")
    snap = {}
    for rec in out.split("\0"):
        if not rec:
            continue
        meta, path = rec.split("\t", 1)
        snap[path] = meta.split()[2]
    return snap


def remote_snapshot(commit_sha):
    """远端提交的内容快照：{路径: blob sha}。

    注意：不能用 tree sha 做等价判断——本脚本用 base_tree 增量拼树，
    GitHub 生成的 tree sha 与本地同内容的 tree sha 并不相等
    （条目顺序/嵌套结构由服务端决定）。只有逐文件的 blob sha 才可比。
    """
    data = api("GET", f"/repos/{REPO}/git/trees/{commit_sha}?recursive=1")
    return {e["path"]: e["sha"] for e in data.get("tree", []) if e["type"] == "blob"}


def resolve_remote_base(remote_sha):
    """把 GitHub 侧的 sha 映射到本地可用的基准提交（远端已达状态）。

    本脚本每次推送都用 CreateCommit 重建提交，产出的 sha 与本地 sha 不同
    （内容、author、日期相同，但 tree 由服务端增量拼出、父链也不同）。
    因此远端 HEAD 的 sha 在本地并不存在，`rev-list remote..HEAD` 会退化成
    "把整条本地历史都算成待推送"——实测导致 M1/L3/M5 各被重复推送一遍，
    main 链上出现两段内容相同的提交。

    解决办法：比较**内容快照**，在本地历史里找出与远端内容完全一致的
    最新提交；它就是"远端已达状态"在本地对应的提交。返回 (本地基准, 待推送提交列表)。
    """
    want = remote_snapshot(remote_sha)
    for sha in git("rev-list", "HEAD").split():
        if local_snapshot(sha) == want:
            return sha
    return None


def main():
    # 1) 本地相对远端领先的提交
    local = git("rev-parse", "HEAD")
    remote = api("GET", f"/repos/{REPO}/git/ref/heads/{BRANCH}")["object"]["sha"]
    print(f"本地 HEAD : {local}")
    print(f"远端 {BRANCH} : {remote}")

    if local == remote:
        print("远端已是最新，无需推送。")
        return

    # 远端 HEAD 对应的本地等价提交（本地链路里 tree 相同的那个）
    base = resolve_remote_base(remote)
    if base is not None:
        ahead = git("rev-list", "--reverse", f"{base}..{local}").split()
        print(f"远端等价于本地 {base[:7]}，待推送 {len(ahead)} 个提交")
        if not ahead:
            print("远端内容与本地一致（sha 不同但树相同），无需推送。")
            return
    else:
        # 远端存在本地没有的历史（如历史被重写）：退回全量对齐，并明确告警。
        base = local
        ahead = [local]
        print("⚠️  本地历史中找不到与远端 tree 相同的提交，按全量对齐处理")

    base_tree = api("GET", f"/repos/{REPO}/git/commits/{remote}")["tree"]["sha"]

    # 2) 收集要推送的文件（复用 git 的 NUL 分隔输出，正确处理特殊字符名）
    #    diff 基准用 base（本地 sha）→ local，而不是 remote（GitHub 侧 sha）。
    changed = git("diff", "--name-only", "-z", f"{base}..{local}").split("\0")
    changed = [p for p in changed if p]

    # 需要重建的 blob：本次推送涉及的全部文件
    files = {}
    all_paths = [p for p in git("ls-tree", "-r", "--name-only", "-z", local).split("\0") if p]
    text_files = set(git("diff", "--name-only", "-z", "--diff-filter=ACMRT",
                         f"{base}..{local}").split("\0")) - {""}
    text_files.update(changed)      # 新增文件（A）含在 changed 里，确保不漏

    for p in all_paths:
        if p not in text_files:
            continue
        with open(p, "rb") as f:
            files[p] = f.read()

    # 被删除的文件。
    # 必须加 --no-renames：git diff 默认开启重命名检测（-M），把
    # "Fonts/x.otf → Resources/x.otf" 报成 R100 而非 D，于是 --diff-filter=D
    # 返回空集，远端旧路径永远删不掉（表现为 200 而非 404）。
    # 逐提交重建时同理，见下方 sub_deleted。
    deleted = [p for p in git("diff", "--no-renames", "--name-only", "-z", "--diff-filter=D",
                              f"{base}..{local}").split("\0") if p]

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
    # 真删除用 "sha": null 表示（全零 sha 会被 GitHub 当成非法 blob 而 422）。
    # 同时先确认远端确实有该路径——对远端本就不存在的路径发 null 同样会被拒。
    deleted_emitted = 0
    for p in deleted:
        if remote_path_exists(remote, p):
            tree_entries.append({"path": p, "mode": "100644", "type": "blob", "sha": None})
            deleted_emitted += 1
    if deleted:
        print(f"  实际发出删除 {deleted_emitted}/{len(deleted)} 个")

    tree = api("POST", f"/repos/{REPO}/git/trees", {"base_tree": base_tree, "tree": tree_entries})
    print(f"tree: {tree['sha']}")

    # 4) 逐个重建本地领先的所有提交，保持提交历史与 message 完整。
    #    GitHub API 的 CreateCommit 以 parent 的 tree 为基底做增量，
    #    因此每个中间提交都必须有对应的 tree，只能按序重建。
    #    ahead 已在第 1 步按「本地等价基准」算好，此处不再重算——
    #    否则会退回用 GitHub 侧 sha 做 rev-list，把整条历史重复推一遍。
    print(f"待重建提交 {len(ahead)} 个")

    # local_parent 用于本地 diff（必须是本地存在的 sha）
    # remote_parent 用于 API 的 parent 链（是 GitHub 上的 sha）
    local_parent = base
    remote_parent = remote
    reused = 0
    for sha in ahead:
        raw = git("show", "-s", "--pretty=format:%B", sha)

        # 幂等短路：若远端父提交里已存在 tree 完全相同的提交（说明本提交
        # 上次已推送成功，只是本地 sha 与 GitHub 侧重建出的 sha 不同），
        # 直接复用那个远端提交，不再重建。
        #
        # 为什么必须这么做：本脚本每次推送都会用 CreateCommit 重建提交，
        # 产出的 sha 与本地 sha 不一致（内容同、author 同，但树/父链不同）。
        # 于是下次运行时 `remote..local` 会把远端已存在的这批提交
        # 再次判定为"本地领先"，导致历史被整体重复一遍
        # （实测：main 链上出现两段内容相同的 M1/L3/M5 提交）。
        # 用 tree sha 做等价判断可彻底消除重复。
        if raw.strip():
            local_tree = git("rev-parse", f"{sha}^{{tree}}")
            remote_tree = api("GET", f"/repos/{REPO}/git/commits/{remote_parent}")["tree"]["sha"]
            if local_tree == remote_tree:
                print(f"  commit {sha[:7]} == 复用远端 {remote_parent[:7]}（树相同，跳过重建）")
                local_parent = sha
                reused += 1
                continue

        an = git("show", "-s", "--pretty=format:%an", sha)
        ae = git("show", "-s", "--pretty=format:%ae", sha)
        ad = git("show", "-s", "--pretty=format:%aI", sha)

        # 该提交的 tree 需基于上一提交的 tree 做增量
        parent_tree = api("GET", f"/repos/{REPO}/git/commits/{remote_parent}")["tree"]["sha"]
        entries = []
        # 重要：这里必须用本地 sha 做 diff，但本地 parent 链与 GitHub 上的 parent 链
        # 未必一一对应（历史上有过「本地重写提交」），因此不能仅凭 diff 结果判断删除。
        # GitHub CreateTree 的 base_tree 语义下，**不列出某路径即保持原样**，
        # 而删除必须用 "sha": null（不是全零 sha，全零会被当成非法 blob 而 422）。
        # 因此：先算出本提交相对 local_parent 的删除集合，再用「远端 tree 里是否
        # 真的存在该路径」做二次确认，只有确实存在才发 null。
        sub_files = [p for p in git("diff", "--no-renames", "--name-only", "-z",
                                    f"{local_parent}..{sha}").split("\0") if p]
        sub_deleted = set(p for p in git("diff", "--no-renames", "--name-only", "-z", "--diff-filter=D",
                                         f"{local_parent}..{sha}").split("\0") if p)

        def upload_blob(path):
            with open(path, "rb") as fh:
                return api("POST", f"/repos/{REPO}/git/blobs",
                           {"content": base64.b64encode(fh.read()).decode(),
                            "encoding": "base64"})["sha"]

        # 复用已上传的 blob；本提交内新增的文件需补上传
        for p in sub_files:
            if p in sub_deleted:
                # 本提交里该路径是「删除」。两种情形要分清：
                #   (a) 远端确实有它 → 发 "sha": null 表示删除（如 Fonts/→Resources/ 的重命名，
                #       旧路径即是删除项）；
                #   (b) 远端也没有它 → 本地与远端都不存在，无从上传也无需删除，直接跳过。
                # 早期实现把 (b) 当成「diff 误判、按新增处理」去 open()，而重命名后的
                # 旧路径本地已不存在，于是抛 FileNotFoundError 中断整次推送。
                if remote_path_exists(remote_parent, p):
                    entries.append({"path": p, "mode": "100644", "type": "blob", "sha": None})
                continue

            if p in shas:
                s = shas[p]
            elif os.path.exists(p):
                s = upload_blob(p)
                shas[p] = s
            else:
                # 既不在本次变更的"新增/修改"语义里，本地又不存在：
                # 属 diff 与工作树不一致的异常情况，跳过并告警，不让整次推送失败。
                print(f"  ⚠️  跳过不存在的路径：{p}", flush=True)
                continue
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
    print(f"✅ 推送完成：{REPO}@{BRANCH} = {remote_parent[:7]}"
          f"（重建 {len(ahead) - reused} 个提交，复用 {reused} 个）")


if __name__ == "__main__":
    main()
#!/usr/bin/env python3
"""E3 校准 JSONL 分析器（REMAKE-IMPL-E3B）。

用法：
  python3 tools/verify/e3/analyze.py Logs/e3/raw/calib-*.jsonl
  python3 tools/verify/e3/analyze.py --md Logs/e3/raw/calib-*.jsonl > table.md

只依赖标准库；P50/P90 用 nearest-rank（小样本下最诚实，不做插值）。
"""
import argparse
import glob
import json
import statistics
import sys


def pct(values, p):
    if not values:
        return 0
    xs = sorted(values)
    k = max(0, min(len(xs) - 1, int(round((p / 100.0) * (len(xs) - 1)))))
    return xs[k]


def load(paths):
    rows = []
    for pattern in paths:
        for path in glob.glob(pattern):
            with open(path, encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if line:
                        rows.append(json.loads(line))
    return rows


def stats(rows):
    n = len(rows)
    ok = [r for r in rows if r.get("success")]
    totals = [r.get("total_ms", 0) for r in rows]
    ttfts = [r.get("ttft_ms", 0) for r in rows]
    teff = [r.get("token_eff", 0) for r in ok] or [0]
    karma = [r.get("karma", 0) for r in ok] or [0]
    rounds = [r.get("rounds", 0) for r in rows]
    cached2 = [1 for r in rows if r.get("round2_cached", 0) > 0]
    fails = {}
    for r in rows:
        if not r.get("success"):
            key = r.get("error_code") or r.get("result_type") or "unknown"
            fails[key] = fails.get(key, 0) + 1
    scopes = {}
    for r in ok:
        key = r.get("scope") or "—"
        scopes[key] = scopes.get(key, 0) + 1
    return {
        "n": n,
        "ok": len(ok),
        "ok_rate": len(ok) / n if n else 0,
        "total_p50": pct(totals, 50),
        "total_p90": pct(totals, 90),
        "total_max": max(totals) if totals else 0,
        "ttft_p50": pct(ttfts, 50),
        "ttft_p90": pct(ttfts, 90),
        "teff_p50": pct(teff, 50),
        "teff_p90": pct(teff, 90),
        "karma_p50": pct(karma, 50),
        "karma_p90": pct(karma, 90),
        "rounds_p50": pct(rounds, 50),
        "cached2_rate": len(cached2) / n if n else 0,
        "timed_out": sum(1 for r in rows if r.get("timed_out")),
        "cap": sum(1 for r in rows if r.get("cap_warned")),
        "fails": fails,
        "scopes": scopes,
    }


TIER_ORDER = ["E", "D", "B", "A", "C", "C+", "MULTI"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--md", action="store_true")
    args = ap.parse_args()
    rows = load(args.paths)
    by_tier = {}
    for r in rows:
        by_tier.setdefault(r.get("tier", "?"), []).append(r)

    tiers = [t for t in TIER_ORDER if t in by_tier] + [t for t in by_tier if t not in TIER_ORDER]
    if args.md:
        print("| 档 | n | 成功 | P50 total(ms) | P90 total(ms) | P50 TTFT | P50 T_eff | P50 karma | 作用域 | P50 rounds | 第2轮命中率 | 到点 | 顶格 | 失败码 |")
        print("|:--:|:--:|:--:|--:|--:|--:|--:|--:|:--|:--:|--:|--:|--:|:--|")
        for t in tiers:
            s = stats(by_tier[t])
            fails = ", ".join(f"{k}×{v}" for k, v in sorted(s["fails"].items())) or "—"
            scopes = ", ".join(f"{k}×{v}" for k, v in sorted(s["scopes"].items())) or "—"
            print(f"| {t} | {s['n']} | {s['ok']}/{s['n']} | {s['total_p50']} | {s['total_p90']} | "
                  f"{s['ttft_p50']} | {s['teff_p50']} | {s['karma_p50']} | {scopes} | {s['rounds_p50']} | "
                  f"{s['cached2_rate']*100:.0f}% | {s['timed_out']} | {s['cap']} | {fails} |")
    else:
        for t in tiers:
            s = stats(by_tier[t])
            print(f"{t}: {json.dumps(s, ensure_ascii=False)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

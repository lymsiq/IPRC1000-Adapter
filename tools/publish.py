#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
一条命令发包：推送 → 建 Release → 传 5 个附件 → 自检。

为什么单独写脚本：发包要 30+ 次 HTTPS 往返，中间任何一次网络抖动都会半途而废
（2026-10-28 就因为代理挂掉重试了 4 次）。把它做成幂等脚本，断了重跑即可 —— 
已上传的附件会跳过，Release 已存在则复用。

用法：
    python tools/publish.py --version 1.2.81 --notes _rel_body.md
    python tools/publish.py --version 1.2.81 --notes _rel_body.md --dry-run
"""
import argparse
import hashlib
import io
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = "lymsiq/IPRC1000-Adapter"


def token() -> str:
    out = subprocess.run(["git", "credential", "fill"], cwd=ROOT, input="protocol=https\nhost=github.com\n\n",
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    for line in (out.stdout or "").splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    raise SystemExit("✗ 从 git credential 拿不到 token")


def api(path: str, tok: str, method="GET", data=None, raw=None, ctype="application/json", retries=4):
    url = path if path.startswith("https://") else f"https://api.github.com/repos/{REPO}{path}"
    for i in range(retries):
        try:
            req = urllib.request.Request(url, method=method)
            req.add_header("Authorization", f"token {tok}")
            req.add_header("Accept", "application/vnd.github+json")
            body = None
            if raw is not None:
                body = raw
                req.add_header("Content-Type", ctype)
            elif data is not None:
                body = json.dumps(data, ensure_ascii=False).encode()
                req.add_header("Content-Type", "application/json")
            with urllib.request.urlopen(req, body, timeout=180) as r:
                payload = r.read()
                return r.status, (json.loads(payload) if payload else None)
        except urllib.error.HTTPError as e:
            body = e.read()[:300].decode("utf-8", "replace")
            # 404 在 GET 上就是"不存在"，不是网络错误 —— 别重试 4 次刷屏
            if e.code == 404:
                return 404, None
            if e.code in (401, 403, 422):
                return e.code, {"message": body}
            print(f"    HTTP {e.code}（第 {i+1} 次）：{body[:120]}")
        except Exception as e:
            print(f"    网络异常（第 {i+1} 次）：{type(e).__name__} {str(e)[:90]}")
        time.sleep(3 * (i + 1))
    return 0, None


def assets_for(ver: str):
    names = [f"IPRC1000_Adapter.zip", f"IPRC1000_Adapter_v{ver}.zip",
             f"IPRC1000_Adapter_full.zip", f"IPRC1000_Adapter_full_v{ver}.zip",
             f"IPRC1000_Setup_v{ver}.exe"]
    return [(n, os.path.join(ROOT, n)) for n in names]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--notes", default="_rel_body.md")
    ap.add_argument("--name", default=None)
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    ver, tag = a.version, "v" + a.version

    items = assets_for(ver)
    missing = [n for n, p in items if not os.path.isfile(p)]
    print(f"=== 发包 {tag} ===")
    print(f"  附件 {len(items)} 个：")
    for n, p in items:
        ok = os.path.isfile(p)
        sz = os.path.getsize(p) / 1048576 if ok else 0
        print(f"    {'✓' if ok else '✗'} {n:<40} {sz:>6.1f} MB")
    if missing:
        print(f"  ✗ 缺少：{', '.join(missing)}")
        return 1
    if a.dry_run:
        print("  （dry-run，未做任何网络操作）")
        return 0

    head = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    print(f"  本地 HEAD {head}")

    print("\n[1/4] 推送…")
    for i in range(5):
        r = subprocess.run(["git", "-c", "http.sslBackend=openssl", "push", "origin", "main"],
                           cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
        out = (r.stdout or "") + (r.stderr or "")
        if "main -> main" in out or "up-to-date" in out:
            print("  ✓ 已推送")
            break
        print(f"  第 {i+1} 次失败：{out.strip().splitlines()[-1][:90] if out.strip() else '?'}")
        time.sleep(15)
    else:
        print("  ✗ 推送失败（网络），稍后重跑本脚本即可")
        return 1

    tok = token()

    print("\n[2/4] 建 Release…")
    notes = io.open(os.path.join(ROOT, a.notes), encoding="utf-8").read()
    st, rel = api(f"/releases/tags/{tag}", tok)
    if st == 200 and rel:
        print(f"  ✓ 已存在（id {rel['id']}），复用")
    else:
        st, rel = api("/releases", tok, "POST",
                      {"tag_name": tag, "name": a.name or f"{tag}", "body": notes,
                       "draft": False, "prerelease": False})
        if not rel or "id" not in rel:
            print(f"  ✗ 建 Release 失败：{st}")
            return 1
        print(f"  ✓ 已建（id {rel['id']}）")

    print("\n[3/4] 传附件…")
    st, have = api(f"/releases/{rel['id']}/assets", tok)
    existing = {x["name"] for x in (have or []) if x.get("state") == "uploaded"}
    for n, p in items:
        if n in existing:
            print(f"  - {n}（已存在，跳过）")
            continue
        with open(p, "rb") as f:
            raw = f.read()
        ctype = "application/octet-stream"
        st, got = api(f"https://uploads.github.com/repos/{REPO}/releases/{rel['id']}/assets?name={urllib.parse.quote(n)}",
                      tok, "POST", raw=raw, ctype=ctype, retries=3)
        ok = bool(got and got.get("state") == "uploaded")
        print(f"  {'✓' if ok else '✗'} {n}")
        if not ok:
            return 1

    print("\n[4/4] 自检…")
    st, rel2 = api(f"/releases/tags/{tag}", tok)
    st2, latest = api("/releases/latest", tok)
    n_up = sum(1 for x in (rel2 or {}).get("assets", []) if x.get("state") == "uploaded")
    print(f"  附件 {n_up}/{len(items)} uploaded")
    print(f"  latest = {(latest or {}).get('tag_name')}")
    print(f"  https://github.com/{REPO}/releases/tag/{tag}")
    return 0 if n_up == len(items) and (latest or {}).get("tag_name") == tag else 1


if __name__ == "__main__":
    sys.exit(main())

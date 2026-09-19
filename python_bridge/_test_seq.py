# -*- coding: utf-8 -*-
"""临时脚本：测量连续多个桥接请求的耗时，复现 workflow 场景。"""
import json
import subprocess
import sys
import time
from pathlib import Path

exe = r"D:\anaconda\python.exe"
script = str(Path(r"D:\agent\python_bridge\bridge.py"))

procs = []
p = subprocess.Popen(
    [exe, "-X", "utf8", script, "--preload"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding="utf-8", bufsize=1,
)
# 等待 ready
line = p.stdout.readline()
print("ready:", line.strip()[:80])

def call(req_id, method, params=None, timeout=30):
    payload = json.dumps({"id": req_id, "method": method, "params": params or {}}, ensure_ascii=False)
    t0 = time.time()
    p.stdin.write(payload + "\n")
    p.stdin.flush()
    p.stdin.flush()
    while True:
        if time.time() - t0 > timeout:
            print(f"[{req_id}] {method} TIMEOUT after {timeout}s")
            return
        import select
        # 非阻塞读不可行，直接 readline（阻塞）——换用 poll
        break
    # 使用线程读超时
    import threading
    result = {}
    def _read():
        result["line"] = p.stdout.readline()
    th = threading.Thread(target=_read, daemon=True)
    th.start()
    th.join(timeout)
    if th.is_alive():
        print(f"[{req_id}] {method} TIMEOUT after {timeout}s")
        # 清掉缓冲
        return None
    line = result.get("line", "")
    dt = time.time() - t0
    print(f"[{req_id}] {method} OK in {dt:.1f}s -> {line.strip()[:100]}")
    return line

call("1", "get_session_stats", {"session_id": "50603346514@chatroom", "limit": 50})
call("2", "list_sessions", {"limit": 5})
call("3", "get_session_stats", {"session_id": "50603346514@chatroom", "limit": 50})
call("4", "list_sessions", {"limit": 5})
call("5", "get_session_stats", {"session_id": "50603346514@chatroom", "limit": 50})

p.stdin.write('{"id":"9","method":"/exit"}\n')
p.stdin.flush()
p.kill()

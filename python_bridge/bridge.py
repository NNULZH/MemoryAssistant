# -*- coding: utf-8 -*-
"""
Memory Assistant Python Bridge
==============================
通过 stdin/stdout + JSON Lines 暴露 wxchat SDK 给 C# 侧。

协议：
  请求  {"id": "<unique>", "method": "<name>", "params": {...}}
  响应  {"id": "<same>", "success": true, "data": ...}
        或 {"id": "<same>", "success": false, "error": "..."}

约定：
  - stdout 只输出 JSON Lines（日志一律走 stderr）
  - Python 异常必须以 success=false 返回，不允许让 C# 侧崩溃
  - 支持 /exit 优雅退出
"""
import json
import os
import sys
import traceback

# 允许同一进程加载多个 OpenMP 运行时副本（torch 与 wxchat/welive 各自带 libiomp5md，
# 首次 numpy/torch 计算会触发 OMP Error #15；统一放行避免初始化即崩溃）。
os.environ.setdefault("KMP_DUPLICATE_LIB_OK", "TRUE")

# 重要：必须让 rag_dispatcher（→ torch）先于 wxchat_adapter import。
# wxchat SDK 先加载会破坏 torch 的 OpenMP/BLAS 运行时，后续 encode 直接
# access violation 崩溃（已用对照实验确认导入顺序是唯一变量）。
from rag_dispatcher import RagDispatcher
from wxchat_adapter import WxChatAdapter


def _log(msg: str) -> None:
    """所有日志走 stderr，保持 stdout 协议纯净。"""
    print(msg, file=sys.stderr, flush=True)


def main() -> None:
    # 可选预热：启动即初始化 wxchat SDK（把首次初始化成本挪到启动阶段）
    preload = "--preload" in sys.argv[1:]
    adapter = WxChatAdapter(preload=preload)
    rag = RagDispatcher(adapter)
    _log("[bridge] adapter initialized" + (" (preloaded)" if preload else ""))
    if preload:
        # 预热完成 → 输出就绪标记；C# 侧等待该标记后才放行请求。
        sys.stdout.write('{"event": "ready"}\n')
        sys.stdout.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            # 非法输入：仍按协议返回错误，不崩溃。
            _write({"id": None, "success": False, "error": "invalid json"})
            continue

        req_id = req.get("id")
        method = req.get("method", "")
        params = req.get("params") or {}

        if method == "/exit":
            _write({"id": req_id, "success": True, "data": "bye"})
            break

        try:
            if method.startswith("rag_"):
                result = rag.invoke(method, params)
            else:
                result = adapter.invoke(method, params)
            _write({"id": req_id, "success": True, "data": result})
        except Exception as exc:  # noqa: BLE001  -- 边界兜底
            _log(f"[bridge] method={method} failed: {exc}\n{traceback.format_exc()}")
            _write({"id": req_id, "success": False, "error": str(exc)})

    _log("[bridge] exiting")
    adapter.close()


def _write(obj) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()

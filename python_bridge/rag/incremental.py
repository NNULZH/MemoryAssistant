# -*- coding: utf-8 -*-
"""增量索引：只处理 sort_seq > last_seq 的新消息，append-only 追加 chunk 与向量。

核心约定（对齐 SDK stream.py 与 chunk 语义）：
- 消息时间用毫秒 `sort_seq`（create_time 秒精度不够，群聊同秒连发常见）。
- `client.messages()` 返回降序（最新在前）；chunk 内 `start_time` = 最新一条、`end_time` = 最旧一条。
- 会话已索引位置 = meta.session_last_seq（毫秒）；无 meta 时由 chunks 推导 `max(start_time)*1000 + 999`。
- 增量只追加新 chunk，旧 chunk 与向量完全不动（emb[i]↔chunks[i] 对齐保持不变）。
- encode 必须发生在任何 np.load 之前（模块顶部 import embedder → torch，避免访问违规）。
"""
from __future__ import annotations

import time
from pathlib import Path
from typing import Any, Callable

# 触发 torch import（与 embedder 约定一致），供本模块内任意 encode 前安全执行。
from rag.chunker import chunk_messages  # noqa: E402
from rag.embedder import LocalEmbedder  # noqa: E402
from rag.vector_store import VectorStore  # noqa: E402

import numpy as np  # noqa: E402


def msg_seq(m: Any) -> int:
    """消息的毫秒序号：sort_seq 优先，缺失回退 create_time*1000。"""
    s = getattr(m, "sort_seq", 0) or 0
    if s:
        return int(s)
    return int(getattr(m, "create_time", 0) or 0) * 1000


def derive_last_seq(chunks: list[dict]) -> dict[str, int]:
    """升级路径：从 chunks 推导各会话最后位置（毫秒）。

    chunk.start_time = chunk 内最新消息时间（秒）；`*1000 + 999` 跳过已完整索引的
    最后一整秒，避免旧快照里该秒尾部消息被重复追加。
    """
    out: dict[str, int] = {}
    for c in chunks:
        sid = c.get("session_id", "")
        if not sid:
            continue
        st = c.get("start_time", 0) or 0
        v = int(st) * 1000 + 999
        if v > out.get(sid, 0):
            out[sid] = v
    return out


def filter_new(msgs: list[Any], last_seq: int) -> list[Any]:
    """按毫秒序过滤出新增消息（严格 > last_seq）。"""
    return [m for m in msgs if msg_seq(m) > last_seq]


def fetch_new(client: Any, sid: str, last_seq: int,
              sample_limit: int = 2000, max_total: int = 20000,
              log: Callable[[str], None] | None = None) -> list[Any]:
    """分页拉取新增消息（messages 降序；最早一批越界即提前退出）。"""
    out: list[Any] = []
    offset = 0
    while offset < max_total:
        msgs = client.messages(sid, limit=sample_limit, offset=offset)
        if not msgs:
            break
        out += [m for m in msgs if msg_seq(m) > last_seq]
        if msg_seq(msgs[-1]) <= last_seq:
            break  # 该批最早一条已越界，更早的必然越界
        offset += len(msgs)
    if offset >= max_total and log:
        log(f"[warn] 会话 {sid} 新消息超采样上限 {max_total}，建议全量重建")
    return out


def incremental_index(
    client: Any,
    index_dir: str,
    scope: str | None = None,
    hard_cap: int = 200,
    embed_batch: int = 32,
    sample_limit: int = 2000,
    max_total_per_session: int = 20000,
    progress: Callable[[str], None] | None = None,
) -> dict:
    """增量更新索引：检测并追加新消息，不重排旧数据。返回统计 dict。"""
    t0 = time.time()
    log = progress or (lambda m: None)
    store = VectorStore(index_dir)

    # ---- Phase 1：只读 json（不触 numpy/torch）----
    chunks = store.load_chunks_only()
    if not store._meta_path.exists() or not store._emb_path.exists():
        return {"ok": False, "error": "索引缺失，请先全量构建"}

    meta = store.load_meta_only()
    if not meta:
        meta = {
            "version": 1,
            "built_at": None,
            "last_updated": None,
            "scope": "all",
            "recent_days": 0,
            "session_last_seq": derive_last_seq(chunks),
            "session_last_time": {},
        }
    if meta.get("version", 1) != 1:
        return {"ok": False, "error": "meta 版本过高，请全量重建"}
    if meta.get("recent_days", 0) > 0:
        return {"ok": False, "error": "时间窗索引不支持增量，请全量重建"}

    scope = scope or meta.get("scope", "all")
    sessions = client.sessions()
    if scope == "private":
        sessions = [s for s in sessions if not s.is_chatroom]
    elif scope == "group":
        sessions = [s for s in sessions if s.is_chatroom]

    last_seq_map: dict[str, int] = dict(meta.get("session_last_seq") or {})
    active = {s.username for s in sessions}
    stale = [sid for sid in last_seq_map if sid not in active]  # 仅提示不删除

    # ---- Phase 2：新消息检测 ----
    new_by_sid: dict[str, list[Any]] = {}
    for s in sessions:
        last_seq = last_seq_map.get(s.username, 0)
        if last_seq and (getattr(s, "sort_timestamp", 0) or 0) * 1000 <= last_seq:
            continue  # 空闲会话零开销
        fresh = fetch_new(client, s.username, last_seq, sample_limit, max_total_per_session, log)
        if fresh:
            new_by_sid[s.username] = fresh

    if not new_by_sid:
        return {
            "ok": True, "noop": True, "added_chunks": 0, "new_messages": 0,
            "updated_sessions": [], "stale_sessions": stale,
            "total_chunks": len(chunks),
            "total_msgs": sum(c.get("msg_count", 0) for c in chunks),
            "elapsed_sec": round(time.time() - t0, 2),
        }

    session_name_map = {s.username: (s.summary or s.username) for s in sessions}
    # ---- Phase 3：只切新消息，纯 append ----
    new_chunk_dicts: list[dict] = []
    new_msgs_count = 0
    for sid, msgs in new_by_sid.items():
        names = client.display_names({m.sender_username for m in msgs})
        name = session_name_map.get(sid, sid)
        new_chunk_dicts += [c.to_dict() for c in chunk_messages(msgs, sid, name, names, hard_cap)]
        new_msgs_count += len(msgs)
    log(f"新增消息 {new_msgs_count} 条 → {len(new_chunk_dicts)} chunks")

    # ---- Phase 4：encode 新文本（必须先于任何 np.load）----
    embedder = LocalEmbedder()
    new_emb = embedder.encode([c["text"] for c in new_chunk_dicts], batch_size=embed_batch)

    if not store.load():
        return {"ok": False, "error": "索引文件缺失（embeddings.npz/chunks.json）"}
    if new_emb.shape[1] != store.dim:
        return {"ok": False, "error": f"向量维度 {new_emb.shape[1]} != 旧索引 {store.dim}，模型已更换，请全量重建"}

    old_chunks = store._chunks
    # 可选：刷新有新增的会话名
    for c in old_chunks:
        n = session_name_map.get(c.get("session_id", ""))
        if n:
            c["session_name"] = n

    all_chunks = old_chunks + new_chunk_dicts
    all_emb = np.concatenate([store._emb, new_emb], axis=0)

    # ---- Phase 5：更新 meta 并落盘 ----
    last_seq_map = dict(last_seq_map)
    last_time_map: dict[str, int] = dict(meta.get("session_last_time") or {})
    for sid, msgs in new_by_sid.items():
        last_seq_map[sid] = max(last_seq_map.get(sid, 0), max(msg_seq(m) for m in msgs))
        last_time_map[sid] = max(last_time_map.get(sid, 0), max(getattr(m, "create_time", 0) or 0 for m in msgs))

    from datetime import datetime

    meta.update(
        last_updated=datetime.now().astimezone().isoformat(timespec="seconds"),
        built_at=meta.get("built_at") or datetime.now().astimezone().isoformat(timespec="seconds"),
        scope=scope,
        recent_days=0,
        session_last_seq=last_seq_map,
        session_last_time=last_time_map,
    )
    store.save(all_emb, all_chunks, meta)

    return {
        "ok": True,
        "noop": False,
        "added_chunks": len(new_chunk_dicts),
        "new_messages": new_msgs_count,
        "updated_sessions": sorted(new_by_sid),
        "stale_sessions": stale,
        "total_chunks": len(all_chunks),
        "total_msgs": sum(c.get("msg_count", 0) for c in all_chunks),
        "elapsed_sec": round(time.time() - t0, 2),
    }


if __name__ == "__main__":
    import json
    import sys

    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    from wxchat_adapter import WxChatAdapter  # noqa: E402

    d = sys.argv[1] if len(sys.argv) > 1 else r"D:\agent\data\index"
    print(json.dumps(incremental_index(WxChatAdapter().client, d, progress=print), ensure_ascii=False))

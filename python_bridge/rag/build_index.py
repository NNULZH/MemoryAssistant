# -*- coding: utf-8 -*-
"""索引构建：从 wxchat 拉全部会话消息 → 切块 → 编码 → 落盘。"""
from __future__ import annotations

import sys
import time
from pathlib import Path
from typing import Any, Callable

# 支持直接运行：让 python_bridge 目录进入 sys.path（rag 包位于其下）
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from rag.chunker import chunk_messages  # noqa: E402
from rag.embedder import LocalEmbedder  # noqa: E402
from rag.vector_store import VectorStore  # noqa: E402


def build_index(
    client: Any,
    index_dir: str,
    scope: str = "all",          # all | private | group
    recent_days: int = 0,        # 0 = 全部
    hard_cap: int = 200,
    embed_batch: int = 32,
    progress: Callable[[str], None] | None = None,
) -> dict:
    """构建完整索引。client: 已初始化的 WxchatClient（由 Adapter 提供）。"""
    t0 = time.time()
    log = progress or (lambda m: None)

    log("正在读取会话列表...")
    sessions = client.sessions()
    if scope == "private":
        sessions = [s for s in sessions if not s.is_chatroom]
    elif scope == "group":
        sessions = [s for s in sessions if s.is_chatroom]

    if recent_days > 0:
        cutoff = int(time.time()) - recent_days * 86400
        sessions = [s for s in sessions if s.last_timestamp >= cutoff]

    # 1. 切块
    chunks = []
    session_names: dict[str, str] = {}
    log(f"会话数: {len(sessions)}，开始切块...")
    for s in sessions:
        try:
            messages = client.messages(s.username, limit=10000)
            if not messages:
                continue
        except Exception as exc:  # noqa: BLE001
            log(f"  [skip] {s.username}: {exc}")
            continue
        names = client.display_names({m.sender_username for m in messages})
        session_names[s.username] = s.summary or s.username
        chunks.extend(
            chunk_messages(messages, s.username, session_names[s.username], names, hard_cap)
        )
    log(f"切块完成: {len(chunks)} chunks")

    # 2. 编码
    if not chunks:
        raise RuntimeError("无可用消息，索引为空。")

    embedder = LocalEmbedder()
    texts = [c.text for c in chunks]
    log(f"编码 {len(texts)} 段 (dim={embedder.dim}, batch={embed_batch})...")
    embeddings = embedder.encode(texts, batch_size=embed_batch)

    # 3. 落盘
    store = VectorStore(index_dir)
    meta = [c.to_dict() for c in chunks]
    # P9：落盘索引元数据（增量索引用）
    from datetime import datetime

    index_meta = {
        "built_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "last_updated": datetime.now().astimezone().isoformat(timespec="seconds"),
        "scope": scope,
        "recent_days": recent_days,
        "session_last_seq": {},  # 全量构建后由首次增量升级推导
        "session_last_time": {},
    }
    store.save(embeddings, meta, index_meta)

    return {
        "ok": True,
        "chunks": len(meta),
        "sessions": len(session_names),
        "total_msgs": sum(c["msg_count"] for c in meta),
        "dim": int(embeddings.shape[1]),
        "elapsed_sec": round(time.time() - t0, 1),
        "index_dir": str(Path(index_dir).resolve()),
    }


if __name__ == "__main__":
    import json
    import sys

    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    from wxchat_adapter import WxChatAdapter  # noqa: E402

    d = sys.argv[1] if len(sys.argv) > 1 else r"D:\agent\data\index"
    print(json.dumps(build_index(WxChatAdapter().client, d, progress=print), ensure_ascii=False))

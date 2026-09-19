# -*- coding: utf-8 -*-
"""向量存储：NPZ(向量) + JSON(chunks) + JSON(meta)，暴力余弦搜索。"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np

try:
    import numba  # noqa: F401  -- 可选加速，未安装时用 numpy 兜底
    _HAS_NUMBA = True
except ImportError:
    _HAS_NUMBA = False


class VectorStore:
    """轻量向量库，面向几千到几万 chunk 规模。

    文件布局：
      embeddings.npz  — emb: (N, dim) float32 已归一化，emb[i] 与 chunks[i] 对齐
      chunks.json     — list[dict]，每项 7 key
      meta.json       — 索引元数据（P9 增量索引用：built_at/last_updated/session_last_seq 等）
    """

    def __init__(self, dir_path: str):
        self._dir = Path(dir_path)
        self._emb_path = self._dir / "embeddings.npz"
        self._meta_path = self._dir / "chunks.json"
        self._meta_json_path = self._dir / "meta.json"
        self._emb: np.ndarray | None = None  # (N, dim) float32 已归一化
        self._chunks: list[dict] = []
        self._meta: dict = {}
        self._has_meta = False

    # ---- 持久化 ----

    def save(self, embeddings: np.ndarray, chunks: list[dict], meta: dict | None = None) -> None:
        self._dir.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(self._emb_path, emb=embeddings)
        tmp = self._meta_path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(chunks, ensure_ascii=False), encoding="utf-8")
        tmp.replace(self._meta_path)

        meta = meta if meta is not None else self._derive_meta(chunks)
        meta.setdefault("version", 1)
        meta.update(
            chunks=len(chunks),
            sessions=len({c.get("session_id") for c in chunks}),
            total_msgs=sum(c.get("msg_count", 0) for c in chunks),
        )
        meta_tmp = self._meta_json_path.with_suffix(".tmp")
        meta_tmp.write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
        meta_tmp.replace(self._meta_json_path)

        self._emb = embeddings
        self._chunks = chunks
        self._meta = meta
        self._has_meta = True

    def load(self) -> bool:
        if not self._emb_path.exists() or not self._meta_path.exists():
            return False
        with np.load(self._emb_path) as data:
            self._emb = np.asarray(data["emb"], dtype=np.float32)
        self._chunks = json.loads(self._meta_path.read_text(encoding="utf-8"))
        self._load_meta_file()
        return True

    # ---- meta 轻量读取（纯 json，不触 np.load，供增量流程先读后 encode）----

    def load_meta_only(self) -> dict:
        """只读 meta.json；文件缺失或损坏返回空 dict（旧目录兼容）。"""
        self._load_meta_file()
        return dict(self._meta)

    def load_chunks_only(self) -> list[dict]:
        """只读 chunks.json；缺失返回空列表。"""
        if not self._meta_path.exists():
            return []
        return json.loads(self._meta_path.read_text(encoding="utf-8"))

    def _load_meta_file(self) -> None:
        self._has_meta = False
        self._meta = {}
        if self._meta_json_path.exists():
            try:
                self._meta = json.loads(self._meta_json_path.read_text(encoding="utf-8")) or {}
                self._has_meta = True
            except json.JSONDecodeError:
                self._meta = {}

    @staticmethod
    def _derive_meta(chunks: list[dict]) -> dict:
        """无 meta 时从 chunks 推导：session_last_time = 各会话 max(start_time)（start_time 为 chunk 内最新消息）。"""
        last_time: dict[str, int] = {}
        for c in chunks:
            sid = c.get("session_id", "")
            if not sid:
                continue
            st = c.get("start_time", 0) or 0
            if st > last_time.get(sid, 0):
                last_time[sid] = st
        return {
            "built_at": _iso_now(),
            "last_updated": _iso_now(),
            "scope": "all",
            "recent_days": 0,
            "session_last_seq": {},
            "session_last_time": last_time,
        }

    @property
    def is_loaded(self) -> bool:
        return self._emb is not None

    @property
    def count(self) -> int:
        return len(self._chunks)

    @property
    def dim(self) -> int:
        return int(self._emb.shape[1]) if self._emb is not None else 0

    def stats(self) -> dict:
        return {
            "loaded": self.is_loaded,
            "chunks": self.count,
            "dim": self.dim,
            "sessions": len({c.get("session_id") for c in self._chunks}),
            "total_msgs": sum(c.get("msg_count", 0) for c in self._chunks),
            "path": str(self._meta_path),
            "has_meta": self._has_meta,
            "built_at": self._meta.get("built_at"),
            "last_updated": self._meta.get("last_updated"),
            "scope": self._meta.get("scope", "all"),
        }

    # ---- 检索 ----

    def search(self, query_vec: np.ndarray, top_k: int = 5) -> list[dict]:
        """query_vec: (dim,) 已归一化。返回 [{chunk, score}]，按 score 降序。"""
        if self._emb is None or self._emb.shape[0] == 0:
            return []
        q = np.asarray(query_vec, dtype=np.float32).reshape(1, -1)
        scores = self._emb @ q.T  # 已归一化 → 余弦
        scores = scores.reshape(-1)
        k = min(top_k, len(scores))
        idx = np.argpartition(-scores, k - 1)[:k]
        idx = idx[np.argsort(-scores[idx])]
        out = []
        for i in idx.tolist():
            out.append({"chunk": self._chunks[i], "score": float(scores[i])})
        return out


def _iso_now() -> str:
    from datetime import datetime

    return datetime.now().astimezone().isoformat(timespec="seconds")

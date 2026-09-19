# -*- coding: utf-8 -*-
"""检索器：向量检索统一入口（第一版）。"""
from __future__ import annotations

from pathlib import Path

from rag.embedder import LocalEmbedder
from rag.vector_store import VectorStore


class RagRetriever:
    """给定自然语言 Query → top_k 相关聊天片段。"""

    def __init__(self, index_dir: str):
        self._store = VectorStore(index_dir)
        self._embedder = LocalEmbedder()
        self._index_dir = Path(index_dir)

    @property
    def ready(self) -> bool:
        return self._store.load()

    def stats(self) -> dict:
        self._store.load()
        return self._store.stats()

    def search(self, query: str, top_k: int = 5) -> list[dict]:
        """返回 [{chunk, score}]，score 为余弦相似度。

        注意：必须先 encode 再 np.load（torch encode 在 np.load 之后会触发
        access violation，见 OpenMP/numpy 交互问题）。
        """
        q = self._embedder.encode([query])[0]
        if not self._store.load():
            return []
        return self._store.search(q, top_k)

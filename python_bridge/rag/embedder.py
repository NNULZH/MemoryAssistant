# -*- coding: utf-8 -*-
"""Embedding：本地 BAAI/bge-small-zh-v1.5 离线编码。"""
from __future__ import annotations

import os
from pathlib import Path

import numpy as np

# 关键：必须先 import torch（即使不用它初始化模型），否则后续 np.load（VectorStore）
# 触发 OpenMP/BLAS 线程初始化后，再初始化 torch 会直接 access violation 崩溃。
import torch  # noqa: F401  -- 仅保证初始化顺序

# 本地模型目录（首次自动下载已由人工 curl 完成）
DEFAULT_MODEL_DIR = str(Path(__file__).resolve().parent.parent.parent / "models" / "bge-small-zh-v1.5")


class LocalEmbedder:
    """sentence_transformers 本地编码器，懒加载 + 批处理。"""

    def __init__(self, model_dir: str | None = None, device: str | None = None):
        self._model_dir = model_dir or os.environ.get("BGE_MODEL_DIR") or DEFAULT_MODEL_DIR
        self._device = device or os.environ.get("BGE_DEVICE", "cpu")
        self._model = None
        self._dim = 512

    def _ensure(self):
        if self._model is None:
            from sentence_transformers import SentenceTransformer

            self._model = SentenceTransformer(self._model_dir, device=self._device)
            self._dim = self._model.get_embedding_dimension()

    @property
    def dim(self) -> int:
        self._ensure()
        return self._dim

    def encode(self, texts: list[str], batch_size: int = 32) -> np.ndarray:
        """返回 float32 矩阵 (N, dim)，行已归一化（便于余弦 = 点积）。"""
        if not texts:
            return np.zeros((0, self.dim), dtype=np.float32)
        self._ensure()
        vec = self._model.encode(
            texts,
            batch_size=batch_size,
            normalize_embeddings=True,
            show_progress_bar=False,
            convert_to_numpy=True,
        )
        return np.asarray(vec, dtype=np.float32)

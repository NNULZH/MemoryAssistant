# -*- coding: utf-8 -*-
"""numpy 手写 KMeans（不引入 sklearn/torch 新依赖）。

输入 x 为 (n, dim) 已 L2 归一化的向量（与索引 embedding 一致），点积即余弦相似度。
固定 seed 保证可复现（验收/答辩可对照）。
"""
from __future__ import annotations

import numpy as np


def kmeans(x: np.ndarray, k: int, iters: int = 20, seed: int = 20260907) -> tuple[np.ndarray, np.ndarray]:
    """返回 (labels, centers)。

    - labels: (n,) 每个样本所属簇 id
    - centers: (k, dim) 归一化后的簇心
    - 空簇处理：该簇中心重置为随机样本点
    - 收敛早停：中心变化小于 atol 提前退出
    """
    n = len(x)
    if n == 0:
        return np.zeros(0, dtype=np.int64), np.zeros((0, x.shape[1]), dtype=np.float32)
    k = max(1, min(k, n))
    rng = np.random.default_rng(seed)
    centers = x[rng.choice(n, k, replace=False)].astype(np.float32)

    for _ in range(iters):
        labels = (x @ centers.T).argmax(axis=1)
        new_centers = []
        for i in range(k):
            mask = labels == i
            if mask.sum():
                new_centers.append(x[mask].mean(axis=0))
            else:
                new_centers.append(x[rng.choice(n)])
        new_centers = np.stack(new_centers).astype(np.float32)
        norm = np.linalg.norm(new_centers, axis=1, keepdims=True) + 1e-9
        new_centers /= norm
        if np.allclose(centers, new_centers, atol=1e-4):
            centers = new_centers
            break
        centers = new_centers
    return labels, centers


def pick_k(n: int, max_clusters: int = 10) -> int:
    """簇数启发式：n<3 不聚类；否则 min(max_clusters, max(3, n//40))。"""
    if n < 3:
        return 0
    return max(1, min(max_clusters, max(3, n // 40)))

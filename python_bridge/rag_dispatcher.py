# -*- coding: utf-8 -*-
"""RAG 桥接分发：把 rag_* 方法路由到索引构建/检索。"""
from __future__ import annotations

import sys
import threading
import time
import re
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rag.build_index import build_index  # noqa: E402
from rag.kmeans import kmeans, pick_k  # noqa: E402
from rag.retriever import RagRetriever  # noqa: E402
from rag.topic_keywords import classify_keywords  # noqa: E402

DEFAULT_INDEX_DIR = str(Path(__file__).resolve().parent.parent / "data" / "index")

# 承诺短语正则（索引行级扫描；命中即候选，供 LLM 判定"我的/他人的"）
_PROMISE_RE = re.compile(
    r"我(?:明天|下周|周末|晚点|回头|改天|待会|一会儿|下午|晚上|到时候|之后|稍后|这几天|尽快|过两天)"
    r"(?:给|发|做|弄|看|处理|安排|约|带|帮|回|补|买|写|查|改|联系)|"
    r"这个我来|我来搞|我来弄|我负责|我答应|我保证|记得提醒我|包在我身上|"
    r"我帮(?:你|你们)?(?:看看|弄|搞|发|写|查|处理|问|带|约|买|安排|联系|找)|"
    r"我发给你|回头给你|周末发你|明天给你|待会给你|晚点给你|下次给你|之后给你|"
    r"等你回来|回去(?:再|给|就|说)|到时候(?:再|给|发|请|带|约)"
)


class RagDispatcher:
    """RAG 相关方法入口。retriever 懒加载；build 加锁防并发。"""

    def __init__(self, adapter: Any, index_dir: str | None = None):
        self._adapter = adapter
        self._index_dir = index_dir or DEFAULT_INDEX_DIR
        self._retriever: RagRetriever | None = None
        self._build_lock = threading.Lock()

    def _get_retriever(self) -> RagRetriever:
        if self._retriever is None:
            self._retriever = RagRetriever(self._index_dir)
        return self._retriever

    def _real_names(self, session_ids: Any) -> dict[str, str]:
        """会话真实名字（备注名/昵称/群名）。

        索引里的 session_name 其实是建库时的"最后一条消息摘要"（例如"不肥抱着不爽🤫"），
        把它当会话名展示会让人看不懂"这条记录到底来自谁"。这里在查询时用 SDK 批量解析真名，
        因此不需要重建索引；解析不到就保持原样。
        """
        sids = sorted({str(s) for s in session_ids if str(s).strip()})
        if not sids:
            return {}
        try:
            return {k: (v or "").strip() for k, v in (self._adapter.client.display_names(sids) or {}).items()}
        except Exception as exc:  # noqa: BLE001
            print(f"[rag] display_names failed: {exc}", file=sys.stderr, flush=True)
            return {}

    def _with_real_names(self, items: list[dict]) -> list[dict]:
        """把 [{chunk, score}] 里的 chunk.session_name 换成真名（其余形态传入即原样返回）。"""
        def sid_of(item: dict) -> str:
            chunk = item.get("chunk")
            if isinstance(chunk, dict):
                return str(chunk.get("session_id") or "")
            return str(item.get("session_id") or "")

        names = self._real_names(sid_of(i) for i in items)
        if not names:
            return items

        out: list[dict] = []
        for item in items:
            real = names.get(sid_of(item), "")
            chunk = item.get("chunk")
            if real and isinstance(chunk, dict):
                out.append({**item, "chunk": {**chunk, "session_name": real}})
            elif real and item.get("session_id"):
                out.append({**item, "session_name": real})
            else:
                out.append(item)
        return out

    def invoke(self, method: str, params: dict[str, Any]) -> Any:
        handler = getattr(self, f"method_{method}", None)
        if handler is None:
            raise ValueError(f"unknown rag method: {method}")
        return handler(params)

    def method_rag_index_status(self, params: dict[str, Any]) -> dict:
        return self._get_retriever().stats()

    def method_rag_search(self, params: dict[str, Any]) -> list[dict]:
        query = str(params.get("query", "")).strip()
        if not query:
            raise ValueError("query required")
        top_k = int(params.get("top_k", 5))
        return self._with_real_names(self._get_retriever().search(query, top_k))

    def method_rag_timeline(self, params: dict[str, Any]) -> dict:
        """按日聚合索引 chunks，返回时间线（倒序）。"""
        retriever = self._get_retriever()
        if not retriever.ready:
            return {"ok": False, "days": []}
        # VectorStore 已 load；直接读取内部 chunks 聚合
        store = retriever._store
        by_day: dict[str, dict] = {}
        for c in store._chunks:
            day = c.get("date", "")
            if not day:
                continue
            d = by_day.setdefault(day, {"date": day, "msg_count": 0, "session_count": 0, "sessions": {}})
            d["msg_count"] += c.get("msg_count", 0)
            sid = c.get("session_id", "")
            if sid not in d["sessions"]:
                d["sessions"][sid] = {
                    "session_id": sid,
                    "session_name": c.get("session_name", sid),
                    "msg_count": 0,
                    "chunks": 0,
                }
            d["sessions"][sid]["msg_count"] += c.get("msg_count", 0)
            d["sessions"][sid]["chunks"] += 1
        days = []
        for day, d in by_day.items():
            sessions = sorted(d["sessions"].values(), key=lambda s: s["msg_count"], reverse=True)[:10]
            days.append({
                "date": day,
                "msg_count": d["msg_count"],
                "session_count": len(d["sessions"]),
                "sessions": sessions,
            })
        days.sort(key=lambda x: x["date"], reverse=True)
        # 会话名换成真名（索引里存的是建库时的摘要，不是名字）
        names = self._real_names(s["session_id"] for d in days for s in d["sessions"])
        for d in days:
            for s in d["sessions"]:
                real = names.get(s["session_id"])
                if real:
                    s["session_name"] = real
        return {"ok": True, "days": days}

    def method_rag_day_detail(self, params: dict[str, Any]) -> dict:
        """某天的会话片段详情（来自索引 chunks）。"""
        date = str(params.get("date", "")).strip()
        if not date:
            raise ValueError("date required")
        retriever = self._get_retriever()
        if not retriever.ready:
            return {"ok": False, "chunks": []}
        store = retriever._store
        out = []
        for c in store._chunks:
            if c.get("date", "") != date:
                continue
            out.append({
                "session_id": c.get("session_id", ""),
                "session_name": c.get("session_name", c.get("session_id", "")),
                "text": c.get("text", ""),
                "msg_count": c.get("msg_count", 0),
                "date": date,
                # 片段起始时间：C# 侧点卡片时用它定位到"那一段对话"的准确位置
                "create_time": int(c.get("start_time", 0) or 0),
            })
        out = self._with_real_names(out)
        out.sort(key=lambda c: c["session_name"])
        return {"ok": True, "chunks": out}

    def method_rag_topics(self, params: dict[str, Any]) -> dict:
        """最近 N 天主题分析：关键词分类 + Embedding KMeans 聚类（簇未命名，由 C# 侧 LLM 命名）。"""
        recent_days = int(params.get("recent_days", 14))
        max_clusters = int(params.get("max_clusters", 10))
        retriever = self._get_retriever()
        if not retriever.ready:
            return {"ok": False, "recent_days": recent_days, "chunks_in_scope": 0,
                    "keyword_topics": [], "clusters": []}
        store = retriever._store
        cutoff = time.time() - recent_days * 86400

        idx = [i for i, c in enumerate(store._chunks) if c.get("start_time", 0) >= cutoff]
        scoped = [store._chunks[i] for i in idx]

        # (a) 关键词分类：按主题聚合 + 按日聚合
        kw_by_cat: dict[str, dict[str, int]] = {}  # category -> {date: count}
        kw_total: dict[str, int] = {}
        for c in scoped:
            text = c.get("text", "")
            if not text:
                continue
            hits = classify_keywords(text)
            if not hits:
                continue
            # 每 chunk 内同一主题只计 1 次（去重）
            for cat in hits:
                kw_total[cat] = kw_total.get(cat, 0) + 1
                day = c.get("date", "")
                if day:
                    per = kw_by_cat.setdefault(cat, {})
                    per[day] = per.get(day, 0) + 1
        keyword_topics = []
        for cat, total in sorted(kw_total.items(), key=lambda kv: kv[1], reverse=True):
            per = kw_by_cat[cat]
            keyword_topics.append({
                "category": cat,
                "total": total,
                "per_day": [{"date": d, "count": per[d]} for d in sorted(per, reverse=True)],
            })

        # (b) Embedding KMeans 聚类
        clusters = []
        if idx:
            # 重要：numpy matmul（MKL/OpenBLAS）前先让 torch 初始化 OpenMP 运行时，
            # 否则与 wxchat/welive 已加载的 libiomp5md 冲突 → OMP Error #15 崩溃。
            # （与 rag_search"先 encode 再 load"同原理；torch 必须先于其他库初始化。）
            import torch  # noqa: F401
            _ = torch.mm(torch.zeros(1, 1), torch.zeros(1, 1))
            k = pick_k(len(idx), max_clusters)
            if k > 0:
                x = store._emb[idx]
                labels, centers = kmeans(x, k)
                # 簇心相似度最高的 chunk 作为代表文本
                rep_idx = (x @ centers.T).argmax(axis=0)
                for cid in range(k):
                    members = [i for i, lb in enumerate(labels) if lb == cid]
                    if not members:
                        continue
                    # 按日聚合
                    per_day: dict[str, int] = {}
                    seen: set[tuple[str, str]] = set()
                    samples = []
                    for m in members:
                        c = scoped[m]
                        day = c.get("date", "")
                        if day:
                            per_day[day] = per_day.get(day, 0) + 1
                        key = (c.get("session_id", ""), day)
                        if key in seen:
                            continue
                        seen.add(key)
                        samples.append({
                            "session_id": c.get("session_id", ""),
                            "session_name": c.get("session_name", c.get("session_id", "")),
                            "date": day,
                            "text": (c.get("text", "") or "")[:150],
                            # 片段起始时间：点样本卡片时定位到那一段对话
                            "create_time": int(c.get("start_time", 0) or 0),
                        })
                    rep_text = (scoped[rep_idx[cid]].get("text", "") or "")[:300]
                    clusters.append({
                        "cluster_id": cid,
                        "size": len(members),
                        "representative": rep_text,
                        "per_day": [{"date": d, "count": per_day[d]} for d in sorted(per_day, reverse=True)],
                        "samples": sorted(samples, key=lambda s: s["date"], reverse=True)[:3],
                    })
                clusters.sort(key=lambda c: c["size"], reverse=True)

        # 样本的会话名换成真名（索引里存的是建库时的摘要，点卡片跳转时标题会显示它）
        sample_sids = [s.get("session_id", "") for c in clusters for s in c.get("samples", [])]
        real_names = self._real_names(sample_sids)
        for c in clusters:
            for s in c.get("samples", []):
                real = real_names.get(s.get("session_id", ""))
                if real:
                    s["session_name"] = real

        return {"ok": True, "recent_days": recent_days, "chunks_in_scope": len(scoped),
                "keyword_topics": keyword_topics, "clusters": clusters}

    def method_rag_profiles(self, params: dict[str, Any]) -> dict:
        """会话画像：按 session_id 聚合索引 chunks（消息数/天数/日期范围/高频关键词主题）。"""
        top = int(params.get("top", 100))
        retriever = self._get_retriever()
        if not retriever.ready:
            return {"ok": False, "total_sessions": 0, "profiles": []}
        store = retriever._store

        by_session: dict[str, dict] = {}
        for c in store._chunks:
            sid = c.get("session_id", "")
            if not sid:
                continue
            p = by_session.setdefault(sid, {
                "session_id": sid,
                "session_name": c.get("session_name", sid),
                "msg_count": 0,
                "days": set(),
                "kw": {},
            })
            p["msg_count"] += c.get("msg_count", 0)
            day = c.get("date", "")
            if day:
                p["days"].add(day)
            hits = classify_keywords(c.get("text", "") or "")
            for cat in hits:
                p["kw"][cat] = p["kw"].get(cat, 0) + 1

        profiles = []
        for p in by_session.values():
            days = sorted(p["days"])
            top_kw = sorted(p["kw"].items(), key=lambda kv: kv[1], reverse=True)[:5]
            profiles.append({
                "session_id": p["session_id"],
                "session_name": p["session_name"],
                "msg_count": p["msg_count"],
                "day_count": len(days),
                "first_date": days[0] if days else "",
                "last_date": days[-1] if days else "",
                "top_keyword_topics": [{"category": k, "count": v} for k, v in top_kw],
            })
        profiles.sort(key=lambda p: p["msg_count"], reverse=True)
        # 会话名换成真名（索引里存的是建库时的摘要，不是名字）
        names = self._real_names(p["session_id"] for p in profiles[:top])
        for p in profiles[:top]:
            real = names.get(p["session_id"])
            if real:
                p["session_name"] = real
        return {"ok": True, "total_sessions": len(profiles), "profiles": profiles[:top]}

    def method_rag_commitments(self, params: dict[str, Any]) -> dict:
        """承诺候选（索引行级扫描）：覆盖全部索引历史，毫秒级。

        命中行 → 候选卡片。is_self 用"自己的发送者标记"判定：
        SDK 导出的 is_self 标志不可靠（实测全 False），改由 display_names 解析
        自己的无后缀 wxid 得到显示名（如"求一下通项公式"），行发送者命中即视为"我的承诺"。
        """
        limit = int(params.get("limit", 200))
        retriever = self._get_retriever()
        if not retriever.ready:
            return {"ok": False, "note": "索引未加载", "scanned_chunks": 0, "scanned_lines": 0, "candidates": []}
        store = retriever._store

        # 自己的发送者标记：无后缀 wxid + 其显示名
        my_senders: set[str] = set()
        try:
            full = str(self._adapter.client.dec.get("wxid", "") or "")
            no_suffix = re.sub(r"_d\d+$", "", full) if full else ""
            if no_suffix:
                my_senders.add(no_suffix)
                dn = self._adapter.client.display_names({no_suffix}).get(no_suffix, "")
                if dn:
                    my_senders.add(dn)
        except Exception:  # noqa: BLE001
            pass

        candidates: list[dict[str, Any]] = []
        scanned_lines = 0
        for c in store._chunks:
            text = c.get("text", "") or ""
            for line in text.splitlines():
                scanned_lines += 1
                body = line.split("]", 1)[-1].strip() if line.startswith("[") else line.strip()
                if ":" not in body:
                    continue
                sender, content = body.split(":", 1)
                sender = sender.strip()
                content = content.strip()
                if not content or not sender:
                    continue
                m = _PROMISE_RE.search(content)
                if not m:
                    continue
                candidates.append({
                    "session_id": c.get("session_id", ""),
                    "session_name": c.get("session_name", c.get("session_id", "")),
                    "date": c.get("date", ""),
                    "create_time": c.get("end_time", 0) or 0,
                    "sender": sender,
                    "is_self": sender in my_senders,
                    "content": content[:120],
                    "matched_text": m.group(0)[:40],
                })
        candidates.sort(key=lambda x: (x["date"], x["create_time"]), reverse=True)
        return {
            "ok": True,
            "note": "规则候选，待确认：索引行级关键词正则初筛，仅供线索，需 LLM/人工判断。",
            "scanned_chunks": len(store._chunks),
            "scanned_lines": scanned_lines,
            "candidates": candidates[:limit],
        }

    def method_rag_build_index(self, params: dict[str, Any]) -> dict:
        scope = str(params.get("scope", "all"))
        recent_days = int(params.get("recent_days", 0))
        hard_cap = int(params.get("hard_cap", 200))
        embed_batch = int(params.get("embed_batch", 32))
        logs: list[str] = []

        def progress(msg: str) -> None:
            logs.append(msg)
            print(f"[bridge][rag-build] {msg}", file=sys.stderr, flush=True)

        with self._build_lock:
            result = build_index(
                self._adapter.client,
                self._index_dir,
                scope=scope,
                recent_days=recent_days,
                hard_cap=hard_cap,
                embed_batch=embed_batch,
                progress=progress,
            )
        result["progress_logs"] = logs
        # 重建后重置 retriever 引用
        self._retriever = None
        return result

    def method_rag_incremental_index(self, params: dict[str, Any]) -> dict:
        """P9 增量索引：只追加新消息，不重排旧数据。"""
        from rag.incremental import incremental_index  # noqa: E402

        hard_cap = int(params.get("hard_cap", 200))
        embed_batch = int(params.get("embed_batch", 32))
        sample_limit = int(params.get("sample_limit", 2000))
        logs: list[str] = []

        def progress(msg: str) -> None:
            logs.append(msg)
            print(f"[bridge][rag-incremental] {msg}", file=sys.stderr, flush=True)

        with self._build_lock:
            result = incremental_index(
                self._adapter.client,
                self._index_dir,
                hard_cap=hard_cap,
                embed_batch=embed_batch,
                sample_limit=sample_limit,
                progress=progress,
            )
        result["progress_logs"] = logs
        self._retriever = None
        return result

# -*- coding: utf-8 -*-
"""消息切块：按（会话, 日历日）聚合为语义单元。"""
from __future__ import annotations

from datetime import datetime
from typing import Any


class Chunk:
    """一个语义单元：某会话某一天的消息集合。"""

    __slots__ = (
        "session_id", "session_name", "date", "text",
        "msg_count", "start_time", "end_time",
    )

    def __init__(
        self,
        session_id: str,
        session_name: str,
        date: str,
        text: str,
        msg_count: int,
        start_time: int,
        end_time: int,
    ):
        self.session_id = session_id
        self.session_name = session_name
        self.date = date
        self.text = text
        self.msg_count = msg_count
        self.start_time = start_time
        self.end_time = end_time

    def to_dict(self) -> dict[str, Any]:
        return {
            "session_id": self.session_id,
            "session_name": self.session_name,
            "date": self.date,
            "text": self.text,
            "msg_count": self.msg_count,
            "start_time": self.start_time,
            "end_time": self.end_time,
        }


def mask_text(text: str) -> str:
    """简单敏感信息脱敏（与 wxchat_adapter 一致）。"""
    import re

    text = re.sub(r"(?<!\d)(1[3-9]\d{9})(?!\d)", lambda m: m.group(1)[:3] + "****" + m.group(1)[-4:], text)
    text = re.sub(r"(?<!\d)(\d{17}[\dXx])(?!\d)", lambda m: m.group(1)[:4] + "**********" + m.group(1)[-4:], text)
    text = re.sub(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", lambda m: m.group(0)[:1] + "***@" + m.group(0).split("@")[-1], text)
    text = re.sub(r"(?<!\d)(\d{16,19})(?!\d)", lambda m: m.group(1)[:4] + "****" + m.group(1)[-4:], text)
    return text


def chunk_messages(
    messages: list[Any],
    session_id: str,
    session_name: str,
    display_names: dict[str, str],
    hard_cap: int = 200,
) -> list[Chunk]:
    """把某会话的消息列表切成（日历日）块。

    messages: wxchat SDK 消息对象列表（需含 create_time, sender_username,
              is_self, local_type, message_content）。
    display_names: sender_username -> 昵称。
    hard_cap: 单日消息超过该值硬切为多个块。
    """
    by_day: dict[str, list[Any]] = {}
    for m in messages:
        day = datetime.fromtimestamp(m.create_time).strftime("%Y-%m-%d")
        by_day.setdefault(day, []).append(m)

    chunks: list[Chunk] = []
    for day in sorted(by_day):
        msgs = by_day[day]
        # 硬切子块
        for i in range(0, len(msgs), hard_cap):
            sub = msgs[i:i + hard_cap]
            lines = []
            for m in sub:
                sender = m.sender_username if m.is_self else display_names.get(m.sender_username, m.sender_username)
                t = datetime.fromtimestamp(m.create_time).strftime("%H:%M")
                content = mask_text(m.message_content or "")
                lines.append(f"[{t}] {sender}: {content}")
            chunks.append(
                Chunk(
                    session_id=session_id,
                    session_name=session_name,
                    date=day,
                    text="\n".join(lines),
                    msg_count=len(sub),
                    start_time=sub[0].create_time,
                    end_time=sub[-1].create_time,
                )
            )
    return chunks

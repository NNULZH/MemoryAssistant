# -*- coding: utf-8 -*-
"""
wxchat SDK Adapter
==================
把 wxchat SDK 的方法包装成 Bridge 可调用的统一接口。

职责：
  - 懒加载 WxchatClient（只实例化一次）
  - 统一返回可 JSON 序列化的结构
  - 工具输出前做敏感信息脱敏（手机号 / 身份证 / 邮箱 / 银行卡）
"""
import json
import os
import re
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any

try:  # WCDB 用 zstd 压缩列内容，表情包的 emoji XML 就在压缩后的 message_content 里
    import zstandard as _zstd
except Exception:  # noqa: BLE001
    _zstd = None

# 微信库快照的自动刷新窗口（秒）：SDK 读的是微信库的**快照副本**，只在构造时复制一次；
# 不主动 refresh 的话，读到的会话永远停在"桥进程启动那一刻"——
# 实测：每 40 秒跑一轮的聊天任务，会话一直停在 10:15（正是桥启动时刻），
# 10:15 之后双方的往来一条都没落库，AI 只好改去 OCR 看微信窗口。
# 设成 0 可关掉自动刷新（回到旧行为）；可用环境变量临时调整。
SNAPSHOT_MAX_AGE_S = float(os.environ.get("WXCHAT_SNAPSHOT_MAX_AGE_S", "20"))

# wxchat SDK 位于 D:\智能比赛\wxchat；SDK 自身会处理中文路径（v0.4.0 自动重定位快照）。
WXCHAT_DIR = r"D:\智能比赛\wxchat"
if str(WXCHAT_DIR) not in sys.path:
    sys.path.insert(0, WXCHAT_DIR)

from wxchat import WxchatClient  # noqa: E402

# 图片/语音解密落盘目录（UI 直接按路径加载；默认放仓库 data/media，保持 ASCII 路径）
DEFAULT_MEDIA_DIR = str(Path(__file__).resolve().parent.parent / "data" / "media")

# 敏感信息脱敏
_PHONE = re.compile(r"(?<!\d)(1[3-9]\d{9})(?!\d)")
_ID_CARD = re.compile(r"(?<!\d)(\d{17}[\dXx])(?!\d)")
_EMAIL = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")
_BANK = re.compile(r"(?<!\d)(\d{16,19})(?!\d)")

# 表情包：message_content 解压后是 <emoji md5="…" …/>，md5 是表情库里查描述/图的唯一钥匙
_EMOJI_MD5 = re.compile(r'md5\s*=\s*"([0-9a-fA-F]{32})"')
_ZSTD_MAGIC = "28b52ffd"


def _mask(text: str) -> str:
    if not isinstance(text, str):
        return text
    text = _PHONE.sub(lambda m: m.group(1)[:3] + "****" + m.group(1)[-4:], text)
    text = _ID_CARD.sub(lambda m: m.group(1)[:4] + "**********" + m.group(1)[-4:], text)
    text = _EMAIL.sub(lambda m: m.group(0)[:1] + "***@" + m.group(0).split("@")[-1], text)
    text = _BANK.sub(lambda m: m.group(1)[:4] + "****" + m.group(1)[-4:], text)
    return text


def _log_stderr(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def _is_broadcast(username: str) -> bool:
    """公众号/服务号/品牌占位会话：属于推送，不是"聊天对象"。"""
    u = (username or "").lower()
    return u.startswith("gh_") or "brandsessionholder" in u


def _int_safe(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _render_kind(local_type: int) -> str:
    """消息类型 → 展示类别（1文本 3图片 34语音 43视频 47表情包 49应用/链接）。"""
    return {
        1: "text",
        3: "image",
        34: "voice",
        43: "video",
        47: "sticker",
        48: "location",
        49: "app",
        50: "call",
        10000: "system",
    }.get(local_type, "other")


class WxChatAdapter:
    def __init__(self, preload: bool = False):
        self._client: WxchatClient | None = None
        self._caption_cache: dict[str, str] = {}
        # 上次刷新快照的时刻（monotonic）：见 SNAPSHOT_MAX_AGE_S 的说明
        self._last_snapshot_at = 0.0
        if preload:
            _ = self.client

    @property
    def client(self) -> WxchatClient:
        """拿 SDK 客户端；**读之前先保证快照够新**（过期就增量刷新一次）。

        把刷新挂在取客户端的入口上，是因为 C# 侧与 RAG 索引都会经这里访问数据——
        挂在任何单个工具方法里都会漏掉另一条路（rag_* 走的是 rag_dispatcher，但它同样用 self._adapter.client）。
        """
        if self._client is None:
            self._client = WxchatClient()          # SDK 构造时自己会做第一次快照
            self._last_snapshot_at = time.monotonic()
        else:
            self._maybe_refresh_snapshot()
        return self._client

    def _maybe_refresh_snapshot(self) -> None:
        """快照超过 SNAPSHOT_MAX_AGE_S 就增量刷新（只复制变化的文件，代价小）。"""
        if self._client is None or SNAPSHOT_MAX_AGE_S <= 0:
            return
        now = time.monotonic()
        if now - self._last_snapshot_at < SNAPSHOT_MAX_AGE_S:
            return
        # 先记时间：刷新失败也不要每轮都重试（微信长时间锁库时会把请求拖垮）
        self._last_snapshot_at = now
        try:
            stats = self._client.refresh()   # 公开 API：增量复制 + 清会话名缓存
        except Exception as exc:  # noqa: BLE001
            _log_stderr(f"[snapshot] 刷新微信库快照失败（本轮继续用旧快照）：{exc}")
            return
        copied = int(stats.get("copied_files", 0) or 0)
        if copied:
            _log_stderr(f"[snapshot] 已刷新微信库快照：{copied} 个文件 / {stats.get('copied_mb', 0)}MB")

    def close(self) -> None:
        self._client = None

    def invoke(self, method: str, params: dict[str, Any]) -> Any:
        handler = getattr(self, f"tool_{method}", None)
        if handler is None:
            raise ValueError(f"unknown method: {method}")
        return handler(params)

    # ---- 工具实现 ------------------------------------------------------

    def tool_list_sessions(self, params: dict[str, Any]) -> list[dict[str, Any]]:
        limit = int(params.get("limit", 20))
        keyword = str(params.get("keyword", ""))
        sessions = self.client.sessions()
        if keyword:
            sessions = self.client.find_session(keyword)
        out = []
        for s in sessions[:limit]:
            out.append({
                "username": s.username,
                "is_chatroom": s.is_chatroom,
                "summary": _mask(s.summary),
                "last_timestamp": s.last_timestamp,
                "last_time_text": self._time_text(s.last_timestamp),
                "unread_count": s.unread_count,
            })
        return out

    def tool_find_sessions(self, params: dict[str, Any]) -> list[dict[str, Any]]:
        """按"人 / 会话名"找会话，并可按类型过滤（只有私聊 / 只有群）。

        与 list_sessions 的区别：list_sessions 的关键词只匹配 wxid / 会话摘要 / 最后发言人，
        所以"张晓明"会命中"她最后发言的那个群"。这里额外用备注名/昵称（display_names）匹配，
        并能按类型过滤，才回答得了"我和张晓明的私聊"这类问题。
        private_only: True 只要私聊 / False 只要群 / None 不限。
        """
        keyword = str(params.get("keyword", "")).strip()
        limit = int(params.get("limit", 10))
        private_only = params.get("private_only")

        sessions = self.client.sessions()
        names: dict[str, str] = {}
        if keyword:
            try:
                names = self.client.display_names([s.username for s in sessions]) or {}
            except Exception as exc:  # noqa: BLE001
                _log_stderr(f"[find_sessions] display_names failed: {exc}")

        hits: list[dict[str, Any]] = []
        for s in sessions:
            display = (names.get(s.username) or "").strip()
            if keyword and keyword not in s.username and keyword not in display:
                continue
            if private_only is True and s.is_chatroom:
                continue
            if private_only is False and not s.is_chatroom:
                continue
            hits.append({
                "username": s.username,
                "display_name": display,
                "is_chatroom": s.is_chatroom,
                "summary": _mask(s.summary),
                "last_timestamp": s.last_timestamp,
                "last_time_text": self._time_text(s.last_timestamp),
            })
        hits.sort(key=lambda h: h["last_timestamp"], reverse=True)
        return hits[:limit]

    def tool_read_rendered(self, params: dict[str, Any]) -> list[dict[str, Any]]:
        """按会话 + 时间范围导出"可以直接展示"的消息。

        比 read_messages 多做的事：
          - 解析内容类型（文本 / 图片 / 语音 / 表情包 / 文件 / 链接），UI 才能像微信那样原样渲染；
          - 图片按 welive 的 XOR/AES 密钥解密落盘，返回本地文件路径（text 之外的"原样显示"关键）；
          - 语音导出为 wav，可直接播放。
        密钥与账号目录优先从 welive.yaml 读（dec），缺失时该条消息只给文字占位。
        """
        session_id = str(params.get("session_id", "")).strip()
        if not session_id:
            raise ValueError("session_id required")
        begin = int(params.get("begin", 0) or 0)
        end = int(params.get("end", 0) or 0)
        limit = int(params.get("limit", 400) or 400)
        media_dir = Path(str(params.get("media_dir") or DEFAULT_MEDIA_DIR))
        media_dir.mkdir(parents=True, exist_ok=True)

        out = media_dir / f"_render_{abs(hash((session_id, begin, end))) % 10**10}.jsonl"
        dec = getattr(self.client, "dec", {}) or {}
        args = [
            "export-session", "--session-id", session_id,
            "--out", str(out), "--jsonl", "--parse-content", "--media-dir", str(media_dir),
            "--asc", "--batch-size", "20000",
        ]
        xor = str(dec.get("image_xor_key") or "").strip()
        aes = str(dec.get("image_aes_key") or "").strip()
        if xor:
            args += ["--image-xor-key", xor]
        if aes:
            args += ["--image-aes-key", aes]
        account_dir = str(params.get("account_dir") or "").strip() or self._account_dir(dec)
        if account_dir:
            args += ["--account-dir", account_dir]
        if begin:
            args += ["--begin", str(begin)]
        if end:
            args += ["--end", str(end)]

        self.client.cli.run_raw(*args)

        raw: list[dict[str, Any]] = []
        if out.exists():
            for line in out.read_text(encoding="utf-8").splitlines():
                if not line.strip():
                    continue
                value = json.loads(line)
                raw.extend(value if isinstance(value, list) else [value])
        try:
            out.unlink()
        except OSError:
            pass

        names = self._display_names({str(m.get("sender_username") or "") for m in raw})
        mine = self._my_senders()
        items: list[dict[str, Any]] = []
        sticker_rows: list[dict[str, Any]] = []
        for m in raw[-limit:] if len(raw) > limit else raw:
            uid = str(m.get("sender_username") or "")
            sender_name = names.get(uid, "")
            # SDK 的 is_self 标志实测几乎全是 False（自己发的消息会被当成对方），
            # 仿微信界面靠它分左右气泡，所以这里用"自己的账号 wxid / 显示名"兜底判定。
            is_self = bool(_int_safe(m.get("is_self"))) or uid in mine or (sender_name in mine if sender_name else False)
            local_type = _int_safe(m.get("local_type"))
            if local_type == 47:
                sticker_rows.append(m)
            items.append({
                "local_id": m.get("local_id"),
                "create_time": _int_safe(m.get("create_time")),
                "sender_username": uid,
                "display_name": sender_name,
                "is_self": is_self,
                "local_type": local_type,
                "kind": _render_kind(local_type),
                "text": _mask(str(m.get("message_content") or "")),
                "media_path": m.get("media_path") or "",
                "media_error": m.get("media_error") or "",
            })
        if sticker_rows:
            self._fill_sticker_captions(items, sticker_rows)
        return items

    def _fill_sticker_captions(self, items: list[dict[str, Any]], rows: list[dict[str, Any]]) -> None:
        """表情包（local_type=47）：把 md5 抠出来，再拿表情库里的中文描述。

        export-session 解析过的 message_content 只会给 "[表情包]" 占位，md5 得回原始
        消息库取（WCDB 把内容 zstd 压缩在 message_content 列里）。拿不到就让 text 留空，
        UI 侧退回 "[表情包]"，不猜也不编。
        """
        if _zstd is None:
            _log_stderr("[read_rendered] 未安装 zstandard，表情包只能显示 [表情包]")
            return
        by_key: dict[tuple[str, str], set[int]] = {}
        for m in rows:
            db_path = str(m.get("_db_path") or m.get("db_path") or "")
            table = str(m.get("table_name") or "")
            local_id = _int_safe(m.get("local_id"))
            if db_path and table and local_id:
                by_key.setdefault((db_path, table), set()).add(local_id)

        md5_of: dict[int, str] = {}
        dctx = _zstd.ZstdDecompressor()
        for (db_path, table), ids in by_key.items():
            id_list = ",".join(str(i) for i in sorted(ids))
            try:
                queried = self.client.cli.run(
                    "exec", "--kind", "message", "--path", db_path, "--sql",
                    f"SELECT local_id, hex(message_content) AS mc FROM {table} "
                    f"WHERE local_type=47 AND local_id IN ({id_list})")
            except Exception as exc:  # noqa: BLE001
                _log_stderr(f"[read_rendered] 表情包 md5 查询失败: {exc}")
                continue
            for r in queried or []:
                content = str(r.get("mc") or "")
                if not content.lower().startswith(_ZSTD_MAGIC):
                    continue
                try:
                    xml = dctx.decompress(bytes.fromhex(content), max_output_size=65536)
                    hit = _EMOJI_MD5.search(xml.decode("utf-8", "replace"))
                except Exception:  # noqa: BLE001
                    continue
                if hit:
                    md5_of[_int_safe(r.get("local_id"))] = hit.group(1).lower()

        captions = self._sticker_captions(list(md5_of.values()))
        for item in items:
            if _render_kind(_int_safe(item.get("local_type"))) != "sticker":
                continue
            md5 = md5_of.get(_int_safe(item.get("local_id")), "")
            item["text"] = _mask(captions.get(md5, ""))

    def _sticker_captions(self, md5s: list[str]) -> dict[str, str]:
        """md5 → 中文描述（商店表情在 kStoreEmoticonCaptionsTable，自建表情在其自带 caption）。"""
        out: dict[str, str] = {}
        missing = [m for m in dict.fromkeys(md5s) if m and m not in self._caption_cache]
        if missing:
            in_list = ",".join(f"'{m}'" for m in missing)
            queries = (
                "SELECT md5_ AS md5, caption_ AS caption FROM kStoreEmoticonCaptionsTable "
                f"WHERE language_='default' AND md5_ IN ({in_list})",
                "SELECT md5 AS md5, caption AS caption FROM kNonStoreEmoticonTable "
                f"WHERE md5 IN ({in_list})",
            )
            for sql in queries:
                try:
                    for r in self.client.cli.run("exec", "--kind", "emoticon", "--sql", sql) or []:
                        md5 = str(r.get("md5") or "").lower()
                        caption = str(r.get("caption") or "").strip()
                        if md5 and caption:
                            out[md5] = caption
                except Exception as exc:  # noqa: BLE001
                    _log_stderr(f"[read_rendered] 表情描述查询失败: {exc}")
            for m in missing:
                self._caption_cache[m] = out.get(m, "")
        return {m: self._caption_cache.get(m, "") for m in md5s}

    def _my_senders(self) -> set[str]:
        """自己的发送者标识：账号 wxid（去掉 _dN 后缀）+ 它的显示名。"""
        ids: set[str] = set()
        try:
            dec = getattr(self.client, "dec", {}) or {}
            full = str(dec.get("wxid", "") or "")
            no_suffix = re.sub(r"_d\d+$", "", full) if full else ""
            if not no_suffix:
                return ids
            ids.add(no_suffix)
            name = (self.client.display_names({no_suffix}) or {}).get(no_suffix, "")
            if name:
                ids.add(str(name))
        except Exception as exc:  # noqa: BLE001
            _log_stderr(f"[read_rendered] resolve self failed: {exc}")
        return ids

    def _account_dir(self, dec: dict[str, Any]) -> str:
        """账号目录 = database_root / wxid（图片 .dat 与 hardlink 都在这里）。"""
        root = str(dec.get("database_root") or "").strip()
        wxid = str(dec.get("wxid") or "").strip()
        if not root or not wxid:
            return ""
        candidate = Path(root) / wxid
        return str(candidate) if candidate.exists() else ""

    def _display_names(self, usernames: set[str]) -> dict[str, str]:
        query = {u for u in usernames if u}
        if not query:
            return {}
        try:
            return self.client.display_names(query) or {}
        except Exception as exc:  # noqa: BLE001
            _log_stderr(f"[read_rendered] display_names failed: {exc}")
            return {}

    @staticmethod
    def _is_my_name(sender_name: str, mine: set[str]) -> bool:
        """按显示名判"是不是我发的"（SDK 的 is_self 不可靠，这是兜底的一环）。"""
        return bool(sender_name) and sender_name in mine

    @staticmethod
    def _time_text(ts: Any) -> str:
        """Unix 秒 → **本地**时间文本。

        必须由工具自己给出：模型看不到时区，只拿到裸时间戳时会按 UTC 自己换算，
        于是所有时间差 8 小时（实测：本地 16:37 被显示成 08:37，而且同一份数据在不同问题里
        时区还不一致）。给出本地时间文本，模型就没有换算余地了。
        """
        try:
            n = int(ts)
        except (TypeError, ValueError):
            return ""
        if n <= 0:
            return ""
        try:
            return datetime.fromtimestamp(n).strftime("%Y-%m-%d %H:%M")
        except (ValueError, OSError, OverflowError):
            return ""

    def tool_read_messages(
        self, params: dict[str, Any]
    ) -> list[dict[str, Any]]:
        session_id = str(params["session_id"])
        limit = int(params.get("limit", 30))
        offset = int(params.get("offset", 0))
        begin = int(params.get("begin", 0) or 0)
        end = int(params.get("end", 0) or 0)

        if not begin and not end:
            messages = self.client.messages(session_id, limit=limit, offset=offset)
        else:
            # 时间范围查询：messages() 只能从最新往回翻，先取最近 limit 条再过滤的话，
            # 查历史某天永远是空（最近 limit 条早就不是那天了）。所以这里要一直往回翻到早于 begin，
            # 再做区间过滤——"翻出我和某人某天的聊天记录"靠的就是这条路径。
            collected: list[Any] = []
            page = max(100, limit)
            walked = 0
            for _ in range(20):  # 最多 20 页（约 2000 条）兜底，避免旧日期把 Bridge 拖死
                batch = self.client.messages(session_id, limit=page, offset=walked)
                if not batch:
                    break
                collected.extend(batch)
                oldest = min((m.create_time for m in batch), default=0)
                if begin and oldest and oldest < begin:
                    break
                if len(batch) < page:
                    break
                walked += page
            messages = [
                m for m in collected
                if (not begin or m.create_time >= begin) and (not end or m.create_time <= end)
            ][:limit]
        names = self.client.display_names({m.sender_username for m in messages})
        # SDK 的 is_self 实测几乎全是 False（自己发的会被标成对方）。不兜底判定的话，模型会看到
        # 每一行都"是对方发的"，于是把你自己写的话当成对方说的，还会困惑"记录里没标成你自己"。
        mine = self._my_senders()
        # 会话身份：私聊里 session_name 就是**对方**，群聊里是群名。
        # 不给这个字段，模型只能从"发送者名"猜会话对方是谁——私聊里你自己发的那条
        # （is_self=true，display_name 恰好又是你自己的名字）很容易被当成聊天对象，
        # 于是得出"这个会话只有一个人"这类荒谬结论。
        session_is_chatroom = session_id.endswith("@chatroom")
        session_name = session_id
        try:
            resolved = self._display_names({session_id})
            if resolved.get(session_id):
                session_name = str(resolved[session_id])
        except Exception as exc:  # noqa: BLE001
            _log_stderr(f"[read_messages] resolve session name failed: {exc}")
        out = []
        for m in messages:
            sender_name = names.get(m.sender_username, m.sender_username)
            out.append({
                "create_time": m.create_time,
                "time_text": self._time_text(m.create_time),
                "sender": m.sender_username,
                "display_name": sender_name,
                "is_self": bool(m.is_self) or m.sender_username in mine or self._is_my_name(sender_name, mine),
                "local_type": m.local_type,
                "content": _mask(m.message_content),
                "session_name": session_name,
                "session_is_chatroom": session_is_chatroom,
            })
        return out

    def tool_search_messages(
        self, params: dict[str, Any]
    ) -> list[dict[str, Any]]:
        keyword = str(params["keyword"])
        session_id = str(params.get("session_id", ""))
        limit = int(params.get("limit", 20))
        begin = int(params.get("begin", 0) or 0)
        end = int(params.get("end", 0) or 0)

        messages = self.client.search(
            keyword, session_id=session_id, limit=limit, begin=begin, end=end
        )
        names = self.client.display_names({m.sender_username for m in messages})
        mine = self._my_senders()   # 同上：SDK 的 is_self 不可靠，用自己的账号/昵称兜底
        out = []
        for m in messages:
            sender_name = names.get(m.sender_username, m.sender_username)
            out.append({
                "session_id": m.session_id,
                "create_time": m.create_time,
                "time_text": self._time_text(m.create_time),
                "sender": m.sender_username,
                "display_name": sender_name,
                "is_self": bool(m.is_self) or m.sender_username in mine or self._is_my_name(sender_name, mine),
                "local_type": m.local_type,
                "content": _mask(m.message_content),
            })
        return out

    def tool_get_group_members(
        self, params: dict[str, Any]
    ) -> list[dict[str, Any]]:
        chatroom_id = str(params["chatroom_id"])
        members = self.client.group_members(chatroom_id)
        out = []
        for m in members:
            if isinstance(m, dict):
                out.append({
                    "username": m.get("username", ""),
                    "display_name": m.get("display_name", ""),
                })
            else:
                out.append({"username": str(m), "display_name": ""})
        return out

    def tool_get_session_stats(
        self, params: dict[str, Any]
    ) -> dict[str, Any]:
        session_id = str(params.get("session_id", ""))
        begin = int(params.get("begin", 0) or 0)
        end = int(params.get("end", 0) or 0)
        # 会话级统计：不逐条分页全表扫描（避免 Bridge 超时）。
        # 每个会话仅读取最近 limit 条作为样本；精确总数需 SQLite 索引（后续阶段）。
        limit = int(params.get("limit", 200))
        session_limit = 0
        if not session_id:
            # 无 session_id 时只扫最近活跃的若干个会话，且每会话样本量收敛到 50，
            # 避免全量扫描（135+ 会话）让 Bridge 单线程阻塞数十秒。
            session_limit = int(params.get("session_limit", 10))
            limit = min(limit, 50)

        sessions = self.client.sessions()
        if session_id:
            sessions = [s for s in sessions if s.username == session_id]
        else:
            sessions = sorted(
                sessions,
                key=lambda s: getattr(s, "last_timestamp", 0) or 0,
                reverse=True,
            )[:session_limit]

        result: dict[str, Any] = {
            "total_sessions": len(sessions),
            "total_messages": 0,
            "active_days": 0,
            "per_day_top": [],
            "top_senders": [],
            "note": "会话级统计，消息数为已读取样本量（非全量精确值）。",
        }
        if not session_id:
            result["note"] = f"样本统计：仅扫描最近活跃的 {len(sessions)} 个会话。"
        per_day: dict[str, int] = {}
        per_sender: dict[str, int] = {}
        per_session: dict[str, int] = {}
        for s in sessions:
            try:
                batch = self.client.messages(s.username, limit=limit, offset=0)
            except Exception as exc:  # noqa: BLE001
                _log_stderr(f"[stats] skip {s.username}: {exc}")
                continue
            for m in batch:
                if begin and m.create_time < begin:
                    continue
                if end and m.create_time > end:
                    continue
                result["total_messages"] += 1
                day = datetime.fromtimestamp(m.create_time).strftime("%Y-%m-%d")
                per_day[day] = per_day.get(day, 0) + 1
                per_sender[m.sender_username] = per_sender.get(m.sender_username, 0) + 1
                per_session[s.username] = per_session.get(s.username, 0) + 1
        result["active_days"] = len(per_day)
        result["per_day_top"] = sorted(per_day.items(), key=lambda kv: kv[1], reverse=True)[:10]
        top_senders = sorted(per_sender.items(), key=lambda kv: kv[1], reverse=True)[:10]
        # 会话排行："最近和谁聊得最多"问的是会话，而不是发送者（群里发言最多的人≠聊天对象）。
        top_sessions = sorted(per_session.items(), key=lambda kv: kv[1], reverse=True)[:10]
        names: dict[str, str] = {}
        try:
            # 批量取名（备注优先，其次昵称，再次群名）；取不到就留空，由上层回落成 id
            names = self.client.display_names([u for u, _ in top_sessions] + [u for u, _ in top_senders]) or {}
        except Exception as exc:  # noqa: BLE001
            _log_stderr(f"[stats] display_names failed: {exc}")
        result["top_sessions"] = [
            {
                "username": u,
                "display_name": names.get(u, ""),
                "count": c,
                "is_broadcast": _is_broadcast(u),
            }
            for u, c in top_sessions
        ]
        result["top_senders"] = [
            {"username": u, "display_name": names.get(u, ""), "count": c} for u, c in top_senders
        ]
        return result

    def tool_retrieve_memory(
        self, params: dict[str, Any]
    ) -> dict[str, Any]:
        # P1 阶段：RAG 尚在后续阶段实现。
        # 此处返回明确占位，供 Agent Core 阶段替换为真正的向量检索。
        return {
            "status": "not_implemented",
            "message": "RAG retriever will be wired in P3.",
            "query": str(params.get("query", "")),
        }
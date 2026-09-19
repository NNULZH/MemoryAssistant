# -*- coding: utf-8 -*-
"""
RAG 模块：从 wxchat 消息构建可检索的聊天记忆索引。

设计（对应计划 §14-16）：
- Chunk 单元：会话 + 日历日
- 文本形式：`[HH:MM] 昵称: 内容`
- 存储：NPZ(向量) + JSON(元数据)，暴力余弦搜索
- Embedding：本地 BAAI/bge-small-zh-v1.5（sentence_transformers）
"""

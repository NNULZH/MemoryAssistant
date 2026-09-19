# -*- coding: utf-8 -*-
"""主题关键词字典：rag_topics / rag_profiles 共用，避免两处复制。

规则：对 chunk 文本做子串计数（text.count(kw)），每条消息命中多次只算 1 次。
"""
from __future__ import annotations

KEYWORD_TOPICS: dict[str, list[str]] = {
    "课程": ["作业", "考试", "课程", "老师", "实验", "论文", "结课", "选修", "学分", "上课", "期末", "预习", "复习", "课堂", "考试周"],
    "实习工作": ["实习", "面试", "offer", "简历", "上班", "加班", "工资", "入职", "离职", "招聘", "投递", "项目", "代码", "公司", "领导"],
    "游戏": ["游戏", "开黑", "排位", "上分", "段位", "副本", "氪金", "原神", "王者", "英雄联盟", "LOL", "抽卡"],
    "生活美食": ["吃饭", "外卖", "食堂", "火锅", "奶茶", "烧烤", "做饭", "聚餐", "好吃", "夜宵", "零食", "美食"],
    "情感朋友": ["对象", "女朋友", "男朋友", "分手", "喜欢", "暧昧", "兄弟", "闺蜜", "朋友", "相亲", "结婚"],
}


def classify_keywords(text: str) -> dict[str, int]:
    """返回 {主题: 命中关键词次数}；同一主题内多个关键词命中累加（但文本内去重见调用方）。"""
    if not text:
        return {}
    out: dict[str, int] = {}
    for category, words in KEYWORD_TOPICS.items():
        n = 0
        for w in words:
            n += text.count(w)
        if n > 0:
            out[category] = n
    return out

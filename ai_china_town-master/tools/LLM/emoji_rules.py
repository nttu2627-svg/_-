# emoji_rules.py
"""Utility helpers for consistent schedule emoji classification."""
from __future__ import annotations

import json
import re
from functools import lru_cache
from pathlib import Path
from typing import Dict, Sequence, Tuple

_PROJECT_ROOT = Path(__file__).resolve().parents[2]
_SCHEDULE_PATH = _PROJECT_ROOT / "src" / "data" / "schedules.json"

# Ordered configuration so that more specific categories are evaluated first.
_CATEGORY_CONFIG: Sequence[Dict[str, object]] = (
    {
        "label": "睡覺",
        "emoji": "💤",
        "aliases": ["睡覺", "睡眠", "睡觉", "就寢", "睡覺時間"],
        "keywords": ["睡覺", "睡觉", "就寢", "入睡", "準備睡覺", "午睡", "打盹", "小睡", "夜休", "躺下休息"],
    },
    {
        "label": "起床",
        "emoji": "🌅",
        "aliases": ["起床", "起床／晨間", "晨間", "清晨"],
        "keywords": [
            "起床", "醒來", "晨跑", "晨間", "早起", "morning", "晨練", "晨讀", "晨寫", "晨間伸展", "起身",
            "晨間冥想", "晨間規劃", "晨間梳洗", "迎接晨光",
        ],
    },
    {
        "label": "家務",
        "emoji": "🧺",
        "aliases": ["家務", "家務整理", "家務事"],
        "keywords": [
            "家務", "打掃", "清潔", "整理", "收拾", "整理公寓", "整理房間", "整理環境", "整理照片", "準備早餐",
            "做飯並", "煮飯並", "料理並", "家中料理", "洗衣", "烹飪家務",
        ],
    },
    {
        "label": "工作／上課",
        "emoji": "🏫",
        "aliases": ["工作", "上課", "上班", "學習"],
        "keywords": [
            "工作", "上班", "辦公", "會議", "會晤", "協作", "專案", "主持", "激勵同事", "研究課題", "學校", "課程", "上課",
            "教學", "講座", "圖書館", "閱讀教材", "深度工作", "處理機動工作", "監督專案", "指揮", "完成工作", "進行講座",
            "創意工作坊", "研究原理", "學習", "學術", "幫助同事", "繼續解決核心問題", "討論創業專案", "下班",
        ],
    },
    {
        "label": "健身",
        "emoji": "🏋️",
        "aliases": ["健身", "運動"],
        "keywords": ["健身", "運動", "鍛鍊", "鍛练", "跑步", "瑜伽", "伸展", "晨跑後健身", "去健身房"],
    },
    {
        "label": "購物",
        "emoji": "🏬",
        "aliases": ["購物", "逛街"],
        "keywords": ["購物", "採購", "買菜", "買東西", "逛街", "超市", "商場", "shopping"],
    },
    {
        "label": "外食",
        "emoji": "🍽️",
        "aliases": ["外食", "餐廳"],
        "keywords": [
            "餐廳", "外食", "外出用餐", "與同事共進午餐", "與同事午餐", "與朋友午餐", "和朋友午餐", "與客戶午餐",
            "和新朋友吃午餐", "參加晚宴", "外出午餐", "出門解決午餐", "聚餐", "餐聚", "簡單的午餐", "與團隊午餐",
            "和朋友晚餐", "和朋友吃午餐", "團隊午餐", "朋友午餐", "朋友晚餐",
        ],
    },
    {
        "label": "用餐",
        "emoji": "🍱",
        "aliases": ["吃飯", "用餐", "餐食"],
        "keywords": [
            "吃飯", "吃饭", "用餐", "早餐", "午餐", "晚餐", "便當", "工作午餐", "叫外賣午餐", "準備晚餐",
            "準備午餐", "準備餐點", "料理餐點", "晚餐並回家", "和家人晚餐",
        ],
    },
    {
        "label": "回家／居家活動",
        "emoji": "🏠",
        "aliases": ["回家", "居家", "在家"],
        "keywords": [
            "回家", "在家", "居家", "家中", "Apartment", "公寓", "家庭", "玩遊戲", "玩策略遊戲", "看電影", "晚間閱讀", "自然醒",
            "瀏覽技術論壇", "處理戰略問題", "個人專案研究", "專案研究", "研究新科技", "藝術創作", "深夜思考", "上網辯論",
            "網上辯論", "準備派對", "準備家庭派對",
        ],
    },
    {
        "label": "放鬆／咖啡",
        "emoji": "☕",
        "aliases": ["休息", "放鬆", "咖啡"],
        "keywords": [
            "咖啡", "休息", "放鬆", "放松", "小憩", "閱讀或寫作", "閱讀", "寫作", "尋找靈感", "下午茶", "tea", "coffee",
            "咖啡店", "咖啡館", "去咖啡店和朋友碰面", "在咖啡店與人交流",
        ],
    },
    {
        "label": "社交",
        "emoji": "💬",
        "aliases": ["社交", "聊天", "交流"],
        "keywords": ["聊天", "交談", "對話", "交流", "聚會", "線上聊天", "互動", "碰面", "和朋友喝咖啡"],
    },
    {
        "label": "戶外活動",
        "emoji": "🌳",
        "aliases": ["戶外活動", "外出"],
        "keywords": [
            "散步", "公園", "戶外", "外出走動", "在公園", "城市兜風", "騎車", "派對時間", "戶外即興", "帶寵物",
        ],
    },
)

SPECIAL_LABELS: Dict[str, str] = {
    "繼續解決核心問題": "工作／上課",
    "回家進行晚間閱讀": "回家／居家活動",
    "個人專案研究": "回家／居家活動",
    "玩策略遊戲": "回家／居家活動",
    "去咖啡店和朋友碰面": "放鬆／咖啡",
    "在咖啡店與人交流": "放鬆／咖啡",
    "準備派對": "回家／居家活動",
    "在網上辯論": "回家／居家活動",
    "討論創業專案": "外食",
    "工作午餐": "用餐",
    "下班": "工作／上課",
    "下班並去健身房": "健身",
    "幫助同事": "工作／上課",
    "與團隊午餐": "外食",
}

@lru_cache(maxsize=1)
def _load_schedule_emojis() -> Sequence[str]:
    try:
        data = json.loads(_SCHEDULE_PATH.read_text(encoding="utf-8"))
    except Exception:
        return tuple(cfg["emoji"] for cfg in _CATEGORY_CONFIG)
    emoji_set = {
        entry.get("emoji")
        for profile in data.values()
        for entry in profile.get("dailySchedule", [])
        if isinstance(entry, dict) and entry.get("emoji")
    }
    return tuple(sorted(emoji_set))

_ALLOWED_EMOJIS = set(_load_schedule_emojis())
if not _ALLOWED_EMOJIS:
    _ALLOWED_EMOJIS = {cfg["emoji"] for cfg in _CATEGORY_CONFIG}

CATEGORY_RULES: Tuple[Dict[str, object], ...] = tuple(
    cfg for cfg in _CATEGORY_CONFIG if cfg["emoji"] in _ALLOWED_EMOJIS
)

CATEGORY_TO_EMOJI: Dict[str, str] = {
    cfg["label"]: str(cfg["emoji"])
    for cfg in CATEGORY_RULES
}

_LABEL_ALIASES: Dict[str, str] = {}
for cfg in CATEGORY_RULES:
    label = str(cfg["label"])
    _LABEL_ALIASES[label] = label
    for alias in cfg.get("aliases", []) or []:
        _LABEL_ALIASES[str(alias)] = label

# Build keyword map (lowercase for case-insensitive matching)
_KEYWORD_MAP: Dict[str, str] = {}
for cfg in CATEGORY_RULES:
    label = str(cfg["label"])
    for keyword in cfg.get("keywords", []) or []:
        _KEYWORD_MAP[str(keyword).lower()] = label

_EMOJI_PATTERN = None
if _ALLOWED_EMOJIS:
    pattern_parts = sorted((_emoji for _emoji in _ALLOWED_EMOJIS), key=len, reverse=True)
    _EMOJI_PATTERN = re.compile("|".join(re.escape(part) for part in pattern_parts))
_DEFAULT_LABEL = next(iter(CATEGORY_TO_EMOJI))
_DEFAULT_EMOJI = CATEGORY_TO_EMOJI[_DEFAULT_LABEL]


def allowed_emojis() -> Tuple[str, ...]:
    """Return all emojis that are allowed for activity classification."""
    return tuple(sorted(_ALLOWED_EMOJIS))


def normalize_label(raw: str) -> str:
    """Normalize an activity description to a canonical label."""
    if not raw:
        return _DEFAULT_LABEL
    candidate = str(raw).strip()
    if candidate in SPECIAL_LABELS:
        return SPECIAL_LABELS[candidate]
    if candidate in _LABEL_ALIASES:
        return _LABEL_ALIASES[candidate]
    lowered = candidate.lower()
    # Exact keyword matches first
    for keyword, label in _KEYWORD_MAP.items():
        if keyword and keyword in lowered:
            return label
    # Partial alias containment
    for alias, label in _LABEL_ALIASES.items():
        if alias and alias in candidate:
            return label
    return _DEFAULT_LABEL


def classify_activity(raw: str) -> Tuple[str, str]:
    """Return the canonical label and emoji for the given activity string."""
    if not raw:
        return _DEFAULT_LABEL, _DEFAULT_EMOJI
    candidate = str(raw).strip()
    if _EMOJI_PATTERN and _EMOJI_PATTERN.search(candidate):
        for label, emoji in CATEGORY_TO_EMOJI.items():
            if str(emoji) in candidate:
                return label, emoji
    label = normalize_label(candidate)
    emoji = CATEGORY_TO_EMOJI.get(label, _DEFAULT_EMOJI)
    return label, emoji


def match_known_emoji(text: str) -> str:
    """Extract a known emoji from text, or fall back to the default."""
    if not isinstance(text, str):
        return _DEFAULT_EMOJI
    if _EMOJI_PATTERN:
        match = _EMOJI_PATTERN.search(text)
        if match:
            emoji = match.group(0)
            if emoji in _ALLOWED_EMOJIS:
                return emoji
    _, emoji = classify_activity(text)
    return emoji
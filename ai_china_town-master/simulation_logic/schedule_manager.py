# schedule_manager.py (功能增強版)

"""
此模組提供從 JSON 檔案載入預設行程表的功能。
`schedules.json` 的預期結構如下::
    {
        "MBTI": {
            "weeklySchedule": { ... },
            "dailySchedule": [
                {"time": "06:30", "action": "起床", "target": "公寓"},
                ...
            ]
        }
    }

其中 `time` 為開始時間、`action` 為要執行的行動、`target` 為
行動的目標地點。若缺少 `target`，則預設與 `action` 相同。此
模組會將 `dailySchedule` 轉換為 `[['行動', '開始時間', '目標'], ...]`
的格式，供後端及代理人邏輯使用。
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional

@dataclass
class 行程項目:
    """代表一個行程項目。"""

    行動: str
    開始時間: str
    目標: str


def 格式化時間(time_str: str) -> str:
    """將多種時間格式統一為 ``HH-MM``。"""
    time_str = str(time_str).replace(":", "-")
    parts = time_str.split("-")
    return f"{int(parts[0]):02d}-{int(parts[1]):02d}"

def 從檔案載入行程表(agent_id: str, 檔案路徑: str) -> Optional[List[List[str]]]:
    """為指定代理人從 JSON 檔案載入行程表。

    參數:
        agent_id: 代理人的識別字串（如 MBTI）。
        檔案路徑: ``schedules.json`` 的路徑。

    回傳:
        ``[['行動', '開始時間', '目標'], ...]`` 或 ``None`` 如果載入失敗。
    """
    try:
        with open(檔案路徑, "r", encoding="utf-8") as f:
            all_schedules = json.load(f)
        
        agent_schedule_data = all_schedules.get(agent_id)
        if not agent_schedule_data or "dailySchedule" not in agent_schedule_data:
             return None

        formatted_schedule: List[List[str]] = []
        for item in agent_schedule_data["dailySchedule"]:
            start_time = item.get("time")
            action = item.get("action")
            target = item.get("target") or action
            if start_time and action:
                formatted_schedule.append([action, 格式化時間(start_time), target])
        
        formatted_schedule.sort(key=lambda x: datetime.strptime(x[1], "%H-%M"))
        return formatted_schedule
        
    except (FileNotFoundError, json.JSONDecodeError, KeyError) as e:
        print(f"❌ [行程管理器] 載入或解析檔案 '{檔案路徑}' 失敗: {e}")
        return None
# schedule_manager.py (功能简化版)

"""
此模組提供從 JSON 檔案載入預設行程表的功能。
目前使用的 schedules.json 結構如下::

    {
        "MBTI": {
            "weeklySchedule": { ... },
            "dailySchedule": [
                {"time": "06:30", "action": "起床", "target": "公寓"},
                ...
            ]
        }
    }

函式會讀取 `dailySchedule` 並轉換為後端所需的
`[['行動', '開始時間']]` 格式。
"""

import json
from datetime import datetime
from typing import List, Optional

# 行程项目的资料结构 (与 agent_memory.py 中的类似，但可以独立)
class 行程項目:
    def __init__(self, 開始時間: str, 行動: str, 目標: str = ""):
        # 结束时间现在由下一个项目的开始时间决定
        self.開始時間 = 開始時間
        self.行動 = 行動
        self.目標 = 目標 if 目標 else 行動

def 格式化時間(time_str: str) -> str:
    """将多种时间格式统一为 HH-MM"""
    time_str = str(time_str).replace(":", "-")
    parts = time_str.split('-')
    return f"{int(parts[0]):02d}-{int(parts[1]):02d}"

def 從檔案載入行程表(agent_id: str, 檔案路徑: str) -> Optional[List[List[str]]]:
    """
    為指定代理人從 JSON 檔案載入行程表，並轉換為後端所需的格式。
    返回格式: [['行動', '開始時間 HH-MM'], ...]
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
            if start_time and action:
                formatted_schedule.append([action, 格式化時間(start_time)])
        
        formatted_schedule.sort(key=lambda x: datetime.strptime(x[1], "%H-%M"))
        return formatted_schedule
        
    except (FileNotFoundError, json.JSONDecodeError, KeyError) as e:
        print(f"❌ [行程管理器] 載入或解析檔案 '{檔案路徑}' 失敗: {e}")
        return None
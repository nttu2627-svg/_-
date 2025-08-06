# schedule_manager.py (功能简化版)

"""
此模組提供从 JSON 档案载入预设行程表的功能。
"""

import json
import datetime
from typing import Dict, List, Optional

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
    为指定代理人从 JSON 档案载入行程表，并转换为后端所需的格式。
    返回格式: [['行动', '开始时间 HH-MM'], ...]
    """
    try:
        with open(檔案路徑, "r", encoding="utf-8") as f:
            all_schedules = json.load(f)
        
        agent_schedule_data = all_schedules.get(agent_id)
        if not agent_schedule_data or "行程清單" not in agent_schedule_data:
            return None

        # 转换为后端使用的 [行动, 开始时间] 格式
        formatted_schedule = []
        for item in agent_schedule_data["行程清單"]:
            start_time = item.get("開始時間")
            action = item.get("行動")
            if start_time and action:
                formatted_schedule.append([action, 格式化時間(start_time)])
        
        # 按照开始时间排序
        formatted_schedule.sort(key=lambda x: datetime.strptime(x[1], '%H-%M'))
        return formatted_schedule
        
    except (FileNotFoundError, json.JSONDecodeError, KeyError) as e:
        print(f"❌ [行程管理器] 载入或解析档案 '{檔案路徑}' 失败: {e}")
        return None
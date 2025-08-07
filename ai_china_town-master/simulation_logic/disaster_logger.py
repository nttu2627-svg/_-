# disaster_logger.py (合作评分重构版)

"""
此模組負責在災難模擬過程中記錄事件，並在模擬結束時計算代理人的評價分數。
"""

from datetime import datetime
from collections import defaultdict
from typing import Dict, List, Any

class 事件紀錄:
    def __init__(self, 時間戳: datetime, 事件類型: str, 詳細資料: Dict[str, Any]):
        self.時間戳 = 時間戳
        self.事件類型 = 事件類型
        self.詳細資料 = 詳細資料

class 災難記錄器:
    def __init__(self):
        self.代理人事件: Dict[str, List[事件紀錄]] = defaultdict(list)
        self.災難開始時間: datetime | None = None

    def 設定災難開始(self, 開始時間: datetime):
        """設定灾难的模拟开始时间。"""
        self.災難開始時間 = 開始時間
        print(f"[災難記錄器] 灾难开始时间已设定为: {開始時間}")

    def 記錄事件(self, 代理人_id: str, 事件類型: str, 記錄時間: datetime, 詳細資料: Dict[str, Any]):
        """向记录器新增一个事件，并使用传入的模拟时间。"""
        if self.災難開始時間 is None and 事件類型 != "初始化": return
        記錄 = 事件紀錄(記錄時間, 事件類型, 詳細資料)
        self.代理人事件[代理人_id].append(記錄)

    def 計算評分(self, 代理人最終狀態: Dict[str, Any]) -> Dict[str, Dict[str, float]]:
        """
        根据事件记录和代理人最终状态，计算每个代理人的评分。
        代理人最終狀態 的格式应为: { "代理人ID": {"hp": 最终HP值} }
        """
        結果: Dict[str, Dict[str, float]] = {}
        
        # 第一遍：处理所有事件，收集原始数据
        原始數據 = defaultdict(lambda: {
            "損失": 0.0, "反應時間": float('inf'), 
            "合作事件": [], "爭吵次數": 0
        })

        for agent_id, events in self.代理人事件.items():
            for e in events:
                if e.事件類型 == "損失":
                    原始數據[agent_id]["損失"] += float(e.詳細資料.get("value", 0))
                elif e.事件類型 == "反應" and self.災難開始時間:
                    response_time = (e.時間戳 - self.災難開始時間).total_seconds()
                    if response_time < 原始數據[agent_id]["反應時間"]:
                        原始數據[agent_id]["反應時間"] = response_time
                elif e.事件類型 == "合作":
                    # 记录合作事件的详情
                    原始數據[agent_id]["合作事件"].append(e.詳細資料)
                elif e.事件類型 == "爭吵":
                    原始數據[agent_id]["爭吵次數"] += 1

        # 第二遍：根据原始数据和最终状态计算分数
        for agent_id, data in 原始數據.items():
            # 1. 损失分数
            loss_score = max(0.0, 10.0 - (data["損失"] / 10.0))

            # 2. 反应分数
            response_score = 0.0
            if data["反應時間"] != float('inf'):
                response_score = max(0.0, 10.0 - ((max(0, data["反應時間"] - 5)) / 55.0) * 10.0) # 5秒内满分

            # 3. 合作分数
            coop_score = 0.0
            有效合作次數 = 0
            for coop_event in data["合作事件"]:
                受助者_id = coop_event.get("受助者")
                原始HP = coop_event.get("原始HP")
                
                if 受助者_id and 原始HP is not None:
                    受助者最終狀態 = 代理人最終狀態.get(受助者_id)
                    if 受助者最終狀態:
                        最終HP = 受助者最終狀態.get("hp")
                        # "有效合作"的定义：受助者的最终HP高于他受伤时的HP
                        if 最終HP is not None and 最終HP > 原始HP:
                            有效合作次數 += 1
            
            # 每个有效合作加 2.5 分，最多 10 分
            coop_score = min(10.0, 有效合作次數 * 2.5)

            # 4. 争吵扣分
            爭吵懲罰 = data["爭吵次數"] * 2.0 # 每次争吵扣 2 分

            # 5. 计算总分
            total = (loss_score + response_score + coop_score) - 爭吵懲罰
            total = max(0.0, total) # 总分不能低于0

            結果[agent_id] = {
                "loss_score": round(loss_score, 2),
                "response_score": round(response_score, 2),
                "coop_score": round(coop_score, 2),
                "total_score": round(total, 2),
                "notes": f"有效合作 {有效合作次數} 次, 爭吵 {data['爭吵次數']} 次"
            }
        return 結果

    def 生成報表(self, 代理人最終狀態: Dict[str, Any]) -> Dict[str, Any]:
        """產出包含各代理人分數細項的報表。"""
        評分結果 = self.計算評分(代理人最終狀態)
        行數 = ["--- 災難模擬評分報表 ---"]
        for agent_id, scores in 評分結果.items():
            行數.append(
                f"{agent_id}: 總分 {scores['total_score']} (損失 {scores['loss_score']}, 反應 {scores['response_score']}, 合作 {scores['coop_score']})"
            )
            行數.append(f"  {scores['notes']}")
        報表文字 = "\n".join(行數)
        return {"scores": 評分結果, "text": 報表文字}

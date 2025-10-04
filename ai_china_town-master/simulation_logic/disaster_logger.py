# disaster_logger.py (合作评分重构版)

"""
此模組負責在災難模擬過程中記錄事件，並在模擬結束時計算代理人的評價分數。
"""

from datetime import datetime
from collections import defaultdict
from typing import Any, Dict, List

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
        """設定災難的模擬起始時間。"""
        self.災難開始時間 = 開始時間
        print(f"[災難記錄器] 災難開始時間：{開始時間}")

    def 記錄事件(self, 代理人_id: str, 事件類型: str, 記錄時間: datetime, 詳細資料: Dict[str, Any]):
        """新增一筆事件記錄。"""
        if self.災難開始時間 is None and 事件類型 != "初始化":
            return
        記錄 = 事件紀錄(記錄時間, 事件類型, 詳細資料)
        self.代理人事件[代理人_id].append(記錄)
        message = 詳細資料.get("message") if isinstance(詳細資料, dict) else None
        if message:
            print(f"[災難記錄器] {代理人_id} - {事件類型}: {message}")

    def 計算評分(self, 代理人最終狀態: Dict[str, Any]) -> Dict[str, Dict[str, float]]:
        """
        根據事件記錄與模擬結束時的代理人狀態計算分數。
        代理人最終狀態格式：{"代理人ID": {"hp": 最終HP}}
        """
        結果: Dict[str, Dict[str, float]] = {}
        

        原始數據 = defaultdict(
            lambda: {
                "損失": 0.0,
                "反應時間": float("inf"),
                "合作事件": [],
                "爭吵次數": 0,
            }
        )

        for agent_id, events in self.代理人事件.items():
            for event in events:
                if event.事件類型 == "損失":
                    原始數據[agent_id]["損失"] += float(event.詳細資料.get("value", 0))
                elif event.事件類型 == "反應" and self.災難開始時間:
                    response_time = (event.時間戳 - self.災難開始時間).total_seconds()
                    if response_time < 原始數據[agent_id]["反應時間"]:
                        原始數據[agent_id]["反應時間"] = response_time
                elif event.事件類型 == "合作":
                    原始數據[agent_id]["合作事件"].append(event.詳細資料)
                elif event.事件類型 == "爭吵":
                    原始數據[agent_id]["爭吵次數"] += 1

        for agent_id, data in 原始數據.items():
            loss_score = max(0.0, 10.0 - (data["損失"] / 10.0))

            response_score = 0.0
            if data["反應時間"] != float("inf"):
                response_score = max(0.0, 10.0 - ((max(0, data["反應時間"] - 5)) / 55.0) * 10.0)

            coop_events = data["合作事件"]
            total_coop_events = len(coop_events)
            有效合作次數 = 0
            for coop_event in coop_events:
                受助者_id = coop_event.get("受助者")
                原始HP = coop_event.get("原始HP")
                
                if 受助者_id and 原始HP is not None:
                    受助者狀態 = 代理人最終狀態.get(受助者_id)
                    if 受助者狀態:
                        最終HP = 受助者狀態.get("hp")
                        if 最終HP is not None and 最終HP > 原始HP:
                            有效合作次數 += 1
            

            coop_score = min(10.0, 有效合作次數 * 2.5)
            爭吵懲罰 = data["爭吵次數"] * 2.0

            total = max(0.0, (loss_score + response_score + coop_score) - 爭吵懲罰)

            結果[agent_id] = {
                "loss_score": round(loss_score, 2),
                "response_score": round(response_score, 2),
                "coop_score": round(coop_score, 2),
                "total_score": round(total, 2),
                "合作次數": total_coop_events,
                "notes": f"記錄合作 {total_coop_events} 次, 有效合作 {有效合作次數} 次, 爭吵 {data['爭吵次數']} 次",
            }
        return 結果

    def 生成報表(self, 代理人最終狀態: Dict[str, Any]) -> Dict[str, Any]:
        """產出整體評分報表。"""
        評分結果 = self.計算評分(代理人最終狀態)
        行數 = ["--- 災難模擬評分報表 ---", ""]
        # 建立表頭與資料列，並計算欄寬以利排版
        表頭 = ["代理人", "總分", "損失", "反應", "合作", "合作次數"]
        資料列: List[List[str]] = []
        欄寬 = [len(欄) for 欄 in 表頭]

        for agent_id, scores in 評分結果.items():
            列資料 = [
                str(agent_id),
                f"{scores['total_score']:.2f}",
                f"{scores['loss_score']:.2f}",
                f"{scores['response_score']:.2f}",
                f"{scores['coop_score']:.2f}",
                str(scores.get("合作次數", 0)),
            ]
            資料列.append(列資料)
            欄寬 = [max(欄寬[i], len(列資料[i])) for i in range(len(表頭))]

        if 資料列:
            行數.append("  ".join(表頭[i].ljust(欄寬[i]) for i in range(len(表頭))))
            行數.append("-" * (sum(欄寬) + 2 * (len(表頭) - 1)))
            行數.append("")

            for 列資料, (agent_id, scores) in zip(資料列, 評分結果.items()):
                行數.append("  ".join(列資料[i].ljust(欄寬[i]) for i in range(len(列資料))))
                if scores.get("notes"):
                    行數.append(f"  • {scores['notes']}")
                行數.append("")

        while 行數 and 行數[-1] == "":
            行數.pop()

        報表文字 = "\n".join(行數)
        return {"scores": 評分結果, "text": 報表文字}

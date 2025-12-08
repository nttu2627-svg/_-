# simulation_logic/failure_analyzer.py
# ============================================================================
# 失敗案例分析模組 (Failure Analysis Module)
# ============================================================================
# 目的：誠實呈現系統缺陷，作為學術研究的重要部分
# 功能：
# 1. 偵測代理人卡住 (Stuck)
# 2. 偵測不合理行為 (Irrational Behavior)
# 3. 記錄發生時間與可能原因
# 4. 生成失敗案例分析報告
# ============================================================================

from datetime import datetime
from typing import Dict, List, Any, Optional
from dataclasses import dataclass, field
from enum import Enum
import json


class FailureType(Enum):
    """失敗類型分類"""
    STUCK = "stuck"                           # 代理人卡住
    IRRATIONAL_ACTION = "irrational_action"   # 不合理行為
    LOCATION_MISMATCH = "location_mismatch"   # 位置與行動不匹配
    MEMORY_ERROR = "memory_error"             # 記憶檢索錯誤
    PROMPT_FAILURE = "prompt_failure"         # Prompt 生成失敗
    NAVIGATION_ERROR = "navigation_error"     # 導航錯誤
    LLM_HALLUCINATION = "llm_hallucination"   # LLM 幻覺


@dataclass
class FailureCase:
    """單個失敗案例的資料結構"""
    failure_id: str
    failure_type: FailureType
    agent_name: str
    sim_time: str                    # 模擬內時間
    real_time: str                   # 真實時間
    phase: str                       # 當時的模擬階段 (Normal/Earthquake/Recovery)
    description: str                 # 錯誤描述
    agent_action: str                # 當時的行動
    agent_location: str              # 當時的位置
    target_location: str             # 目標位置
    potential_causes: List[str]      # 可能原因列表
    context: Dict[str, Any] = field(default_factory=dict)  # 額外上下文
    severity: str = "medium"         # 嚴重程度: low, medium, high, critical


class FailureAnalyzer:
    """
    失敗案例分析器
    
    用於偵測、記錄和分析代理人的異常行為
    """
    
    # 不合理行為定義：特定情境下不應該出現的行動
    IRRATIONAL_BEHAVIORS = {
        "Earthquake": [
            "睡覺", "sleep", "休息", "rest", "nap",
            "購物", "shopping", "逛街",
            "學習", "study", "reading", "閱讀",
            "娛樂", "entertainment", "遊戲", "game",
        ],
        "Recovery": [
            "睡覺", "sleep",  # 災後仍在睡覺可能不合理
        ],
    }
    
    # 位置與行動的合理組合
    VALID_LOCATION_ACTION_PAIRS = {
        "Apartment_F1": ["睡覺", "休息", "起床", "醒來", "sleep", "wake", "rest"],
        "Apartment_F2": ["睡覺", "休息", "起床", "醒來", "sleep", "wake", "rest"],
        "School": ["學習", "上課", "study", "class", "教學"],
        "Gym": ["運動", "健身", "exercise", "workout"],
        "Super": ["購物", "買東西", "shopping", "grocery"],
        "Rest": ["用餐", "吃飯", "eat", "dining", "lunch", "dinner", "breakfast"],
        "Subway": ["通勤", "移動", "travel", "commute"],
    }
    
    def __init__(self):
        self.failure_cases: List[FailureCase] = []
        self._failure_counter = 0
        self._last_positions: Dict[str, tuple] = {}  # agent_name -> (position, time)
        self._stuck_counters: Dict[str, int] = {}     # agent_name -> stuck count
        
    def _generate_failure_id(self) -> str:
        """生成唯一的失敗案例 ID"""
        self._failure_counter += 1
        return f"FAIL-{self._failure_counter:04d}"
    
    def check_agent_behavior(
        self,
        agent,
        sim_time: str,
        phase: str,
        context: Optional[Dict[str, Any]] = None
    ) -> Optional[FailureCase]:
        """
        檢查單個代理人的行為是否異常
        
        Args:
            agent: TownAgent 實例
            sim_time: 模擬內時間 (如 "09:30")
            phase: 當前模擬階段
            context: 額外上下文資訊
            
        Returns:
            如果偵測到異常，返回 FailureCase；否則返回 None
        """
        failure_case = None
        
        # 1. 檢查不合理行為 (例如：地震時睡覺)
        failure_case = self._check_irrational_behavior(agent, sim_time, phase, context)
        if failure_case:
            self.failure_cases.append(failure_case)
            return failure_case
        
        # 2. 檢查位置與行動不匹配
        failure_case = self._check_location_action_mismatch(agent, sim_time, phase, context)
        if failure_case:
            self.failure_cases.append(failure_case)
            return failure_case
        
        return None
    
    def check_stuck_agent(
        self,
        agent,
        current_position: tuple,
        target_position: tuple,
        sim_time: str,
        phase: str,
        movement_threshold: float = 0.5
    ) -> Optional[FailureCase]:
        """
        檢查代理人是否卡住
        
        Args:
            agent: TownAgent 實例
            current_position: 當前位置 (x, y)
            target_position: 目標位置 (x, y)
            sim_time: 模擬內時間
            phase: 當前模擬階段
            movement_threshold: 位移閾值
            
        Returns:
            如果偵測到卡住，返回 FailureCase；否則返回 None
        """
        agent_name = getattr(agent, 'name', str(agent))
        
        # 計算與上次位置的距離
        if agent_name in self._last_positions:
            last_pos, last_time = self._last_positions[agent_name]
            distance = ((current_position[0] - last_pos[0])**2 + 
                       (current_position[1] - last_pos[1])**2) ** 0.5
            
            # 計算與目標的距離
            distance_to_target = ((current_position[0] - target_position[0])**2 + 
                                  (current_position[1] - target_position[1])**2) ** 0.5
            
            if distance < movement_threshold and distance_to_target > 1.0:
                # 可能卡住了
                self._stuck_counters[agent_name] = self._stuck_counters.get(agent_name, 0) + 1
                
                if self._stuck_counters[agent_name] >= 3:
                    failure_case = FailureCase(
                        failure_id=self._generate_failure_id(),
                        failure_type=FailureType.STUCK,
                        agent_name=agent_name,
                        sim_time=sim_time,
                        real_time=datetime.now().isoformat(),
                        phase=phase,
                        description=f"代理人卡在 {current_position}，距離目標 {distance_to_target:.2f} 單位",
                        agent_action=getattr(agent, 'curr_action', 'Unknown'),
                        agent_location=getattr(agent, 'curr_place', 'Unknown'),
                        target_location=getattr(agent, 'target_place', 'Unknown'),
                        potential_causes=[
                            "NavMesh 路徑規劃失敗",
                            "傳送門觸發條件未滿足",
                            "代理人被其他物件阻擋",
                            "目標位置不在 NavMesh 上",
                        ],
                        context={
                            "position": current_position,
                            "target": target_position,
                            "stuck_count": self._stuck_counters[agent_name],
                        },
                        severity="high"
                    )
                    self.failure_cases.append(failure_case)
                    self._stuck_counters[agent_name] = 0
                    return failure_case
            else:
                self._stuck_counters[agent_name] = 0
        
        self._last_positions[agent_name] = (current_position, sim_time)
        return None
    
    def _check_irrational_behavior(
        self,
        agent,
        sim_time: str,
        phase: str,
        context: Optional[Dict[str, Any]] = None
    ) -> Optional[FailureCase]:
        """檢查不合理行為"""
        agent_name = getattr(agent, 'name', str(agent))
        action = getattr(agent, 'curr_action', '')
        
        if not action or phase not in self.IRRATIONAL_BEHAVIORS:
            return None
        
        action_lower = action.lower()
        irrational_keywords = self.IRRATIONAL_BEHAVIORS[phase]
        
        for keyword in irrational_keywords:
            if keyword.lower() in action_lower:
                # 確定潛在原因
                potential_causes = []
                
                if phase == "Earthquake":
                    potential_causes = [
                        "Prompt 未強調地震優先級",
                        "代理人記憶未正確更新地震狀態",
                        "LLM 幻覺：忽略災難情境",
                        "排程系統覆蓋了緊急行為",
                    ]
                elif phase == "Recovery":
                    potential_causes = [
                        "災後恢復邏輯未正確觸發",
                        "代理人未感知到災難結束",
                        "LLM 生成了不適當的行動",
                    ]
                
                return FailureCase(
                    failure_id=self._generate_failure_id(),
                    failure_type=FailureType.IRRATIONAL_ACTION,
                    agent_name=agent_name,
                    sim_time=sim_time,
                    real_time=datetime.now().isoformat(),
                    phase=phase,
                    description=f"在 {phase} 階段執行了不合理的行動：{action}",
                    agent_action=action,
                    agent_location=getattr(agent, 'curr_place', 'Unknown'),
                    target_location=getattr(agent, 'target_place', 'Unknown'),
                    potential_causes=potential_causes,
                    context={
                        "mbti": getattr(agent, 'mbti', 'Unknown'),
                        "health": getattr(agent, 'health', 100),
                        "memory_snippet": str(getattr(agent, 'memory', ''))[-200:],
                    },
                    severity="high" if phase == "Earthquake" else "medium"
                )
        
        return None
    
    def _check_location_action_mismatch(
        self,
        agent,
        sim_time: str,
        phase: str,
        context: Optional[Dict[str, Any]] = None
    ) -> Optional[FailureCase]:
        """檢查位置與行動是否匹配"""
        agent_name = getattr(agent, 'name', str(agent))
        action = getattr(agent, 'curr_action', '')
        location = getattr(agent, 'curr_place', '')
        
        if not action or not location:
            return None
        
        # 標準化位置名稱
        location_key = None
        for key in self.VALID_LOCATION_ACTION_PAIRS.keys():
            if key.lower() in location.lower():
                location_key = key
                break
        
        if location_key and location_key in self.VALID_LOCATION_ACTION_PAIRS:
            valid_actions = self.VALID_LOCATION_ACTION_PAIRS[location_key]
            action_lower = action.lower()
            
            # 檢查是否有任何有效行動匹配
            is_valid = any(va.lower() in action_lower for va in valid_actions)
            
            # 如果是移動類行動，視為合理
            if any(kw in action_lower for kw in ["移動", "走", "去", "前往", "travel", "go", "walk"]):
                is_valid = True
            
            # 如果是地震中，任何求生行動都合理
            if phase == "Earthquake":
                is_valid = True
            
            if not is_valid and phase == "Normal":
                return FailureCase(
                    failure_id=self._generate_failure_id(),
                    failure_type=FailureType.LOCATION_MISMATCH,
                    agent_name=agent_name,
                    sim_time=sim_time,
                    real_time=datetime.now().isoformat(),
                    phase=phase,
                    description=f"在 {location} 執行了不相關的行動：{action}",
                    agent_action=action,
                    agent_location=location,
                    target_location=getattr(agent, 'target_place', 'Unknown'),
                    potential_causes=[
                        "LLM 生成的行動與位置不符",
                        "代理人位置更新延遲",
                        "Prompt 未提供足夠的位置資訊",
                    ],
                    context={
                        "valid_actions": valid_actions,
                    },
                    severity="low"
                )
        
        return None
    
    def log_llm_error(
        self,
        agent_name: str,
        sim_time: str,
        phase: str,
        error_type: str,
        error_message: str,
        prompt_snippet: str = ""
    ):
        """
        記錄 LLM 相關錯誤
        
        Args:
            agent_name: 代理人名稱
            sim_time: 模擬內時間
            phase: 當前階段
            error_type: 錯誤類型 ("hallucination", "parse_error", "timeout")
            error_message: 錯誤訊息
            prompt_snippet: Prompt 片段
        """
        failure_type = FailureType.LLM_HALLUCINATION if "hallucination" in error_type.lower() \
                       else FailureType.PROMPT_FAILURE
        
        potential_causes = []
        if "hallucination" in error_type.lower():
            potential_causes = [
                "模型溫度設定過高",
                "Prompt 指令不夠明確",
                "上下文窗口限制導致資訊遺失",
            ]
        elif "parse" in error_type.lower():
            potential_causes = [
                "LLM 輸出格式不符合預期",
                "JSON 解析失敗",
                "特殊字符導致解析錯誤",
            ]
        elif "timeout" in error_type.lower():
            potential_causes = [
                "LLM 服務回應過慢",
                "網路連線問題",
                "模型負載過高",
            ]
        
        failure_case = FailureCase(
            failure_id=self._generate_failure_id(),
            failure_type=failure_type,
            agent_name=agent_name,
            sim_time=sim_time,
            real_time=datetime.now().isoformat(),
            phase=phase,
            description=f"LLM 錯誤 ({error_type}): {error_message}",
            agent_action="N/A",
            agent_location="N/A",
            target_location="N/A",
            potential_causes=potential_causes,
            context={
                "error_type": error_type,
                "prompt_snippet": prompt_snippet[:500] if prompt_snippet else "",
            },
            severity="medium"
        )
        self.failure_cases.append(failure_case)
    
    def generate_report(self) -> Dict[str, Any]:
        """
        生成失敗案例分析報告
        
        Returns:
            報告資料字典
        """
        if not self.failure_cases:
            return {
                "total_failures": 0,
                "summary": "本次模擬未偵測到明顯的失敗案例。",
                "cases": [],
            }
        
        # 統計各類型失敗數量
        type_counts = {}
        severity_counts = {"low": 0, "medium": 0, "high": 0, "critical": 0}
        agent_failure_counts = {}
        
        for case in self.failure_cases:
            # 類型統計
            type_name = case.failure_type.value
            type_counts[type_name] = type_counts.get(type_name, 0) + 1
            
            # 嚴重程度統計
            severity_counts[case.severity] = severity_counts.get(case.severity, 0) + 1
            
            # 代理人統計
            agent_failure_counts[case.agent_name] = agent_failure_counts.get(case.agent_name, 0) + 1
        
        # 找出問題最多的代理人
        most_problematic_agent = max(agent_failure_counts.items(), key=lambda x: x[1]) \
                                  if agent_failure_counts else ("None", 0)
        
        # 生成摘要
        summary_parts = [
            f"本次模擬共偵測到 {len(self.failure_cases)} 個失敗案例。\n",
        ]
        
        if type_counts:
            summary_parts.append("失敗類型分布：\n")
            for t, count in sorted(type_counts.items(), key=lambda x: -x[1]):
                summary_parts.append(f"  - {t}: {count} 次\n")
        
        if most_problematic_agent[1] > 1:
            summary_parts.append(f"\n問題最多的代理人：{most_problematic_agent[0]} ({most_problematic_agent[1]} 次)")
        
        return {
            "total_failures": len(self.failure_cases),
            "type_distribution": type_counts,
            "severity_distribution": severity_counts,
            "agent_failure_counts": agent_failure_counts,
            "most_problematic_agent": most_problematic_agent[0],
            "summary": "".join(summary_parts),
            "cases": [
                {
                    "id": case.failure_id,
                    "type": case.failure_type.value,
                    "agent": case.agent_name,
                    "sim_time": case.sim_time,
                    "phase": case.phase,
                    "description": case.description,
                    "action": case.agent_action,
                    "location": case.agent_location,
                    "potential_causes": case.potential_causes,
                    "severity": case.severity,
                }
                for case in self.failure_cases
            ],
        }
    
    def generate_markdown_report(self) -> str:
        """生成 Markdown 格式的報告"""
        report = self.generate_report()
        
        if report["total_failures"] == 0:
            return "# 失敗案例分析報告\n\n本次模擬未偵測到明顯的失敗案例。"
        
        md = ["# 失敗案例分析報告\n"]
        md.append(f"生成時間：{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n\n")
        
        # 摘要
        md.append("## 摘要\n\n")
        md.append(f"- **總失敗數**：{report['total_failures']}\n")
        md.append(f"- **問題最多的代理人**：{report.get('most_problematic_agent', 'N/A')}\n\n")
        
        # 類型分布
        md.append("## 失敗類型分布\n\n")
        md.append("| 類型 | 數量 |\n|------|------|\n")
        for t, count in report.get("type_distribution", {}).items():
            md.append(f"| {t} | {count} |\n")
        md.append("\n")
        
        # 詳細案例
        md.append("## 詳細案例\n\n")
        for case in report.get("cases", []):
            md.append(f"### {case['id']} - {case['type']}\n\n")
            md.append(f"- **代理人**：{case['agent']}\n")
            md.append(f"- **模擬時間**：{case['sim_time']}\n")
            md.append(f"- **階段**：{case['phase']}\n")
            md.append(f"- **嚴重程度**：{case['severity']}\n")
            md.append(f"- **描述**：{case['description']}\n")
            md.append(f"- **當時行動**：{case['action']}\n")
            md.append(f"- **當時位置**：{case['location']}\n\n")
            
            if case.get("potential_causes"):
                md.append("**可能原因**：\n")
                for cause in case["potential_causes"]:
                    md.append(f"  - {cause}\n")
                md.append("\n")
            
            md.append("---\n\n")
        
        return "".join(md)
    
    def clear(self):
        """清除所有記錄"""
        self.failure_cases.clear()
        self._failure_counter = 0
        self._last_positions.clear()
        self._stuck_counters.clear()


# ====== 全局實例 ======
_failure_analyzer: Optional[FailureAnalyzer] = None

def get_failure_analyzer() -> FailureAnalyzer:
    """取得失敗分析器單例"""
    global _failure_analyzer
    if _failure_analyzer is None:
        _failure_analyzer = FailureAnalyzer()
    return _failure_analyzer

def reset_failure_analyzer():
    """重置失敗分析器"""
    global _failure_analyzer
    _failure_analyzer = FailureAnalyzer()
    return _failure_analyzer

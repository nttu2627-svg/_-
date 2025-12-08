# simulation_logic/report_generator.py
# ============================================================================
# 模擬結束後的 LLM 報告生成模組
# ============================================================================
# 功能：
# 1. 生成代理人「質性故事」（災後日記）
# 2. 生成「系統效能分析報告」
# ============================================================================

import asyncio
import os
import json
from datetime import datetime
from typing import Dict, List, Any, Optional

# ====== Prompt Templates ======

STORYTELLING_PROMPT = """
# Role
You are {Agent_Name}, a {Age}-year-old {Occupation} with the MBTI personality type: {MBTI_Type}.
You have just survived a severe earthquake simulation.

# Context
The simulation has ended. Here is a summary of your performance and experiences:
- **Final Health Status:** {Health_Status} (HP: {HP}/100)
- **Scores:** Loss Score: {Loss_Score}, Reaction Score: {Reaction_Score}, Cooperation Score: {Cooperation_Score}
- **Key Events Log:**
{Key_Events_Log}
(Note: The log contains your thoughts, dialogues, and actions during the disaster.)

# Task
Based on your MBTI personality and the events above, write a first-person "Disaster Diary Entry" (approx. 200-300 words).

# Requirements
1. **Reflect on your decisions:** Why did you choose to run/hide/help others? (e.g., As an INTJ, did you prioritize logic? As an ESFJ, did you prioritize people?)
2. **Explain the outcome:** How do you feel about your final health and cooperation score?
3. **Tone:** Your writing style must strictly match your {MBTI_Type} traits (e.g., ENTJ: confident and direct; INFP: emotional and reflective).
4. **Language:** Traditional Chinese (Taiwan).

# Output Format
## [{Agent_Name} 的災後日記]
(Your story here...)
"""

PERFORMANCE_ANALYSIS_PROMPT = """
# Role
You are a Senior System Performance Analyst specializing in Real-time AI Simulations (Unity + LLM Integration).

# Input Data
I have conducted a simulation with {Agent_Count} agents. Here are the performance metrics collected:

1. **LLM Generation Speed (Token/sec):**
   - Average Speed: {Avg_Token_Sec} tokens/sec
   - Fastest Response: {Max_Token_Sec} tokens/sec
   - Slowest Response: {Min_Token_Sec} tokens/sec

2. **Unity Client Performance (FPS):**
   - Average FPS: {Avg_FPS}
   - FPS during heavy LLM processing: {Min_FPS}

3. **Latency Correlation:**
   - Average "Thinking Time" (Time from Request to Action): {Avg_Latency} seconds.

# Task
Analyze these metrics and generate a brief "Technical Performance Report".

# Requirements
1. **Analyze the Trade-off:** Discuss how the LLM token generation speed impacted the Unity FPS. Did the FPS drop when agents were "thinking" (generating text)?
2. **Latency Impact:** Is the {Avg_Latency} second delay acceptable for a disaster simulation? How did it affect the visual fluidity?
3. **Optimization Suggestions:** Based on the data, suggest one improvement (e.g., caching, smaller models, async handling).
4. **Language:** Traditional Chinese (Taiwan), Academic tone.

# Output Format
## [系統效能與延遲分析報告]
**1. LLM 推論效能分析:**
(Your analysis...)

**2. 對即時渲染 (FPS) 之影響:**
(Your analysis...)

**3. 綜合評估與優化建議:**
(Your conclusion...)
"""


class ReportGenerator:
    """模擬結束後的報告生成器"""
    
    def __init__(self, llm_module):
        """
        初始化報告生成器
        
        Args:
            llm_module: LLM 模組參考 (通常是 tools.LLM.run_gpt_prompt)
        """
        self.llm = llm_module
        self.performance_metrics: Dict[str, List[float]] = {
            "token_speeds": [],          # tokens/sec
            "response_times": [],         # seconds
            "fps_samples": [],            # Unity FPS samples
        }
        self._start_time: Optional[float] = None
        self._total_tokens: int = 0
        self._total_llm_time: float = 0.0
    
    def start_tracking(self):
        """開始追蹤效能指標"""
        import time
        self._start_time = time.time()
        self.performance_metrics = {
            "token_speeds": [],
            "response_times": [],
            "fps_samples": [],
        }
        self._total_tokens = 0
        self._total_llm_time = 0.0
    
    def record_llm_call(self, tokens: int, elapsed_seconds: float):
        """記錄單次 LLM 呼叫的效能數據"""
        if elapsed_seconds > 0:
            speed = tokens / elapsed_seconds
            self.performance_metrics["token_speeds"].append(speed)
            self.performance_metrics["response_times"].append(elapsed_seconds)
            self._total_tokens += tokens
            self._total_llm_time += elapsed_seconds
    
    def record_fps(self, fps: float):
        """記錄 Unity FPS 樣本"""
        if fps > 0:
            self.performance_metrics["fps_samples"].append(fps)
    
    def _calculate_stats(self) -> Dict[str, Any]:
        """計算統計數據"""
        speeds = self.performance_metrics["token_speeds"]
        fps_list = self.performance_metrics["fps_samples"]
        response_times = self.performance_metrics["response_times"]
        
        def safe_avg(lst): return sum(lst) / len(lst) if lst else 0
        def safe_max(lst): return max(lst) if lst else 0
        def safe_min(lst): return min(lst) if lst else 0
        
        return {
            "avg_token_sec": round(safe_avg(speeds), 2),
            "max_token_sec": round(safe_max(speeds), 2),
            "min_token_sec": round(safe_min(speeds), 2),
            "avg_fps": round(safe_avg(fps_list), 1),
            "min_fps": round(safe_min(fps_list), 1),
            "avg_latency": round(safe_avg(response_times), 2),
            "total_llm_calls": len(speeds),
            "total_tokens": self._total_tokens,
        }
    
    async def generate_storytelling(self, agent, disaster_logger) -> str:
        """
        為單個代理人生成災後日記
        
        Args:
            agent: TownAgent 實例
            disaster_logger: 災難記錄器實例
            
        Returns:
            生成的故事文字
        """
        # 從 agent 取得必要資訊
        name = getattr(agent, 'name', 'Unknown')
        mbti = getattr(agent, 'mbti', 'INFP')
        health = getattr(agent, 'health', 100)
        memory = getattr(agent, 'memory', '')
        
        # 推斷年齡和職業 (如果 agent 沒有這些屬性，使用預設值)
        age = getattr(agent, 'age', 25)
        occupation = getattr(agent, 'occupation', '上班族')
        
        # 從災難記錄器取得分數
        scores = {
            "loss": disaster_logger.取得損失分數(name) if hasattr(disaster_logger, '取得損失分數') else 0,
            "reaction": disaster_logger.取得反應分數(name) if hasattr(disaster_logger, '取得反應分數') else 0,
            "cooperation": disaster_logger.取得合作分數(name) if hasattr(disaster_logger, '取得合作分數') else 0,
        }
        
        # 取得關鍵事件日誌
        key_events = disaster_logger.取得代理人事件(name) if hasattr(disaster_logger, '取得代理人事件') else []
        events_log = "\n".join([f"- {event}" for event in key_events[-20:]]) if key_events else "(無特別記錄)"
        
        # 健康狀態描述
        if health >= 80:
            health_status = "輕微受傷"
        elif health >= 50:
            health_status = "中度受傷"
        elif health > 0:
            health_status = "重傷"
        else:
            health_status = "不治身亡"
        
        # 填充 Prompt
        prompt = STORYTELLING_PROMPT.format(
            Agent_Name=name,
            Age=age,
            Occupation=occupation,
            MBTI_Type=mbti,
            Health_Status=health_status,
            HP=health,
            Loss_Score=scores["loss"],
            Reaction_Score=scores["reaction"],
            Cooperation_Score=scores["cooperation"],
            Key_Events_Log=events_log,
        )
        
        try:
            # 使用現有的 LLM 呼叫機制
            if hasattr(self.llm, 'ollama_agent') and self.llm.ollama_agent:
                response = await self.llm.ollama_agent.ollama_stream_generate_response(
                    prompt=prompt,
                    special_instruction="請務必使用繁體中文回答，以第一人稱撰寫災後日記。",
                    expect_json=False
                )
                return response if response else f"## [{name} 的災後日記]\n(生成失敗)"
            else:
                return f"## [{name} 的災後日記]\n(LLM 未初始化)"
        except Exception as e:
            print(f"❌ [ReportGenerator] 生成 {name} 故事時發生錯誤: {e}")
            return f"## [{name} 的災後日記]\n(生成過程發生錯誤: {e})"
    
    async def generate_performance_analysis(self, agent_count: int) -> str:
        """
        生成系統效能分析報告
        
        Args:
            agent_count: 模擬中的代理人數量
            
        Returns:
            生成的分析報告文字
        """
        stats = self._calculate_stats()
        
        # 如果沒有足夠數據，返回預設報告
        if stats["total_llm_calls"] < 3:
            return self._generate_default_performance_report(agent_count, stats)
        
        prompt = PERFORMANCE_ANALYSIS_PROMPT.format(
            Agent_Count=agent_count,
            Avg_Token_Sec=stats["avg_token_sec"],
            Max_Token_Sec=stats["max_token_sec"],
            Min_Token_Sec=stats["min_token_sec"],
            Avg_FPS=stats["avg_fps"] if stats["avg_fps"] > 0 else "N/A (未收集)",
            Min_FPS=stats["min_fps"] if stats["min_fps"] > 0 else "N/A",
            Avg_Latency=stats["avg_latency"],
        )
        
        try:
            if hasattr(self.llm, 'ollama_agent') and self.llm.ollama_agent:
                response = await self.llm.ollama_agent.ollama_stream_generate_response(
                    prompt=prompt,
                    special_instruction="請務必使用繁體中文回答，採用學術論文的正式語氣。",
                    expect_json=False
                )
                return response if response else self._generate_default_performance_report(agent_count, stats)
            else:
                return self._generate_default_performance_report(agent_count, stats)
        except Exception as e:
            print(f"❌ [ReportGenerator] 生成效能分析時發生錯誤: {e}")
            return self._generate_default_performance_report(agent_count, stats)
    
    def _generate_default_performance_report(self, agent_count: int, stats: Dict) -> str:
        """生成預設的效能報告（當 LLM 不可用時）"""
        return f"""## [系統效能與延遲分析報告]

**模擬概況:**
- 代理人數量: {agent_count}
- LLM 呼叫總次數: {stats['total_llm_calls']}
- 總生成 Token 數: {stats['total_tokens']}

**1. LLM 推論效能分析:**
- 平均速度: {stats['avg_token_sec']} tokens/sec
- 最快回應: {stats['max_token_sec']} tokens/sec
- 最慢回應: {stats['min_token_sec']} tokens/sec

**2. 延遲分析:**
- 平均思考時間: {stats['avg_latency']} 秒

**3. 備註:**
此報告為自動生成的基礎統計數據。如需更詳細的分析，請確保 LLM 服務可用。
"""
    
    async def generate_all_reports(self, agents: List, disaster_logger, output_dir: str = None) -> Dict[str, Any]:
        """
        生成所有報告
        
        Args:
            agents: 所有代理人列表
            disaster_logger: 災難記錄器
            output_dir: 報告輸出目錄 (可選)
            
        Returns:
            包含所有報告的字典
        """
        reports = {
            "storytelling": {},
            "performance_analysis": "",
            "generated_at": datetime.now().isoformat(),
        }
        
        # 1. 為每個代理人生成故事
        print("📝 [ReportGenerator] 開始生成代理人災後日記...")
        for agent in agents:
            if getattr(agent, 'health', 100) > 0:  # 只為存活者生成
                story = await self.generate_storytelling(agent, disaster_logger)
                reports["storytelling"][agent.name] = story
                print(f"  ✓ {agent.name} 的故事已生成")
        
        # 2. 生成效能分析報告
        print("📊 [ReportGenerator] 開始生成系統效能分析報告...")
        reports["performance_analysis"] = await self.generate_performance_analysis(len(agents))
        print("  ✓ 效能分析報告已生成")
        
        # 3. 儲存到檔案 (可選)
        if output_dir:
            os.makedirs(output_dir, exist_ok=True)
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            
            # 儲存故事
            story_path = os.path.join(output_dir, f"storytelling_{timestamp}.md")
            with open(story_path, "w", encoding="utf-8") as f:
                f.write("# 災後日記集\n\n")
                for name, story in reports["storytelling"].items():
                    f.write(f"{story}\n\n---\n\n")
            
            # 儲存效能報告
            perf_path = os.path.join(output_dir, f"performance_{timestamp}.md")
            with open(perf_path, "w", encoding="utf-8") as f:
                f.write(reports["performance_analysis"])
            
            reports["files"] = {
                "storytelling": story_path,
                "performance": perf_path,
            }
            print(f"📁 [ReportGenerator] 報告已儲存至 {output_dir}")
        
        return reports


# ====== 全局實例 ======
_report_generator: Optional[ReportGenerator] = None

def get_report_generator(llm_module=None) -> ReportGenerator:
    """取得報告生成器單例"""
    global _report_generator
    if _report_generator is None and llm_module:
        _report_generator = ReportGenerator(llm_module)
    return _report_generator

def init_report_generator(llm_module):
    """初始化報告生成器"""
    global _report_generator
    _report_generator = ReportGenerator(llm_module)
    return _report_generator

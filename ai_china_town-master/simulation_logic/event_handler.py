# simulation_logic/event_handler.py (在您的版本基礎上加入偵錯日誌)

import random
from datetime import timedelta
import asyncio

# 確保能從正確的路徑導入
try:
    from tools.LLM.run_gpt_prompt import run_gpt_prompt_summarize_disaster, run_gpt_prompt_pronunciatio
except ImportError:
    print("❌ [event_handler] 警告：無法導入 LLM 模組。")
    # 提供一個假的佔位符函式，以防萬一
    async def run_gpt_prompt_summarize_disaster(*args): return "經歷了一場地震。"
    async def run_gpt_prompt_pronunciatio(*args): return "❓"


def generate_disaster_report(buildings, initial_report=False):
    """生成災前或災後損傷報告的文字。"""
    title = "--- 災前建築狀況評估 ---" if initial_report else "--- 災後最終損傷報告 ---"
    report = [title, "建築狀況:"]
    
    damaged_buildings = [f"  - {name}: 完整度 {bldg.integrity:.1f}%" for name, bldg in buildings.items() if bldg.integrity < 100]
    if damaged_buildings:
        report.extend(sorted(damaged_buildings))
    else:
        report.append("  所有建築狀況良好。")
    
    report.append("----------------------")
    return "\n".join(report)

async def check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context):
    """
    異步處理所有階段轉換，並整合災難事件記錄。
    """
    phase = sim_state.get('phase', 'Normal')
    current_time = sim_state.get('time')
    update_log = llm_context.get('update_log', lambda msg, lvl: print(f"[{lvl}] {msg}"))
    disaster_logger = llm_context.get('disaster_logger')

    # 1. 從正常階段檢查是否進入地震
    if phase == "Normal" and sim_state.get('eq_enabled', False) and sim_state.get('next_event_idx', 0) < len(scheduled_events):
        next_eq = scheduled_events[sim_state['next_event_idx']]
        
        # ### 核心偵錯日誌 ###
        # 每一分鐘的模擬時間，在後端終端機印出一次時間比對狀態，幫助我們確認。
        # current_time.second == 0 這個條件可以確保即使模擬步伐很小，也只會每分鐘印一次。
        if current_time.second == 0:
            print(f"[事件檢查] 當前模擬時間: {current_time} | 下一個地震預計時間: {next_eq['time_dt']}")

        # 核心判斷邏輯
        if current_time >= next_eq['time_dt']:
            # ### 核心偵錯日誌 ###
            # 當條件滿足時，在後端終端機印出一個非常明顯的觸發訊號。
            print(f"🔥🔥🔥 [觸發] 地震事件觸發！當前時間 {current_time} >= 排程時間 {next_eq['time_dt']} 🔥🔥🔥")
            
            sim_state['phase'] = "Earthquake"
            sim_state['quake_details'] = { 'intensity': next_eq['intensity'], 'end_time_dt': current_time + timedelta(minutes=next_eq['duration']) }
            sim_state['next_event_idx'] += 1
            
            update_log(f"!!! 地震開始 !!! 強度: {next_eq['intensity']:.2f}. 持續 {next_eq['duration']} 分鐘.", "EVENT")
            
            if disaster_logger:
                disaster_logger.設定災難開始(current_time)
            
            update_log(generate_disaster_report(buildings, initial_report=True), "REPORT")
            
            pronunciatio_tasks = []
            for agent in agents:
                original_hp = agent.health
                was_asleep = agent.is_asleep(current_time.strftime('%H-%M'))
                
                agent.interrupt_action()
                agent.disaster_experience_log = []
                agent.react_to_earthquake(next_eq['intensity'], buildings, agents)
                
                if disaster_logger:
                    disaster_logger.記錄事件(agent.name, "反應", current_time, {})
                
                damage = original_hp - agent.health
                if damage > 0 and disaster_logger:
                    disaster_logger.記錄事件(agent.name, "損失", current_time, {"value": damage, "reason": "Initial Impact"})

                pronunciatio_tasks.append(run_gpt_prompt_pronunciatio(agent.curr_action))
                
                log_msg_base = f"初步反應: {agent.curr_action}, HP:{agent.health}"
                if was_asleep: update_log(f"  {agent.name}: 在睡夢中被驚醒！{log_msg_base}", "UPDATE")
                else: update_log(f"  {agent.name}: {log_msg_base}, 狀態:{agent.mental_state}", "UPDATE")
            
            pronunciatios = await asyncio.gather(*pronunciatio_tasks)
            for i, agent in enumerate(agents):
                agent.curr_action_pronunciatio = pronunciatios[i]
            return

    # 2. 處理地震中狀態，並檢查是否結束
    if phase == "Earthquake":
        # ... (此區塊邏輯與您提供的版本完全相同，無需修改)
        quake_details = sim_state.get('quake_details')
        if not quake_details: 
            sim_state['phase'] = 'Normal'
            return

        action_tasks = [agent.perform_earthquake_step_action(agents, buildings, quake_details['intensity'], disaster_logger, current_time) for agent in agents if agent.health > 0]
        action_logs = await asyncio.gather(*action_tasks)
        for log in action_logs:
            if log: llm_context['event_log_buffer'].append(log)
        
        if current_time >= quake_details['end_time_dt']:
            sim_state['phase'] = "Recovery"
            sim_state['recovery_end_time'] = current_time + timedelta(minutes=60)
            update_log(f"!!! 地震結束 @ {current_time.strftime('%H:%M')} !!!", "EVENT")
            update_log(generate_disaster_report(buildings, initial_report=False), "REPORT")
            
            summary_tasks = [run_gpt_prompt_summarize_disaster(agent.name, agent.MBTI, agent.health, agent.disaster_experience_log) for agent in agents if agent.disaster_experience_log]
            summaries = await asyncio.gather(*summary_tasks)
            
            agent_with_log = [agent for agent in agents if agent.disaster_experience_log]
            for i, agent in enumerate(agent_with_log):
                summary = summaries[i]
                agent.memory += f"\n[災難記憶] {summary}"
            
            sim_state['quake_details'] = None
            return

    # 3. 處理恢復階段，並檢查是否結束
    if phase == "Recovery":
        # ... (此區塊邏輯與您提供的版本完全相同，無需修改)
        recovery_tasks = [agent.perform_recovery_step_action(agents, buildings, disaster_logger, current_time) for agent in agents if agent.health > 0]
        recovery_logs = await asyncio.gather(*recovery_tasks)
        for log in recovery_logs:
            if log: llm_context['event_log_buffer'].append(log)

        if current_time >= sim_state.get('recovery_end_time', current_time):
            sim_state['phase'] = "PostQuakeDiscussion"
            sim_state['discussion_end_time'] = current_time + timedelta(hours=6)
            update_log("恢復階段結束，進入災後討論期。", "EVENT")
            for agent in agents: agent.last_action = "重新評估中"
            return
            
    # 4. 處理災後討論階段，並檢查是否結束
    if phase == "PostQuakeDiscussion" and current_time >= sim_state.get('discussion_end_time', current_time):
        # ... (此區塊邏輯與您提供的版本完全相同，無需修改)
        sim_state['phase'] = "Normal"
        update_log("災後討論期結束，恢復正常。", "EVENT")
        return
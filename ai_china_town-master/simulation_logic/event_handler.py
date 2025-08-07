# simulation_logic/event_handler.py (灾难记录整合版)

import random
from datetime import timedelta
import asyncio

# 导入异步 LLM 函数
from tools.LLM.run_gpt_prompt import run_gpt_prompt_summarize_disaster, run_gpt_prompt_pronunciatio
def _push_earthquake_update(agents, buildings, intensity, llm_context):
    """將地震狀態推送到 llm_context 的佇列中以供前端使用。"""
    queue = llm_context.setdefault("ws_event_queue", [])
    building_states = {name: {"id": b.id, "integrity": b.integrity} for name, b in buildings.items()}
    agent_states = {
        agent.name: {
            "name": agent.name,
            "currentState": agent.curr_action,
            "location": agent.curr_place,
            "hp": agent.health,
        }
        for agent in agents
    }
    queue.append(
        {
            "type": "earthquake",
            "data": {
                "intensity": intensity,
                "buildingStates": building_states,
                "agentStates": agent_states,
            },
        }
    )

def generate_disaster_report(buildings, initial_report=False):
    """生成災前或災後損傷報告的文字。"""
    title = "--- 災前建築狀況評估 ---" if initial_report else "--- 災後最終損傷報告 ---"
    report = [title, "建築狀況:"]
    
    damaged_buildings = [f"  - {name}: 完整度 {bldg.integrity:.1f}%" for name, bldg in buildings.items() if bldg.integrity < 100]
    if damaged_buildings:
        report.extend(sorted(damaged_buildings))
    else:
        report.append("  所有建築在此次事件中均未受損或狀況良好。")
    
    report.append("----------------------")
    return "\n".join(report)

async def check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context):
    """
    异步处理所有阶段转换，并集成灾难事件记录。
    """
    phase = sim_state.get('phase', 'Normal')
    current_time = sim_state.get('time')
    update_log = llm_context.get('update_log', lambda msg, lvl: print(f"[{lvl}] {msg}"))
    
    # 从 llm_context 中获取记录器实例
    disaster_logger = llm_context.get('disaster_logger')

    # 1. 从正常阶段检查是否进入地震
    if phase == "Normal" and sim_state.get('eq_enabled', False) and sim_state.get('next_event_idx', 0) < len(scheduled_events):
        next_eq = scheduled_events[sim_state['next_event_idx']]
        if current_time >= next_eq['time_dt']:
            sim_state['phase'] = "Earthquake"
            sim_state['quake_details'] = { 'intensity': next_eq['intensity'], 'end_time_dt': current_time + timedelta(minutes=next_eq['duration']) }
            sim_state['next_event_idx'] += 1
            
            update_log(f"!!! 地震開始 !!! 強度: {next_eq['intensity']:.2f}. 持續 {next_eq['duration']} 分鐘.", "EVENT")
            
            # ### 核心修改：设定灾难开始时间并记录事件 ###
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
                
                # 记录反应事件
                if disaster_logger:
                    disaster_logger.記錄事件(agent.name, "反應", current_time, {})
                
                # 记录损失事件
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
            _push_earthquake_update(agents, buildings, next_eq["intensity"], llm_context)
            return

    # 2. 处理地震中状态，并检查是否结束
    if phase == "Earthquake":
        quake_details = sim_state.get('quake_details')
        if not quake_details: sim_state['phase'] = 'Normal'; return

        # 并发执行所有代理人的地震行动
        action_tasks = [agent.perform_earthquake_step_action(agents, buildings, quake_details['intensity'], disaster_logger, current_time) for agent in agents if agent.health > 0]
        action_logs = await asyncio.gather(*action_tasks)
        for log in action_logs:
            if log: llm_context['event_log_buffer'].append(log)
        
        _push_earthquake_update(agents, buildings, quake_details['intensity'], llm_context)
        
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
                if isinstance(agent.memory, str): agent.memory += f"\n[災難記憶] {summary}"
                else: agent.memory = f"[災難記憶] {summary}"
            
            sim_state['quake_details'] = None
            return

    # 3. 处理恢复阶段，并检查是否结束
    if phase == "Recovery":
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
            
    # 4. 处理灾后讨论阶段，并检查是否结束
    if phase == "PostQuakeDiscussion" and current_time >= sim_state.get('discussion_end_time', current_time):
        sim_state['phase'] = "Normal"
        update_log("災後討論期結束，恢復正常。", "EVENT")
        if disaster_logger:
            最終狀態 = {agent.name: {"hp": agent.health} for agent in agents}
            報表 = disaster_logger.生成報表(最終狀態)
            llm_context['evaluation_report'] = 報表
        return
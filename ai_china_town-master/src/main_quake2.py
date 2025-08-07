# src/main_quake2.py (功能整合最终版)

import json
import os
import sys
import time
import traceback
from datetime import datetime, timedelta
import random
import asyncio
import websockets

# --- 专案路径配置 ---
try:
    this_file_path = os.path.abspath(__file__)
    src_dir = os.path.dirname(this_file_path)
    project_root = os.path.dirname(src_dir)
    if project_root not in sys.path: sys.path.insert(0, project_root)
except NameError:
    print("警告: 無法自動設定專案路徑。")

# --- 模组导入 (异步) ---
try:
    from tools.LLM import run_gpt_prompt as llm
    from simulation_logic.agent_classes import TownAgent, Building
    from simulation_logic.agent_memory import find_agent_current_activity, update_agent_schedule
    from simulation_logic.event_handler import check_and_handle_phase_transitions
    from simulation_logic.agent_actions import handle_social_interactions
    from simulation_logic.disaster_logger import 災難記錄器 # ### 新增：导入灾难记录器 ###
    print("✅ [SUCCESS] 所有核心模組已成功導入。")
    LLM_FUNCTIONS = {
        'double_agents_chat': llm.double_agents_chat,
        'generate_inner_monologue': llm.generate_inner_monologue,
        'run_gpt_prompt_summarize_disaster': llm.run_gpt_prompt_summarize_disaster,
        'run_gpt_prompt_pronunciatio': llm.run_gpt_prompt_pronunciatio,
    }
except ImportError as e:
    print(f"❌ [CRITICAL_ERROR] 導入模組失敗，模擬器無法運行: {e}", file=sys.stderr)
    traceback.print_exc(file=sys.stderr)
    LLM_FUNCTIONS = {}

# --- 全局配置 ---
DEFAULT_HOME_LOCATION = "公寓"
SCHEDULE_FILE_PATH = os.path.join(src_dir, "data", "schedules.json")  # 預設行程檔案路徑
simulation_agents = []

# --- 异步模拟核心逻辑 (异步生成器) ---
async def initialize_and_simulate(params):
    global simulation_agents
    
    total_sim_duration_minutes = params.get('duration', 2400)
    schedule_mode = params.get('scheduleMode', 'preset')
    min_per_step_normal_ui = params.get('step', 30)
    start_time_dt = datetime(params.get('year', 2024), params.get('month', 11), params.get('day', 18), params.get('hour', 3), params.get('minute', 0))
    selected_mbti_list = params.get('mbti', [])
    available_locations = params.get('locations', [])
    
    if not available_locations: yield {"type": "error", "message": "錯誤：Unity 未提供可用的地點列表。"}; return
    if not LLM_FUNCTIONS: yield {"type": "error", "message": "後端LLM模組載入失敗"}; return

    yield {"type": "status", "message": "後端開始初始化代理人..."}
    
    agents = [TownAgent(mbti, DEFAULT_HOME_LOCATION, available_locations) for mbti in selected_mbti_list]
    simulation_agents = agents
    
    init_tasks = [agent.initialize_agent(start_time_dt, schedule_mode, SCHEDULE_FILE_PATH) for agent in agents]
    init_results = await asyncio.gather(*init_tasks, return_exceptions=True)
    
    for i, result in enumerate(init_results):
        if isinstance(result, Exception) or not result:
            yield {"type": "error", "message": f"代理人 {agents[i].name} 初始化失敗: {result}"}; return

    buildings = {loc: Building(loc, (0,0)) for loc in available_locations}
    for agent in agents: agent.update_current_building(buildings)

    disaster_logger = 災難記錄器() # ### 新增：初始化灾难记录器 ###

    def get_full_status(current_buildings):
        return {
            "agentStates": { agent.name: { "name": agent.name, "currentState": agent.curr_action, "location": agent.curr_place, "hp": agent.health, "schedule": f"{agent.wake_time} ~ {agent.sleep_time}", "memory": agent.memory, "weeklySchedule": agent.weekly_schedule, "dailySchedule": agent.daily_schedule } for agent in agents },
            "buildingStates": { name: {"id": b.id, "integrity": b.integrity} for name, b in current_buildings.items() }
        }

    _history_log_buffer, _chat_buffer, _event_log_buffer = [], {}, []
    
    def format_log(current_time_dt, current_phase, all_asleep=False):
        current_step_log = []
        sim_time_str = current_time_dt.strftime('%Y年%m月%d日 %H點%M分 (%A)')
        current_step_log.append(f"當前時間: {sim_time_str}")
        if current_phase in ["Earthquake", "Recovery"]:
            current_step_log.append(f"--- {current_phase.upper()} ---")
            if _event_log_buffer: current_step_log.extend(_event_log_buffer); _event_log_buffer.clear()
        elif all_asleep:
            current_step_log.append("所有代理人都在休息中...")
        else:
            for agent in agents:
                pronunciatio = agent.curr_action_pronunciatio
                log_line = f"{agent.name} 當前活動: {agent.curr_action} ({pronunciatio}) --- 所在的地點({agent.curr_place})"
                if agent.curr_action != "聊天" and agent.current_thought: log_line += f"\n  內心想法: 『{agent.current_thought}』"
                current_step_log.append(log_line)
            if _chat_buffer:
                for location, dialogue_str in _chat_buffer.items(): current_step_log.append(f"\n  在 {location} 的聊天內容: {dialogue_str}")
                _chat_buffer.clear()
        current_step_log.append("-" * 60)
        return "\n".join(current_step_log)
    
    sim_end_time_dt = start_time_dt + timedelta(minutes=int(total_sim_duration_minutes))
    eq_enabled = params.get('eq_enabled', False)
    eq_events_json_str = params.get('eq_json', '[]')
    eq_step_minutes_ui = params.get('eq_step', 5)
    scheduled_events = []
    if eq_enabled:
        try:
            for eq_data in json.loads(eq_events_json_str): scheduled_events.append({'time_dt': datetime.strptime(eq_data['time'], "%Y-%m-%d-%H-%M"), 'duration': int(eq_data['duration']), 'intensity': float(eq_data.get('intensity', 0.7))})
        except Exception as e: _history_log_buffer.append(f"[ERROR] 載入地震事件JSON錯誤: {e}")

    sim_state = {'phase': "Normal", 'time': start_time_dt, 'next_event_idx': 0, 'eq_enabled': eq_enabled}
    llm_context = {
        'update_log': lambda msg, lvl: _history_log_buffer.append(f"[{lvl}] {msg}"),
        'chat_buffer': _chat_buffer,
        'event_log_buffer': _event_log_buffer,
        'disaster_logger': disaster_logger,
        'ws_event_queue': []
    }    
    while sim_state['time'] < sim_end_time_dt:
        current_time_dt = sim_state['time']
        current_time_hm_str = current_time_dt.strftime('%H-%M')
        llm_context['current_time_str'] = current_time_hm_str
        
        await check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context)
        
        active_agents = [agent for agent in agents if agent.health > 0 and not agent.is_asleep(current_time_hm_str)]
        all_asleep = not active_agents and sim_state['phase'] == "Normal"

        if not all_asleep and sim_state['phase'] in ["Normal", "PostQuakeDiscussion"]:
            update_tasks = []
            if current_time_dt.hour == 3 and current_time_dt.minute == 0 and sim_state['phase'] == "Normal":
                for agent in agents:
                    if agent.health > 0: update_tasks.append(agent.update_daily_schedule(current_time_dt, schedule_mode, SCHEDULE_FILE_PATH))
            
            for agent in agents:
                update_tasks.append(agent_update_wrapper(agent, active_agents, current_time_hm_str))
            
            await asyncio.gather(*update_tasks)

            if len(active_agents) > 1:
                await handle_social_interactions(active_agents, llm_context, LLM_FUNCTIONS)
        
        current_log = format_log(current_time_dt, sim_state['phase'], all_asleep)
        _history_log_buffer.append(current_log)

        status_data = get_full_status(buildings)
        yield { "type": "update", "data": { "mainLog": current_log, "historyLog": "\n\n".join(_history_log_buffer), "agentStates": status_data["agentStates"], "buildingStates": status_data["buildingStates"], "llmLog": llm.get_llm_log(), "status": f"模擬時間: {current_time_dt.strftime('%H:%M:%S')}" } }
        
        step_minutes = int(min_per_step_normal_ui)
        if sim_state.get('phase') == "Earthquake": step_minutes = int(eq_step_minutes_ui)
        elif sim_state.get('phase') in ["Recovery"]: step_minutes = 10
        sim_state['time'] += timedelta(minutes=step_minutes)
        await asyncio.sleep(0.1)
    
    # ### 新增：模拟结束后计算并发送评分 ###
    final_agent_states = {agent.name: {"hp": agent.health} for agent in agents}
    report = llm_context.get('evaluation_report')
    if not report:
        report = disaster_logger.生成報表(final_agent_states)
    print(f"模擬結束，災後評分: {report['scores']}")
    yield {"type": "evaluation", "data": report}

    yield {"type": "end", "data": { "message": "模擬結束" }}

async def agent_update_wrapper(agent, active_agents, current_time_hm_str):
    """一个统一的封装器来处理代理人的行动更新。"""
    if agent in active_agents:
        if agent.last_action in ["睡覺", "Unconscious", "等待初始化"]: 
            await agent.set_new_action("醒來", agent.home)
        
        new_action = find_agent_current_activity(current_time_hm_str, agent.daily_schedule)
        if agent.curr_action != new_action:
            # 此处简化：目的地直接设为行动名称，具体寻路在 agent.find_path 中处理
            destination = new_action 
            await agent.set_new_action(new_action, destination)
    else: 
        agent.curr_action = "Unconscious" if agent.health <= 0 else "睡覺"
        agent.current_thought = ""
        agent.curr_action_pronunciatio = await llm.run_gpt_prompt_pronunciatio(agent.curr_action)
    
    agent.last_action = agent.curr_action

async def handler(websocket, path):
    print(f"Unity客戶端已連接: {websocket.remote_address}")
    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                command_type = data.get("command")
                if command_type == "start_simulation":
                    print("收到來自Unity的開始模擬指令...")
                    async for update_data in initialize_and_simulate(data['params']):
                        await websocket.send(json.dumps(update_data, ensure_ascii=False))
                elif command_type == "agent_teleport":
                    agent_name = data.get("agent_name")
                    target_portal_name = data.get("target_portal_name")
                    agent_to_teleport = next((agent for agent in simulation_agents if agent.name == agent_name), None)
                    if agent_to_teleport and target_portal_name:
                        agent_to_teleport.teleport(target_portal_name)
            except Exception as e:
                print(f"處理消息時發生錯誤: {e}"); traceback.print_exc()
                if not websocket.closed: await websocket.send(json.dumps({"type": "error", "message": f"後端錯誤: {e}"}))
                break
    except websockets.exceptions.ConnectionClosed as e:
        print(f"Unity客戶端斷開連接: {websocket.remote_address}, 原因: {e}")
    finally:
        print("伺服器處理程序結束。")

async def main():
    if not await llm.initialize_llm():
        print("LLM 初始化失败，程式退出。")
        return
    
    server = await websockets.serve(handler, "localhost", 8765, ping_interval=20, ping_timeout=60)
    print(f"WebSocket 伺服器正在監聽 ws://localhost:8765")
    try:
        await server.wait_closed()
    finally:
        await llm.close_llm_session()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("伺服器被手動關閉。")
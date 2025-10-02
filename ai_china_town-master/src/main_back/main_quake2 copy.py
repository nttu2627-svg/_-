# src/main_quake2.py (依赖注入最终版)

import json
import os
import sys
import time
import traceback
from datetime import datetime, timedelta
import random
import asyncio
import websockets

from simulation_logic.agent_memory import update_agent_schedule

# --- 专案路径配置 ---
try:
    this_file_path = os.path.abspath(__file__)
    src_dir = os.path.dirname(this_file_path)
    project_root = os.path.dirname(src_dir)
    if project_root not in sys.path: sys.path.insert(0, project_root)
except NameError:
    print("警告: 无法自动设定专案路径。")

# --- 模组导入 (异步) ---
LLM_LOADED = False
try:
    # 导入整个 llm 模组，而不是单个函数
    from tools.LLM import run_gpt_prompt as llm
    from simulation_logic.agent_classes import TownAgent, Building
    from simulation_logic.agent_memory import find_agent_current_activity
    from simulation_logic.event_handler import check_and_handle_phase_transitions
    from simulation_logic.agent_actions import handle_social_interactions
    from simulation_logic.disaster_logger import 災難記錄器
    print("✅ [SUCCESS] 所有核心模组已成功導入。")
    LLM_LOADED = True
    # 将所有需要的 LLM 函数打包到一个字典中，以便传递
    LLM_FUNCTIONS = {
        'double_agents_chat': llm.double_agents_chat,
        'generate_inner_monologue': llm.generate_inner_monologue,
        'run_gpt_prompt_summarize_disaster': llm.run_gpt_prompt_summarize_disaster,
        'run_gpt_prompt_pronunciatio': llm.run_gpt_prompt_pronunciatio,
    }
except ImportError as e:
    print(f"❌ [CRITICAL_ERROR] 導入模組失敗，模擬器無法運行: {e}", file=sys.stderr)
    traceback.print_exc(file=sys.stderr)
    LLM_FUNCTIONS = {} # 创建一个空字典以避免后续错误

# --- 全局配置 ---
DEFAULT_HOME_LOCATION = "公寓"
SCHEDULE_FILE_PATH = "data/schedules.json" # ### 新增：定义预设行程档案路径 ###
simulation_agents = []

# --- 异步模拟核心逻辑 (异步生成器) ---
async def initialize_and_simulate(params):
    global simulation_agents
    
    total_sim_duration_minutes = params.get('duration', 2400)
    schedule_mode = params.get('scheduleMode', 'llm') # ### 新增：默认为 'llm' ###
    min_per_step_normal_ui = params.get('step', 30)
    start_time_dt = datetime(params.get('year', 2024), params.get('month', 11), params.get('day', 18), params.get('hour', 3), params.get('minute', 0))
    selected_mbti_list = params.get('mbti', [])
    available_locations = params.get('locations', [])
    
    if not available_locations:
        yield {"type": "error", "message": "错误：Unity 未提供可用的地點列表。"}
        return
    if not LLM_FUNCTIONS:
        yield {"type": "error", "message": "后端LLM模组加载失败"}
        return

    yield {"type": "status", "message": "后端开始初始化代理人..."}
    
    agents = [TownAgent(mbti, DEFAULT_HOME_LOCATION, available_locations) for mbti in selected_mbti_list]
    simulation_agents = agents
    
    init_tasks = [agent_init_wrapper(agent, start_time_dt) for agent in agents]
    init_results = await asyncio.gather(*init_tasks, return_exceptions=True)
    
    for i, result in enumerate(init_results):
        if isinstance(result, Exception) or not result:
            yield {"type": "error", "message": f"代理人 {agents[i].name} 初始化失败: {result}"}
            return

    buildings = {loc: Building(loc, (0,0)) for loc in available_locations}
    for agent in agents: agent.update_current_building(buildings)

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
    llm_context = {'update_log': lambda msg, lvl: _history_log_buffer.append(f"[{lvl}] {msg}"), 'chat_buffer': _chat_buffer, 'event_log_buffer': _event_log_buffer}
    
    while sim_state['time'] < sim_end_time_dt:
        current_time_dt = sim_state['time']
        current_time_hm_str = current_time_dt.strftime('%H-%M')
        llm_context['current_time_str'] = current_time_hm_str
        
        await check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context, LLM_FUNCTIONS)
        
        active_agents = [agent for agent in agents if agent.health > 0 and not agent.is_asleep(current_time_hm_str)]
        all_asleep = not active_agents and sim_state['phase'] == "Normal"

        if not all_asleep and sim_state['phase'] in ["Normal", "PostQuakeDiscussion"]:
            update_tasks = []
            if current_time_dt.hour == 3 and current_time_dt.minute == 0 and sim_state['phase'] == "Normal":
                for agent in agents:
                    if agent.health > 0: update_tasks.append(agent_update_wrapper(agent, 'update_schedule', current_date=current_time_dt))
            
            for agent in agents:
                update_tasks.append(agent_update_wrapper(agent, 'update_action', active_agents=active_agents, current_time_hm_str=current_time_hm_str))
            
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

    yield {"type": "end", "data": { "message": "模擬結束" }}

async def agent_init_wrapper(agent, current_date):
    """封装代理人的完整异步初始化流程。"""
    memory, mem_success = await llm.run_gpt_prompt_generate_initial_memory(agent.name, agent.MBTI, agent.persona_summary, agent.home)
    if not mem_success: return False
    agent.memory = memory
    
    schedule, sched_success = await llm.run_gpt_prompt_generate_weekly_schedule(agent.persona_summary)
    if not sched_success: return False
    agent.weekly_schedule = schedule
    
    return await agent_update_wrapper(agent, 'update_schedule', current_date=current_date)

async def agent_update_wrapper(agent, update_type, **kwargs):
    """一个统一的封装器来处理所有需要LLM的代理人更新。"""
    if update_type == 'update_schedule':
        current_date = kwargs['current_date']
        weekday_name = current_date.strftime('%A')
        today_goal = agent.weekly_schedule.get(weekday_name, "自由活動")
        base_tasks = await llm.run_gpt_prompt_generate_hourly_schedule(agent.persona_summary, current_date.strftime('%Y-%m-%d'), today_goal)
        if not (base_tasks and isinstance(base_tasks[0], list)): return False
        
        wake_time_str = await llm.run_gpt_prompt_wake_up_hour(agent.persona_summary, current_date.strftime('%Y-%m-%d'), base_tasks)
        if not wake_time_str: return False
        
        agent.wake_time = wake_time_str.replace(":", "-")
        try:
            total_duration = sum(int(task[1]) for task in base_tasks)
            agent.sleep_time = (datetime.strptime(agent.wake_time, '%H-%M') + timedelta(minutes=total_duration)).strftime('%H-%M')
        except: 
            agent.sleep_time = (datetime.strptime(agent.wake_time, '%H-%M') + timedelta(hours=16)).strftime('%H-%M')
        
        agent.daily_schedule = update_agent_schedule(agent.wake_time, base_tasks)
        return True

    elif update_type == 'update_action':
        active_agents = kwargs['active_agents']
        current_time_hm_str = kwargs['current_time_hm_str']
        if agent in active_agents:
            if agent.last_action in ["睡覺", "Unconscious", "等待初始化"]: 
                agent.curr_action = "醒來"
                agent.current_thought = "新的一天開始了！"
                agent.curr_action_pronunciatio = await llm.run_gpt_prompt_pronunciatio(agent.curr_action)

            new_action = find_agent_current_activity(current_time_hm_str, agent.daily_schedule)
            if agent.curr_action != new_action:
                destination = agent.home if "家" in new_action else new_action
                agent.curr_action = new_action
                agent.target_place = destination
                agent.curr_place = agent.find_path(destination)
                agent.current_thought = await llm.generate_action_thought(agent.persona_summary, agent.curr_place, new_action)
                agent.curr_action_pronunciatio = await llm.run_gpt_prompt_pronunciatio(agent.curr_action)
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
                        print(f"後端已處理 {agent_name} 的傳送請求，新目標: {agent_to_teleport.curr_place}")
                        
            except Exception as e:
                print(f"處理消息時發生錯誤: {e}"); traceback.print_exc()
                if not websocket.closed:
                    await websocket.send(json.dumps({"type": "error", "message": f"後端錯誤: {e}"}))
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
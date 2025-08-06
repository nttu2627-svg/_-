# src/main_quake1.py (專屬日誌格式版)

import json
import os
import sys
import time
import traceback
from datetime import datetime, timedelta
import random
import gradio as gr

# --- 專案路徑配置 ---
try:
    this_file_path = os.path.abspath(__file__)
    src_dir = os.path.dirname(this_file_path)
    project_root = os.path.dirname(src_dir)
    if project_root not in sys.path: sys.path.insert(0, project_root)
except NameError:
    print("警告: 無法自動設定專案路徑。")

# --- 模組導入 ---
LLM_LOADED = False
try:
    from tools.LLM.run_gpt_prompt import generate_action_thought, run_gpt_prompt_pronunciatio, get_llm_log
    from simulation_logic.agent_classes import TownAgent, Building, DEFAULT_MBTI_TYPES
    from simulation_logic.agent_memory import find_agent_current_activity
    from simulation_logic.event_handler import check_and_handle_phase_transitions
    from simulation_logic.agent_actions import handle_social_interactions
    print("✅ [SUCCESS] 所有核心模組已成功導入。")
    LLM_LOADED = True
except ImportError as e:
    print(f"❌ [CRITICAL_ERROR] 導入模組失敗，模擬器無法運行: {e}", file=sys.stderr)
    traceback.print_exc(file=sys.stderr)
    DEFAULT_MBTI_TYPES = []

# --- 全局配置 ---
MAP = [['醫院', '咖啡店', '#', '蜜雪冰城', '學校', '#', '#', '小芳家', '#', '#', '火鍋店', '#', '#'], ['#', '#', '綠道', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'], ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'], ['#', '#', '#', '#', '#', '#', '小明家', '#', '小王家', '#', '#', '#', '#'], ['#', '#', '肯德基', '鄉村基', '#', '#', '#', '#', '#', '#', '#', '健身房', '#'], ['電影院', '#', '#', '#', '#', '商場', '#', '#', '#', '#', '#', '#', '#'], ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'], ['#', '#', '#', '#', '#', '#', '#', '海邊', '#', '#', '#', '#', '#']]
PREDEFINED_HOMES = ['小明家', '小芳家', '小王家', '醫院宿舍', '學校宿舍', '咖啡店閣樓']
BASE_DIR = './agents/'

# --- 初始化與模擬核心邏輯 ---
def initialize_and_simulate(total_sim_duration_minutes, min_per_step_normal_ui, start_year, start_month, start_day, start_hour, start_minute, selected_mbti_list, eq_enabled, eq_events_json_str, eq_step_minutes_ui, progress=gr.Progress(track_tqdm=True)):
    
    if not LLM_LOADED:
        yield "❌ LLM或模組加載失敗，無法開始。", {}, "錯誤", "", ""
        return

    progress(0, desc="準備初始化...")
    start_time_dt = datetime(int(start_year), int(start_month), int(start_day), int(start_hour), int(start_minute))
    
    agents = [TownAgent(mbti, PREDEFINED_HOMES[i % len(PREDEFINED_HOMES)], MAP) for i, mbti in enumerate(selected_mbti_list)]

    for i, agent in enumerate(agents):
        def update_progress(msg):
            progress((i + 1) / len(agents), desc=f"初始化 {agent.name}: {msg}")
        init_success = False
        for attempt in range(3):
            if agent.initialize_agent(start_time_dt, update_progress):
                init_success = True; break
            update_progress(f"第 {attempt + 1} 次嘗試失敗，重試中...")
            time.sleep(2)
        if not init_success:
            yield f"❌ {agent.name} 初始化失敗三次，終止模擬。", {}, "錯誤", get_llm_log(), ""
            return

    used_homes = {agent.home for agent in agents}
    all_places = set(p for row in MAP for p in row if p != '#') | used_homes
    buildings = {place: Building(place, (0,0)) for place in all_places}
    for agent in agents: agent.update_current_building(buildings)

    def get_full_status():
        return {agent.name: { "當前狀態": agent.curr_action, "位置": agent.curr_place, "HP": agent.health, "作息": f"{agent.wake_time} ~ {agent.sleep_time}", "初始記憶": agent.memory, "一週行事曆": agent.weekly_schedule, "今日行程": agent.daily_schedule } for agent in agents}

    _history_log_buffer = []
    _chat_buffer = {} 
    _event_log_buffer = []

    def yield_state(current_time_dt, current_phase, all_asleep=False):
        current_step_log = []
        sim_time_str = current_time_dt.strftime('%Y年%m月%d日 %H點%M分 (%A)')
        current_step_log.append(f"當前時間: {sim_time_str}")
        
        # ### 核心修改：根據階段選擇日誌格式 ###
        if current_phase in ["Earthquake", "Recovery"]:
            current_step_log.append(f"--- {current_phase.upper()} ---")
            if _event_log_buffer:
                current_step_log.extend(_event_log_buffer)
                _event_log_buffer.clear()
        elif all_asleep:
            current_step_log.append("所有代理人都在休息中...")
        else: # Normal or PostQuakeDiscussion
            for agent in agents:
                pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                log_line = f"{agent.name} 當前活動: {agent.curr_action} ({pronunciatio}) --- 所在的地點({agent.curr_place})"
                if agent.curr_action != "聊天" and agent.current_thought:
                     log_line += f"\n  內心想法: 『{agent.current_thought}』"
                current_step_log.append(log_line)
            if _chat_buffer:
                for location, dialogue_str in _chat_buffer.items():
                    current_step_log.append(f"\n  在 {location} 的聊天內容: {dialogue_str}")
                _chat_buffer.clear()
        
        current_step_log.append("-" * 60)
        current_step_log_str = "\n".join(current_step_log)
        _history_log_buffer.append(current_step_log_str)
        
        yield current_step_log_str, get_full_status(), f"模擬時間: {current_time_dt.strftime('%H:%M:%S')}", "\n\n".join(_history_log_buffer), get_llm_log()

    yield from yield_state(start_time_dt, 'Normal')

    sim_end_time_dt = start_time_dt + timedelta(minutes=int(total_sim_duration_minutes))
    scheduled_events = []
    if eq_enabled:
        try:
            for eq_data in json.loads(eq_events_json_str): scheduled_events.append({'time_dt': datetime.strptime(eq_data['time'], "%Y-%m-%d-%H-%M"), 'duration': int(eq_data['duration']), 'intensity': float(eq_data.get('intensity', 0.7))})
        except Exception as e: _history_log_buffer.append(f"[ERROR] 加載地震事件JSON錯誤: {e}")

    sim_state = {'phase': "Normal", 'time': start_time_dt, 'next_event_idx': 0, 'eq_enabled': eq_enabled, 'quake_details': None, 'recovery_end_time': None, 'discussion_end_time': None}
    llm_context = {'update_log': lambda msg, lvl: _history_log_buffer.append(f"[{lvl}] {msg}"), 'chat_buffer': _chat_buffer, 'event_log_buffer': _event_log_buffer}
    
    while sim_state['time'] < sim_end_time_dt:
        current_time_dt = sim_state['time']
        current_time_hm_str = current_time_dt.strftime('%H-%M')
        llm_context['current_time_str'] = current_time_hm_str
        
        check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context)
        
        # 只有在正常和災後討論階段才處理日常事務和社交
        if sim_state['phase'] in ["Normal", "PostQuakeDiscussion"]:
            if current_time_dt.hour == 3 and current_time_dt.minute == 0 and sim_state['phase'] == "Normal":
                for agent in agents:
                    if agent.health > 0: agent.update_daily_schedule(current_time_dt)

            active_agents = [agent for agent in agents if agent.health > 0 and not agent.is_asleep(current_time_hm_str)]
            
            if not active_agents and sim_state['phase'] == "Normal":
                yield from yield_state(current_time_dt, sim_state['phase'], all_asleep=True)
            else:
                for agent in agents:
                    if agent in active_agents:
                        if agent.last_action in ["睡覺", "Unconscious", "等待初始化"]:
                            agent.current_thought = "新的一天開始了！"; agent.curr_action = "醒來"
                        new_action = find_agent_current_activity(current_time_hm_str, agent.daily_schedule)
                        if agent.curr_action != new_action:
                            agent.curr_action = new_action; agent.current_thought = generate_action_thought(agent, new_action) if LLM_LOADED else ""
                    else:
                        agent.curr_action = "Unconscious" if agent.health <= 0 else "睡覺"; agent.current_thought = ""
                    agent.last_action = agent.curr_action
                
                if len(active_agents) > 1 and LLM_LOADED:
                    handle_social_interactions(active_agents, llm_context)
                
                yield from yield_state(current_time_dt, sim_state['phase'])
        else:
            # 在地震和恢復階段，只更新日誌
            yield from yield_state(current_time_dt, sim_state['phase'])

        step_minutes = int(min_per_step_normal_ui)
        if sim_state.get('phase') == "Earthquake": step_minutes = int(eq_step_minutes_ui)
        elif sim_state.get('phase') in ["Recovery"]: step_minutes = 10
        
        sim_state['time'] += timedelta(minutes=step_minutes)
        time.sleep(0.1)

    _history_log_buffer.append(f"\n--- 模擬結束 @ {sim_state['time'].strftime('%Y-%m-%d %H:%M')} ---")
    final_log = "\n\n".join(_history_log_buffer)
    yield final_log, get_full_status(), "模擬結束", final_log, get_llm_log()
    
def launch_gradio_interface():
    available_mbti = [d for d in os.listdir(BASE_DIR) if os.path.isdir(os.path.join(BASE_DIR, d))] if os.path.exists(BASE_DIR) else DEFAULT_MBTI_TYPES
    if not available_mbti:
        available_mbti = DEFAULT_MBTI_TYPES
        print("警告: 未在 ./agents/ 中找到任何代理人設定資料夾，將使用內建的MBTI列表。")

    with gr.Blocks(theme=gr.themes.Soft(), css="footer {display: none !important;}") as demo:
        gr.Markdown("# 🏙️ AI 小鎮生活模擬器 (v12.3 - 地震流程修正版)")
        if not LLM_LOADED: gr.Markdown("⚠️ **警告:** LLM或其依賴模組加載失敗，模擬器無法運行。")
        with gr.Row():
            with gr.Column(scale=2):
                gr.Markdown("### 模擬控制")
                with gr.Accordion("基本設置", open=True):
                    sim_duration_minutes_num = gr.Number(value=2400, label="總模擬時長 (分鐘)", minimum=10, step=10)
                    min_per_step_normal_num = gr.Number(value=30, label="正常階段步長 (分鐘/步)", minimum=1, step=1)
                    start_year_num = gr.Number(value=2024, label="起始年份", minimum=2020, step=1)
                    start_month_num = gr.Slider(1, 12, value=11, label="起始月份", step=1)
                    start_day_num = gr.Slider(1, 31, value=18, label="起始日期", step=1)
                    start_hour_num = gr.Slider(0, 23, value=3, label="起始小時", step=1)
                    start_minute_num = gr.Slider(0, 59, value=0, label="起始分鐘", step=5)
                with gr.Accordion("代理人與事件", open=True):
                     selected_mbtis_cb_group = gr.CheckboxGroup(available_mbti, label="選擇代理人 (基於 ./agents/ 資料夾)", value=available_mbti[:3] if len(available_mbti) >= 3 else available_mbti)
                     eq_enabled_cb = gr.Checkbox(label="啟用地震事件", value=True)
                     default_eq = json.dumps([{"time": "2024-11-18-11-00", "duration": 30, "intensity": 0.75}], indent=2, ensure_ascii=False)
                     eq_events_tb = gr.Textbox(label="地震事件 (JSON)", value=default_eq, lines=4)
                     eq_step_duration_radio = gr.Radio([1, 5, 10], label="地震期間步長 (分鐘)", value=5)
                simulate_button = gr.Button("🚀 初始化並運行模擬", variant="primary", size="lg", interactive=LLM_LOADED)
            with gr.Column(scale=3):
                status_bar = gr.Textbox(label="當前狀態", interactive=False)
                with gr.Tabs():
                    with gr.TabItem("模擬主日誌 (當前狀態)"):
                        simulation_output_log = gr.Textbox(label="Main Log", interactive=False, lines=40, max_lines=80, autoscroll=True)
                    with gr.TabItem("完整歷史日誌"):
                        simulation_history_log = gr.Textbox(label="History Log", interactive=False, lines=40, max_lines=80, autoscroll=True)
                    with gr.TabItem("代理人實時狀態 (JSON)"):
                        agents_status_json = gr.JSON(label="Agents Full Status", scale=2)
                    with gr.TabItem("LLM 調用日誌"):
                        llm_log_output = gr.Textbox(label="LLM Raw Log", interactive=False, lines=40, max_lines=80, autoscroll=True)
        
        run_inputs = [sim_duration_minutes_num, min_per_step_normal_num, start_year_num, start_month_num, start_day_num, start_hour_num, start_minute_num, selected_mbtis_cb_group, eq_enabled_cb, eq_events_tb, eq_step_duration_radio]
        run_outputs = [simulation_output_log, agents_status_json, status_bar, simulation_history_log, llm_log_output]
        
        simulate_button.click(fn=initialize_and_simulate, inputs=run_inputs, outputs=run_outputs)

    demo.queue().launch(share=False)

if __name__ == "__main__":
    launch_gradio_interface()
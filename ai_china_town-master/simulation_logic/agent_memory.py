# simulation_logic/agent_memory.py (完整版)

from datetime import datetime, timedelta
import sys


def get_llm_memory_func():
    """在函數內部導入並返回 LLM 函數，以解決模組間的循環依賴。"""
    try:
        from tools.LLM.run_gpt_prompt import run_gpt_prompt_generate_initial_memory
        return run_gpt_prompt_generate_initial_memory
    except ImportError as e:
        print(f"❌ [CRITICAL_ERROR] 無法從 tools.LLM.run_gpt_prompt 導入 'run_gpt_prompt_generate_initial_memory': {e}", file=sys.stderr)
        # 返回一個總是失敗的佔位符函數
        return lambda agent: (None, None, False)


from datetime import datetime, timedelta
import sys


def compare_times_hm(time_str1_hm, time_str2_hm):

    try:
        time1 = datetime.strptime(str(time_str1_hm), '%H-%M')
        time2 = datetime.strptime(str(time_str2_hm), '%H-%M')
        return time1 < time2
    except (ValueError, TypeError):
        return False

def update_agent_schedule(wake_up_time_str, schedule_tasks):

    try:
        wake_up_time_str = str(wake_up_time_str).replace(":", "-")
        if "-" in wake_up_time_str and len(wake_up_time_str.split('-')[0]) == 1:
            wake_up_time_str = "0" + wake_up_time_str
        wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    except (ValueError, TypeError):
        wake_up_time = datetime.strptime("07-00", '%H-%M')
    
    current_time = wake_up_time
    updated_schedule = [['醒來', wake_up_time.strftime('%H-%M')]]
    
    if not isinstance(schedule_tasks, list):
        return updated_schedule

    for item in schedule_tasks:
        if not isinstance(item, (list, tuple)) or len(item) < 2:
            continue
        activity, duration_val = item[0], item[1]
        try:
            duration_minutes = int(duration_val)
            if duration_minutes <= 0: continue
            updated_schedule.append([activity, current_time.strftime('%H-%M')])
            current_time += timedelta(minutes=duration_minutes)
        except (ValueError, TypeError):
            continue
            
    return updated_schedule

def find_agent_current_activity(current_time_hm_str, schedule_with_start_times):

    try:
        current_time = datetime.strptime(current_time_hm_str, '%H-%M')
    except (ValueError, TypeError):
        return '時間錯誤' 
        
    if not isinstance(schedule_with_start_times, list) or not schedule_with_start_times:
        return '睡覺'

    possible_activities = []
    for item in schedule_with_start_times:
        if not isinstance(item, (list, tuple)) or len(item) < 2 or not isinstance(item[1], str):
            continue
        activity, time_str = item[0], item[1]
        try:
            activity_start_time = datetime.strptime(time_str.replace(":", "-"), '%H-%M')
            if activity_start_time <= current_time:
                possible_activities.append((activity_start_time, activity))
        except (ValueError, TypeError):
            continue

    if possible_activities:
        latest_activity = max(possible_activities, key=lambda x: x[0])
        return latest_activity[1]
    else:
        return '睡覺'


def generate_and_set_initial_memory(agent):
    # 獲取 LLM 函數
    run_gpt_prompt_generate_initial_memory = get_llm_memory_func()
    
    # 調用 LLM 生成數據
    memory, schedule_item, success = run_gpt_prompt_generate_initial_memory(agent)
    
    if success:
        # 如果成功，直接在 agent 對象上設置屬性
        agent.initial_memory = memory
        agent.weekly_schedule_summary = schedule_item
        # 將背景故事和本週目標合併到代理人的主記憶中，以便後續LLM調用可以參考
        agent.memory = f"[背景]\n{memory}\n\n[本週目標]\n{schedule_item}"
        print(f"✅ [{agent.name}] 的初始記憶和週目標已成功生成。")
    else:
        # 如果失敗，打印錯誤日誌
        print(f"❌ [{agent.name}] 的初始記憶生成失敗。將在下次循環重試。")

    # 返回成功標誌，供 main_quake1.py 中的初始化循環判斷是否需要重試
    return success
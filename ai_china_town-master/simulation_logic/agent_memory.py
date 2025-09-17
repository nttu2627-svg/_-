# simulation_logic/agent_memory.py (完整修正版)

from datetime import datetime, timedelta

def update_agent_schedule(wake_up_time_str, schedule_tasks):
    """
    【核心工具函式】
    將一個包含 [活動名稱, 持續分鐘數] 的列表，
    根據指定的起床時間，轉換為一個包含 [活動名稱, 開始時間 HH-MM] 的完整日程表。

    Args:
        wake_up_time_str (str): 起床時間，格式為 "HH-MM" 或 "HH:MM"。
        schedule_tasks (list): 原始的任務列表，例如 [['工作', 240], ['午餐', 60]]。

    Returns:
        list: 轉換後的完整日程表，例如 [['醒來', '07-00'], ['工作', '07-00'], ['午餐', '11-00']]。
    """
    try:
        # 標準化時間格式
        wake_up_time_str = str(wake_up_time_str).replace(":", "-")
        # 處理單個小時數的情況，例如 "7-00" -> "07-00"
        if "-" in wake_up_time_str and len(wake_up_time_str.split('-')[0]) == 1:
            wake_up_time_str = "0" + wake_up_time_str
        wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    except (ValueError, TypeError):
        # 如果傳入的時間格式不正確，使用一個安全的預設值
        wake_up_time = datetime.strptime("07-00", '%H-%M')
    
    current_time = wake_up_time
    # 日程表的第一項永遠是 "醒來"
    updated_schedule = [['醒來', wake_up_time.strftime('%H-%M')]]
    
    if not isinstance(schedule_tasks, list):
        return updated_schedule

    for item in schedule_tasks:
        # 健壯性檢查，確保列表中的項目是正確的格式
        if not isinstance(item, (list, tuple)) or len(item) < 2:
            continue
        
        activity, duration_val = item[0], item[1]
        try:
            duration_minutes = int(duration_val)
            if duration_minutes <= 0: continue
            
            # 將當前時間點作為活動的開始時間
            updated_schedule.append([activity, current_time.strftime('%H-%M')])
            # 將當前時間往後推移活動的持續時間
            current_time += timedelta(minutes=duration_minutes)
        except (ValueError, TypeError):
            continue
            
    return updated_schedule

def find_agent_current_activity(current_time_hm_str, schedule_with_start_times):
    """
    【核心工具函式】
    根據當前時間，從一個包含開始時間的完整日程表中，找出代理人現在應該從事的活動。

    Args:
        current_time_hm_str (str): 當前時間，格式為 "HH-MM"。
        schedule_with_start_times (list): 完整的日程表，格式為 [['活動', 'HH-MM'], ...]。

    Returns:
        str: 當前應該從事的活動名稱。如果找不到，則返回 '睡覺'。
    """
    try:
        current_time = datetime.strptime(current_time_hm_str, '%H-%M')
    except (ValueError, TypeError):
        return '時間格式錯誤' 
        
    if not isinstance(schedule_with_start_times, list) or not schedule_with_start_times:
        return '睡覺'

    latest_activity = '睡覺' # 預設活動
    latest_activity_time = datetime.min # 用於比較的最小時間

    for item in schedule_with_start_times:
        if not isinstance(item, (list, tuple)) or len(item) < 2 or not isinstance(item[1], str):
            continue
        
        activity, time_str = item[0], item[1]
        try:
            activity_start_time = datetime.strptime(time_str.replace(":", "-"), '%H-%M')
            # 如果活動的開始時間小於等於當前時間，
            # 並且比上一個找到的活動時間更晚，就更新它。
            if activity_start_time <= current_time and activity_start_time >= latest_activity_time:
                latest_activity_time = activity_start_time
                latest_activity = activity
        except (ValueError, TypeError):
            continue

    return latest_activity
# simulation_logic/agent_classes.py (依赖注入最终版)

import random
import os
from datetime import datetime, timedelta
import sys
# 从我们重构后的模组中导入
from tools.LLM import run_gpt_prompt as llm
from .agent_memory import update_agent_schedule
from .schedule_manager import 從檔案載入行程表 # 导入新的预设行程载入函数

from .agent_memory import update_agent_schedule

# --- 動態載入代理人設定 ---
BASE_DIR = './agents/'

def read_file(file_path):
    try:
        with open(file_path, "r", encoding="utf-8") as file: return file.read()
    except Exception as e: return f"讀取檔案 {file_path} 出錯: {e}"

def parse_profile_from_content(content):
    profile = {}
    for line in content.splitlines():
        if ":" in line:
            key, value = line.split(":", 1)
            key_lower = key.strip().lower()
            if 'name' in key_lower: profile['name'] = value.strip()
            elif 'mbti' in key_lower: profile['mbti'] = value.strip()
            elif 'personality' in key_lower: profile['desc'] = value.strip()
    return profile

def load_mbti_profiles_from_files(base_dir):
    profiles = {}
    if not os.path.exists(base_dir): return {}
    mbti_cooperation = {'ISTJ': 0.2, 'ISFJ': 0.5, 'INFJ': 0.6, 'INTJ': 0.3, 'ISTP': 0.4, 'ISFP': 0.5, 'INFP': 0.7, 'INTP': 0.4, 'ESTP': 0.6, 'ESFP': 0.7, 'ENFP': 0.8, 'ENTP': 0.7, 'ESTJ': 0.8, 'ESFJ': 0.9, 'ENFJ': 0.9, 'ENTJ': 0.8}
    for mbti_type in os.listdir(base_dir):
        agent_folder = os.path.join(base_dir, mbti_type)
        if os.path.isdir(agent_folder):
            profile_path = os.path.join(agent_folder, '1.txt')
            if os.path.exists(profile_path):
                content = read_file(profile_path)
                parsed_profile = parse_profile_from_content(content)
                desc = parsed_profile.get('desc', '無描述')
                coop = mbti_cooperation.get(mbti_type.upper(), 0.5)
                profiles[mbti_type.upper()] = {'desc': desc, 'cooperation': coop}
    return profiles

MBTI_PROFILES = load_mbti_profiles_from_files(BASE_DIR)
DEFAULT_MBTI_TYPES = list(MBTI_PROFILES.keys())

class Building:
    def __init__(self, bld_id, position, integrity=100.0):
        self.id = bld_id; self.position = position; self.integrity = float(integrity)
    def apply_damage(self, intensity):
        vulnerability = (100 - self.integrity) / 100.0
        damage = (intensity * 20) + (intensity * 30 * vulnerability) + random.uniform(-5, 5)
        self.integrity = max(0, self.integrity - max(0, damage))
        return damage

class TownAgent:
    def __init__(self, agent_id_mbti, initial_home_name, available_locations):
        self.id = agent_id_mbti.upper(); self.name = agent_id_mbti.upper(); self.MBTI = agent_id_mbti.upper()
        self.available_locations = available_locations 
        self.home = initial_home_name
        mbti_info = MBTI_PROFILES.get(self.MBTI, {'desc': '未知個性', 'cooperation': 0.5})
        self.personality_desc = mbti_info['desc']
        self.cooperation_inclination = mbti_info['cooperation']
        self.persona_summary = f"MBTI: {self.MBTI}. 個性: {self.personality_desc}"
        
        self.curr_place = initial_home_name
        self.target_place = initial_home_name
        
        self.last_action = "等待初始化"; self.curr_action = "等待初始化"; self.curr_action_pronunciatio = "⏳"
        self.current_thought = ""; self.health = 100; self.is_injured = False; self.mental_state = "calm"
        self.current_building = None; self.interrupted_action = None
        self.memory = "尚未生成"; self.weekly_schedule = {}
        self.daily_schedule = []; self.wake_time = "07-00"; self.sleep_time = "23-00"
        self.disaster_experience_log = []

    def is_location_outdoors(self, location_name):
        outdoor_keywords = ["Park", "街道", "海邊", "綠道", "城鎮", "斑馬線"]
        return any(keyword in str(location_name) for keyword in outdoor_keywords)

    def find_path(self, destination):
        is_current_outdoors = self.is_location_outdoors(self.curr_place)
        is_destination_outdoors = self.is_location_outdoors(destination)

        if is_current_outdoors == is_destination_outdoors:
            return destination
        elif is_current_outdoors and not is_destination_outdoors:
            return f"城鎮_{destination}_門口"
        else:
            base_location = str(self.curr_place).split('_')[0]
            return f"{base_location}_門口"
            
    def teleport(self, new_location):
        self.curr_place = new_location
        current_step_destination = self.find_path(self.target_place)
        self.curr_place = current_step_destination
        self.current_thought = f"好了，現在去{self.target_place}。"

    def is_asleep(self, current_time_hm_str):
        try:
            wake_t = datetime.strptime(self.wake_time, '%H-%M')
            sleep_t = datetime.strptime(self.sleep_time, '%H-%M')
            current_t = datetime.strptime(current_time_hm_str, '%H-%M')
            if wake_t == sleep_t: return False
            return not (wake_t <= current_t < sleep_t) if wake_t < sleep_t else not (current_t < sleep_t or current_t >= wake_t)
        except (ValueError, TypeError): return False

    def react_to_earthquake(self, intensity, buildings_dict, other_agents_list):
        self.update_current_building(buildings_dict)
        building_integrity = self.current_building.integrity if self.current_building else 100
        damage = 0
        if building_integrity < 50: damage = random.randint(int(intensity * 25), int(intensity * 55))
        elif self.current_building and random.random() < intensity * 0.5: damage = random.randint(1, int(intensity * 30))
        elif not self.current_building and random.random() < intensity * 0.25: damage = random.randint(1, int(intensity * 15))
        self.health = max(0, self.health - damage)
        self.disaster_experience_log.append(f"遭受 {damage} 點傷害 (HP: {self.health})")
        if self.health <= 0:
            self.is_injured = True; self.mental_state = "unconscious"; self.curr_action = "Unconscious"
            return
        elif self.health < 50: self.is_injured = True
        
        reaction_action_key = "alert"; new_mental_state = "alert"
        if self.is_injured: reaction_action_key, new_mental_state = "尋找醫療救助", "injured"
        elif intensity >= 0.65:
            if 'E' in self.MBTI and 'TJ' in self.MBTI: reaction_action_key, new_mental_state = "指揮疏散", "focused"
            elif 'E' in self.MBTI and 'F' in self.MBTI: reaction_action_key, new_mental_state = "安撫他人", "panicked"
            elif 'I' in self.MBTI and 'F' in self.MBTI: reaction_action_key, new_mental_state = "躲到桌下", "frozen"
            else: reaction_action_key, new_mental_state = "尋找安全出口", "alert"
        else:
            if 'J' in self.MBTI: reaction_action_key, new_mental_state = "評估周圍環境", "calm"
            else: reaction_action_key, new_mental_state = "尋找遮蔽物", "alert"
        if not self.is_injured and self.cooperation_inclination > 0.6 and reaction_action_key not in ["躲到桌下"]:
            if any(o.id != self.id and o.health > 0 and o.is_injured and self.Is_nearby(o.get_position()) for o in other_agents_list):
                reaction_action_key, new_mental_state = "協助受傷的人", "helping"
        self.mental_state = new_mental_state
        self.curr_action = reaction_action_key

    def perceive_and_help(self, other_agents):
        nearby_injured = [o for o in other_agents if o.id != self.id and o.health > 0 and o.is_injured and self.Is_nearby(o.get_position())]
        if not nearby_injured: return None
        target = min(nearby_injured, key=lambda x: x.health)
        heal = min(100 - target.health, random.randint(10, 20))
        target.health += heal
        return f"協助 {target.name} (+{heal} HP -> {target.health})"

    def update_current_building(self, buildings_dict):
        self.current_building = buildings_dict.get(self.curr_place)
    
    def get_position(self): return (0, 0)
    def Is_nearby(self, other_agent_position): return True
    def interrupt_action(self):
        self.interrupted_action = self.curr_action if self.curr_action not in ["睡覺", "Unconscious"] else None

    async def initialize_agent(self, current_date, schedule_mode: str, schedule_file_path: str):
        """
        根据指定的模式初始化代理人。
        schedule_mode: 'llm' 或 'preset'
        schedule_file_path: 预设行程档案的路径
        """
        # 记忆和周计划总是由 LLM 生成，以保证角色的独特性
        memory, mem_success = await llm.run_gpt_prompt_generate_initial_memory(self.name, self.MBTI, self.persona_summary, self.home)
        if not mem_success: return False
        self.memory = memory
        
        schedule, sched_success = await llm.run_gpt_prompt_generate_weekly_schedule(self.persona_summary)
        if not sched_success: return False
        self.weekly_schedule = schedule

        # 根据模式选择如何生成每日行程
        return await self.update_daily_schedule(current_date, schedule_mode, schedule_file_path)

    # ### 核心修改：每日更新流程也接受 schedule_mode ###
    async def update_daily_schedule(self, current_date, schedule_mode: str, schedule_file_path: str):
        """根据模式更新并设定一天的日程。"""
        
        base_tasks = []
        if schedule_mode == "llm":
            # --- LLM 生成模式 ---
            print(f"[Agent {self.name}] 使用 LLM 生成今日行程...")
            weekday_name = current_date.strftime('%A')
            today_goal = self.weekly_schedule.get(weekday_name, "自由活動")
            raw_tasks = await llm.run_gpt_prompt_generate_hourly_schedule(self.persona_summary, current_date.strftime('%Y-%m-%d'), today_goal)
            
            # 从 LLM 返回的 [名称, 分钟数] 格式转换为 [名称, 开始时间] 格式所需的原始数据
            if raw_tasks and isinstance(raw_tasks[0], list):
                 # 重新计算 wake_time
                wake_time_str = await llm.run_gpt_prompt_wake_up_hour(self.persona_summary, current_date.strftime('%Y-%m-%d'), raw_tasks)
                if not wake_time_str: return False
                self.wake_time = wake_time_str.replace(":", "-")
                
                # 使用原始的 [名称, 分钟数] 列表来生成行程
                self.daily_schedule = update_agent_schedule(self.wake_time, raw_tasks)
                
                # 计算睡眠时间
                try:
                    total_duration = sum(int(task[1]) for task in raw_tasks)
                    self.sleep_time = (datetime.strptime(self.wake_time, '%H-%M') + timedelta(minutes=total_duration)).strftime('%H-%M')
                except:
                    self.sleep_time = (datetime.strptime(self.wake_time, '%H-%M') + timedelta(hours=16)).strftime('%H-%M')
                
                return True

        elif schedule_mode == "preset":
            # --- 预设行程模式 ---
            print(f"[Agent {self.name}] 使用预设档案 '{schedule_file_path}' 载入行程...")
            preset_schedule = 從檔案載入行程表(self.name, schedule_file_path)
            if preset_schedule:
                self.daily_schedule = preset_schedule
                # 从预设行程中推断作息时间
                if self.daily_schedule:
                    self.wake_time = self.daily_schedule[0][1] # 第一个活动的开始时间
                    # 假设最后一个活动持续一小时
                    last_activity_time = datetime.strptime(self.daily_schedule[-1][1], '%H-%M')
                    self.sleep_time = (last_activity_time + timedelta(hours=1)).strftime('%H-%M')
                return True

        # 如果两种模式都失败，则返回 False
        print(f"❌ [Agent {self.name}] 无法生成或载入行程。")
        return False
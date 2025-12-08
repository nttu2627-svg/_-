# --- Imports ---
import math
import random
import json
import re
import os
import sys
from datetime import datetime, timedelta
import gradio as gr
import numpy as np
from sklearn.cluster import DBSCAN
import time # For potential delays if needed
import traceback # For detailed error logging

# --- Local LLM Integration ---
try:
    # Use the exact import requested by the user
    from tools.LLM.run_gpt_prompt import (
        run_gpt_prompt_generate_hourly_schedule, run_gpt_prompt_wake_up_hour,
        run_gpt_prompt_pronunciatio, double_agents_chat, go_map,
        modify_schedule, summarize, # Original functions
        run_gpt_prompt_get_recovery_action, # NEW: for recovery phase actions
        run_gpt_prompt_summarize_disaster, # NEW: for summarizing disaster experience
    )
    print("Local LLM functions imported successfully from tools.LLM.run_gpt_prompt.")
    LLM_LOADED = True
except ImportError as e:
    # Keep placeholder logic as fallback
    print(f"Error importing from tools.LLM.run_gpt_prompt: {e}")
    print("Please ensure 'tools/LLM/run_gpt_prompt.py' exists and contains the required functions.")
    LLM_LOADED = False
    def placeholder_llm(*args, func_name='unknown', **kwargs):
        print(f"Warning: LLM function '{func_name}' called but not loaded. Using placeholder behavior.")
        if func_name == 'generate_schedule': return [["Placeholder Task", 60]]
        if func_name == 'wake_up_hour': return "07-00"
        if func_name == 'pronunciatio': return "❓"
        if func_name == 'chat':
            a1 = args[1] if len(args)>1 else 'Agent1'
            a2 = args[2] if len(args)>2 else 'Agent2'
            eq_ctx = kwargs.get('eq_ctx')
            if eq_ctx and "地震" in eq_ctx:
                 return [[a1, "刚刚地震好可怕！"], [a2, "是啊，你没事吧？"]]
            return [[a1, "Placeholder chat."],[a2, "..."]]
        if func_name == 'go_map': return args[1] if len(args)>1 else "Placeholder Location"
        if func_name == 'modify_schedule': return args[0] if args else []
        if func_name == 'summarize': return "Placeholder summary."
        if func_name == 'get_recovery_action': return "原地休息"
        if func_name == 'summarize_disaster': return "经历了一场地震，现在安全。"
        return None
    run_gpt_prompt_generate_hourly_schedule = lambda p, n: placeholder_llm(p, n, func_name='generate_schedule')
    run_gpt_prompt_wake_up_hour = lambda p, n, h: placeholder_llm(p, n, h, func_name='wake_up_hour')
    run_gpt_prompt_pronunciatio = lambda a: placeholder_llm(a, func_name='pronunciatio')
    # Updated double_agents_chat placeholder to accept eq_ctx
    double_agents_chat = lambda m, a1, a2, c, i, t, nt, eq_ctx=None: placeholder_llm(m, a1, a2, c, i, t, nt, eq_ctx=eq_ctx, func_name='chat')
    go_map = lambda n, h, cp, cg, ct: placeholder_llm(n, h, cp, cg, ct, func_name='go_map')
    modify_schedule = lambda o, nt, m, wt, r: placeholder_llm(o, nt, m, wt, r, func_name='modify_schedule')
    summarize = lambda m, nt, n: placeholder_llm(m, nt, n, func_name='summarize')
    run_gpt_prompt_get_recovery_action = lambda p, ms, cp: placeholder_llm(p, ms, cp, func_name='get_recovery_action')
    run_gpt_prompt_summarize_disaster = lambda n, m, h, el: placeholder_llm(n, m, h, el, func_name='summarize_disaster')
    print("Using placeholder LLM functions.")

# --- UTF-8 Configuration ---
try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except AttributeError:
    print("Warning: sys.stdout.reconfigure not available. Ensure UTF-8 environment.")

# === MBTI Profiles ===
MBTI_PROFILES = {
    'ISTJ': {'desc': '負責任、嚴謹保守，講求秩序，不傾向主動合作。', 'cooperation': 0.2},
    'ISFJ': {'desc': '和善、盡責，重視他人感受，內向使其合作意願中等。', 'cooperation': 0.5},
    'INFJ': {'desc': '理想主義且有洞察力，默默關懷他人，合作意願中等偏高。', 'cooperation': 0.6},
    'INTJ': {'desc': '獨立戰略思考，講求邏輯，如有助計畫則願合作。', 'cooperation': 0.3},
    'ISTP': {'desc': '務實冷靜，喜歡獨立解決問題，合作意願偏低。', 'cooperation': 0.4},
    'ISFP': {'desc': '溫和敏感，樂於照顧親近的人，一對一合作尚可。', 'cooperation': 0.5},
    'INFP': {'desc': '富同理心且忠於價值觀，若符合信念則樂於助人。', 'cooperation': 0.7},
    'INTP': {'desc': '客觀好奇，獨立分析問題，只有在合理時才會合作。', 'cooperation': 0.4},
    'ESTP': {'desc': '外向實際，適應力強，危機中會立即行动也可能协助他人。', 'cooperation': 0.6},
    'ESFP': {'desc': '活泼友善，喜欢带动团队，遇事积极协助他人。', 'cooperation': 0.7},
    'ENFP': {'desc': '热情创意且善社交，倾向群体行动与合作。', 'cooperation': 0.8},
    'ENTP': {'desc': '机敏健谈，喜欢寻找新奇解决方案，愿意与人合作解决问题。', 'cooperation': 0.7},
    'ESTJ': {'desc': '务实果断，擅长组织管理，他们会主导并要求合作。', 'cooperation': 0.8},
    'ESFJ': {'desc': '热心合群，重视团队和谐，乐于为群体付出合作。', 'cooperation': 0.9},
    'ENFJ': {'desc': '有同情心又善于领导，天然会带领并协助他人。', 'cooperation': 0.9},
    'ENTJ': {'desc': '自信领导，逻辑效率并重，会有效组织协调团体行动。', 'cooperation': 0.8}
}
DEFAULT_MBTI_TYPES = list(MBTI_PROFILES.keys())

# --- Town Map & Config ---
MAP =    [['医院', '咖啡店', '#', '蜜雪冰城', '学校', '#', '#', '小芳家', '#', '#', '火锅店', '#', '#'],
          ['#', '#', '绿道', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '小明家', '#', '小王家', '#', '#', '#', '#'],
          ['#', '#', '肯德基', '乡村基', '#', '#', '#', '#', '#', '#', '#', '健身房', '#'],
          ['电影院', '#', '#', '#', '#', '商场', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '海边', '#', '#', '#', '#', '#']]

can_go_place = sorted(list(set(item for row in MAP for item in row if item != '#')))
PREDEFINED_HOMES = ['小明家', '小芳家', '小王家', '医院宿舍', '学校宿舍', '咖啡店阁楼', '商场公寓', '海边小屋',
                   '绿道帐篷', '火锅店楼上', '肯德基员工间', '健身房休息室', '电影院放映室', '乡村基单间',
                   '蜜雪冰城仓库', '神秘空屋']
for home in PREDEFINED_HOMES:
    if home not in can_go_place: can_go_place.append(home)
can_go_place = sorted(list(set(can_go_place)))

# --- Agent Profile File Handling ---
BASE_DIR = './agents/'
TARGET_FILENAME = "1.txt"

def initialize_agent_profiles(mbti_list):
    target_files = {}
    for mbti_type in mbti_list:
        folder = os.path.join(BASE_DIR, mbti_type)
        os.makedirs(folder, exist_ok=True)
        file_path = os.path.join(folder, TARGET_FILENAME)
        if not os.path.exists(file_path):
            try:
                with open(file_path, "w", encoding="utf-8") as f:
                    f.write(f"Name: {mbti_type}\n")
                    f.write(f"MBTI: {mbti_type}\n")
                    persona_desc = MBTI_PROFILES.get(mbti_type, {}).get('desc', 'Unknown personality.')
                    f.write(f"Personality Notes: {persona_desc}\n")
                    f.write("Occupation: Resident\n")
                    f.write("Age: 30\n")
                    f.write("Goals: Live a fulfilling life.\n")
                    f.write("Daily Routine Notes:\n")
                    f.write("Likes a mix of routine and spontaneity.\n")
            except Exception as e:
                print(f"Error creating default profile for {mbti_type}: {e}")
        target_files[mbti_type] = file_path
    return target_files

def get_target_files_for_agents(mbti_list):
    target_files = {}
    for mbti_type in mbti_list:
        folder = os.path.join(BASE_DIR, mbti_type)
        file_path = os.path.join(folder, TARGET_FILENAME)
        if os.path.exists(folder):
             target_files[mbti_type] = file_path
    return target_files

def read_file(file_path):
    try:
        with open(file_path, "r", encoding="utf-8") as file:
            return file.read()
    except FileNotFoundError:
        return f"错误：配置文件 {file_path} 未找到。\n请确保文件存在或运行一次模拟。"
    except Exception as e:
        return f"读取文件时出错 {file_path}: {e}"

def save_file(file_path, new_content):
    try:
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, "w", encoding="utf-8") as file:
            file.write(new_content)
        return f"文件 {os.path.basename(file_path)} 已成功保存在 {os.path.dirname(file_path)}！"
    except Exception as e:
        return f"保存文件时出错 {file_path}: {e}"

# --- Building Class ---
class Building:
    def __init__(self, bld_id, position, integrity=100.0):
        self.id = bld_id
        self.position = position
        self.integrity = integrity # Percentage

# --- TownAgent Class ---
class TownAgent:
    def __init__(self, agent_id_mbti, initial_home_name, map_layout):
        # --- Basic Info ---
        self.id = agent_id_mbti
        self.name = agent_id_mbti
        self.MBTI = agent_id_mbti
        self.MAP = map_layout
        self.home = initial_home_name

        # --- MBTI Derived ---
        mbti_info = MBTI_PROFILES.get(self.MBTI, {'desc': '未知個性', 'cooperation': 0.5})
        self.personality_desc = mbti_info['desc']
        self.cooperation_inclination = mbti_info['cooperation']

        # --- Core State ---
        self.schedule = []
        self.schedule_time = [] # Processed schedule with start times
        self.curr_place = initial_home_name # Name of current location
        self.position = self._find_initial_pos(initial_home_name) # (row, col) tuple
        self.last_action = "Initializing"
        self.curr_action = "Initializing" # Current task/activity name
        self.curr_action_pronunciatio = "⏳" # Emoji representation
        self.memory = "" # Summarized experiences
        self.talk_arr = "" # Daily chat log (JSON strings)
        self.wake = "07-00" # Wake time HH-MM

        # --- Health & Status ---
        self.health = 100
        self.is_injured = False
        self.mental_state = "calm" # calm, alert, panicked, frozen, focused, helping, injured, unconscious, recovering, seeking_help, assessing_damage
        self.current_building = None # Reference to Building object if inside, else None
        self.interrupted_action = None # Stores action interrupted by quake
        self.disaster_experience_log = [] # Logs specific to the disaster phase for summary

        # --- Load Profile ---
        self.profile_path = os.path.join(BASE_DIR, self.id, TARGET_FILENAME)
        self._load_profile()

    def _load_profile(self):
        try:
            self.profile_content = read_file(self.profile_path)
            self.profile_lines = self.profile_content.splitlines()
            persona_lines = [line for line in self.profile_lines if "Personality Notes:" in line or "Daily Routine Notes:" in line]
            if persona_lines:
                 self.persona_summary = " ".join([line.split(":", 1)[1].strip() for line in persona_lines if ':' in line])
            elif len(self.profile_lines) > 2 and self.profile_lines[2].strip():
                 self.persona_summary = self.profile_lines[2].strip()
            else:
                 self.persona_summary = f"MBTI: {self.MBTI}. Desc: {self.personality_desc}"
        except Exception as e:
            print(f"Error loading profile for {self.id}: {e}. Using default persona.")
            self.profile_content = ""
            self.profile_lines = []
            self.persona_summary = f"MBTI: {self.MBTI}. Desc: {self.personality_desc}"

    def _find_initial_pos(self, place_name):
        for r, row in enumerate(self.MAP):
            for c, cell in enumerate(row):
                if cell == place_name:
                    return (r, c)
        for r, row in enumerate(self.MAP):
             for c, cell in enumerate(row):
                  if cell == '#':
                      return (r, c)
        return (0, 0)

    def update_current_building(self, buildings_dict):
        r, c = self.position
        if 0 <= r < len(self.MAP) and 0 <= c < len(self.MAP[0]):
            map_cell = self.MAP[r][c]
            if map_cell != '#':
                self.current_building = buildings_dict.get(map_cell)
                return
        self.current_building = None

    def get_position(self):
        return self.position

    def goto_scene(self, scene_name, buildings_dict):
        target_pos = None
        for r, row in enumerate(self.MAP):
            for c, cell in enumerate(row):
                if cell == scene_name:
                    target_pos = (r, c)
                    break
            if target_pos: break

        if target_pos:
            self.position = target_pos
            self.curr_place = scene_name
            self.update_current_building(buildings_dict)
            return True
        else:
            return False

    def Is_nearby(self, other_agent_position):
        if self.position is None or other_agent_position is None: return False
        x1, y1 = self.position
        x2, y2 = other_agent_position
        return abs(x1 - x2) <= 1 and abs(y1 - y2) <= 1

    def interrupt_action(self):
        if self.curr_action not in ["Initializing", "睡觉", "Unconscious"]:
            self.interrupted_action = self.curr_action
            # print(f"  Agent {self.id} action '{self.curr_action}' interrupted.")
        else:
            self.interrupted_action = None

    def react_to_earthquake(self, intensity, buildings_dict,other_agents_list):
        """Determines IMMEDIATE reaction and applies initial damage."""
        if self.mental_state == "unconscious": return

        original_health = self.health
        damage = 0
        self.update_current_building(buildings_dict)
        building_obj = self.current_building
        building_integrity = building_obj.integrity if building_obj else 100

        location_context = "outdoors"
        if building_obj: location_context = f"in {building_obj.id}"

        # Damage logic
        if building_integrity < 50: damage = random.randint(int(intensity * 25), int(intensity * 55))
        elif building_obj:
            if random.random() < intensity * 0.5: damage = random.randint(1, int(intensity * 30))
        else:
            if random.random() < intensity * 0.25: damage = random.randint(1, int(intensity * 15))

        self.health = max(0, self.health - damage)
        damage_log = f"遭受 {damage} 點傷害" if damage > 0 else "未受傷"
        health_change = f"HP: {original_health} -> {self.health}" if damage > 0 else ""

        self.disaster_experience_log.append(f"地震開始：在 {location_context}，{damage_log} {health_change}")

        if self.health <= 0:
            self.is_injured = True
            self.mental_state = "unconscious"
            self.curr_action = "Unconscious"
            self.curr_action_pronunciatio = "😵"
            self.disaster_experience_log.append("因重傷失去意識。")
            return
        elif self.health < 50:
            if not self.is_injured: self.disaster_experience_log.append("受到傷害。")
            self.is_injured = True
        else: self.is_injured = False

        reaction_action = "alert"
        new_mental_state = "alert"

        # Higher priority is given to self-preservation if injured
        if self.is_injured:
            reaction_action, new_mental_state = "injured_flee", "injured"
        elif intensity >= 0.65:
            if 'E' in self.MBTI and 'TJ' in self.MBTI: reaction_action, new_mental_state = "lead", "focused"
            elif 'E' in self.MBTI and 'F' in self.MBTI: reaction_action, new_mental_state = "panic", "panicked"
            elif 'I' in self.MBTI and 'F' in self.MBTI: reaction_action, new_mental_state = "freeze", "frozen"
            elif 'I' in self.MBTI and 'TP' in self.MBTI: reaction_action, new_mental_state = "flee", "alert"
            else: reaction_action, new_mental_state = "flee", "alert"
        else:
            if 'J' in self.MBTI: reaction_action, new_mental_state = "calm", "alert"
            else: reaction_action, new_mental_state = "flee", "alert"

        # Cooperation possibility if not panicking and capable
        if not self.is_injured and self.cooperation_inclination > 0.6 and reaction_action not in ["panic", "freeze"]:
            # *** 修改這裡：遍歷 other_agents_list ***
            can_help_nearby = any(o.id != self.id and o.health > 0 and o.is_injured and self.Is_nearby(o.get_position()) for o in other_agents_list)
            if can_help_nearby:
                 reaction_action, new_mental_state = "assist_others", "helping"


        self.mental_state = new_mental_state
        self.curr_action = reaction_action
        emoji_map = {"lead": "🧑‍🚒","panic": "😱","flee": "🏃","freeze": "🥶","calm": "🧘","assist_others": "🤝","injured_flee": "🤕","unconscious": "😵","alert":"⚠️"}
        self.curr_action_pronunciatio = emoji_map.get(reaction_action, "❓")
        self.disaster_experience_log.append(f"初步反應：{self.curr_action} ({self.curr_action_pronunciatio})，精神狀態: {self.mental_state}")

    def perform_earthquake_step_action(self, other_agents, buildings_dict, quake_intensity):
        """Action performed during each 1-minute step of the earthquake duration."""
        if self.mental_state == "unconscious": return None

        action_log = [] # Collect thoughts/actions for this 1-minute step
        log_prefix = f"  {self.name} ({self.MBTI}):"

        # --- Self-Damage/Health Update (minor continued damage during shaking) ---
        if random.random() < 0.15: # Small chance of minor damage per minute
             minor_dmg = random.randint(0, int(quake_intensity * 5))
             self.health = max(0, self.health - minor_dmg)
             if minor_dmg > 0:
                 action_log.append(f"受到 {minor_dmg} 點輕微傷害 ({self.health} HP)")
                 if self.health <= 0: self.mental_state = "unconscious"; self.curr_action="Unconscious"; self.curr_action_pronunciatio="😵"
                 elif self.health < 50 and not self.is_injured: self.is_injured = True; action_log.append("感覺受傷了。")

        # --- Objective Action ---
        if self.curr_action == "assist_others":
            help_event = self.perceive_and_help(other_agents)
            if help_event: action_log.append(help_event)
            else: action_log.append(f"{self.curr_action_pronunciatio} 尋找可協助的目標。")
        elif self.curr_action in ["flee", "injured_flee"]:
            # Simple flee: try to move to a safer adjacent spot or just log
            action_log.append(f"{self.curr_action_pronunciatio} 正在逃離險境。")
        elif self.curr_action == "lead":
            action_log.append(f"{self.curr_action_pronunciatio} 試圖引導周圍的人。")
        elif self.curr_action == "panic":
            action_log.append(f"{self.curr_action_pronunciatio} 恐慌地不知所措。")
        elif self.curr_action == "freeze":
            action_log.append(f"{self.curr_action_pronunciatio} 僵立在原地。")
        elif self.curr_action == "calm":
            action_log.append(f"{self.curr_action_pronunciatio} 冷靜觀察周遭情況。")
        elif self.curr_action == "alert":
             action_log.append(f"{self.curr_action_pronunciatio} 保持高度警惕。")
        elif self.curr_action == "Unconscious":
             action_log.append("失去意識，無法行動。")
             return (f"{log_prefix} {self.curr_action_pronunciatio} {action_log[0]}") # Only log unconscious once

        # --- Subjective Thought (LLM call for agent's inner monologue, based on MBTI/state) ---
        # This is a simplified subjective thought. A more detailed one would be another LLM call.
        subjective_thought = ""
        if self.mental_state == "panicked": subjective_thought = "（心里一片混乱，只想快点结束！）"
        elif self.mental_state == "frozen": subjective_thought = "（吓呆了，手脚不听使唤。）"
        elif self.mental_state == "helping": subjective_thought = "（一定要帮助他们！）"
        elif self.mental_state == "focused": subjective_thought = "（冷静，需要找到最安全的路线。）"
        elif self.is_injured: subjective_thought = "（好痛，我撑不住了...）"
        elif self.mental_state == "calm": subjective_thought = "（情况不妙，需要思考下一步。）"

        full_log_msg = f"{log_prefix} {self.curr_action} {self.curr_action_pronunciatio} " + ", ".join(action_log) + subjective_thought
        self.disaster_experience_log.append(f"地震中：{self.curr_action}，{', '.join(action_log)}。") # Add to per-agent disaster log for summary

        return full_log_msg

    def perceive_and_help(self, other_agents):
        """Attempts to help nearby injured conscious agents."""
        if self.mental_state != "helping": return None

        nearby_injured = [agent for agent in other_agents if agent.is_injured and agent.mental_state != "unconscious"]

        log_prefix = f"  {self.name} ({self.MBTI}):"
        action_desc = ""

        # --- Health Recovery ---
        if self.health < 100 and random.random() < 0.5: # Chance to recover health
            heal_amount = random.randint(1, 5) # Minor healing
            self.health = min(100, self.health + heal_amount)
            if heal_amount > 0: action_desc += f"生命值恢复 {heal_amount} 点 ({self.health} HP)。"
            if self.health >= 50 and self.is_injured: self.is_injured = False; action_desc += "不再受伤。"

        # --- Mental State Recovery ---
        if self.mental_state not in ["calm", "unconscious"] and random.random() < 0.4:
            if self.is_injured:
                 self.mental_state = "injured" # Stay injured state until healed
            elif self.mental_state == "panicked" :
                self.mental_state = "alert" # From panic to alert
            elif self.mental_state == "frozen":
                self.mental_state = "alert" # From frozen to alert
            elif self.mental_state == "helping":
                self.mental_state = "calm" # Helping agents might calm down after initial chaos
            elif self.mental_state == "focused":
                self.mental_state = "calm" # Leaders might calm down
            if self.mental_state == "alert" and random.random() < 0.5:
                self.mental_state = "calm" # From alert to calm
            action_desc += f"精神状态变为 {self.mental_state}。"

        # --- Action based on state / LLM prompt ---
        # Get suggested action from LLM for recovery (based on new LLM call)
        persona_info = self.persona_summary
        recovery_action_llm = run_gpt_prompt_get_recovery_action(persona_info, self.mental_state, self.curr_place)
        self.curr_action = recovery_action_llm
        self.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(self.curr_action)
        action_desc += f"执行 {self.curr_action} ({self.curr_action_pronunciatio})。"

        # Simple movement/interaction based on action
        if "检查" in self.curr_action and self.current_building:
             action_desc += f" 检查建筑 {self.current_building.id}。"
        elif "寻找" in self.curr_action and "帮助" in self.curr_action:
             help_log = self.perceive_and_help(other_agents)
             if help_log: action_desc += f" 成功 {help_log}。"
             else: action_desc += " 未找到可帮助者。"
        elif "避难" in self.curr_action and self.curr_place != self.home:
             self.goto_scene(self.home, buildings_dict)
             action_desc += f" 返回家中避难。"
        elif "休息" in self.curr_action:
             action_desc += " 在原地休息。"

        self.disaster_experience_log.append(f"灾后恢复中：{action_desc}")
        return f"{log_prefix} {action_desc}"

# --- Time & Schedule Functions ---
def get_now_time(oldtime_str, step_num, min_per_step):
    try:
        start_time = datetime.strptime(oldtime_str, "%Y-%m-%d-%H-%M")
        if min_per_step <= 0: min_per_step = 1
        new_time = start_time + timedelta(minutes=min_per_step * step_num)
        return new_time.strftime("%Y-%m-%d-%H-%M")
    except (ValueError, TypeError) as e:
        print(f"Error parsing/calculating time: {oldtime_str}, {step_num}, {min_per_step}. Error: {e}. Returning original.")
        try: return (datetime.strptime(oldtime_str, "%Y-%m-%d-%H-%M") + timedelta(minutes=1)).strftime("%Y-%m-%d-%H-%M")
        except: return oldtime_str
def get_weekday(nowtime_str):
    try:
        dt = datetime.strptime(nowtime_str, '%Y-%m-%d-%H-%M')
        weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"]
        return weekdays[dt.weekday()]
    except ValueError: return "未知日期"
def format_date_time(date_str):
    try:
        dt = datetime.strptime(date_str, '%Y-%m-%d-%H-%M')
        return dt.strftime('%Y年%m月%d日 %H:%M')
    except ValueError: return date_str
def compare_times(time_str1_hm, time_str2_hm):
    try:
        time1 = datetime.strptime(time_str1_hm, '%H-%M')
        time2 = datetime.strptime(time_str2_hm, '%H-%M')
        return time1 < time2
    except ValueError: return False
def update_schedule(wake_up_time_str, schedule_tasks):
    try:
        wake_up_time_str = wake_up_time_str.replace(":", "-")
        if "-" not in wake_up_time_str:
            if len(wake_up_time_str) == 3: wake_up_time_str = "0" + wake_up_time_str[0] + "-" + wake_up_time_str[1:]
            elif len(wake_up_time_str) == 4: wake_up_time_str = wake_up_time_str[:2] + "-" + wake_up_time_str[2:]
            else: raise ValueError("Invalid time format")
        wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    except ValueError:
        print(f"Error parsing wake time: '{wake_up_time_str}'. Defaulting to 07-00.")
        wake_up_time_str = "07-00"
        wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    current_time = wake_up_time
    updated_schedule = []
    if not isinstance(schedule_tasks, list): return []
    updated_schedule.append(['醒来', wake_up_time.strftime('%H-%M')])
    for item in schedule_tasks:
        if not isinstance(item, (list, tuple)) or len(item) < 2: continue
        activity, duration_val = item[0], item[1]
        try:
            duration_minutes = int(duration_val)
            if duration_minutes <= 0: continue
        except (ValueError, TypeError): continue
        updated_schedule.append([activity, current_time.strftime('%H-%M')])
        current_time += timedelta(minutes=duration_minutes)
    return updated_schedule
def find_current_activity(current_time_hm_str, schedule_with_start_times):
    try: current_time = datetime.strptime(current_time_hm_str, '%H-%M')
    except ValueError: return ['时间错误', current_time_hm_str]
    if not isinstance(schedule_with_start_times, list) or not schedule_with_start_times: return ['睡觉', "00-00"]
    current_activity_found = ['睡觉', "00-00"]
    latest_start_time_found = datetime.strptime("00-00", '%H-%M')
    for item in schedule_with_start_times:
         if not isinstance(item, (list, tuple)) or len(item) < 2 or not isinstance(item[1], str): continue
         activity, time_str = item[0], item[1]
         try:
             activity_start_time = datetime.strptime(time_str.replace(":", "-"), '%H-%M')
             if activity_start_time <= current_time:
                 if activity_start_time >= latest_start_time_found:
                      latest_start_time_found = activity_start_time
                      current_activity_found = [activity, time_str]
         except ValueError: continue
    return current_activity_found

# --- Interaction Functions ---
def DBSCAN_chat(agents_list):
    active_agents = [a for a in agents_list if a.health > 0 and a.mental_state not in ["unconscious", "panicked", "frozen"]]
    if len(active_agents) < 2: return None
    points_list = [a.get_position() for a in active_agents]
    if not points_list: return None
    points_array = np.array(points_list)
    dbscan = DBSCAN(eps=1.5, min_samples=2)
    try: labels = dbscan.fit_predict(points_array)
    except ValueError: return None
    clusters = {}
    for agent, label in zip(active_agents, labels):
        if label != -1:
            if label not in clusters: clusters[label] = []
            clusters[label].append(agent)
    potential_groups = [group for group in clusters.values() if len(group) >=2]
    if not potential_groups: return None
    if random.random() < 0.6:
        selected_group = random.choice(potential_groups)
        return selected_group
    else: return None

# --- Helper: Damage Report ---
def generate_damage_report(agents, buildings):
    report = ["--- 灾后损伤报告 ---"]
    report.append("建筑状况:")
    for name, bldg in buildings.items():
        status = "完好" if bldg.integrity > 80 else "轻微受损" if bldg.integrity > 50 else "严重受损" if bldg.integrity > 0 else "完全摧毁"
        report.append(f"  - {name}: 完整度 {bldg.integrity:.1f}% ({status})")
    report.append("\n人员状况:")
    for agent in agents:
        status = "安全" if agent.health > 70 else "轻伤" if agent.health > 0 else "重伤/昏迷"
        loc = f"@ {agent.curr_place}"
        report.append(f"  - {agent.name} ({agent.MBTI}): 生命值 {agent.health}/100 ({status}) {loc}")
    report.append("----------------------")
    return "\n".join(report)

# --- Simulation Core Logic ---
def simulate_town_life(total_sim_duration_minutes, # Total simulation duration (minutes)
                         min_per_step_normal, # Normal phase step duration (minutes)
                         start_year, start_month, start_day, start_hour, start_minute, # Granular start time
                         selected_mbti_list,
                         eq_enabled, eq_events_json_str): # New: Earthquake event list

    output_log = []
    MAX_LOG_LINES = 2500 # Increase log buffer further for more details

    def add_log(message, level="INFO"):
        # Format chat for readability
        if level == "CHAT":
            formatted_message = ""
            try:
                chat_data = json.loads(message)
                formatted_message += f"\n  --- 对话开始 ---"
                for entry in chat_data:
                    speaker = entry.get('speaker', '?')
                    utterance = entry.get('utterance', '...')
                    formatted_message += f"\n    🎤 {speaker}: {utterance}"
                formatted_message += f"\n  --- 对话结束 ---\n"
                message = formatted_message.strip()
            except: # Fallback for malformed JSON or non-JSON
                message = f"\n  --- 对话 (原始格式) ---\n    {message}\n  --- 对话结束 ---\n"
        else:
             # Indent sub-logs for better hierarchy
            if level in ["SUB", "ACTION", "STATE", "DEBUG", "REPORT", "EVENT"]:
                message = f"  {message}"
            message = f"[{datetime.now().strftime('%H:%M:%S')} {level}] {message}"

        if len(output_log) > MAX_LOG_LINES: output_log.pop(0)
        output_log.append(message)
        yield "\n".join(output_log) # Yield after each log message for real-time updates

    # --- Initial Setup ---
    yield from add_log("--- 模拟启动中 ---", "INFO") # Initial yield to clear previous output

    if not LLM_LOADED:
        yield from add_log("警告: LLM 未加载，模拟行为将非常有限。", "WARN")
    if not selected_mbti_list:
         yield from add_log("错误：没有选择任何 Agent！", "ERROR")
         return
    yield from add_log(f"选择的 Agents: {', '.join(selected_mbti_list)} ({len(selected_mbti_list)} 名)", "INFO")

    # Agent Initialization
    selected_mbtis = selected_mbti_list[:16] # Limit to 16
    agents = []
    used_homes = set()
    available_homes = PREDEFINED_HOMES[:]
    for i, mbti in enumerate(selected_mbtis):
        assigned_home = None
        if available_homes:
             assigned_home = available_homes.pop(random.randrange(len(available_homes)))
             used_homes.add(assigned_home)
        else:
             assigned_home = f"{mbti}_家_{i+1}"
             if assigned_home not in can_go_place: can_go_place.append(assigned_home)
             yield from add_log(f"注意：预设房屋用尽，为 {mbti} 分配动态房屋: {assigned_home}", "WARN")
        try:
            agent = TownAgent(agent_id_mbti=mbti, initial_home_name=assigned_home, map_layout=MAP)
            agents.append(agent)
            yield from add_log(f"初始化 Agent: {agent.name} ({agent.MBTI}) @ {agent.home} (Pos: {agent.position})", "INFO")
        except Exception as e:
            yield from add_log(f"初始化 Agent {mbti} 失败: {e}", "ERROR")
            yield from add_log(traceback.format_exc(), "DEBUG")
    if not agents:
        yield from add_log("错误：未能成功初始化任何 Agents。", "ERROR"); return

    # Initialize Buildings (Global objects)
    buildings = {}
    for r, row in enumerate(MAP):
        for c, cell in enumerate(row):
            if cell != '#' and cell not in buildings:
                 buildings[cell] = Building(cell, (r, c))
    # Ensure homes also have Building objects for damage tracking
    for home_name in used_homes:
        if home_name not in buildings:
             pos = agents[0]._find_initial_pos(home_name) if agents else (0,0)
             buildings[home_name] = Building(home_name, pos)
    # Make sure all agents' current_building references are updated at start
    for agent in agents:
         agent.update_current_building(buildings)


    # --- Simulation Time Initialization ---
    try:
        current_sim_time_dt = datetime(start_year, start_month, start_day, start_hour, start_minute)
        sim_end_time_dt = current_sim_time_dt + timedelta(minutes=total_sim_duration_minutes)
        yield from add_log(f"模拟开始时间: {current_sim_time_dt.strftime('%Y年%m月%d日 %H:%M:%S')} ({get_weekday(current_sim_time_dt.strftime('%Y-%m-%d-%H-%M'))})", "INFO")
        yield from add_log(f"模拟结束时间: {sim_end_time_dt.strftime('%Y年%m月%d日 %H:%M:%S')}", "INFO")
    except Exception as e:
        yield from add_log(f"初始时间设置错误: {e}. 请检查输入。", "ERROR")
        return

    # --- Earthquake Event Scheduling ---
    scheduled_eq_events = [] # List of {'time_dt': datetime, 'duration': int, 'intensity': float}
    if eq_enabled:
        try:
            raw_eq_events = json.loads(eq_events_json_str)
            for eq_data in raw_eq_events:
                eq_time_str = eq_data.get('time')
                eq_duration = eq_data.get('duration')
                eq_intensity = eq_data.get('intensity', random.uniform(0.5, 0.95)) # Default intensity
                if not (eq_time_str and isinstance(eq_duration, (int, float)) and eq_duration > 0):
                    raise ValueError(f"Invalid earthquake event data: {eq_data}")
                eq_time_dt = datetime.strptime(eq_time_str, "%Y-%m-%d-%H-%M")
                scheduled_eq_events.append({'time_dt': eq_time_dt, 'duration': int(eq_duration), 'intensity': float(eq_intensity)})
            scheduled_eq_events.sort(key=lambda x: x['time_dt']) # Sort by time
            yield from add_log(f"已排程 {len(scheduled_eq_events)} 场地震事件。", "INFO")
            for eq in scheduled_eq_events:
                yield from add_log(f"  - 地震计划于 {eq['time_dt'].strftime('%Y-%m-%d %H:%M')}, 持续 {eq['duration']} 分钟, 强度约 {eq['intensity']:.1f}", "INFO")
        except json.JSONDecodeError:
            yield from add_log("错误：地震事件列表格式无效。请使用正确的 JSON 数组格式。", "ERROR")
            eq_enabled = False
        except ValueError as e:
            yield from add_log(f"错误：地震事件数据解析失败: {e}", "ERROR")
            eq_enabled = False
        except Exception as e:
            yield from add_log(f"加载地震事件时发生未知错误: {e}", "ERROR")
            eq_enabled = False

    # --- Simulation State Variables ---
    current_phase = "Normal"
    # These track the end times of ongoing phases
    next_earthquake_event_idx = 0 # Index of the next earthquake to trigger
    current_quake_intensity = 0.0
    quake_end_time_dt = None
    recovery_end_time_dt = None
    post_quake_discussion_end_time_dt = None # For 6-hour discussion period
    # To manage earthquake/recovery step durations
    EARTHQUAKE_STEP_MINUTES = 1
    RECOVERY_STEP_MINUTES = 5

    # Variable to control chat content based on recent disaster
    # This will be `None` normally, or a context string during `PostQuakeDiscussion`
    post_quake_chat_context = None

    # === Main Simulation Loop ===
    sim_step_counter = 0 # To count "main" simulation steps

    while current_sim_time_dt < sim_end_time_dt:
        sim_step_counter += 1 # Increment main step counter
        current_time_hm_str = current_sim_time_dt.strftime('%H-%M')
        current_date_weekday_str = f"{current_sim_time_dt.strftime('%Y-%m-%d')}-{get_weekday(current_sim_time_dt.strftime('%Y-%m-%d-%H-%M'))}"
        log_header_time_str = format_date_time(current_sim_time_dt.strftime('%Y-%m-%d-%H-%M'))
        status_indicator = ""

        # --- Phase Management ---
        if current_phase == "Normal":
            # Check for next scheduled earthquake
            if eq_enabled and next_earthquake_event_idx < len(scheduled_eq_events):
                next_eq = scheduled_eq_events[next_earthquake_event_idx]
                if current_sim_time_dt >= next_eq['time_dt']:
                    current_phase = "Earthquake"
                    current_quake_intensity = next_eq['intensity']
                    quake_end_time_dt = current_sim_time_dt + timedelta(minutes=next_eq['duration'])
                    next_earthquake_event_idx += 1 # Move to next scheduled quake

                    yield from add_log(f"!!! 地震开始 !!! 强度: {current_quake_intensity:.2f}. 持续 {next_eq['duration']} 分钟. 预计结束于: {quake_end_time_dt.strftime('%H:%M')}", "EVENT")
                    yield from add_log("--- Agent 地震反应 (打断行动) ---", "SUB")
                    for agent in agents:
                        agent.interrupt_action() # Store interrupted task
                        agent.disaster_experience_log = [] # Clear previous disaster logs for new event
                        agent.react_to_earthquake(current_quake_intensity, buildings, agents) # 傳遞 agents 列表
                        yield from add_log(f"  {agent.name}: {agent.curr_action} ({agent.curr_action_pronunciatio}), HP:{agent.health}, State:{agent.mental_state}", "ACTION")
                    status_indicator = "[ 地震中! ]"

            if current_phase == "Normal": # Still in Normal phase if no quake triggered
                yield from add_log(f"--- 模拟步骤 {sim_step_counter} | 时间: {log_header_time_str} ({current_date_weekday_str}) | 阶段: Normal ---", "STEP")
                # Daily Planning (03:00)
                if current_time_hm_str == "03-00":
                    yield from add_log(f"--- 新的一天 ({current_date_weekday_str}) | 执行每日计划 ---", "EVENT")
                    for agent in agents:
                         if agent.health <=0 : continue
                         if agent.talk_arr:
                              agent.memory = summarize(agent.talk_arr, current_date_weekday_str, agent.name)
                              agent.talk_arr = ""
                              yield from add_log(f"  {agent.name}: 记忆已更新。", "INFO")
                         if agent.last_action == "睡觉":
                              agent.goto_scene(agent.home, buildings)
                              agent.mental_state = "calm"
                              agent.health = min(100, agent.health + random.randint(15, 30))
                         persona_info = agent.persona_summary
                         base_schedule_tasks = run_gpt_prompt_generate_hourly_schedule(persona_info, current_date_weekday_str)
                         agent.wake = run_gpt_prompt_wake_up_hour(persona_info, current_date_weekday_str, base_schedule_tasks)
                         agent.schedule_time = update_schedule(agent.wake, base_schedule_tasks)
                         agent.schedule_time = modify_schedule(agent.schedule_time, current_date_weekday_str, agent.memory, agent.wake, persona_info)
                         is_sleeping = compare_times(current_time_hm_str, agent.wake)
                         agent.curr_action = "睡觉" if is_sleeping else find_current_activity(current_time_hm_str, agent.schedule_time)[0]
                         agent.last_action = agent.curr_action
                         agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                         yield from add_log(f"  {agent.name}: 醒来时间 {agent.wake}, 日程已生成。当前: {agent.curr_action}", "INFO")

                # Normal Agent Action Update
                yield from add_log("--- Agent 行动更新 ---", "SUB")
                active_agents_for_chat = []
                for agent in agents:
                    if agent.health <= 0: continue
                    log_prefix = f"  {agent.name}:"
                    log_suffix = f"({agent.curr_action_pronunciatio}) @ {agent.curr_place} (Pos:{agent.position}) | HP:{agent.health} St:{agent.mental_state}"
                    is_sleeping_now = compare_times(current_time_hm_str, agent.wake)
                    if is_sleeping_now:
                        if agent.curr_action != "睡觉":
                            agent.curr_action = "睡觉"
                            agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                            agent.goto_scene(agent.home, buildings)
                            yield from add_log(f"{log_prefix} {agent.curr_action} {log_suffix}", "ACTION")
                    else:
                        if not isinstance(agent.schedule_time, list): agent.schedule_time = [['自由活动', current_time_hm_str]]
                        new_action, _ = find_current_activity(current_time_hm_str, agent.schedule_time)
                        if agent.last_action != new_action or agent.curr_place == "Initializing":
                            agent.curr_action = new_action
                            agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                            persona_info = agent.persona_summary
                            new_place = go_map(agent.name, agent.home, agent.curr_place, can_go_place, agent.curr_action)
                            moved = False
                            if new_place != agent.curr_place and new_place in can_go_place: moved = agent.goto_scene(new_place, buildings)
                            log_suffix = f"({agent.curr_action_pronunciatio}) @ {agent.curr_place} (Pos:{agent.position}) | HP:{agent.health} St:{agent.mental_state}"
                            if moved: yield from add_log(f"{log_prefix} 前往 {agent.curr_place} 执行 {agent.curr_action} {log_suffix}", "ACTION")
                            else: yield from add_log(f"{log_prefix} 在 {agent.curr_place} 开始 {agent.curr_action} {log_suffix}", "ACTION")
                            agent.last_action = agent.curr_action
                        else:
                             yield from add_log(f"{log_prefix} 继续 {agent.curr_action} {log_suffix}", "ACTION")
                    active_agents_for_chat.append(agent)

                # Normal Chat Interaction
                if len(active_agents_for_chat) >= 2:
                    chat_group = DBSCAN_chat(active_agents_for_chat)
                    if chat_group:
                        agent_names = " & ".join([a.id for a in chat_group])
                        chat_location = chat_group[0].curr_place
                        yield from add_log(f"--- {agent_names} @ {chat_location} 相遇并聊天 ---", "EVENT")
                        original_actions = {a.name: a.curr_action for a in chat_group}
                        for agent in chat_group:
                            agent.curr_action = "聊天"
                            agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio("聊天")
                            yield from add_log(f"  {agent.name}: 暂停 {original_actions[agent.name]}, 开始聊天 ({agent.curr_action_pronunciatio})", "ACTION")
                        agents_in_chat_pair = random.sample(chat_group, 2)
                        a1, a2 = agents_in_chat_pair[0], agents_in_chat_pair[1]
                        context = f"{a1.name} 原本在 {original_actions[a1.name]}, {a2.name} 原本在 {original_actions[a2.name]}"
                        try:
                            chat_result = double_agents_chat(
                                chat_location, a1.name, a2.name, context,
                                a1.talk_arr[-600:], a2.talk_arr[-600:], current_date_weekday_str, eq_ctx=None # No quake context here
                            )
                            chat_json = json.dumps(chat_result, ensure_ascii=False)
                            yield from add_log(chat_json, "CHAT")
                            for agent in agents_in_chat_pair: agent.talk_arr += chat_json + "\n"
                        except Exception as e: yield from add_log(f"聊天生成失败: {e}", "ERROR")

                current_sim_time_dt += timedelta(minutes=min_per_step_normal) # Advance time by normal step
                continue # Go to next loop iteration

        elif current_phase == "Earthquake":
            yield from add_log(f"--- 模拟步骤 {sim_step_counter} | 时间: {log_header_time_str} ({current_date_weekday_str}) | 阶段: Earthquake {status_indicator} ---", "STEP")
            # --- Earthquake Actions (1 minute per sub-step) ---
            yield from add_log("--- 地震持续中: Agent 行为与主观想法 ---", "SUB")
            active_quake_agents = 0
            for agent in agents:
                if agent.health > 0:
                    action_log_msg = agent.perform_earthquake_step_action(agents, buildings, current_quake_intensity)
                    if action_log_msg: yield from add_log(action_log_msg, "ACTION")
                    active_quake_agents += 1
            if active_quake_agents == 0: yield from add_log("所有有意识的 Agent 均已行动。", "INFO")

            # Check if earthquake duration ends
            if current_sim_time_dt + timedelta(minutes=EARTHQUAKE_STEP_MINUTES) >= quake_end_time_dt:
                current_sim_time_dt = quake_end_time_dt # Align time to exact end
                yield from add_log(f"!!! 地震结束 @ {current_sim_time_dt.strftime('%H:%M')} !!!", "EVENT")
                current_phase = "Recovery"
                recovery_end_time_dt = current_sim_time_dt + timedelta(minutes=60) # 1 hour recovery
                yield from add_log(f"--- 进入 1 小时灾后恢复阶段 (至 {recovery_end_time_dt.strftime('%H:%M')}) ---", "EVENT")

                # Damage report immediately after earthquake ends
                report = generate_damage_report(agents, buildings)
                yield from add_log(report, "REPORT")

                # Update agent memories with disaster summary
                yield from add_log("--- 更新 Agent 记忆 (地震经历总结) ---", "SUB")
                for agent in agents:
                    if agent.health > 0:
                        # Use LLM to summarize individual disaster experience
                        disaster_summary = run_gpt_prompt_summarize_disaster(
                            agent.name, agent.MBTI, agent.health, agent.disaster_experience_log
                        )
                        agent.memory += f"\n[灾害记忆] {disaster_summary}" # Append to memory
                        agent.disaster_experience_log = [] # Clear log after summarizing
                        yield from add_log(f"  {agent.name}: 记忆已更新 - '{disaster_summary}'", "INFO")
                    else:
                        yield from add_log(f"  {agent.name}: 昏迷，记忆未更新。", "INFO")

            current_sim_time_dt += timedelta(minutes=EARTHQUAKE_STEP_MINUTES) # Advance time by 1 minute
            continue # Go to next loop iteration

        elif current_phase == "Recovery":
            yield from add_log(f"--- 模拟步骤 {sim_step_counter} | 时间: {log_header_time_str} ({current_date_weekday_str}) | 阶段: Recovery {status_indicator} ---", "STEP")
            # --- Recovery Actions (5 minutes per sub-step) ---
            yield from add_log("--- 灾后恢复行动 ---", "SUB")
            active_recovery_agents = 0
            for agent in agents:
                if agent.health > 0:
                    action_log_msg = agent.perform_recovery_step_action(agents, buildings)
                    if action_log_msg: yield from add_log(action_log_msg, "ACTION")
                    active_recovery_agents +=1
            if active_recovery_agents == 0: yield from add_log("所有有意识的 Agent 均已行动。", "INFO")

            # Check if recovery phase ends
            if current_sim_time_dt + timedelta(minutes=RECOVERY_STEP_MINUTES) >= recovery_end_time_dt:
                current_sim_time_dt = recovery_end_time_dt # Align time to exact end
                yield from add_log(f"--- 灾后恢复阶段结束 @ {current_sim_time_dt.strftime('%H:%M')} ---", "EVENT")
                current_phase = "PostQuakeDiscussion"
                post_quake_discussion_end_time_dt = current_sim_time_dt + timedelta(hours=6) # 6 hours discussion
                yield from add_log(f"--- 进入 6 小时灾后讨论期 (至 {post_quake_discussion_end_time_dt.strftime('%H:%M')}) ---", "EVENT")
                post_quake_chat_context = "（刚刚经历了一场地震，对话可能会围绕地震及灾后情况展开，请根据性格特点体现不同的关注点。）"
                # Optionally, here we could "reset" the main clock's step counter
                # but since we're using current_sim_time_dt, it's implicitly handled.
                # The main simulation loop (while current_sim_time_dt < sim_end_time_dt) will continue.

            current_sim_time_dt += timedelta(minutes=RECOVERY_STEP_MINUTES) # Advance time by 5 minutes
            continue # Go to next loop iteration

        elif current_phase == "PostQuakeDiscussion":
            status_indicator = "[ 灾后讨论期 ]"
            yield from add_log(f"--- 模拟步骤 {sim_step_counter} | 时间: {log_header_time_str} ({current_date_weekday_str}) | 阶段: Post-Quake Discussion {status_indicator} ---", "STEP")
            # Check if discussion period ends
            if current_sim_time_dt >= post_quake_discussion_end_time_dt:
                yield from add_log(f"--- 灾后讨论期结束 @ {current_sim_time_dt.strftime('%H:%M')} ---", "EVENT")
                current_phase = "Normal"
                post_quake_chat_context = None # Clear context
                yield from add_log("--- 模拟回到正常阶段 ---", "EVENT")

            # Normal Agent Action Update
            yield from add_log("--- Agent 行动更新 ---", "SUB")
            active_agents_for_chat = []
            for agent in agents:
                 if agent.health <= 0: continue
                 log_prefix = f"  {agent.name}:"
                 log_suffix = f"({agent.curr_action_pronunciatio}) @ {agent.curr_place} (Pos:{agent.position}) | HP:{agent.health} St:{agent.mental_state}"
                 is_sleeping_now = compare_times(current_time_hm_str, agent.wake)
                 if is_sleeping_now:
                     if agent.curr_action != "睡觉":
                         agent.curr_action = "睡觉"
                         agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                         agent.goto_scene(agent.home, buildings)
                         yield from add_log(f"{log_prefix} {agent.curr_action} {log_suffix}", "ACTION")
                 else:
                     if not isinstance(agent.schedule_time, list): agent.schedule_time = [['自由活动', current_time_hm_str]]
                     new_action, _ = find_current_activity(current_time_hm_str, agent.schedule_time)
                     if agent.last_action != new_action or agent.curr_place == "Initializing":
                         agent.curr_action = new_action
                         agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                         persona_info = agent.persona_summary
                         new_place = go_map(agent.name, agent.home, agent.curr_place, can_go_place, agent.curr_action)
                         moved = False
                         if new_place != agent.curr_place and new_place in can_go_place: moved = agent.goto_scene(new_place, buildings)
                         log_suffix = f"({agent.curr_action_pronunciatio}) @ {agent.curr_place} (Pos:{agent.position}) | HP:{agent.health} St:{agent.mental_state}"
                         if moved: yield from add_log(f"{log_prefix} 前往 {agent.curr_place} 执行 {agent.curr_action} {log_suffix}", "ACTION")
                         else: yield from add_log(f"{log_prefix} 在 {agent.curr_place} 开始 {agent.curr_action} {log_suffix}", "ACTION")
                         agent.last_action = agent.curr_action
                     else:
                         yield from add_log(f"{log_prefix} 继续 {agent.curr_action} {log_suffix}", "ACTION")
                     active_agents_for_chat.append(agent)

            # Chat Interaction (with post-quake context)
            if len(active_agents_for_chat) >= 2:
                 chat_group = DBSCAN_chat(active_agents_for_chat)
                 if chat_group:
                     agent_names = " & ".join([a.id for a in chat_group])
                     chat_location = chat_group[0].curr_place
                     yield from add_log(f"--- {agent_names} @ {chat_location} 相遇并聊天 ---", "EVENT")
                     original_actions = {a.name: a.curr_action for a in chat_group}
                     for agent in chat_group:
                         agent.curr_action = "聊天"
                         agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio("聊天")
                         yield from add_log(f"  {agent.name}: 暂停 {original_actions[agent.name]}, 开始聊天 ({agent.curr_action_pronunciatio})", "ACTION")
                     agents_in_chat_pair = random.sample(chat_group, 2)
                     a1, a2 = agents_in_chat_pair[0], agents_in_chat_pair[1]
                     context = f"{a1.name} 原本在 {original_actions[a1.name]}, {a2.name} 原本在 {original_actions[a2.name]}"
                     try:
                         chat_result = double_agents_chat(
                             chat_location, a1.name, a2.name, context,
                             a1.talk_arr[-600:], a2.talk_arr[-600:], current_date_weekday_str, eq_ctx=post_quake_chat_context
                         )
                         chat_json = json.dumps(chat_result, ensure_ascii=False)
                         yield from add_log(chat_json, "CHAT")
                         for agent in agents_in_chat_pair: agent.talk_arr += chat_json + "\n"
                     except Exception as e: yield from add_log(f"聊天生成失败: {e}", "ERROR")

            current_sim_time_dt += timedelta(minutes=min_per_step_normal) # Advance time by normal step
            continue # Go to next loop iteration

        # Fallback for unexpected states or if time exceeds defined phases
        yield from add_log(f"--- 模拟步骤 {sim_step_counter} | 时间: {log_header_time_str} ({current_date_weekday_str}) | 阶段: Unhandled ({current_phase}) ---", "WARN")
        current_sim_time_dt += timedelta(minutes=min_per_step_normal) # Advance time by normal step
        continue


    # --- End of Simulation ---
    end_msg = f"--- 模拟正常结束 @ {format_date_time(current_sim_time_dt.strftime('%Y-%m-%d-%H-%M'))} ({get_weekday(current_sim_time_dt.strftime('%Y-%m-%d-%H-%M'))}) ---"
    yield from add_log(end_msg, "EVENT")
    yield "\n".join(output_log) # Final yield to ensure all logs are sent


# --- Helper Functions for UI ---
def weekday2START_TIME_from_date(year, month, day):
    # This helper is for internal use to get weekday from Y/M/D for display
    try:
        dt = datetime(year, month, day)
        weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"]
        return weekdays[dt.weekday()]
    except ValueError:
        return "日期无效"

def generate_tabs(all_mbti_types):
    target_files = get_target_files_for_agents(all_mbti_types)
    if not target_files:
         gr.Markdown("未能找到任何 Agent 的配置文件。请确保初始化已运行。")
         return
    for agent_name in all_mbti_types:
        if agent_name in target_files:
            file_path = target_files[agent_name]
            def create_save_callback(fp):
                def save_callback(new_content): return save_file(fp, new_content)
                return save_callback
            with gr.Tab(agent_name):
                file_content = read_file(file_path)
                textbox = gr.Textbox(label=f"内容", value=file_content, lines=20, max_lines=40, interactive=True)
                save_button = gr.Button(f"💾 保存 {agent_name}")
                save_status = gr.Label()
                save_button.click(create_save_callback(file_path), inputs=[textbox], outputs=save_status)
        else:
             with gr.Tab(agent_name):
                 gr.Markdown(f"未能找到 **{agent_name}** 的配置文件。\n路径: `{os.path.join(BASE_DIR, agent_name, TARGET_FILENAME)}`")


# --- Gradio Interface ---
def launch_gradio_interface():
    os.makedirs(BASE_DIR, exist_ok=True)
    print("Initializing agent profiles...")
    initialize_agent_profiles(DEFAULT_MBTI_TYPES)
    print("Profile initialization complete.")

    with gr.Blocks(theme=gr.themes.Default(), css="footer {display: none !important;}") as demo:
        gr.Markdown("# 🏙️ AI 小镇生活模拟器 (v4 - 灾害事件与时间流重构)")
        if not LLM_LOADED:
             gr.Markdown("⚠️ **警告:** 未能加载本地 LLM 函数。模拟行为将受限。")

        with gr.Row():
            # --- Left Column: Simulation Controls ---
            with gr.Column(scale=2):
                gr.Markdown("### 模拟控制")
                with gr.Accordion("基本设置与起始时间", open=True):
                    with gr.Row():
                        sim_duration_minutes = gr.Number(value=60*12, label="总模拟时长 (分钟)", minimum=60, step=60,info="例如：720分钟 = 12小时")
                        min_per_step_normal_num = gr.Number(value=5, label="正常阶段步长 (分钟/步)", minimum=1, step=1,info="建议 5 或 10 分钟")
                    with gr.Row():
                        start_year_num = gr.Number(value=2024, label="起始年份", minimum=2020, step=1)
                        start_month_num = gr.Slider(value=11, label="起始月份", minimum=1, maximum=12, step=1)
                        start_day_num = gr.Slider(value=18, label="起始日期", minimum=1, maximum=31, step=1)
                    with gr.Row():
                        start_hour_num = gr.Slider(value=3, label="起始小时 (0-23)", minimum=0, maximum=23, step=1)
                        start_minute_num = gr.Slider(value=0, label="起始分钟 (0-59)", minimum=0, maximum=59, step=5)
                        # Display inferred weekday
                        start_weekday_display = gr.Textbox(label="起始星期", interactive=False, value="星期一")

                        # Update weekday display when year/month/day changes
                        def update_weekday_display(year, month, day):
                            try:
                                return weekday2START_TIME_from_date(int(year), int(month), int(day))
                            except ValueError:
                                return "日期无效"
                        start_year_num.change(update_weekday_display, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display)
                        start_month_num.change(update_weekday_display, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display)
                        start_day_num.change(update_weekday_display, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display)


                with gr.Accordion("选择 Agents (1-16 名)", open=True):
                     selected_mbtis_cb_group = gr.CheckboxGroup(
                         DEFAULT_MBTI_TYPES, label="勾选要模拟的 Agent",
                         value=["ISTJ", "ENFP", "ESFJ", "INTP", "ESTP"],
                         info="选择1到16个Agent。Agent越多，模拟速度越慢。"
                     )

                with gr.Accordion("灾害设置: 地震事件排程", open=True):
                     eq_enabled_cb = gr.Checkbox(label="启用地震事件", value=True, info="启用后，将按照下方列表中的时间触发地震。")
                     # Default JSON for multiple events
                     default_eq_events = json.dumps([
                         {"time": "2024-11-18-08-00", "duration": 5, "intensity": 0.7},
                         {"time": "2024-11-18-12-00", "duration": 10, "intensity": 0.8}
                     ], indent=2)
                     eq_events_tb = gr.Textbox(
                         label="地震事件列表 (JSON 格式)",
                         value=default_eq_events,
                         lines=10,
                         info="格式: [{'time': 'YYYY-MM-DD-HH-MM', 'duration': 分钟, 'intensity': 强度(0.5-1.0)}]"
                     )


                simulate_button = gr.Button("▶️ 运行模拟", variant="primary", size="lg")

                gr.Markdown("### 模拟日志")
                simulation_output_tb = gr.Textbox(
                    label="Simulation Log", interactive=False, lines=35, max_lines=60, autoscroll=True
                 )

            # --- Right Column: Agent Profile Editor ---
            with gr.Column(scale=1):
                gr.Markdown("### Agent 配置文件编辑器")
                gr.Markdown("编辑所有可能的 Agent 的基础设定。")
                with gr.Tabs() as profile_tabs:
                     generate_tabs(DEFAULT_MBTI_TYPES)

        # --- Button Click Logic ---
        run_inputs = [
            sim_duration_minutes,
            min_per_step_normal_num,
            start_year_num, start_month_num, start_day_num, start_hour_num, start_minute_num,
            selected_mbtis_cb_group,
            eq_enabled_cb, eq_events_tb
        ]
        simulate_button.click(
            fn=simulate_town_life,
            inputs=run_inputs,
            outputs=[simulation_output_tb]
        )

    print("Gradio Interface configured. Launching...")
    demo.queue().launch(share=False)

# --- Main Execution ---
if __name__ == "__main__":
    launch_gradio_interface()
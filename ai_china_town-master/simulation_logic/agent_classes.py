# simulation_logic/agent_classes.py (完整最終版)

import random
import os
import json
from datetime import datetime, timedelta
import sys
# 从我们重构后的模组中导入
from tools.LLM import run_gpt_prompt as llm
from .agent_memory import update_agent_schedule
from .schedule_manager import 從檔案載入行程表

# --- 核心：定義場景中的傳送門連接關係 ---
# 鍵(Key): 代理人當前所在的傳送點 GameObject 名稱。
# 值(Value): 代理人穿過該傳送點後，應該出現的目標傳送點 GameObject 名稱。
PORTAL_CONNECTIONS = {
    # --- 公寓出入口 (雙向) ---
    "公寓大門_室內": "公寓大門_室外",
    "公寓大門_室外": "公寓大門_室內",
    "公寓側門_室內": "公寓側門_室外",
    "公寓側門_室外": "公寓側門_室內",
    "公寓頂樓_室內": "公寓頂樓_室外",
    "公寓頂樓_室外": "公寓頂樓_室內",

    # --- 公寓樓層間 (雙向) ---
    "公寓一樓_室內": "公寓二樓_室內",
    "公寓二樓_室內": "公寓一樓_室內", # 假設可以從二樓走回一樓
    "公寓二樓_室內_上": "公寓頂樓_室內", # 假設有明確的上下樓物件
    "公寓頂樓_室內_下": "公寓二樓_室內",

    # --- 超市出入口 (雙向) ---
    "超市側門_室內": "超市側門_室外",
    "超市側門_室外": "超市側門_室內",
    "超市左門_室內": "超市左門_室外",
    "超市左門_室外": "超市左門_室內",
    "超市右門_室內": "超市右門_室外",
    "超市右門_室外": "超市右門_室內",
    
    # --- 地鐵出入口 (複雜關係，雙向) ---
    # 從室內出去 (一對多，隨機選一個出口)
    "地鐵左樓梯_室內": ["地鐵左入口_室外", "地鐵上入口_室外"],
    "地鐵右樓梯_室內": ["地鐵右入口_室外", "地鐵下入口_室外"],
    # 從室外進來 (多對一)
    "地鐵左入口_室外": "地鐵左樓梯_室內",
    "地鐵上入口_室外": "地鐵左樓梯_室內",
    "地鐵右入口_室外": "地鐵右樓梯_室內",
    "地鐵下入口_室外": "地鐵右樓梯_室內",

    # --- 其他單一出入口建築 (雙向) ---
    "學校門口_室內": "學校門口_室外",
    "學校門口_室外": "學校門口_室內",
    "健身房_室內": "健身房_室外",
    "健身房_室外": "健身房_室內",
    "餐廳_室內": "餐廳_室外",
    "餐廳_室外": "餐廳_室內",
}

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

class Building:
    def __init__(self, bld_id, position, integrity=100.0):
        self.id = bld_id
        self.position = position
        self.integrity = float(integrity)

    def apply_damage(self, intensity):
        vulnerability = (100 - self.integrity) / 100.0
        damage = (intensity * 20) + (intensity * 30 * vulnerability) + random.uniform(-5, 5)
        self.integrity = max(0, self.integrity - max(0, damage))
        return damage

class TownAgent:
    def __init__(self, agent_id_mbti, initial_home_name, available_locations):
        self.id = agent_id_mbti.upper()
        self.name = agent_id_mbti.upper()
        self.MBTI = agent_id_mbti.upper()
        self.available_locations = available_locations 
        self.home = initial_home_name
        
        mbti_info = MBTI_PROFILES.get(self.MBTI, {'desc': '未知個性', 'cooperation': 0.5})
        self.personality_desc = mbti_info['desc']
        self.cooperation_inclination = mbti_info['cooperation']
        self.persona_summary = f"MBTI: {self.MBTI}. 個性: {self.personality_desc}"
        
        self.curr_place = initial_home_name
        self.target_place = initial_home_name
        
        self.last_action = "等待初始化"
        self.curr_action = "等待初始化"
        self.curr_action_pronunciatio = "⏳"
        self.current_thought = ""
        self.health = 100
        self.is_injured = False
        self.mental_state = "calm"
        self.current_building = None
        self.interrupted_action = None
        self.memory = "尚未生成"
        self.weekly_schedule = {}
        self.daily_schedule = []
        self.wake_time = "07-00"
        self.sleep_time = "23-00"
        self.disaster_experience_log = []

    def is_location_outdoors(self, location_name):
        return "_室外" in str(location_name)

    def find_path(self, destination):
        if not destination or destination == self.curr_place:
            return self.curr_place
        
        is_current_outdoors = self.is_location_outdoors(self.curr_place)
        is_destination_outdoors = self.is_location_outdoors(destination)

        if is_current_outdoors == is_destination_outdoors:
            return destination
        
        elif is_current_outdoors and not is_destination_outdoors:
            return f"{destination}_門口_室外"
            
        else:
            if self.curr_place in PORTAL_CONNECTIONS:
                return self.curr_place
            
            building_name = self.curr_place.split('_')[0]
            main_exit = f"{building_name}大門_室內"
            if main_exit in PORTAL_CONNECTIONS:
                return main_exit
            for portal in PORTAL_CONNECTIONS.keys():
                if portal.startswith(building_name) and "_室內" in portal:
                    return portal
        
        return destination

    def get_schedule_item_at(self, current_time_hm_str):
        try:
            current_t = datetime.strptime(current_time_hm_str, "%H-%M")
        except (ValueError, TypeError):
            return None

        latest_item = None
        for item in self.daily_schedule:
            if len(item) < 2:
                continue
            action, start_str = item[0], item[1]
            target = item[2] if len(item) > 2 else action
            try:
                start_t = datetime.strptime(start_str, "%H-%M")
            except (ValueError, TypeError):
                continue
            if start_t <= current_t:
                latest_item = (action, target)
        return latest_item

    async def update_action_by_time(self, current_time_hm_str):
        item = self.get_schedule_item_at(current_time_hm_str)
        if not item:
            return
        action, target = item
        if action != self.curr_action or target != self.target_place:
            await self.set_new_action(action, target)

    def teleport(self, target_portal_name: str):
        destination = PORTAL_CONNECTIONS.get(target_portal_name)
        
        if not destination:
            print(f"⚠️ [傳送警告] 在 PORTAL_CONNECTIONS 中找不到 '{target_portal_name}' 的對應目標。")
            self.current_thought = f"嗯？這扇門好像是壞的... ({target_portal_name})"
            return

        if isinstance(destination, list):
            self.curr_place = random.choice(destination)
        else:
            self.curr_place = destination
            
        self.current_thought = f"好了，我到 '{self.curr_place}' 了。"
        print(f"✅ [傳送成功] {self.name} 從 '{target_portal_name}' 傳送到 '{self.curr_place}'")

    async def set_new_action(self, new_action, destination):
        self.interrupt_action()

        self.curr_action = new_action
        self.target_place = destination
        self.curr_place = self.find_path(destination)

        try:
            if new_action == "醒來":
                self.current_thought = "新的一天開始了！"
            else:
                self.current_thought = await llm.generate_action_thought(
                    self.persona_summary, self.curr_place, new_action
                )
        except Exception:
            self.current_thought = ""

        try:
            self.curr_action_pronunciatio = await llm.run_gpt_prompt_pronunciatio(
                self.curr_action
            )
        except Exception:
            self.curr_action_pronunciatio = ""

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
            self.is_injured = True
            self.mental_state = "unconscious"
            self.curr_action = "Unconscious"
            return
        elif self.health < 50: self.is_injured = True
        
        reaction_action_key = "alert"
        new_mental_state = "alert"
        if self.is_injured: 
            reaction_action_key, new_mental_state = "尋找醫療救助", "injured"
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
        if self.curr_action not in ["睡覺", "Unconscious"]:
            self.interrupted_action = self.curr_action
        else:
            self.interrupted_action = None

    async def initialize_agent(self, current_date, schedule_mode: str, schedule_file_path: str):
        """
        根據指定的模式初始化代理人，徹底分離 preset 和 llm 的邏輯。
        """
        # --- Preset 模式：完全不使用 LLM ---
        if schedule_mode == "preset":
            print(f"🏃 [Agent {self.name}] 正在以 'preset' 模式初始化...")
            try:
                # 記憶：直接使用 personality_desc 作為基本記憶
                self.memory = self.persona_summary
                
                # 從 schedules.json 讀取週計劃和日計劃
                with open(schedule_file_path, "r", encoding="utf-8") as f:
                    all_schedules = json.load(f)
                agent_data = all_schedules.get(self.name)
                if not agent_data:
                    print(f"❌ [Agent {self.name}] 在 '{schedule_file_path}' 中找不到對應的資料。")
                    return False
                
                # 讀取週計劃，如果沒有就給一個預設值
                self.weekly_schedule = agent_data.get("weeklySchedule", {day: "自由活動" for day in ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]})
                
                # 呼叫 update_daily_schedule 處理日計劃
                return await self.update_daily_schedule(current_date, "preset", schedule_file_path)

            except Exception as e:
                print(f"❌ [Agent {self.name}] 在 'preset' 模式初始化期間發生錯誤: {e}")
                return False

        # --- LLM 模式：完全由 LLM 生成 ---
        elif schedule_mode == "llm":
            print(f"🤖 [Agent {self.name}] 正在以 'llm' 模式初始化...")
            # 1. 生成初始記憶
            memory, mem_success = await llm.run_gpt_prompt_generate_initial_memory(
                self.name, self.MBTI, self.persona_summary, self.home
            )
            if not mem_success:
                print(f"❌ [Agent {self.name}] LLM 生成初始記憶失敗。")
                return False
            self.memory = memory
            
            # 2. 生成週計劃
            schedule, sched_success = await llm.run_gpt_prompt_generate_weekly_schedule(self.persona_summary)
            if not sched_success:
                print(f"❌ [Agent {self.name}] LLM 生成週計劃失敗。")
                return False
            self.weekly_schedule = schedule

            # 3. 生成日計劃
            return await self.update_daily_schedule(current_date, "llm", schedule_file_path)

        # 未知模式
        else:
            print(f"❌ [Agent {self.name}] 未知的 schedule_mode: '{schedule_mode}'")
            return False

    async def update_daily_schedule(self, current_date, schedule_mode: str, schedule_file_path: str):
        """根据模式更新并设定一天的日程。"""
        
        if schedule_mode == "llm":
            print(f"🤖 [Agent {self.name}] 使用 LLM 生成今日行程...")
            weekday_name = current_date.strftime('%A')
            today_goal = self.weekly_schedule.get(weekday_name, "自由活動")
            raw_tasks = await llm.run_gpt_prompt_generate_hourly_schedule(self.persona_summary, current_date.strftime('%Y-%m-%d'), today_goal)
            
            if raw_tasks and isinstance(raw_tasks[0], list):
                wake_time_str = await llm.run_gpt_prompt_wake_up_hour(self.persona_summary, current_date.strftime('%Y-%m-%d'), raw_tasks)
                if not wake_time_str: return False
                self.wake_time = wake_time_str.replace(":", "-")
                
                self.daily_schedule = update_agent_schedule(self.wake_time, raw_tasks)
                
                try:
                    total_duration = sum(int(task[1]) for task in raw_tasks)
                    self.sleep_time = (datetime.strptime(self.wake_time, '%H-%M') + timedelta(minutes=total_duration)).strftime('%H-%M')
                except:
                    self.sleep_time = (datetime.strptime(self.wake_time, '%H-%M') + timedelta(hours=16)).strftime('%H-%M')
                
                return True

        elif schedule_mode == "preset":
            print(f"💾 [Agent {self.name}] 使用预设档案 '{schedule_file_path}' 载入行程...")
            preset_schedule = 從檔案載入行程表(self.name, schedule_file_path)
            if preset_schedule:
                self.daily_schedule = preset_schedule
                if self.daily_schedule:
                    self.wake_time = self.daily_schedule[0][1]
                    last_activity_time = datetime.strptime(self.daily_schedule[-1][1], '%H-%M')
                    self.sleep_time = (last_activity_time + timedelta(hours=1)).strftime('%H-%M')
                print(f"✅ [Agent {self.name}] 已成功從檔案載入行程。")
                return True

        print(f"❌ [Agent {self.name}] 无法生成或载入行程。")
        return False
    async def perform_earthquake_step_action(self, agents, buildings, intensity, disaster_logger, current_time):
        """
        在地震的每一個時間步驟中，決定並執行代理人的行動。
        """
        self.update_current_building(buildings)
        
        # 根據隨機性和建築損毀度，施加持續傷害
        if self.current_building and random.random() < intensity * 0.1 * (120 - self.current_building.integrity) / 100:
            damage = random.randint(1, int(intensity * 10))
            original_hp = self.health
            self.health = max(0, self.health - damage)
            log_msg = f"{self.name} 因建築物搖晃/掉落物受到 {damage} 點傷害 (HP: {self.health})。"
            self.disaster_experience_log.append(log_msg)
            if disaster_logger:
                disaster_logger.記錄事件(self.name, "損失", current_time, {"value": damage, "reason": "Falling Debris"})
            if self.health <= 0:
                self.curr_action = "Unconscious"
                return log_msg + " 代理人已失去意識。"
        
        # 使用 LLM 決定下一步行動
        new_action, new_thought = await llm.run_gpt_prompt_earthquake_step_action(
            self.persona_summary, self.health, self.mental_state, self.curr_place, intensity, self.disaster_experience_log[-5:]
        )
        self.curr_action = new_action
        self.current_thought = new_thought
        self.disaster_experience_log.append(f"在 {self.curr_place} 決定 {new_action}。內心想法: {new_thought}")

        # 執行幫助行為
        help_log = self.perceive_and_help(agents)
        if help_log and disaster_logger:
            # 這裡可以擴充，記錄幫助事件的細節
            disaster_logger.記錄事件(self.name, "合作", current_time, {"details": help_log})

        return f"{self.name} 正在 {self.curr_action} (HP:{self.health})。想法:『{self.current_thought}』"

    async def perform_recovery_step_action(self, agents, buildings, disaster_logger, current_time):
        """
        在地震後的恢復階段，決定並執行代理人的行動。
        """
        # 優先處理自救或互救
        if self.is_injured:
            self.curr_action = "尋找醫療資源或休息"
        else:
            help_log = self.perceive_and_help(agents)
            if help_log:
                self.curr_action = "幫助他人"
                if disaster_logger:
                     disaster_logger.記錄事件(self.name, "合作", current_time, {"details": help_log})
            else:
                # 如果沒有人需要幫助，使用 LLM 決定恢復行動
                self.curr_action = await llm.run_gpt_prompt_get_recovery_action(
                    self.persona_summary, self.mental_state, self.curr_place
                )
        
        log_msg = f"{self.name} 正在 {self.curr_action} (HP:{self.health})。"
        self.disaster_experience_log.append(log_msg)
        return log_msg    
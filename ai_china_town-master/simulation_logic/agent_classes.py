# simulation_logic/agent_classes.py (完整最終版)

from argparse import Action
import random
import os
import json
from datetime import datetime, timedelta
import sys
# 从我们重构后的模组中导入
from tools.LLM import run_gpt_prompt as llm
from .agent_memory import update_agent_schedule
from .schedule_manager import 從檔案載入行程表
# --- 輕量化時使用的預設回應 ---
LIGHTWEIGHT_ACTION_RESPONSES = {
    "睡覺": ("", "💤"),
    "醒來": ("新的一天開始了！", "🌅"),
    "等待初始化": ("稍等，我正在確認今日的安排。", "☕"),
    "意識不明": ("", "💤"),
}
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
LOCATION_ENTRY_PORTALS = {
    "Apartment": "公寓大門_室外",
    "Apartment_F1": "公寓大門_室外",
    "Apartment_F2": "公寓大門_室外",
    "School": "學校門口_室外",
    "Rest": "餐廳_室外",
    "Gym": "健身房_室外",
    "Super": "超市右門_室外",
    "Subway": "地鐵左入口_室外",
}
SUBWAY_INTERIOR_PORTALS = {
    "地鐵左樓梯_室內",
    "地鐵右樓梯_室內"
}
PORTAL_DESTINATION_ALIASES = {
    "公寓大門_室內": "Apartment_F1",
    "公寓側門_室內": "Apartment_F1",
    "公寓一樓_室內": "Apartment_F1",
    "公寓二樓_室內": "Apartment_F2",
    "公寓頂樓_室內": "Apartment_F2",
    "公寓大門_室外": "Exterior",
    "公寓側門_室外": "Exterior",
    "公寓頂樓_室外": "Exterior",
    "健身房_室內": "Gym",
    "健身房_室外": "Exterior",
    "學校門口_室內": "School",
    "學校門口_室外": "Exterior",
    "餐廳_室內": "Rest",
    "餐廳_室外": "Exterior",
    "超市側門_室內": "Super",
    "超市左門_室內": "Super",
    "超市右門_室內": "Super",
    "超市側門_室外": "Exterior",
    "超市左門_室外": "Exterior",
    "超市右門_室外": "Exterior",
    "地鐵左樓梯_室內": "Subway",
    "地鐵右樓梯_室內": "Subway",
    "地鐵左入口_室外": "Exterior",
    "地鐵右入口_室外": "Exterior",
    "地鐵上入口_室外": "Exterior",
    "地鐵下入口_室外": "Exterior",
}
LOCATION_ALIAS_MAP = {
    "apartment": "Apartment_F1",
    "apartment_f1": "Apartment_F1",
    "apartment f1": "Apartment_F1",
    "apartment_f2": "Apartment_F2",
    "apartment f2": "Apartment_F2",
    "apartment_floor2": "Apartment_F2",
    "apartment floor2": "Apartment_F2",
    "公寓": "Apartment_F1",
    "公寓一樓": "Apartment_F1",
    "公寓f1": "Apartment_F1",
    "公寓二樓": "Apartment_F2",
    "公寓f2": "Apartment_F2",
    "學校": "School",
    "学校": "School",
    "school": "School",
    "rest": "Rest",
    "餐廳": "Rest",
    "餐厅": "Rest",
    "咖啡店": "Rest",
    "cafe": "Rest",
    "restaurant": "Rest",
    "gym": "Gym",
    "健身房": "Gym",
    "super": "Super",
    "超市": "Super",
    "商場": "Super",
    "商场": "Super",
    "便利店": "Super",
    "subway": "Subway",
    "地鐵": "Subway",
    "地铁": "Subway",
    "metro": "Subway",
    "exterior": "Exterior",
    "室外": "Exterior",
    "戶外": "Exterior",
    "户外": "Exterior",
    "公園": "Exterior",
    "公园": "Exterior",
    "park": "Exterior",
}

# --- 路由表：定義跨區域移動的中繼點 (CurrentZone, TargetZone) -> NextPortalOrZone ---
LOCATION_ROUTING = {
    # ==========================================
    # 1. 從 [公寓二樓] (Apartment_F2) 出發
    # ==========================================
    # 去一樓：走樓梯下樓
    ("Apartment_F2", "Apartment_F1"): "公寓二樓_室內",
    # 去其他任何地方 (室外、地鐵、商店...)：必須先下樓到一樓
    ("Apartment_F2", "Exterior"): "公寓二樓_室內",
    ("Apartment_F2", "Subway"): "公寓二樓_室內",
    ("Apartment_F2", "School"): "公寓二樓_室內",
    ("Apartment_F2", "Rest"): "公寓二樓_室內",
    ("Apartment_F2", "Gym"): "公寓二樓_室內",
    ("Apartment_F2", "Super"): "公寓二樓_室內",

    # ==========================================
    # 2. 從 [公寓一樓] (Apartment_F1) 出發
    # ==========================================
    # 上樓：走室內樓梯
    ("Apartment_F1", "Apartment_F2"): "公寓一樓_室內",
    # 出門：走大門 (去室外或任何其他建築必須先出門)
    ("Apartment_F1", "Exterior"): "公寓大門_室內",
    ("Apartment_F1", "Subway"): "公寓大門_室內",
    ("Apartment_F1", "School"): "公寓大門_室內",
    ("Apartment_F1", "Rest"): "公寓大門_室內",
    ("Apartment_F1", "Gym"): "公寓大門_室內",
    ("Apartment_F1", "Super"): "公寓大門_室內",

    # ==========================================
    # 3. 從 [室外] (Exterior) 出發 (分發中心)
    # ==========================================
    # 回公寓 (預設到 F1)
    ("Exterior", "Apartment_F1"): "公寓大門_室外",
    # 去公寓二樓：先進大門 (到 F1)
    ("Exterior", "Apartment_F2"): "公寓大門_室外",
    
    # 去地鐵：隨機選一個入口 (這裡指定左入口為主要路徑)
    ("Exterior", "Subway"): "地鐵左入口_室外",
    
    # 去各個衛星建築
    ("Exterior", "School"): "學校門口_室外",
    ("Exterior", "Rest"): "餐廳_室外",
    ("Exterior", "Gym"): "健身房_室外",
    ("Exterior", "Super"): "超市右門_室外", # 根據 Alias 選擇右門作為主要入口

    # ==========================================
    # 4. 從 [地鐵] (Subway) 出發
    # ==========================================
    # 去任何地方都必須先回到地面 (Exterior)
    ("Subway", "Exterior"): "地鐵左樓梯_室內",
    ("Subway", "Apartment_F1"): "地鐵左樓梯_室內",
    ("Subway", "Apartment_F2"): "地鐵左樓梯_室內",
    ("Subway", "School"): "地鐵左樓梯_室內",
    ("Subway", "Rest"): "地鐵左樓梯_室內",
    ("Subway", "Gym"): "地鐵左樓梯_室內",
    ("Subway", "Super"): "地鐵左樓梯_室內",

    # ==========================================
    # 5. 從 [衛星建築] 出發 (School, Rest, Gym, Super)
    # ==========================================
    # 邏輯：如果在這些單一出入口的室內，要去任何其他地方，唯一的路就是出門。
    
    # --- 從學校出發 ---
    ("School", "Exterior"): "學校門口_室內",
    ("School", "Apartment_F1"): "學校門口_室內",
    ("School", "Apartment_F2"): "學校門口_室內",
    ("School", "Subway"): "學校門口_室內",
    ("School", "Rest"): "學校門口_室內",
    ("School", "Gym"): "學校門口_室內",
    ("School", "Super"): "學校門口_室內",

    # --- 從餐廳出發 ---
    ("Rest", "Exterior"): "餐廳_室內",
    ("Rest", "Apartment_F1"): "餐廳_室內",
    ("Rest", "Subway"): "餐廳_室內",
    ("Rest", "School"): "餐廳_室內",
    ("Rest", "Gym"): "餐廳_室內",
    ("Rest", "Super"): "餐廳_室內",

    # --- 從健身房出發 ---
    ("Gym", "Exterior"): "健身房_室內",
    ("Gym", "Apartment_F1"): "健身房_室內",
    ("Gym", "Subway"): "健身房_室內",
    ("Gym", "School"): "健身房_室內",
    ("Gym", "Rest"): "健身房_室內",
    ("Gym", "Super"): "健身房_室內",

    # --- 從超市出發 ---
    # 假設超市內部是連通的，選擇一個主要出口
    ("Super", "Exterior"): "超市右門_室內",
    ("Super", "Apartment_F1"): "超市右門_室內",
    ("Super", "Subway"): "超市右門_室內",
    ("Super", "School"): "超市右門_室內",
    ("Super", "Rest"): "超市右門_室內",
    ("Super", "Gym"): "超市右門_室內",
}

def normalize_location_name(location_name):
    if not location_name:
        return location_name

    location_str = str(location_name).strip()
    if location_str in PORTAL_CONNECTIONS or location_str in PORTAL_DESTINATION_ALIASES:
        return location_str

    lookup_key = location_str.lower()
    return LOCATION_ALIAS_MAP.get(lookup_key, location_str)

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

        normalized_locations = []
        seen_locations = set()
        for loc in available_locations:
            normalized = normalize_location_name(loc)
            if normalized not in seen_locations:
                seen_locations.add(normalized)
                normalized_locations.append(normalized)
        self.available_locations = normalized_locations

        normalized_home = normalize_location_name(initial_home_name)
        if normalized_home not in self.available_locations and self.available_locations:
            normalized_home = self.available_locations[0]

        self.home = normalized_home
        
        mbti_info = MBTI_PROFILES.get(self.MBTI, {'desc': '未知個性', 'cooperation': 0.5})
        self.personality_desc = mbti_info['desc']
        self.cooperation_inclination = mbti_info['cooperation']
        self.persona_summary = f"MBTI: {self.MBTI}. 個性: {self.personality_desc}"
        
        self.curr_place = normalized_home
        self.target_place = normalized_home
        self.previous_place = normalized_home
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
        self._pronunciatio_cache = {}
        self.quake_has_taken_cover = False
        self.quake_evacuation_started = False
        self.quake_cooperation_inclination = min(1.0, self.cooperation_inclination + self._compute_quake_bonus())
        self.quake_support_committed = False
        self.is_thinking = False
        self._thinking_depth = 0
        self.sync_events = []

    def is_location_outdoors(self, location_name):
        name_str = str(location_name)
        if "_室外" in name_str:
            return True
        normalized = normalize_location_name(name_str)
        return normalized == "Exterior"
    
    def _compute_quake_bonus(self) -> float:
        bonus = 0.25
        if 'F' in self.MBTI:
            bonus += 0.2
        if 'E' in self.MBTI:
            bonus += 0.1
        if 'J' in self.MBTI:
            bonus += 0.05
        if self.MBTI.startswith('IN'):
            bonus += 0.05
        return bonus

    def find_path(self, destination):
        previous_target = getattr(self, "target_place", None)
        current_location = self.curr_place or previous_target or self.home or ""
        current_location = normalize_location_name(current_location)
        sleep_keywords = ["睡", "sleep", "Sleep"]

        if not destination or destination == current_location:
            return current_location

        destination_str = str(destination)
        destination_str = normalize_location_name(destination_str)
        
        # [New] Routing Logic
        if (current_location, destination_str) in LOCATION_ROUTING:
            return LOCATION_ROUTING[(current_location, destination_str)]

        if destination_str and destination_str.lower() == "subway":
            if self.curr_place == "Subway" or (self.curr_place and "地鐵" in self.curr_place):
                return "Subway"

        if any(keyword in destination_str for keyword in sleep_keywords):
            if destination_str not in self.available_locations:
                return self.home or current_location

        return destination_str

    def resolve_destination(self, action, destination):
        """Normalize ambiguous destinations to meaningful map locations."""
        previous_target = getattr(self, "target_place", None)
        current_location = self.curr_place or previous_target or self.home or ""
        current_location = normalize_location_name(current_location)
        sleep_keywords = ["睡", "sleep", "Sleep"]

        if not destination or destination == action:
            if any(keyword in str(action) for keyword in sleep_keywords):
                return self.home or current_location
            return previous_target or current_location

        destination_str = str(destination)
        normalized_destination = normalize_location_name(destination_str)
        if any(keyword in destination_str for keyword in sleep_keywords):
            if normalized_destination not in self.available_locations:
                return self.home or current_location

        return normalized_destination

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

    def _enter_thinking(self):
        self._thinking_depth += 1
        self.is_thinking = True

    def _exit_thinking(self):
        if self._thinking_depth > 0:
            self._thinking_depth -= 1
        if self._thinking_depth <= 0:
            self._thinking_depth = 0
            self.is_thinking = False
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
            return None


        if isinstance(destination, list):
            chosen = random.choice(destination)
        else:
            chosen = destination
        
        # [Fix] Update previous place correctly
        self.previous_place = self.curr_place

        if chosen in SUBWAY_INTERIOR_PORTALS:
            canonical_place = "Subway"
        else:
            canonical_place = PORTAL_DESTINATION_ALIASES.get(chosen, chosen)

        canonical_place = normalize_location_name(canonical_place)

        fallback_candidates = []
        if canonical_place in self.available_locations:
            fallback_candidates.append(canonical_place)

        normalized_chosen = normalize_location_name(chosen)
        if normalized_chosen in self.available_locations:
            fallback_candidates.append(normalized_chosen)

        if self.home in self.available_locations:
            fallback_candidates.append(self.home)
        if "Exterior" in self.available_locations:
            fallback_candidates.append("Exterior")
        if self.available_locations:
            fallback_candidates.append(self.available_locations[0])

        safe_location = next((loc for loc in fallback_candidates if loc), canonical_place)
        safe_location = normalize_location_name(safe_location)
        self.curr_place = safe_location
        self.target_place = self.curr_place
        self.current_thought = f"好了，我到 '{self.curr_place}' 了。"
        print(f"✅ [傳送成功] {self.name} 從 '{target_portal_name}' 傳送到 '{self.curr_place}' (出口: {chosen})")
        event_payload = {
            "type": "teleport",
            "fromPortal": target_portal_name,
            "toPortal": chosen,
            "finalLocation": self.curr_place,
            "targetPlace": self.target_place,
        }
        self.sync_events.append(event_payload)
        return event_payload

    def ensure_spawn_position(self):
        preferred_location = self.curr_place or self.home
        preferred_location = normalize_location_name(preferred_location)

        entry_portal = LOCATION_ENTRY_PORTALS.get(preferred_location)
        if not entry_portal and self.home:
            entry_portal = LOCATION_ENTRY_PORTALS.get(self.home)

        if not entry_portal:
            print(f"⚠️ [傳送警告] 找不到 {self.name} 對應 {preferred_location} 的預設入口。")
            return None

        return self.teleport(entry_portal)
    def get_lightweight_response(self, action):
        return LIGHTWEIGHT_ACTION_RESPONSES.get(action)

    async def get_pronunciatio(self, action):
        lightweight = self.get_lightweight_response(action)
        if lightweight:
            return lightweight[1]

        if action in self._pronunciatio_cache:
            return self._pronunciatio_cache[action]

        try:
            result = await llm.run_gpt_prompt_pronunciatio(action)
        except Exception:
            result = ""

        self._pronunciatio_cache[action] = result
        return result

    async def set_new_action(self, new_action, destination):
        resolved_destination = self.resolve_destination(new_action, destination)
        print(f"[{self.name}] Action: {new_action}, Target: {destination} -> Resolved: {resolved_destination} (Home: {self.home})")
        if self.curr_action == new_action and self.target_place == resolved_destination:
            return
        self.interrupt_action()

        self.curr_action = new_action
        self.target_place = resolved_destination
        self.previous_place = self.curr_place
        self.curr_place = self.find_path(resolved_destination)

        lightweight = self.get_lightweight_response(new_action)
        if lightweight:
            thought, pronunciatio = lightweight
            self.current_thought = thought
            self.curr_action_pronunciatio = pronunciatio
            self._thinking_depth = 0
            self.is_thinking = False 
            return

        self._enter_thinking()
        try:
            try:
                self.current_thought = await llm.generate_action_thought(
                    self.persona_summary, self.curr_place, new_action
                )
            except Exception:
                self.current_thought = ""

            self.curr_action_pronunciatio = await self.get_pronunciatio(self.curr_action)
        finally:
            self._exit_thinking()

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
        
        nearby_injured_agents = [
            o
            for o in other_agents_list
            if o.id != self.id and o.health > 0 and o.is_injured and self.Is_nearby(o.get_position())
        ]

        if not self.is_injured and nearby_injured_agents:
            cooperation = self.quake_cooperation_inclination
            help_probability = 0.35
            if cooperation >= 0.9:
                help_probability = 0.97
            elif cooperation >= 0.75:
                help_probability = 0.85
            elif cooperation >= 0.6:
                help_probability = 0.7
            elif cooperation >= 0.45:
                help_probability = 0.55

            self_protection_actions = {"尋找遮蔽物", "躲到桌下", "尋找安全出口", "評估周圍環境"}
            help_action_key = "協助受傷的人"

            if reaction_action_key in self_protection_actions:
                help_action_key = "確認安全後協助他人"
                if (self.current_building and self.current_building.integrity > 40) or not self.current_building or intensity < 0.5:
                    help_probability = min(1.0, help_probability + 0.15)
                else:
                    help_probability *= 0.85

            if random.random() < help_probability:
                reaction_action_key, new_mental_state = help_action_key, "helping"

        
        self.mental_state = new_mental_state
        self.quake_has_taken_cover = False
        self.quake_evacuation_started = False
        self.target_place = self.curr_place
        self.curr_action = "尋找遮蔽物"
        self.disaster_experience_log.append("立即尋找掩護。")

    def perceive_and_help(self, other_agents):
        nearby_candidates = [
            o
            for o in other_agents
            if o.id != self.id and o.health > 0 and (o.is_injured or o.health < 90) and self.Is_nearby(o.get_position())
        ]
        if nearby_candidates:
            target = min(nearby_candidates, key=lambda x: x.health)
            original_hp = target.health
            heal = min(100 - original_hp, max(6, random.randint(8, 20)))
            if heal <= 0:
                heal = 3
            if heal <= 0:
                return None
            target.health = min(100, original_hp + heal)
            target.is_injured = target.health < 60
            message = f"協助 {target.name} (+{heal} HP -> {target.health})"
            return {
                "message": message,
                "受助者": target.name,
                "原始HP": original_hp,
                "治療量": heal,
                "新HP": target.health,
            }

        if self.quake_support_committed:
            return None

        potential_allies = [o for o in other_agents if o.id != self.id and o.health > 0]
        if not potential_allies:
            return None

        target = random.choice(potential_allies)
        original_hp = target.health
        heal = min(100 - original_hp, max(2, random.randint(4, 10)))
        if heal <= 0:
            return None
        target.health = min(100, original_hp + heal)
        target.is_injured = target.health < 60
        self.quake_support_committed = True
        message = f"協助 {target.name} 穩定狀態 (+{heal} HP -> {target.health})"
        return {
            "message": message,
            "受助者": target.name,
            "原始HP": original_hp,
            "治療量": heal,
            "新HP": target.health,
        }

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
            self._enter_thinking()
            try:
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
            finally:
                self._exit_thinking()
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
            self._enter_thinking()
            try:
                raw_tasks = await llm.run_gpt_prompt_generate_hourly_schedule(
                    self.persona_summary,
                    current_date.strftime('%Y-%m-%d'),
                    today_goal
                )

                if raw_tasks and isinstance(raw_tasks[0], list):
                    wake_time_str = await llm.run_gpt_prompt_wake_up_hour(
                        self.persona_summary,
                        current_date.strftime('%Y-%m-%d'),
                        raw_tasks
                    )
                    if not wake_time_str:
                        return False
                    self.wake_time = wake_time_str.replace(":", "-")

                    self.daily_schedule = update_agent_schedule(self.wake_time, raw_tasks)

                    try:
                        total_duration = sum(int(task[1]) for task in raw_tasks)
                        self.sleep_time = (
                            datetime.strptime(self.wake_time, '%H-%M') + timedelta(minutes=total_duration)
                        ).strftime('%H-%M')
                    except:
                        self.sleep_time = (
                            datetime.strptime(self.wake_time, '%H-%M') + timedelta(hours=16)
                        ).strftime('%H-%M')

                    return True
            finally:
                self._exit_thinking()

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

        if not self.quake_has_taken_cover:
            self.quake_has_taken_cover = True
            self.target_place = self.curr_place
            self.curr_action = "尋找遮蔽物"
            self.current_thought = "保持冷靜，先就近尋找掩護。"
            self.disaster_experience_log.append("就地掩護以避免受傷。")
            return f"{self.name} 正在尋找掩護 (HP:{self.health})。"

        if not self.quake_evacuation_started:
            self.quake_evacuation_started = True
            if self.target_place != "Subway":
                self.previous_place = self.curr_place
                self.target_place = "Subway"
                self.curr_place = self.find_path("Subway")
                if self.curr_place in PORTAL_CONNECTIONS and "地鐵" in self.curr_place:
                    self.teleport(self.curr_place)
            self.curr_action = "撤離到地鐵"
            self.current_thought = "往地鐵避難會更安全。"
            self.disaster_experience_log.append("開始撤離前往地鐵避難。")
            return f"{self.name} 正在撤離到地鐵避難 (HP:{self.health})。"

        if self.target_place == "Subway" and self.curr_place != "Subway":
            if self.curr_place in PORTAL_CONNECTIONS and "地鐵" in self.curr_place:
                self.teleport(self.curr_place)
                if self.curr_place == "Subway":
                    self.curr_action = "在地鐵避難"
                    self.current_thought = "已經抵達地鐵，繼續保持警戒。"
                    return f"{self.name} 已抵達地鐵避難 (HP:{self.health})。"
            self.curr_action = "撤離到地鐵"
            self.current_thought = "沿著路線前往地鐵避難。"
            return f"{self.name} 正在前往地鐵避難 (HP:{self.health})。"

        # 使用 LLM 決定下一步行動
        self._enter_thinking()
        try:
            new_action, new_thought = await llm.run_gpt_prompt_earthquake_step_action(
                self.persona_summary, self.health, self.mental_state, self.curr_place, intensity, self.disaster_experience_log[-5:]
            )
        finally:
            self._exit_thinking()
        self.curr_action = new_action
        self.current_thought = new_thought
        self.disaster_experience_log.append(f"在 {self.curr_place} 決定 {new_action}。內心想法: {new_thought}")


        # 執行幫助行為
        help_log = self.perceive_and_help(agents)
        if help_log:
            message = help_log.get("message")
            if message:
                self.disaster_experience_log.append(message)
            if disaster_logger:
                disaster_logger.記錄事件(self.name, "合作", current_time, help_log)

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
                message = help_log.get("message")
                if message:
                    self.disaster_experience_log.append(message)
                if disaster_logger:
                    disaster_logger.記錄事件(self.name, "合作", current_time, help_log)
            else:
                # 如果沒有人需要幫助，使用 LLM 決定恢復行動
                self._enter_thinking()
                try:
                    self.curr_action = await llm.run_gpt_prompt_get_recovery_action(
                        self.persona_summary, self.mental_state, self.curr_place
                    )
                finally:
                    self._exit_thinking()
        
        log_msg = f"{self.name} 正在 {self.curr_action} (HP:{self.health})。"
        self.disaster_experience_log.append(log_msg)
        return log_msg

def get_flash_reaction_prompt(agent_profile, sensory_input):
    """
    針對 DeepSeek/Ollama 優化的 System 1 極速反應 Prompt
    """
    
    # 1. 定義極簡的 System Prompt，強調 "INSTINCT" (本能) 與 "JSON"
    system_prompt = f"""
You are {agent_profile['name']}, a {agent_profile['mbti']} personality ({agent_profile['traits']}).
MODE: SURVIVAL INSTINCT (SYSTEM 1).
NO THINKING. NO EXPLANATION. REACT IMMEDIATELY.

You must output a SINGLE JSON object containing:
- "action": One of.
- "target": The closest logical object or vector.
- "anim": A specific Unity animation trigger string.

Example Response:
{{"action": "DUCK", "target": "Table", "anim": "Duck_Cover_Idle"}}
"""

    # 2. 使用 Few-Shot Examples 建立"條件反射"
    # 這裡直接教模型：看到什麼(Input) -> 做什麼(Output)
    few_shot_examples = """
Input: Event=EARTHQUAKE, Intensity=High, Nearby=Desk
Response: {"action": "DUCK", "target": "Desk", "anim": "Duck_Under_Table"}

Input: Event=FIRE, Intensity=Medium, Nearby=Exit_Door
Response: {"action": "RUN", "target": "Exit_Door", "anim": "Run_Panic"}

Input: Event=AFTERSHOCK, Intensity=Low, Nearby=Family_Member
Response: {"action": "INTERACT", "target": "Family_Member", "anim": "Hug_Fear"}
"""

    # 3. 組合當前情境
    user_prompt = f"""
{few_shot_examples}

Input: Event={sensory_input['event']}, Intensity={sensory_input['intensity']}, Nearby={sensory_input['nearby_objects']}
Response: 
"""
    
    return system_prompt, user_prompt
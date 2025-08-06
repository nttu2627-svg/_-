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
import time
import traceback

# --- Local LLM Integration ---
try:
    from tools.LLM.run_gpt_prompt import (
        run_gpt_prompt_generate_hourly_schedule, run_gpt_prompt_wake_up_hour,
        run_gpt_prompt_pronunciatio, double_agents_chat, go_map,
        modify_schedule, summarize,
        run_gpt_prompt_get_recovery_action,
        run_gpt_prompt_summarize_disaster,
    )
    print("本地 LLM 函數已成功從 tools.LLM.run_gpt_prompt 導入。")
    LLM_LOADED = True
except ImportError as e:
    print(f"從 tools.LLM.run_gpt_prompt 導入錯誤: {e}")
    print("請確保 'tools/LLM/run_gpt_prompt.py' 存在且包含所需函數。")
    LLM_LOADED = False
    def placeholder_llm(*args, func_name='unknown', **kwargs):
        print(f"警告: LLM 函數 '{func_name}' 未加載，使用占位符行為。")
        if func_name == 'generate_schedule': return [["占位符任務", 60]]
        if func_name == 'wake_up_hour': return "07-00"
        if func_name == 'pronunciatio': return "❓"
        if func_name == 'chat':
            a1_name = args[1] if len(args)>1 else 'Agent1'
            a2_name = args[2] if len(args)>2 else 'Agent2'
            eq_ctx = kwargs.get('eq_ctx')
            if eq_ctx and "地震" in eq_ctx:
                 return [[a1_name, "剛剛地震好可怕！"], [a2_name, "是啊，你沒事吧？"]]
            return [[a1_name, "占位符對話。"],[a2_name, "..."]]
        if func_name == 'go_map': return args[1] if len(args)>1 else "占位符地點"
        if func_name == 'modify_schedule': return args[0] if args else []
        if func_name == 'summarize': return "占位符總結。"
        if func_name == 'get_recovery_action': return "原地休息"
        if func_name == 'summarize_disaster': return "經歷了一場地震，現在安全。"
        return None
    run_gpt_prompt_generate_hourly_schedule = lambda p, n: placeholder_llm(p, n, func_name='generate_schedule')
    run_gpt_prompt_wake_up_hour = lambda p, n, h: placeholder_llm(p, n, h, func_name='wake_up_hour')
    run_gpt_prompt_pronunciatio = lambda a: placeholder_llm(a, func_name='pronunciatio')
    double_agents_chat = lambda m, a1, a2, c, i, t, nt, eq_ctx=None: placeholder_llm(m, a1, a2, c, i, t, nt, eq_ctx=eq_ctx, func_name='chat')
    go_map = lambda n, h, cp, cg, ct: placeholder_llm(n, h, cp, cg, ct, func_name='go_map')
    modify_schedule = lambda o, nt, m, wt, r: placeholder_llm(o, nt, m, wt, r, func_name='modify_schedule')
    summarize = lambda m, nt, n: placeholder_llm(m, nt, n, func_name='summarize')
    run_gpt_prompt_get_recovery_action = lambda p, ms, cp: placeholder_llm(p, ms, cp, func_name='get_recovery_action')
    run_gpt_prompt_summarize_disaster = lambda n_name, mbti_type, health_val, exp_log: placeholder_llm(n_name, mbti_type, health_val, exp_log, func_name='summarize_disaster')
    print("正在使用占位符 LLM 函數。")

# --- UTF-8 Configuration ---
try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except AttributeError:
    print("警告: sys.stdout.reconfigure 不可用。請確保環境為 UTF-8。")

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
    'ESTP': {'desc': '外向實際，適應力強，危機中會立即行動也可能協助他人。', 'cooperation': 0.6},
    'ESFP': {'desc': '活潑友善，喜歡帶動團隊，遇事積極協助他人。', 'cooperation': 0.7},
    'ENFP': {'desc': '熱情創意且善社交，傾向群體行動與合作。', 'cooperation': 0.8},
    'ENTP': {'desc': '機敏健談，喜歡尋找新奇解決方案，願意與人合作解決問題。', 'cooperation': 0.7},
    'ESTJ': {'desc': '務實果斷，擅長組織管理，他們會主導並要求合作。', 'cooperation': 0.8},
    'ESFJ': {'desc': '熱心合群，重視團隊和諧，樂於為群體付出合作。', 'cooperation': 0.9},
    'ENFJ': {'desc': '有同情心又善於領導，天然會帶領並協助他人。', 'cooperation': 0.9},
    'ENTJ': {'desc': '自信領導，邏輯效率並重，會有效組織協調團體行動。', 'cooperation': 0.8}
}
DEFAULT_MBTI_TYPES = list(MBTI_PROFILES.keys())

# --- Town Map & Config ---
MAP =    [['醫院', '咖啡店', '#', '蜜雪冰城', '學校', '#', '#', '小芳家', '#', '#', '火鍋店', '#', '#'],
          ['#', '#', '綠道', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '小明家', '#', '小王家', '#', '#', '#', '#'],
          ['#', '#', '肯德基', '鄉村基', '#', '#', '#', '#', '#', '#', '#', '健身房', '#'],
          ['電影院', '#', '#', '#', '#', '商場', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#', '#'],
          ['#', '#', '#', '#', '#', '#', '#', '海邊', '#', '#', '#', '#', '#']]

can_go_place = sorted(list(set(item for row in MAP for item in row if item != '#')))
PREDEFINED_HOMES = ['小明家', '小芳家', '小王家', '醫院宿舍', '學校宿舍', '咖啡店閣樓', '商場公寓', '海邊小屋',
                   '綠道帳篷', '火鍋店樓上', '肯德基員工間', '健身房休息室', '電影院放映室', '鄉村基單間',
                   '蜜雪冰城倉庫', '神秘空屋']
for home in PREDEFINED_HOMES:
    if home not in can_go_place: can_go_place.append(home)
can_go_place = sorted(list(set(can_go_place)))

# --- Agent Profile File Handling & Localization ---
BASE_DIR = './agents/'
TARGET_FILENAME = "1.txt"

SIMP_TO_TRAD_MAP = {
    '“': '「', '”': '」', '‘': '『', '’': '』','：': '：', '，': '，', '。': '。', '！': '！', '？': '？',
    '你': '你', '我': '我', '他': '他', '她': '她', '它': '它', '们': '們','的': '的', '地': '地', '得': '得',
    '个': '個', '么': '麼', '样': '樣', '什': '甚', '么': '麼','着': '著', '里': '裡', '后': '後', '面': '面',
    '说': '說', '话': '話', '时': '時', '间': '間','为': '為', '点': '點','会': '會', '过': '過',
    '发': '發', '现': '現', '实': '實','体': '體', '验': '驗', '状': '狀', '态': '態', '处': '處',
    '记忆': '記憶', '总结': '總結', '计划': '計劃', '行动': '行動','角色': '角色', '背景': '背景', '位置': '位置', '地点': '地點',
    '健康': '健康', '精神': '精神', '合作': '合作','地震': '地震', '灾害': '災害', '灾难': '災難', '恢复': '恢復', '期间': '期間',
    '建筑': '建築', '损伤': '損傷', '安全': '安全','现在': '現在', '今天': '今天', '明天': '明天', '早上': '早上', '晚上': '晚上',
    '感觉': '感覺', '有点': '有點', '可能': '可能', '应该': '應該','检查': '檢查', '休息': '休息', '帮助': '幫助', '寻找': '尋找',
    '进行': '進行', '描述': '描述', '项目': '項目', '内容': '內容','信息': '資訊', '格式': '格式', '主要': '主要', '反应': '反應',
    '输出': '輸出', '输入': '輸入', '逻辑': '邏輯', '处理': '處理','相关': '相關', '具体': '具體', '其他': '其他', '不同': '不同',
    '影响': '影響', '情况': '情況', '环境': '環境', '管理': '管理','功能': '功能', '需求': '需求', '问题': '問題', '解决': '解決',
    '部分': '部分', '所有': '所有', '这个': '這個', '那个': '那個','模拟': '模擬', '设置': '設置', '控制': '控制', '参数': '參數',
    '代理人': '代理人', '机器人': '機器人','代码': '程式碼', '文件': '檔案', '目录': '目錄', '路径': '路徑',
    '执行': '執行', '启动': '啟動', '测试': '測試', '调试': '調試','接口': '介面', '用户': '使用者', '界面': '介面',
    '对话': '對話', '聊天': '聊天', '生成': '生成', '更新': '更新','模型': '模型', '本地': '本地', '服务': '服務',
    '确保': '確保', '注意': '注意', '警告': '警告', '错误': '錯誤','修复': '修復', '修改': '修改', '调整': '調整', '优化': '優化',
    '算法': '演算法', '数据': '數據', '结构': '結構','配置': '配置', '选项': '選項', '默认': '預設', '自定义': '自訂',
    'Placeholder': '占位符', 'Task': '任務', 'Location': '地點','Event': '事件', 'Action': '行動', 'State': '狀態', 'Update': '更新',
    'Report': '報告', 'Summary': '總結', 'Chat': '聊天', 'Debug': '調試','Info': '資訊', 'Warn': '警告', 'Error': '錯誤',
    'Critical': '嚴重錯誤','MBTI': 'MBTI', 'Agent': '代理人', 'Building': '建築','JSON': 'JSON', 'Text': '文本', 'String': '字串',
    'List': '列表', 'Dict': '字典','Import': '導入', 'Module': '模組', 'Function': '函數', 'Class': '類別',
    'Got': '收到', 'Unexpected': '意外的', 'Keyword': '關鍵字', 'Argument': '參數','Attribute': '屬性', 'Variable': '變數',
    'Undefined': '未定義','Path': '路徑', 'File': '檔案', 'Directory': '目錄', 'Not': '非', 'Found': '找到',
    'Value': '值', 'Type': '類型', 'Format': '格式', 'Invalid': '無效', 'Initializing': '初始化中',
}
def to_traditional(text):
    if not isinstance(text, str): return text
    for simp, trad in SIMP_TO_TRAD_MAP.items(): text = text.replace(simp, trad)
    return text

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
                    persona_desc = to_traditional(MBTI_PROFILES.get(mbti_type, {}).get('desc', '未知個性。'))
                    f.write(f"Personality Notes: {persona_desc}\n")
                    f.write(to_traditional("Occupation: 居民\n"))
                    f.write(to_traditional("Age: 30\n"))
                    f.write(to_traditional("Goals: 過著充實的生活。\n"))
                    f.write(to_traditional("Daily Routine Notes:\n"))
                    f.write(to_traditional("喜歡例行公事和自發性的混合。\n"))
            except Exception as e: print(f"創建預設設定檔 {mbti_type} 錯誤: {e}")
        target_files[mbti_type] = file_path
    return target_files

def get_target_files_for_agents(mbti_list):
    target_files = {}
    for mbti_type in mbti_list:
        folder = os.path.join(BASE_DIR, mbti_type)
        file_path = os.path.join(folder, TARGET_FILENAME)
        if os.path.exists(folder): target_files[mbti_type] = file_path
    return target_files

def read_file(file_path):
    try:
        with open(file_path, "r", encoding="utf-8") as file: return file.read()
    except FileNotFoundError: return to_traditional(f"錯誤：設定檔 {file_path} 未找到。\n請確保檔案存在或運行一次模擬。")
    except Exception as e: return to_traditional(f"讀取檔案 {file_path} 時出錯: {e}")

def save_file(file_path, new_content):
    try:
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, "w", encoding="utf-8") as file: file.write(new_content)
        return to_traditional(f"檔案 {os.path.basename(file_path)} 已成功保存在 {os.path.dirname(file_path)}！")
    except Exception as e: return to_traditional(f"保存檔案 {file_path} 時出錯: {e}")

# --- Building Class ---
class Building:
    def __init__(self, bld_id, position, integrity=100.0):
        self.id = bld_id
        self.position = position
        self.integrity = integrity

# --- TownAgent Class ---
class TownAgent:
    def __init__(self, agent_id_mbti, initial_home_name, map_layout):
        self.id = agent_id_mbti; self.name = agent_id_mbti; self.MBTI = agent_id_mbti
        self.MAP = map_layout; self.home = initial_home_name
        mbti_info = MBTI_PROFILES.get(self.MBTI, {'desc': '未知個性', 'cooperation': 0.5})
        self.personality_desc = to_traditional(mbti_info['desc'])
        self.cooperation_inclination = mbti_info['cooperation']
        self.schedule = []; self.schedule_time = []
        self.curr_place = initial_home_name; self.position = self._find_initial_pos(initial_home_name)
        self.last_action = to_traditional("初始化中"); self.curr_action = to_traditional("初始化中")
        self.curr_action_pronunciatio = "⏳"; self.memory = ""; self.talk_arr = ""; self.wake = "07-00"
        self.health = 100; self.is_injured = False; self.mental_state = "calm"
        self.current_building = None; self.interrupted_action = None; self.disaster_experience_log = []
        self.profile_path = os.path.join(BASE_DIR, self.id, TARGET_FILENAME); self._load_profile()

    def _load_profile(self):
        try:
            self.profile_content = read_file(self.profile_path)
            self.profile_lines = self.profile_content.splitlines()
            persona_lines = [line for line in self.profile_lines if "Personality Notes:" in line or "Daily Routine Notes:" in line]
            if persona_lines: self.persona_summary = " ".join([line.split(":", 1)[1].strip() for line in persona_lines if ':' in line])
            elif len(self.profile_lines) > 2 and self.profile_lines[2].strip(): self.persona_summary = self.profile_lines[2].strip()
            else: self.persona_summary = f"MBTI: {self.MBTI}. {to_traditional('描述')}: {self.personality_desc}"
        except Exception as e:
            print(f"{to_traditional('為')} {self.id} {to_traditional('加載設定檔錯誤')}: {e}. {to_traditional('使用預設人格。')}")
            self.persona_summary = f"MBTI: {self.MBTI}. {to_traditional('描述')}: {self.personality_desc}"
        self.persona_summary = to_traditional(self.persona_summary)

    def _find_initial_pos(self, place_name):
        for r, row in enumerate(self.MAP):
            for c, cell in enumerate(row):
                if cell == place_name: return (r, c)
        for r, row in enumerate(self.MAP):
             for c, cell in enumerate(row):
                  if cell == '#': return (r, c)
        return (0, 0)

    def update_current_building(self, buildings_dict):
        r, c = self.position
        self.current_building = None
        if 0 <= r < len(self.MAP) and 0 <= c < len(self.MAP[0]):
            map_cell = self.MAP[r][c]
            if map_cell != '#': self.current_building = buildings_dict.get(map_cell)

    def get_position(self): return self.position

    def goto_scene(self, scene_name, buildings_dict):
        target_pos = None
        for r, row in enumerate(self.MAP):
            for c, cell in enumerate(row):
                if cell == scene_name: target_pos = (r,c); break
            if target_pos: break
        if target_pos:
            self.position = target_pos; self.curr_place = scene_name
            self.update_current_building(buildings_dict); return True
        return False

    def Is_nearby(self, other_agent_position):
        if not self.position or not other_agent_position: return False
        return abs(self.position[0] - other_agent_position[0]) <= 1 and abs(self.position[1] - other_agent_position[1]) <= 1

    def interrupt_action(self):
        if self.curr_action not in [to_traditional("初始化中"), to_traditional("睡覺"), "Unconscious"]:
            self.interrupted_action = self.curr_action
        else: self.interrupted_action = None

    def react_to_earthquake(self, intensity, buildings_dict, other_agents_list):
        if self.mental_state == "unconscious": return
        original_health = self.health; damage = 0
        self.update_current_building(buildings_dict)
        building_obj = self.current_building
        building_integrity = building_obj.integrity if building_obj else 100
        location_context = to_traditional("戶外")
        if building_obj: location_context = to_traditional(f"在 {to_traditional(building_obj.id)} 內")

        if building_integrity < 50: damage = random.randint(int(intensity * 25), int(intensity * 55))
        elif building_obj and random.random() < intensity * 0.5: damage = random.randint(1, int(intensity * 30))
        elif not building_obj and random.random() < intensity * 0.25: damage = random.randint(1, int(intensity * 15))

        self.health = max(0, self.health - damage)
        damage_log = to_traditional(f"遭受 {damage} 點傷害") if damage > 0 else to_traditional("未受傷")
        health_change = f"HP: {original_health} -> {self.health}" if damage > 0 else ""
        self.disaster_experience_log.append(f"{to_traditional('地震開始')}：{location_context}，{damage_log} {health_change}")

        if self.health <= 0:
            self.is_injured = True; self.mental_state = "unconscious"; self.curr_action = to_traditional("Unconscious"); self.curr_action_pronunciatio = "😵"
            self.disaster_experience_log.append(to_traditional("因重傷失去意識。"))
            return
        elif self.health < 50:
            if not self.is_injured: self.disaster_experience_log.append(to_traditional("受到傷害。"))
            self.is_injured = True
        else: self.is_injured = False

        reaction_action_key = "alert"; new_mental_state = "alert" # Use English keys for logic
        if self.is_injured: reaction_action_key, new_mental_state = "injured_flee", "injured"
        elif intensity >= 0.65:
            if 'E' in self.MBTI and 'TJ' in self.MBTI: reaction_action_key, new_mental_state = "lead", "focused"
            elif 'E' in self.MBTI and 'F' in self.MBTI: reaction_action_key, new_mental_state = "panic", "panicked"
            elif 'I' in self.MBTI and 'F' in self.MBTI: reaction_action_key, new_mental_state = "freeze", "frozen"
            else: reaction_action_key, new_mental_state = "flee", "alert"
        else:
            if 'J' in self.MBTI: reaction_action_key, new_mental_state = "calm", "alert"
            else: reaction_action_key, new_mental_state = "flee", "alert"

        if not self.is_injured and self.cooperation_inclination > 0.6 and reaction_action_key not in ["panic", "freeze"]:
            can_help_nearby = any(o.id != self.id and o.health > 0 and o.is_injured and o.mental_state != "unconscious" and self.Is_nearby(o.get_position()) for o in other_agents_list)
            if can_help_nearby: reaction_action_key, new_mental_state = "assist_others", "helping"

        self.mental_state = new_mental_state
        self.curr_action = to_traditional(reaction_action_key) # Store as Traditional Chinese
        emoji_map = {"lead": "🧑‍🚒","panic": "😱","flee": "💨","freeze": "🥶","calm": "🧘","assist_others": "🤝","injured_flee": "🤕","unconscious": "😵","alert":"⚠️"}
        self.curr_action_pronunciatio = emoji_map.get(reaction_action_key, "❓")
        self.disaster_experience_log.append(to_traditional(f"初步反應：{self.curr_action} ({self.curr_action_pronunciatio})，精神狀態: {self.mental_state}"))

    def perform_earthquake_step_action(self, other_agents, buildings_dict, quake_intensity):
        if self.mental_state == "unconscious": return None
        action_log_parts = []
        log_prefix = f"  {self.name} ({self.MBTI}):"

        if random.random() < 0.15:
             minor_dmg = random.randint(0, int(quake_intensity * 2))
             self.health = max(0, self.health - minor_dmg)
             if minor_dmg > 0:
                 action_log_parts.append(to_traditional(f"受到 {minor_dmg} 點輕微搖晃傷害 ({self.health} HP)"))
                 if self.health <= 0: self.mental_state = "unconscious"; self.curr_action=to_traditional("Unconscious"); self.curr_action_pronunciatio="😵"; action_log_parts.append(to_traditional("失去意識。"))
                 elif self.health < 50 and not self.is_injured: self.is_injured = True; action_log_parts.append(to_traditional("感覺受傷加劇。"))

        action_verb = self.curr_action # Already Traditional
        emoji = self.curr_action_pronunciatio

        if self.curr_action == to_traditional("assist_others"):
            help_event = self.perceive_and_help(other_agents)
            if help_event: action_log_parts.append(help_event)
            else: action_log_parts.append(f"{emoji} {to_traditional('環顧四周尋找需要幫助的人。')}")
        elif self.curr_action == to_traditional("flee") or self.curr_action == to_traditional("injured_flee"): action_log_parts.append(f"{emoji} {to_traditional('試圖移動到更安全的地方。')}")
        elif self.curr_action == to_traditional("lead"): action_log_parts.append(f"{emoji} {to_traditional('大聲呼喊，引導他人注意安全。')}")
        elif self.curr_action == to_traditional("panic"): action_log_parts.append(f"{emoji} {to_traditional('發出驚叫，顯得非常慌亂。')}")
        elif self.curr_action == to_traditional("freeze"): action_log_parts.append(f"{emoji} {to_traditional('身體僵硬，無法動彈。')}")
        elif self.curr_action == to_traditional("calm"): action_log_parts.append(f"{emoji} {to_traditional('躲在遮蔽物下，冷靜觀察。')}")
        elif self.curr_action == to_traditional("alert"): action_log_parts.append(f"{emoji} {to_traditional('保持警惕，尋找出口或掩體。')}")
        elif self.curr_action == to_traditional("Unconscious"): return f"{log_prefix} {emoji} {to_traditional('失去意識，無任何行動。')}"

        subjective_thought = ""
        if self.mental_state == "panicked": subjective_thought = to_traditional("（天啊！怎麼辦！快停下來！）")
        elif self.mental_state == "frozen": subjective_thought = to_traditional("（動不了...我動不了...）")
        elif self.mental_state == "helping": subjective_thought = to_traditional("（一定要幫助他們！附近還有人嗎？）")
        elif self.mental_state == "focused": subjective_thought = to_traditional("（保持冷靜，指揮大家疏散！這裡不安全！）")
        elif self.is_injured: subjective_thought = to_traditional("（好痛，得先確保自己安全...）")
        elif self.mental_state == "calm": subjective_thought = to_traditional("（地震了，先找個安全的地方躲避。）")


        full_log_msg = f"{log_prefix} {action_verb} {emoji} " + " ".join(action_log_parts) + f" {subjective_thought}"
        self.disaster_experience_log.append(f"{to_traditional('地震中')} ({action_verb}): {' '.join(action_log_parts)}")
        return full_log_msg

    def perceive_and_help(self, other_agents):
        if self.mental_state != "helping": return None
        nearby_injured = [o for o in other_agents if o.id != self.id and o.health > 0 and o.mental_state != "unconscious" and o.is_injured and self.Is_nearby(o.get_position())]
        if not nearby_injured: return None
        target_agent = min(nearby_injured, key=lambda x: x.health)
        heal_amount = min(100 - target_agent.health, random.randint(10, 20))
        target_agent.health += heal_amount
        log_event = to_traditional(f"協助代理人 {target_agent.id} (+{heal_amount} HP -> {target_agent.health})")
        self.disaster_experience_log.append(to_traditional(f"協助：幫助了 {target_agent.id}"))
        if target_agent.health >= 50:
            target_agent.is_injured = False
            if target_agent.mental_state in ["panicked", "injured"]:
                 target_agent.mental_state = "alert"; target_agent.curr_action = to_traditional("recovering"); target_agent.curr_action_pronunciatio = "😌"
                 log_event += to_traditional(f" (代理人 {target_agent.id} 狀態穩定)")
                 self.disaster_experience_log.append(to_traditional(f"{target_agent.id} 狀態穩定。"))
        return log_event

    def perform_recovery_step_action(self, other_agents, buildings_dict):
        if self.mental_state == "unconscious": return f"  {self.name} {to_traditional('依然昏迷。')}"
        log_prefix = f"  {self.name} ({self.MBTI}):"
        action_desc_parts = []

        if self.health < 100 and random.random() < 0.6:
            heal_amount = random.randint(2, 7)
            self.health = min(100, self.health + heal_amount)
            if heal_amount > 0: action_desc_parts.append(to_traditional(f"生命值恢復 {heal_amount} 點 ({self.health} HP)。"))
            if self.health >= 50 and self.is_injured: self.is_injured = False; action_desc_parts.append(to_traditional("不再感到嚴重受傷。"))
            elif self.health >= 80 and self.is_injured: self.is_injured = False; action_desc_parts.append(to_traditional("傷勢已大致恢復。"))

        if self.mental_state not in ["calm", "unconscious"] and random.random() < 0.5:
            original_mental_state = self.mental_state
            if self.is_injured and self.health < 70 : self.mental_state = "injured"
            elif self.mental_state in ["panicked", "frozen", "helping", "focused", "alert"]: self.mental_state = "calm"
            if self.mental_state != original_mental_state: action_desc_parts.append(to_traditional(f"精神狀態從 '{original_mental_state}' 逐漸平復為 '{self.mental_state}'。"))

        last_recovery_action = getattr(self, "last_recovery_action", None)
        recovery_action_llm = run_gpt_prompt_get_recovery_action(self.persona_summary, self.mental_state, self.curr_place)

        if recovery_action_llm == last_recovery_action and recovery_action_llm == to_traditional("原地休息") and self.health > 75 and not self.is_injured:
            possible_actions = [to_traditional("檢查周圍環境安全"), to_traditional("尋找其他倖存者並詢問狀況"), to_traditional("整理可用物資"), to_traditional("嘗試用手機聯繫外界")]
            if self.current_building and self.current_building.integrity < 70 : possible_actions.append(to_traditional("仔細評估所在建築的結構損傷"))
            if self.cooperation_inclination > 0.5 and self.health > 60 : possible_actions.append(to_traditional("主動詢問附近的人是否需要幫助"))
            self.curr_action = random.choice(possible_actions) if possible_actions else recovery_action_llm # Fallback if no other actions
            action_desc_parts.append(to_traditional(f"改變行動，決定 {self.curr_action}。"))
        else:
            self.curr_action = recovery_action_llm
        self.last_recovery_action = self.curr_action
        self.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(self.curr_action)
        action_desc_parts.append(to_traditional(f"正在 {self.curr_action} ({self.curr_action_pronunciatio})。"))

        if to_traditional("檢查") in self.curr_action and self.current_building: action_desc_parts.append(to_traditional(f"仔細檢查建築 {to_traditional(self.current_building.id)} 的損壞情況。"))
        elif to_traditional("尋找") in self.curr_action and (to_traditional("幫助") in self.curr_action or to_traditional("他人") in self.curr_action or to_traditional("倖存者") in self.curr_action) :
             help_log = self.perceive_and_help(other_agents)
             if help_log: action_desc_parts.append(f"{help_log}。")
             else: action_desc_parts.append(to_traditional("在附近搜尋，但未立即發現需要緊急幫助的人。"))
        elif (to_traditional("避難") in self.curr_action or to_traditional("回家") in self.curr_action) and self.curr_place != self.home:
             if self.goto_scene(self.home, buildings_dict): action_desc_parts.append(to_traditional(f"決定返回家中 ({to_traditional(self.home)}) 察看情況。"))
             else: action_desc_parts.append(to_traditional(f"嘗試返回家中 ({to_traditional(self.home)}) 失敗，可能道路受阻。"))

        full_action_desc = " ".join(filter(None, action_desc_parts))
        self.disaster_experience_log.append(to_traditional(f"災後恢復中 ({self.curr_action})：{full_action_desc}"))
        return f"{log_prefix} {full_action_desc}" if full_action_desc else f"{log_prefix} {to_traditional('正在休整與觀察。')}"

# --- Time & Schedule Functions ---
def get_weekday_from_dt(nowtime_dt_obj):
    try:
        weekdays = [to_traditional("星期一"), to_traditional("星期二"), to_traditional("星期三"), to_traditional("星期四"), to_traditional("星期五"), to_traditional("星期六"), to_traditional("星期天")]
        return weekdays[nowtime_dt_obj.weekday()]
    except ValueError: return to_traditional("未知日期")

def format_datetime_obj(nowtime_dt_obj):
    try:
        return to_traditional(nowtime_dt_obj.strftime('%Y年%m月%d日 %H:%M'))
    except ValueError: return str(nowtime_dt_obj)

def compare_times_hm(time_str1_hm, time_str2_hm):
    try:
        time1 = datetime.strptime(time_str1_hm, '%H-%M'); time2 = datetime.strptime(time_str2_hm, '%H-%M')
        return time1 < time2
    except ValueError: return False

def update_agent_schedule(wake_up_time_str, schedule_tasks):
    try:
        wake_up_time_str = wake_up_time_str.replace(":", "-")
        if "-" not in wake_up_time_str:
            if len(wake_up_time_str) == 3: wake_up_time_str = "0" + wake_up_time_str[0] + "-" + wake_up_time_str[1:]
            elif len(wake_up_time_str) == 4: wake_up_time_str = wake_up_time_str[:2] + "-" + wake_up_time_str[2:]
            else: raise ValueError("Invalid time format")
        wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    except ValueError:
        wake_up_time_str = "07-00"; wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    current_time = wake_up_time; updated_schedule = []
    if not isinstance(schedule_tasks, list): return []
    updated_schedule.append([to_traditional('醒來'), wake_up_time.strftime('%H-%M')])
    for item in schedule_tasks:
        if not isinstance(item, (list, tuple)) or len(item) < 2: continue
        activity, duration_val = item[0], item[1]
        try:
            duration_minutes = int(duration_val)
            if duration_minutes <= 0: continue
        except (ValueError, TypeError): continue
        updated_schedule.append([to_traditional(activity), current_time.strftime('%H-%M')])
        current_time += timedelta(minutes=duration_minutes)
    return updated_schedule

def find_agent_current_activity(current_time_hm_str, schedule_with_start_times):
    try: current_time = datetime.strptime(current_time_hm_str, '%H-%M')
    except ValueError: return [to_traditional('時間錯誤'), current_time_hm_str]
    if not isinstance(schedule_with_start_times, list) or not schedule_with_start_times: return [to_traditional('睡覺'), "00-00"]
    current_activity_found = [to_traditional('睡覺'), "00-00"]
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
def find_chat_groups(agents_list):
    active_agents = [a for a in agents_list if a.health > 0 and a.mental_state not in ["unconscious", "panicked", "frozen"]]
    if len(active_agents) < 2: return None
    location_groups = {}
    for agent in active_agents:
        place = agent.curr_place
        if place not in location_groups: location_groups[place] = []
        location_groups[place].append(agent)
    potential_chat_groups = [group for group in location_groups.values() if len(group) >= 2]
    if not potential_chat_groups: return None
    if random.random() < 0.75: return random.choice(potential_chat_groups)
    return None

# --- Helper: Damage Report ---
def generate_disaster_report(agents, buildings):
    report = [to_traditional("--- 灾后损伤报告 ---")]
    report.append(to_traditional("建筑状况:"))
    damaged_buildings_count = 0
    for name, bldg in buildings.items():
        if bldg.integrity < 100:
            status_key = "完好" if bldg.integrity > 80 else "轻微受损" if bldg.integrity > 50 else "严重受损" if bldg.integrity > 0 else "完全摧毁"
            report.append(f"  - {to_traditional(name)}: {to_traditional('完整度')} {bldg.integrity:.1f}% ({to_traditional(status_key)})")
            damaged_buildings_count +=1
    if damaged_buildings_count == 0: report.append(to_traditional("  所有建筑在此次事件中均未受损。"))
    report.append(f"\n{to_traditional('人员状况:')}")
    for agent in agents:
        status_key = "安全" if agent.health > 70 else "轻伤" if agent.health > 0 else "重伤/昏迷"
        loc = f"@ {to_traditional(agent.curr_place)}"
        report.append(f"  - {agent.name} ({agent.MBTI}): {to_traditional('生命值')} {agent.health}/100 ({to_traditional(status_key)}) {loc}")
    report.append("----------------------")
    return "\n".join(report)

# --- Simulation Core Logic ---
def simulate_town_life(total_sim_duration_minutes,
                         min_per_step_normal_ui,
                         start_year, start_month, start_day, start_hour, start_minute,
                         selected_mbti_list,
                         eq_enabled, eq_events_json_str,
                         eq_step_minutes_ui):

    _log_buffer_sim = []
    MAX_LOG_LINES_SIM = 3500

    def add_log_sim(message, level="INFO"):
        nonlocal _log_buffer_sim
        timestamp = datetime.now().strftime("%H:%M:%S")
        indented = level in ["SUB", "UPDATE", "ACTION", "STATE", "REPORT", "EVENT", "CHAT_CONTENT", "BUILDING_DMG"]
        
        if level == "CHAT_CONTENT": formatted_message = f"{timestamp} [CHAT]     🎤 {message}"
        elif level == "CHAT_EVENT": formatted_message = f"{timestamp} [EVENT] {message}"
        else: formatted_message = f"{timestamp} [{level}]{'  ' if indented else ' '} {to_traditional(message)}"

        if len(_log_buffer_sim) >= MAX_LOG_LINES_SIM: _log_buffer_sim.pop(0)
        _log_buffer_sim.append(formatted_message)
        return "\n".join(_log_buffer_sim)

    yield add_log_sim("--- 模擬啟動中 ---", "INFO")

    if not LLM_LOADED: yield add_log_sim("警告: LLM 未加載，模擬行為將非常有限。", "WARN")
    if not selected_mbti_list: yield add_log_sim("錯誤：沒有選擇任何代理人！", "ERROR"); return
    yield add_log_sim(f"選擇的代理人: {', '.join(selected_mbti_list)} ({len(selected_mbti_list)} 名)", "INFO")

    selected_mbtis = selected_mbti_list[:16]
    agents = []
    used_homes = set()
    available_homes = PREDEFINED_HOMES[:]
    for i, mbti in enumerate(selected_mbtis):
        assigned_home = available_homes.pop(random.randrange(len(available_homes))) if available_homes else f"{mbti}_家_{i+1}"
        used_homes.add(assigned_home)
        if assigned_home not in can_go_place: can_go_place.append(assigned_home)
        try:
            agent = TownAgent(agent_id_mbti=mbti, initial_home_name=assigned_home, map_layout=MAP)
            agents.append(agent)
        except Exception as e: yield add_log_sim(f"初始化代理人 {mbti} 失敗: {e}\n{traceback.format_exc()}", "ERROR")
    if not agents: yield add_log_sim("錯誤：未能成功初始化任何代理人。", "ERROR"); return
    yield add_log_sim(f"成功初始化 {len(agents)} 個代理人。", "INFO")

    buildings = {}
    for r, row in enumerate(MAP):
        for c, cell in enumerate(row):
            if cell != '#' and cell not in buildings: buildings[cell] = Building(cell, (r, c))
    for home_name in used_homes:
        if home_name not in buildings:
             pos = agents[0]._find_initial_pos(home_name) if agents else (0,0)
             buildings[home_name] = Building(home_name, pos)
    for agent in agents: agent.update_current_building(buildings)

    try:
        current_sim_time_dt = datetime(int(start_year), int(start_month), int(start_day), int(start_hour), int(start_minute))
        sim_end_time_dt = current_sim_time_dt + timedelta(minutes=int(total_sim_duration_minutes))
        yield add_log_sim(f"模擬開始時間: {format_datetime_obj(current_sim_time_dt)} ({get_weekday_from_dt(current_sim_time_dt)})", "INFO")
        yield add_log_sim(f"模擬結束時間: {format_datetime_obj(sim_end_time_dt)}", "INFO")
    except Exception as e: yield add_log_sim(f"初始時間設定錯誤: {e}. 請檢查輸入。", "ERROR"); return

    scheduled_eq_events = []
    if eq_enabled:
        try:
            raw_eq_events = json.loads(eq_events_json_str) if eq_events_json_str else []
            for eq_data in raw_eq_events:
                eq_time_dt = datetime.strptime(eq_data['time'], "%Y-%m-%d-%H-%M")
                scheduled_eq_events.append({'time_dt': eq_time_dt, 'duration': int(eq_data['duration']), 'intensity': float(eq_data.get('intensity', random.uniform(0.6,0.9)))})
            scheduled_eq_events.sort(key=lambda x: x['time_dt'])
            yield add_log_sim(f"已排程 {len(scheduled_eq_events)} 場地震事件。", "INFO")
            for eq_idx, eq in enumerate(scheduled_eq_events): yield add_log_sim(f"  - 地震 {eq_idx+1} 計劃於 {format_datetime_obj(eq['time_dt'])}, 持續 {eq['duration']} 分鐘, 強度約 {eq['intensity']:.1f}", "INFO")
        except Exception as e: yield add_log_sim(f"加載地震事件時發生錯誤: {e}。地震模擬可能受影響。", "ERROR")

    current_phase = "Normal"
    next_earthquake_event_idx = 0
    current_quake_details = None
    recovery_end_time_dt = None
    post_quake_discussion_end_time_dt = None
    
    EARTHQUAKE_STEP_MINUTES = int(eq_step_minutes_ui)
    RECOVERY_STEP_MINUTES = 10
    min_per_step_normal = int(min_per_step_normal_ui)

    post_quake_chat_context = None
    normal_phase_step_counter = 0
    earthquake_phase_step_counter = 0
    recovery_phase_step_counter = 0
    post_quake_discussion_phase_step_counter = 0

    while current_sim_time_dt < sim_end_time_dt:
        # *** Moved current_date_weekday_str here ***
        current_date_weekday_str = f"{current_sim_time_dt.strftime('%Y-%m-%d')}-{get_weekday_from_dt(current_sim_time_dt)}"
        current_time_hm_str = current_sim_time_dt.strftime('%H-%M')
        log_header_time_str = format_datetime_obj(current_sim_time_dt)
        status_indicator = ""
        current_step_duration_minutes = min_per_step_normal

        phase_step_display = ""
        if current_phase == "Earthquake":
            status_indicator = to_traditional("[ 地震中! ]")
            current_step_duration_minutes = EARTHQUAKE_STEP_MINUTES
            phase_step_display = f" ({to_traditional('地震第')} {earthquake_phase_step_counter + 1} {to_traditional('分鐘')})"
        elif current_phase == "Recovery":
            status_indicator = to_traditional("[ 災後恢復中 ]")
            current_step_duration_minutes = RECOVERY_STEP_MINUTES
            phase_step_display = f" ({to_traditional('恢復第')} {recovery_phase_step_counter + 1} {to_traditional('階段')})"
        elif current_phase == "PostQuakeDiscussion":
            status_indicator = to_traditional("[ 災後討論期 ]")
            phase_step_display = f" ({to_traditional('討論期第')} {post_quake_discussion_phase_step_counter + 1} {to_traditional('步')})"
        else: # Normal
            phase_step_display = f" ({to_traditional('常規第')} {normal_phase_step_counter + 1} {to_traditional('步')})"
        
        yield add_log_sim(f"--- 時間: {log_header_time_str} ({get_weekday_from_dt(current_sim_time_dt)}) | 階段: {to_traditional(current_phase)}{phase_step_display} {status_indicator} ---", "STEP")

        # ====================================
        # === Event Triggers & Phase Transitions ===
        # ====================================
        if current_phase == "Normal" and eq_enabled and next_earthquake_event_idx < len(scheduled_eq_events):
            next_eq = scheduled_eq_events[next_earthquake_event_idx]
            if current_sim_time_dt >= next_eq['time_dt']:
                current_phase = "Earthquake"
                earthquake_phase_step_counter = 0
                current_quake_details = {'intensity': next_eq['intensity'], 'start_time_dt': current_sim_time_dt, 'end_time_dt': current_sim_time_dt + timedelta(minutes=next_eq['duration'])}
                next_earthquake_event_idx += 1
                yield add_log_sim(f"!!! 地震開始 !!! 強度: {current_quake_details['intensity']:.2f}. 持續 {next_eq['duration']} 分鐘. 預計結束於: {format_datetime_obj(current_quake_details['end_time_dt'])}", "EVENT")
                yield add_log_sim("--- 代理人地震反應 (打斷行動) ---", "SUB")
                for agent in agents:
                    agent.interrupt_action(); agent.disaster_experience_log = []
                    agent.react_to_earthquake(current_quake_details['intensity'], buildings, agents)
                    yield add_log_sim(f"  {agent.name}: {agent.curr_action} ({agent.curr_action_pronunciatio}), HP:{agent.health}, 狀態:{to_traditional(agent.mental_state)}", "UPDATE")

        if current_phase == "Earthquake":
            earthquake_phase_step_counter +=1
            yield add_log_sim("--- 地震持續中: 代理人行為與主觀想法 ---", "SUB")
            if earthquake_phase_step_counter > 0 and random.random() < 0.2: # earthquake_phase_step_counter > 1 changed to > 0 to allow damage on first step after trigger
                yield add_log_sim("--- 地震持續中: 建築持續受損評估 ---", "SUB")
                for bld_name, bld_obj in buildings.items():
                    if bld_obj.integrity > 0 and bld_obj.integrity < 100:
                        further_damage = random.uniform(0, current_quake_details['intensity'] * 0.5)
                        original_integrity = bld_obj.integrity
                        bld_obj.integrity = max(0, bld_obj.integrity - further_damage)
                        if original_integrity - bld_obj.integrity > 0.1:
                             yield add_log_sim(f"    {to_traditional('建築')} {to_traditional(bld_name)} {to_traditional('持續受損')}, {to_traditional('完整度')} {original_integrity:.1f}% -> {bld_obj.integrity:.1f}%", "BUILDING_DMG")
            for agent in agents:
                if agent.health > 0:
                    action_log_msg = agent.perform_earthquake_step_action(agents, buildings, current_quake_details['intensity'])
                    if action_log_msg: yield add_log_sim(action_log_msg, "ACTION")
            
            if current_quake_details and current_sim_time_dt >= current_quake_details['end_time_dt']:
                yield add_log_sim(f"!!! 地震結束 @ {format_datetime_obj(current_sim_time_dt)} !!!", "EVENT")
                report_str = generate_disaster_report(agents, buildings) # Generate report before phase change
                yield add_log_sim(report_str, "REPORT")
                current_phase = "Recovery"
                recovery_phase_step_counter = 0
                recovery_end_time_dt = current_sim_time_dt + timedelta(minutes=60)
                yield add_log_sim(f"--- 進入 1 小時災後恢復階段 (至 {format_datetime_obj(recovery_end_time_dt)}) ---", "EVENT")
                yield add_log_sim("--- 更新代理人記憶 (地震經歷總結) ---", "SUB")
                for agent in agents:
                    if agent.health > 0:
                        disaster_summary = run_gpt_prompt_summarize_disaster(agent.name, agent.MBTI, agent.health, agent.disaster_experience_log)
                        agent.memory += f"\n[{to_traditional('災難記憶')}: {format_datetime_obj(current_sim_time_dt)}] {disaster_summary}"
                        agent.disaster_experience_log = []
                        yield add_log_sim(f"  {agent.name}: {to_traditional('記憶已更新')} - '{disaster_summary}'", "INFO")
                current_quake_details = None

        elif current_phase == "Recovery":
            recovery_phase_step_counter += 1
            yield add_log_sim("--- 災後恢復行動 ---", "SUB")
            for agent in agents:
                if agent.health > 0:
                    action_log_msg = agent.perform_recovery_step_action(agents, buildings)
                    if action_log_msg: yield add_log_sim(action_log_msg, "ACTION")
            
            if recovery_end_time_dt and current_sim_time_dt >= recovery_end_time_dt:
                yield add_log_sim(f"--- 災後恢復階段結束 @ {format_datetime_obj(current_sim_time_dt)} ---", "EVENT")
                current_phase = "PostQuakeDiscussion"
                post_quake_discussion_phase_step_counter = 0
                post_quake_discussion_end_time_dt = current_sim_time_dt + timedelta(hours=6)
                yield add_log_sim(f"--- 進入 6 小時災後討論期 (至 {format_datetime_obj(post_quake_discussion_end_time_dt)}) ---", "EVENT")
                post_quake_chat_context = to_traditional("（剛剛經歷了一場地震，我們的對話可能會圍繞地震的影響、各自的經歷或未來的計劃展開，請根據性格特點體現不同的關注点。）")
                for agent in agents:
                    if agent.health > 0: agent.last_action = to_traditional("重新評估中"); agent.interrupted_action = None


        elif current_phase == "PostQuakeDiscussion" or current_phase == "Normal":
            if current_phase == "PostQuakeDiscussion":
                post_quake_discussion_phase_step_counter += 1
                if post_quake_discussion_end_time_dt and current_sim_time_dt >= post_quake_discussion_end_time_dt:
                    yield add_log_sim(f"--- 災後討論期結束 @ {format_datetime_obj(current_sim_time_dt)} ---", "EVENT")
                    current_phase = "Normal"; normal_phase_step_counter = 0; post_quake_chat_context = None
                    yield add_log_sim("--- 模擬回到正常階段 ---", "EVENT")
                    for agent in agents:
                         if agent.health > 0: agent.last_action = to_traditional("恢復日常")
            else: # Normal phase
                normal_phase_step_counter += 1

            if current_time_hm_str == "03-00" and current_phase == "Normal":
                yield add_log_sim(f"--- {to_traditional('新的一天')} ({get_weekday_from_dt(current_sim_time_dt)}) | {to_traditional('執行每日計畫')} ---", "EVENT")
                for agent in agents:
                    if agent.health <=0 : continue
                    if agent.talk_arr: agent.memory = summarize(agent.talk_arr, current_date_weekday_str, agent.name); agent.talk_arr = ""; yield add_log_sim(f"  {agent.name}: {to_traditional('記憶已更新。')}", "INFO")
                    if agent.last_action == to_traditional("睡覺"): agent.goto_scene(agent.home, buildings); agent.mental_state = "calm"; agent.health = min(100, agent.health + random.randint(15, 30))
                    base_schedule_tasks = run_gpt_prompt_generate_hourly_schedule(agent.persona_summary, current_date_weekday_str)
                    agent.wake = run_gpt_prompt_wake_up_hour(agent.persona_summary, current_date_weekday_str, base_schedule_tasks)
                    agent.schedule_time = update_agent_schedule(agent.wake, base_schedule_tasks)
                    agent.schedule_time = modify_schedule(agent.schedule_time, current_date_weekday_str, agent.memory, agent.wake, agent.persona_summary)
                    is_sleeping = compare_times_hm(current_time_hm_str, agent.wake)
                    agent.curr_action = to_traditional("睡覺") if is_sleeping else find_agent_current_activity(current_time_hm_str, agent.schedule_time)[0]
                    agent.last_action = agent.curr_action; agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                    yield add_log_sim(f"  {agent.name}: {to_traditional('醒來時間')} {agent.wake}, {to_traditional('日程已生成。當前')}: {agent.curr_action}", "INFO")

            yield add_log_sim(f"--- {to_traditional('代理人行動更新')} ---", "SUB")
            active_agents_for_chat = []
            for agent in agents:
                if agent.health <= 0: continue
                log_prefix = f"  {agent.name}:"
                log_suffix = f"({agent.curr_action_pronunciatio}) @ {to_traditional(agent.curr_place)} (Pos:{agent.position}) | HP:{agent.health} St:{to_traditional(agent.mental_state)}"
                is_sleeping_now = compare_times_hm(current_time_hm_str, agent.wake)
                if is_sleeping_now:
                    if agent.curr_action != to_traditional("睡覺"):
                        agent.curr_action = to_traditional("睡覺"); agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                        agent.goto_scene(agent.home, buildings)
                        yield add_log_sim(f"{log_prefix} {agent.curr_action} {log_suffix}", "UPDATE")
                else:
                    if not isinstance(agent.schedule_time, list) or not agent.schedule_time : agent.schedule_time = [[to_traditional('自由活動'), current_time_hm_str]]
                    new_action, _ = find_agent_current_activity(current_time_hm_str, agent.schedule_time)
                    if agent.last_action != new_action or to_traditional("初始化中") in agent.last_action or agent.last_action == to_traditional("重新評估中") or agent.last_action == to_traditional("恢復日常"):
                        agent.curr_action = new_action
                        agent.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent.curr_action)
                        new_place = go_map(agent.name, agent.home, agent.curr_place, can_go_place, agent.curr_action)
                        moved = False
                        if new_place != agent.curr_place and new_place in can_go_place: moved = agent.goto_scene(new_place, buildings)
                        log_suffix = f"({agent.curr_action_pronunciatio}) @ {to_traditional(agent.curr_place)} (Pos:{agent.position}) | HP:{agent.health} St:{to_traditional(agent.mental_state)}"
                        if moved: yield add_log_sim(f"{log_prefix} {to_traditional('前往')} {to_traditional(agent.curr_place)} {to_traditional('執行')} {agent.curr_action} {log_suffix}", "UPDATE")
                        else: yield add_log_sim(f"{log_prefix} {to_traditional('在')} {to_traditional(agent.curr_place)} {to_traditional('開始')} {agent.curr_action} {log_suffix}", "UPDATE")
                        agent.last_action = agent.curr_action
                        agent.interrupted_action = None
                    else:
                        yield add_log_sim(f"{log_prefix} {to_traditional('繼續')} {agent.curr_action} {log_suffix}", "UPDATE")
                if agent.health > 0 and agent.mental_state not in ["unconscious", "panicked", "frozen"]: active_agents_for_chat.append(agent)

            if len(active_agents_for_chat) >= 2:
                chat_group = find_chat_groups(active_agents_for_chat)
                if chat_group:
                    chat_location_for_log = chat_group[0].curr_place
                    agent_names_in_chat = " & ".join([a.id for a in chat_group])
                    yield add_log_sim(f"--- {agent_names_in_chat} @ {to_traditional(chat_location_for_log)} {to_traditional('相遇並聊天')} ---", "CHAT_EVENT")
                    original_actions = {a.name: a.curr_action for a in chat_group}
                    for agent_in_group in chat_group:
                        agent_in_group.curr_action = to_traditional("聊天"); agent_in_group.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(agent_in_group.curr_action)
                        yield add_log_sim(f"  {agent_in_group.name}: {to_traditional('暫停')} {to_traditional(original_actions[agent_in_group.name])}, {to_traditional('開始聊天')} ({agent_in_group.curr_action_pronunciatio})", "UPDATE")
                    if len(chat_group) >= 2:
                        a1_chat, a2_chat = random.sample(chat_group, 2)
                        group_context_for_llm = f"{agent_names_in_chat} 在 {to_traditional(chat_location_for_log)}。 "
                        chat_context_from_actions = f"{a1_chat.name} {to_traditional('原本在做')} {to_traditional(original_actions[a1_chat.name])}, {a2_chat.name} {to_traditional('原本在做')} {to_traditional(original_actions[a2_chat.name])}。"
                        full_chat_context = group_context_for_llm + chat_context_from_actions
                        try:
                            chat_result_llm = double_agents_chat(
                                chat_location_for_log, a1_chat.name, a2_chat.name, full_chat_context,
                                a1_chat.talk_arr[-600:], a2_chat.talk_arr[-600:], current_date_weekday_str, eq_ctx=post_quake_chat_context
                            )
                            if chat_result_llm:
                                for chat_entry_idx, chat_entry in enumerate(chat_result_llm):
                                    if len(chat_entry) == 2: yield add_log_sim(f"{to_traditional(chat_entry[0])}: {to_traditional(chat_entry[1])}", "CHAT_CONTENT")
                                chat_json_for_memory = json.dumps(chat_result_llm, ensure_ascii=False)
                                for agent_in_group in chat_group: agent_in_group.talk_arr += chat_json_for_memory + "\n"
                            else: yield add_log_sim(f"  LLM {to_traditional('未生成有效對話內容。')}", "WARN")
                        except Exception as e: yield add_log_sim(f"{to_traditional('聊天生成失敗')}: {e}\n{traceback.format_exc()}", "ERROR")
        else:
            yield add_log_sim(f"--- {to_traditional('模擬步驟')} ({current_phase}) | {to_traditional('時間')}: {log_header_time_str} ({get_weekday_from_dt(current_sim_time_dt)}) | {to_traditional('階段')}: {to_traditional('未處理')} ({to_traditional(current_phase)}) ---", "WARN")

        current_sim_time_dt += timedelta(minutes=current_step_duration_minutes)
        # End of main while loop iteration

    end_msg = f"--- {to_traditional('模擬正常結束')} @ {format_datetime_obj(current_sim_time_dt)} ({get_weekday_from_dt(current_sim_time_dt)}) ---"
    yield add_log_sim(end_msg, "EVENT")

# --- Helper Functions for UI ---
def ui_update_weekday_display_wrapper(year, month, day):
    try:
        y = int(year) if year is not None else datetime.now().year
        m = int(month) if month is not None else datetime.now().month
        d = int(day) if day is not None else datetime.now().day
        return get_weekday_from_dt(datetime(y, m, d))
    except (ValueError, TypeError): return to_traditional("日期無效")

def generate_ui_tabs(all_mbti_types):
    target_files = get_target_files_for_agents(all_mbti_types)
    if not target_files: gr.Markdown(to_traditional("未能找到任何代理人的設定檔。請確保初始化已運行。")); return
    for agent_name in all_mbti_types:
        if agent_name in target_files:
            file_path = target_files[agent_name]
            def create_save_callback(fp):
                def save_callback(new_content): return save_file(fp, new_content)
                return save_callback
            with gr.Tab(agent_name):
                file_content = read_file(file_path)
                textbox = gr.Textbox(label=to_traditional("內容"), value=file_content, lines=20, max_lines=40, interactive=True)
                save_button = gr.Button(f"💾 {to_traditional('保存')} {agent_name}")
                save_status = gr.Label()
                save_button.click(create_save_callback(file_path), inputs=[textbox], outputs=save_status)
        else:
             with gr.Tab(agent_name): gr.Markdown(to_traditional(f"未能找到 **{agent_name}** 的設定檔。\n路徑: `{os.path.join(BASE_DIR, agent_name, TARGET_FILENAME)}`"))

# --- Gradio Interface ---
def launch_gradio_interface():
    os.makedirs(BASE_DIR, exist_ok=True)
    print(to_traditional("正在初始化代理人設定檔..."))
    initialize_agent_profiles(DEFAULT_MBTI_TYPES)
    print(to_traditional("代理人設定檔初始化完成。"))

    with gr.Blocks(theme=gr.themes.Soft(), css="footer {display: none !important;}") as demo:
        gr.Markdown(to_traditional("# 🏙️ AI 小鎮生活模擬器 (v5.3 - 精細災變管理)"))
        if not LLM_LOADED: gr.Markdown(to_traditional("⚠️ **警告:** 未能加載本地 LLM 函數。模擬行為將受限。"))

        with gr.Row():
            with gr.Column(scale=3):
                gr.Markdown(to_traditional("### 模擬控制"))
                with gr.Accordion(to_traditional("基本設置與起始時間"), open=True):
                    with gr.Row():
                        sim_duration_minutes_num = gr.Number(value=60*80, label=to_traditional("總模擬時長 (分鐘)"), minimum=60, step=60, info=to_traditional("例如：480分鐘 = 8小時"))
                        min_per_step_normal_num = gr.Number(value=30, label=to_traditional("正常階段步長 (分鐘/步)"), minimum=1, step=1, info=to_traditional("災難階段有獨立步長"))
                    with gr.Row():
                        start_year_num = gr.Number(value=2024, label=to_traditional("起始年份"), minimum=2020, step=1)
                        start_month_num = gr.Slider(value=11, label=to_traditional("起始月份"), minimum=1, maximum=12, step=1)
                        start_day_num = gr.Slider(value=18, label=to_traditional("起始日期"), minimum=1, maximum=31, step=1)
                    with gr.Row():
                        start_hour_num = gr.Slider(value=3, label=to_traditional("起始小時 (0-23)"), minimum=0, maximum=23, step=1)
                        start_minute_num = gr.Slider(value=0, label=to_traditional("起始分鐘 (0-59)"), minimum=0, maximum=59, step=5)
                        start_weekday_display_tb = gr.Textbox(label=to_traditional("起始星期"), interactive=False, value=to_traditional("星期一"))
                        
                        start_year_num.change(ui_update_weekday_display_wrapper, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display_tb)
                        start_month_num.change(ui_update_weekday_display_wrapper, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display_tb)
                        start_day_num.change(ui_update_weekday_display_wrapper, inputs=[start_year_num, start_month_num, start_day_num], outputs=start_weekday_display_tb)

                with gr.Accordion(to_traditional("選擇代理人 (1-16 名)"), open=True):
                     selected_mbtis_cb_group = gr.CheckboxGroup(DEFAULT_MBTI_TYPES, label=to_traditional("勾選要模擬的代理人"), value=["ISTJ", "ENFP", "ESFJ"], info=to_traditional("選擇1到16個代理人。越多越慢。"))

                with gr.Accordion(to_traditional("灾害设置: 地震事件排程"), open=True):
                     eq_enabled_cb = gr.Checkbox(label=to_traditional("啟用地震事件"), value=True, info=to_traditional("啟用後，將按照下方列表中的時間觸發地震。"))
                     default_eq_events = json.dumps([{"time": "2024-11-18-11-00", "duration": 30, "intensity": 0.75}], indent=2, ensure_ascii=False)
                     eq_events_tb = gr.Textbox(label=to_traditional("地震事件列表 (JSON 格式)"), value=default_eq_events, lines=8, info=to_traditional("格式: [{'time': 'YYYY-MM-DD-HH-MM', 'duration': 分鐘, 'intensity': 強度(0.1-1.0)}]"))
                     eq_step_duration_radio = gr.Radio([1, 5], label=to_traditional("地震期間步長 (分鐘)"), value=5, info=to_traditional("地震進行中每一步模擬的時間長度。"))


                simulate_button = gr.Button(f"▶️ {to_traditional('運行模擬')}", variant="primary", size="lg")
                gr.Markdown(to_traditional("### 模擬日誌"))
                simulation_output_tb = gr.Textbox(label="Simulation Log", interactive=False, lines=40, max_lines=80, autoscroll=True)

            with gr.Column(scale=1):
                gr.Markdown(to_traditional("### 代理人設定檔編輯器"))
                gr.Markdown(to_traditional("編輯所有可能的代理人的基礎設定。"))
                with gr.Tabs(): generate_ui_tabs(DEFAULT_MBTI_TYPES)

        run_inputs = [sim_duration_minutes_num, min_per_step_normal_num,
                      start_year_num, start_month_num, start_day_num, start_hour_num, start_minute_num,
                      selected_mbtis_cb_group,
                      eq_enabled_cb, eq_events_tb, eq_step_duration_radio]
        simulate_button.click(fn=simulate_town_life, inputs=run_inputs, outputs=[simulation_output_tb])

    print(to_traditional("Gradio 介面已配置。正在啟動..."))
    demo.queue().launch(share=False)

if __name__ == "__main__":
    launch_gradio_interface()
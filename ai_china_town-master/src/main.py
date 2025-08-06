# --- 引入必要的模組 ---
import random
from datetime import datetime, timedelta
import gradio as gr

# === 代理人 MBTI 個性設定 (Feature 2: MBTI Profiles) ===
# 定義16種 MBTI 個性的描述與合作意願指數，供代理人初始化使用
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
# === End of MBTI Profiles ===

# === Agent 代理人類別 (Features 2 & 3: MBTI Attributes and State) ===
class Agent:
    def __init__(self, agent_id, mbti_type, position=None, current_building=None):
        self.id = agent_id
        self.MBTI = mbti_type
        # MBTI 個性描述與合作傾向值
        self.personality_desc = MBTI_PROFILES[mbti_type]['desc']
        self.cooperation_inclination = MBTI_PROFILES[mbti_type]['cooperation']
        # 狀態屬性
        self.health = 100            # 健康值（0為重傷或死亡）
        self.is_injured = False      # 是否受傷（健康值過低時為 True）
        self.mental_state = "calm"   # 心理狀態: "calm", "alert", "panicked", "injured", "unconscious" 等
        self.position = position
        self.current_building = current_building  # 所在建築物 (Building 物件) 或 None（在室外）
        self.action = None           # 當前行動/反應 (如 'flee', 'assist_others', 'freeze', 等)

    def react_to_earthquake(self, intensity):
        """
        根據地震強度和自身狀態決定地震當下受到的傷害和反應行為。
        """
        # **地震傷害判定**: 根據位置決定傷害大小
        if self.current_building and self.current_building.integrity < 50:
            # 若所在建築損毀嚴重，受較大傷害
            damage = random.randint(int(intensity * 20), int(intensity * 50))
            self.health = max(0, self.health - damage)
        else:
            # 室外或建築安全情況下，較小機率受傷
            if random.random() < intensity * 0.5:
                damage = random.randint(1, int(intensity * 30))
                self.health = max(0, self.health - damage)
        # 更新受傷狀態
        if self.health <= 0:
            self.is_injured = True
            self.mental_state = "unconscious"  # 重傷失去意識
        elif self.health < 50:
            self.is_injured = True
            self.mental_state = "injured"      # 受傷但有意識
        # **地震當下心理反應**（若有意識）: 根據 MBTI 和強度決定
        if self.mental_state == "unconscious":
            self.action = None  # 失去意識無行動
            return
        reaction = None
        if intensity >= 0.7:
            # 強烈地震時的反應
            if 'E' in self.MBTI:  # 外向型
                if 'T' in self.MBTI and 'J' in self.MBTI:
                    reaction = "lead"    # 外向＋思考＋判斷型：保持冷靜領導他人
                    self.mental_state = "focused"
                elif 'F' in self.MBTI:
                    reaction = "panic"   # 外向＋情感型：可能過度驚慌失措
                    self.mental_state = "panicked"
                else:
                    reaction = "flee"    # 其他外向型：嘗試逃離
                    self.mental_state = "alert"
            else:
                # 內向型
                if 'F' in self.MBTI:
                    reaction = "freeze"  # 內向＋情感型：可能嚇呆在原地
                    self.mental_state = "panicked"
                else:
                    reaction = "flee"    # 其他內向型：默默尋找安全地點
                    self.mental_state = "alert"
        else:
            # 中等或較弱地震時的反應
            if 'J' in self.MBTI:
                reaction = "calm"   # 判斷型：傾向冷靜守秩序行動
                self.mental_state = "alert"
            elif 'P' in self.MBTI:
                reaction = "flee"   # 知覺型：傾向立即逃跑
                self.mental_state = "alert"
            else:
                reaction = "flee"
                self.mental_state = "alert"
        # **災時優先順序/合作考量**:
        # 若角色冷靜領導 (calm/lead) 或合作傾向很高，且自身未重傷，則選擇協助他人
        if (reaction in ["calm", "lead"]) or (self.cooperation_inclination > 0.7 and not self.is_injured):
            reaction = "assist_others"
            self.mental_state = "focused"
        self.action = reaction

    def perceive_and_help(self, other_agents):
        """
        在地震後立即執行合作行為:
        如當前設定為協助他人，則尋找附近受傷代理人進行救助。
        返回描述合作行為的字符串以記錄日誌。
        """
        if self.action == "assist_others":
            for other in other_agents:
                if other.id != self.id and other.is_injured and other.health > 0:
                    # 找到一位受傷但有意識的代理人進行救助
                    healed = min(100 - other.health, 10)  # 為對方回復一定健康值
                    other.health += healed
                    if other.health >= 50:
                        # 將受傷者狀態穩定下來
                        other.is_injured = False
                        other.mental_state = "calm"
                    # 救助一人後，將自身行動改為撤離以確保自身安全
                    self.action = "flee"
                    return f"Agent {self.id} helps Agent {other.id}"  # 記錄救助行為
        return None
# === End of Agent Class ===

# === Building 建築物類別 (Feature 1: Earthquake Damage) ===
class Building:
    def __init__(self, bld_id, position):
        self.id = bld_id
        self.position = position
        self.integrity = 100.0  # 建築物完整度百分比 (100為完好)

# === Simulation 模擬類別 (Features 1, 2 & 3) ===
class Simulation:
    def __init__(self, num_agents, earthquake_enabled=False, initial_quake_time=None, quake_frequency=0):
        # 模擬初始化：設定起始時間及地震參數
        self.time = datetime(2025, 1, 1, 0, 0)  # 模擬內部時間起點
        self.earthquake_enabled = earthquake_enabled
        self.quake_frequency = quake_frequency      # 地震頻率（模擬分鐘）
        self.initial_quake_time = initial_quake_time
        self.next_quake_time = None
        if self.earthquake_enabled and self.initial_quake_time:
            try:
                # 將初始地震時間字串轉為 datetime 物件
                self.next_quake_time = datetime.strptime(self.initial_quake_time, "%Y-%m-%d-%H-%M")
            except Exception as e:
                # 若時間格式解析失敗，停用地震模擬以避免錯誤
                self.earthquake_enabled = False

        # 建立環境中的建築物清單（此處假設固定若干建築，可依專案實際情況調整）
        self.buildings = [Building(i, position=(i * 10, 0)) for i in range(5)]
        # 建立代理人清單，依照指定數量初始化每個代理人
        self.agents = []
        for i in range(num_agents):
            # 隨機指派 MBTI 個性給代理人
            mbti = random.choice(list(MBTI_PROFILES.keys()))
            # 隨機決定代理人初始位置：一半機率在某棟建築物內，否則在室外隨機座標
            if random.random() < 0.5:
                bld = random.choice(self.buildings)
                pos = bld.position
                current_bld = bld
            else:
                pos = (random.randint(0, 40), random.randint(0, 20))
                current_bld = None
            agent = Agent(agent_id=i, mbti_type=mbti, position=pos, current_building=current_bld)
            self.agents.append(agent)
        # 事件日誌，用於記錄模擬中發生的關鍵事件（如地震、受傷、合作行為等）
        self.event_log = []

    def trigger_earthquake(self):
        """
        觸發一次地震事件，計算建築與人員損害，並產生相應反應與合作行為。
        """
        # 模擬地震強度（例如隨機0.5~1.0之間）
        intensity = random.uniform(0.5, 1.0)
        event_time_str = self.time.strftime("%Y-%m-%d %H:%M")
        # 紀錄地震發生事件
        self.event_log.append(f"Earthquake of intensity {intensity:.2f} at time {event_time_str}")
        # **建築物損害計算**: 根據強度隨機降低各建築完整度
        for bld in self.buildings:
            damage = random.uniform(0, intensity) * 100  # 根據強度決定降低百分比
            bld.integrity = max(0, bld.integrity - damage)
            if bld.integrity < 50:
                # 若建築完整度低於50%，視為嚴重受損，紀錄不可安全進入
                self.event_log.append(f"Building {bld.id} heavily damaged (integrity={bld.integrity:.1f}%)")
        # **代理人受損與反應**: 逐一讓代理人根據地震更新自身狀態並決定行動
        for agent in self.agents:
            agent.react_to_earthquake(intensity)
            # 紀錄受傷情況
            if agent.mental_state == "unconscious":
                self.event_log.append(f"Agent {agent.id} was critically injured and is unconscious")
            elif agent.is_injured:
                self.event_log.append(f"Agent {agent.id} is injured (health={agent.health})")
            # 紀錄代理人當下的初步反應行為
            if agent.action == "assist_others":
                self.event_log.append(f"Agent {agent.id} stays to assist others")
            elif agent.action == "flee":
                self.event_log.append(f"Agent {agent.id} is fleeing to safety")
            elif agent.action == "freeze":
                self.event_log.append(f"Agent {agent.id} is frozen in fear")
            elif agent.action == "lead":
                self.event_log.append(f"Agent {agent.id} tries to lead and protect others")
            elif agent.action == "panic":
                self.event_log.append(f"Agent {agent.id} panics")
            elif agent.action == "calm":
                self.event_log.append(f"Agent {agent.id} remains calm")
        # **合作行為觸發**: 讓每個代理人嘗試執行協助他人的動作（若其反應是 assist_others）
        for agent in self.agents:
            help_event = agent.perceive_and_help(self.agents)
            if help_event:
                # 紀錄代理人合作救援他人的事件
                self.event_log.append(help_event)
        # **安排下一次地震**: 若設置了重複地震頻率，則更新下次地震的觸發時間
        if self.quake_frequency and self.quake_frequency > 0:
            # 下一次地震時間 = 當前時間 + 設定的模擬分鐘
            self.next_quake_time = self.time + timedelta(minutes=self.quake_frequency)
        else:
            # 無頻率（只發生一次）則停用後續地震
            self.earthquake_enabled = False

    def run(self, duration_minutes=60):
        """
        執行模擬指定的模擬分鐘數。每一單位時間檢查是否觸發地震，並前進時間。
        返回事件日誌列表供顯示或分析。
        """
        end_time = self.time + timedelta(minutes=duration_minutes)
        while self.time < end_time:
            # 檢查地震觸發條件
            if self.earthquake_enabled and self.next_quake_time and self.time >= self.next_quake_time:
                self.trigger_earthquake()
            # （此處可加入角色移動等其他模擬行為邏輯）
            # 前進模擬時間1分鐘
            self.time += timedelta(minutes=1)
        return self.event_log
# === End of Simulation Class ===

# === Gradio 介面設定 (Features 1 & 2: Earthquake controls & Agent count control) ===
# 定義一個函式來運行模擬，將 Gradio 輸入映射到 Simulation 類別
def run_simulation(num_agents, eq_enabled, eq_start_time, eq_frequency):
    # 將輸入參數轉換並餵給 Simulation 進行模擬
    sim = Simulation(
        num_agents=int(num_agents),
        earthquake_enabled=bool(eq_enabled),
        initial_quake_time=eq_start_time,
        quake_frequency=int(eq_frequency) if eq_frequency is not None else 0
    )
    log = sim.run(duration_minutes=60)  # 執行模擬（例如模擬60分鐘，可視需要調整）
    # 將事件日誌列表合併為文字輸出
    return "\n".join(log)

# 建立 Gradio 介面，新增地震開關、初始時間、頻率和代理人數控制
with gr.Blocks() as demo:
    gr.Markdown("## AI Chinatown 模擬控制面板")
    # 輸入元件
    agent_num = gr.Slider(label="代理人數量", minimum=1, maximum=50, step=1, value=5)
    eq_toggle = gr.Checkbox(label="啟用地震事件模擬", value=False)
    eq_time = gr.Textbox(label="初始地震時間 (YYYY-MM-DD-HH-MM)", value="2025-01-01-00-10")
    eq_freq = gr.Number(label="地震頻率 (每次間隔模擬分鐘)", value=0, precision=0)
    # 輸出顯示模擬事件日誌
    sim_output = gr.Textbox(label="模擬事件日誌", lines=15)
    # 執行按鈕
    run_button = gr.Button("開始模擬")
    run_button.click(fn=run_simulation,
                     inputs=[agent_num, eq_toggle, eq_time, eq_freq],
                     outputs=sim_output)

# 啟動 Gradio 網頁介面（在主程式中執行時啟用）
# demo.launch()

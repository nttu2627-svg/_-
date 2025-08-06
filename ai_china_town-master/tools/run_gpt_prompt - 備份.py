import json
import re
import os
import traceback
from .ollama_agent import OllamaAgent # 使用相對導入
from datetime import datetime

# ──────────────────── 全局配置 ────────────────────
OLLAMA_URL = "http://127.0.0.1:11434"
MODEL_NAME = "deepseek-32b" # 請確保此模型已通過 'ollama pull deepseek-32b' 下載

PROMPT_DIR = os.path.join(os.path.dirname(__file__), 'prompt_template')
os.makedirs(PROMPT_DIR, exist_ok=True)

PROMPT_PATHS = {
    "generate_schedule": os.path.join(PROMPT_DIR, "生成日程安排时间表.txt"),
    "wake_up_hour": os.path.join(PROMPT_DIR, "起床时间.txt"),
    "pronunciatio": os.path.join(PROMPT_DIR, "行为转为图标显示.txt"),
    "double_chat": os.path.join(PROMPT_DIR, "聊天.txt"),
    "go_map": os.path.join(PROMPT_DIR, "行动需要去的地方.txt"),
    "modify_schedule": os.path.join(PROMPT_DIR, "细化每日安排时间表.txt"),
    "summarize_chat": os.path.join(PROMPT_DIR, "总结经历交谈为记忆.txt"),
    "get_recovery_action": os.path.join(PROMPT_DIR, "获取恢复行动.txt"),
    "summarize_disaster": os.path.join(PROMPT_DIR, "总结灾害经历.txt"),
    "inner_monologue": os.path.join(PROMPT_DIR, "生成內心獨白.txt"),
    "generate_initial_memory": os.path.join(PROMPT_DIR, "生成初始記憶.txt"),
}

DEFAULT_PROMPTS = {
    "generate_schedule": "基於角色 !<INPUT 0>! 的背景和當前時間 !<INPUT 1>!，為他/她生成一份包含[活動名稱, 預計持續分鐘數]的日程列表。請考慮角色的性格特點和可能的日常需求。",
    "wake_up_hour": "基於角色 !<INPUT 0>! 的背景，今天的日期 !<INPUT 1>! 和大致日程 !<INPUT 2>!，決定一個合理的起床時間。輸出格式為 HH-MM。",
    "pronunciatio": "為以下行動 '!<INPUT 0>!' 生成一個簡潔且具代表性的表情符號。",
    "double_chat": """
    模擬一場發生在 !<INPUT 0>! (地點) 的對話，參與者是：
    - !<INPUT 1>! (角色1)
    - !<INPUT 2>! (角色2)

    當前情境：
    !<INPUT 3>!

    角色 !<INPUT 1>! 的近期對話摘要：
    !<INPUT 4>!

    角色 !<INPUT 2>! 的近期對話摘要：
    !<INPUT 5>!

    模擬日期：!<INPUT 6>!
    近期特殊事件背景：!<INPUT 7>!

    請根據他們的性格特點、當前情境以及特殊事件背景，生成一段自然且符合邏輯的對話。
    對話應包含多輪交流，總長度約50到150字。
    如果近期事件背景提及了地震，請讓他們的對話適當地圍繞地震的影響、感受或後續計劃展開。
    確保對話風格符合各自的MBTI性格。
    """,
    "go_map": "角色 !<INPUT 0>! (家在 !<INPUT 1>!) 當前在 !<INPUT 2>!，想要執行 '!<INPUT 4>!'。附近的可選地點有：!<INPUT 3>!。他/她最應該去哪個地點？請只回答地點名稱。",
    "modify_schedule": "根據角色 !<INPUT 4>! 的舊日程 !<INPUT 0>!，當前時間 !<INPUT 1>!，近期記憶 !<INPUT 2>!，和起床時間 !<INPUT 3>!，適當調整並優化他/她的日程安排。請考慮記憶中可能提到的突發事件或新計劃。",
    "summarize_chat": "請將 !<INPUT 2>! (角色名) 在 !<INPUT 1>! (日期) 的這段聊天記錄 '!<INPUT 0>!' 總結為一段簡短（不超過50字）的記憶。",
    "get_recovery_action": "角色背景: !<INPUT 0>!\n當前精神狀態: !<INPUT 1>!\n所在位置: !<INPUT 2>!\n剛剛經歷了一場地震，現在是災後恢復期。請建議一個最優先的、簡短的恢復行動。",
    "summarize_disaster": "角色 !<INPUT 0>! (MBTI: !<INPUT 1>!) 剛剛經歷了一場災難。他/她目前的健康狀況是 !<INPUT 2>!/100。以下是災難期間的關鍵經歷記錄：\n!<INPUT 3>!\n請根據這些信息，為角色生成一段第一人稱的、簡短（約50-100字）的災後記憶總結，反映他/她的感受和狀態。",
}

for key, path in PROMPT_PATHS.items():
    if not os.path.exists(path):
        print(f"⚠️ [WARN] 提示詞檔案不存在: {path}。正在創建預設檔案。")
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(DEFAULT_PROMPTS.get(key, f"Default prompt for {key}"))
        except Exception as e:
            print(f"❌ [ERROR] 創建預設提示詞檔案 {path} 失敗: {e}")

try:
    ollama_agent = OllamaAgent(MODEL_NAME, OLLAMA_URL, "agent_simulator_v3")
    print(f"✅ [SUCCESS] OllamaAgent 初始化成功，模型: {MODEL_NAME}, URL: {OLLAMA_URL}")
    LLM_INITIALIZED_SUCCESSFULLY = True
except Exception as e:
    print(f"❌ [CRITICAL_ERROR] 初始化 OllamaAgent 失敗。請確認 Ollama 服務正在運行且模型 '{MODEL_NAME}' 已下載。")
    print(f"    詳細錯誤: {e}")
    ollama_agent = None
    LLM_INITIALIZED_SUCCESSFULLY = False

# ──────────────────── 預設返回值 (Fallback Values) ────────────────────
_DEF_EMOJI = "❓"
_DEF_WAKE  = "07-00"
_DEF_PLAN  = [["休息", 180]]
_DEF_RECOVERY_ACTION = "原地休息"
_DEF_DISASTER_SUMMARY = "經歷了一場地震，現在安全。"

# ──────────────────── 核心函數 ────────────────────

def _json_output_regex(text: str, default: any):
    if not text or not isinstance(text, str):
        return default
    
    match = re.search(r"```json\s*(\{.*?\})\s*```", text, re.DOTALL)
    if match:
        text = match.group(1)
    
    try:
        data = json.loads(text)
        return data.get("output", default)
    except json.JSONDecodeError:
        match = re.search(r'\{\s*"output"\s*:\s*(?P<value>.*?)\s*\}', text, re.DOTALL)
        if match:
            value_str = match.group("value").strip()
            if value_str.endswith(','): value_str = value_str[:-1]
            try:
                return json.loads(f'{{"output": {value_str}}}')['output']
            except json.JSONDecodeError:
                if value_str.startswith('"') and value_str.endswith('"'): return value_str[1:-1]
                return value_str
        return default

def _safe_llm_call(prompt_key: str, prompt_args: list, example_output: any, special_instruction: str, repeat: int, fail_safe_json: any, default_on_error: any):
    """
    帶有詳細日誌和錯誤處理的 LLM 調用封裝器
    """
    print(f"➡️  [LLM_CALL] 嘗試呼叫 '{prompt_key}'...")

    if not LLM_INITIALIZED_SUCCESSFULLY or ollama_agent is None:
        print(f"    - ❌ [LLM_CALL_FAIL] 因 OllamaAgent 未成功初始化，呼叫 '{prompt_key}' 中斷，返回預設值。")
        return default_on_error

    prompt_template_path = PROMPT_PATHS.get(prompt_key)
    if not prompt_template_path or not os.path.exists(prompt_template_path):
        print(f"    - ❌ [LLM_CALL_FAIL] 找不到提示詞模板檔案 {prompt_key} ({prompt_template_path})。返回預設值。")
        return default_on_error

    try:
        processed_prompt_args = [str(arg) if not isinstance(arg, str) else arg for arg in prompt_args]
        prompt = OllamaAgent.generate_prompt(processed_prompt_args, prompt_template_path)
        
        final_instruction = f"{special_instruction} 請務必使用繁體中文（Traditional Chinese）回答。"

        output_str = ollama_agent.ollama_safe_generate_response(
            prompt=prompt,
            example_output=example_output,
            special_instruction=final_instruction,
            repeat=repeat,
            func_validate=lambda x: isinstance(x, str) and len(x) > 5,
            func_clean_up=lambda x: x.strip(),
            fail_safe=fail_safe_json,
        )

        print(f"    - 📄 [LLM_RAW_OUTPUT] 從 '{prompt_key}' 收到原始輸出: {str(output_str)[:250]}...")
        
        parsed_output = _json_output_regex(output_str, default_on_error)
        
        print(f"    - ✨ [LLM_PARSED_OUTPUT] 解析後輸出: {parsed_output}")
        print(f"✅ [LLM_CALL_SUCCESS] 成功完成 '{prompt_key}' 呼叫。")
        
        return parsed_output

    except Exception as e:
        print(f"❌ [LLM_CALL_ERROR] 在 LLM 呼叫 '{prompt_key}' 期間發生嚴重錯誤: {e}")
        print(traceback.format_exc())
        print(f"    - ⚠️ 返回預設值: {default_on_error}")
        return default_on_error

# ──────────────────── 導出的 LLM 函數 ────────────────────

def run_gpt_prompt_generate_hourly_schedule(persona, now_time):
    example = [["吃早餐", 30], ["上課", 120]]
    parsed_output = _safe_llm_call(
        prompt_key="generate_schedule", prompt_args=[persona, now_time],
        example_output=example,
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為一個列表，其中每個子列表包含[活動名稱, 預計持續分鐘數]。',
        repeat=3, fail_safe_json={"output": _DEF_PLAN}, default_on_error=_DEF_PLAN
    )
    if isinstance(parsed_output, list) and all(isinstance(i, (list, tuple)) and len(i) == 2 for i in parsed_output):
        return parsed_output
    print(f"⚠️ [VALIDATION_FAIL] 'generate_schedule' 的輸出格式不正確，返回預設日程。收到的內容: {parsed_output}")
    return _DEF_PLAN

def run_gpt_prompt_wake_up_hour(persona, now_time, hourly_schedule):
    schedule_str = json.dumps(hourly_schedule, ensure_ascii=False)
    wake_time = _safe_llm_call(
        prompt_key="wake_up_hour", prompt_args=[persona, now_time, schedule_str],
        example_output="07-15",
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為 "HH-MM" 格式的起床時間字符串。',
        repeat=3, fail_safe_json={"output": _DEF_WAKE}, default_on_error=_DEF_WAKE
    )
    if isinstance(wake_time, str) and re.match(r"^\d{2}-\d{2}$", wake_time):
         return wake_time
    print(f"⚠️ [VALIDATION_FAIL] 'wake_up_hour' 的輸出格式不正確，返回預設時間。收到的內容: {wake_time}")
    return _DEF_WAKE

def run_gpt_prompt_pronunciatio(action_dec):
    common_emojis = {
        "睡覺": "😴", "休息": "🛋️", "吃飯": "🍕", "早餐": "🍳", "午餐": "🍔", "晚餐": "🍜", "上課": "📚",
        "學習": "✍️", "工作": "💼", "開會": "🗣️", "移動": "🚶", "跑步": "🏃", "健身": "🏋️", "走路": "🚶‍♀️",
        "看電影": "🎬", "玩遊戲": "🎮", "看書": "📖", "聽音樂": "🎧", "聊天": "💬", "打電話": "📞",
        "購物": "🛒", "做飯": "🧑‍🍳", "洗澡": "🛀", "醒來": "☀️", "恢復中": "🩹", "檢查": "👀",
        "搜索": "🔎", "Unconscious": "😵", "lead": "🧑‍🚒", "panic": "😱", "flee": "💨", "freeze": "🥶",
        "calm": "🧘", "assist_others": "🤝", "injured_flee": "🤕", "recovering": "😌",
        "Initializing": "⏳", "時間錯誤": "❓", "評估損傷": "🧐", "尋求協助": "🆘", "初始化中": "⏳"
    }
    for key, emoji in common_emojis.items():
        if key in action_dec:
            return emoji
    return _DEF_EMOJI

def double_agents_chat(
    location,
    agent1, agent2,
    chat_history: list,
    eq_ctx=None
):
    """
    生成兩個 Agent 之間的多輪、有上下文的對話。
    agent1 和 agent2 是 TownAgent 物件。
    """
    earthquake_context = eq_ctx if eq_ctx else "目前一切正常。"
    
    # 準備 Prompt 所需的參數
    a1_name = agent1.name
    a2_name = agent2.name
    a1_mbti = agent1.MBTI
    a2_mbti = agent2.MBTI
    a1_memory = agent1.memory[-500:] # 取最近500字的記憶
    a2_memory = agent2.memory[-500:]
    now_time = datetime.now().strftime('%Y年%m月%d日 %H:%M') # 假設我們需要當前時間
    a1_original_action = agent1.interrupted_action or agent1.last_action
    a2_original_action = agent2.interrupted_action or agent2.last_action
    
    # 將對話歷史格式化為字串
    chat_history_str = "\n".join([f"{name}: {dialog}" for name, dialog in chat_history])

    example = {
        "thought": "丹尼看到蘇克很驚訝，可能會問他為什麼在這裡。蘇克比較內向，可能會簡單回答。",
        "dialogue": [["丹尼","蘇克？真巧，你怎麼會在這？"],["蘇克","...來買點東西。"]]
    }
    fail_safe = {"output": {"thought": "LLM 思考失敗。", "dialogue": []}}

    new_prompt_args = [
        location,                       # 0
        a1_name,                        # 1
        a1_mbti,                        # 2
        agent1.personality_desc,        # 3 (使用更詳細的性格描述)
        a1_memory,                      # 4
        a2_name,                        # 5
        a2_mbti,                        # 6
        agent2.personality_desc,        # 7
        a2_memory,                      # 8
        now_time,                       # 9
        a1_original_action,             # 10
        a2_original_action,             # 11
        earthquake_context,             # 12
        chat_history_str                # 13
    ]

    parsed_output = _safe_llm_call(
        prompt_key="double_chat", 
        prompt_args=new_prompt_args,
        example_output=example,
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為一個列表，其中每個子列表包含["說話者名稱", "話語內容"]。',
        repeat=3, 
        fail_safe_json=fail_safe, 
        default_on_error=[]
    )
    thought = parsed_output.get("thought", "思考過程提取失敗。")
    dialogue = parsed_output.get("dialogue", [])
    if isinstance(parsed_output, list) and all(isinstance(i, list) and len(i) == 2 for i in parsed_output):
        return parsed_output
    
    print(f"⚠️ [VALIDATION_FAIL] 'double_agents_chat' 的輸出格式不正確，返回空對話。收到的內容: {parsed_output}")
    return []
def go_map(agent_name, home, curr_place, can_go_list, curr_task):
    can_go_str = ", ".join(can_go_list)
    destination = _safe_llm_call(
        prompt_key="go_map", prompt_args=[agent_name, home, curr_place, can_go_str, curr_task],
        example_output="海邊",
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為目標地點名稱字符串。',
        repeat=3, fail_safe_json={"output": home}, default_on_error=home
    )
    if isinstance(destination, str) and destination in can_go_list:
        return destination
    print(f"⚠️ [VALIDATION_FAIL] 'go_map' 的輸出地點無效 '{destination}'，返回家中。")
    return home

def modify_schedule(old_sched, now_time, memory, wake_time, role):
    old_sched_str = json.dumps(old_sched, ensure_ascii=False)
    parsed_output = _safe_llm_call(
        prompt_key="modify_schedule", prompt_args=[old_sched_str, now_time, memory, wake_time, role],
        example_output=[["吃早餐", 45], ["閱讀", 60]],
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為調整後的日程列表，格式同輸入。',
        repeat=4, fail_safe_json={"output": old_sched}, default_on_error=old_sched
    )
    if isinstance(parsed_output, list) and all(isinstance(i, (list, tuple)) and len(i) == 2 for i in parsed_output):
        return parsed_output
    print(f"⚠️ [VALIDATION_FAIL] 'modify_schedule' 的輸出格式不正確，返回舊日程。收到的內容: {parsed_output}")
    return old_sched

def summarize(memory, now_time, name):
    summary = _safe_llm_call(
        prompt_key="summarize_chat", prompt_args=[memory, now_time, name],
        example_output="今天和小芳聊了週末去公園的事，感覺很期待。",
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為簡短的聊天總結字符串。',
        repeat=3, fail_safe_json={"output":"今天沒有特別的對話。"}, default_on_error="未能總結對話。"
    )
    if isinstance(summary, str):
        return summary
    print(f"⚠️ [VALIDATION_FAIL] 'summarize' 的輸出格式不正確，返回預設總結。收到的內容: {summary}")
    return "對話總結處理錯誤。"

def run_gpt_prompt_get_recovery_action(persona, mental_state, curr_place):
    action = _safe_llm_call(
        prompt_key="get_recovery_action", prompt_args=[persona, mental_state, curr_place],
        example_output="檢查房屋損傷",
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為建議的恢復行動短語字符串。',
        repeat=3, fail_safe_json={"output": _DEF_RECOVERY_ACTION}, default_on_error=_DEF_RECOVERY_ACTION
    )
    if isinstance(action, str):
        return action
    print(f"⚠️ [VALIDATION_FAIL] 'get_recovery_action' 的輸出格式不正確，返回預設行動。收到的內容: {action}")
    return _DEF_RECOVERY_ACTION

def run_gpt_prompt_summarize_disaster(agent_name, mbti, health, experience_log):
    log_str = "\n".join([f"- {entry}" for entry in experience_log])
    if not log_str: log_str = "(沒有具體事件記錄)"

    summary = _safe_llm_call(
        prompt_key="summarize_disaster", prompt_args=[agent_name, mbti, health, log_str],
        example_output="地震中受了點輕傷，但安全度過了，現在感覺有些後怕。",
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為簡短的災後記憶總結字符串。',
        repeat=3, fail_safe_json={"output": _DEF_DISASTER_SUMMARY}, default_on_error=_DEF_DISASTER_SUMMARY
    )
    if isinstance(summary, str):
        return summary
    print(f"⚠️ [VALIDATION_FAIL] 'summarize_disaster' 的輸出格式不正確，返回預設總結。收到的內容: {summary}")
    return _DEF_DISASTER_SUMMARY
def generate_inner_monologue(agent, eq_ctx=None):
    """
    為單個 agent 生成內心獨白。
    """
    earthquake_context = eq_ctx if eq_ctx else "目前一切正常。"
    now_time = datetime.now().strftime('%Y年%m月%d日 %H:%M')

    prompt_args = [
        agent.name,                     # 0
        agent.MBTI,                     # 1
        agent.personality_desc,         # 2
        agent.curr_place,               # 3
        agent.curr_action,              # 4
        now_time,                       # 5
        agent.memory[-500:],            # 6
        earthquake_context,             # 7
    ]

    example = {
        "thought": "角色覺得疲憊，想著晚飯吃什麼。",
        "monologue": "（好累啊...不知道今晚該吃點什麼好。）"
    }
    fail_safe = {"output": {"thought": "LLM 思考失敗。", "monologue": "（...）"}}

    parsed_output = _safe_llm_call(
        prompt_key="inner_monologue",
        prompt_args=prompt_args,
        example_output=example,
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為一段以「（」開頭，以「）」結尾的內心獨白字符串。',
        repeat=3,
        fail_safe_json=fail_safe,
        default_on_error={"thought": "解析錯誤。", "monologue": "（正在思考...）"}
    )
    thought = parsed_output.get("thought", "思考過程提取失敗。")
    monologue = parsed_output.get("monologue", "（...）")
    if isinstance(monologue, str) and monologue.startswith('（') and monologue.endswith('）'):
        return monologue
    
    print(f"⚠️ [VALIDATION_FAIL] 'inner_monologue' 的輸出格式不正確，返回預設獨白。收到的內容: {monologue}")
    return thought, monologue
def run_gpt_prompt_generate_initial_memory(agent):
    """為代理人生成初始背景記憶和一個簡單的待辦事項。"""
    
    example = {
        "memory": "最近一直在忙工作，感覺有點累。上週末和朋友去海邊散步，心情好了很多。希望這週能有時間放鬆一下。",
        "schedule": "後天要去醫院做個例行檢查。"
    }
    fail_safe = {"output": {"memory": "這是一個新開始。", "schedule": "明天需要去商場買東西。"}}

    parsed_output = _safe_llm_call(
        prompt_key="generate_initial_memory",
        prompt_args=[agent.name, agent.MBTI, agent.personality_desc, agent.home],
        example_output=example,
        special_instruction='請以包含名為 "output" 的單一鍵的 JSON 物件格式返回。該鍵的值應為一個包含 "memory" 和 "schedule" 鍵的物件。請務必使用繁體中文（Traditional Chinese）回答。',
        repeat=3,
        fail_safe_json=fail_safe,
        default_on_error={"memory": "記憶生成失敗。", "schedule": "日程生成失敗。"}
    )

    return parsed_output.get("memory", ""), parsed_output.get("schedule", "")
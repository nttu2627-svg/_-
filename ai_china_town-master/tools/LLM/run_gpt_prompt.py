# tools/LLM/run_gpt_prompt.py (时间正则最终完整版)

import json
import re
import os
import sys
import traceback
import random
from datetime import datetime
from .ollama_agent import OllamaAgent
import asyncio
from opencc import OpenCC
from .emoji_rules import (
    allowed_emojis,
    classify_activity,
    match_known_emoji,
    normalize_label,
)
# --- 全局配置与日志 ---
try:
    cc = OpenCC('s2twp')
    print("✅ [SUCCESS] OpenCC 繁簡轉換器初始化成功。")
except Exception as e:
    print(f"❌ [CRITICAL_ERROR] 初始化 OpenCC 失败: {e}", file=sys.stderr)
    class MockCC:
        def convert(self, text): return text
    cc = MockCC()

OLLAMA_URL = "http://127.0.0.1:11434/api"
MODEL_NAME = "deepseek-r1:14b"
PROMPT_DIR = os.path.join(os.path.dirname(__file__), 'prompt_template')
os.makedirs(PROMPT_DIR, exist_ok=True)
ALLOWED_EMOJIS = set(allowed_emojis())
DEFAULT_LABEL, DEFAULT_EMOJI = classify_activity("")
LLM_LOG_BUFFER = []
MAX_LLM_LOG_LINES = 400

def _limit_repetitive_sequences(text: str, max_repeat: int = 6, max_seq_len: int = 12):
    """Clamp pathological repetitions caused by LLM streaming glitches."""
    if not isinstance(text, str) or not text:
        return text, False

    sanitized = text
    changed = False
    for seq_len in range(1, max_seq_len + 1):
        pattern = re.compile(rf'(.{{{seq_len}}})\1{{{max_repeat},}}', flags=re.DOTALL)

        def _replace(match):
            nonlocal changed
            changed = True
            segment = match.group(1)
            return segment * max_repeat

        sanitized = pattern.sub(_replace, sanitized)
    return sanitized, changed


def _sanitize_repetitive_output(value):
    """Recursively clean strings, lists or dicts returned by the LLM."""
    if isinstance(value, str):
        return _limit_repetitive_sequences(value)
    if isinstance(value, list):
        any_changed = False
        sanitized_list = []
        for item in value:
            sanitized_item, changed = _sanitize_repetitive_output(item)
            any_changed = any_changed or changed
            sanitized_list.append(sanitized_item)
        return sanitized_list, any_changed
    if isinstance(value, dict):
        any_changed = False
        sanitized_dict = {}
        for key, item in value.items():
            sanitized_item, changed = _sanitize_repetitive_output(item)
            any_changed = any_changed or changed
            sanitized_dict[key] = sanitized_item
        return sanitized_dict, any_changed
    return value, False



def _classify_activity_label(raw: str):
    if not isinstance(raw, str) or not raw.strip():
        return DEFAULT_LABEL, DEFAULT_EMOJI
    candidate = raw.strip()
    canonical, emoji = classify_activity(candidate)
    return canonical, emoji


def _normalize_activity_label(raw: str) -> str:
    return normalize_label(str(raw))

def log_llm_call(prompt_key, final_prompt, raw_response, final_output):
    global LLM_LOG_BUFFER
    log_entry = (f"--- LLM Call @ {datetime.now().strftime('%Y-%m-%d %H:%M:%S')} ---\n"
                 f"Prompt Key: {prompt_key}\nFinal Prompt:\n---\n{final_prompt}\n---\n"
                 f"Raw Response:\n---\n{raw_response}\n---\n"
                 f"Final Parsed Output:\n---\n{json.dumps(final_output, ensure_ascii=False, indent=2)}\n"
                 f"---------------------------------------------------\n")
    LLM_LOG_BUFFER.append(log_entry)
    if len(LLM_LOG_BUFFER) > MAX_LLM_LOG_LINES: LLM_LOG_BUFFER.pop(0)

def get_llm_log(): return "\n".join(LLM_LOG_BUFFER)

PROMPT_PATHS = {
    "generate_schedule": os.path.join(PROMPT_DIR, "生成日程安排时间表.txt"), "wake_up_hour": os.path.join(PROMPT_DIR, "起床时间.txt"),
    "pronunciatio": os.path.join(PROMPT_DIR, "行为转为图标显示.txt"), "double_chat": os.path.join(PROMPT_DIR, "聊天.txt"),
    "go_map": os.path.join(PROMPT_DIR, "行动需要去的地方.txt"), "modify_schedule": os.path.join(PROMPT_DIR, "细化每日安排时间表.txt"),
    "summarize_chat": os.path.join(PROMPT_DIR, "总结经历交谈为记忆.txt"), "get_recovery_action": os.path.join(PROMPT_DIR, "获取恢复行動.txt"),
    "summarize_disaster": os.path.join(PROMPT_DIR, "总结灾害经历.txt"), "generate_initial_memory": os.path.join(PROMPT_DIR, "生成初始記憶.txt"),
    "inner_monologue": os.path.join(PROMPT_DIR, "生成內心獨白.txt"), "generate_action_thought": os.path.join(PROMPT_DIR, "生成行動想法.txt"),
    "earthquake_step_action": os.path.join(PROMPT_DIR, "生成地震中行動.txt"), "generate_weekly_schedule": os.path.join(PROMPT_DIR, "生成七日行事曆.txt"),
}

ollama_agent = None
async def initialize_llm():
    global ollama_agent
    if ollama_agent is None:
        try:
            ollama_agent = OllamaAgent(MODEL_NAME, OLLAMA_URL)
            print(f"✅ [SUCCESS] OllamaAgent (流式) 初始化成功，模型: {MODEL_NAME}")
            return True
        except Exception as e:
            print(f"❌ [CRITICAL_ERROR] 初始化 OllamaAgent (流式) 失敗: {e}", file=sys.stderr)
            return False
    return True

async def close_llm_session():
    if ollama_agent: await ollama_agent.close_session(); print("OllamaAgent 對話已關閉。")

def _json_output_regex(text: str, default: any):
    if not isinstance(text, str): return default
    match = re.search(r"```json\s*(\{.*?\})\s*```", text, re.DOTALL)
    json_str = match.group(1) if match else None
    if not json_str:
        start, end = text.find('{'), text.rfind('}')
        if start != -1 and end != -1 and end > start: json_str = text[start:end+1]
        else:
            if isinstance(default, str): return text.strip()
            return default
    try:
        data = json.loads(json_str)
        return data.get("output", data)
    except json.JSONDecodeError:
        if isinstance(default, str): return text.strip()
        return default

async def _safe_llm_call(prompt_key: str, prompt_args: list, special_instruction: str, default_on_error: any):
    if not await initialize_llm(): return default_on_error
    prompt_template_path = PROMPT_PATHS.get(prompt_key)
    if not os.path.exists(prompt_template_path): return default_on_error
    raw_response, final_output, prompt = "N/A", default_on_error, ""
    try:
        processed_args = [str(arg) for arg in prompt_args]
        prompt = OllamaAgent.generate_prompt(processed_args, prompt_template_path)
        final_instruction = (
            f"{special_instruction} 請務必使用繁體中文（Traditional Chinese）回答，"
            "請直接給出精簡的最終輸出，避免冗長的推理步驟、<think> 標籤或重複語句。"
        )
        is_json_output = not isinstance(default_on_error, str)
        raw_response = await ollama_agent.ollama_stream_generate_response(prompt=prompt, special_instruction=final_instruction, expect_json=is_json_output, example_output=default_on_error)
        if raw_response is None: final_output = default_on_error
        else: final_output = _json_output_regex(raw_response, default_on_error) if is_json_output else raw_response
        if isinstance(final_output, str): converted_output = cc.convert(final_output)
        elif isinstance(final_output, dict): converted_output = {cc.convert(k): (cc.convert(v) if isinstance(v, str) else v) for k, v in final_output.items()}
        elif isinstance(final_output, list):
            def convert_list_items(item):
                if isinstance(item, str): return cc.convert(item)
                if isinstance(item, list): return [convert_list_items(i) for i in item]
                if isinstance(item, dict): return {cc.convert(k): (cc.convert(v) if isinstance(v, str) else v) for k, v in item.items()}
                return item
            converted_output = [convert_list_items(i) for i in final_output]
        else: converted_output = final_output
        converted_output, sanitized = _sanitize_repetitive_output(converted_output)
        if sanitized:
            print(f"⚠️ [LLM_OUTPUT_SANITIZED] 偵測到 '{prompt_key}' 回傳大量重複內容，已自動裁切。", file=sys.stderr)
        log_llm_call(prompt_key, prompt, str(raw_response), converted_output)
        return converted_output
    except Exception as e:
        print(f"❌ [LLM_CALL_ERROR] 在 LLM 呼叫 '{prompt_key}' 期間發生嚴重錯誤: {e}", file=sys.stderr)
        log_llm_call(prompt_key, prompt, str(e), default_on_error)
        return default_on_error

async def run_gpt_prompt_generate_initial_memory(name, mbti, persona_summary, home):
    default_memory = "記憶生成失敗，請檢查LLM連線。"
    memory_result = await _safe_llm_call("generate_initial_memory", [name, mbti, persona_summary, home], '僅返回描述代理人背景故事的純文字字串。', default_memory)
    success = memory_result != default_memory and isinstance(memory_result, str)
    return str(memory_result), success
def _match_common_emoji(text: str):
    if not isinstance(text, str):
        return DEFAULT_EMOJI
    return match_known_emoji(text)


def _normalize_schedule(schedule):
    if not isinstance(schedule, list):
        return schedule
    updated_schedule = []
    for entry in schedule:
        if isinstance(entry, list) and entry:
            label = entry[0]
            canonical = _normalize_activity_label(str(label))
            new_entry = entry.copy()
            new_entry[0] = canonical
            updated_schedule.append(new_entry)
            continue
        if isinstance(entry, str):
            updated_schedule.append(_normalize_activity_label(entry))
        else:
            updated_schedule.append(entry)
    return updated_schedule
async def run_gpt_prompt_generate_weekly_schedule(persona_summary):
    default_label = normalize_label("休息")
    default_schedule = {day: default_label for day in ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]}
    schedule = await _safe_llm_call("generate_weekly_schedule", [persona_summary], '返回一個包含七天（Monday-Sunday）鍵的 JSON 物件。', default_schedule)
    if isinstance(schedule, dict):
        schedule = {day: _normalize_activity_label(str(activity)) for day, activity in schedule.items()}
    success = schedule != default_schedule and isinstance(schedule, dict) and len(schedule) == 7
    return schedule, success

async def run_gpt_prompt_generate_hourly_schedule(persona, now_time, today_goal):
    default_on_error = [[normalize_label("休息"), 1440]]
    schedule = await _safe_llm_call("generate_schedule", [persona, now_time, today_goal], '返回一個列表，其中每個子列表包含[活動名稱, 持續分鐘數]。', default_on_error)
    return _normalize_schedule(schedule)
async def run_gpt_prompt_wake_up_hour(persona, now_time, hourly_schedule):
    schedule_str = json.dumps(hourly_schedule, ensure_ascii=False)
    default_on_error = f"{random.randint(6, 8):02d}-{random.choice(['00', '15', '30'])}"
    raw_llm_output = await _safe_llm_call("wake_up_hour", [persona, now_time, schedule_str], '返回 "HH:MM" 或 "HH-MM" 格式的時間字串。', default_on_error)
    match = re.search(r'\b([0-1][0-9]|2[0-3])[:|-]([0-5][0-9])\b', str(raw_llm_output))
    return f"{match.group(1)}-{match.group(2)}" if match else default_on_error

async def run_gpt_prompt_pronunciatio(action_dec):
    action_str = str(action_dec)
    canonical, emoji = _classify_activity_label(action_str)
    if canonical != "意識不明":
        return emoji
    result = await _safe_llm_call("pronunciatio", [action_str], '只返回一個最適合的 emoji 圖標字串。', DEFAULT_EMOJI)
    return _match_common_emoji(str(result))
async def generate_action_thought(persona_summary, current_place, new_action):
    return await _safe_llm_call("generate_action_thought", [persona_summary, current_place, new_action], '返回一句約20字的簡短內心想法字串。', "")

async def run_gpt_prompt_earthquake_step_action(persona_summary, health, mental_state, current_place, intensity, disaster_log):
    default_on_error = {"action": "保持警惕", "thought": "(恐懼中...)"}
    log_context = "\n".join(disaster_log)
    parsed_output = await _safe_llm_call("earthquake_step_action", [persona_summary, health, mental_state, current_place, intensity, log_context], '輸出包含 "action" 和 "thought" 鍵的 JSON 物件。', default_on_error)
    return parsed_output.get("action", default_on_error["action"]), parsed_output.get("thought", default_on_error["thought"])

async def double_agents_chat(chat_context):
    default_on_error = {"thought": "解析錯誤。", "dialogue": []}
    prompt_args = [
        chat_context['location'], chat_context['agent1']['name'], chat_context['agent1']['mbti'], chat_context['agent1']['persona'], str(chat_context['agent1']['memory'])[-300:],
        chat_context['agent2']['name'], chat_context['agent2']['mbti'], chat_context['agent2']['persona'], str(chat_context['agent2']['memory'])[-300:],
        chat_context['now_time'], chat_context['agent1']['action'], chat_context['agent2']['action'],
        chat_context.get('eq_ctx') or "目前一切正常。", json.dumps(chat_context['history'], ensure_ascii=False) ]
    parsed_output = await _safe_llm_call(
        "double_chat",
        prompt_args,
        '輸出一個包含 "thought" 和 "dialogue" 鍵的 JSON 物件，dialogue 請限制 2~4 句，每句不超過 20 字，避免重複或贅詞。',
        default_on_error
    )
    return parsed_output.get("thought", default_on_error["thought"]), parsed_output.get("dialogue", default_on_error["dialogue"])

async def generate_inner_monologue(agent_context):
    default_on_error = {"thought": "解析錯誤。", "monologue": "（正在思考...）"}
    prompt_args = [
        agent_context['name'], agent_context['mbti'], agent_context['persona'], agent_context['location'],
        agent_context['action'], agent_context['now_time'], str(agent_context['memory'])[-300:],
        agent_context.get('eq_ctx') or "目前一切正常。" ]
    parsed_output = await _safe_llm_call(
        "inner_monologue",
        prompt_args,
        '輸出一個包含 "thought" 和 "monologue" 鍵的 JSON 物件，monologue 內容請控制在 25 字以內並避免重複語句。',
        default_on_error
    )
    return parsed_output.get("thought", default_on_error["thought"]), parsed_output.get("monologue", default_on_error["monologue"])
    
async def run_gpt_prompt_summarize_disaster(agent_name, mbti, health, experience_log):
    log_str = "\n".join([str(entry) for entry in experience_log]) or "(沒有具體事件記錄)"
    return await _safe_llm_call("summarize_disaster", [agent_name, mbti, health, log_str], '返回簡短的災後記憶總結字串。', "經歷了一場地震，現在安全。")

async def run_gpt_prompt_get_recovery_action(persona, mental_state, curr_place):
    return await _safe_llm_call("get_recovery_action", [persona, mental_state, curr_place], '返回建議的恢復行動短語字串。', "原地休息")


async def go_map_async(agent_name, home, curr_place, can_go_list, curr_task):
    can_go_str = ", ".join(can_go_list)
    destination = await _safe_llm_call(
        "go_map",
        [agent_name, home, curr_place, can_go_str, curr_task],
        '只返回目標地點名稱的純文字。',
        home,
    )
    if isinstance(destination, str) and destination in can_go_list:
        return destination
    return home


async def modify_schedule_async(old_sched, now_time, memory, wake_time, role):
    old_sched = old_sched or []
    try:
        old_sched_str = json.dumps(old_sched, ensure_ascii=False)
    except TypeError:
        old_sched_str = str(old_sched)
    parsed_output = await _safe_llm_call(
        "modify_schedule",
        [old_sched_str, now_time, memory, wake_time, role],
        '返回調整後的日程列表，每個元素為[活動名稱, 持續分鐘數]。',
        old_sched,
    )
    if isinstance(parsed_output, list) and all(isinstance(item, (list, tuple)) and len(item) >= 2 for item in parsed_output):
        return _normalize_schedule([list(item[:2]) for item in parsed_output])
    return old_sched


async def summarize_async(memory, now_time, name):
    default_summary = "今天沒有特別的對話。"
    summary = await _safe_llm_call(
        "summarize_chat",
        [memory, now_time, name],
        '只返回簡短的聊天總結。',
        default_summary,
    )
    return summary if isinstance(summary, str) else default_summary


def _run_async(coro):
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        loop = None
    if loop and loop.is_running():
        return asyncio.ensure_future(coro)
    return asyncio.run(coro)


def go_map(agent_name, home, curr_place, can_go_list, curr_task):
    result = _run_async(go_map_async(agent_name, home, curr_place, can_go_list, curr_task))
    if asyncio.isfuture(result):
        raise RuntimeError("go_map() was called inside an active event loop. Please await go_map_async() instead.")
    return result


def modify_schedule(old_sched, now_time, memory, wake_time, role):
    result = _run_async(modify_schedule_async(old_sched, now_time, memory, wake_time, role))
    if asyncio.isfuture(result):
        raise RuntimeError("modify_schedule() was called inside an active event loop. Please await modify_schedule_async() instead.")
    return result


def summarize(memory, now_time, name):
    result = _run_async(summarize_async(memory, now_time, name))
    if asyncio.isfuture(result):
        raise RuntimeError("summarize() was called inside an active event loop. Please await summarize_async() instead.")
    return result

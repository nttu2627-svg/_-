# src/main_quake2.py (強化分片傳送 & 錯誤防護版)

import json
import os
import sys
import traceback
from datetime import datetime, timedelta
import random
import asyncio
import websockets

# ====== 傳輸/長度控制參數 ======
# 單次 WebSocket 文字分片大小（字元數），視前端可承受調整
WS_CHUNK_SIZE = 200_000
# JSON 內長字串欄位截斷長度（字元數）
LONG_TEXT_LIMIT = 8_000
# history/llm log 併接時的總長度上限（字元數）
LOG_TAIL_LIMIT = 50_000

# --- 專案路徑配置 ---
try:
    this_file_path = os.path.abspath(__file__)
    # src_dir 是 .../ai_china_town-master/src/
    src_dir = os.path.dirname(this_file_path)
    # project_root 是 .../ai_china_town-master/
    project_root = os.path.dirname(src_dir)
    if project_root not in sys.path:
        sys.path.insert(0, project_root)
except NameError:
    project_root = os.path.abspath(".")
    if project_root not in sys.path:
        sys.path.insert(0, project_root)
    print("警告: 無法透過 __file__ 自動設定專案路徑，已將當前目錄設為根目錄。")

# --- 模組導入 ---
try:
    from tools.LLM import run_gpt_prompt as llm
    from simulation_logic.agent_classes import TownAgent, Building
    from simulation_logic.event_handler import check_and_handle_phase_transitions
    from simulation_logic.agent_actions import handle_social_interactions, generate_action_instructions
    from simulation_logic.disaster_logger import 災難記錄器
    print("✅ [SUCCESS] 所有核心模組已成功導入。")
    LLM_FUNCTIONS = {
        'double_agents_chat': llm.double_agents_chat,
        'generate_inner_monologue': llm.generate_inner_monologue,
        'run_gpt_prompt_summarize_disaster': llm.run_gpt_prompt_summarize_disaster,
        'run_gpt_prompt_pronunciatio': llm.run_gpt_prompt_pronunciatio,
    }
except ImportError as e:
    print(f"❌ [CRITICAL_ERROR] 導入模組失敗，模擬器無法運行: {e}", file=sys.stderr)
    traceback.print_exc(file=sys.stderr)
    LLM_FUNCTIONS = {}

# --- 全局配置 ---
DEFAULT_HOME_LOCATION = "公寓"

# 根據你專案結構，schedules.json 在 src/data/
SCHEDULE_FILE_PATH = os.path.join(src_dir, "data", "schedules.json")
simulation_agents = []


# ====== 公用工具：安全傳送 / 截斷長欄位 / 取尾端 ======
async def safe_send_text(ws, text: str, chunk_size: int = WS_CHUNK_SIZE):
    """
    將長字串分片傳送到 WebSocket，並對連線關閉做容錯。
    """
    from websockets.exceptions import ConnectionClosed, ConnectionClosedOK, ConnectionClosedError

    if not ws.open:
        return
    for i in range(0, len(text), chunk_size):
        try:
            await ws.send(text[i:i+chunk_size])
        except (ConnectionClosedOK, ConnectionClosedError, ConnectionClosed):
            # 對方已關閉或連線異常，結束傳送即可
            break


async def safe_send_json(ws, data, chunk_size: int = WS_CHUNK_SIZE):
    """
    將資料轉為 JSON 後，以分片方式送出。
    """
    try:
        text = json.dumps(data, ensure_ascii=False)
    except Exception as e:
        # 若序列化失敗，嘗試退化為簡易錯誤包
        text = json.dumps({"type": "error", "message": f"JSON 序列化失敗: {e}"}, ensure_ascii=False)
    await safe_send_text(ws, text, chunk_size=chunk_size)


def _truncate_str(s: str, limit: int = LONG_TEXT_LIMIT) -> str:
    if s is None:
        return s
    if len(s) <= limit:
        return s
    return s[:limit] + f"...(truncated {len(s) - limit} chars)"


def shrink_update(obj, text_limit: int = LONG_TEXT_LIMIT):
    """
    對將要送出的 dict 進行「就地」瘦身，截斷常見長字串欄位，或縮小大型列表。
    """
    if obj is None:
        return obj

    # 常見長字串欄位名稱
    LONG_KEYS = {"content", "reasoning", "raw", "message", "mainLog", "historyLog", "dialogue", "llmLog", "status"}

    def _shrink(x):
        if isinstance(x, dict):
            for k, v in list(x.items()):
                if isinstance(v, str) and (k in LONG_KEYS or len(v) > text_limit):
                    x[k] = _truncate_str(v, text_limit)
                elif isinstance(v, (dict, list)):
                    _shrink(v)
        elif isinstance(x, list):
            # 若列表極大可在此做上限（例如 300 筆）
            MAX_LIST = 300
            if len(x) > MAX_LIST:
                del x[MAX_LIST:]
                x.append(f"...(list truncated, kept first {MAX_LIST})")
            for i, v in enumerate(x):
                if isinstance(v, str) and len(v) > text_limit:
                    x[i] = _truncate_str(v, text_limit)
                elif isinstance(v, (dict, list)):
                    _shrink(v)

    _shrink(obj)
    return obj


def tail_join(lines, sep="\n\n", max_chars: int = LOG_TAIL_LIMIT) -> str:
    """
    僅取 lines 的尾端，拼接後總長不超過 max_chars。
    """
    out = []
    total = 0
    for line in reversed(lines):
        add = (sep if out else "") + line
        if total + len(add) > max_chars:
            break
        out.append(add)
        total += len(add)
    s = "".join(reversed(out))
    # 若被截斷，補上提示
    if len(s) < sum(len(l) for l in lines) + (len(lines) - 1) * len(sep):
        s = f"...(history truncated, showing last ~{max_chars} chars)\n" + s
    return s


# ====== 模擬主流程 ======
async def initialize_and_simulate(params):
    global simulation_agents
    print(f"後端收到來自 Unity 的參數: {json.dumps(params, indent=2, ensure_ascii=False)}")

    # 讀取前端傳來的初始位置（無則用預設）
    initial_positions = params.get('initial_positions', {})

    use_preset = params.get('use_default_calendar', False)
    schedule_mode = 'preset' if use_preset else 'llm'
    print(f"日曆模式已設定為: '{schedule_mode}' (來自 use_default_calendar: {use_preset})")

    total_sim_duration_minutes = params.get('duration', 2400)
    min_per_step_normal_ui = params.get('step', 30)
    start_time_dt = datetime(
        params.get('year', 2024),
        params.get('month', 11),
        params.get('day', 18),
        params.get('hour', 3),
        params.get('minute', 0)
    )
    selected_mbti_list = params.get('mbti', [])
    available_locations = params.get('locations', [])

    if not available_locations:
        yield {"type": "error", "message": "錯誤：Unity 未提供可用的地點列表。"}
        return
    if not LLM_FUNCTIONS:
        yield {"type": "error", "message": "後端LLM模組載入失敗"}
        return

    yield {"type": "status", "message": "後端開始初始化代理人..."}

    agents = []
    for mbti in selected_mbti_list:
        initial_location = initial_positions.get(mbti, DEFAULT_HOME_LOCATION)
        agent = TownAgent(mbti, initial_location, available_locations)
        agents.append(agent)
        print(f"代理人 {mbti} 的初始位置被設定為: {initial_location}")

    simulation_agents = agents

    init_tasks = [agent.initialize_agent(start_time_dt, schedule_mode, SCHEDULE_FILE_PATH) for agent in agents]
    init_results = await asyncio.gather(*init_tasks, return_exceptions=True)

    for i, result in enumerate(init_results):
        if isinstance(result, Exception) or not result:
            yield {"type": "error", "message": f"代理人 {agents[i].name} 初始化失敗: {result}"}
            return

    buildings = {loc: Building(loc, (0, 0)) for loc in available_locations}
    for agent in agents:
        agent.update_current_building(buildings)

    disaster_logger = 災難記錄器()

    def get_full_status(current_buildings):
        return {
            "agentStates": {
                agent.name: {
                    "name": agent.name,
                    "currentState": agent.curr_action,
                    "location": agent.curr_place,
                    "hp": agent.health,
                    "schedule": f"{agent.wake_time} ~ {agent.sleep_time}",
                    "memory": agent.memory,
                    "weeklySchedule": agent.weekly_schedule,
                    "dailySchedule": agent.daily_schedule
                } for agent in agents
            },
            "buildingStates": {
                name: {"id": b.id, "integrity": b.integrity}
                for name, b in current_buildings.items()
            }
        }

    _history_log_buffer, _chat_buffer, _event_log_buffer = [], {}, []

    def format_log(current_time_dt, current_phase, all_asleep=False):
        current_step_log = []
        sim_time_str = current_time_dt.strftime('%Y年%m月%d日 %H點%M分 (%A)')
        current_step_log.append(f"當前時間: {sim_time_str}")
        if current_phase in ["Earthquake", "Recovery"]:
            current_step_log.append(f"--- {current_phase.upper()} ---")
            if _event_log_buffer:
                current_step_log.extend(_event_log_buffer)
                _event_log_buffer.clear()
        elif all_asleep:
            current_step_log.append("所有代理人都在休息中...")
        else:
            for agent in agents:
                pronunciatio = agent.curr_action_pronunciatio
                log_line = f"{agent.name} 當前活動: {agent.curr_action} ({pronunciatio}) --- 所在的地點({agent.curr_place})"
                if agent.curr_action != "聊天" and agent.current_thought:
                    log_line += f"\n  內心想法: 『{_truncate_str(agent.current_thought, LONG_TEXT_LIMIT)}』"
                current_step_log.append(log_line)
            if _chat_buffer:
                for location, dialogue_str in _chat_buffer.items():
                    current_step_log.append(f"\n  在 {location} 的聊天內容: {_truncate_str(dialogue_str, LONG_TEXT_LIMIT)}")
                _chat_buffer.clear()
        current_step_log.append("-" * 60)
        return "\n".join(current_step_log)

    sim_end_time_dt = start_time_dt + timedelta(minutes=int(total_sim_duration_minutes))
    eq_enabled = params.get('eq_enabled', False)
    eq_events_json_str = params.get('eq_json', '[]')
    eq_step_minutes_ui = params.get('eq_step', 5)
    scheduled_events = []

    if eq_enabled:
        print("地震模組已啟用。正在嘗試解析地震事件...")
        try:
            events_data = json.loads(eq_events_json_str)
            print(f"成功解析地震 JSON，找到 {len(events_data)} 個事件設定。")
            for eq_data in events_data:
                event_time = datetime.strptime(eq_data['time'], "%Y-%m-%d-%H-%M")
                scheduled_events.append({
                    'time_dt': event_time,
                    'duration': int(eq_data['duration']),
                    'intensity': float(eq_data.get('intensity', 0.7))
                })
                print(f"✅ 已成功排程地震於: {event_time}")
        except Exception as e:
            error_msg = f"[ERROR] 載入地震事件JSON錯誤: {e}"
            _history_log_buffer.append(error_msg)
            print(f"❌ {error_msg}")
    else:
        print("地震模組未啟用。")

    sim_state = {'phase': "Normal", 'time': start_time_dt, 'next_event_idx': 0, 'eq_enabled': eq_enabled}
    try:
        configured_max_chat = int(params.get('max_chat_groups', 1))
    except (TypeError, ValueError):
        configured_max_chat = 1
    configured_max_chat = max(1, configured_max_chat)

    llm_context = {
        'update_log': lambda msg, lvl: _history_log_buffer.append(f"[{lvl}] {msg}"),
        'chat_buffer': _chat_buffer,
        'event_log_buffer': _event_log_buffer,
        'disaster_logger': disaster_logger,
        'max_chat_groups': configured_max_chat
        }

    while sim_state['time'] < sim_end_time_dt:
        current_time_dt = sim_state['time']
        llm_context['current_time_str'] = current_time_dt.strftime('%H-%M')

        await check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context)

        active_agents = [
            agent for agent in agents
            if agent.health > 0 and not agent.is_asleep(current_time_dt.strftime('%H-%M'))
        ]
        all_asleep = not active_agents and sim_state['phase'] == "Normal"
        llm_context['skip_reasoning'] = all_asleep

        if not all_asleep and sim_state['phase'] in ["Normal", "PostQuakeDiscussion"]:
            update_tasks = []
            # 每日 03:00 正常模式重新產生日程
            if current_time_dt.hour == 3 and current_time_dt.minute == 0 and sim_state['phase'] == "Normal":
                for agent in agents:
                    if agent.health > 0:
                        update_tasks.append(agent.update_daily_schedule(current_time_dt, schedule_mode, SCHEDULE_FILE_PATH))

            for agent in agents:
                update_tasks.append(agent_update_wrapper(agent, active_agents, current_time_dt.strftime('%H-%M')))

            await asyncio.gather(*update_tasks)

            if len(active_agents) > 1:
                await handle_social_interactions(active_agents, llm_context, LLM_FUNCTIONS)
        agent_action_plan = await generate_action_instructions(agents)
        current_log = format_log(current_time_dt, sim_state['phase'], all_asleep)
        _history_log_buffer.append(current_log)

        status_data = get_full_status(buildings)

        # 控制 llmLog / historyLog 長度，避免單筆更新過大
        llm_log_raw = ""
        try:
            llm_log_raw = llm.get_llm_log()
        except Exception:
            llm_log_raw = ""

        update_payload = {
            "type": "update",
            "data": {
                "mainLog": _truncate_str(current_log, LONG_TEXT_LIMIT),
                "historyLog": tail_join(_history_log_buffer, sep="\n\n", max_chars=LOG_TAIL_LIMIT),
                "agentStates": status_data["agentStates"],
                "buildingStates": status_data["buildingStates"],
                "llmLog": _truncate_str(llm_log_raw, LOG_TAIL_LIMIT),
                "status": f"模擬時間: {current_time_dt.strftime('%H:%M:%S')}",
                "agentActions": agent_action_plan,
            }
        }

        # 在產生器層級先瘦身（雙保險）
        shrink_update(update_payload, LONG_TEXT_LIMIT)
        yield update_payload

        # 依階段調整步階
        step_minutes = int(min_per_step_normal_ui)
        if sim_state.get('phase') == "Earthquake":
            step_minutes = int(eq_step_minutes_ui)
        elif sim_state.get('phase') in ["Recovery"]:
            step_minutes = 10

        sim_state['time'] += timedelta(minutes=step_minutes)
        await asyncio.sleep(0.1)

    final_agent_states = {agent.name: {"hp": agent.health} for agent in agents}
    report = disaster_logger.生成報表(final_agent_states)

    print(f"模擬結束，災後評分: {report['scores']}")
    yield {"type": "evaluation", "data": report}
    yield {"type": "end", "message": "模擬結束"}


async def agent_update_wrapper(agent, active_agents, current_time_hm_str):
    if agent in active_agents:
        if agent.last_action in ["睡覺", "Unconscious", "等待初始化"]:
            await agent.set_new_action("醒來", agent.home)

        schedule_item = agent.get_schedule_item_at(current_time_hm_str)
        if schedule_item:
            if isinstance(schedule_item, (list, tuple)):
                new_action = schedule_item[0]
                raw_destination = schedule_item[1] if len(schedule_item) > 1 else schedule_item[0]
            else:
                new_action = schedule_item
                raw_destination = schedule_item

            destination = agent.resolve_destination(new_action, raw_destination)
            if new_action and (agent.curr_action != new_action or agent.target_place != destination):
                await agent.set_new_action(new_action, destination)
    else:
        agent.curr_action = "Unconscious" if agent.health <= 0 else "睡覺"
        lightweight = agent.get_lightweight_response(agent.curr_action)
        if lightweight:
            agent.current_thought, agent.curr_action_pronunciatio = lightweight
        else:
            agent.current_thought = ""
            agent.curr_action_pronunciatio = await agent.get_pronunciatio(agent.curr_action)

    agent.last_action = agent.curr_action


async def handler(websocket, path):
    print(f"Unity客戶端已連接: {websocket.remote_address}")
    from websockets.exceptions import ConnectionClosed, ConnectionClosedOK, ConnectionClosedError

    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                command_type = data.get("command")

                if command_type == "start_simulation":
                    print("收到來自Unity的開始模擬指令...")
                    async for update_data in initialize_and_simulate(data['params']):
                        # 每筆更新先瘦身，再安全分片送出
                        shrink_update(update_data, LONG_TEXT_LIMIT)
                        if not websocket.open:
                            break
                        await safe_send_json(websocket, update_data, chunk_size=WS_CHUNK_SIZE)

                elif command_type == "agent_teleport":
                    agent_name = data.get("agent_name")
                    target_portal_name = data.get("target_portal_name")
                    agent_to_teleport = next((agent for agent in simulation_agents if agent.name == agent_name), None)
                    if agent_to_teleport and target_portal_name:
                        agent_to_teleport.teleport(target_portal_name)

            except (ConnectionClosedOK, ConnectionClosedError, ConnectionClosed):
                # 對方關閉或連線錯誤，結束本 handler
                break
            except Exception as e:
                print(f"處理消息時發生錯誤: {e}")
                traceback.print_exc()
                # 若連線仍開啟，回傳錯誤資訊並結束
                if not websocket.closed:
                    await safe_send_json(websocket, {"type": "error", "message": f"後端錯誤: {str(e)}"})
                break

    except (ConnectionClosedOK, ConnectionClosedError, ConnectionClosed) as e:
        close_code = getattr(e, "code", None)
        close_reason = getattr(e, "reason", "")

        if close_code or close_reason:
            print(f"Unity客戶端斷開連接: {websocket.remote_address}, 原因: {close_reason or close_code}")
        else:
            print(f"Unity客戶端斷開連接: {websocket.remote_address}, 原因: {e}")
    finally:
        print("伺服器處理程序結束。")


async def main():
    # 若 LLM 初始化失敗，直接退出
    if not await llm.initialize_llm():
        print("LLM 初始化失敗，程式退出。")
        return
    # 關鍵：提高/取消訊息大小限制 + 開壓縮，避免大包就被斷線，並取消 ping 限制防止閒置斷線
    server = await websockets.serve(
        handler, "localhost", 8765,
        max_size=None,                # 取消預設 1MiB 限制
        compression='deflate',        # 啟用 permessage-deflate
        max_queue=64,
        ping_interval=None,
        ping_timeout=None
    )

    print(f"WebSocket 伺服器正在監聽 ws://localhost:8765")
    try:
        await server.wait_closed()
    finally:
        await llm.close_llm_session()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("伺服器被手動關閉。")

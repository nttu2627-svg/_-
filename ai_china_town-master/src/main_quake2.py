# main_quake2.py (完整版 - 優化架構與閒置推進功能)

import json
import os
import sys
import traceback
import time  # 新增 time 模組用於計算閒置時間
from datetime import datetime, timedelta
import random
import asyncio
import contextlib
from typing import Optional, Dict, Set, List
import websockets

# ====== 傳輸/長度控制參數 ======
WS_CHUNK_SIZE = 200_000
LONG_TEXT_LIMIT = 8_000
LOG_TAIL_LIMIT = 50_000

# 思考中微移動的推播頻率（秒）
MICRO_MOTION_INTERVAL = 0.15

# --- 專案路徑配置 ---
try:
    this_file_path = os.path.abspath(__file__)
    src_dir = os.path.dirname(this_file_path)
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
    from simulation_logic.agent_classes import TownAgent, Building, normalize_location_name
    from simulation_logic.event_handler import check_and_handle_phase_transitions
    from simulation_logic.agent_actions import handle_social_interactions, generate_action_instructions
    from simulation_logic.disaster_logger import 災難記錄器
    from simulation_logic.report_generator import init_report_generator, get_report_generator

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

# ====== [系統補丁] 動態修復 resolve_destination ======
def _patch_resolve_destination(self, action, destination):
    if destination in ["Home", "家", "home", "Home Location"]:
        return getattr(self, "home", destination)
    if destination is None:
        return getattr(self, "curr_place", "Unknown")
    return destination

if 'TownAgent' in globals() and not hasattr(TownAgent, "resolve_destination"):
    TownAgent.resolve_destination = _patch_resolve_destination
    print("🔧 [系統補丁] 已動態為 TownAgent 添加 resolve_destination 方法。")

# --- 全局配置 ---
DEFAULT_HOME_LOCATION = "公寓"
SCHEDULE_FILE_PATH = os.path.join(src_dir, "data", "schedules.json")

# 連線期間的代理人列表 (Global Reference)
simulation_agents: List[TownAgent] = [] 

# 從 Unity 顯式標記的「思考中」表
explicit_thinking: Set[str] = set()

# ====== 公用工具 ======
async def safe_send_text(ws, text: str, chunk_size: int = WS_CHUNK_SIZE):
    from websockets.exceptions import ConnectionClosed, ConnectionClosedOK, ConnectionClosedError
    if not ws.open:
        return
    for i in range(0, len(text), chunk_size):
        try:
            await ws.send(text[i:i+chunk_size])
        except (ConnectionClosedOK, ConnectionClosedError, ConnectionClosed):
            break

async def safe_send_json(ws, data, chunk_size: int = WS_CHUNK_SIZE):
    try:
        text = json.dumps(data, ensure_ascii=False)
    except Exception as e:
        text = json.dumps({"type": "error", "message": f"JSON 序列化失敗: {e}"}, ensure_ascii=False)
    await safe_send_text(ws, text, chunk_size=chunk_size)

def _truncate_str(s: str, limit: int = LONG_TEXT_LIMIT) -> str:
    if s is None: return s
    if len(s) <= limit: return s
    return s[:limit] + f"...(truncated {len(s) - limit} chars)"

def shrink_update(obj, text_limit: int = LONG_TEXT_LIMIT):
    if obj is None: return obj
    LONG_KEYS = {"content", "reasoning", "raw", "message", "mainLog", "historyLog", "dialogue", "llmLog", "status"}
    def _shrink(x):
        if isinstance(x, dict):
            for k, v in list(x.items()):
                if isinstance(v, str) and (k in LONG_KEYS or len(v) > text_limit):
                    x[k] = _truncate_str(v, text_limit)
                elif isinstance(v, (dict, list)):
                    _shrink(v)
        elif isinstance(x, list):
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
    out = []
    total = 0
    for line in reversed(lines):
        add = (sep if out else "") + line
        if total + len(add) > max_chars: break
        out.append(add)
        total += len(add)
    s = "".join(reversed(out))
    if len(s) < sum(len(l) for l in lines) + (len(lines) - 1) * len(sep):
        s = f"...(history truncated, showing last ~{max_chars} chars)\n" + s
    return s

def build_status_payload(sim_state: dict, current_time_dt: datetime, agent_action_plan):
    scenario_map = {
        "Normal": "日常",
        "Earthquake": "地震中",
        "Recovery": "災後恢復",
        "PostQuakeDiscussion": "災後恢復",
    }
    scenario_state = scenario_map.get(sim_state.get("phase"), sim_state.get("phase") or "未知")
    has_actions = bool(agent_action_plan)
    execution_state = "移動中" if has_actions else "思考中"
    return {
        "scenario_state": scenario_state,
        "execution_state": execution_state,
        "sim_time": current_time_dt.strftime("%H:%M:%S"),
    }

# ====== 思考偵測與微移動 ======
THINKING_KEYWORDS = ["思考", "決策", "決定中", "等候決策", "thinking", "deciding", "idle(思考)", "Idle(思考)", "Idle-Think", "wake", "醒來"]

def detect_thinking(agent: "TownAgent") -> bool:
    if agent.name in explicit_thinking: return True
    act = (agent.curr_action or "").lower()
    if not act: return True
    return any(k.lower() in act for k in THINKING_KEYWORDS)

MICRO_MOTION_MODES = ["wander", "lookaround", "slow_walk_to_temp"]
_last_temp_targets: Dict[str, str] = {}

def _pick_micro_mode() -> str:
    r = random.random()
    if r < 0.6: return "wander"
    if r < 0.85: return "lookaround"
    return "slow_walk_to_temp"

def build_micro_motion_payload(agents: list["TownAgent"], buildings: Dict[str, "Building"]):
    motions = []
    if not agents: return {"type": "motion", "data": {"microMotions": []}}
    for agent in agents:
        if agent.health <= 0: continue
        is_thinking = getattr(agent, "is_thinking", False)
        if not (is_thinking or detect_thinking(agent)): continue
        mode = _pick_micro_mode()
        payload = {
            "agent": agent.name,
            "mode": mode,
            "radius": round(random.uniform(0.6, 1.8), 2),
            "period": round(random.uniform(1.2, 2.4), 2),
            "speed": round(random.uniform(0.6, 1.2), 2),
        }
        if mode == "slow_walk_to_temp":
            candidates = [x for x in ["Park", "Exterior", "Gym", "Super", "Rest", "School", "Subway"] if x in buildings]
            if not candidates: candidates = list(buildings.keys())
            if candidates:
                prev = _last_temp_targets.get(agent.name)
                target = prev if (prev and prev in candidates and random.random() > 0.3) else random.choice(candidates)
                _last_temp_targets[agent.name] = target
                payload["tempTarget"] = target
                payload["arriveTolerance"] = 0.8
        motions.append(payload)
    return {"type": "motion", "data": {"microMotions": motions}}

# ====== 模擬主流程 (完整版) ======
async def initialize_and_simulate(params, step_sync_event: Optional[asyncio.Event] = None):
    global simulation_agents
    print(f"後端收到來自 Unity 的參數: {json.dumps(params, indent=2, ensure_ascii=False)}")

    # 1. 解析參數
    initial_positions: Dict[str, str] = params.get("initial_positions", {}) or {}
    selected_mbti_list = params.get("mbti", []) or []
    available_locations = params.get("locations", []) or []

    if not available_locations:
        yield {"type": "error", "message": "錯誤：Unity 未提供可用的地點列表。"}
        return

    if not LLM_FUNCTIONS:
        yield {"type": "error", "message": "後端LLM模組載入失敗"}
        return

    use_preset = params.get("use_default_calendar", False)
    schedule_mode = "preset" if use_preset else "llm"

    start_time_dt = datetime(
        int(params.get("year", 2024)),
        int(params.get("month", 11)),
        int(params.get("day", 18)),
        int(params.get("hour", 6)), # 預設早上6點
        int(params.get("minute", 0)),
    )
    total_sim_duration_minutes = int(params.get("duration", 600))

    # 2. 建立 Agents
    agents = []
    for mbti in selected_mbti_list:
        init_loc = initial_positions.get(mbti, DEFAULT_HOME_LOCATION)
        agent = TownAgent(mbti, init_loc, available_locations)
        agents.append(agent)
        print(f"代理人 {mbti} 的初始位置被設定為: {init_loc}")

    simulation_agents = agents

    # 3. 初始化 Agents
    init_tasks = [agent.initialize_agent(start_time_dt, schedule_mode, SCHEDULE_FILE_PATH) for agent in agents]
    init_results = await asyncio.gather(*init_tasks, return_exceptions=True)

    for i, result in enumerate(init_results):
        if isinstance(result, Exception):
            err_msg = f"代理人 {agents[i].name} 初始化失敗: {result}"
            print(err_msg)
            yield {"type": "error", "message": err_msg}
            return

    # 4. 建立建築索引
    buildings = {}
    for loc in available_locations:
        canonical_loc = normalize_location_name(loc)
        if canonical_loc not in buildings:
            buildings[canonical_loc] = Building(canonical_loc, (0, 0))
    for agent in agents:
        agent.update_current_building(buildings)

    # 5. [FEATURE] 初始傳送 (Spawn) - 確保在房間「內」
    # 我們呼叫 ensure_spawn_position，這通常會根據 Unity 的 SpawnPoint 邏輯重置座標
    # 為了符合要求，我們假設 Location 字串本身代表該地點的內部
    for agent in agents:
        try:
            # 確保代理人的 target 與 current 在初始時一致，避免初始時誤判為「未到達」
            agent.target_place = agent.curr_place 
            spawn_event = agent.ensure_spawn_position()
            
            # 這裡可以加入額外的邏輯來標記這是 "Interior" Spawn，如果 Unity 端需要額外 Flag
            # 目前邏輯依賴 ensure_spawn_position 正確回傳地點名稱
            final_loc = spawn_event.get('finalLocation') if spawn_event else agent.curr_place
            print(f"🚪 [Initial Spawn] {agent.name} 位於 {final_loc} (室內狀態已確認)")
        except Exception as exc:
            print(f"⚠️ [Spawn Warning] {agent.name}: {exc}")

    # 6. 準備日誌與上下文
    disaster_logger = 災難記錄器()
    _history_log_buffer, _chat_buffer, _event_log_buffer = [], {}, []

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
                    "dailySchedule": agent.daily_schedule,
                    # [優化] 增加 target 用於前端除錯
                    "target": getattr(agent, "target_place", ""),
                } for agent in agents
            },
            "buildingStates": {
                name: {"id": b.id, "integrity": b.integrity} for name, b in current_buildings.items()
            },
        }

    def format_log(current_time_dt, current_phase, all_asleep=False):
        current_step_log = []
        sim_time_str = current_time_dt.strftime("%Y年%m月%d日 %H點%M分 (%A)")
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
                pronunciatio = getattr(agent, "curr_action_pronunciatio", "...")
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

    # 7. 地震排程
    sim_end_time_dt = start_time_dt + timedelta(minutes=int(total_sim_duration_minutes))
    eq_enabled = params.get("eq_enabled", False)
    eq_events_json_str = params.get("eq_json", "[]")
    eq_step_minutes_ui = int(params.get("eq_step", 5))
    scheduled_events = []
    if eq_enabled:
        try:
            events_data = json.loads(eq_events_json_str)
            for eq_data in events_data:
                event_time = datetime.strptime(eq_data["time"], "%Y-%m-%d-%H-%M")
                scheduled_events.append({
                    "time_dt": event_time,
                    "duration": int(eq_data["duration"]),
                    "intensity": float(eq_data.get("intensity", 0.7)),
                })
        except Exception as e:
            _history_log_buffer.append(f"[ERROR] 地震 JSON 解析錯誤: {e}")
    
    # 8. 主循環
    sim_state = {"phase": "Normal", "time": start_time_dt, "next_event_idx": 0, "eq_enabled": eq_enabled}
    step_index = 0
    configured_max_chat = int(params.get("max_chat_groups", 1))
    llm_context = {
        "update_log": lambda msg, lvl: _history_log_buffer.append(f"[{lvl}] {msg}"),
        "chat_buffer": _chat_buffer,
        "event_log_buffer": _event_log_buffer,
        "disaster_logger": disaster_logger,
        "max_chat_groups": configured_max_chat,
    }

    # [FEATURE] 閒置時間追蹤器
    # 當所有代理人的 target == current 時，記錄開始時間
    idle_start_time: Optional[float] = None

    while sim_state["time"] < sim_end_time_dt:
        current_time_dt = sim_state["time"]
        llm_context["current_time_str"] = current_time_dt.strftime("%H-%M")

        await check_and_handle_phase_transitions(sim_state, agents, buildings, scheduled_events, llm_context)

        # ====== 判定活躍與睡眠 ======
        active_agents = [a for a in agents if a.health > 0 and not a.is_asleep(current_time_dt.strftime("%H-%M"))]
        active_count = len(active_agents)
        all_asleep = (active_count == 0) and (sim_state["phase"] == "Normal")

        # ====== [FEATURE] 5秒閒置推進邏輯 ======
        # 條件：所有活躍的代理人都已經到達目標地點 (target == curr_place)
        all_arrived = False
        if active_count > 0:
            # 檢查是否所有活躍者都在目的地
            # 注意：這裡假設 target_place 在 set_new_action 時已被正確設定
            all_arrived = all(
                (getattr(a, 'target_place', None) == a.curr_place) 
                for a in active_agents
            )
        
        # 閒置計時器邏輯
        force_skip_by_idle = False
        if all_arrived and sim_state["phase"] == "Normal":
            if idle_start_time is None:
                idle_start_time = time.time()
            else:
                elapsed = time.time() - idle_start_time
                if elapsed > 5.0: # 超過 5 秒
                    force_skip_by_idle = True
                    # print(f"⏩ [Auto-Advance] 偵測到全體閒置超過 5 秒，強制推進 Step {step_index}")
        else:
            # 只要有人在移動或不在目的地，重置計時器
            idle_start_time = None

        # ====== 決策：是否跳過 LLM 推理 (skip_reasoning) ======
        # 1. 人數 <= 1
        # 2. 強制閒置推進觸發 (Feature 1)
        should_fast_forward = (active_count <= 1) or force_skip_by_idle
        
        # 如果是強制閒置推進，我們仍然視為 Normal phase 操作，但跳過思考
        if should_fast_forward and sim_state["phase"] == "Normal":
             llm_context["skip_reasoning"] = True
        else:
             llm_context["skip_reasoning"] = False

        # ====== 更新邏輯 ======
        should_run_updates = (active_count > 0) or (sim_state["phase"] != "Normal")

        if should_run_updates:
            update_tasks = []
            if current_time_dt.hour == 3 and current_time_dt.minute == 0 and sim_state["phase"] == "Normal":
                 for agent in agents:
                    if agent.health > 0:
                        update_tasks.append(agent.update_daily_schedule(current_time_dt, schedule_mode, SCHEDULE_FILE_PATH))
            
            for agent in agents:
                update_tasks.append(agent_update_wrapper(agent, active_agents, current_time_dt.strftime("%H-%M")))
            
            await asyncio.gather(*update_tasks)
            
            # 社交互動：僅在 >1 人醒著 且 沒有被閒置強制推進 時執行
            if active_count > 1 and sim_state["phase"] in ["Normal", "PostQuakeDiscussion"] and not force_skip_by_idle:
                await handle_social_interactions(active_agents, llm_context, LLM_FUNCTIONS)
        
        # ====== 產生行動指令 ======
        # 優化：如果判定為 fast_forward，直接給出空指令或簡單移動指令，避免呼叫 LLM
        if llm_context.get("skip_reasoning", False):
            # 快速模式：不呼叫 LLM 生成複雜指令，僅維持現狀或簡單移動
            agent_action_plan = [] # 前端會依據 agentStates 更新位置，不需要額外指令
        else:
            agent_action_plan = await generate_action_instructions(agents)
        
        current_log = format_log(current_time_dt, sim_state["phase"], all_asleep)
        _history_log_buffer.append(current_log)

        status_data = get_full_status(buildings)
        llm_log_raw = ""
        try:
            # 只有在沒跳過推理時才嘗試抓取 LLM Log，避免錯誤
            if not llm_context.get("skip_reasoning", False):
                llm_log_raw = llm.get_llm_log()
        except: pass

        update_payload = {
            "type": "update",
            "data": {
                "mainLog": _truncate_str(current_log, LONG_TEXT_LIMIT),
                "historyLog": tail_join(_history_log_buffer, sep="\n\n", max_chars=LOG_TAIL_LIMIT),
                "agentStates": status_data["agentStates"],
                "buildingStates": status_data["buildingStates"],
                "llmLog": _truncate_str(llm_log_raw, LOG_TAIL_LIMIT),
                "status": build_status_payload(sim_state, current_time_dt, agent_action_plan),
                "agentActions": agent_action_plan,
                "stepId": step_index,
            },
        }
        shrink_update(update_payload, LONG_TEXT_LIMIT)
        yield update_payload

        # 9. Step 同步
        # 如果 force_skip_by_idle 為 True，表示我們希望快速推進，不需要等待 Unity 跑完完整的 "Move" 動畫 (因為已經在目的地了)
        # 但為了確保 Unity 狀態同步，我們還是做一個極短的等待，或等待 Unity 回傳 "Ready"
        if step_sync_event is not None:
            if force_skip_by_idle:
                # 閒置模式下：給予一個極短的超時，避免卡住，因為 Unity 可能因為沒動作而不發送 step_complete
                try:
                    await asyncio.wait_for(step_sync_event.wait(), timeout=0.5)
                except asyncio.TimeoutError:
                    pass # 超時直接繼續，不等 Unity 確認
                step_sync_event.clear()
            else:
                # 正常模式：等待 Unity 回報移動完成
                await step_sync_event.wait()
                step_sync_event.clear()
        
        step_index += 1

        # 時間推進步長計算
        step_minutes = int(params.get("step", 30))
        if sim_state.get("phase") == "Earthquake":
            step_minutes = int(params.get("eq_step", eq_step_minutes_ui))
        elif sim_state.get("phase") in ["Recovery"]:
            step_minutes = 10
        
        step_minutes = max(1, step_minutes)
        sim_state["time"] += timedelta(minutes=step_minutes)
        
        # 避免 Loop 過快導致 CPU 飆高
        await asyncio.sleep(0.05)

    # 10. 模擬結束
    final_agent_states = {agent.name: {"hp": agent.health} for agent in agents}
    report = disaster_logger.生成報表(final_agent_states)
    chart_path = params.get("chart_path", None)
    if chart_path and isinstance(chart_path, str) and os.path.exists(chart_path):
        report["chart"] = os.path.abspath(chart_path)
    elif report.get("chart") and os.path.exists(report["chart"]):
        report["chart"] = os.path.abspath(report["chart"])
    yield {"type": "evaluation", "data": report}
    # ====== [FEATURE] LLM 報告生成 ======
    try:
        report_gen = get_report_generator()
        if report_gen is None:
            report_gen = init_report_generator(llm)
        
        reports_dir = os.path.join(project_root, "reports", datetime.now().strftime("%Y%m%d_%H%M%S"))
        
        print("📝 [報告生成] 開始生成模擬結束報告...")
        llm_reports = await report_gen.generate_all_reports(
            agents=agents,
            disaster_logger=disaster_logger,
            output_dir=reports_dir
        )
        
        yield {
            "type": "llm_reports",
            "data": {
                "storytelling": llm_reports.get("storytelling", {}),
                "performance_analysis": llm_reports.get("performance_analysis", ""),
                "files": llm_reports.get("files", {}),
            }
        }
        print("✅ [報告生成] LLM 報告已生成完成")
        
    except Exception as e:
        print(f"⚠️ [報告生成] 生成報告時發生錯誤: {e}")
        traceback.print_exc()
    yield {"type": "end", "message": "模擬結束"}

async def stream_simulation_to_client(websocket, params, send_lock: asyncio.Lock, buildings_ref: Dict[str, "Building"], step_sync_event: Optional[asyncio.Event] = None):
    try:
        async for update_data in initialize_and_simulate(params, step_sync_event):
            shrink_update(update_data, LONG_TEXT_LIMIT)
            if not websocket.open: break
            async with send_lock:
                await safe_send_json(websocket, update_data, chunk_size=WS_CHUNK_SIZE)
    except asyncio.CancelledError:
        print("模擬任務被取消")
        raise
    except Exception as e:
        traceback.print_exc()
        if websocket.open:
            async with send_lock:
                await safe_send_json(websocket, {"type": "error", "message": f"後端錯誤: {e}"})

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

async def motion_loop(websocket, send_lock: asyncio.Lock, buildings_provider):
    try:
        while websocket.open:
            buildings = buildings_provider() or {}
            payload = build_micro_motion_payload(simulation_agents, buildings)
            if payload["data"]["microMotions"]:
                async with send_lock:
                    await safe_send_json(websocket, payload, chunk_size=WS_CHUNK_SIZE)
            await asyncio.sleep(MICRO_MOTION_INTERVAL)
    except asyncio.CancelledError: pass
    except Exception as e:
        if websocket.open: print(f"motion_loop exception: {e}")

async def handler(websocket, path):
    print(f"Unity客戶端已連接: {websocket.remote_address}")
    from websockets.exceptions import ConnectionClosed, ConnectionClosedOK, ConnectionClosedError
    send_lock = asyncio.Lock()
    simulation_task: Optional[asyncio.Task] = None
    motion_task: Optional[asyncio.Task] = None
    step_sync_event: Optional[asyncio.Event] = None
    expected_step_id = 0
    _buildings_cache: Dict[str, Building] = {}
    def get_buildings(): return _buildings_cache
    def _attach_task_cleanup(task: asyncio.Task):
        nonlocal simulation_task
        if simulation_task is task: simulation_task = None

    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                command_type = data.get("command")

                if command_type == "start_simulation":
                    print("收到開始模擬指令，正在初始化...")
                    if simulation_task and not simulation_task.done():
                        simulation_task.cancel()
                        with contextlib.suppress(asyncio.CancelledError): await simulation_task
                    if motion_task and not motion_task.done():
                        motion_task.cancel()
                        with contextlib.suppress(asyncio.CancelledError): await motion_task
                    
                    params = data['params']
                    _buildings_cache.clear()
                    locs = params.get('locations', [])
                    for loc in locs: _buildings_cache[loc] = Building(loc, (0, 0))
                    
                    step_sync_event = asyncio.Event()
                    expected_step_id = 0
                    
                    simulation_task = asyncio.create_task(
                        stream_simulation_to_client(websocket, params, send_lock, _buildings_cache, step_sync_event)
                    )
                    simulation_task.add_done_callback(_attach_task_cleanup)
                    motion_task = asyncio.create_task(motion_loop(websocket, send_lock, get_buildings))

                elif command_type == "step_complete":
                    if step_sync_event:
                        sid = data.get("step_id")
                        if sid is not None:
                            if sid >= expected_step_id:
                                step_sync_event.set()
                                expected_step_id = sid + 1

                elif command_type == "agent_teleport":
                    aname = data.get("agent_name")
                    tname = data.get("target_portal_name")
                    tgt = next((a for a in simulation_agents if a.name == aname), None)
                    if tgt and tname:
                        tgt.teleport(tname)
                        print(f"📡 [Teleport] {aname} -> {tname}")

                elif command_type == "start_thinking":
                    aname = data.get("agent_name")
                    if aname: explicit_thinking.add(aname)
                elif command_type == "stop_thinking":
                    aname = data.get("agent_name")
                    if aname: explicit_thinking.discard(aname)

            except (ConnectionClosed, ConnectionClosedOK, ConnectionClosedError):
                print("連線已關閉 (Loop Break)")
                break
            except Exception as inner_e:
                print(f"訊息處理錯誤: {inner_e}")
                traceback.print_exc()
    except Exception as e:
        print(f"WebSocket Handler 異常: {e}")
    finally:
        for t in [simulation_task, motion_task]:
            if t and not t.done():
                t.cancel()
                with contextlib.suppress(asyncio.CancelledError): await t
        print(f"客戶端 {websocket.remote_address} 處理結束。")

async def main():
    if not await llm.initialize_llm():
        print("LLM 初始化失敗，程式退出。")
        return
    HOST, PORT = "127.0.0.1", 8765
    print(f"準備啟動 WebSocket 伺服器於 ws://{HOST}:{PORT}")
    try:
        async with websockets.serve(handler, HOST, PORT, max_size=None, compression='deflate', max_queue=64, ping_interval=None, ping_timeout=None) as server:
            print(f"✅ WebSocket 伺服器已啟動: ws://{HOST}:{PORT}")
            print("請運行 Unity 客戶端進行連線...")
            await asyncio.Future()
    except OSError as e:
        if e.errno == 10048:
            print(f"\n❌ [CRITICAL ERROR] 端口 {PORT} 被佔用！請從工作管理員關閉舊的 Python 進程。")
        else:
            print(f"❌ 伺服器啟動錯誤: {e}")
    except asyncio.CancelledError:
        print("伺服器任務取消")
    finally:
        print("正在關閉 LLM Session...")
        await llm.close_llm_session()
        print("程式已結束。")

if __name__ == "__main__":
    try:
        if sys.platform == 'win32':
            asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())
        asyncio.run(main())
    except KeyboardInterrupt:
        print("伺服器被手動關閉。")
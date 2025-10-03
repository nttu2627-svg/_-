# -*- coding: utf-8 -*-
"""
Unity socket bridge (cleaned for current Unity front-end)
- Removes legacy town map & Gradio UI
- Uses current scene/building/portal names:
  Apartment_F1 / Apartment_F2 / School / Rest / Gym / Super / Subway / Exterior
- "Apartment" target defaults to F1
- Safe LLM loader to avoid OllamaAgent __init__ arg mismatch during import
"""

import argparse
import asyncio
import json
import logging
import math
import os
import random
import socket
import time
from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from sklearn.cluster import DBSCAN

# ---------------------------
# Logging
# ---------------------------
logging.basicConfig(level=logging.INFO, format='[%(levelname)s] %(message)s')
logger = logging.getLogger("unity_socket")

# ---------------------------
# Unity socket config
# ---------------------------
UNITY_IP = "127.0.0.1"
UNITY_PORT = 12345

# ---------------------------
# Current Unity scene markers (anchors) & portals
# ---------------------------
UNITY_LOCATION_MARKERS: Dict[str, Dict[str, Any]] = {
    "Apartment_F1": {"anchor": (-83.5, -50.6), "aliases": ["Apartment", "公寓", "公寓一樓", "公寓F1"]},
    "Apartment_F2": {"anchor": (-184.7, -57.0), "aliases": ["公寓二樓", "Apartment_Floor2"]},
    "School":       {"anchor": (-1.0, -109.7), "aliases": ["學校", "教室", "校園", "校园"]},
    "Rest":         {"anchor": (-98.0, 10.5),   "aliases": ["餐廳", "餐厅", "咖啡店", "Cafe", "Restaurant"]},
    "Gym":          {"anchor": (-86.8, 42.9),   "aliases": ["健身房", "Gymnasium"]},
    "Super":        {"anchor": (52.2, 92.9),    "aliases": ["超市", "商場", "商场", "便利店"]},
    "Subway":       {"anchor": (166.7, -97.1),  "aliases": ["地鐵", "地铁", "Metro"]},
    "Exterior":     {"anchor": (174.8, 1.9),    "aliases": ["室外", "戶外", "户外", "公園", "Park"]},
}

# Apartment 別名預設導向 F1
PORTAL_NAME_ALIASES = {
    "公寓": "Apartment_F1",
    "公寓F1": "Apartment_F1",
    "公寓F2": "Apartment_F2",
    "Apartment": "Apartment_F1",
    "Apartment_Floor2": "Apartment_F2",
    "公寓二樓": "Apartment_F2",
    "公寓一樓": "Apartment_F1",
}

RAW_PORTAL_MARKERS: List[Dict[str, Any]] = [
    {"name": "健身房_室內", "position": (-66.92, 17.73), "targets": ["健身房_室外"]},
    {"name": "健身房_室外", "position": (97.5, 15.17), "targets": ["健身房_室內"]},
    {"name": "公寓一樓_室內", "position": (-67.92, -13.82), "targets": ["公寓二樓_室內"]},
    {"name": "公寓二樓_室內", "position": (-117.08, -46.82), "targets": ["公寓頂樓_室內", "公寓一樓_室內"]},
    {"name": "公寓側門_室內", "position": (-57.92, -44.995003), "targets": ["公寓側門_室外"]},
    {"name": "公寓側門_室外", "position": (6.06, -10.34), "targets": ["公寓側門_室內"]},
    {"name": "公寓大門_室內", "position": (-77.008, -44.995003), "targets": ["公寓大門_室外"]},
    {"name": "公寓大門_室外", "position": (-3.4, -9.01), "targets": ["公寓大門_室內"]},
    {"name": "公寓頂樓_室內", "position": (-117.08, -13.62), "targets": ["公寓頂樓_室外", "公寓二樓_室內"]},
    {"name": "公寓頂樓_室外", "position": (-2.4, 4.42), "targets": ["公寓頂樓_室內"]},
    {"name": "地鐵上入口_室外", "position": (42.46, -30.38), "targets": ["地鐵左樓梯_室內"]},
    {"name": "地鐵下入口_室外", "position": (42.46, -36.45), "targets": ["地鐵右樓梯_室內"]},
    {"name": "地鐵右入口_室外", "position": (45.46, -33.47), "targets": ["地鐵右樓梯_室內"]},
    {"name": "地鐵右樓梯_室內", "position": (78.03999, -32.58), "targets": ["地鐵右入口_室外", "地鐵下入口_室外"]},
    {"name": "地鐵左入口_室外", "position": (39.4, -33.5), "targets": ["地鐵左樓梯_室內"]},
    {"name": "地鐵左樓梯_室內", "position": (55.970005, -48.980003), "targets": ["地鐵左入口_室外", "地鐵上入口_室外"]},
    {"name": "學校門口_室內", "position": (-26.504, -63.017), "targets": ["學校門口_室外"]},
    {"name": "學校門口_室外", "position": (106.4, -33.0), "targets": ["學校門口_室內"]},
    {"name": "超市側門_室內", "position": (8.98, 55.15), "targets": ["超市側門_室外"]},
    {"name": "超市側門_室外", "position": (12.1, 19.830002), "targets": ["超市側門_室內"]},
    {"name": "超市右門_室內", "position": (5.98, 38.07), "targets": ["超市右門_室外"]},
    {"name": "超市左門_室內", "position": (-3.91, 38.07), "targets": ["超市左門_室外"]},
    {"name": "超市左門_室外", "position": (1.87, 15.88), "targets": ["超市左門_室內"]},
    {"name": "超市左門_室外", "position": (8.03, 15.88), "targets": ["超市左門_室內"]},
    {"name": "餐廳_室內", "position": (-73.00139, 0.972929), "targets": ["餐廳_室外"]},
    {"name": "餐廳_室外", "position": (96.95, -5.1), "targets": ["餐廳_室內"]},
]

def _build_coordinate_lookup():
    coordinate_map: Dict[str, Any] = {}
    alias_map: Dict[str, str] = {}

    # 主地點 anchors
    for name, meta in UNITY_LOCATION_MARKERS.items():
        anchor = meta.get("anchor")
        if anchor:
            coordinate_map[name] = (float(anchor[0]), float(anchor[1]))
        for alias in meta.get("aliases", []):
            alias_map[alias] = name

    # 傳送門
    portal_map: Dict[str, Dict[str, Any]] = {}
    for entry in RAW_PORTAL_MARKERS:
        pos = entry["position"]
        anchor = (round(float(pos[0]), 4), round(float(pos[1]), 4))
        payload = portal_map.setdefault(entry["name"], {"anchors": [], "targets": set()})
        if anchor not in payload["anchors"]:
            payload["anchors"].append(anchor)
        payload["targets"].update(entry.get("targets", []))

    # 別名
    for alias, canonical in PORTAL_NAME_ALIASES.items():
        alias_map[alias] = canonical

    # 整理 portal anchors 與 alias 對應
    for name, payload in portal_map.items():
        anchors = payload["anchors"]
        coordinate_map[name] = anchors[0] if len(anchors) == 1 else anchors
        payload["targets"] = sorted(payload["targets"])

    for alias, canonical in alias_map.items():
        if canonical in coordinate_map:
            coordinate_map[alias] = coordinate_map[canonical]

    return coordinate_map, portal_map

COORDINATE_MAP, UNITY_PORTAL_MARKERS = _build_coordinate_lookup()
CAN_GO_PLACES: List[str] = list(UNITY_LOCATION_MARKERS.keys())

ENVIRONMENT_OBJECTS = {
    "Apartment_F1": ["床", "沙發", "書桌"],
    "Apartment_F2": ["床", "書架", "陽台椅"],
    "School": ["黑板", "課桌椅", "講台"],
    "Rest": ["咖啡機", "甜點櫃", "沙發椅"],
    "Gym": ["啞鈴", "跑步機", "瑜珈墊"],
    "Super": ["貨架", "收銀台", "購物籃"],
    "Subway": ["售票機", "候車椅", "路線圖"],
    "Exterior": ["長椅", "路燈", "噴泉"],
}

# ---------------------------
# Default agents (names = MBTI / or your own agent folder names)
# ---------------------------
DEFAULT_AGENT_ORDER = ["ISTJ", "ISFJ", "INFJ"]
DEFAULT_AGENT_HOMES = {"ISTJ": "Apartment_F1", "ISFJ": "Apartment_F1", "INFJ": "Apartment_F2"}

# ---------------------------
# Unity TCP helpers
# ---------------------------
def send_move_command(ip: str, port: int, object_positions: List[Tuple[int, float, float]], delay: float = 0.5):
    try:
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))
        command = "MOVE:" + ";".join([f"{oid},{x},{y}" for oid, x, y in object_positions])
        client.sendall(command.encode("utf-8"))
        logger.debug("Sent: %s", command)
        client.close()
        if delay > 0:
            time.sleep(delay)
    except Exception as e:
        logger.error("send_move_command error: %s", e)

def send_speak_command(ip: str, port: int, object_id: int, message: str):
    try:
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))
        command = f"SPEAK:{object_id}:{message}"
        client.sendall(command.encode("utf-8"))
        client.close()
    except Exception as e:
        logger.error("send_speak_command error: %s", e)

def send_update_ui_command(ip: str, port: int, element_id: int, new_text: str):
    try:
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))
        command = f"UPDATE_UI:{element_id}:{new_text}"
        client.sendall(command.encode("utf-8"))
        client.close()
    except Exception as e:
        logger.error("send_update_ui_command error: %s", e)

def broadcast_walk_paths(ip: str, port: int, agents: List["Agent"], step_delay: float = 0.55, idle_delay: float = 0.15):
    """批次送出多 Agent 的逐步路徑（若有），否則送當前定點位置。"""
    if not agents:
        return
    active = any(a.has_pending_walk() for a in agents)
    if not active:
        batch = [(a.index, float(a.position[0]), float(a.position[1])) for a in agents if isinstance(a.position, tuple)]
        if batch:
            send_move_command(ip, port, batch, delay=idle_delay)
        return

    while any(a.has_pending_walk() for a in agents):
        step_batch = []
        for a in agents:
            nxt = a.pop_next_walk_step()
            cur = nxt if isinstance(nxt, tuple) else (a.position if isinstance(a.position, tuple) else None)
            if cur:
                step_batch.append((a.index, float(cur[0]), float(cur[1])))
        if step_batch:
            send_move_command(ip, port, step_batch, delay=step_delay)

    final_batch = [(a.index, float(a.position[0]), float(a.position[1])) for a in agents if isinstance(a.position, tuple)]
    if final_batch:
        send_move_command(ip, port, final_batch, delay=idle_delay)

# ---------------------------
# Utility
# ---------------------------
def add_random_noise(location: str, map_dict: Dict[str, Any]) -> Tuple[float, float]:
    anchor = map_dict.get(location)
    if anchor is None:
        raise KeyError(f"Location '{location}' not found.")
    if isinstance(anchor, dict):
        anchor = anchor.get("anchor") or anchor.get("anchors")
    base = random.choice(anchor) if isinstance(anchor, list) else anchor
    x, y = float(base[0]), float(base[1])
    return (x + random.uniform(-3, 3), y + random.uniform(-3, 3))

def generate_walk_path(start: Tuple[float, float], end: Tuple[float, float], step_size: float = 2.8, jitter: float = 0.4):
    dx, dy = end[0] - start[0], end[1] - start[1]
    dist = math.hypot(dx, dy)
    if dist < 1e-3:
        return []
    segs = max(1, int(math.ceil(dist / max(step_size, 1e-3))))
    path: List[Tuple[float, float]] = []
    for i in range(1, segs + 1):
        t = i / segs
        if i == segs:
            path.append(end)
        else:
            path.append((start[0] + dx * t + random.uniform(-jitter, jitter),
                         start[1] + dy * t + random.uniform(-jitter, jitter)))
    return path

def DBSCAN_chat(agents: List["Agent"]) -> Optional[List["Agent"]]:
    points = [a.position for a in agents]
    arr = np.array(points)
    db = DBSCAN(eps=4.5, min_samples=1)
    labels = db.fit_predict(arr)
    clusters: List[List[Agent]] = []
    for label, agent in zip(labels, agents):
        while label >= len(clusters):
            clusters.append([])
        clusters[label].append(agent)
    candidates = [c for c in clusters if len(c) >= 2]
    if not candidates:
        return None
    if random.random() < 0.8:
        return random.choice(candidates)
    return None

def get_now_time(oldtime: str, step_num: int, min_per_step: int) -> str:
    dt = datetime.strptime(oldtime, "%Y-%m-%d-%H-%M")
    return (dt + timedelta(minutes=min_per_step * step_num)).strftime("%Y-%m-%d-%H-%M")

def get_weekday(nowtime: str) -> str:
    dt = datetime.strptime(nowtime, "%Y-%m-%d-%H-%M")
    return ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"][dt.weekday()]

def format_date_time(date_str: str) -> str:
    dt = datetime.strptime(date_str, "%Y-%m-%d-%H-%M")
    return dt.strftime("%Y年%m月%d日%H点%M分")

def compare_times(time_str1: str, time_str2: str, fmt: str = "%H-%M") -> bool:
    t1 = datetime.strptime(time_str1, fmt)
    t2 = datetime.strptime(time_str2, fmt)
    return t1 < t2

def update_schedule(wake_up_time_str: str, schedule: List[List[Any]]) -> List[List[Any]]:
    wake = datetime.strptime(wake_up_time_str, "%H-%M")
    cur = wake
    out: List[List[Any]] = []
    for activity, duration in schedule:
        out.append([activity, cur.strftime("%H-%M")])
        cur += timedelta(minutes=int(duration))
    return out

def find_current_activity(current_time_str: str, schedule: List[List[str]]) -> List[str]:
    cur = datetime.strptime(current_time_str, "%H-%M")
    for activity, time_str in schedule:
        t = datetime.strptime(time_str.replace(":", "-"), "%H-%M")
        if cur <= t:
            return [activity, time_str]
    return ["睡覺", current_time_str]

def weekday2START_TIME(weekday: str) -> str:
    base = {
        "星期一": "2024-11-18-03-00",
        "星期二": "2024-11-19-03-00",
        "星期三": "2024-11-20-03-00",
        "星期四": "2024-11-21-03-00",
        "星期五": "2024-11-22-03-00",
        "星期六": "2024-11-23-03-00",
        "星期天": "2024-11-24-03-00",
    }
    return base.get(weekday, "2024-11-18-03-00")

# ---------------------------
# Safe LLM loader (avoids OllamaAgent __init__ arg mismatch crash)
# ---------------------------
class LLMShim:
    """Lazy import async API from run_gpt_prompt; fallback to simple rules if import fails."""
    def __init__(self):
        self.ready = False
        self._load()

    def _load(self):
        try:
            # 盡量優先載入 tools.* 版本（通常較少副作用）
            from tools.LLM.run_gpt_prompt import (  # type: ignore
                go_map_async,
                modify_schedule_async,
                summarize_async,
                run_gpt_prompt_generate_hourly_schedule,
                run_gpt_prompt_wake_up_hour,
                run_gpt_prompt_pronunciatio,
                double_agents_chat,
                close_llm_session,
            )
            self.go_map_async = go_map_async
            self.modify_schedule_async = modify_schedule_async
            self.summarize_async = summarize_async
            self.generate_hourly = run_gpt_prompt_generate_hourly_schedule
            self.wake_up_hour = run_gpt_prompt_wake_up_hour
            self.pronunciatio = run_gpt_prompt_pronunciatio
            self.double_chat = double_agents_chat
            self.close_session = close_llm_session
            self.ready = True
            logger.info("LLM connected via tools.LLM.run_gpt_prompt")
        except Exception as e_tools:
            try:
                from ai_china_town.tools.LLM.run_gpt_prompt import (  # type: ignore
                    go_map_async,
                    modify_schedule_async,
                    summarize_async,
                    run_gpt_prompt_generate_hourly_schedule,
                    run_gpt_prompt_wake_up_hour,
                    run_gpt_prompt_pronunciatio,
                    double_agents_chat,
                    close_llm_session,
                )
                self.go_map_async = go_map_async
                self.modify_schedule_async = modify_schedule_async
                self.summarize_async = summarize_async
                self.generate_hourly = run_gpt_prompt_generate_hourly_schedule
                self.wake_up_hour = run_gpt_prompt_wake_up_hour
                self.pronunciatio = run_gpt_prompt_pronunciatio
                self.double_chat = double_agents_chat
                self.close_session = close_llm_session
                self.ready = True
                logger.info("LLM connected via ai_china_town.tools.LLM.run_gpt_prompt")
            except Exception as e_pkg:
                # Fallback：不讓程式崩潰；用極簡規則替代
                self.ready = False
                self._install_fallback()
                logger.warning(
                    "LLM import failed; using fallback (no external LLM). "
                    "errors: tools=%s | ai_china_town=%s",
                    repr(e_tools), repr(e_pkg)
                )

    # ---------------- Fallbacks (non-LLM) ----------------
    async def go_map_async(self, name: str, home: str, curr: str, places: List[str], activity: str) -> str:
        # 簡單映射：活動->地點
        table = {
            "上學": "School",
            "學習": "School",
            "用餐": "Rest",
            "吃飯": "Rest",
            "運動": "Gym",
            "購物": "Super",
            "搭車": "Subway",
            "散步": "Exterior",
            "睡覺": home,
            "睡覺": home,
        }
        return table.get(activity, curr or home or "Exterior")

    async def modify_schedule_async(self, schedule_time: List[List[str]], day_tag: str, memory: str,
                                    wake: str, persona: str) -> List[List[str]]:
        # 原樣返回
        return schedule_time

    async def summarize_async(self, content: str, day_tag: str, who: str) -> str:
        # 取最近 200 字
        return content[-200:]

    async def generate_hourly(self, persona: str, day_tag: str, today_goal: str = "") -> List[List[Any]]:
        # 極簡：起床→上學/工作→用餐→運動→購物→散步→回家
        return [
            ["Wake", 0],
            ["上學", 180],
            ["用餐", 60],
            ["學習", 120],
            ["運動", 60],
            ["購物", 30],
            ["散步", 45],
            ["休息", 180],
        ]

    async def wake_up_hour(self, persona: str, day_tag: str, schedule_after_wake: List[List[Any]]) -> str:
        return "07-00"

    async def pronunciatio(self, action: str) -> str:
        return "😃"

    async def double_chat(self, context: Dict[str, Any]) -> Tuple[str, List[List[str]]]:
        a = context["agent1"]["name"]
        b = context["agent2"]["name"]
        return (
            "smalltalk",
            [
                [a, "早安！今天打算做什麼？"],
                [b, "我準備去超市買點東西，順便散步。"],
                [a, "好啊，路上小心。"],
            ],
        )

    async def close_session(self):
        return

    def _install_fallback(self):
        # methods already defined above in class namespace
        pass

LLM = LLMShim()

# ---------------------------
# Agent
# ---------------------------
class Agent:
    def __init__(self, name: str, index: int, coord_map: Dict[str, Any]):
        self.name = name
        self.index = index  # Unity 端以序號識別
        self.MAP = coord_map

        self.home: str = DEFAULT_AGENT_HOMES.get(name, "Apartment_F1")
        self.curr_place: str = ""
        self.position: Tuple[float, float] = (0.0, 0.0)

        self.walk_path: List[Tuple[float, float]] = []

        self.schedule: List[List[Any]] = []
        self.wake: str = "07-00"
        self.schedule_time: List[List[str]] = []
        self.last_action: str = ""
        self.curr_action: str = ""
        self.curr_action_pronunciatio: str = ""
        self.memory: str = ""
        self.talk_arr: List[List[str]] = []

        # 載入個人設定（可選）
        profile_path = os.path.join("agents", self.name, "1.txt")
        self.persona_summary = self._read_persona(profile_path)

    def _read_persona(self, path: str) -> str:
        if not os.path.exists(path):
            return f"{self.name} 是模擬代理人。"
        try:
            with open(path, "r", encoding="utf-8") as f:
                lines = f.readlines()
            # 簡單取第 7 行（與舊版相容），或合併前幾行
            return (lines[6].strip() if len(lines) >= 7 else "".join(lines[:3])).strip()
        except Exception:
            return f"{self.name} 是模擬代理人。"

    # --- movement ---
    def goto_scene(self, scene_name: str, walk: bool = False):
        scene_name = PORTAL_NAME_ALIASES.get(scene_name, scene_name)
        dest = add_random_noise(scene_name, self.MAP)
        self.curr_place = scene_name
        if walk and isinstance(self.position, tuple):
            path = generate_walk_path(self.position, dest)
            if path:
                self.walk_path = path
                return
        self.walk_path = []
        self.position = dest

    def has_pending_walk(self) -> bool:
        return bool(self.walk_path)

    def pop_next_walk_step(self) -> Optional[Tuple[float, float]]:
        if not self.walk_path:
            return None
        nxt = self.walk_path.pop(0)
        self.position = nxt
        return nxt

    def plan_idle_wander(self, radius: float = 3.5) -> bool:
        if self.walk_path:
            return False
        anchor = self.MAP.get(self.curr_place)
        if isinstance(anchor, dict):
            anchor = anchor.get("anchor")
        anchor = anchor or self.position
        target = (anchor[0] + random.uniform(-radius, radius), anchor[1] + random.uniform(-radius, radius))
        if math.hypot(target[0] - self.position[0], target[1] - self.position[1]) < 0.5:
            target = (anchor[0] + random.choice([-radius, radius]) * 0.5, anchor[1] + random.choice([-radius, radius]) * 0.5)
        go = generate_walk_path(self.position, target, step_size=1.6, jitter=0.25)
        back_anchor = add_random_noise(self.curr_place, self.MAP)
        back = generate_walk_path(go[-1] if go else self.position, back_anchor, step_size=1.6, jitter=0.25)
        seq = [p for p in go + back if isinstance(p, tuple)]
        if seq:
            self.walk_path.extend(seq)
            return True
        return False

# ---------------------------
# High-level sim helpers
# ---------------------------
def _serialize_dialogues(dialogues: List[List[str]]) -> str:
    try:
        return json.dumps(dialogues, ensure_ascii=False)
    except Exception:
        return "\n".join(f"{s}: {u}" for s, u in dialogues if isinstance(s, str) and isinstance(u, str))

def _build_chat_context(a: Agent, b: Agent, now_time: str, weekday_label: str) -> Dict[str, Any]:
    location = a.curr_place or b.curr_place or a.home or b.home
    history = (a.talk_arr or [])[-10:]
    fmt_time = f"{format_date_time(now_time)}({weekday_label})"
    return {
        "location": location,
        "now_time": fmt_time,
        "history": history,
        "agent1": {"name": a.name, "mbti": a.name, "persona": a.persona_summary, "memory": a.memory, "action": a.curr_action or a.last_action},
        "agent2": {"name": b.name, "mbti": b.name, "persona": b.persona_summary, "memory": b.memory, "action": b.curr_action or b.last_action},
    }

async def _update_daily_plan(agent: Agent, now_time: str, weekday_label: str):
    if agent.talk_arr:
        agent.memory = await LLM.summarize_async(_serialize_dialogues(agent.talk_arr), f"{now_time[:10]}-{weekday_label}", agent.name)
        agent.talk_arr.clear()

    agent.goto_scene(agent.home)
    persona = agent.persona_summary
    today_goal = "保持良好心情"

    schedule = await LLM.generate_hourly(persona, f"{now_time[:10]}-{weekday_label}", today_goal)
    agent.schedule = schedule if isinstance(schedule, list) else []
    schedule_for_wake = agent.schedule[1:] if len(agent.schedule) > 1 else agent.schedule
    agent.wake = await LLM.wake_up_hour(persona, now_time[:10] + weekday_label, schedule_for_wake)

    if ":" in agent.wake:
        agent.wake = agent.wake.replace(":", "-")
    if "-" not in agent.wake:
        s = agent.wake
        if len(s) == 2:
            agent.wake = f"0{s[0]}-0{s[1:]}"
        elif len(s) == 3:
            agent.wake = f"0{s[0]}-{s[1:]}"
        elif len(s) == 4:
            agent.wake = f"{s[:2]}-{s[2:]}"

    agent.schedule_time = update_schedule(agent.wake, [item[:2] for item in schedule_for_wake] or [["休息", 1440]])
    agent.schedule_time = await LLM.modify_schedule_async(agent.schedule_time, f"{now_time[:10]}-{weekday_label}", agent.memory, agent.wake, persona)

    agent.curr_action = "睡覺"
    agent.last_action = "睡覺"
    agent.curr_place = agent.home
    agent.curr_action_pronunciatio = "😴"
    send_speak_command(UNITY_IP, UNITY_PORT, agent.index, agent.curr_action)
    logger.info("%s 当前活动: %s(%s)---所在地点(%s)", agent.name, agent.curr_action, agent.curr_action_pronunciatio, agent.curr_place)

async def _process_agent_activity(agent: Agent, now_time: str, weekday_label: str):
    current_hm = now_time[-5:]
    if compare_times(current_hm, agent.wake):
        agent.curr_action = "睡覺"
        if agent.curr_place != agent.home:
            agent.goto_scene(agent.home, walk=True)
        else:
            agent.curr_place = agent.home
        agent.curr_action_pronunciatio = "😴"
        logger.debug("%s 睡覺 @ %s", agent.name, agent.curr_place)
        return

    if isinstance(agent.schedule_time, list) and agent.schedule_time:
        current_activity = find_current_activity(current_hm, agent.schedule_time)[0]
    else:
        current_activity = "休息"

    if agent.last_action != current_activity:
        agent.curr_action_pronunciatio = await LLM.pronunciatio(current_activity)
        # LLM 決定地點；若 LLM 不可用，fallback 會回傳合理地點
        next_place = await LLM.go_map_async(agent.name, agent.home, agent.curr_place, CAN_GO_PLACES, current_activity)
        agent.curr_place = PORTAL_NAME_ALIASES.get(next_place, next_place)
        agent.goto_scene(agent.curr_place, walk=True)
        send_speak_command(UNITY_IP, UNITY_PORT, agent.index, current_activity)
        agent.last_action = current_activity
    agent.curr_action = current_activity
    logger.info("%s 当前活动: %s(%s)---所在地点(%s)", agent.name, agent.curr_action, agent.curr_action_pronunciatio, agent.curr_place)

async def _handle_possible_chat(agents: List[Agent], now_time: str, weekday_label: str):
    pair = DBSCAN_chat(agents)
    if not pair:
        return
    a, b = pair[0], pair[1]
    if a.curr_place != b.curr_place:
        return
    a.curr_action = b.curr_action = "聊天"
    ctx = _build_chat_context(a, b, now_time, weekday_label)
    thought, dialogue = await LLM.double_chat(ctx)
    if not isinstance(dialogue, list):
        logger.warning("LLM 聊天輸出格式異常: %s", dialogue)
        return
    combined: List[List[str]] = []
    a_lines, b_lines = [], []
    for idx, item in enumerate(dialogue, start=1):
        if not (isinstance(item, (list, tuple)) and len(item) >= 2):
            continue
        speaker, utterance = item[0], item[1]
        combined.append([speaker, utterance])
        if speaker == a.name:
            a_lines.append(f"{idx}. {utterance}")
        elif speaker == b.name:
            b_lines.append(f"{idx}. {utterance}")
    if combined:
        a.talk_arr.extend(combined)
        b.talk_arr.extend(combined)
        send_speak_command(UNITY_IP, UNITY_PORT, a.index, "\n".join(a_lines))
        send_speak_command(UNITY_IP, UNITY_PORT, b.index, "\n".join(b_lines))
        logger.info("聊天內容(%s): %s", thought, combined)

# ---------------------------
# Main simulation
# ---------------------------
async def run_simulation(steps: int, min_per_step: int, weekday: str):
    # 建立 Agents（序號 = Unity 物件 ID）
    agents: List[Agent] = []
    for idx, name in enumerate(DEFAULT_AGENT_ORDER):
        ag = Agent(name=name, index=idx, coord_map=COORDINATE_MAP)
        ag.goto_scene(ag.home)  # 初始落點
        agents.append(ag)

    now_time = weekday2START_TIME(weekday)
    send_update_ui_command(UNITY_IP, UNITY_PORT, 0, f"当前时间：{now_time}")

    # 每天重置的步數間隔
    day_step_interval = max(1, int(1440 / max(min_per_step, 1)))
    for step in range(steps):
        weekday_label = get_weekday(now_time)
        formatted_time = format_date_time(now_time)
        send_update_ui_command(UNITY_IP, UNITY_PORT, 0, f"当前时间：{formatted_time}({weekday_label})")

        if step % day_step_interval == 0:
            logger.info("新的一天：%s(%s)", formatted_time, weekday_label)
            for a in agents:
                await _update_daily_plan(a, now_time, weekday_label)
        else:
            for a in agents:
                await _process_agent_activity(a, now_time, weekday_label)
            # 送路徑/定位
            broadcast_walk_paths(UNITY_IP, UNITY_PORT, agents)
            # 嘗試觸發聊天
            await _handle_possible_chat(agents, now_time, weekday_label)

        now_time = get_now_time(now_time, 1, min_per_step)

    logger.info("模擬結束。")
    try:
        await LLM.close_session()
    except Exception:
        pass

# ---------------------------
# CLI
# ---------------------------
def parse_arguments():
    p = argparse.ArgumentParser(description="Unity 前端小鎮模擬（清理版）")
    p.add_argument("--steps", type=int, default=60, help="模擬步數")
    p.add_argument("--minutes-per-step", type=int, default=30, help="每步模擬分鐘數")
    p.add_argument("--weekday", default="星期一", help="起始星期：星期一~星期天")
    return p.parse_args()

def main():
    args = parse_arguments()
    asyncio.run(run_simulation(args.steps, args.minutes_per_step, args.weekday))

if __name__ == "__main__":
    main()

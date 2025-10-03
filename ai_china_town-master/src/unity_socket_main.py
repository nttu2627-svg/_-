# /src/unity_socket_main.py

import math
import random
import time
from datetime import datetime, timedelta
import gradio as gr
import numpy as np
from sklearn.cluster import DBSCAN
from ai_china_town.tools.LLM.run_gpt_prompt import go_map, modify_schedule, summarize
from tools.LLM.run_gpt_prompt import *
import os
import socket



def send_move_command(ip, port, object_positions, delay: float = 0.5):
    """
    发送多个角色的目标坐标到Unity。
    object_positions: [(object_id, x, y), (object_id, x, y), ...]
    """
    try:
        # 创建 socket 客户端
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))

        # 构造移动命令，支持多个角色
        command = "MOVE:" + ";".join([f"{object_id},{x},{y}" for object_id, x, y in object_positions])
        client.sendall(command.encode('utf-8'))
        print(f"Sent: {command}")

        # 关闭连接
        client.close()
        if delay and delay > 0:
            time.sleep(delay)
    except Exception as e:
        print(f"Error: {e}")


def send_speak_command(ip, port, object_id, message):
    """
    发送角色说话命令到Unity。
    """
    try:
        # 创建 socket 客户端
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))

        # 构造说话命令
        command = f"SPEAK:{object_id}:{message}"
        client.sendall(command.encode('utf-8'))
        print(f"Sent: {command}")
        time.sleep(3)
        # 关闭连接
        client.close()
    except Exception as e:
        print(f"Error: {e}")

def send_update_ui_command(ip, port, element_id, new_text):
    """
    发送UI文本更新命令到Unity。
    element_id: UI元素的索引
    new_text: 更新后的文本内容
    """
    try:
        # 创建 socket 客户端
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((ip, port))

        # 构造更新UI文本命令
        command = f"UPDATE_UI:{element_id}:{new_text}"
        client.sendall(command.encode('utf-8'))
        print(f"Sent: {command}")

        # 关闭连接
        client.close()
    except Exception as e:
        print(f"Error: {e}")

def broadcast_walk_paths(ip, port, agents, step_delay: float = 0.55, idle_delay: float = 0.15):
    if not agents:
        return
    active = any(agent.has_pending_walk() for agent in agents if hasattr(agent, "has_pending_walk"))
    if not active:
        object_positions = []
        for agent in agents:
            if isinstance(agent.position, tuple):
                object_positions.append((int(agents_name.index(agent.name)), float(agent.position[0]), float(agent.position[1])))
        if object_positions:
            send_move_command(ip, port, object_positions, delay=idle_delay)
        return

    while any(agent.has_pending_walk() for agent in agents if hasattr(agent, "has_pending_walk")):
        batch_positions = []
        for agent in agents:
            next_pos = agent.pop_next_walk_step() if hasattr(agent, "pop_next_walk_step") else None
            current = next_pos if isinstance(next_pos, tuple) else (agent.position if isinstance(agent.position, tuple) else None)
            if current:
                batch_positions.append((int(agents_name.index(agent.name)), float(current[0]), float(current[1])))
        if batch_positions:
            send_move_command(ip, port, batch_positions, delay=step_delay)

    final_positions = []
    for agent in agents:
        if isinstance(agent.position, tuple):
            final_positions.append((int(agents_name.index(agent.name)), float(agent.position[0]), float(agent.position[1])))
    if final_positions:
        send_move_command(ip, port, final_positions, delay=idle_delay)
unity_ip = "127.0.0.1"  # Unity 运行的 IP 地址
unity_port = 12345  # Unity 使用的端口号
# === Unity 前端場景定義 ===
UNITY_SCENE_NAME = "CityScene"

UNITY_LOCATION_MARKERS = {
    "Apartment_F1": {
        "anchor": (-83.5, -50.6),
        "aliases": ["Apartment", "公寓", "公寓一樓", "公寓F1"]
    },
    "Apartment_F2": {
        "anchor": (-184.7, -57.0),
        "aliases": ["公寓二樓", "Apartment_Floor2"]
    },
    "School": {
        "anchor": (-1.0, -109.7),
        "aliases": ["學校", "教室", "校園", "校园"]
    },
    "Rest": {
        "anchor": (-98.0, 10.5),
        "aliases": ["餐廳", "餐厅", "咖啡店", "Cafe"]
    },
    "Gym": {
        "anchor": (-86.8, 42.9),
        "aliases": ["健身房", "Gymnasium"]
    },
    "Super": {
        "anchor": (52.2, 92.9),
        "aliases": ["超市", "商場", "商场", "便利店"]
    },
    "Subway": {
        "anchor": (166.7, -97.1),
        "aliases": ["地鐵", "地铁", "Metro"]
    },
    "Exterior": {
        "anchor": (174.8, 1.9),
        "aliases": ["室外", "戶外", "户外", "公園", "Park"]
    }
}

RAW_PORTAL_MARKERS = [
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
    {"name": "餐廳_室外", "position": (96.95, -5.1), "targets": ["餐廳_室內"]}
]

PORTAL_NAME_ALIASES = {
    "公寓": "Apartment_F1",
    "公寓F1": "Apartment_F1",
    "公寓F2": "Apartment_F2",
    "Apartment": "Apartment_F1",
    "Apartment_Floor2": "Apartment_F2",
    "公寓二樓": "Apartment_F2",
    "公寓一樓": "Apartment_F1",
    "公寓二樓_室內_上": "公寓頂樓_室內",
    "公寓頂樓_室內_下": "公寓二樓_室內"
}


def _build_coordinate_lookup():
    coordinate_map = {}
    alias_map = {}

    for name, meta in UNITY_LOCATION_MARKERS.items():
        anchor = meta.get("anchor")
        if anchor:
            coordinate_map[name] = (anchor[0], anchor[1])
        for alias in meta.get("aliases", []):
            alias_map[alias] = name

    portal_map = {}
    for entry in RAW_PORTAL_MARKERS:
        pos = entry["position"]
        anchor = (round(pos[0], 4), round(pos[1], 4))
        payload = portal_map.setdefault(entry["name"], {"anchors": [], "targets": set()})
        if anchor not in payload["anchors"]:
            payload["anchors"].append(anchor)
        payload["targets"].update(entry.get("targets", []))

    for alias, canonical in PORTAL_NAME_ALIASES.items():
        alias_map[alias] = canonical

    for name, payload in portal_map.items():
        anchors = payload["anchors"]
        if len(anchors) == 1:
            coordinate_map[name] = anchors[0]
        else:
            coordinate_map[name] = anchors
        payload["targets"] = sorted(payload["targets"])

    for alias, canonical in alias_map.items():
        if canonical in coordinate_map:
            coordinate_map[alias] = coordinate_map[canonical]

    return coordinate_map, portal_map


COORDINATE_MAP, UNITY_PORTAL_MARKERS = _build_coordinate_lookup()

can_go_place = list(UNITY_LOCATION_MARKERS.keys())
# TODO 暂时不考虑环境周围物品
ENVIRONMENT_OBJECTS = {
    "Apartment_F1": ["床", "沙發", "書桌"],
    "Apartment_F2": ["床", "書架", "陽台椅"],
    "School": ["黑板", "課桌椅", "講台"],
    "Rest": ["咖啡機", "甜點櫃", "沙發椅"],
    "Gym": ["啞鈴", "跑步機", "瑜珈墊"],
    "Super": ["貨架", "收銀台", "購物籃"],
    "Subway": ["售票機", "候車椅", "路線圖"],
    "Exterior": ["長椅", "路燈", "噴泉"]
}

DEFAULT_AGENT_ORDER = ["ISTJ", "ISFJ", "INFJ"]
DEFAULT_AGENT_HOMES = {
    "ISTJ": "Apartment_F1",
    "ISFJ": "Apartment_F1",
    "INFJ": "Apartment_F2"
}

# 世界的规则
# TODO 暂时只考虑学校上学的时间，配合苏醒时间
world_rule = ""

# 角色
agents_name = DEFAULT_AGENT_ORDER[:]

class agent_v:
    def __init__(self, name, coordinate_map):
        self.name = name
        self.MAP = coordinate_map
        self.schedule = []
        self.Visual_Range = 1
        self.home = ""
        self.curr_place  = ""
        self.position = (0,0)
        self.walk_path = []
        self.schedule_time = []
        self.last_action = ""
        self.memory = ""
        self.talk_arr = ""
        self.wake = ""
        self.curr_action = ""
        self.curr_action_pronunciatio  = ""
        self.ziliao = open(f"./agents/{self.name}/1.txt",encoding="utf-8").readlines()

    def agent_init(self, home):
        self.home = home
        self.goto_scene(self.home)
        return self

    def getpositon(self):
        return self.position


    def goto_scene(self,scene_name, walk: bool = False):
        destination = add_random_noise(scene_name,self.MAP)
        self.curr_place =  scene_name
        if not isinstance(destination, tuple):
            self.walk_path = []
            self.position = destination
            return self.position

        if walk and isinstance(self.position, tuple):
            path = generate_walk_path(self.position, destination)
            if path:
                self.walk_path = path
                return destination

        self.walk_path = []
        self.position = destination
        return self.position

    def has_pending_walk(self) -> bool:
        return bool(self.walk_path)

    def pop_next_walk_step(self):
        if not self.walk_path:
            return None
        next_pos = self.walk_path.pop(0)
        if isinstance(next_pos, tuple):
            self.position = next_pos
        return self.position

    def plan_idle_wander(self, radius: float = 3.5) -> bool:
        if self.walk_path:
            return False
        if not isinstance(self.position, tuple):
            return False
        anchor = self.MAP.get(self.curr_place)
        if not isinstance(anchor, tuple):
            anchor = self.position
        target = (
            anchor[0] + random.uniform(-radius, radius),
            anchor[1] + random.uniform(-radius, radius)
        )
        if math.hypot(target[0] - self.position[0], target[1] - self.position[1]) < 0.5:
            target = (
                anchor[0] + random.choice([-radius, radius]) * 0.5,
                anchor[1] + random.choice([-radius, radius]) * 0.5
            )
        path_out = generate_walk_path(self.position, target, step_size=1.6, jitter=0.25)
        pivot = path_out[-1] if path_out else self.position
        back_anchor = add_random_noise(self.curr_place, self.MAP)
        path_back = generate_walk_path(pivot, back_anchor, step_size=1.6, jitter=0.25)
        combined = [p for p in path_out + path_back if isinstance(p, tuple)]
        if combined:
            self.walk_path.extend(combined)
            return True
        return False
    def Is_nearby(self,position):
        x1=self.position[0]
        x2=position[0]
        y1=self.position[1]
        y2=position[1]
        manhattan_distance = abs(x1 - x2) + abs(y1 - y2)
        # 计算欧几里得距离
        euclidean_distance = math.sqrt((x1 - x2) ** 2 + (y1 - y2) ** 2)
        # 判断是否相邻
        return manhattan_distance == 1 or euclidean_distance == 1 or euclidean_distance == math.sqrt(2)


# 给场景增加噪声
def add_random_noise(location, map_dict):
    anchor = map_dict.get(location)
    if anchor is None:
        return "Location not found in the dictionary."

    if isinstance(anchor, dict):
        anchor = anchor.get("anchor") or anchor.get("anchors")

    if isinstance(anchor, list):
        if not anchor:
            return "Location not found in the dictionary."
        base = random.choice(anchor)
    else:
        base = anchor

    if isinstance(base, (tuple, list)) and len(base) >= 2:
        x, y = float(base[0]), float(base[1])
    else:
        return "Location not found in the dictionary."
    # 为每个坐标添加随机噪声
    x_with_noise = x + random.uniform(-3, 3)
    y_with_noise = y + random.uniform(-3, 3)

    return (x_with_noise, y_with_noise)

def generate_walk_path(start, end, step_size: float = 2.8, jitter: float = 0.4):
    if not (isinstance(start, tuple) and isinstance(end, tuple)):
        return [end] if isinstance(end, tuple) else []
    dx = end[0] - start[0]
    dy = end[1] - start[1]
    distance = math.hypot(dx, dy)
    if distance < 1e-3:
        return []
    segments = max(1, int(math.ceil(distance / max(step_size, 1e-3))))
    path = []
    for idx in range(1, segments + 1):
        ratio = idx / segments
        if idx == segments:
            path.append(end)
        else:
            path.append(
                (
                    start[0] + dx * ratio + random.uniform(-jitter, jitter),
                    start[1] + dy * ratio + random.uniform(-jitter, jitter)
                )
            )
    return path

# DBSCAN聚类方式感知聊天

def DBSCAN_chat(agents):
    result = []
    points_list =  []
    agent_list = []
    for agent in agents:
        points_list.append(agent.getpositon())
        agent_list.append(agent)
    points_array = np.array(points_list)
    dbscan = DBSCAN(eps=4.5, min_samples=1)
    labels = dbscan.fit_predict(points_array)

    for point, label,agent in zip(points_list, labels,agent_list):
        # print(f"Point {point} belongs to cluster {label}")
        index  = int(label)
        if index >= len(result):
            result.extend([[] for _ in range(index - len(result) + 1)])
        result[index] += [(point,agent)]
        # 筛选至少两个元素的聚类
    filtered_clusters = [cluster for cluster in result if len(cluster) >= 2]
    # 如果没有符合条件的聚类，返回 None
    if not filtered_clusters:
        return None
    if random.random() < 0.8:
        selected_cluster = random.choice(filtered_clusters)
        return [i[1] for i in selected_cluster]
    else:
        return None



# memory记忆 TODO 暂时考虑使用永久记忆，不设置遗忘曲线
'''
    记录：
        聊天的总结
        每天的工作计划
        这几天有什么重要的事情
'''



# 计算时间的表示的函数
def get_now_time(oldtime,step_num,min_per_step):
    def format_time(dt):
        return dt.strftime("%Y-%m-%d-%H-%M")
    def calculate_new_time(oldtime, step_num,min_per_step):
        # 将字符串转换为 datetime 对象
        start_time = datetime.strptime(oldtime, "%Y-%m-%d-%H-%M")
        # 计算新的时间
        new_time = start_time + timedelta(minutes=min_per_step * step_num)
        # 将新的时间格式化为字符串
        return format_time(new_time)
    return calculate_new_time(oldtime, step_num, min_per_step)

# 获取时间对于的星期
def get_weekday(nowtime):
    date_format = '%Y-%m-%d-%H-%M'
    dt = datetime.strptime(nowtime, date_format)
    # 获取星期几，0表示星期一，6表示星期日
    weekday = dt.weekday()
    # 定义星期几的名称
    days_of_week = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"]
    return days_of_week[weekday]

# 时间转为2024年11月16日早上06点30分格式
def format_date_time(date_str):
    # 定义输入日期时间的格式
    input_format = '%Y-%m-%d-%H-%M'
    # 解析日期时间字符串
    dt = datetime.strptime(date_str, input_format)
    # 定义输出日期时间的格式
    output_format = '%Y年%m月%d日%H点%M分'
    # 格式化日期时间字符串
    formatted_date = dt.strftime(output_format)
    return formatted_date

# 比较两个时间谁更早
def compare_times(time_str1, time_str2, time_format="%H-%M"):
    # 解析时间字符串为 datetime 对象
    time1 = datetime.strptime(time_str1, time_format)
    time2 = datetime.strptime(time_str2, time_format)
    # 比较两个时间
    if time1 < time2:
        return True
    elif time1 > time2:
        return False
    else:
        return False


# 日程安排转为开始时间
#  TODO 时间有问题，睡觉时间,传入参数[1:]即可解决
def update_schedule(wake_up_time_str, schedule):
    # 将字符串格式的时间转换为datetime对象
    wake_up_time = datetime.strptime(wake_up_time_str, '%H-%M')
    # 初始化当前时间为醒来时间
    current_time = wake_up_time
    # 创建一个新的列表来存储更新后的日程安排
    updated_schedule = []
    for activity, duration in schedule:
        updated_schedule.append([activity, current_time.strftime('%H-%M')])
        current_time += timedelta(minutes=duration)

    return updated_schedule

# 确定当前时间agent开展的活动
def find_current_activity(current_time_str, schedule):
    # 将当前时间字符串转换为datetime对象
    current_time = datetime.strptime(current_time_str, '%H-%M')
    # 遍历日程安排列表，找到当前时间对应的日程安排项
    for i, (activity, time_str) in enumerate(schedule):
        time_str = time_str.replace(':', '-')
        # 将日程安排的时间字符串转换为datetime对象
        activity_time = datetime.strptime(time_str, '%H-%M')
        # 如果当前时间小于等于当前日程安排的时间，则返回当前日程安排项
        if current_time <= activity_time:
            return [activity, time_str]
    # 如果当前时间大于所有日程安排的时间，返回睡觉
    return ['睡觉',current_time_str]

# 文件处理部分
BASE_DIR = './agents/'
PARENT_DIRS = [os.path.join(BASE_DIR, folder) for folder in agents_name]
TARGET_FILENAME = "1.txt"  # 文件名相同

# 获取所有父文件夹中的目标文件路径
def get_target_files(parent_dirs, target_filename):
    target_files = {}
    for folder in parent_dirs:
        file_path = os.path.join(folder, target_filename)
        if os.path.exists(file_path):
            target_files[os.path.basename(folder)] = file_path
    return target_files

# 读取文件内容
def read_file(file_path):
    with open(file_path, "r", encoding="utf-8") as file:
        return file.read()

# 保存文件内容
def save_file(file_path, new_content):
    with open(file_path, "w", encoding="utf-8") as file:
        file.write(new_content)
    return f"文件 {os.path.basename(file_path)} 已成功保存！"

# 生成选项页函数
def generate_tabs(target_files):
    for folder_name, file_path in target_files.items():

        def save_callback(new_content, file_path=file_path):
            return save_file(file_path, new_content)

        with gr.Tab(folder_name):
            file_content = read_file(file_path)
            textbox = gr.Textbox(
                label=f"{folder_name}/{TARGET_FILENAME} 内容",
                value=file_content,
                lines=20,
                interactive=True
            )
            save_button = gr.Button("保存")
            save_status = gr.Label()

            save_button.click(save_callback, inputs=[textbox], outputs=save_status)

# 通过星期几确定日期
def weekday2START_TIME(weekday_dropdown):
    weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"]
    if weekday_dropdown == weekdays[0]:
        result = "2024-11-18-03-00"
    elif weekday_dropdown == weekdays[1]:
        result = "2024-11-19-03-00"
    elif weekday_dropdown == weekdays[2]:
        result = "2024-11-20-03-00"
    elif weekday_dropdown == weekdays[3]:
        result = "2024-11-21-03-00"
    elif weekday_dropdown == weekdays[4]:
        result = "2024-11-22-03-00"
    elif weekday_dropdown == weekdays[5]:
        result = "2024-11-23-03-00"
    elif weekday_dropdown == weekdays[6]:
        result = "2024-11-24-03-00"
    else:
        result = "2024-11-18-03-00"
    return result

# 模拟主循环逻辑
def simulate_town_simulation(steps, min_per_step,weekday_dropdown):
    output_gradio = []

    agents = []
    for name in DEFAULT_AGENT_ORDER:
        home = DEFAULT_AGENT_HOMES.get(name, "Apartment_F1")
        agent = agent_v(name, COORDINATE_MAP).agent_init(home)
        agents.append(agent)
    step = 0
    START_TIME = weekday2START_TIME(weekday_dropdown)
    now_time = START_TIME
    send_update_ui_command(unity_ip, unity_port, 0, f'当前时间：{now_time}')

    for i in range(steps):
        output_gradio.append(f'第 {i+1} 个 step'.center(140,'-'))
        yield "\n".join(output_gradio)

        if step % int((1440 / min_per_step)) == 0:
            weekday_1 = get_weekday(START_TIME)
            format_time = format_date_time(START_TIME)
            output_gradio.append(f'当前时间：{format_time}({weekday_1})')
            yield "\n".join(output_gradio)
            for i in agents:
                if i.talk_arr != "":
                    # print(i.name, i.talk_arr)
                    i.memory = summarize(i.talk_arr, f'{now_time[:10]}-{weekday_1}', i.name)
                i.goto_scene(i.home)
                i.schedule = run_gpt_prompt_generate_hourly_schedule(i.ziliao[6], f'{now_time[:10]}-{weekday_1}')
                i.wake = run_gpt_prompt_wake_up_hour(i.ziliao[6], now_time[:10]+weekday_1, i.schedule[1:])

                # print("i.wake", i.wake)
                # 解决deepseek-v3生成的问题
                if ":" in i.wake:
                    # print(i.wake,'i.wake = i.wake.replace(":","-")')
                    i.wake = i.wake.replace(":", "-")
                # 解决qwen2.5-3b
                if "-" not in i.wake:
                    # print(i.wake,'elif "-" not in i.wake:')
                    if len(i.wake) == 2:
                        i.wake = "0" + i.wake[0] + "-" + "0" + i.wake[1:]
                    elif len(i.wake) == 3:
                        i.wake = "0" + i.wake[0] + "-" + i.wake[1:]
                    elif len(i.wake) == 4:
                        i.wake = "0" + i.wake[:2] + "-" + i.wake[2:]

                i.schedule_time = update_schedule(i.wake, i.schedule[1:])
                i.schedule_time = modify_schedule(i.schedule_time, f'{now_time[:10]}-{weekday_1}', i.memory, i.wake,
                                                  i.ziliao[6])
                print("i.schedule_time",i.schedule_time)
                if step == 0:
                    i.curr_action = "睡觉"
                    i.last_action = "睡觉"
                # TODO
                send_speak_command(unity_ip, unity_port, int(agents_name.index(i.name)), i.curr_action)
                output_gradio.append(f'{i.name}当前活动:{i.curr_action}(😴💤)---所在地点({i.home})')
                yield "\n".join(output_gradio)
        else:
            weekday_2 = get_weekday(now_time)
            format_time = format_date_time(now_time)
            output_gradio.append(f'当前时间：{format_time}({weekday_2})')
            yield "\n".join(output_gradio)
            send_update_ui_command(unity_ip, unity_port, 0, f'当前时间：{format_time}({weekday_2})')
            for i in agents:
                if compare_times(now_time[-5:], i.wake):
                    i.curr_action = "睡觉"
                    if i.curr_place != i.home:
                        i.goto_scene(i.home, walk=True)
                    else:
                        i.curr_place = i.home
                    i.last_action = "睡觉"
                    output_gradio.append(f'{i.name}当前活动:{i.curr_action}(😴💤)---所在地点({i.curr_place})')

                else:
                    if type(i.schedule_time) in [list]:
                        i.curr_action = find_current_activity(now_time[-5:], i.schedule_time)[0]
                    else:
                        print('ERROR : i.schedule_time不是列表')
                    if i.last_action != i.curr_action:
                        i.curr_action_pronunciatio = run_gpt_prompt_pronunciatio(i.curr_action)[:2]
                        i.last_action = i.curr_action
                        i.curr_place = go_map(i.name, i.home, i.curr_place, can_go_place, i.curr_action)
                        i.goto_scene(i.curr_place, walk=True)
                        # TODO
                        send_speak_command(unity_ip, unity_port, int(agents_name.index(i.name)), i.curr_action)
                        output_gradio.append(
                            f'{i.name}当前活动:{i.curr_action}({i.curr_action_pronunciatio})---所在地点({i.curr_place})')
                    else:
                        # TODO
                        send_speak_command(unity_ip, unity_port, int(agents_name.index(i.name)),i.curr_action)
                        if random.random() < 0.35:
                            i.plan_idle_wander()
                        output_gradio.append(
                            f'{i.name}当前活动:{i.curr_action}({i.curr_action_pronunciatio})---所在地点({i.curr_place})')
                yield "\n".join(output_gradio)
            broadcast_walk_paths(unity_ip, unity_port, agents)

            # 感知周围其他角色决策行动
                # 主视角查看全地图，获取角色坐标
                    # 触发聊天
                        # 反思聊天变成记忆存储
                # 行动完成
            chat_part = DBSCAN_chat(agents)
            if chat_part == None:
                pass
            else:
                output_gradio.append(
                    f'{chat_part[0].name}和{chat_part[1].name}在{chat_part[1].curr_place}相遇,他们在进行聊天')
                yield "\n".join(output_gradio)
                chat_part[0].curr_action = "聊天"
                chat_part[1].curr_action = "聊天"

                if chat_part[0].curr_place == chat_part[1].curr_place:
                    output_gradio.append(
                        f'{chat_part[0].name}当前活动:{chat_part[0].curr_action}---所在地点({chat_part[0].curr_place}旁)')
                    output_gradio.append(
                        f'{chat_part[1].name}当前活动:{chat_part[1].curr_action}---所在地点({chat_part[0].curr_place}旁)')
                else:
                    output_gradio.append(
                        f'{chat_part[0].name}当前活动:{chat_part[0].curr_action}---所在地点({chat_part[0].curr_place}和{chat_part[1].curr_place}旁)')
                    output_gradio.append(
                        f'{chat_part[1].name}当前活动:{chat_part[1].curr_action}---所在地点({chat_part[0].curr_place}和{chat_part[1].curr_place}旁)')
                chat_result = double_agents_chat(
                    chat_part[0].curr_place,
                    chat_part[0].name,
                    chat_part[1].name,
                    f"{chat_part[0].name}正在{chat_part[0].curr_action},{chat_part[1].name}正在{chat_part[1].curr_action}",
                    chat_part[0].talk_arr,
                    chat_part[1].talk_arr,
                    f'{now_time[:10]}-{weekday_2}')
                output_gradio.append(f'聊天内容:{chat_result}')
                yield "\n".join(output_gradio)
                # 初始化一个空列表用于存储所有对话
                all_dialogues = []
                # 将所有对话按顺序存入新的列表
                for dialogue in chat_result:
                    all_dialogues.append(dialogue)
                # 初始化两个空字符串用于存储各自的内容
                xiaoming_dialogue = ""
                xiaofang_dialogue = ""
                # 初始化一个全局计数器
                global_count = 1
                # 遍历所有对话，根据名字将内容添加到对应的字符串中，并加上序号
                for dialogue in all_dialogues:
                    if dialogue[0] == chat_part[0].name:
                        xiaoming_dialogue += f"{global_count}. {dialogue[1]}\n"
                    elif dialogue[0] == chat_part[1].name:
                        xiaofang_dialogue += f"{global_count}. {dialogue[1]}\n"
                    global_count += 1

                send_speak_command(unity_ip, unity_port, int(agents_name.index(chat_part[0].name)),xiaoming_dialogue)
                send_speak_command(unity_ip, unity_port, int(agents_name.index(chat_part[1].name)), xiaofang_dialogue)

                # print(343, type(chat_result))
                # print(344, type( chat_part[0].memory))
                # print(345, chat_result)
                chat_part[0].talk_arr += json.dumps(chat_result, ensure_ascii=False)
                chat_part[1].talk_arr += json.dumps(chat_result, ensure_ascii=False)





        step += 1
        now_time = get_now_time(now_time, 1,min_per_step)
        if step == steps:
            output_gradio.append("已到最大执行步数，结束".center(120, '-'))
        # 在每个循环结束时返回结果
            yield "\n".join(output_gradio)



# Gradio界面
def launch_gradio_interface():
    with gr.Blocks() as demo:
        with gr.Row():
            with gr.Column():
                gr.Markdown('''
                       # AI小镇活动模拟
                   ''')
                # 星期选项
                weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期天"]
                weekday_dropdown = gr.Dropdown(weekdays, label="选择星期")
                steps_input = gr.Number(value=60, label="模拟步数")
                min_per_step_input = gr.Number(value=30, label="每步模拟分钟数")
                simulation_output = gr.Textbox(label="模拟结果", interactive=False)
                simulate_button = gr.Button("开始模拟")
                simulate_button.click(simulate_town_simulation,
                                      inputs=[steps_input, min_per_step_input, weekday_dropdown],
                                      outputs=[simulation_output])

            with gr.Column():
                gr.Markdown("### 编辑文件")
                target_files = get_target_files(PARENT_DIRS, TARGET_FILENAME)
                generate_tabs(target_files)

    demo.launch()

if __name__ == "__main__":
    launch_gradio_interface()
    # TODO 总结一天的，不要覆盖聊天记录而是+=
# simulation_logic/agent_actions.py (依赖注入最终版)

import asyncio
import random
import json

# ### 核心修改：此档案不再直接导入 LLM 函数 ###

def find_chat_groups(agents_list):
    """根据地点将代理人分组。"""
    if len(agents_list) < 2: return {}
    groups_by_location = {}
    for agent in agents_list:
        location = agent.curr_place
        if location not in groups_by_location:
            groups_by_location[location] = []
        groups_by_location[location].append(agent)
    return {loc: group for loc, group in groups_by_location.items() if len(group) > 1}

async def handle_social_interactions(active_agents, llm_context, llm_functions=None):
    """
    异步处理社交互动，LLM 函数通过依赖注入传入。
    """
    if llm_context.get('skip_reasoning'):
        return
    if not active_agents:
        return
    if all(getattr(agent, 'curr_action', None) in {"睡覺", "Unconscious"} for agent in active_agents):
        return
    chat_buffer = llm_context.get('chat_buffer', {})
    now_time = llm_context.get('current_time_str', '')
    double_agents_chat = llm_functions['double_agents_chat']
    generate_inner_monologue = llm_functions['generate_inner_monologue']

    chatting_agents = set()
    chat_groups_by_location = find_chat_groups(active_agents)

    chat_tasks = []
    max_groups = llm_context.get('max_chat_groups', 2)
    for location, group in list(chat_groups_by_location.items())[:max_groups]:
        if random.random() < 0.6:
            chat_tasks.append(process_chat_group(group, location, chat_buffer, now_time, chatting_agents, double_agents_chat))

    if chat_tasks:
        await asyncio.gather(*chat_tasks)

    monologue_tasks = []
    non_chatting_agents = [agent for agent in active_agents if agent.name not in chatting_agents]
    if non_chatting_agents and random.random() < 0.3:
        agent_to_think = random.choice(non_chatting_agents)
        agent_context = {
            'name': agent_to_think.name, 'mbti': agent_to_think.MBTI, 'persona': agent_to_think.persona_summary,
            'location': agent_to_think.curr_place, 'action': agent_to_think.curr_action,
            'now_time': now_time, 'memory': agent_to_think.memory, 'eq_ctx': None
        }
        monologue_tasks.append(process_monologue(agent_to_think, agent_context, generate_inner_monologue))

    if monologue_tasks:
        await asyncio.gather(*monologue_tasks)

async def process_chat_group(group, location, chat_buffer, now_time, chatting_agents, double_agents_chat):
    """处理单个聊天群组的异步函数。"""
    for agent in group:
        if agent.curr_action != "聊天": agent.interrupt_action()
        agent.curr_action = "聊天"
        chatting_agents.add(agent.name)
        if hasattr(agent, "_enter_thinking"):
            agent._enter_thinking()
    a1, a2 = random.sample(group, 2)

    chat_context = {
        'location': location, 'now_time': now_time, 'history': [], 'eq_ctx': None,
        'agent1': {'name': a1.name, 'mbti': a1.MBTI, 'persona': a1.persona_summary, 'memory': a1.memory, 'action': a1.curr_action},
        'agent2': {'name': a2.name, 'mbti': a2.MBTI, 'persona': a2.persona_summary, 'memory': a2.memory, 'action': a2.curr_action}
    }
    
    try:
        _, new_dialogs = await double_agents_chat(chat_context)
    finally:
        for agent in group:
            if hasattr(agent, "_exit_thinking"):
                agent._exit_thinking()
    if new_dialogs:
        dialogue_str = " ".join([f"[{speaker}]: '{dialogue}'" for speaker, dialogue in new_dialogs])
        chat_buffer[location] = dialogue_str
        
        chat_json = json.dumps(new_dialogs, ensure_ascii=False)
        for agent in group:
            agent.memory += f"\n[聊天記錄] 與 {'、'.join([p.name for p in group if p.id != agent.id])} 的對話: {chat_json}"

async def process_monologue(agent, agent_context, generate_inner_monologue):
    """处理单个独白的异步函数。"""
    if hasattr(agent, "_enter_thinking"):
        agent._enter_thinking()
    try:
        _, monologue = await generate_inner_monologue(agent_context)
    finally:
        if hasattr(agent, "_exit_thinking"):
            agent._exit_thinking()
    agent.current_thought = monologue


# ---------------------------------------------------------------------------
# 新增：依據行程產生「移動」與「互動」指令
# ---------------------------------------------------------------------------

async def generate_action_instructions(all_agents):
    """
    根據每個代理人的狀態，產生移動或互動指令，供前端解析。

    回傳格式為::

        [
            {"agent": 名稱, "command": "move", "origin": 前一地點, "destination": 目標地點, "next_step": 下一個節點},
            {"agent": 名稱, "command": "interact", "origin": 當前地點, "destination": 目標地點, "action": 行動描述},
            ...
        ]
    """

    instructions = []
    for agent in all_agents:
        sync_events = list(getattr(agent, "sync_events", []))
        if sync_events:
            for event in sync_events:
                if event.get("type") == "teleport":
                    instructions.append({
                        "agent": agent.name,
                        "command": "teleport",
                        "fromPortal": event.get("fromPortal"),
                        "toPortal": event.get("toPortal"),
                        "destination": event.get("finalLocation"),
                        "target": event.get("targetPlace"),
                    })
            agent.sync_events.clear()
        origin = getattr(agent, 'previous_place', agent.curr_place)
        destination = agent.target_place or agent.curr_place
        if origin and destination and origin != destination:
            instructions.append({
                "agent": agent.name,
                "command": "move",
                "origin": origin,
                "destination": destination,
                "next_step": agent.curr_place or destination,
                "action": agent.curr_action,
            })
        else:
            instructions.append({
                "agent": agent.name,
                "command": "interact",
                "origin": agent.curr_place,
                "destination": destination,
                "action": agent.curr_action,
            })
    return instructions
// Scripts/DataModels.cs (修正版，已補全所有資料結構)

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// --- 發送到後端 ---

[Serializable]
public class SimulationStartCommand 
{
    [JsonProperty("command")] public string Command = "start_simulation";
    [JsonProperty("params")] public SimulationParameters Params;
}

[Serializable]
public class SimulationParameters
{
    [JsonProperty("duration")] public int Duration;
    [JsonProperty("step")] public int Step;
    [JsonProperty("year")] public int Year;
    [JsonProperty("month")] public int Month;
    [JsonProperty("day")] public int Day;
    [JsonProperty("hour")] public int Hour;
    [JsonProperty("minute")] public int Minute;
    [JsonProperty("mbti")] public List<string> Mbti;
    [JsonProperty("locations")] public List<string> Locations;
    [JsonProperty("eq_enabled")] public bool EqEnabled;
    [JsonProperty("eq_json")] public string EqJson;
    [JsonProperty("eq_step")] public int EqStep;
    [JsonProperty("use_default_calendar")] public bool UseDefaultCalendar;
        // ### 核心修正：新增一個欄位來傳遞初始位置 ###
    // 這是一個 代理人名稱 -> 地點名稱 的字典
    [JsonProperty("initial_positions")]
    public Dictionary<string, string> InitialPositions;

    // Removed invalid implicit operator for SimulationParameters
}

// --- 從後端接收 ---

[Serializable]
public class WebSocketMessage
{
    [JsonProperty("type")] public string Type;
    [JsonProperty("data")] public JToken Data;
    [JsonProperty("message")] public string Message;
}

[Serializable]
public class UpdateData
{
    [JsonProperty("mainLog")] public string MainLog;
    [JsonProperty("historyLog")] public string HistoryLog;
    [JsonProperty("agentStates")] public Dictionary<string, AgentState> AgentStates;
    [JsonProperty("buildingStates")] public Dictionary<string, BuildingState> BuildingStates;
    [JsonProperty("llmLog")] public string LlmLog;
    [JsonProperty("status")] public string Status;
    [JsonProperty("agentActions")] public List<AgentActionInstruction> AgentActions;
}

[Serializable]
public class AgentState 
{
    [JsonProperty("name")] public string Name;
    [JsonProperty("currentState")] public string CurrentState;
    [JsonProperty("location")] public string Location;
    [JsonProperty("hp")] public int Hp;
    [JsonProperty("schedule")] public string Schedule;
    [JsonProperty("memory")] public string Memory;
    [JsonProperty("weeklySchedule")] public Dictionary<string, string> WeeklySchedule;
    [JsonProperty("dailySchedule")] public List<List<string>> DailySchedule;
}

[Serializable]
public class BuildingState
{
    [JsonProperty("id")] public string Id;
    [JsonProperty("integrity")] public float Integrity;
}
[Serializable]
public class AgentActionInstruction
{
    [JsonProperty("agent")] public string Agent;
    [JsonProperty("command")] public string Command;
    [JsonProperty("origin")] public string Origin;
    [JsonProperty("destination")] public string Destination;
    [JsonProperty("to_portal")] public string ToPortal;
    [JsonProperty("next_step")] public string NextStep;
    [JsonProperty("action")] public string Action;
}

/// <summary>
/// 【核心修正】
/// 新增這個 class 以解決 "找不到類型" 的編譯錯誤。
/// 這個結構對應後端在 type="earthquake" 時發送的資料。
/// </summary>
[Serializable]
public class EarthquakeData
{
    [JsonProperty("agentStates")] public Dictionary<string, AgentState> AgentStates;
    [JsonProperty("buildingStates")] public Dictionary<string, BuildingState> BuildingStates;
    [JsonProperty("intensity")] public float Intensity;
}

[Serializable]
public class EvaluationReport
{
    [JsonProperty("scores")] public Dictionary<string, ScoreDetail> Scores;
    [JsonProperty("text")] public string Text;
}

[Serializable]
public class ScoreDetail
{
    [JsonProperty("loss_score")] public float LossScore;
    [JsonProperty("response_score")] public float ResponseScore;
    [JsonProperty("coop_score")] public float CoopScore;
    [JsonProperty("total_score")] public float TotalScore;
    [JsonProperty("notes")] public string Notes;
}
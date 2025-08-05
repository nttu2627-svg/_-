// Scripts/DataModels.cs (更新版)
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// 发送到后端
[Serializable]
public class SimulationStartCommand { [JsonProperty("command")] public string Command = "start_simulation"; [JsonProperty("params")] public SimulationParameters Params; }

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
    [JsonProperty("locations")] public List<string> Locations; // ### 新增 ###
    [JsonProperty("eq_enabled")] public bool EqEnabled;
    [JsonProperty("eq_json")] public string EqJson;
    [JsonProperty("eq_step")] public int EqStep;
}

// 从后端接收
[Serializable]
public class WebSocketMessage { [JsonProperty("type")] public string Type; [JsonProperty("data")] public UpdateData Data; [JsonProperty("message")] public string Message; }

[Serializable]
public class UpdateData
{
    [JsonProperty("mainLog")] public string MainLog;
    [JsonProperty("historyLog")] public string HistoryLog;
    [JsonProperty("agentStates")] public Dictionary<string, AgentState> AgentStates;
    [JsonProperty("buildingStates")] public Dictionary<string, BuildingState> BuildingStates;
    [JsonProperty("llmLog")] public string LlmLog;
    [JsonProperty("status")] public string Status;
}

[Serializable]
public class AgentState { [JsonProperty("name")] public string Name; [JsonProperty("currentState")] public string CurrentState; [JsonProperty("location")] public string Location; [JsonProperty("hp")] public int Hp; [JsonProperty("schedule")] public string Schedule; [JsonProperty("memory")] public string Memory; [JsonProperty("weeklySchedule")] public Dictionary<string, string> WeeklySchedule; [JsonProperty("dailySchedule")] public List<List<string>> DailySchedule; }

[Serializable]
public class BuildingState { [JsonProperty("id")] public string Id; [JsonProperty("integrity")] public float Integrity; }
// Scripts/DataModels.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// DataModels 是純資料結構，不需要 UnityEngine 或 Threading
// 除非你有用到 Vector3，否則 UnityEngine 也可以移除，這裡保留以防萬一

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
    [JsonProperty("initial_positions")]
    public Dictionary<string, string> InitialPositions;
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
    [JsonProperty("stepId")] public int StepId;

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

// ---------------------------------------------------------------------------
// Action-driven synchronisation protocol models
// ---------------------------------------------------------------------------

[Serializable]
public class ServerCommandEnvelope
{
    [JsonProperty("command")] public string Command;
}

[Serializable]
public class AgentInitData
{
    [JsonProperty("id")] public string Id;
    [JsonProperty("pos")] public List<float> Position;
}

[Serializable]
public class InitializeAgentsCommand : ServerCommandEnvelope
{
    [JsonProperty("agents")] public List<AgentInitData> Agents;
}

[Serializable]
public class AgentAction
{
    [JsonProperty("id")] public string Id;
    [JsonProperty("type")] public string Type;
    [JsonProperty("path")] public List<List<float>> Path;
    [JsonProperty("pos")] public List<float> Position;
}

[Serializable]
public class ExecuteActionsCommand : ServerCommandEnvelope
{
    [JsonProperty("step")] public int Step;
    [JsonProperty("actions")] public List<AgentAction> Actions;
}

[Serializable]
public class ClientMessage
{
    [JsonProperty("status")] public string Status;

    [JsonProperty("agent_id", NullValueHandling = NullValueHandling.Ignore)]
    public string AgentId;

    [JsonProperty("step", NullValueHandling = NullValueHandling.Ignore)]
    public int? Step;

    public static ClientMessage InitializationComplete()
    {
        return new ClientMessage { Status = "initialization_complete", Step = null };
    }

    public static ClientMessage ActionComplete(string agentId, int step)
    {
        return new ClientMessage
        {
            Status = "action_complete",
            AgentId = agentId,
            Step = step
        };
    }
}
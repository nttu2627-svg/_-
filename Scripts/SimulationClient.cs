// Scripts/SimulationClient.cs (功能健壯最終版)

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq; // 確保引用 JToken

public class SimulationClient : MonoBehaviour
{
    [Header("Connection Settings")]
    public string serverUrl = "ws://localhost:8765";

    [Header("Scene References")]
    public Transform characterRoot;
    public Transform locationRoot;
    public Transform buildingRoot;
    
    // 私有變數
    private WebSocket websocket;
    private readonly Queue<Action> _mainThreadActions = new Queue<Action>();
    private readonly Dictionary<string, AgentController> _sceneAgentControllers = new Dictionary<string, AgentController>();
    private readonly Dictionary<string, AgentController> _activeAgentControllers = new Dictionary<string, AgentController>();
    private readonly Dictionary<string, Transform> _locationTransforms = new Dictionary<string, Transform>();
    private readonly Dictionary<string, BuildingController> _buildingControllers = new Dictionary<string, BuildingController>();
    private List<Collider2D> _boundsColliders = new List<Collider2D>();


    // --- 全局靜態事件 ---
    // 外部腳本 (如 UIController) 可以訂閱這些事件來接收更新
    public static event Action<string> OnStatusUpdate;
    public static event Action<UpdateData> OnLogUpdate;
    public static event Action<EvaluationReport> OnEvaluationReceived;
    public static event Action<float> OnEarthquake; // 地震事件

    [Obsolete]
    void Start()
    {
        Debug.Log("[SimulationClient] Starting...");
        InitializeSceneReferences();
        _ = ConnectToServer();
    }
    
    void Update()
    {
        // 在主線程中執行來自網路線程的任務
        lock (_mainThreadActions)
        {
            while (_mainThreadActions.Count > 0)
            {
                var action = _mainThreadActions.Dequeue();
                try { action?.Invoke(); }
                catch (Exception e) { Debug.LogError($"[SimulationClient] EXCEPTION in Main Thread Action: {e}"); }
            }
        }
        
        #if !UNITY_WEBGL || UNITY_EDITOR
        if (websocket != null) { websocket.DispatchMessageQueue(); }
        #endif
    }
    
    private void OnApplicationQuit()
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            Debug.Log("[SimulationClient] Application quitting, closing WebSocket connection...");
            _ = websocket.Close();
        }
    }

    [Obsolete]
    private void InitializeSceneReferences()
    {
        // ... (這部分與您提供的程式碼完全相同，無需修改)
        Debug.Log("[SimulationClient] Initializing scene references...");
        if (locationRoot != null)
        {
            foreach (Transform location in locationRoot) { _locationTransforms[location.name] = location; }
            Debug.Log($"[SimulationClient] Registered {_locationTransforms.Count} locations.");
        }
        if (characterRoot != null)
        {
            foreach (AgentController agent in characterRoot.GetComponentsInChildren<AgentController>(true))
            {
                string standardizedName = agent.agentName.ToUpper();
                if (!_sceneAgentControllers.ContainsKey(standardizedName))
                {
                    _sceneAgentControllers[standardizedName] = agent;
                    agent.Initialize(_locationTransforms);
                    agent.gameObject.SetActive(false);
                }
            }
            Debug.Log($"[SimulationClient] Registered {_sceneAgentControllers.Count} total agent controllers.");
        }
        if (buildingRoot != null)
        {
            foreach (BuildingController building in buildingRoot.GetComponentsInChildren<BuildingController>(true))
            {
                if (!string.IsNullOrEmpty(building.buildingName)) { _buildingControllers[building.buildingName] = building; }
            }
            Debug.Log($"[SimulationClient] Registered {_buildingControllers.Count} building controllers.");
        }
        
        // 收集場景中所有名稱包含 "Bounds" 的 Collider2D，避免隨機傳送到不可到達區域。
        _boundsColliders.Clear();
        foreach (Collider2D col in FindObjectsOfType<Collider2D>())
        {
            if (col.gameObject.name.Contains("Bounds"))
            {
                _boundsColliders.Add(col);
            }
        }
    }

    private async Task ConnectToServer()
    {
        websocket = new WebSocket(serverUrl);
        websocket.OnOpen += () => EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke("已連接到伺服器"));
        websocket.OnError += (e) => EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke($"錯誤: {e}"));
        websocket.OnClose += (e) => EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke($"與伺服器斷開連接 (代碼: {e})"));
        
        websocket.OnMessage += (bytes) => {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"[SimulationClient] Raw message received:\n{message}");
            try
            {
                var wsMessage = JsonConvert.DeserializeObject<WebSocketMessage>(message);
                // 將反序列化後的訊息排入主線程佇列等待處理
                EnqueueMainThreadAction(() => ProcessMessageOnMainThread(wsMessage));
            }
            catch (Exception e)
            {
                EnqueueMainThreadAction(() => Debug.LogError($"[SimulationClient] JSON Deserialization failed: {e.Message}\nRawData: {message}"));
            }
        };
        await websocket.Connect();
    }
    
    /// <summary>
    /// 在主線程中安全地處理來自 WebSocket 的訊息。
    /// </summary>
    private void ProcessMessageOnMainThread(WebSocketMessage wsMessage)
    {
        if (wsMessage == null) return;
        try
        {
            // 使用 switch 根據訊息類型，將 Data 反序列化為對應的具體類別
            switch (wsMessage.Type)
            {
                case "update":
                    if (wsMessage.Data != null)
                    {
                        var updateData = wsMessage.Data.ToObject<UpdateData>();
                        OnStatusUpdate?.Invoke(updateData.Status);
                        OnLogUpdate?.Invoke(updateData);
                        UpdateAllAgentStates(updateData.AgentStates);
                        UpdateAllBuildingStates(updateData.BuildingStates);
                    }
                    break;
                    
                case "evaluation":
                    if (wsMessage.Data != null)
                    {
                        var evalData = wsMessage.Data.ToObject<EvaluationReport>();
                        OnEvaluationReceived?.Invoke(evalData);
                    }
                    break;

                case "earthquake":
                    if (wsMessage.Data != null)
                    {
                        var quakeData = wsMessage.Data.ToObject<EarthquakeData>();
                        UpdateAllAgentStates(quakeData.AgentStates);
                        UpdateAllBuildingStates(quakeData.BuildingStates);
                        OnEarthquake?.Invoke(quakeData.Intensity);
                    }
                    break;

                case "status": 
                case "error": 
                case "end":
                    OnStatusUpdate?.Invoke(wsMessage.Message);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimulationClient] Error processing message on main thread (Type: {wsMessage.Type}): {e}");
        }
    }

    private void EnqueueMainThreadAction(Action action)
    {
        lock (_mainThreadActions) { _mainThreadActions.Enqueue(action); }
    }

    private void UpdateAllAgentStates(Dictionary<string, AgentState> agentStates)
    {
        if (agentStates == null) return;
        foreach (var activeControllerPair in _activeAgentControllers)
        {
            if (agentStates.TryGetValue(activeControllerPair.Key, out AgentState state))
            {
                activeControllerPair.Value.UpdateState(state);
            }
        }
    }
    
    private void UpdateAllBuildingStates(Dictionary<string, BuildingState> buildingStates)
    {
        if (buildingStates == null) return;
        foreach (var buildingStatePair in buildingStates)
        {
            if (_buildingControllers.TryGetValue(buildingStatePair.Key, out BuildingController controller))
            {
                controller.UpdateState(buildingStatePair.Value);
            }
        }
    }
    private bool IsInsideBounds(Vector3 position)
    {
        foreach (var col in _boundsColliders)
        {
            if (col.bounds.Contains(position)) return true;
        }
        return false;
    }

    private Vector3 GetRandomApartmentPosition()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            bool secondFloor = UnityEngine.Random.value > 0.5f;
            bool singleRoom = UnityEngine.Random.value > 0.5f;
            float x, y;
            if (!secondFloor)
            {
                if (singleRoom)
                {
                    x = UnityEngine.Random.Range(-130.4f, -112.9f);
                    y = UnityEngine.Random.Range(-93.9f, -46.9f);
                }
                else
                {
                    x = UnityEngine.Random.Range(-96f, -80.4f);
                    y = UnityEngine.Random.Range(-86.9f, -46.4f);
                }
            }
            else
            {
                if (singleRoom)
                {
                    x = UnityEngine.Random.Range(-194.01f, -179.52f);
                    y = UnityEngine.Random.Range(-93.52f, -45.09f);
                }
                else
                {
                    x = UnityEngine.Random.Range(-224.69f, -210.07f);
                    y = UnityEngine.Random.Range(-87.37f, -46.16f);
                }
            }

            Vector3 candidate = new Vector3(x, y, 0f);
            if (!IsInsideBounds(candidate))
            {
                return candidate;
            }
        }
        return Vector3.zero;
    }

    private void TeleportActiveAgentsToApartmentArea()
    {
        foreach (var controller in _activeAgentControllers.Values)
        {
            controller.TeleportTo(GetRandomApartmentPosition());
        }
    }

    public async void StartSimulation(SimulationParameters parameters)
    {
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            OnStatusUpdate?.Invoke("錯誤：未連接到伺服器");
            return;
        }
        
        Debug.Log("[SimulationClient] Activating selected agents for simulation...");
        _activeAgentControllers.Clear();
        foreach (var agentName in parameters.Mbti)
        {
            string standardizedMbti = agentName.ToUpper();
            if (_sceneAgentControllers.TryGetValue(standardizedMbti, out AgentController controller))
            {
                controller.gameObject.SetActive(true);
                _activeAgentControllers[standardizedMbti] = controller;
            }
            else 
            {
                Debug.LogWarning($"[SimulationClient] Selected agent '{standardizedMbti}' not found in scene controllers.");
            }
        }
        TeleportActiveAgentsToApartmentArea();
        var command = new SimulationStartCommand { Params = parameters };
        string jsonCommand = JsonConvert.SerializeObject(command, Formatting.Indented);
        OnStatusUpdate?.Invoke("已發送模擬指令，等待後端響應...");
        await websocket.SendText(jsonCommand);
    }

    public async void SendTeleportRequest(string agentName, string targetPortalName)
    {
        // ... (這部分與您提供的程式碼完全相同，無需修改)
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("Cannot send teleport request: WebSocket is not connected.");
            return;
        }

        var command = new Dictionary<string, string>
        {
            { "command", "agent_teleport" },
            { "agent_name", agentName },
            { "target_portal_name", targetPortalName }
        };

        string jsonCommand = JsonConvert.SerializeObject(command);
        Debug.Log($"[SimulationClient] Sending teleport request: {jsonCommand}");
        await websocket.SendText(jsonCommand);
    }
}
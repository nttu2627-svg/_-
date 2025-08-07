// Scripts/SimulationClient.cs (传送功能补全最终版)

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class SimulationClient : MonoBehaviour
{
    [Header("Connection Settings")]
    public string serverUrl = "ws://localhost:8765";

    [Header("Scene References")]
    public Transform characterRoot;
    public Transform locationRoot;
    public Transform buildingRoot;
        [Header("Camera Control")]
    public CameraController cameraController;

    private WebSocket websocket;
    private readonly Queue<Action> _mainThreadActions = new Queue<Action>();

    private Dictionary<string, AgentController> _sceneAgentControllers = new Dictionary<string, AgentController>();
    private Dictionary<string, AgentController> _activeAgentControllers = new Dictionary<string, AgentController>();
    private Dictionary<string, Transform> _locationTransforms = new Dictionary<string, Transform>();
    private Dictionary<string, BuildingController> _buildingControllers = new Dictionary<string, BuildingController>();

    public static event Action<string> OnStatusUpdate;
    public static event Action<UpdateData> OnLogUpdate;
    public static event Action<EvaluationReport> OnEvaluationReceived;

    public static event Action<float> OnEarthquake;

    void Start()
    {
        Debug.Log("[SimulationClient] Starting...");
        InitializeSceneReferences();
        _ = ConnectToServer();
    }
    
    void Update()
    {
        lock (_mainThreadActions)
        {
            while (_mainThreadActions.Count > 0)
            {
                var action = _mainThreadActions.Dequeue();
                try 
                {
                    action?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SimulationClient] EXCEPTION in Main Thread Action: {e}");
                }
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

    private void InitializeSceneReferences()
    {
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
    }

    private async Task ConnectToServer()
    {
        websocket = new WebSocket(serverUrl);
        websocket.OnOpen += () => { EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke("已連接到伺服器")); };
        websocket.OnError += (e) => { EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke($"錯誤: {e}")); };
        websocket.OnClose += (e) => { EnqueueMainThreadAction(() => OnStatusUpdate?.Invoke($"與伺服器斷開連接 (代碼: {e})")); };
        websocket.OnMessage += (bytes) => {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"[SimulationClient] Raw message received:\n{message}");
            try
            {
                var wsMessage = JsonConvert.DeserializeObject<WebSocketMessage>(message);
                EnqueueMainThreadAction(() => { ProcessMessageOnMainThread(wsMessage); });
            }
            catch (Exception e)
            {
                EnqueueMainThreadAction(() => { Debug.LogError($"[SimulationClient] JSON Deserialization failed: {e.Message}"); });
            }
        };
        await websocket.Connect();
    }
    
    private void ProcessMessageOnMainThread(WebSocketMessage wsMessage)
    {
        if (wsMessage == null) return;
        try
        {
            switch (wsMessage.Type)
            {
                case "update":
                    if(wsMessage.Data != null)
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
                        UpdateAllAgentStates(wsMessage.Data.AgentStates);
                        UpdateAllBuildingStates(wsMessage.Data.BuildingStates);
                        OnEarthquake?.Invoke(wsMessage.Data.Intensity);
                    }
                    break;
                case "status": case "error": case "end":
                    OnStatusUpdate?.Invoke(wsMessage.Message);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SimulationClient] Error processing message on main thread: {e}");
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
                // 新增：轉送當前行動狀態，供控制器顯示或處理
                activeControllerPair.Value.SetActionState(state.CurrentState);
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

    public async void StartSimulation(SimulationParameters parameters)
    {
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            OnStatusUpdate?.Invoke("錯誤：未連接到伺服器");
            return;
        }
        
        Debug.Log("[SimulationClient] Activating selected agents for simulation...");
        foreach (var controller in _sceneAgentControllers.Values) { controller.gameObject.SetActive(false); }
        _activeAgentControllers.Clear();

        foreach (string mbti in parameters.Mbti)
        {
            string standardizedMbti = mbti.ToUpper();
            if (_sceneAgentControllers.TryGetValue(standardizedMbti, out AgentController controller))
            {
                controller.gameObject.SetActive(true);
                _activeAgentControllers[standardizedMbti] = controller;
                Debug.Log($"-- Activating {standardizedMbti}");
            }
            else { Debug.LogWarning($"[SimulationClient] Selected agent '{standardizedMbti}' not found in scene controllers."); }
        }

        var command = new SimulationStartCommand { Params = parameters };
        string jsonCommand = JsonConvert.SerializeObject(command, Formatting.Indented);
        OnStatusUpdate?.Invoke("已發送模擬指令，等待後端響應...");
        await websocket.SendText(jsonCommand);
    }


    public async void SendTeleportRequest(string agentName, string targetPortalName)
    {
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
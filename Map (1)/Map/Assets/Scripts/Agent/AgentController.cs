// Scripts/Agent/AgentController.cs (安全边距最终版)

using UnityEngine;
using TMPro;
using System;
using System.Collections.Generic;
using System.Globalization;

using DisasterSimulation;

public class AgentController : MonoBehaviour
{
    [Header("核心设定")]
    [Tooltip("代理人的唯一標識符，必须与其 GameObject 的名字一致，例如 'ISTJ'")]
    public string agentName;
    
    [Header("UI (可选)")]
    [Tooltip("對應此代理人在Canvas下的名字UI组件 (TextMeshProUGUI)")]
    public TextMeshProUGUI nameTextUGUI;

    [Header("氣泡 (可選)")]
    [Tooltip("顯示代理人行為的氣泡控制器")]
    public 思考氣泡控制器 bubbleController;

    // 私有变量
    private Transform _transform;
    private Dictionary<string, Transform> _locationTransforms;
    private bool _isInitialized = false;
    private Vector3 _targetPosition;
    private float _movementSpeed = 3f;
    private Camera _mainCamera;
    private SimulationClient _simulationClient; 
    private string _targetLocationName;
    private string _currentAction;
    private readonly HashSet<string> _manualLocationOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();

        if (string.IsNullOrEmpty(agentName))
        {
            agentName = gameObject.name.ToUpper();
        }
        else
        {
            agentName = agentName.ToUpper();
        }
        
        gameObject.SetActive(false);
    }
    
    public void Initialize(Dictionary<string, Transform> locations)
    {
        _locationTransforms = locations;
        _isInitialized = true;
        SetManualLocationOverrides();
    }

    void Update()
    {
        if (_isInitialized && gameObject.activeSelf)
        {
            _transform.position = Vector3.Lerp(_transform.position, _targetPosition, Time.deltaTime * _movementSpeed);
        }
    }

    void LateUpdate()
    {
        if (_isInitialized && gameObject.activeSelf && nameTextUGUI != null && _mainCamera != null)
        {
            UpdateNameplatePosition();
        }
    }

    private void UpdateNameplatePosition()
    {
        // ### 核心修正：增加一个安全边距 (padding) ###
        float padding = 0.02f; // 2% 的萤幕边距

        Vector3 viewportPoint = _mainCamera.WorldToViewportPoint(_transform.position);

        // 检查物件是否在摄影机前方，并且在加入了安全边距的视口范围内
        bool isVisible = viewportPoint.z > 0 &&
                         viewportPoint.x > padding && viewportPoint.x < 1 - padding &&
                         viewportPoint.y > padding && viewportPoint.y < 1 - padding;

        if (isVisible)
        {
            if (!nameTextUGUI.gameObject.activeSelf)
            {
                nameTextUGUI.gameObject.SetActive(true);
            }
            nameTextUGUI.transform.position = _mainCamera.WorldToScreenPoint(_transform.position + Vector3.up * 1.5f);
        }
        else
        {
            if (nameTextUGUI.gameObject.activeSelf)
            {
                nameTextUGUI.gameObject.SetActive(false);
            }
        }
    }

    private void SetManualLocationOverrides(params string[] locationAliases)
    {
        _manualLocationOverrides.Clear();

        if (locationAliases == null) return;

        foreach (string alias in locationAliases)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            _manualLocationOverrides.Add(alias.Trim());
        }
    }

    private bool ShouldRespectManualOverride(string incomingLocation)
    {
        if (_manualLocationOverrides.Count == 0 || string.IsNullOrEmpty(incomingLocation))
        {
            return false;
        }

        foreach (string alias in _manualLocationOverrides)
        {
            if (string.Equals(alias, incomingLocation, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (incomingLocation.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateState(AgentState state)
    {
        if (!_isInitialized) return;
        if (state == null) return;

        string incomingLocation = state.Location ?? string.Empty;
        incomingLocation = incomingLocation.Trim();

        if (string.IsNullOrEmpty(incomingLocation))
        {
            _currentAction = state.CurrentState;
            return;
        }

        _targetLocationName = incomingLocation;

        if (ShouldRespectManualOverride(incomingLocation))
        {
            _currentAction = state.CurrentState;
            return;
        }

        if (_manualLocationOverrides.Count > 0)
        {
            _manualLocationOverrides.Clear();
        }

        if (_locationTransforms != null && _locationTransforms.TryGetValue(incomingLocation, out Transform targetLocation))
        {
            _targetPosition = targetLocation.position;
        }
        else if (TryParseVector3(incomingLocation, out Vector3 pos))
        {
            // 如果後端傳回的是座標字串而非地點名稱，直接使用座標
            _targetPosition = pos;
            _transform.position = pos;
        }
        else if (incomingLocation == "公寓")
        {
            // 若後端傳來的地點在場景中沒有對應的標記（例如 "公寓"），
            // 則維持目前的位置，避免代理人被傳回原點。
            _targetPosition = _transform.position;
        }
        else
        {
            Debug.LogWarning($"地點 '{state.Location}' 在場景中未找到，代理人 '{agentName}' 將停在原地。");
        }
        _currentAction = state.CurrentState;
    }
    private static bool TryParseVector3(string input, out Vector3 result)
    {
        result = Vector3.zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();
        input = input.Trim('(', ')');
        string[] parts = input.Split(',');
        if (parts.Length != 3) return false;
        float x, y, z;
        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
        if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

        result = new Vector3(x, y, z);
        return true;
    }

    /// <summary>
    /// 立即將代理人傳送到指定位置，並同步目標位置。
    /// </summary>
    public void TeleportTo(Vector3 position, params string[] locationAliases)
    {
        _transform.position = position;
        _targetPosition = position;
        SetManualLocationOverrides(locationAliases);
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        string bubbleText = null;
        if (!string.IsNullOrEmpty(action))
        {
            string lower = action.ToLower();
            if (lower.Contains("chat") || lower.Contains("聊天")) bubbleText = "・・・";
            else if (lower.Contains("rest") || lower.Contains("休息")) bubbleText = "😴";
            else if (lower.Contains("move") || lower.Contains("移動")) bubbleText = "🏃";
        }
        if (!string.IsNullOrEmpty(bubbleText) && bubbleController != null)
        {
            bubbleController.顯示氣泡(bubbleText, _transform);
        }
    }
    
    private void OnTriggerEnter2D(Collider2D other)
    {
        PortalController portal = other.GetComponent<PortalController>();
        if (portal != null && _simulationClient != null && other.name == _targetLocationName)
        {
            Debug.Log($"[Agent {agentName}] 已到達傳送門 '{other.name}', 請求傳送到 '{portal.targetPortalName}'");
            _simulationClient.SendTeleportRequest(agentName, portal.targetPortalName);
        }
    }
    
    public void SetVisibility(bool isVisible)
    {
        gameObject.SetActive(isVisible);
    }

    void OnEnable()
    {
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
    }

    void OnDisable()
    {
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
    }
}
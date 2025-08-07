// Scripts/Agent/AgentController.cs (安全边距最终版)

using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class AgentController : MonoBehaviour
{
    [Header("核心设定")]
    [Tooltip("代理人的唯一標識符，必须与其 GameObject 的名字一致，例如 'ISTJ'")]
    public string agentName;
    
    [Header("UI (可选)")]
    [Tooltip("對應此代理人在Canvas下的名字UI组件 (TextMeshProUGUI)")]
    public TextMeshProUGUI nameTextUGUI;

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

    public void UpdateState(AgentState state)
    {
        if (!_isInitialized) return;
        _targetLocationName = state.Location;
        if (_locationTransforms.TryGetValue(state.Location, out Transform targetLocation))
        {
            _targetPosition = targetLocation.position;
        }
        else
        {
            Debug.LogWarning($"地點 '{state.Location}' 在場景中未找到，代理人 '{agentName}' 將停在原地。");
        }
        _currentAction = state.CurrentState;
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        // 目前僅記錄動作，可在此觸發動畫或 UI 顯示
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
    }
}
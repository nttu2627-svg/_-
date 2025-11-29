using UnityEngine;
using UnityEngine.AI;
using TMPro;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DisasterSimulation;
using Cysharp.Threading.Tasks;
using System.Threading;


// 定義指令類型與結構
public enum AgentInternalCommandType { Move, Teleport, ActionOnly }
public class AgentCommand
{
    public AgentInternalCommandType Type;    
    public Vector3 TargetPosition;
    public string TargetLocationName;
    public Transform TargetTransform;
    public string ActionName;
    public bool UseTeleport;
}

public class AgentController : MonoBehaviour
{
    [HideInInspector]
    public string agentName;

    [HideInInspector]
    public TextMeshProUGUI nameTextUGUI;

    [Header("氣泡 (可選)")]
    [Tooltip("顯示代理人行為的氣泡控制器")]
    public 思考氣泡控制器 bubbleController;

    // 私有變數
    private Transform _transform;
    private Dictionary<string, Transform> _locationTransforms;
    private Dictionary<string, Transform> _normalizedLocationLookup;
    private readonly Dictionary<string, Collider2D> _locationColliders = new Dictionary<string, Collider2D>();
    private bool _isInitialized = false;
    
    // 移動與狀態相關
    private Vector3 _targetPosition;
    private float _movementSpeed = 4.5f;
    private float _arrivalThreshold = 0.05f;
    private Vector3 _lastPosition;
    private UnityEngine.AI.NavMeshAgent _navMeshAgent;
    private Vector3 _smoothedVelocity;
    private float _interpolationSpeed = 10f;
    // 視覺控制 (需確保場景中有對應組件)
    private AgentVisualController _visualController; 
    
    private float _lastStateApplyTime = -999f;
    // [已移除 unused] private float _minStateApplyInterval; 

    private Camera _mainCamera;
    private SimulationClient _simulationClient;
    private AgentMovementController _movementController; 
    private string _targetLocationName;
    private string _lastValidLocationName;
    private string _currentAction;
    private readonly HashSet<string> _manualLocationOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private const float CoordinateSnapThreshold = 8f;
    private string _lastInstructionDestination;
    
    private static readonly HashSet<string> UnknownLocationAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "未知地點", "未知地点", "未知位置", "未知區域", 
        "unknown", "unknown location", "unknown place"
    };

    private Color _idleNameColor = Color.white;
    private Color _activeNameColor = new Color32(255, 204, 102, 255);
    private string _statusLabel = "待機";
    private string _displayName;
    private bool _isCurrentlySleeping = false;
    private bool _awaitingMovementBatch = false;
    private bool _isPortalPaused = false;
    private bool _navMeshDriving = false;
    private enum AgentBehaviourState
    {
        Idle,
        Moving,
        Interacting
    }

    private AgentBehaviourState _currentBehaviourState = AgentBehaviourState.Idle;
    private CancellationTokenSource _idleCts;
    [SerializeField, Tooltip("待機時隨機小動作的徘徊半徑")]
    private float _idleWanderRadius = 1.5f;
    private static readonly (string english, string localized)[] LocationPrefixAliases = new (string, string)[]
    {
        ("Apartment", "公寓"), ("Apartment_F1", "公寓一樓"), ("Apartment_F2", "公寓二樓"),
        ("School", "學校"), ("Gym", "健身房"), ("Rest", "餐廳"),
        ("Super", "超市"), ("Subway", "地鐵"), ("Exterior", "室外")
    };

    // 佇列系統
    private Queue<AgentCommand> _commandQueue = new Queue<AgentCommand>();
    private CancellationTokenSource _cts;
    // [已移除 unused] private bool _isProcessing;

    // 公開屬性
    public AgentMovementController MovementController => _movementController;

    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();

        // 初始化位置記錄，避免第一幀計算速度時暴衝
        _lastPosition = _transform.position;
        _targetPosition = _transform.position;
        _smoothedVelocity = Vector3.zero;
        agentName = string.IsNullOrEmpty(agentName)
            ? gameObject.name.ToUpper()
            : agentName.ToUpper();

        // 初始化 MovementController
        if (!TryGetComponent(out _movementController))
        {
            _movementController = gameObject.AddComponent<AgentMovementController>();
        }
        _movementController.ConfigureFromAgent(this, _movementSpeed, _arrivalThreshold);
        _navMeshAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (_navMeshAgent != null)
        {
            _navMeshAgent.updateRotation = false;
            _navMeshAgent.updateUpAxis = false;
            _navMeshAgent.speed = _movementSpeed;
            _navMeshAgent.stoppingDistance = Mathf.Max(_arrivalThreshold, _navMeshAgent.stoppingDistance);
        }
        if (!TryGetComponent(out _navMeshAgent))
        {
            _navMeshAgent = gameObject.AddComponent<UnityEngine.AI.NavMeshAgent>();
        }
        ConfigureNavMeshAgent();
        // ======== 補上：初始化 VisualController ========
        if (!TryGetComponent(out _visualController))
        {
            // 如果沒有掛載，嘗試掛一個或是報錯，這裡假設使用者會手動掛載或自動添加
            // 為了不報錯，我們先嘗試 GetComponentInChildren
            _visualController = GetComponentInChildren<AgentVisualController>();
            if (_visualController == null)
            {
                 // 如果真的沒有，這裡可能需要 AddComponent 或是僅輸出警告
                 // Debug.LogWarning($"[Agent {agentName}] 缺少 AgentVisualController!");
            }
        }
        // ===============================================

        _displayName = nameTextUGUI != null && !string.IsNullOrEmpty(nameTextUGUI.text)
            ? nameTextUGUI.text
            : agentName;

        if (bubbleController == null)
        {
            bubbleController = GetComponentInChildren<思考氣泡控制器>(true);
            if (bubbleController == null)
            {
                Debug.LogWarning($"[Agent {agentName}] 場景中找不到思考氣泡控制器，將無法顯示行動提示。", this);
            }
        }

        ShowIdleStatus();
        
        // 啟動指令循環
        _cts = new CancellationTokenSource();
        ProcessCommandBufferLoop(_cts.Token).Forget();

        gameObject.SetActive(false);
        SetBehaviourState(AgentBehaviourState.Idle);
    }

    private void OnDestroy()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    public void Initialize(Dictionary<string, Transform> locations)
    {
        _locationTransforms = locations;
        _normalizedLocationLookup = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        _locationColliders.Clear();
        if (_locationTransforms != null)
        {
            foreach (var pair in _locationTransforms)
            {
                if (pair.Value == null) continue;

                AddNormalizedLocationKey(pair.Key, pair.Value);

                if (_locationColliders.ContainsKey(pair.Key)) continue;
                Collider2D collider = pair.Value.GetComponentInChildren<Collider2D>();
                if (collider != null)
                {
                    _locationColliders[pair.Key] = collider;
                }
            }
        }
        _isInitialized = true;
        SetManualLocationOverrides();
        SetBehaviourState(AgentBehaviourState.Moving);

        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
        {
            // 將目的地設為當前位置，確保初始化時不會意外移動
            _navMeshAgent.SetDestination(_transform.position);
        }
        else if (_movementController != null)
        {
            _movementController.RegisterLocations(_locationTransforms);
        }
        if (TryResolveCoordinateLocation(_transform.position, out string resolvedName, out Vector3 resolvedPosition, out _))
        {
            _lastValidLocationName = resolvedName;
            _targetLocationName = resolvedName;
            _targetPosition = resolvedPosition;
        }
    }

    void Update()
    {
        if (!_isInitialized || !gameObject.activeSelf) return;
        if (_isPortalPaused) return;
        Vector3 sampledVelocity = (_transform.position - _lastPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled && _navMeshDriving)
        {
            sampledVelocity = _navMeshAgent.velocity;
            _targetPosition = _navMeshAgent.destination;
        }

        _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, sampledVelocity, Time.deltaTime * _interpolationSpeed);

        if (_visualController != null)
        {
            _visualController.UpdateVisuals(_smoothedVelocity);
        }

        _lastPosition = _transform.position;

        if (_navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled)
        {
            // 物理移動 (作為最後防線)
            Vector3 currentPosition = _transform.position;
            Vector3 toTarget = _targetPosition - currentPosition;
            float arrivalThresholdSqr = _arrivalThreshold * _arrivalThreshold;

            if (toTarget.sqrMagnitude <= arrivalThresholdSqr)
            {
                _transform.position = _targetPosition;
                return;
            }

            _transform.position = Vector3.MoveTowards(
                currentPosition,
                _targetPosition,
                Mathf.Max(0f, _movementSpeed) * Time.deltaTime);
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
        float padding = 0.02f; 
        Vector3 viewportPoint = _mainCamera.WorldToViewportPoint(_transform.position);

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

    private void SetSleepState(bool sleeping)
    {
        _isCurrentlySleeping = sleeping;
    }

    private void SetManualLocationOverrides(params string[] locationAliases)
    {
        _manualLocationOverrides.Clear();

        if (locationAliases == null) return;

        foreach (string alias in locationAliases)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            if (IsUnknownLocation(alias)) continue;
            _manualLocationOverrides.Add(alias.Trim());
        }
    }
    private void ConfigureNavMeshAgent()
    {
        if (_navMeshAgent == null) return;

        _navMeshAgent.updateRotation = false;
        _navMeshAgent.updateUpAxis = false;
        _navMeshAgent.speed = Mathf.Max(0.1f, _movementSpeed);
        _navMeshAgent.stoppingDistance = Mathf.Max(0.01f, _arrivalThreshold);
        _navMeshAgent.acceleration = Mathf.Max(1f, _movementSpeed * 2f);
        _navMeshAgent.autoBraking = true;
        _navMeshAgent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance;

        if (_navMeshAgent.enabled)
        {
            _navMeshAgent.Warp(_transform.position);
            _navMeshAgent.ResetPath();
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

    private static string NormalizeLocationKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '（' || c == '）') continue;
            builder.Append(char.ToUpperInvariant(c));
        }
        return builder.ToString();
    }

    private static bool IsUnknownLocation(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string trimmed = value.Trim();
        if (UnknownLocationAliases.Contains(trimmed)) return true;
        string lower = trimmed.ToLowerInvariant();
        if (UnknownLocationAliases.Contains(lower)) return true;
        if (lower.Contains("未知") || lower.Contains("unknown")) return true;
        return false;
    }

    private void AddNormalizedLocationKey(string key, Transform transform)
    {
        if (_normalizedLocationLookup == null || string.IsNullOrWhiteSpace(key) || transform == null) return;
        string normalized = NormalizeLocationKey(key);
        if (!string.IsNullOrEmpty(normalized) && !_normalizedLocationLookup.ContainsKey(normalized))
        {
            _normalizedLocationLookup[normalized] = transform;
        }
    }

    private bool TryGetNormalizedLocation(string normalizedKey, out Transform transform)
    {
        transform = null;
        if (_normalizedLocationLookup == null || string.IsNullOrEmpty(normalizedKey)) return false;
        if (_normalizedLocationLookup.TryGetValue(normalizedKey, out Transform cached) && cached != null)
        {
            transform = cached;
            return true;
        }
        return false;
    }

    internal bool TryFindLocationTransform(string locationName, out Transform transform)
    {
        transform = null;
        if (_locationTransforms == null || string.IsNullOrWhiteSpace(locationName)) return false;

        if (_locationTransforms.TryGetValue(locationName, out Transform direct) && direct != null)
        {
            transform = direct;
            return true;
        }

        string trimmed = locationName.Trim();
        if (!string.Equals(trimmed, locationName, StringComparison.Ordinal) &&
            _locationTransforms.TryGetValue(trimmed, out Transform trimmedMatch) && trimmedMatch != null)
        {
            transform = trimmedMatch;
            return true;
        }


        foreach (var (english, localized) in LocationPrefixAliases)
        {
            if (trimmed.StartsWith(english, StringComparison.OrdinalIgnoreCase))
            {
                string alias = localized + trimmed.Substring(english.Length);
                if (_locationTransforms.TryGetValue(alias, out Transform mapped) && mapped != null)
                {
                    transform = mapped;
                    return true;
                }

                string aliasNormalized = NormalizeLocationKey(alias);
                if (TryGetNormalizedLocation(aliasNormalized, out Transform normalizedMatch))
                {
                    transform = normalizedMatch;
                    return true;
                }

                alias = localized + trimmed.Substring(english.Length).TrimStart('_');
                if (_locationTransforms.TryGetValue(alias, out mapped) && mapped != null)
                {
                    transform = mapped;
                    return true;
                }

                aliasNormalized = NormalizeLocationKey(alias);
                if (TryGetNormalizedLocation(aliasNormalized, out normalizedMatch))
                {
                    transform = normalizedMatch;
                    return true;
                }
            }

            if (trimmed.StartsWith(localized, StringComparison.OrdinalIgnoreCase))
            {
                string alias = english + trimmed.Substring(localized.Length);
                if (_locationTransforms.TryGetValue(alias, out Transform mapped) && mapped != null)
                {
                    transform = mapped;
                    return true;
                }

                string aliasNormalized = NormalizeLocationKey(alias);
                if (TryGetNormalizedLocation(aliasNormalized, out Transform normalizedMatch))
                {
                    transform = normalizedMatch;
                    return true;
                }

                alias = english + trimmed.Substring(localized.Length).TrimStart('_');
                if (_locationTransforms.TryGetValue(alias, out mapped) && mapped != null)
                {
                    transform = mapped;
                    return true;
                }

                aliasNormalized = NormalizeLocationKey(alias);
                if (TryGetNormalizedLocation(aliasNormalized, out normalizedMatch))
                {
                    transform = normalizedMatch;
                    return true;
                }
            }
        }

        string normalizedKey = NormalizeLocationKey(trimmed);
        if (TryGetNormalizedLocation(normalizedKey, out Transform normalizedTransform))
        {
            transform = normalizedTransform;
            return true;
        }

        foreach (var pair in _locationTransforms)
        {
            if (pair.Value == null || string.IsNullOrEmpty(pair.Key)) continue;
            if (pair.Key.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                transform = pair.Value;
                return true;
            }
        }
        return false;
    }

    private string ResolveLocationKey(string requestedName, Transform transform)
    {
        if (_locationTransforms != null && !string.IsNullOrWhiteSpace(requestedName) &&
            _locationTransforms.ContainsKey(requestedName))
        {
            return requestedName;
        }
        return transform != null ? transform.name : requestedName;
    }

    private string DetermineBuildingFromTransform(Transform target)
    {
        if (target == null) return null;
        BuildingController building = target.GetComponentInParent<BuildingController>();
        if (building != null && !string.IsNullOrEmpty(building.buildingName))
        {
            return building.buildingName;
        }
        return null;
    }

    internal string GetBuildingFromTransform(Transform target) => DetermineBuildingFromTransform(target);

    internal string GuessBuildingForLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName)) return null;
        if (TryFindLocationTransform(locationName, out Transform locationTransform) && locationTransform != null)
        {
            return DetermineBuildingFromTransform(locationTransform);
        }
        return null;
    }

    internal string GuessCurrentBuilding()
    {
        string fromTransform = DetermineBuildingFromTransform(_transform);
        if (!string.IsNullOrEmpty(fromTransform)) return fromTransform;
        string fromLast = GuessBuildingForLocation(_lastValidLocationName);
        if (!string.IsNullOrEmpty(fromLast)) return fromLast;
        return GuessBuildingForLocation(_targetLocationName);
    }

    public void UpdateState(AgentState state)
    {
        if (!_isInitialized || state == null) return;

        if (_commandQueue.Count > 5)
        {
            Debug.LogWarning($"[Agent {agentName}] 指令堆積過多 ({_commandQueue.Count})，清空。");
            _commandQueue.Clear();
        }

        Vector3 finalPos = _targetPosition;
        string finalLocName = _lastValidLocationName;
        Transform finalTrans = null;
        bool shouldMove = false;

        string incomingLocation = (state.Location ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(incomingLocation)) shouldMove = false;
        else if (IsUnknownLocation(incomingLocation)) shouldMove = false;
        else if (ShouldRespectManualOverride(incomingLocation)) shouldMove = false;
        else
        {
            bool isSameLocation = !string.IsNullOrEmpty(_lastValidLocationName) &&
                                  string.Equals(incomingLocation, _lastValidLocationName, StringComparison.OrdinalIgnoreCase);
            
            float dist = Vector2.Distance(_transform.position, _targetPosition);
            bool isArrived = dist <= _arrivalThreshold * 1.5f;

            if (isSameLocation && isArrived) shouldMove = false;
            else
            {
                if (TryFindLocationTransform(incomingLocation, out Transform targetLocation) && targetLocation != null)
                {
                    finalPos = targetLocation.position;
                    finalTrans = targetLocation;
                    finalLocName = ResolveLocationKey(incomingLocation, targetLocation);
                    shouldMove = true;
                }
                else if (TryParseVector3(incomingLocation, out Vector3 pos))
                {
                    if (TryResolveCoordinateLocation(pos, out string resolvedName, out Vector3 resolvedPosition, out Transform resolvedTransform))
                    {
                        finalPos = resolvedPosition;
                        finalLocName = resolvedName;
                        finalTrans = resolvedTransform;
                    }
                    else
                    {
                        finalPos = pos;
                    }
                    shouldMove = true;
                }
                else if (incomingLocation == "公寓")
                {
                    finalPos = _transform.position;
                    shouldMove = true;
                }
                else
                {
                    Debug.LogWarning($"地點 '{state.Location}' 未找到，代理人 '{agentName}' 停在原地。");
                    shouldMove = false;
                }
            }
        }

        AgentCommand cmd = new AgentCommand
        {
            Type = shouldMove ? AgentInternalCommandType.Move : AgentInternalCommandType.ActionOnly,
            TargetPosition = finalPos,
            TargetLocationName = finalLocName,
            TargetTransform = finalTrans,
            ActionName = state.CurrentState,
            UseTeleport = false
        };

        _commandQueue.Enqueue(cmd);
        _lastStateApplyTime = Time.time;
    }

    private void SetTargetLocation(string locationName, Vector3 position, Transform locationTransform = null)
    {
        _targetLocationName = locationName;
        _targetPosition = position;
        _lastValidLocationName = locationName;
        _navMeshDriving = _movementController == null && _navMeshAgent != null && _navMeshAgent.isActiveAndEnabled;
        if (_movementController != null)
        {
            _movementController.RequestPathTo(locationName, position, locationTransform);
        }
    }

    private bool TryGetLocationPosition(string locationName, out Vector3 position, out Transform transform)
    {
        position = Vector3.zero;
        transform = null;
        if (string.IsNullOrWhiteSpace(locationName) || _locationTransforms == null) return false;

        if (TryFindLocationTransform(locationName, out Transform directTransform) && directTransform != null)
        {
            position = directTransform.position;
            transform = directTransform;
            return true;
        }
        return false;
    }

    private bool TryResolveCoordinateLocation(Vector3 coordinate, out string locationName, out Vector3 position, out Transform resolvedTransform)
    {
        locationName = null;
        position = coordinate;
        resolvedTransform = null;

        if (_locationTransforms == null || _locationTransforms.Count == 0) return false;

        foreach (var pair in _locationColliders)
        {
            Collider2D collider = pair.Value;
            if (collider == null || !collider.enabled) continue;
            if (collider.OverlapPoint(coordinate))
            {
                locationName = pair.Key;
                if (_locationTransforms != null && _locationTransforms.TryGetValue(pair.Key, out Transform linkedTransform) && linkedTransform != null)
                {
                    position = linkedTransform.position;
                    resolvedTransform = linkedTransform;
                }
                else
                {
                    position = collider.transform.position;
                    resolvedTransform = collider.transform;
                }
                return true;
            }
        }

        // 簡單的最近距離查找
        float closestDistance = float.MaxValue;
        string closestName = null;
        Vector3 closestPosition = Vector3.zero;

        foreach (var pair in _locationTransforms)
        {
            if (pair.Value == null) continue;
            float distance = Vector2.Distance(coordinate, pair.Value.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestName = pair.Key;
                closestPosition = pair.Value.position;
                resolvedTransform = pair.Value;
            }
        }

        if (!string.IsNullOrEmpty(closestName) && closestDistance <= CoordinateSnapThreshold)
        {
            locationName = closestName;
            position = closestPosition;
            if (_locationTransforms != null && _locationTransforms.TryGetValue(closestName, out Transform closestTransform))
            {
                resolvedTransform = closestTransform;
            }
            return true;
        }
        return false;
    }

    private static bool TryParseVector3(string input, out Vector3 result)
    {
        result = Vector3.zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim('(', ')');
        string[] parts = input.Split(',');
        if (parts.Length != 3) return false;
        if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            result = new Vector3(x, y, z);
            return true;
        }
        return false;
    }

    public void TeleportTo(Vector3 position, params string[] locationAliases)
    {
        TeleportTo(position, false, locationAliases);
    }

    public void TeleportTo(Vector3 position, bool suppressEffects, params string[] locationAliases)
    {
        _transform.position = position;
        _targetPosition = position;
        if (_navMeshAgent != null)
        {
            _navMeshAgent.ResetPath();
            _navMeshAgent.nextPosition = new Vector3(position.x, position.y, _navMeshAgent.nextPosition.z);
            _navMeshDriving = false;
        }
        _movementController?.HandleTeleport(position);
        ResetNavMeshPosition(position);
        SetManualLocationOverrides(locationAliases);
        _lastInstructionDestination = null;
        NotifyMovementCompleted();
        OnTeleported(false, suppressEffects);
    }

    public void OnTeleported(bool usedDoor, bool suppressEffects = false)
    {
        ForceImmediateVisualRefresh();
        if (!suppressEffects) _simulationClient?.ReportTeleport(agentName);
    }
    public void PauseMovementForPortal()
    {
        _isPortalPaused = true;
        _movementController?.CancelMovement();
    }

    public void ResumeMovementAfterPortal(bool syncPosition = true)
    {
        _isPortalPaused = false;
        if (syncPosition)
        {
            SyncTargetToCurrentPosition();
        }
    }
    public void SyncTargetToCurrentPosition()
    {
        _targetPosition = _transform.position;
        _movementController?.HandleTeleport(_transform.position);
        ResetNavMeshPosition(_transform.position);
        _navMeshDriving = false;
        ForceImmediateVisualRefresh();
    }

    private void ResetNavMeshPosition(Vector3 position)
    {
        if (_navMeshAgent == null) return;

        bool wasEnabled = _navMeshAgent.enabled;
        if (!wasEnabled) _navMeshAgent.enabled = true;

        _navMeshAgent.Warp(position);
        _navMeshAgent.nextPosition = position;
        _navMeshAgent.velocity = Vector3.zero;
        _navMeshAgent.ResetPath();
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        UpdateStatusIndicatorFromAction(action);
        if (IsIdleAction(action)) SetBehaviourState(AgentBehaviourState.Idle);
        else SetBehaviourState(AgentBehaviourState.Interacting);
        if (bubbleController != null)
        {
            string bubbleText = BuildBubbleText(action);
            if (!string.IsNullOrEmpty(bubbleText))
            {
                bubbleController.顯示氣泡(bubbleText, _transform);
            }
        }
    }
    private void EnterAwaitingBatchCompletion(bool performingAction)
    {
        _awaitingMovementBatch = true;
        _movementController?.MarkArrivalHold();
        if (_visualController != null)
        {
            _visualController.EnterWaitingLoop(performingAction);
        }
        UpdateStatusIndicatorFromAction(_currentAction);
        ForceImmediateVisualRefresh();
    }

    internal void CompleteMovementBatch(bool notifySimulationClient)
    {
        if (!_awaitingMovementBatch)
        {
            return;
        }

        _awaitingMovementBatch = false;
        NotifyMovementCompleted(notifySimulationClient);
    }

    internal void OnMovementControllerArrived()
    {
        bool performingAction = !IsIdleAction(_currentAction);
        EnterAwaitingBatchCompletion(performingAction);
        _simulationClient?.RegisterMovementArrival(agentName, this);
    }

    internal void ForceImmediateVisualRefresh()
    {
        if (nameTextUGUI != null && _mainCamera != null) UpdateNameplatePosition();
    }

    public void ApplyActionInstruction(AgentActionInstruction instruction)
    {
        if (!_isInitialized || instruction == null) return;
        string command = instruction.Command?.Trim()?.ToLowerInvariant();
        if (command == "teleport") HandleTeleportInstruction(instruction);
        else if (command == "move") HandleMoveInstruction(instruction);
        else if (command == "interact")
        {
            SetActionState(instruction.Action);
            _lastInstructionDestination = instruction.Destination;
        }
    }
    public void ApplyNetworkDestination(Vector3 destination, string locationName, string actionName = null)
    {
        if (!_isInitialized) return;

        _lastInstructionDestination = locationName;
        SetActionState(string.IsNullOrEmpty(actionName) ? "移動" : actionName);
        SetTargetLocation(locationName, destination, null);
        _smoothedVelocity = Vector3.zero;
    }

    public void ApplyNetworkAction(string actionName)
    {
        if (!_isInitialized) return;
        SetActionState(actionName);
    }
    private void HandleTeleportInstruction(AgentActionInstruction instruction)
    {
        if (IsUnknownLocation(instruction.Destination) || IsUnknownLocation(instruction.ToPortal))
        {
            _simulationClient?.ReportTeleport(agentName);
            return;
        }

        Vector3 exitPosition = _transform.position;
        string resolvedLocation = null;
        Transform exitTransform;

        if (!string.IsNullOrWhiteSpace(instruction.ToPortal) &&
            TryFindLocationTransform(instruction.ToPortal, out exitTransform) && exitTransform != null)
        {
            resolvedLocation = ResolveLocationKey(instruction.ToPortal, exitTransform);
            exitPosition = exitTransform.position;
        }
        else if (!string.IsNullOrWhiteSpace(instruction.Destination) &&
                 TryFindLocationTransform(instruction.Destination, out exitTransform) && exitTransform != null)
        {
            resolvedLocation = ResolveLocationKey(instruction.Destination, exitTransform);
            exitPosition = exitTransform.position;
        }
        else if (!string.IsNullOrWhiteSpace(instruction.Destination) &&
                 TryParseVector3(instruction.Destination, out Vector3 destinationCoords))
        {
            exitPosition = destinationCoords;
            resolvedLocation = instruction.Destination;
        }
        else
        {
            return;
        }

        List<string> manualAliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolvedLocation)) manualAliases.Add(resolvedLocation);
        
        TeleportTo(exitPosition, true, manualAliases.ToArray());

        string nextLocationName = !string.IsNullOrWhiteSpace(resolvedLocation) ? resolvedLocation : instruction.Destination;
        if (!string.IsNullOrWhiteSpace(nextLocationName))
        {
            _targetLocationName = nextLocationName;
            _lastValidLocationName = nextLocationName;
        }
        _lastInstructionDestination = instruction.Destination;
        SetActionState(string.IsNullOrEmpty(instruction.Action) ? "傳送" : instruction.Action);
    }

    private void HandleMoveInstruction(AgentActionInstruction instruction)
    {
        if (IsUnknownLocation(instruction.Destination) || IsUnknownLocation(instruction.NextStep))
        {
            _simulationClient?.ReportMovementCompleted(agentName);
            return;
        }

        string destinationKey = string.IsNullOrWhiteSpace(instruction.Destination) ? instruction.NextStep : instruction.Destination;
        if (TryGetLocationPosition(destinationKey, out Vector3 destination, out Transform destinationTransform))
        {
            SetTargetLocation(destinationKey, destination, destinationTransform);
            _lastInstructionDestination = destinationKey;
        }
        else if (TryParseVector3(destinationKey, out Vector3 destinationCoords))
        {
            _lastInstructionDestination = destinationKey;
            SetTargetLocation(destinationKey, destinationCoords, null);
        }

        SetActionState(string.IsNullOrEmpty(instruction.Action) ? "移動" : instruction.Action);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        PortalController portal = other.GetComponent<PortalController>();
        if (portal != null && _simulationClient != null && other.name == _targetLocationName)
        {
            _simulationClient.SendTeleportRequest(agentName, portal.targetPortalName);
        }
    }
    
    public void SetVisibility(bool isVisible) => gameObject.SetActive(isVisible);

    public string GetDisplayLocationName()
    {
        string candidate = !string.IsNullOrWhiteSpace(_lastValidLocationName) ? _lastValidLocationName : _targetLocationName;
        if (string.IsNullOrWhiteSpace(candidate) && TryResolveCoordinateLocation(_transform.position, out string resolvedLocation, out _, out _))
        {
            _lastValidLocationName = resolvedLocation;
            candidate = resolvedLocation;
        }
        if (string.IsNullOrWhiteSpace(candidate)) candidate = GuessCurrentBuilding();
        return LocationNameLocalizer.ToDisplayName(candidate);
    }

    void OnEnable()
    {
        _isCurrentlySleeping = false;
        _targetPosition = _transform.position;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetBehaviourState(AgentBehaviourState.Idle);
    }

    void OnDisable()
    {
        _isCurrentlySleeping = false;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
        StopIdleMicroActions();
    }

    internal void NotifyMovementStarted()
    {
        ShowActiveStatus("執行任務");
        ForceImmediateVisualRefresh();
        SetBehaviourState(AgentBehaviourState.Moving);
        _simulationClient?.ReportMovementStarted(agentName);
    }

    internal void NotifyMovementCompleted(bool notifySimulationClient = true)
    {
        UpdateStatusIndicatorFromAction(_currentAction);
        ForceImmediateVisualRefresh();
        if (notifySimulationClient)
        {
            _simulationClient?.ReportMovementCompleted(agentName);
        }
        if (IsIdleAction(_currentAction)) SetBehaviourState(AgentBehaviourState.Idle);
        else SetBehaviourState(AgentBehaviourState.Interacting);
        _simulationClient?.ReportMovementCompleted(agentName);
    }
    public void ApplyAgentUpdate(Vector3 destination, string actionState, bool teleport, bool preferNavMeshAgent, float? speedOverride)
    {
        if (!_isInitialized) return;

        if (teleport)
        {
            TeleportTo(destination, true);
            SetActionState(string.IsNullOrWhiteSpace(actionState) ? "傳送" : actionState);
            return;
        }

        string resolvedAction = string.IsNullOrWhiteSpace(actionState) ? "移動" : actionState;
        bool isIdle = string.Equals(resolvedAction, "idle", StringComparison.OrdinalIgnoreCase);

        if (isIdle)
        {
            _movementController?.CancelMovement();
            if (_navMeshAgent != null)
            {
                _navMeshAgent.isStopped = true;
                _navMeshAgent.ResetPath();
                _navMeshDriving = false;
            }
            SetActionState(resolvedAction);
            NotifyMovementCompleted();
            return;
        }

        SetActionState(resolvedAction);

        if (preferNavMeshAgent && _navMeshAgent != null)
        {
            if (speedOverride.HasValue)
            {
                _navMeshAgent.speed = Mathf.Max(0.1f, speedOverride.Value);
            }
            _navMeshAgent.isStopped = false;
            _navMeshAgent.SetDestination(destination);
            _navMeshDriving = true;
            NotifyMovementStarted();
        }
        else if (_movementController != null)
        {
            _movementController.RequestPathTo(null, destination, null);
        }

        _targetPosition = destination;
    }

    private void ShowIdleStatus()
    {
        UpdateStatusIndicator("待機", false);
        SetSleepState(false);
    }

    private void ShowActiveStatus(string status)
    {
        string label = string.IsNullOrWhiteSpace(status) ? "執行任務" : status.Trim();
        UpdateStatusIndicator(label, true);
        SetSleepState(false);
    }

    private void ShowSleepingStatus(string status)
    {
        string label = string.IsNullOrWhiteSpace(status) ? "睡覺" : status.Trim();
        UpdateStatusIndicator(label, false);
        SetSleepState(true);
    }

    private void UpdateStatusIndicator(string status, bool isActive)
    {
        _statusLabel = status;
        if (nameTextUGUI != null)
        {
            nameTextUGUI.text = $"{_displayName} [{status}]";
            nameTextUGUI.color = isActive ? _activeNameColor : _idleNameColor;
        }
    }

    private void UpdateStatusIndicatorFromAction(string action)
    {
        if (IsSleepAction(action)) ShowSleepingStatus(action);
        else if (IsIdleAction(action)) ShowIdleStatus();
        else ShowActiveStatus(action);
    }

    private static bool IsIdleAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return true;
        string lower = action.Trim().ToLowerInvariant();
        return lower.Contains("idle") || lower.Contains("待機") || lower.Contains("站立") || lower.Contains("wait") || lower.Contains("stand");
    }

    private static bool IsSleepAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        string lower = action.Trim().ToLowerInvariant();
        return lower.Contains("sleep") || lower.Contains("睡") || lower.Contains("nap");
    }

    private string BuildBubbleText(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return null;
        string trimmed = action.Trim();
        string lower = trimmed.ToLowerInvariant();
        if (lower.Contains("chat") || lower.Contains("聊天")) return "💬 " + trimmed;
        if (lower.Contains("rest") || lower.Contains("休息")) return "😴 " + trimmed;
        if (lower.Contains("move") || lower.Contains("移動")) return "🏃 " + trimmed;
        if (lower.Contains("hide") || lower.Contains("掩護") || lower.Contains("duck")) return "🛡️ " + trimmed;
        if (lower.Contains("evacuate") || lower.Contains("避難") || lower.Contains("subway")) return "🚇 " + trimmed;
        return trimmed;
    }
    private void SetBehaviourState(AgentBehaviourState newState)
    {
        if (_currentBehaviourState == newState) return;
        _currentBehaviourState = newState;

        if (newState == AgentBehaviourState.Idle)
        {
            StartIdleMicroActions();
        }
        else
        {
            StopIdleMicroActions();
            if (newState == AgentBehaviourState.Moving)
            {
                _navMeshAgent?.ResetPath();
            }
        }
    }

    private void StartIdleMicroActions()
    {
        StopIdleMicroActions();
        _idleCts = new CancellationTokenSource();
        IdleMicroActionLoop(_idleCts.Token).Forget();
    }

    private void StopIdleMicroActions()
    {
        if (_idleCts != null)
        {
            _idleCts.Cancel();
            _idleCts.Dispose();
            _idleCts = null;
        }
    }

    private async UniTaskVoid IdleMicroActionLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            float delay = UnityEngine.Random.Range(1.5f, 3.5f);
            try { await UniTask.Delay(TimeSpan.FromSeconds(delay), cancellationToken: token); }
            catch (OperationCanceledException) { break; }
            if (token.IsCancellationRequested || _currentBehaviourState != AgentBehaviourState.Idle) break;

            int action = UnityEngine.Random.Range(0, 3);
            switch (action)
            {
                case 0:
                    _visualController?.PlayEmote("HeadTurn");
                    break;
                case 1:
                    _visualController?.PlayEmote("Stretch");
                    break;
                default:
                    await PerformIdleWander(token);
                    break;
            }
        }
    }

    private async UniTask PerformIdleWander(CancellationToken token)
    {
        if (_navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled) return;

        Vector2 randomOffset = UnityEngine.Random.insideUnitCircle * _idleWanderRadius;
        Vector3 wanderTarget = new Vector3(_transform.position.x + randomOffset.x, _transform.position.y + randomOffset.y, _transform.position.z);
        _navMeshAgent.SetDestination(wanderTarget);
        _navMeshDriving = true;
        float wanderThreshold = Mathf.Max(_arrivalThreshold, 0.05f);
        while (!token.IsCancellationRequested && _navMeshAgent.remainingDistance > wanderThreshold && _currentBehaviourState == AgentBehaviourState.Idle)
        {
            await UniTask.Yield(PlayerLoopTiming.Update, token);
        }

        _navMeshAgent.ResetPath();
        _navMeshDriving = false;
    }
    private async UniTaskVoid ProcessCommandBufferLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_commandQueue.Count > 0)
            {
                // [已移除 unused] _isProcessing = true;
                AgentCommand cmd = _commandQueue.Dequeue();
                try
                {
                    await ExecuteCommandAsync(cmd, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { Debug.LogError($"[Agent {agentName}] 執行指令錯誤: {e}"); }
            }
            else
            {
                // [已移除 unused] _isProcessing = false;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }
    }

    private async UniTask ExecuteCommandAsync(AgentCommand cmd, CancellationToken token)
    {
        if (_awaitingMovementBatch)
        {
            CompleteMovementBatch(false);
        }
        if (!string.IsNullOrEmpty(cmd.ActionName)) SetActionState(cmd.ActionName);

        if (cmd.Type == AgentInternalCommandType.Move)
        {
            SetTargetLocation(cmd.TargetLocationName, cmd.TargetPosition, cmd.TargetTransform);
            NotifyMovementStarted();
            await WaitUntilArrival(token);
            bool performingAction = !IsIdleAction(cmd.ActionName);
            EnterAwaitingBatchCompletion(performingAction);
            _simulationClient?.RegisterMovementArrival(agentName, this);
        }
        else if (cmd.Type == AgentInternalCommandType.Teleport)
        {
            TeleportTo(cmd.TargetPosition);
            await UniTask.Delay(500, cancellationToken: token);
        }
        else if (cmd.Type == AgentInternalCommandType.ActionOnly)
        {
            await UniTask.Delay(500, cancellationToken: token);
        }
    }

    private async UniTask WaitUntilArrival(CancellationToken token)
    {
        float sqrThreshold = _arrivalThreshold * _arrivalThreshold;
        await UniTask.WaitUntil(() =>
        {
            if (token.IsCancellationRequested) return true;
            if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled && _navMeshDriving)
            {
                if (_navMeshAgent.pathPending) return false;
                if (_navMeshAgent.hasPath)
                {
                    if (_navMeshAgent.remainingDistance <= _arrivalThreshold && _navMeshAgent.velocity.sqrMagnitude < 0.01f)
                    {
                        return true;
                    }
                    return false;
                }
            }

            float dist = Vector3.SqrMagnitude(_transform.position - _targetPosition);
            return dist <= sqrThreshold;
        }, cancellationToken: token);
    }
}
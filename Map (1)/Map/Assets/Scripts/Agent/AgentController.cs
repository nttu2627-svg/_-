using UnityEngine;
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

        agentName = string.IsNullOrEmpty(agentName)
            ? gameObject.name.ToUpper()
            : agentName.ToUpper();

        // 初始化 MovementController
        if (!TryGetComponent(out _movementController))
        {
            _movementController = gameObject.AddComponent<AgentMovementController>();
        }
        _movementController.ConfigureFromAgent(this, _movementSpeed, _arrivalThreshold);

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
        if (_movementController != null)
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

        // 計算並更新動畫狀態
        if (_visualController != null)
        {
            Vector3 velocity = (_transform.position - _lastPosition) / Time.deltaTime;
            _visualController.UpdateVisuals(velocity);
        }
        _lastPosition = _transform.position;

        // 物理移動 (作為最後防線，通常由 MovementController 接管)
        Vector3 currentPosition = _transform.position;
        Vector3 toTarget = _targetPosition - currentPosition;
        float arrivalThresholdSqr = _arrivalThreshold * _arrivalThreshold;

        // 如果 MovementController 正在運作，這裡就不插手
        if (_movementController != null && _movementController.IsControllingMovement)
        {
            return;
        }

        if (toTarget.sqrMagnitude <= arrivalThresholdSqr)
        {
            _transform.position = _targetPosition;
            return;
        }

        // 簡單的補間移動 (fallback)
        _transform.position = Vector3.MoveTowards(
            currentPosition,
            _targetPosition,
            Mathf.Max(0f, _movementSpeed) * Time.deltaTime);
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
        _movementController?.HandleTeleport(position);
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

    public void SyncTargetToCurrentPosition()
    {
        _targetPosition = _transform.position;
        _movementController?.HandleTeleport(_transform.position);
        ForceImmediateVisualRefresh();
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        UpdateStatusIndicatorFromAction(action);
        if (bubbleController != null)
        {
            string bubbleText = BuildBubbleText(action);
            if (!string.IsNullOrEmpty(bubbleText))
            {
                bubbleController.顯示氣泡(bubbleText, _transform);
            }
        }
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

        string nextStep = string.IsNullOrWhiteSpace(instruction.NextStep) ? instruction.Destination : instruction.NextStep;
        bool destinationChanged = !string.IsNullOrWhiteSpace(instruction.Destination) && !string.Equals(_lastInstructionDestination, instruction.Destination, StringComparison.OrdinalIgnoreCase);
        bool pathChanged = !string.IsNullOrWhiteSpace(nextStep) && !string.Equals(_targetLocationName, nextStep, StringComparison.OrdinalIgnoreCase);

        if (destinationChanged || pathChanged)
        {
            if (destinationChanged && TryGetLocationPosition(instruction.Origin, out Vector3 originPosition, out _))
            {
                _transform.position = originPosition;
                _movementController?.HandleTeleport(originPosition);
            }

            if (TryGetLocationPosition(nextStep, out Vector3 nextPosition, out Transform nextTransform))
            {
                SetTargetLocation(nextStep, nextPosition, nextTransform);
            }
            else if (TryGetLocationPosition(instruction.Destination, out Vector3 destinationPosition, out Transform destinationTransform))
            {
                SetTargetLocation(instruction.Destination, destinationPosition, destinationTransform);
            }
            _lastInstructionDestination = instruction.Destination;
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
    }

    void OnDisable()
    {
        _isCurrentlySleeping = false;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
    }

    internal void NotifyMovementStarted()
    {
        ShowActiveStatus("執行任務");
        ForceImmediateVisualRefresh();
        _simulationClient?.ReportMovementStarted(agentName);
    }

    internal void NotifyMovementCompleted()
    {
        UpdateStatusIndicatorFromAction(_currentAction);
        ForceImmediateVisualRefresh();
        _simulationClient?.ReportMovementCompleted(agentName);
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
        if (!string.IsNullOrEmpty(cmd.ActionName)) SetActionState(cmd.ActionName);

        if (cmd.Type == AgentInternalCommandType.Move)
        {
            SetTargetLocation(cmd.TargetLocationName, cmd.TargetPosition, cmd.TargetTransform);
            NotifyMovementStarted();
            await WaitUntilArrival(token);
            NotifyMovementCompleted();
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
            float dist = Vector3.SqrMagnitude(_transform.position - _targetPosition);
            return dist <= sqrThreshold;
        }, cancellationToken: token);
    }
}
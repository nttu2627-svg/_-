// Scripts/Agent/AgentController.cs (安全边距最终版)

using UnityEngine;
using TMPro;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
    private Dictionary<string, Transform> _normalizedLocationLookup;

    private readonly Dictionary<string, Collider2D> _locationColliders = new Dictionary<string, Collider2D>();
    private bool _isInitialized = false;
    private Vector3 _targetPosition;
    [SerializeField] private float _movementSpeed = 4.5f;
    [SerializeField] private float _arrivalThreshold = 0.05f;
    [SerializeField] private float _movingBobAmplitude = 0.18f;
    [SerializeField] private float _movingBobFrequency = 4f;
    [SerializeField] private float _idleBobAmplitude = 0.08f;
    [SerializeField] private float _idleBobFrequency = 1.5f;
    private Vector3 _visualOffset = Vector3.zero;
    private float _bobSeed;
    private Camera _mainCamera;
    private SimulationClient _simulationClient;
    private string _targetLocationName;
    private string _lastValidLocationName;
    private string _currentAction;
    private readonly HashSet<string> _manualLocationOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private const float CoordinateSnapThreshold = 8f;
    private string _lastInstructionDestination;
    private static readonly List<AgentController> ActiveAgents = new List<AgentController>();
    private const float SeparationRadius = 1.1f;
    private const float SeparationStrength = 0.85f;
    private const float MaxSeparationOffset = 0.6f;
    private const float PortalEscapeDistance = 0.75f;
    private static readonly Collider2D[] PortalOverlapBuffer = new Collider2D[8];
    private static readonly (string english, string localized)[] LocationPrefixAliases = new (string, string)[]
    {
        ("Apartment", "公寓"),
        ("Apartment_F1", "公寓一樓"),
        ("Apartment_F2", "公寓二樓"),
        ("School", "學校"),
        ("Gym", "健身房"),
        ("Rest", "餐廳"),
        ("Super", "超市"),
        ("Subway", "地鐵"),
        ("Exterior", "室外")
    };
    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();
        _bobSeed = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        _targetPosition = _transform.position;
        if (string.IsNullOrEmpty(agentName))
        {
            agentName = gameObject.name.ToUpper();
        }
        else
        {
            agentName = agentName.ToUpper();
        }
        
        if (bubbleController == null)
        {
            bubbleController = GetComponentInChildren<思考氣泡控制器>(true);
            if (bubbleController == null)
            {
                Debug.LogWarning($"[Agent {agentName}] 場景中找不到思考氣泡控制器，將無法顯示行動提示。", this);
            }
        }

        
        gameObject.SetActive(false);
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
    private static string NormalizeLocationKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '（' || c == '）')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    private void AddNormalizedLocationKey(string key, Transform transform)
    {
        if (_normalizedLocationLookup == null || string.IsNullOrWhiteSpace(key) || transform == null)
        {
            return;
        }

        string normalized = NormalizeLocationKey(key);
        if (!string.IsNullOrEmpty(normalized) && !_normalizedLocationLookup.ContainsKey(normalized))
        {
            _normalizedLocationLookup[normalized] = transform;
        }
    }

    private bool TryGetNormalizedLocation(string normalizedKey, out Transform transform)
    {
        transform = null;
        if (_normalizedLocationLookup == null || string.IsNullOrEmpty(normalizedKey))
        {
            return false;
        }

        if (_normalizedLocationLookup.TryGetValue(normalizedKey, out Transform cached) && cached != null)
        {
            transform = cached;
            return true;
        }

        return false;
    }

    private bool TryFindLocationTransform(string locationName, out Transform transform)
    {
        transform = null;
        if (_locationTransforms == null || string.IsNullOrWhiteSpace(locationName))
        {
            return false;
        }

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

        if (ShouldRespectManualOverride(incomingLocation))
        {
            _currentAction = state.CurrentState;
            return;
        }

        if (_manualLocationOverrides.Count > 0)
        {
            _manualLocationOverrides.Clear();
        }

        if (TryFindLocationTransform(incomingLocation, out Transform targetLocation) && targetLocation != null)
        {
            string resolvedKey = ResolveLocationKey(incomingLocation, targetLocation);
            SetTargetLocation(resolvedKey, targetLocation.position);
        }
        else if (TryParseVector3(incomingLocation, out Vector3 pos))
        {
            if (TryResolveCoordinateLocation(pos, out string resolvedLocation, out Vector3 resolvedPosition))
            {
                SetTargetLocation(resolvedLocation, resolvedPosition);
            }
            else
            {
                Debug.LogWarning($"[Agent {agentName}] 無法將座標 {incomingLocation} 對應到任何已知地點，保持在 {_lastValidLocationName ?? "原地"}。");
                _targetPosition = _lastValidLocationName != null &&
                                   _locationTransforms != null &&
                                   _locationTransforms.TryGetValue(_lastValidLocationName, out Transform lastTransform)
                    ? lastTransform.position
                    : _transform.position;
            }
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
    private void SetTargetLocation(string locationName, Vector3 position)
    {
        _targetLocationName = locationName;
        _targetPosition = position;
        _lastValidLocationName = locationName;
    }
    private bool TryGetLocationPosition(string locationName, out Vector3 position)
    {
        position = Vector3.zero;
        if (string.IsNullOrWhiteSpace(locationName) || _locationTransforms == null)
        {
            return false;
        }

        if (TryFindLocationTransform(locationName, out Transform directTransform) && directTransform != null)
        {
            position = directTransform.position;
            return true;
        }
        return false;
    }

    private bool TryResolveCoordinateLocation(Vector3 coordinate, out string locationName, out Vector3 position)
    {
        locationName = null;
        position = coordinate;

        if (_locationTransforms == null || _locationTransforms.Count == 0)
        {
            return false;
        }

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
                }
                else
                {
                    position = collider.transform.position;
                }
                return true;
            }
        }

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
            }
        }

        if (!string.IsNullOrEmpty(closestName) && closestDistance <= CoordinateSnapThreshold)
        {
            locationName = closestName;
            position = closestPosition;
            return true;
        }

        return false;
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
        _lastInstructionDestination = null;
        NudgeAwayFromPortals();
        _visualOffset = Vector3.zero;

    }

    public void SyncTargetToCurrentPosition()
    {
        _targetPosition = _transform.position;
        _visualOffset = Vector3.zero;
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        if (bubbleController == null) return;

        string bubbleText = BuildBubbleText(action);
        if (!string.IsNullOrEmpty(bubbleText))
        {
            bubbleController.顯示氣泡(bubbleText, _transform);
        }
    }

    public void ApplyActionInstruction(AgentActionInstruction instruction)
    {
        if (!_isInitialized || instruction == null) return;

        string command = instruction.Command?.Trim()?.ToLowerInvariant();
        if (command == "move")
        {
            string nextStep = string.IsNullOrWhiteSpace(instruction.NextStep)
                ? instruction.Destination
                : instruction.NextStep;

            bool destinationChanged = !string.IsNullOrWhiteSpace(instruction.Destination) &&
                !string.Equals(_lastInstructionDestination, instruction.Destination, StringComparison.OrdinalIgnoreCase);
            bool pathChanged = !string.IsNullOrWhiteSpace(nextStep) &&
                !string.Equals(_targetLocationName, nextStep, StringComparison.OrdinalIgnoreCase);

            if (destinationChanged || pathChanged)
            {
                if (destinationChanged && TryGetLocationPosition(instruction.Origin, out Vector3 originPosition))
                {
                    _transform.position = originPosition;
                }

                if (TryGetLocationPosition(nextStep, out Vector3 nextPosition))
                {
                    _targetPosition = nextPosition;
                    _targetLocationName = nextStep;
                }
                else if (TryGetLocationPosition(instruction.Destination, out Vector3 destinationPosition))
                {
                    _targetPosition = destinationPosition;
                    _targetLocationName = instruction.Destination;
                }

                _lastInstructionDestination = instruction.Destination;
            }

            SetActionState(string.IsNullOrEmpty(instruction.Action) ? "移動" : instruction.Action);
        }
        else if (command == "interact")
        {
            SetActionState(instruction.Action);
            _lastInstructionDestination = instruction.Destination;
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
    public string GetDisplayLocationName()
    {
        string candidate = !string.IsNullOrWhiteSpace(_lastValidLocationName)
            ? _lastValidLocationName
            : _targetLocationName;

        return LocationNameLocalizer.ToDisplayName(candidate);
    }
    void OnEnable()
    {
        if (!ActiveAgents.Contains(this))
        {
            ActiveAgents.Add(this);
        }

        _visualOffset = Vector3.zero;
        _targetPosition = _transform.position;
        _bobSeed = UnityEngine.Random.Range(0f, Mathf.PI * 2f);

        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
    }

    void OnDisable()
    {
        ActiveAgents.Remove(this);
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
        _visualOffset = Vector3.zero;
    }

    private Vector3 ComputeSeparation(Vector3 candidatePosition)
    {
        if (ActiveAgents.Count <= 1)
        {
            return Vector3.zero;
        }

        Vector2 accumulated = Vector2.zero;
        int neighbourCount = 0;

        foreach (var agent in ActiveAgents)
        {
            if (agent == null || agent == this || !agent.gameObject.activeSelf) continue;

            Vector3 otherBase = agent._transform.position - agent._visualOffset;
            Vector2 toOther = (Vector2)(candidatePosition - otherBase);
            float sqrMagnitude = toOther.sqrMagnitude;
            if (sqrMagnitude < 0.0001f || sqrMagnitude > SeparationRadius * SeparationRadius) continue;

            float distance = Mathf.Sqrt(sqrMagnitude);
            float weight = (SeparationRadius - distance) / SeparationRadius;
            accumulated += toOther.normalized * weight;
            neighbourCount++;
        }

        if (neighbourCount == 0)
        {
            return Vector3.zero;
        }

        Vector2 separation = accumulated * SeparationStrength;
        float magnitude = separation.magnitude;
        if (magnitude > MaxSeparationOffset)
        {
            separation = separation.normalized * MaxSeparationOffset;
        }

        return new Vector3(separation.x, separation.y, 0f);
    }

    private void NudgeAwayFromPortals()
    {
        Collider2D[] hitPortals = Physics2D.OverlapCircleAll(_transform.position, PortalEscapeDistance);
        if (hitPortals == null || hitPortals.Length == 0)
        {
            return;
        }

        Vector2 cumulative = Vector2.zero;
        int portalCount = 0;

        foreach (Collider2D collider in hitPortals)
        {
            if (collider == null || collider.GetComponent<PortalController>() == null) continue;

            Vector2 away = (Vector2)_transform.position - (Vector2)collider.bounds.center;
            if (away.sqrMagnitude < 0.0001f)
            {
                away = Vector2.up;
            }

            cumulative += away.normalized;
            portalCount++;
        }

        if (portalCount == 0)
        {
            return;
        }

        Vector2 offset = cumulative.normalized * PortalEscapeDistance;
        Vector3 adjusted = _transform.position + new Vector3(offset.x, offset.y, 0f);
        _transform.position = adjusted;
        _targetPosition = adjusted;
    }

    private string BuildBubbleText(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        string trimmed = action.Trim();
        string lower = trimmed.ToLowerInvariant();

        if (lower.Contains("chat") || lower.Contains("聊天")) return "💬 " + trimmed;
        if (lower.Contains("rest") || lower.Contains("休息")) return "😴 " + trimmed;
        if (lower.Contains("move") || lower.Contains("移動")) return "🏃 " + trimmed;
        if (lower.Contains("hide") || lower.Contains("掩護") || lower.Contains("duck")) return "🛡️ " + trimmed;
        if (lower.Contains("evacuate") || lower.Contains("避難") || lower.Contains("subway")) return "🚇 " + trimmed;

        return trimmed;
    }
}
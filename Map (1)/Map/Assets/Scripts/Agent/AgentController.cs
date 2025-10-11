using UnityEngine;
using TMPro;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DisasterSimulation;

public class AgentController : MonoBehaviour
{
    [Header("核心設定")]
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

    [SerializeField, Tooltip("同一代理連續套用後端狀態的最小時間間隔（秒），避免高頻重複指令造成卡頓）")]
    private float _minStateApplyInterval = 0.05f;
    private float _lastStateApplyTime = -999f;
    private Vector3 _visualOffset = Vector3.zero;
    private float _bobSeed;
    private Camera _mainCamera;
    private SimulationClient _simulationClient;
    private AgentMovementController _movementController;
    private string _targetLocationName;
    private string _lastValidLocationName;
    private string _currentAction;
    private readonly HashSet<string> _manualLocationOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private const float CoordinateSnapThreshold = 8f;
    private string _lastInstructionDestination;
    [SerializeField, Tooltip("Bobbing 動態效果的刷新間隔（秒）。建議值介於 0.1 到 0.2 秒，可依效能需求調整。")]
    private float _bobUpdateInterval = 0.15f;
    [SerializeField, Tooltip("姓名牌與相關 UI 的刷新間隔（秒）。")]
    private float _uiUpdateInterval = 0.5f;
    private float _nextVisualUpdateTime = 0f;
    private float _nextNameplateUpdateTime = 0f;
    private bool _forceVisualUpdate = true;
    private bool _forceImmediateNameplateUpdate = true;
    // ### 局部避障調整 ###
    [SerializeField, Tooltip("局部避障檢測的最小間隔（秒），用於降低 Physics2D.OverlapCircle 呼叫頻率。")]
    private float _avoidanceInterval = 0.12f;
    private float _lastAvoidanceTime = -999f;
    private static readonly List<Collider2D> _sharedAvoidanceResults = new List<Collider2D>(16);
    // ### 分離效果調整 ###
    private const float SeparationRadius = 1.2f;
    private const float SeparationStrength = 0.9f;
    private const float MaxSeparationOffset = 0.8f;
    private const float PortalEscapeDistance = 1.0f;
    private static readonly Collider2D[] PortalOverlapBuffer = new Collider2D[8];
    private static readonly Collider2D[] AgentAvoidanceBuffer = new Collider2D[16];
    private static readonly Collider2D[] StuckProbeBuffer = new Collider2D[8];
    private static readonly HashSet<string> UnknownLocationAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "未知地點",
        "未知地点",
        "未知位置",
        "未知區域",
        "unknown",
        "unknown location",
        "unknown place"
    };
    [SerializeField, Tooltip("群聚檢測間隔（秒），較大值可減少物理查詢次數以改善效能")] private float _crowdCheckInterval = 0.25f;
    private float _lastCrowdCheckTime = 0f;
    private bool _cachedIsCrowded = false;
    private float _cachedDensityFactor = 0f;

    [Header("移動速度調整")]
    [SerializeField, Tooltip("非傳送狀態下的基礎速度倍率。")]
    private float _baseSpeedMultiplier = 1.6f;
    [SerializeField, Tooltip("剛通過門時的速度加成倍率。")]
    private float _doorSpeedMultiplier = 1.4f;
    [SerializeField, Tooltip("其他傳送方式的速度加成倍率。")] 
    private float _generalTeleportSpeedMultiplier = 1.1f;
    [SerializeField, Tooltip("門口速度加成持續秒數。")]
    private float _doorSpeedBoostDuration = 0.9f;
    [SerializeField, Tooltip("一般傳送速度加成持續秒數。")] 
    private float _generalTeleportBoostDuration = 1.2f;
    [SerializeField, Tooltip("兩個目的地距離超過此值時啟用趕路加速。")]
    private float _longDistanceThreshold = 6f;
    [SerializeField, Tooltip("長距離行走的額外速度倍率。")]
    private float _longDistanceSpeedMultiplier = 1.5f;
    [SerializeField, Tooltip("接近目標時啟用緩慢移動的距離。")]
    private float _arrivalSlowdownDistance = 1.2f;
    [SerializeField, Range(0.1f, 1f), Tooltip("接近目標時的最低速度倍率。")]
    private float _arrivalSlowdownFactor = 0.55f;
    [SerializeField, Tooltip("在人群擁擠時的減速倍率。1 代表不減速。")]
    private float _crowdSlowdownMultiplier = 0.7f;
    [SerializeField, Tooltip("檢測人群擁擠的半徑。")]
    private float _crowdCheckRadius = 1.1f;
    [SerializeField, Tooltip("判定擁擠所需的鄰近代理人數量。")]
    private int _crowdCountThreshold = 3;

    [Header("局部避障與脫困")]
    [SerializeField, Tooltip("其他代理人的避讓檢測半徑。")]
    private float _agentAvoidanceRadius = 0.45f;
    [SerializeField, Tooltip("物件重疊時計算的推離強度。")]
    private float _agentAvoidanceStrength = 0.65f;
    [SerializeField, Tooltip("局部避障所使用的圖層遮罩。")]
    private LayerMask _agentAvoidanceMask = ~0;
    [SerializeField, Tooltip("判定陷入停滯的最小移動距離平方根閾值。")]
    private float _stuckMovementThreshold = 0.01f;
    [SerializeField, Tooltip("連續多少幀幾乎沒有移動會觸發脫困。")]
    private int _stuckFrameThreshold = 12;
    [SerializeField, Tooltip("脫困時沿著探測方向嘗試移動的距離。")]
    private float _unstuckProbeDistance = 0.35f;
    [SerializeField, Tooltip("脫困時掃描的方向數量。")]
    private int _unstuckProbeRays = 8;
    [SerializeField, Tooltip("脫困檢測時考慮的障礙圖層。")]
    private LayerMask _stuckProbeMask = ~0;

    // 用於取代舊版 NonAlloc API 的物理查詢過濾器
    private ContactFilter2D _agentAvoidanceFilter;
    private ContactFilter2D _stuckProbeFilter;
    private ContactFilter2D _portalNudgeFilter;

    private float _teleportBoostUntil = -1f;
    private bool _lastTeleportUsedDoor = false;
    private int _stuckFrameCounter = 0;
    private Vector3 _previousFramePosition;
    [SerializeField] private Color _idleNameColor = Color.white;
    [SerializeField] private Color _activeNameColor = new Color32(255, 204, 102, 255);
    private string _statusLabel = "待機";
    private string _displayName;
    private int _agentIndex;
    private static int _agentCounter = 0;
    private static int _activeAgentCount = 0;
    private static int _sleepingAgentCount = 0;
    private static bool AreAllAgentsSleeping => _activeAgentCount > 0 && _sleepingAgentCount >= _activeAgentCount;
    private bool _isCurrentlySleeping = false;    private static readonly (string english, string localized)[] LocationPrefixAliases = new (string, string)[]
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

    [Header("視覺效果")]
    [Tooltip("負責顯示代理人模型的子節點，可透過 Inspector 綁定，若為空會在 Awake 時自動尋找子物件。")]
    [SerializeField] private Transform _visualRoot;
    [Tooltip("傳送動畫持續時間，單位秒。")]
    [SerializeField] private float teleportScaleDuration = 0f;
    private Coroutine _teleportEffectCoroutine;

    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();
        _bobSeed = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        _targetPosition = _transform.position;
        _previousFramePosition = _transform.position;
        _teleportBoostUntil = -1f;
        _agentIndex = _agentCounter++;
        if (string.IsNullOrEmpty(agentName))
        {
            agentName = gameObject.name.ToUpper();
        }
        else
        {
            agentName = agentName.ToUpper();
        }

        if (!TryGetComponent(out _movementController))
        {
            _movementController = gameObject.AddComponent<AgentMovementController>();
        }

        _movementController.ConfigureFromAgent(this, _movementSpeed, _arrivalThreshold);

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

        if (_visualRoot == null)
        {
            if (_transform.childCount > 0)
            {
                _visualRoot = _transform.GetChild(0);
            }
            else
            {
                _visualRoot = _transform;
            }
        }
        
        // 初始化物理查詢過濾器
        _agentAvoidanceFilter = new ContactFilter2D();
        _agentAvoidanceFilter.SetLayerMask(_agentAvoidanceMask);
        _agentAvoidanceFilter.useTriggers = true;

        _stuckProbeFilter = new ContactFilter2D();
        _stuckProbeFilter.SetLayerMask(_stuckProbeMask);
        _stuckProbeFilter.useTriggers = true;

        _portalNudgeFilter = new ContactFilter2D();
        _portalNudgeFilter.NoFilter();
        _portalNudgeFilter.useTriggers = true;

        // 關鍵：避免初始化時「閃回 (0,0,0)」
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

        Vector3 currentPosition = _transform.position;
        Vector3 toTarget = _targetPosition - currentPosition;
        float arrivalThresholdSqr = _arrivalThreshold * _arrivalThreshold;
        float distanceSqr = toTarget.sqrMagnitude;

        if (distanceSqr <= arrivalThresholdSqr)
        {
            _transform.position = _targetPosition;
            _previousFramePosition = _targetPosition;
            _stuckFrameCounter = 0;
            UpdateVisualBobbing(0f, true);
            _forceImmediateNameplateUpdate = true;
            return;
        }

        float distanceToTarget = Mathf.Sqrt(distanceSqr);
        Vector3 direction = toTarget / distanceToTarget;
        float speed = ComputeDynamicSpeed(distanceToTarget);
        float step = speed * Time.deltaTime;
        if (step > distanceToTarget)
        {
            step = distanceToTarget;
        }

        Vector3 candidate = currentPosition + direction * step;
        bool allowPhysicsStep = (Time.frameCount + _agentIndex) % 3 == 0;
        float avoidanceInterval = Mathf.Max(0.01f, _avoidanceInterval);
        bool shouldApplyAvoidance = allowPhysicsStep && (Time.time - _lastAvoidanceTime) >= avoidanceInterval;

        if (shouldApplyAvoidance)
        {
            candidate = ApplyAgentAvoidance(candidate);
            _lastAvoidanceTime = Time.time;
        }

        if ((_targetPosition - candidate).sqrMagnitude <= arrivalThresholdSqr)
        {
            candidate = _targetPosition;
        }

        _transform.position = candidate;
        UpdateStuckDetection(currentPosition, candidate, distanceToTarget, allowPhysicsStep);
        UpdateVisualBobbing(distanceToTarget);
    }

    void LateUpdate()
    {
        if (_isInitialized && gameObject.activeSelf && nameTextUGUI != null && _mainCamera != null)
        {
            if (AreAllAgentsSleeping && !_forceImmediateNameplateUpdate)
            {
                _nextNameplateUpdateTime = Time.time + Mathf.Max(0.1f, _uiUpdateInterval);
                return;
            }
            bool shouldUpdate = _forceImmediateNameplateUpdate || Time.time >= _nextNameplateUpdateTime;
            if (shouldUpdate)
            {
                UpdateNameplatePosition();
                _nextNameplateUpdateTime = Time.time + Mathf.Max(0.1f, _uiUpdateInterval);
                _forceImmediateNameplateUpdate = false;
            }
        }
    }

    private float ComputeDynamicSpeed(float distanceToTarget)
    {
        float multiplier = Mathf.Max(0.1f, _baseSpeedMultiplier);

        if (_teleportBoostUntil >= 0f && Time.time <= _teleportBoostUntil)
        {
            float teleportMultiplier = _lastTeleportUsedDoor ? _doorSpeedMultiplier : _generalTeleportSpeedMultiplier;
            multiplier *= Mathf.Max(1f, teleportMultiplier);
        }

        if (distanceToTarget > _longDistanceThreshold)
        {
            multiplier *= Mathf.Max(1f, _longDistanceSpeedMultiplier);
        }
        else if (_arrivalSlowdownDistance > 0f)
        {
            float t = Mathf.Clamp01(distanceToTarget / _arrivalSlowdownDistance);
            float slowdown = Mathf.Lerp(Mathf.Clamp(_arrivalSlowdownFactor, 0.1f, 1f), 1f, t);
            multiplier *= slowdown;
        }

        if (IsCrowded(_transform.position, out float densityFactor))
        {
            float crowdSlowdown = Mathf.Lerp(1f, Mathf.Clamp(_crowdSlowdownMultiplier, 0.2f, 1f), densityFactor);
            multiplier *= crowdSlowdown;
        }

        return _movementSpeed * multiplier;
    }

    private Vector3 ApplyAgentAvoidance(Vector3 candidatePosition)
    {
        float detectionRadius = Mathf.Max(_agentAvoidanceRadius, SeparationRadius);
        _sharedAvoidanceResults.Clear();
        int hitCount = Physics2D.OverlapCircle((Vector2)candidatePosition, detectionRadius, _agentAvoidanceFilter, _sharedAvoidanceResults);
        if (hitCount <= 0)
        {
            return candidatePosition;
        }

        Vector2 separationAccum = Vector2.zero;
        Vector2 avoidanceAccum = Vector2.zero;
        int separationSamples = 0;
        int avoidanceSamples = 0;

        for (int i = 0; i < hitCount; i++)
        {
            var col = _sharedAvoidanceResults[i];
            if (col == null || !col.enabled) continue;
            if (col.isTrigger) continue;
            if (col.transform == _transform || col.transform.IsChildOf(_transform)) continue;

            AgentController otherAgent = col.GetComponentInParent<AgentController>();
            if (otherAgent == null || otherAgent == this || !otherAgent.gameObject.activeSelf) continue;

            Vector2 offset = (Vector2)candidatePosition - (Vector2)col.bounds.center;
            float sqrMagnitude = offset.sqrMagnitude;
            if (sqrMagnitude < 0.0001f)
            {
                offset = UnityEngine.Random.insideUnitCircle * 0.05f;
                sqrMagnitude = offset.sqrMagnitude;
                if (sqrMagnitude < 0.0001f)
                {
                    continue;
                }
            }

            float distance = Mathf.Sqrt(sqrMagnitude);
            Vector2 direction = offset / distance;

            if (distance < SeparationRadius)
            {
                float weight = (SeparationRadius - distance) / SeparationRadius;
                separationAccum += direction * weight;
                separationSamples++;
            }

            if (distance < _agentAvoidanceRadius)
            {
                float weight = Mathf.Clamp01((_agentAvoidanceRadius - distance) / _agentAvoidanceRadius);
                avoidanceAccum += direction * weight;
                avoidanceSamples++;
            }
        }

        Vector3 adjusted = candidatePosition;

        if (separationSamples > 0)
        {
            Vector2 separation = separationAccum * (SeparationStrength / Mathf.Max(1, separationSamples));
            float magnitude = separation.magnitude;
            if (magnitude > MaxSeparationOffset)
            {
                separation = separation / magnitude * MaxSeparationOffset;
            }
            adjusted += new Vector3(separation.x, separation.y, 0f);
        }

        if (avoidanceSamples > 0)
        {
            Vector2 avoidance = avoidanceAccum / Mathf.Max(1, avoidanceSamples);
            adjusted += new Vector3(avoidance.x, avoidance.y, 0f) * _agentAvoidanceStrength;
        }

        return adjusted;
    }

    private bool IsCrowded(Vector3 position, out float densityFactor)
    {
        if (_crowdCheckRadius <= 0f)
        {
            densityFactor = 0f;
            return false;
        }

        float currentTime = Time.time;
        if (currentTime - _lastCrowdCheckTime < _crowdCheckInterval)
        {
            densityFactor = _cachedDensityFactor;
            return _cachedIsCrowded;
        }

        int hitCount = Physics2D.OverlapCircle((Vector2)position, _crowdCheckRadius, _agentAvoidanceFilter, AgentAvoidanceBuffer);
        int neighbourCount = 0;
        for (int i = 0; i < hitCount; i++)
        {
            var col = AgentAvoidanceBuffer[i];
            if (col == null || !col.enabled) continue;
            if (col.isTrigger) continue;
            if (col.transform == _transform || col.transform.IsChildOf(_transform)) continue;

            AgentController otherAgent = col.GetComponentInParent<AgentController>();
            if (otherAgent == null || otherAgent == this || !otherAgent.gameObject.activeSelf) continue;
            neighbourCount++;
        }

        bool crowded = neighbourCount >= 1;
        float density = crowded ? Mathf.Clamp01(neighbourCount / Mathf.Max(1f, _crowdCountThreshold)) : 0f;

        _lastCrowdCheckTime = currentTime;
        _cachedIsCrowded = crowded;
        _cachedDensityFactor = density;

        densityFactor = density;
        return crowded;
    }

    private void UpdateStuckDetection(Vector3 previousPosition, Vector3 currentPosition, float distanceToTarget, bool allowCheck)
    {
        if (distanceToTarget <= _arrivalThreshold)
        {
            _stuckFrameCounter = 0;
            _previousFramePosition = currentPosition;
            return;
        }

        if (!allowCheck)
        {
            _previousFramePosition = currentPosition;
            return;
        }

        float movedSqr = (currentPosition - previousPosition).sqrMagnitude;
        float thresholdSqr = _stuckMovementThreshold * _stuckMovementThreshold;
        if (movedSqr < thresholdSqr)
        {
            _stuckFrameCounter++;
            if (_stuckFrameCounter >= Mathf.Max(3, _stuckFrameThreshold))
            {
                Vector3 nudge = FindUnstuckOffset();
                if (nudge.sqrMagnitude > 0.0001f)
                {
                    Vector3 adjusted = currentPosition + nudge;
                    _transform.position = adjusted;
                    _targetPosition += nudge;
                    currentPosition = adjusted;
                }

                _stuckFrameCounter = 0;
            }
        }
        else
        {
            _stuckFrameCounter = 0;
        }

        _previousFramePosition = currentPosition;
    }

    private Vector3 FindUnstuckOffset()
    {
        if (_unstuckProbeDistance <= 0f) return Vector3.zero;

        int rays = Mathf.Max(3, _unstuckProbeRays);
        float radius = Mathf.Max(0.05f, _agentAvoidanceRadius * 0.5f);
        Vector3 origin = _transform.position;

        for (int i = 0; i < rays; i++)
        {
            float rad = (Mathf.PI * 2f / rays) * i;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector3 candidate = origin + new Vector3(dir.x, dir.y, 0f) * _unstuckProbeDistance;

            int hitCount = Physics2D.OverlapCircle((Vector2)candidate, radius, _stuckProbeFilter, StuckProbeBuffer);
            bool blocked = false;
            for (int h = 0; h < hitCount; h++)
            {
                var col = StuckProbeBuffer[h];
                if (col == null || !col.enabled) continue;
                if (col.isTrigger) continue;
                if (col.transform == _transform || col.transform.IsChildOf(_transform)) continue;
                blocked = true;
                break;
            }
            if (!blocked)
            {
                return new Vector3(dir.x, dir.y, 0f) * _unstuckProbeDistance;
            }
        }

        Vector2 random = UnityEngine.Random.insideUnitCircle;
        if (random.sqrMagnitude < 0.0001f)
        {
            random = Vector2.up;
        }

        return new Vector3(random.x, random.y, 0f) * (_unstuckProbeDistance * 0.5f);
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
        if (sleeping == _isCurrentlySleeping)
        {
            return;
        }

        if (sleeping)
        {
            _sleepingAgentCount++;
        }
        else if (_isCurrentlySleeping)
        {
            _sleepingAgentCount = Mathf.Max(0, _sleepingAgentCount - 1);
        }

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
    private static bool IsUnknownLocation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (UnknownLocationAliases.Contains(trimmed))
        {
            return true;
        }

        string lower = trimmed.ToLowerInvariant();
        if (UnknownLocationAliases.Contains(lower))
        {
            return true;
        }

        if (lower.Contains("未知") || lower.Contains("unknown"))
        {
            return true;
        }

        return false;
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
    internal bool TryFindLocationTransform(string locationName, out Transform transform)
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
   private string DetermineBuildingFromTransform(Transform target)
    {
        if (target == null)
        {
            return null;
        }

        BuildingController building = target.GetComponentInParent<BuildingController>();
        if (building != null && !string.IsNullOrEmpty(building.buildingName))
        {
            return building.buildingName;
        }

        return null;
    }

    internal string GetBuildingFromTransform(Transform target)
    {
        return DetermineBuildingFromTransform(target);
    }

    internal string GuessBuildingForLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
        {
            return null;
        }

        if (TryFindLocationTransform(locationName, out Transform locationTransform) && locationTransform != null)
        {
            return DetermineBuildingFromTransform(locationTransform);
        }

        return null;
    }

    internal string GuessCurrentBuilding()
    {
        string fromTransform = DetermineBuildingFromTransform(_transform);
        if (!string.IsNullOrEmpty(fromTransform))
        {
            return fromTransform;
        }

        string fromLast = GuessBuildingForLocation(_lastValidLocationName);
        if (!string.IsNullOrEmpty(fromLast))
        {
            return fromLast;
        }

        return GuessBuildingForLocation(_targetLocationName);
    }
    public void UpdateState(AgentState state)
    {
        if (!_isInitialized || state == null) return;

        if (Time.time - _lastStateApplyTime < _minStateApplyInterval)
        {
            return;
        }

        string incomingLocation = state.Location ?? string.Empty;
        incomingLocation = incomingLocation.Trim();

        if (!string.IsNullOrEmpty(incomingLocation) &&
            !string.IsNullOrEmpty(_lastValidLocationName) &&
            string.Equals(incomingLocation, _lastValidLocationName, StringComparison.OrdinalIgnoreCase))
        {
            float dist = Vector2.Distance(_transform.position, _targetPosition);
            if (dist <= _arrivalThreshold * 1.5f)
            {
                _currentAction = state.CurrentState;
                _lastStateApplyTime = Time.time;
                return;
            }
        }
        if (!_isInitialized) return;
        if (state == null) return;

        incomingLocation = (state.Location ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(incomingLocation))
        {
            _currentAction = state.CurrentState;
            return;
        }
        if (IsUnknownLocation(incomingLocation))
        {
            _currentAction = state.CurrentState;
            UpdateStatusIndicatorFromAction(_currentAction);
            _lastStateApplyTime = Time.time;
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
            SetTargetLocation(resolvedKey, targetLocation.position, targetLocation);
        }
        else if (TryParseVector3(incomingLocation, out Vector3 pos))
        {
            if (TryResolveCoordinateLocation(pos, out string resolvedLocation, out Vector3 resolvedPosition, out Transform resolvedTransform))
            {
                SetTargetLocation(resolvedLocation, resolvedPosition, resolvedTransform);
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
            _targetPosition = _transform.position;
        }
        else if (!IsUnknownLocation(state.Location))
        {
            Debug.LogWarning($"地點 '{state.Location}' 在場景中未找到，代理人 '{agentName}' 將停在原地。");
        }
        _currentAction = state.CurrentState;
        UpdateStatusIndicatorFromAction(_currentAction);
        
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
        if (string.IsNullOrWhiteSpace(locationName) || _locationTransforms == null)
        {
            return false;
        }

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

        resolvedTransform = null;
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
        NudgeAwayFromPortals();
        _visualOffset = Vector3.zero;
        
        // ======== 修改：通知移動完成 ========
        NotifyMovementCompleted();
        // ====================================
        
        OnTeleported(false, suppressEffects);
    }

    public void OnTeleported(bool usedDoor, bool suppressEffects = false)
    {
        _previousFramePosition = _transform.position;
        _stuckFrameCounter = 0;
        _lastTeleportUsedDoor = usedDoor;

        float duration = usedDoor ? _doorSpeedBoostDuration : _generalTeleportBoostDuration;
        if (duration > 0f)
        {
            _teleportBoostUntil = Time.time + duration;
        }
        else
        {
            _teleportBoostUntil = -1f;
        }

        ForceImmediateVisualRefresh();

        if (_visualRoot != null && !suppressEffects)
        {
            if (_teleportEffectCoroutine != null)
            {
                StopCoroutine(_teleportEffectCoroutine);
            }
            _teleportEffectCoroutine = StartCoroutine(PlayTeleportEffect());
        }
        _simulationClient?.ReportTeleport(agentName);

    }

    public void SyncTargetToCurrentPosition()
    {
        _targetPosition = _transform.position;
        _movementController?.HandleTeleport(_transform.position);
        _visualOffset = Vector3.zero;
        ForceImmediateVisualRefresh();
    }

    public void SetActionState(string action)
    {
        _currentAction = action;
        UpdateStatusIndicatorFromAction(action);
        if (bubbleController == null) return;

        string bubbleText = BuildBubbleText(action);
        if (!string.IsNullOrEmpty(bubbleText))
        {
            bubbleController.顯示氣泡(bubbleText, _transform);
        }
    }
    internal void ForceImmediateVisualRefresh()
    {
        _forceVisualUpdate = true;
        _nextVisualUpdateTime = Time.time;
        _forceImmediateNameplateUpdate = true;
        _nextNameplateUpdateTime = Time.time;
    }

    public void ApplyActionInstruction(AgentActionInstruction instruction)
    {
        if (!_isInitialized || instruction == null) return;

        string command = instruction.Command?.Trim()?.ToLowerInvariant();

        if (command == "teleport")
        {
            HandleTeleportInstruction(instruction);
        }
        else if (command == "move")
        {
            HandleMoveInstruction(instruction);
        }
        else if (command == "interact")
        {
            SetActionState(instruction.Action);
            _lastInstructionDestination = instruction.Destination;
        }
    }


// === 修改 2: 在類別中添加新方法（建議放在 ApplyActionInstruction 之後）===

    // ======== 新增：處理傳送指令 ========
    private void HandleTeleportInstruction(AgentActionInstruction instruction)
    {
        if (IsUnknownLocation(instruction.Destination) || IsUnknownLocation(instruction.ToPortal))
        {
            Debug.LogWarning($"[Agent {agentName}] 忽略傳送至未知地點的指令。");
            _simulationClient?.ReportTeleport(agentName);
            return;
        }

        Vector3 exitPosition = _transform.position;
        string resolvedLocation = null;
        Transform exitTransform;

        if (!string.IsNullOrWhiteSpace(instruction.ToPortal) &&
            TryFindLocationTransform(instruction.ToPortal, out exitTransform) &&
            exitTransform != null)
        {
            resolvedLocation = ResolveLocationKey(instruction.ToPortal, exitTransform);
            exitPosition = exitTransform.position;
        }
        else if (!string.IsNullOrWhiteSpace(instruction.Destination) &&
                 TryFindLocationTransform(instruction.Destination, out exitTransform) &&
                 exitTransform != null)
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
            string targetPortal = string.IsNullOrWhiteSpace(instruction.ToPortal)
                ? instruction.Destination
                : instruction.ToPortal;
            Debug.LogWarning($"[Agent {agentName}] 無法解析傳送出口 '{targetPortal}'，將忽略此傳送指令。");
            return;
        }

        List<string> manualAliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolvedLocation))
        {
            manualAliases.Add(resolvedLocation);
        }
        if (!string.IsNullOrWhiteSpace(instruction.ToPortal) &&
            !manualAliases.Contains(instruction.ToPortal))
        {
            manualAliases.Add(instruction.ToPortal);
        }
        if (!string.IsNullOrWhiteSpace(instruction.Destination) &&
            !manualAliases.Contains(instruction.Destination))
        {
            manualAliases.Add(instruction.Destination);
        }

        TeleportTo(exitPosition, true, manualAliases.ToArray());

        string nextLocationName = !string.IsNullOrWhiteSpace(resolvedLocation)
            ? resolvedLocation
            : (!string.IsNullOrWhiteSpace(instruction.ToPortal)
                ? instruction.ToPortal
                : instruction.Destination);

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

        string nextStep = string.IsNullOrWhiteSpace(instruction.NextStep)
            ? instruction.Destination
            : instruction.NextStep;

        bool destinationChanged = !string.IsNullOrWhiteSpace(instruction.Destination) &&
            !string.Equals(_lastInstructionDestination, instruction.Destination, StringComparison.OrdinalIgnoreCase);

        bool pathChanged = !string.IsNullOrWhiteSpace(nextStep) &&
            !string.Equals(_targetLocationName, nextStep, StringComparison.OrdinalIgnoreCase);

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
            : (!string.IsNullOrWhiteSpace(_targetLocationName) ? _targetLocationName : null);

        if (string.IsNullOrWhiteSpace(candidate) &&
            TryResolveCoordinateLocation(_transform.position, out string resolvedLocation, out Vector3 _, out Transform _))
        {
            _lastValidLocationName = resolvedLocation;
            candidate = resolvedLocation;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            string guessed = GuessCurrentBuilding();
            if (!string.IsNullOrWhiteSpace(guessed))
            {
                candidate = guessed;
            }
        }

        return LocationNameLocalizer.ToDisplayName(candidate);
    }
    void OnEnable()
    {
        _activeAgentCount++;
        if (_isCurrentlySleeping)
        {
            _sleepingAgentCount = Mathf.Max(0, _sleepingAgentCount - 1);
        }
        _isCurrentlySleeping = false;
        _visualOffset = Vector3.zero;
        _targetPosition = _transform.position;
        _bobSeed = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        _previousFramePosition = _transform.position;
        _teleportBoostUntil = -1f;
        _stuckFrameCounter = 0;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        if (_visualRoot != null)
        {
            _visualRoot.localPosition = Vector3.zero;
            _visualRoot.localScale = Vector3.one;
        }
        _forceVisualUpdate = true;
        _forceImmediateNameplateUpdate = true;
        _nextVisualUpdateTime = Time.time;
        _lastAvoidanceTime = -999f;
        _nextNameplateUpdateTime = Time.time;
    }

    void OnDisable()
    {
        if (_isCurrentlySleeping)
        {
            _sleepingAgentCount = Mathf.Max(0, _sleepingAgentCount - 1);
        }
        _activeAgentCount = Mathf.Max(0, _activeAgentCount - 1);
        _isCurrentlySleeping = false;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
        _visualOffset = Vector3.zero;
        if (_visualRoot != null)
        {
            _visualRoot.localPosition = Vector3.zero;
            _visualRoot.localScale = Vector3.one;
        }
        _teleportBoostUntil = -1f;
        _stuckFrameCounter = 0;
        _forceVisualUpdate = true;
        _forceImmediateNameplateUpdate = true;
    }

    private void NudgeAwayFromPortals()
    {
        int hitCount = Physics2D.OverlapCircle((Vector2)_transform.position, PortalEscapeDistance, _portalNudgeFilter, PortalOverlapBuffer);
        if (hitCount <= 0)
        {
            return;
        }

        Vector2 cumulative = Vector2.zero;
        int portalCount = 0;

        for (int i = 0; i < hitCount; i++)
        {
            var collider = PortalOverlapBuffer[i];
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
    
    private void UpdateVisualBobbing(float distanceToTarget, bool forceUpdate = false)
    {
        if (_visualRoot == null) return;

        bool shouldUpdate = forceUpdate || _forceVisualUpdate || Time.time >= _nextVisualUpdateTime;
        if (!shouldUpdate)
        {
            return;
        }
        if (!forceUpdate && !_forceVisualUpdate && AreAllAgentsSleeping)
        {
            _nextVisualUpdateTime = Time.time + Mathf.Max(0.05f, _bobUpdateInterval);
            return;
        }
        bool isMoving = distanceToTarget > _arrivalThreshold;
        float amplitude = isMoving ? _movingBobAmplitude : _idleBobAmplitude;
        float frequency = isMoving ? _movingBobFrequency : _idleBobFrequency;

        float offsetY = Mathf.Sin(Time.time * frequency + _bobSeed) * amplitude;
        _visualOffset = new Vector3(0f, offsetY, 0f);

        _visualRoot.localPosition = _visualOffset;
        _nextVisualUpdateTime = Time.time + Mathf.Max(0.05f, _bobUpdateInterval);
        _forceVisualUpdate = false;
    }
    
    private IEnumerator PlayTeleportEffect()
    {
        if (_visualRoot == null || teleportScaleDuration <= 0f)
        {
            yield break;
        }

        _visualRoot.localScale = Vector3.zero;
        float elapsed = 0f;
        while (elapsed < teleportScaleDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / teleportScaleDuration);
            float scale = Mathf.SmoothStep(0f, 1f, t);
            _visualRoot.localScale = new Vector3(scale, scale, scale);
            yield return null;
        }
        _visualRoot.localScale = Vector3.one;
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
        if (IsSleepAction(action))
        {
            ShowSleepingStatus(action);
        }
        else if (IsIdleAction(action))
        {
            ShowIdleStatus();
        }
        else
        {
            ShowActiveStatus(action);
        }
    }

    private static bool IsIdleAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return true;
        }

        string lower = action.Trim().ToLowerInvariant();
        return lower.Contains("idle") || lower.Contains("待機") || lower.Contains("站立") || lower.Contains("wait") || lower.Contains("stand");
    }
    private static bool IsSleepAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        string lower = action.Trim().ToLowerInvariant();
        return lower.Contains("sleep") || lower.Contains("睡") || lower.Contains("nap");
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
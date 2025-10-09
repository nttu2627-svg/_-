// Scripts/Agent/AgentController.cs (安全边距最终版)

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
    // 全局代理人列表，用於近身避障與分離
    private static readonly List<AgentController> ActiveAgents = new List<AgentController>();
    // ### 分離效果調整 ###
    // 加大半徑與力度，避免多個代理人模型重疊在同一位置
    private const float SeparationRadius = 1.2f;
    private const float SeparationStrength = 0.9f;
    private const float MaxSeparationOffset = 0.8f;
    // 傳送後推離門口的距離加大，減少卡在門口的情況
    private const float PortalEscapeDistance = 1.0f;
    private static readonly Collider2D[] PortalOverlapBuffer = new Collider2D[8];
    private static readonly Collider2D[] AgentAvoidanceBuffer = new Collider2D[16];

    // 優化：使用 List 存儲物理查詢結果，取代 NonAlloc 缓冲陣列，並與 ContactFilter 配合，減少 GC 與棧上配置
    // 這些 List 會在類間共用並持續重用，以避免每幀產生新的集合
    private static readonly List<Collider2D> _agentDetectionResults = new List<Collider2D>(16);
    private static readonly List<Collider2D> _portalDetectionResults = new List<Collider2D>(8);
    // ContactFilter 用於指定圖層遮罩
    private static ContactFilter2D _agentContactFilter;
    private static bool _contactFilterInitialized = false;

    // 群聚檢測的緩存：避免每幀都執行 Physics Overlap，改為定時更新
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

    private float _teleportBoostUntil = -1f;
    private bool _lastTeleportUsedDoor = false;
    private int _stuckFrameCounter = 0;
    private Vector3 _previousFramePosition;
    [SerializeField] private Color _idleNameColor = Color.white;
    [SerializeField] private Color _activeNameColor = new Color32(255, 204, 102, 255);
    private string _statusLabel = "待機";
    private string _displayName;
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

    [Header("視覺效果")]
    [Tooltip("負責顯示代理人模型的子節點，可透過 Inspector 綁定，若為空會在 Awake 時自動尋找子物件。")]
    [SerializeField] private Transform _visualRoot;
    [Tooltip("傳送動畫持續時間，單位秒。")]
    [SerializeField] private float teleportScaleDuration = 0.25f;
    private Coroutine _teleportEffectCoroutine;

    /// <summary>
    /// 初始化 ContactFilter 用於物理查詢。此方法只會呼叫一次。
    /// </summary>
    private void EnsureContactFilter()
    {
        if (_contactFilterInitialized) return;
        _agentContactFilter = new ContactFilter2D();
        _agentContactFilter.SetLayerMask(_agentAvoidanceMask);
        _agentContactFilter.useLayerMask = true;
        // 忽略觸發器避免計算 trigger collider
        _agentContactFilter.useTriggers = false;
        _contactFilterInitialized = true;
    }
    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();
        _bobSeed = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        _targetPosition = _transform.position;
        _previousFramePosition = _transform.position;
        _teleportBoostUntil = -1f;
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

        // 若沒有綁定視覺根節點，嘗試自動找到第一個子節點用於動態效果
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
    }

    void Update()
    {
        if (!_isInitialized || !gameObject.activeSelf) return;

        Vector3 currentPosition = _transform.position;
        float distanceToTarget = Vector3.Distance(currentPosition, _targetPosition);

        if (distanceToTarget <= _arrivalThreshold)
        {
            // 直接對齊目標位置
            _transform.position = _targetPosition;
            _previousFramePosition = _targetPosition;
            _stuckFrameCounter = 0;
            // 更新待機時的浮動動畫
            UpdateVisualBobbing(0f);
            return;
        }

        float speed = ComputeDynamicSpeed(distanceToTarget);
        Vector3 direction = distanceToTarget > 0.0001f ? (_targetPosition - currentPosition).normalized : Vector3.zero;
        float step = speed * Time.deltaTime;
        if (step > distanceToTarget) step = distanceToTarget;

        Vector3 candidate = currentPosition + direction * step;
        candidate = ApplyAgentAvoidance(candidate);

        if (Vector3.Distance(candidate, _targetPosition) <= _arrivalThreshold)
        {
            candidate = _targetPosition;
        }

        _transform.position = candidate;
        UpdateStuckDetection(currentPosition, candidate, distanceToTarget);
        // 移動時更新視覺浮動效果
        UpdateVisualBobbing(distanceToTarget);
    }

    void LateUpdate()
    {
        if (_isInitialized && gameObject.activeSelf && nameTextUGUI != null && _mainCamera != null)
        {
            UpdateNameplatePosition();
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
        candidatePosition += ComputeSeparation(candidatePosition);

        // 使用 List 版本的 OverlapCircle 搭配 ContactFilter，提高效能並避免非預期的 GC 產生
        EnsureContactFilter();
        _agentDetectionResults.Clear();
        Physics2D.OverlapCircle((Vector2)candidatePosition, _agentAvoidanceRadius, _agentContactFilter, _agentDetectionResults);
        int hits = _agentDetectionResults.Count;
        if (hits <= 0)
        {
            return candidatePosition;
        }

        Vector2 separation = Vector2.zero;
        for (int i = 0; i < hits; i++)
        {
            var col = _agentDetectionResults[i];
            if (col == null || !col.enabled) continue;
            if (col.transform.IsChildOf(_transform)) continue;

            AgentController otherAgent = col.GetComponentInParent<AgentController>();
            if (otherAgent == null || otherAgent == this || !otherAgent.gameObject.activeSelf) continue;

            Vector2 away = (Vector2)(candidatePosition - (Vector3)col.bounds.center);
            float sqrMag = away.sqrMagnitude;
            if (sqrMag < 0.0001f)
            {
                away = UnityEngine.Random.insideUnitCircle.normalized * 0.1f;
                sqrMag = away.sqrMagnitude;
            }

            float distance = Mathf.Sqrt(sqrMag);
            float weight = Mathf.Clamp01((_agentAvoidanceRadius - distance) / _agentAvoidanceRadius);
            separation += away.normalized * weight;
        }

        if (separation.sqrMagnitude > 0.0001f)
        {
            Vector3 offset = new Vector3(separation.x, separation.y, 0f) * _agentAvoidanceStrength;
            candidatePosition += offset;
        }

        return candidatePosition;
    }

    private bool IsCrowded(Vector3 position, out float densityFactor)
    {
        // 使用緩存以降低頻繁物理查詢造成的效能負擔
        if (_crowdCheckRadius <= 0f)
        {
            densityFactor = 0f;
            return false;
        }

        // 若在檢查間隔之內，直接返回上次結果
        float currentTime = Time.time;
        if (currentTime - _lastCrowdCheckTime < _crowdCheckInterval)
        {
            densityFactor = _cachedDensityFactor;
            return _cachedIsCrowded;
        }

        EnsureContactFilter();
        _agentDetectionResults.Clear();
        Physics2D.OverlapCircle((Vector2)position, _crowdCheckRadius, _agentContactFilter, _agentDetectionResults);
        int neighbourCount = 0;
        for (int i = 0; i < _agentDetectionResults.Count; i++)
        {
            var col = _agentDetectionResults[i];
            if (col == null || !col.enabled) continue;
            if (col.transform.IsChildOf(_transform)) continue;

            AgentController otherAgent = col.GetComponentInParent<AgentController>();
            if (otherAgent == null || otherAgent == this || !otherAgent.gameObject.activeSelf) continue;
            neighbourCount++;
        }

        bool crowded = neighbourCount >= 1;
        float density = 0f;
        if (crowded)
        {
            density = Mathf.Clamp01(neighbourCount / Mathf.Max(1f, _crowdCountThreshold));
        }

        // 更新緩存
        _lastCrowdCheckTime = currentTime;
        _cachedIsCrowded = crowded;
        _cachedDensityFactor = density;

        densityFactor = density;
        return crowded;
    }

    private void UpdateStuckDetection(Vector3 previousPosition, Vector3 currentPosition, float distanceToTarget)
    {
        float movedSqr = (currentPosition - previousPosition).sqrMagnitude;
        float thresholdSqr = _stuckMovementThreshold * _stuckMovementThreshold;

        if (distanceToTarget <= _arrivalThreshold)
        {
            _stuckFrameCounter = 0;
            _previousFramePosition = currentPosition;
            return;
        }

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
            float angle = (360f / rays) * i;
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector3 candidate = origin + new Vector3(dir.x, dir.y, 0f) * _unstuckProbeDistance;

            // 使用臨時 contact filter 進行障礙檢測
            ContactFilter2D probeFilter = new ContactFilter2D();
            probeFilter.SetLayerMask(_stuckProbeMask);
            probeFilter.useLayerMask = true;
            probeFilter.useTriggers = false;
            _agentDetectionResults.Clear();
            Physics2D.OverlapCircle((Vector2)candidate, radius, probeFilter, _agentDetectionResults);
            bool blocked = false;
            for (int h = 0; h < _agentDetectionResults.Count; h++)
            {
                var col = _agentDetectionResults[h];
                if (col == null || !col.enabled) continue;
                if (col.isTrigger) continue;
                if (col.transform.IsChildOf(_transform)) continue;
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

        // 頻率限制：同一代理人太頻繁的更新會被略過
        if (Time.time - _lastStateApplyTime < _minStateApplyInterval)
        {
            return;
        }

        string incomingLocation = state.Location ?? string.Empty;
        incomingLocation = incomingLocation.Trim();

        // 若地點文字與目前的目標地點相同，且距離已經非常近，直接略過，避免重建路徑
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
            // 若後端傳來的地點在場景中沒有對應的標記（例如 "公寓"），
            // 則維持目前的位置，避免代理人被傳回原點。
            _targetPosition = _transform.position;
        }
        else
        {
            Debug.LogWarning($"地點 '{state.Location}' 在場景中未找到，代理人 '{agentName}' 將停在原地。");
        }
        _currentAction = state.CurrentState;
        UpdateStatusIndicatorFromAction(_currentAction);

        // 成功處理一次才更新時間戳
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
        _movementController?.HandleTeleport(position);
        SetManualLocationOverrides(locationAliases);
        _lastInstructionDestination = null;
        NudgeAwayFromPortals();
        _visualOffset = Vector3.zero;
        NotifyMovementCompleted();
       OnTeleported(false);

    }

    public void OnTeleported(bool usedDoor)
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

        // 執行傳送視覺效果：快速縮放以提供過渡效果
        if (_visualRoot != null)
        {
            if (_teleportEffectCoroutine != null)
            {
                StopCoroutine(_teleportEffectCoroutine);
            }
            _teleportEffectCoroutine = StartCoroutine(PlayTeleportEffect());
        }

    }

    public void SyncTargetToCurrentPosition()
    {
        _targetPosition = _transform.position;
        _movementController?.HandleTeleport(_transform.position);
        _visualOffset = Vector3.zero;
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

    public void ApplyActionInstruction(AgentActionInstruction instruction)
    {
        if (!_isInitialized || instruction == null) return;

        string command = instruction.Command?.Trim()?.ToLowerInvariant();
        if (command == "teleport")
        {
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

            TeleportTo(exitPosition, manualAliases.ToArray());

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
        else if (command == "move")
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
        _previousFramePosition = _transform.position;
        _teleportBoostUntil = -1f;
        _stuckFrameCounter = 0;
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        // 重設視覺模型的位置與縮放
        if (_visualRoot != null)
        {
            _visualRoot.localPosition = Vector3.zero;
            _visualRoot.localScale = Vector3.one;
        }
    }

    void OnDisable()
    {
        ActiveAgents.Remove(this);
        if (nameTextUGUI != null) nameTextUGUI.gameObject.SetActive(false);
        SetManualLocationOverrides();
        _visualOffset = Vector3.zero;
        // 禁用時重置視覺模型的位置與縮放
        if (_visualRoot != null)
        {
            _visualRoot.localPosition = Vector3.zero;
            _visualRoot.localScale = Vector3.one;
        }
        _teleportBoostUntil = -1f;
        _stuckFrameCounter = 0;
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
        // 使用可重用的 List 取得附近的傳送門，減少記憶體配置
        _portalDetectionResults.Clear();
        // ContactFilter2D 預設不設置圖層過濾，會返回所有重疊 collider
        ContactFilter2D portalFilter = new ContactFilter2D();
        portalFilter.useTriggers = true; // 傳送門通常為 trigger collider
        // 不設 useLayerMask 表示不過濾圖層
        Physics2D.OverlapCircle((Vector2)_transform.position, PortalEscapeDistance, portalFilter, _portalDetectionResults);
        if (_portalDetectionResults.Count == 0)
        {
            return;
        }

        Vector2 cumulative = Vector2.zero;
        int portalCount = 0;

        for (int i = 0; i < _portalDetectionResults.Count; i++)
        {
            var collider = _portalDetectionResults[i];
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

    /// <summary>
    /// 根據代理人當前狀態更新視覺浮動效果。移動時使用較大幅度與頻率，待機時則較為平緩。
    /// 此方法會更新 _visualOffset 並同步到 _visualRoot 的 localPosition。
    /// </summary>
    /// <param name="distanceToTarget">當前距離目標的距離，用來判斷是否正在移動</param>
    private void UpdateVisualBobbing(float distanceToTarget)
    {
        // 沒有視覺節點時直接返回
        if (_visualRoot == null) return;

        // 判斷使用移動或待機參數
        bool isMoving = distanceToTarget > _arrivalThreshold;
        float amplitude = isMoving ? _movingBobAmplitude : _idleBobAmplitude;
        float frequency = isMoving ? _movingBobFrequency : _idleBobFrequency;

        // 計算正弦偏移
        float offsetY = Mathf.Sin(Time.time * frequency + _bobSeed) * amplitude;
        _visualOffset = new Vector3(0f, offsetY, 0f);

        // 將偏移套用至視覺根物件
        _visualRoot.localPosition = _visualOffset;
    }

    /// <summary>
    /// 傳送時的縮放動畫，用於提供視覺過渡效果。會將 _visualRoot 的縮放從 0 漸變至 1。
    /// </summary>
    private IEnumerator PlayTeleportEffect()
    {
        if (_visualRoot == null || teleportScaleDuration <= 0f)
        {
            yield break;
        }

        // 初始縮放至 0，隱藏模型
        _visualRoot.localScale = Vector3.zero;
        float elapsed = 0f;
        while (elapsed < teleportScaleDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / teleportScaleDuration);
            // 使用 SmoothStep 使放大過程更自然
            float scale = Mathf.SmoothStep(0f, 1f, t);
            _visualRoot.localScale = new Vector3(scale, scale, scale);
            yield return null;
        }
        _visualRoot.localScale = Vector3.one;
    }
    internal void NotifyMovementStarted()
    {
        ShowActiveStatus("執行任務");
    }

    internal void NotifyMovementCompleted()
    {
        UpdateStatusIndicatorFromAction(_currentAction);
    }

    private void ShowIdleStatus()
    {
        UpdateStatusIndicator("待機", false);
    }

    private void ShowActiveStatus(string status)
    {
        string label = string.IsNullOrWhiteSpace(status) ? "執行任務" : status.Trim();
        UpdateStatusIndicator(label, true);
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
        if (IsIdleAction(action))
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
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
using System.Collections; // 必須引用

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

[Serializable]
public struct ReactionData
{
    public string action;
    public string target;
    public string anim;
}
[RequireComponent(typeof(NavMeshAgent))]
public class AgentController : MonoBehaviour
{
    [Header("狀態監控")]
    // 公開屬性供外部讀取，但不允許外部直接修改
    public bool IsStunned { get; private set; } = false;

    private NavMeshAgent _navAgent;
    private Coroutine _currentStunCoroutine;
    [HideInInspector]
    [Tooltip("代理人名稱")]
    public string agentName;

    [HideInInspector]
    [Tooltip("顯示名稱的 UI 文字組件")]
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
    private Animator _animator; // [New] For direct animation control
    
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
    private bool _isMoving = false;        // 用於判斷是否正在移動
    private Vector3 _originalScale;        // 用於擠壓動畫
    private float _wobbleTime = 0f;        // 用於動畫計時
    private const float TELEPORT_THRESHOLD = 5.0f; // 超過此距離則瞬移
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

    // 公開屬性
    public AgentMovementController MovementController => _movementController;
    public bool IsMoving => _isMoving; // [Fix] Expose IsMoving property
    public string TargetLocationName => _targetLocationName; // [New] Expose TargetLocationName

    void Awake()
    {
        _transform = transform;
        _mainCamera = Camera.main;
        _simulationClient = FindFirstObjectByType<SimulationClient>();
        _originalScale = transform.localScale; // [修改 2] 初始化原始縮放大小

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
        
        _animator = GetComponentInChildren<Animator>(); // [New] Initialize Animator

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

    // Fail-safe variables
    private float _stuckTimer = 0f;
    private const float STUCK_TIME_THRESHOLD = 5.0f;
    private const float STUCK_VELOCITY_THRESHOLD = 0.1f;
    private const float PORTAL_DETECTION_RADIUS = 3.0f;

void Update()
    {
        // [修正 1] 正確的暈眩檢查：如果暈眩中，直接暫停 Update 的導航邏輯
        if (IsStunned) return;

        // 1. 檢查移動狀態
        CheckMovementStatus();

        // 2. 處理面朝方向 (Flip)
        HandleFacingDirection();

        // 3. 程式化動畫 (Liveliness)
        HandleProceduralAnimation();

        // 4. 解決重疊 (Resolve Overlap)
        if (!_isMoving)
        {
            ResolveOverlap();
        }

        // 5. 檢查是否卡在傳送門附近
        CheckAndResolvePortalStuck();

        // 6. 動態更新避障優先級 (解決群體擁擠問題)
        UpdateDynamicAvoidancePriority();

        if (!_isInitialized || !gameObject.activeSelf) return;
        if (_isPortalPaused) return;

        // 計算平滑速度與視覺更新
        Vector3 sampledVelocity = (_transform.position - _lastPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled && _navMeshDriving)
        {
            sampledVelocity = _navMeshAgent.velocity;
            _targetPosition = _navMeshAgent.destination; // 更新目標點以便同步
        }

        _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, sampledVelocity, Time.deltaTime * _interpolationSpeed);

        if (_visualController != null)
        {
            _visualController.UpdateVisuals(_smoothedVelocity);
        }

        _lastPosition = _transform.position;

        // 物理移動兜底 (如果沒有 NavMeshAgent)
        if (_navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled)
        {
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

// [修正 2] 新增公開方法供外部呼叫
    public void Stun(float duration)
    {
        // 如果已經在暈眩中，先停止舊的協程
        if (_currentStunCoroutine != null)
        {
            StopCoroutine(_currentStunCoroutine);
        }
        
        // 啟動新的暈眩協程
        _currentStunCoroutine = StartCoroutine(Co_Stun(duration));
    }

    // [修正 3] 實作暈眩邏輯的協程 (IEnumerator)
    private IEnumerator Co_Stun(float duration)
    {
        IsStunned = true;

        // 使用 _navMeshAgent (與原本代碼變數名稱一致)
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
        {
            // 放棄當前路徑與速度，避免與推力對抗
            _navMeshAgent.ResetPath();
            _navMeshAgent.velocity = Vector3.zero;
            _navMeshAgent.isStopped = true; 
        }

        // 等待指定時間 (這段時間 Portal 的 PushOut 可以自由運作)
        yield return new WaitForSeconds(duration);

        // 恢復狀態
        IsStunned = false;
        
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
        {
            _navMeshAgent.isStopped = false;
        }

        _currentStunCoroutine = null;
    }
    private void CheckAndResolvePortalStuck()
    {
        // 整合通用卡死檢測與傳送門卡死檢測
        if (!_isMoving || _navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled)
        {
            _stuckTimer = 0f;
            return;
        }

        // 1. 通用卡死檢測 (General Stuck Detection)
        // 如果代理人應該移動 (_isMoving) 但速度極低
        if (_navMeshAgent.velocity.sqrMagnitude < STUCK_VELOCITY_THRESHOLD)
        {
            _stuckTimer += Time.deltaTime;
        }
        else
        {
            _stuckTimer = 0f;
        }

        if (_stuckTimer > STUCK_TIME_THRESHOLD)
        {
            Debug.LogWarning($"[Agent {agentName}] Detected stuck (Velocity < {STUCK_VELOCITY_THRESHOLD}) for {_stuckTimer:F1}s. Attempting resolution.");

            // 優先檢查是否在傳送門附近 (既有邏輯)
            bool resolvedByPortal = false;
            Collider[] hits = Physics.OverlapSphere(transform.position, PORTAL_DETECTION_RADIUS);
            foreach (var hit in hits)
            {
                PortalTrigger portalTrigger = hit.GetComponent<PortalTrigger>();
                if (portalTrigger != null && portalTrigger.portal != null && portalTrigger.portal.TargetPortal != null)
                {
                    Debug.Log($"[Agent {agentName}] Stuck near portal {portalTrigger.name}. Teleporting to exit.");
                    
                    Transform exitTransform = portalTrigger.portal.TargetPortal.ExitTransform;
                    if (exitTransform != null)
                    {
                        Vector3 pushDirection = exitTransform.forward; 
                        Vector3 targetPos = exitTransform.position + pushDirection * 1.5f;

                        if (UnityEngine.AI.NavMesh.SamplePosition(targetPos, out UnityEngine.AI.NavMeshHit navHit, 2.0f, UnityEngine.AI.NavMesh.AllAreas))
                        {
                            targetPos = navHit.position;
                        }
                        else if (UnityEngine.AI.NavMesh.SamplePosition(exitTransform.position, out navHit, 2.0f, UnityEngine.AI.NavMesh.AllAreas))
                        {
                            targetPos = navHit.position;
                        }
                        else
                        {
                            targetPos = exitTransform.position;
                        }

                        _navMeshAgent.Warp(targetPos);
                        _navMeshAgent.ResetPath();
                        if (_targetPosition != Vector3.zero)
                        {
                            _navMeshAgent.SetDestination(_targetPosition);
                        }
                        resolvedByPortal = true;
                        break;
                    }
                }
            }

            // 如果不在傳送門附近，嘗試原地重算路徑 (General Unstuck)
            if (!resolvedByPortal)
            {
                Debug.Log($"[Agent {agentName}] General stuck. Resetting path.");
                _navMeshAgent.ResetPath();
                if (_targetPosition != Vector3.zero)
                {
                    // 稍微偏移目標點，強迫重新計算路徑
                    _navMeshAgent.SetDestination(_targetPosition);
                }
            }

            _stuckTimer = 0f;
        }
    }

    // [User Request] Force teleport to target (for Emergency Button)
    public void ForceTeleportToCurrentTarget()
    {
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled && _targetPosition != Vector3.zero)
        {
            Debug.Log($"[Agent {agentName}] Force teleporting to target: {_targetPosition}");
            _navMeshAgent.Warp(_targetPosition);
            _navMeshAgent.ResetPath();
            _navMeshAgent.SetDestination(_targetPosition);
        }
    }

    void LateUpdate()
    {
        if (_isInitialized && gameObject.activeSelf && nameTextUGUI != null && _mainCamera != null)
        {
            UpdateNameplatePosition();
        }
    }
/// <summary>
    /// 使用 NavMeshAgent 移動至目標，過遠則傳送
    /// </summary>
    public void MoveTo(Vector3 targetPosition, bool isTeleport = false)
    {
        if (_navMeshAgent == null || !gameObject.activeSelf) return;

        // 確保 Agent 在 NavMesh 上
        if (!_navMeshAgent.isOnNavMesh)
        {
            _navMeshAgent.Warp(transform.position);
        }

        float distance = Vector3.Distance(transform.position, targetPosition);

        if (isTeleport || distance > TELEPORT_THRESHOLD)
        {
            _navMeshAgent.Warp(targetPosition);
            _navMeshAgent.isStopped = true;
            _isMoving = false;
            // 傳送後重置動畫
            transform.localScale = _originalScale; 
        }
        else
        {
            _navMeshAgent.SetDestination(targetPosition);
            _navMeshAgent.isStopped = false;
            _isMoving = true;
            // [關鍵] 為了讓狀態機知道它正在移動
            SetBehaviourState(AgentBehaviourState.Moving);
        }
    }

    // 檢查是否到達目的地
    private void CheckMovementStatus()
    {
        if (_isMoving)
        {
            // 檢查路徑是否計算完成且剩餘距離小於停止距離
            if (!_navMeshAgent.pathPending && _navMeshAgent.remainingDistance <= _navMeshAgent.stoppingDistance)
            {
                if (!_navMeshAgent.hasPath || _navMeshAgent.velocity.sqrMagnitude == 0f)
                {
                    _isMoving = false;
                    _navMeshAgent.isStopped = true;
                    // 到達後切回 Idle
                    SetBehaviourState(AgentBehaviourState.Idle);
                }
            }
        }
    }

    private void HandleFacingDirection()
    {
        if (Mathf.Abs(_smoothedVelocity.x) > 0.1f)
        {
            float targetScaleX = Mathf.Sign(_smoothedVelocity.x) * Mathf.Abs(_originalScale.x);
            // 保持 y, z 不變，只翻轉 x
            Vector3 currentScale = transform.localScale;
            if (Mathf.Abs(currentScale.x - targetScaleX) > 0.01f)
            {
                transform.localScale = new Vector3(targetScaleX, currentScale.y, currentScale.z);
            }
        }
    }

    private void HandleProceduralAnimation()
    {
        // 只有在真正移動且速度足夠時才播放走路動畫
        if (_isMoving && _navMeshAgent.velocity.sqrMagnitude > 0.1f)
        {
            // 走路：快速擠壓與彈跳 (Squash & Stretch)
            _wobbleTime += Time.deltaTime * 15f;
            float squash = Mathf.Abs(Mathf.Sin(_wobbleTime)) * 0.15f; 
            
            // y 軸變長 (彈跳)，x 軸變窄 (擠壓)，保持體積感
            float currentSignX = Mathf.Sign(transform.localScale.x);
            transform.localScale = new Vector3(
                currentSignX * (Mathf.Abs(_originalScale.x) - squash * 0.5f), 
                _originalScale.y + squash, 
                _originalScale.z
            );
        }
        else
        {
            // 閒置：緩慢呼吸 (Breathing)
            float breath = Mathf.Sin(Time.time * 2f) * 0.05f;
            float currentSignX = Mathf.Sign(transform.localScale.x);
            
            transform.localScale = new Vector3(
                currentSignX * Mathf.Abs(_originalScale.x), 
                _originalScale.y + breath, 
                _originalScale.z
            );
        }
    }

    private void ResolveOverlap()
    {
        if (_navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled) return;

        // 尋找附近的代理人
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, 0.5f);
        Vector3 separation = Vector3.zero;
        int count = 0;

        foreach (var hit in hits)
        {
            if (hit.gameObject == gameObject) continue;
            
            // 只避開其他 Agent
            if (hit.GetComponent<AgentController>() != null)
            {
                Vector3 direction = transform.position - hit.transform.position;
                float distance = direction.magnitude;
                
                // 如果重疊非常嚴重 (距離接近 0)，給一個隨機方向
                if (distance < 0.01f)
                {
                    direction = UnityEngine.Random.insideUnitCircle.normalized;
                    distance = 0.01f;
                }

                // 距離越近，推力越大
                separation += direction.normalized / distance;
                count++;
            }
        }

        if (count > 0)
        {
            // 施加推力
            _navMeshAgent.Move(separation * Time.deltaTime * 1.0f);
        }
    }

    private void UpdateNameplatePosition()
    {
        if (nameTextUGUI == null || _mainCamera == null) return;
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

        // [優化 1] 縮小代理人半徑以解決擁擠問題 (原本 0.5 → 0.25)
        // 這允許代理人在樓梯等狹窄空間中更緊密地排列
        _navMeshAgent.radius = 0.25f;

        // [優化 2] 啟用高品質避障 (HighQualityObstacleAvoidance)
        // 比 NoObstacleAvoidance 更好，在擁擠時仍能避讓
        _navMeshAgent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;

        // [優化 3] 設定較高的避障優先級 (數字越小優先級越高)
        // 移動中的代理人會「推開」靜止的代理人
        _navMeshAgent.avoidancePriority = 50; // 預設值，會在 Update 中動態調整

        if (_navMeshAgent.enabled)
        {
            // [關鍵修復] 加入安全檢查
            if (_navMeshAgent.isOnNavMesh)
            {
                _navMeshAgent.ResetPath();
            }
            else
            {
                // 嘗試把自己拉回 NavMesh
                if (UnityEngine.AI.NavMesh.SamplePosition(_transform.position, out UnityEngine.AI.NavMeshHit hit, 2.0f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    _navMeshAgent.Warp(hit.position);
                }
            }
        }

        // 自動添加 FailSafe 系統
        if (GetComponent<AgentFailSafeSystem>() == null)
        {
            gameObject.AddComponent<AgentFailSafeSystem>();
        }
    }

    /// <summary>
    /// 動態更新避障優先級
    /// 移動中的代理人優先級高 (10)，靜止的代理人優先級低 (80)
    /// </summary>
    private void UpdateDynamicAvoidancePriority()
    {
        if (_navMeshAgent == null || !_navMeshAgent.isActiveAndEnabled) return;

        // 根據速度動態調整優先級
        if (_navMeshAgent.velocity.sqrMagnitude > 0.1f)
        {
            // 正在移動 → 高優先級，可以「推開」別人
            _navMeshAgent.avoidancePriority = 10;
        }
        else
        {
            // 靜止或緩慢 → 低優先級，會被別人推開
            _navMeshAgent.avoidancePriority = 80;
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
private void UpdateNameColor()
    {
        if (nameTextUGUI == null) return;
        bool isActive = !string.IsNullOrEmpty(_currentAction) && !_currentAction.ToLower().Contains("idle");
        nameTextUGUI.color = isActive ? _activeNameColor : _idleNameColor;
        nameTextUGUI.text = $"{agentName} [{_currentAction}]";
    }
    public void TeleportTo(Vector3 position, params string[] locationAliases)
    {
        TeleportTo(position, false, locationAliases);
    }

    public void TeleportTo(Vector3 position, bool suppressEffects, params string[] locationAliases)
    {
        // [修復] 先確保代理人在 NavMesh 上，再執行傳送
        if (_navMeshAgent != null)
        {
            // 先嘗試找到有效的 NavMesh 位置
            Vector3 validPosition = position;
            if (UnityEngine.AI.NavMesh.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5.0f, UnityEngine.AI.NavMesh.AllAreas))
            {
                validPosition = hit.position;
            }

            // 確保代理人啟用
            if (!_navMeshAgent.enabled)
            {
                _navMeshAgent.enabled = true;
            }

            // 使用 Warp 將代理人移動到有效位置 (這會自動處理 NavMesh 放置)
            _navMeshAgent.Warp(validPosition);

            // 現在代理人應該在 NavMesh 上了，可以安全地呼叫 ResetPath
            if (_navMeshAgent.isActiveAndEnabled && _navMeshAgent.isOnNavMesh)
            {
                _navMeshAgent.ResetPath();
            }

            _navMeshDriving = false;
            position = validPosition; // 使用校正後的位置
        }

        _transform.position = position;
        _targetPosition = position;
        
        _movementController?.HandleTeleport(position);
        ResetNavMeshPosition(position);
        SetManualLocationOverrides(locationAliases);
        _lastInstructionDestination = null;
        NotifyMovementCompleted();
        OnTeleported(false, suppressEffects);
    }

    public void OnTeleported(bool usedDoor, bool suppressEffects = false)
    {
        Stun(0.5f);
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

        // 1. 確保 Agent 是開啟的，否則操作無效
        if (!_navMeshAgent.gameObject.activeSelf) 
        {
            _transform.position = position;
            return;
        }

        if (!_navMeshAgent.enabled) _navMeshAgent.enabled = true;

        // 2. [關鍵修復] 不要直接 Warp 到目標座標，而是找 "最近的 NavMesh 地面"
        // 參數 5.0f 是搜尋半徑，NavMesh.AllAreas 表示搜尋所有圖層
        if (UnityEngine.AI.NavMesh.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5.0f, UnityEngine.AI.NavMesh.AllAreas))
        {
            // 找到了合法位置，傳送過去
            _navMeshAgent.Warp(hit.position);
            _navMeshAgent.nextPosition = hit.position;
            _navMeshAgent.velocity = Vector3.zero;

            // 3. 只有在真正位於 NavMesh 上時才重置路徑
            if (_navMeshAgent.isOnNavMesh)
            {
                _navMeshAgent.ResetPath();
            }
        }
        else
        {
            // 如果找不到 NavMesh (例如傳送到虛空)，強制設定 Transform 防止報錯，但無法導航
            Debug.LogWarning($"[Agent {agentName}] Teleport target {position} is not on NavMesh!");
            _navMeshAgent.enabled = false; // 先關閉避免報錯
            _transform.position = position;
            _navMeshAgent.enabled = true;  // 再開啟嘗試恢復
        }
    }
// --- 供 SimulationClient 查詢狀態 ---
    public bool IsMovementComplete()
    {
        // 如果正在 NavMesh 移動 (_isMoving) 或者還在指令佇列中等待 (_navMeshAgent.hasPath)
        return !_isMoving && (!_navMeshAgent.hasPath || _navMeshAgent.velocity.sqrMagnitude < 0.1f);
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
                // [關鍵修復] 只有在 NavMesh 上且啟動時才 ResetPath
                if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled && _navMeshAgent.isOnNavMesh)
                {
                    _navMeshAgent.ResetPath();
                }
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


    // [New] System 1 Fast Reaction Handler
    public void OnFastReactionReceived(string jsonString)
    {
        try
        {
            ReactionData data = JsonUtility.FromJson<ReactionData>(jsonString);
            
            // 立即中斷當前行為 (System 1 優先權最高)
            if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
            {
                _navMeshAgent.isStopped = true;
                _navMeshAgent.ResetPath();
            }
            StopAllCoroutines();
            if (_cts != null) _cts.Cancel();
            _cts = new CancellationTokenSource();
            ProcessCommandBufferLoop(_cts.Token).Forget();

            // 直接觸發動畫與行為
            if (_animator != null && !string.IsNullOrEmpty(data.anim))
            {
                _animator.SetTrigger(data.anim);
            }
            else if (_visualController != null && !string.IsNullOrEmpty(data.anim))
            {
                // Fallback to visual controller if animator is not directly accessible or data.anim is an emote name
                _visualController.PlayEmote(data.anim);
            }
            
            if (data.action == "RUN" && !string.IsNullOrEmpty(data.target))
            {
                if (TryFindLocationTransform(data.target, out Transform targetTrans) && targetTrans != null)
                {
                    if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
                    {
                        _navMeshAgent.isStopped = false;
                        _navMeshAgent.speed = 6.0f; // 恐慌速度
                        _navMeshAgent.SetDestination(targetTrans.position);
                        _navMeshDriving = true;
                        SetBehaviourState(AgentBehaviourState.Moving);
                    }
                }
                else
                {
                    Debug.LogWarning($"[Agent {agentName}] FastReaction RUN target '{data.target}' not found.");
                }
            }
            // Add other actions if needed
        }
        catch (Exception e)
        {
            Debug.LogError($"[Agent {agentName}] OnFastReactionReceived Error: {e}");
        }
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

    // --- Disaster Response FSM ---
    public enum AgentDisasterState { Normal, Thinking, Acting, Panic, Recovery }
    private AgentDisasterState _currentState = AgentDisasterState.Normal;
    private bool _isWaitingForBrain = false;

    public void HandleEarthquakeStart(float intensity)
    {
        // 強制中斷所有 Coroutine (System 1 Interrupt)
        StopAllCoroutines();
        if (_cts != null) _cts.Cancel();
        _cts = new CancellationTokenSource();
        ProcessCommandBufferLoop(_cts.Token).Forget(); // Restart loop

        // 物理凍結
        if (_navMeshAgent != null && _navMeshAgent.isActiveAndEnabled)
        {
            _navMeshAgent.isStopped = true;
            _navMeshAgent.velocity = Vector3.zero;
        }

        // 進入 Panic 狀態
        _currentState = AgentDisasterState.Panic;
        _visualController?.PlayEmote("Panic"); // 假設有 Panic 動畫或是氣泡

        // 立即向 Python 發送高優先級感官數據 (模擬)
        // 實際發送邏輯由 SimulationClient 處理，這裡我們只更新狀態
        _isWaitingForBrain = true;
        Debug.Log($"[Agent {agentName}] EARTHQUAKE START! Panic Mode.");
    }

    public void HandleEarthquakeEnd()
    {
        _currentState = AgentDisasterState.Recovery;
        Debug.Log($"[Agent {agentName}] Earthquake Ended. Entering Recovery Mode.");
        // 通知大腦震動結束，切換回 System 2
        // _simulationClient?.SendEvent(agentName, "EARTHQUAKE_END");
    }

    public void ReceiveCommand(string jsonPayload)
    {
        _isWaitingForBrain = false;

        if (jsonPayload.Contains("REFLEX_ACTION"))
        {
            if (jsonPayload.Contains("DUCK"))
            {
                StartCoroutine(ExecuteReflex("DUCK"));
            }
        }
        else if (jsonPayload.Contains("NAVIGATE"))
        {
            // System 2: Extract target from JSON (Simplified)
            string target = ExtractValueFromJson(jsonPayload, "target");
            if (!string.IsNullOrEmpty(target))
            {
                StartCoroutine(ExecuteNavigation(target));
            }
        }
    }

    private string ExtractValueFromJson(string json, string key)
    {
        string keyPattern = $"\"{key}\": \"";
        int startIndex = json.IndexOf(keyPattern);
        if (startIndex == -1) return null;
        startIndex += keyPattern.Length;
        int endIndex = json.IndexOf("\"", startIndex);
        if (endIndex == -1) return null;
        return json.Substring(startIndex, endIndex - startIndex);
    }

    private System.Collections.IEnumerator ExecuteNavigation(string targetName)
    {
        _currentState = AgentDisasterState.Acting;
        Debug.Log($"[Agent {agentName}] System 2 Navigation to {targetName}");
        
        // Reuse existing Move command logic
        // We need to find the transform for the targetName. 
        // Assuming SimulationClient or a global manager has this info, but here we might need to search.
        GameObject targetObj = GameObject.Find(targetName);
        if (targetObj != null)
        {
            AgentCommand cmd = new AgentCommand
            {
                Type = AgentInternalCommandType.Move,
                TargetLocationName = targetName,
                TargetTransform = targetObj.transform,
                TargetPosition = targetObj.transform.position,
                ActionName = "Walk"
            };
            _commandQueue.Enqueue(cmd);
        }
        else
        {
            Debug.LogWarning($"[Agent {agentName}] Could not find target object: {targetName}");
        }

        yield return null;
        _currentState = AgentDisasterState.Normal; // Return to normal after initiating navigation
    }

    private System.Collections.IEnumerator ExecuteReflex(string action)
    {
        _currentState = AgentDisasterState.Acting;
        if (action == "DUCK")
        {
            _visualController?.PlayEmote("Duck"); // 假設有 Duck 動畫
            yield return new WaitForSeconds(3.0f); // 保持姿勢 3 秒
        }
        
        _currentState = AgentDisasterState.Thinking;
        // Reflex 完成後，主動請求下一步指示
        // _simulationClient?.ReportActionComplete(agentName);
    }

    // --- End Disaster Response FSM ---

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
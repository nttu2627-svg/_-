// Scripts/Agent/AgentFailSafeSystem.cs
// 保底機制系統：確定性區域檢測 + 卡死偵測 + 強制傳送

using UnityEngine;
using UnityEngine.AI;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 保底機制系統 (Fail-Safe System)
/// 解決問題 1 (Apartment_F2 判定失敗) 和問題 5 (保底機制)
/// 
/// 核心功能：
/// 1. 確定性區域檢測 - 使用 Bounds.Contains 替代 OnTriggerEnter
/// 2. 位移監控 - 偵測 position 變化 < 50 的卡死狀態
/// 3. 強制傳送 - 當卡死且位置與目標不一致時觸發 Warp
/// </summary>
public class AgentFailSafeSystem : MonoBehaviour
{
    [Header("保底機制設定")]
    [Tooltip("檢測間隔 (秒)")]
    public float checkInterval = 2.0f;
    
    [Tooltip("位移閾值 - 在檢測間隔內位移小於此值視為可能卡死")]
    public float movementThreshold = 50f;
    
    [Tooltip("連續幾次低位移才觸發強制傳送")]
    public int maxStuckRetries = 3;
    
    [Tooltip("距離目標多遠才需要考慮傳送")]
    public float minDistanceToTeleport = 1.0f;

    [Header("區域檢測設定")]
    [Tooltip("區域檢測頻率 (秒)")]
    public float zoneCheckInterval = 0.2f;

    [Header("群聚偵測設定")]
    [Tooltip("群聚偵測頻率 (秒)")]
    public float crowdingCheckInterval = 1.5f;
    
    [Tooltip("偵測週圍代理人的半徑")]
    public float crowdingRadius = 2.0f;
    
    [Tooltip("周圍代理人數量超過此值視為群聚")]
    public int crowdingThreshold = 2;
    
    [Tooltip("群聚狀態持續多久（秒）才觸發傳送")]
    public float crowdingDuration = 4.0f;

    // 私有變數
    private NavMeshAgent _navAgent;
    private AgentController _agentController;
    private Vector3 _lastCheckPosition;
    private int _stuckCount = 0;
    private string _currentZone = "";
    private float _crowdingStartTime = -1f; // 群聚開始時間
    
    // 區域 Collider 快取
    private static Dictionary<string, Collider2D> _zoneBoundsCache = new Dictionary<string, Collider2D>();
    private static bool _zoneCacheInitialized = false;

    /// <summary>
    /// 當前偵測到的區域名稱 (標準化 ID)
    /// </summary>
    public string CurrentDetectedZone => _currentZone;

    /// <summary>
    /// 是否處於卡死狀態
    /// </summary>
    public bool IsStuck => _stuckCount >= maxStuckRetries;

    void Awake()
    {
        _navAgent = GetComponent<NavMeshAgent>();
        _agentController = GetComponent<AgentController>();
    }

    void Start()
    {
        if (_navAgent == null)
        {
            Debug.LogWarning($"[FailSafe] {gameObject.name} 缺少 NavMeshAgent，保底系統將無法正常運作。");
            enabled = false;
            return;
        }

        _lastCheckPosition = transform.position;

        // 初始化區域快取 (只做一次)
        if (!_zoneCacheInitialized)
        {
            InitializeZoneBoundsCache();
        }

        // 啟動協程
        StartCoroutine(StuckCheckRoutine());
        StartCoroutine(ZoneDetectionRoutine());
        StartCoroutine(CrowdingDetectionRoutine()); // 新增：群聚偵測
    }

    /// <summary>
    /// 初始化區域邊界快取
    /// 搜尋場景中所有帶有 "Bounds" 或 "_Bound" 標籤的 Collider2D
    /// </summary>
    private static void InitializeZoneBoundsCache()
    {
        _zoneBoundsCache.Clear();

        // 尋找場景中的 LocationMarkers 或 Bounds 物件
        GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        
        foreach (var obj in allObjects)
        {
            string name = obj.name;
            
            // 檢測常見的區域邊界命名模式
            if (name.Contains("Bounds") || name.Contains("_Bound") || 
                name.Contains("公寓") || name.Contains("Apartment") ||
                name.Contains("學校") || name.Contains("School") ||
                name.Contains("地鐵") || name.Contains("Subway") ||
                name.Contains("餐廳") || name.Contains("Rest") ||
                name.Contains("健身房") || name.Contains("Gym") ||
                name.Contains("超市") || name.Contains("Super"))
            {
                Collider2D col = obj.GetComponent<Collider2D>();
                if (col != null && col.isTrigger)
                {
                    string normalizedKey = NormalizeZoneKey(name);
                    if (!_zoneBoundsCache.ContainsKey(normalizedKey))
                    {
                        _zoneBoundsCache[normalizedKey] = col;
                    }
                }
            }
        }

        _zoneCacheInitialized = true;
        Debug.Log($"[FailSafe] 區域快取初始化完成，共 {_zoneBoundsCache.Count} 個區域。");
    }

    /// <summary>
    /// 正規化區域名稱為標準 ID
    /// </summary>
    private static string NormalizeZoneKey(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "";

        string lower = rawName.ToLower();
        
        // 公寓二樓
        if (lower.Contains("f2") || lower.Contains("二樓") || lower.Contains("floor2"))
            return "Apartment_F2";
        
        // 公寓一樓
        if (lower.Contains("apartment") || lower.Contains("公寓") || 
            lower.Contains("f1") || lower.Contains("一樓"))
            return "Apartment_F1";
        
        if (lower.Contains("school") || lower.Contains("學校"))
            return "School";
        
        if (lower.Contains("rest") || lower.Contains("餐廳") || lower.Contains("cafe"))
            return "Rest";
        
        if (lower.Contains("gym") || lower.Contains("健身房"))
            return "Gym";
        
        if (lower.Contains("super") || lower.Contains("超市"))
            return "Super";
        
        if (lower.Contains("subway") || lower.Contains("地鐵"))
            return "Subway";
        
        if (lower.Contains("exterior") || lower.Contains("室外") || lower.Contains("戶外"))
            return "Exterior";

        return rawName;
    }

    /// <summary>
    /// 卡死檢測協程
    /// 每隔 checkInterval 秒檢查位移量
    /// </summary>
    IEnumerator StuckCheckRoutine()
    {
        WaitForSeconds wait = new WaitForSeconds(checkInterval);

        while (true)
        {
            yield return wait;

            // 跳過條件：未初始化、非活動
            if (_navAgent == null || !_navAgent.isActiveAndEnabled) continue;
            
            // [修復] 不再限制必須有路徑才檢測
            // 因為代理人卡在傳送門前時，路徑可能已到達終點但傳送門未觸發

            // 計算這段時間內的位移
            float distanceMoved = Vector3.Distance(transform.position, _lastCheckPosition);
            
            // [修復] 優先使用 AgentController 的目標位置（更準確）
            Vector3 targetPos;
            if (_agentController != null)
            {
                // 使用反射或公開屬性取得目標位置
                targetPos = _agentController.transform.position; // 先獲取自身位置
                
                // 嘗試從 NavMeshAgent 取得目標
                if (_navAgent.hasPath || _navAgent.pathPending)
                {
                    targetPos = _navAgent.destination;
                }
                else if (_navAgent.destination != Vector3.zero)
                {
                    targetPos = _navAgent.destination;
                }
            }
            else
            {
                targetPos = _navAgent.destination;
            }
            
            float distanceToTarget = Vector3.Distance(transform.position, targetPos);
            
            // 關鍵判定邏輯：
            // 1. 位移 < 閾值 (可能卡住)
            // 2. 且距離目標 > minDistanceToTeleport (尚未到達)
            bool isLowMovement = distanceMoved < movementThreshold;
            bool hasNotArrived = distanceToTarget > minDistanceToTeleport;

            if (isLowMovement && hasNotArrived)
            {
                _stuckCount++;
                Debug.LogWarning($"[FailSafe] {gameObject.name} 可能卡死 " +
                    $"(位移 {distanceMoved:F2} < {movementThreshold}, " +
                    $"距目標 {distanceToTarget:F2})。計數: {_stuckCount}/{maxStuckRetries}");

                if (_stuckCount >= maxStuckRetries)
                {
                    PerformRescueTeleport(targetPos);
                    _stuckCount = 0;
                }
            }
            else
            {
                // 正常移動，重置計數
                _stuckCount = 0;
            }

            _lastCheckPosition = transform.position;
        }
    }

    /// <summary>
    /// 區域檢測協程
    /// 使用 Bounds.Contains 進行確定性檢測，不依賴 OnTriggerEnter
    /// </summary>
    IEnumerator ZoneDetectionRoutine()
    {
        WaitForSeconds wait = new WaitForSeconds(zoneCheckInterval);

        while (true)
        {
            yield return wait;

            Vector2 currentPos2D = new Vector2(transform.position.x, transform.position.y);
            string detectedZone = "";

            // 遍歷所有快取的區域，檢測是否在範圍內
            foreach (var kvp in _zoneBoundsCache)
            {
                if (kvp.Value == null) continue;

                // 使用 Bounds.Contains 進行純數學計算
                // 這是解決 "判定失敗" 最穩健的方法
                if (kvp.Value.bounds.Contains(currentPos2D))
                {
                    detectedZone = kvp.Key;
                    break; // 找到第一個匹配的區域就停止
                }
            }

            // 更新當前區域
            if (!string.IsNullOrEmpty(detectedZone) && detectedZone != _currentZone)
            {
                string previousZone = _currentZone;
                _currentZone = detectedZone;
                
                Debug.Log($"[FailSafe] {gameObject.name} 區域變更: {previousZone} → {_currentZone}");
                
                // 可選：發送區域變更事件
                OnZoneChanged?.Invoke(this, previousZone, _currentZone);
            }
        }
    }

    /// <summary>
    /// 執行救援傳送
    /// 使用 NavMeshAgent.Warp 確保與 NavMesh 正確同步
    /// </summary>
    private void PerformRescueTeleport(Vector3 targetPosition)
    {
        if (_navAgent == null || !_navAgent.isActiveAndEnabled) return;

        // 嘗試在目標位置附近找到有效的 NavMesh 點
        Vector3 safeTeleportPosition = targetPosition;
        
        if (NavMesh.SamplePosition(targetPosition, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
        {
            safeTeleportPosition = hit.position;
        }

        Debug.LogWarning($"[FailSafe] 強制傳送 {gameObject.name} 到 {safeTeleportPosition}");

        // 關鍵：先停止 Agent，再 Warp，再恢復
        _navAgent.isStopped = true;
        
        // [修復] 只有在代理人已經在 NavMesh 上時才呼叫 ResetPath
        if (_navAgent.isOnNavMesh)
        {
            _navAgent.ResetPath();
        }
        
        _navAgent.Warp(safeTeleportPosition);
        _navAgent.isStopped = false;

        // 重新設定路徑 (如果有目標且在 NavMesh 上)
        if (targetPosition != Vector3.zero && _navAgent.isOnNavMesh)
        {
            _navAgent.SetDestination(targetPosition);
        }

        // 通知 AgentController 已傳送
        _agentController?.OnTeleported(false, false);

        // 重置位置記錄
        _lastCheckPosition = safeTeleportPosition;
    }

    /// <summary>
    /// 手動觸發強制傳送到指定位置
    /// </summary>
    public void ForceTeleportTo(Vector3 position)
    {
        PerformRescueTeleport(position);
    }

    /// <summary>
    /// 取得當前區域的標準化 ID
    /// </summary>
    public string GetStandardizedZoneId()
    {
        return _currentZone;
    }

    /// <summary>
    /// 重新初始化區域快取 (場景切換時呼叫)
    /// </summary>
    public static void RefreshZoneCache()
    {
        _zoneCacheInitialized = false;
        InitializeZoneBoundsCache();
    }

    // 區域變更事件
    public delegate void ZoneChangedHandler(AgentFailSafeSystem agent, string fromZone, string toZone);
    public static event ZoneChangedHandler OnZoneChanged;

    void OnDestroy()
    {
        StopAllCoroutines();
    }

    // ====== 群聚偵測保底機制 ======

    /// <summary>
    /// 群聚偵測協程
    /// 當代理人擠在一起且當前區域與目標區域不匹配時，自動傳送至目標位置
    /// </summary>
    IEnumerator CrowdingDetectionRoutine()
    {
        WaitForSeconds wait = new WaitForSeconds(crowdingCheckInterval);

        while (true)
        {
            yield return wait;

            // 跳過條件：未初始化、非活動
            if (_navAgent == null || !_navAgent.isActiveAndEnabled) continue;
            if (_agentController == null) continue;

            // 取得目標位置
            Vector3 targetPos = _navAgent.destination;
            if (targetPos == Vector3.zero) continue;

            // 計算與目標的距離
            float distanceToTarget = Vector3.Distance(transform.position, targetPos);
            if (distanceToTarget <= minDistanceToTeleport) 
            {
                // 已經到達目標，重置群聚計時器
                _crowdingStartTime = -1f;
                continue;
            }

            // 偵測周圍的代理人數量
            int nearbyAgentCount = CountNearbyAgents();

            // 判斷是否處於群聚狀態
            bool isCrowded = nearbyAgentCount >= crowdingThreshold;

            // 取得當前區域和目標區域
            string currentZone = _currentZone;
            string targetZone = GetTargetZoneName(targetPos);

            // 區域不匹配檢查 (當前區域與目標區域不同)
            bool zoneMismatch = !string.IsNullOrEmpty(currentZone) && 
                               !string.IsNullOrEmpty(targetZone) && 
                               currentZone != targetZone;

            if (isCrowded && (zoneMismatch || distanceToTarget > 5.0f))
            {
                // 開始或繼續群聚計時
                if (_crowdingStartTime < 0)
                {
                    _crowdingStartTime = Time.time;
                    Debug.Log($"[FailSafe] {gameObject.name} 偵測到群聚 (周圍 {nearbyAgentCount} 人)" +
                        $"，當前區域: {currentZone}，目標區域: {targetZone}，開始計時...");
                }
                else
                {
                    float elapsed = Time.time - _crowdingStartTime;
                    if (elapsed >= crowdingDuration)
                    {
                        Debug.LogWarning($"[FailSafe] {gameObject.name} 群聚狀態超過 {crowdingDuration} 秒" +
                            $"，區域不匹配 ({currentZone} → {targetZone})，觸發傳送！");
                        
                        PerformRescueTeleport(targetPos);
                        _crowdingStartTime = -1f;
                    }
                }
            }
            else
            {
                // 不再群聚或已到達，重置計時器
                if (_crowdingStartTime >= 0)
                {
                    _crowdingStartTime = -1f;
                }
            }
        }
    }

    /// <summary>
    /// 計算周圍代理人數量
    /// </summary>
    private int CountNearbyAgents()
    {
        int count = 0;
        
        // 使用 Physics2D.OverlapCircle 偵測周圍的代理人
        Collider2D[] colliders = Physics2D.OverlapCircleAll(
            new Vector2(transform.position.x, transform.position.y), 
            crowdingRadius
        );

        foreach (var col in colliders)
        {
            if (col.gameObject == gameObject) continue; // 排除自己
            
            // 檢查是否為代理人
            if (col.GetComponent<AgentController>() != null || 
                col.GetComponent<NavMeshAgent>() != null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 根據目標位置取得目標區域名稱
    /// </summary>
    private string GetTargetZoneName(Vector3 targetPosition)
    {
        Vector2 targetPos2D = new Vector2(targetPosition.x, targetPosition.y);

        foreach (var kvp in _zoneBoundsCache)
        {
            if (kvp.Value == null) continue;

            if (kvp.Value.bounds.Contains(targetPos2D))
            {
                return kvp.Key;
            }
        }

        return "";
    }

    /// <summary>
    /// 取得當前群聚偵測狀態 (用於除錯)
    /// </summary>
    public bool IsCrowded => _crowdingStartTime >= 0;
}

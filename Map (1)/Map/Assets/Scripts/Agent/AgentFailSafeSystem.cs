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

    // 私有變數
    private NavMeshAgent _navAgent;
    private AgentController _agentController;
    private Vector3 _lastCheckPosition;
    private int _stuckCount = 0;
    private string _currentZone = "";
    
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

            // 跳過條件：未初始化、非活動、無路徑
            if (_navAgent == null || !_navAgent.isActiveAndEnabled) continue;
            if (!_navAgent.hasPath && !_navAgent.pathPending) continue;

            // 計算這段時間內的位移
            float distanceMoved = Vector3.Distance(transform.position, _lastCheckPosition);
            
            // 取得當前目標位置
            Vector3 targetPos = _navAgent.destination;
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

        Debug.LogError($"[FailSafe] 強制傳送 {gameObject.name} 到 {safeTeleportPosition}");

        // 關鍵：先停止 Agent，再 Warp，再恢復
        _navAgent.isStopped = true;
        _navAgent.ResetPath();
        _navAgent.Warp(safeTeleportPosition);
        _navAgent.isStopped = false;

        // 重新設定路徑 (如果有目標)
        if (targetPosition != Vector3.zero)
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
}

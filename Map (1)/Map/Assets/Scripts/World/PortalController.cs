// Scripts/World/PortalController.cs
using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class PortalController : MonoBehaviour
{
    [Header("配對方式（二選一，優先使用 targetPortal）")]
    public PortalController targetPortal;          // 直接拖引用（推薦）
    [Tooltip("若未指定 targetPortal，會用這些名字在場景中尋找（Awake 僅解析一次）")]
    public string[] targetPortalNames;

    public string targetPortalName
    {
        get
        {
            if (_resolvedTarget != null) return _resolvedTarget.name;
            if (targetPortal != null) return targetPortal.name;
            if (targetPortalNames != null && targetPortalNames.Length > 0) return targetPortalNames[0];
            return string.Empty;
        }
    }

    [Header("出口與行為")]
    public Transform exitPoint;                    // 出口點（若為空會在 Reset 自動建立子物件）
    public LayerMask allowedLayers = ~0;           // 允許被傳送的層
    public float reenterCooldown = 0.15f;          // 傳送後忽略觸發秒數（防回彈/抖動）
    public float exitNudge = 0.08f;                // 出口沿 exitPoint.right 推開的距離
    public bool preserveMomentum = true;           // 是否保留速度
    public bool rotateMomentumWithPortal = true;   // 依門的角度差旋轉速度向量
    public bool matchExitRotation = false;         // 把物件 Z 旋轉對齊出口角度差
    public bool keepLocalOffset = false;           // 依「進門的相對位置」映射到出口

    [Header("門類型設定")]
    [Tooltip("若此傳送點代表室內外的門，將使用較短的冷卻時間並加強出口安全檢查。")]
    public bool isDoor = false;
    [Tooltip("門類型的自動冷卻時間（秒）")]
    public float doorReenterCooldown = 0.08f;
    [Tooltip("非門類型的自動冷卻時間（秒）")]
    public float portalReenterCooldown = 0.18f;

    [Header("出口安全偵測")]
    [Tooltip("傳送後需要保留的最小空間半徑，用來避免生成在牆壁或其他角色上。")]
    public float exitClearRadius = 0.25f;
    [Tooltip("出口安全偵測時檢查的圖層。")]
    public LayerMask exitObstructionLayers = ~0;
    [Tooltip("若出口被阻擋，沿著出口方向搜尋的最大迭代次數。")]
    public int maxExitAdjustmentIterations = 6;
    [Tooltip("每次調整時沿出口方向或鄰近方向偏移的距離。")]
    public float exitAdjustmentStep = 0.18f;

    [SerializeField, Tooltip("可保留為 -1 交由程式產生")]
    private int portalId = -1;

    // 內部：實際使用的配對門
    private PortalController _resolvedTarget;
    private static readonly Collider2D[] ExitProbeBuffer = new Collider2D[16];
    private static readonly Vector2[] ExitSearchDirections = new Vector2[]
    {
        Vector2.zero,
        Vector2.right,
        Vector2.left,
        Vector2.up,
        Vector2.down,
        new Vector2(1f, 1f).normalized,
        new Vector2(-1f, 1f).normalized,
        new Vector2(1f, -1f).normalized,
        new Vector2(-1f, -1f).normalized
    };

    void Reset()
    {
        var col = GetComponent<Collider2D>();
        col.isTrigger = true;

        if (exitPoint == null)
        {
            var t = new GameObject("ExitPoint").transform;
            t.SetParent(transform);
            t.localPosition = Vector3.right * 0.5f;
            t.localEulerAngles = Vector3.zero;
            exitPoint = t;
        }
    }

    void Awake()
    {
        ConfigureCooldown();
        if (portalId == -1) portalId = GetInstanceID();

        // 1) 直接引用優先
        if (targetPortal != null)
        {
            _resolvedTarget = targetPortal;
            return;
        }

        // 2) 以名稱尋找（只做一次）
        if (targetPortalNames != null && targetPortalNames.Length > 0)
        {
            foreach (var n in targetPortalNames)
            {
                var go = GameObject.Find(n);
                if (go != null && go.TryGetComponent(out PortalController pc))
                {
                    _resolvedTarget = pc;
                    break;
                }
            }
        }

        if (_resolvedTarget == null)
            Debug.LogError($"[{name}] 無法解析目標傳送門，請設定 targetPortal 或有效的 targetPortalNames。");
    }

    private void ConfigureCooldown()
    {
        float desired = isDoor ? doorReenterCooldown : portalReenterCooldown;
        if (desired < 0f) desired = 0f;
        reenterCooldown = desired;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_resolvedTarget == null || exitPoint == null) return;
        if ((allowedLayers.value & (1 << other.gameObject.layer)) == 0) return;

        // 需要 Teleportable2D（用來做冷卻標記）
        var tp = other.GetComponent<Teleportable2D>();
        if (tp == null) return;

        if (tp.IsIgnoring) return;                                 // 剛傳送過
        if (tp.lastPortalId == _resolvedTarget.portalId) return;   // 避免來回

        var dst = _resolvedTarget;
        var dstExit = dst.exitPoint != null ? dst.exitPoint : dst.transform;

        // 計算落點
        var obj = other.transform;
        Vector3 newPos;
        if (keepLocalOffset)
        {
            Vector3 local = transform.InverseTransformPoint(obj.position);
            newPos = dstExit.TransformPoint(local);
        }
        else
        {
            newPos = dstExit.position;
        }

        // 沿出口面向推出一點
        newPos += dstExit.right * Mathf.Max(0f, exitNudge);

        // 確保出口安全
        newPos = FindSafeExitPosition(dstExit, newPos, other);

        // 速度/旋轉處理
        var rb = other.attachedRigidbody;
        Vector2 newVel = Vector2.zero;
        if (rb != null && preserveMomentum)
        {
            newVel = rb.linearVelocity;
            if (rotateMomentumWithPortal)
            {
                float delta = dstExit.eulerAngles.z - transform.eulerAngles.z;
                newVel = Rotate(newVel, delta);
            }
        }

        float newZ = obj.eulerAngles.z;
        if (matchExitRotation)
        {
            float delta = dstExit.eulerAngles.z - transform.eulerAngles.z;
            newZ += delta;
        }

        // 實際傳送
        obj.position = newPos;
        obj.rotation = Quaternion.Euler(0, 0, newZ);
        if (rb != null && preserveMomentum) rb.linearVelocity = newVel;

        // 通知代理人調整移動狀態
        if (other.TryGetComponent(out AgentController agent))
        {
            bool usedDoor = isDoor || (dst != null && dst.isDoor);
            agent.OnTeleported(usedDoor, false);
        }

        // 設定冷卻
        tp.SetIgnore(reenterCooldown, portalId);
        tp.SetIgnore(dst.reenterCooldown, dst.portalId);
    }

    private Vector3 FindSafeExitPosition(Transform dstExit, Vector3 desiredPosition, Collider2D movingCollider)
    {
        if (exitClearRadius <= 0f)
            return desiredPosition;

        if (!IsExitBlocked(desiredPosition, movingCollider))
            return desiredPosition;

        Vector3 bestPosition = desiredPosition;
        float bestDistance = float.MaxValue;

        for (int step = 1; step <= Mathf.Max(1, maxExitAdjustmentIterations); step++)
        {
            float distance = step * Mathf.Max(0.01f, exitAdjustmentStep);
            foreach (var dir in ExitSearchDirections)
            {
                if (dir == Vector2.zero && step > 1) continue;
                Vector3 worldDir = dstExit.TransformDirection(new Vector3(dir.x, dir.y, 0f));
                Vector3 candidate = desiredPosition + worldDir.normalized * distance;
                if (!IsExitBlocked(candidate, movingCollider))
                {
                    float sqrDistance = (candidate - desiredPosition).sqrMagnitude;
                    if (sqrDistance < bestDistance)
                    {
                        bestDistance = sqrDistance;
                        bestPosition = candidate;
                    }
                }
            }
            if (bestDistance != float.MaxValue)
                return bestPosition;
        }

        return bestPosition;
    }

    private bool IsExitBlocked(Vector3 position, Collider2D movingCollider)
    {
        var filter = new ContactFilter2D
        {
            useLayerMask = true,
            layerMask = exitObstructionLayers,
            useTriggers = false
        };

        int hits = Physics2D.OverlapCircle(position, exitClearRadius, filter, ExitProbeBuffer);
        for (int i = 0; i < hits; i++)
        {
            var col = ExitProbeBuffer[i];
            if (col == null || !col.enabled) continue;
            if (movingCollider != null)
            {
                if (col == movingCollider) continue;
                if (col.transform.IsChildOf(movingCollider.transform)) continue;
            }
            if (col.GetComponent<PortalController>() != null) continue;
            return true;
        }
        return false;
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }

    // === 舊版本兼容接口 ===

    // 讓舊代碼仍可使用 portal.TargetPortal
    public PortalController TargetPortal => targetPortal != null ? targetPortal : _resolvedTarget;

    // 提供舊版本的 GetExitPosition() 與 (Transform requester) 呼叫
    public Vector3 GetExitPosition(Transform requester = null)
    {
        return exitPoint != null ? exitPoint.position : transform.position;
    }

    // 模擬舊版 TryTeleport，支援 1 或 2 參數版本
public bool TryTeleport(Transform mover, object requester = null)
{
    if (targetPortal == null) return false;

    Vector3 exitPos = targetPortal.exitPoint != null
        ? targetPortal.exitPoint.position
        : targetPortal.transform.position;

    // 通知 AgentController（若存在）
    if (mover.TryGetComponent(out AgentController agent))
    {
        agent.OnTeleported(isDoor || (targetPortal != null && targetPortal.isDoor), false);
    }

    mover.position = exitPos;
    return true;
}

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (exitPoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(exitPoint.position, 0.05f);
        Gizmos.DrawRay(exitPoint.position, exitPoint.right * 0.3f);
    }
#endif
}

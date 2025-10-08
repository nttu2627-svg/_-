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

    [SerializeField, Tooltip("可保留為 -1 交由程式產生")]
    private int portalId = -1;

    // 內部：實際使用的配對門
    private PortalController _resolvedTarget;
    private Collider2D _collider;
    private Collider2D _cachedInteriorCollider;

    public int PortalId => portalId;
    public PortalController TargetPortal => _resolvedTarget;

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
        if (portalId == -1) portalId = GetInstanceID();
        _collider = GetComponent<Collider2D>();
        // 1) 直接引用優先
        if (targetPortal != null)
        {
            _resolvedTarget = targetPortal;
            return;
        }

        // 2) 以名稱尋找（只做一次）；若多個候選，固定挑第一個（避免每幀隨機不一致）
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

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_resolvedTarget == null || exitPoint == null) return;
        if ((allowedLayers.value & (1 << other.gameObject.layer)) == 0) return;

        var teleportable = other.GetComponent<Teleportable2D>();
        if (teleportable == null) return;

        TryTeleport(other.transform, teleportable);
    }

    public bool TryTeleport(Transform subject, Teleportable2D teleportable)
    {
        if (_resolvedTarget == null || exitPoint == null || subject == null)
        {
            return false;
        }

        if ((allowedLayers.value & (1 << subject.gameObject.layer)) == 0)
        {
            return false;
        }

        if (teleportable != null)
        {
            if (teleportable.IsIgnoring)
            {
                return false;
            }

            if (teleportable.lastPortalId == _resolvedTarget.portalId)
            {
                return false;
            }
        }

        PortalController dst = _resolvedTarget;
        Transform dstExit = dst.exitPoint != null ? dst.exitPoint : dst.transform;

        Vector3 newPos;
        if (keepLocalOffset)
        {
            Vector3 local = transform.InverseTransformPoint(subject.position);
            newPos = dstExit.TransformPoint(local);
        }
        else
        {
            newPos = dstExit.position;
        }
        newPos += dstExit.right * Mathf.Max(0f, exitNudge);

        Rigidbody2D rb = subject.GetComponent<Rigidbody2D>();
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

        float newZ = subject.eulerAngles.z;
        if (matchExitRotation)
        {
            float delta = dstExit.eulerAngles.z - transform.eulerAngles.z;
            newZ += delta;
        }

        if (teleportable != null)
        {
            Collider2D interior = dst.GetInteriorCollider();
            newPos = teleportable.ClampPositionToInterior(newPos, interior);
        }

        subject.position = newPos;
        subject.rotation = Quaternion.Euler(0, 0, newZ);

        if (rb != null)
        {
            if (preserveMomentum)
            {
                rb.linearVelocity = newVel;
            }
            else
            {
                rb.linearVelocity = Vector2.zero;
            }
        }

        teleportable?.SetIgnore(reenterCooldown, portalId);
        teleportable?.SetIgnore(dst.reenterCooldown, dst.portalId);

        return true;
    }

    public Vector3 GetExitPosition(Transform subject)
    {
        PortalController dst = _resolvedTarget != null ? _resolvedTarget : this;
        Transform dstExit = dst.exitPoint != null ? dst.exitPoint : dst.transform;

        Vector3 position;
        if (keepLocalOffset && subject != null)
        {
            Vector3 local = transform.InverseTransformPoint(subject.position);
            position = dstExit.TransformPoint(local);
        }
        else
        {
            position = dstExit.position;
        }

        return position + dstExit.right * Mathf.Max(0f, exitNudge);
    }

    public Collider2D GetInteriorCollider()
    {
        if (_cachedInteriorCollider != null)
        {
            return _cachedInteriorCollider;
        }

        BuildingController building = GetComponentInParent<BuildingController>();
        if (building == null)
        {
            return null;
        }

        Collider2D[] colliders = building.GetComponentsInChildren<Collider2D>();
        Collider2D fallback = null;
        foreach (Collider2D collider in colliders)
        {
            if (collider == null || collider == _collider)
            {
                continue;
            }

            if (!collider.isTrigger)
            {
                _cachedInteriorCollider = collider;
                return _cachedInteriorCollider;
            }

            if (fallback == null)
            {
                fallback = collider;
            }
        }

        if (fallback != null)
        {
            _cachedInteriorCollider = fallback;
        }

        return _cachedInteriorCollider;
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
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
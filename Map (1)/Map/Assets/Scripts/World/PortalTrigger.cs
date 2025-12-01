using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using Unity.AI.Navigation;

[RequireComponent(typeof(Collider2D))] 
public class PortalTrigger : MonoBehaviour
{
    [System.Serializable]
    public class BoolEvent : UnityEvent<bool> { }

    [Header("Portal Routing")]
    [Tooltip("入口對應的 PortalController，用於決定出口與冷卻。")]
    public PortalController portal;

    [Tooltip("允許以 NavMesh 直接通過而不啟動遠距離傳送。")]
    public bool allowNavMeshWalkThrough = true;

    [Tooltip("無論距離都強制使用傳送（門、遠距離或需要淡入淡出時啟用）。")]
    public bool forceTeleport = false;

    [Tooltip("當入口與出口距離超過此閾值時，啟用戲劇化傳送而非滑動穿牆。")]
    public float cinematicTeleportDistance = 8f;

    [Header("Transitions & Fades")]
    public bool useFadeOnTeleport = false;
    public UnityEvent onTransitionStarted;
    public UnityEvent onFadeOutRequested;
    public UnityEvent onFadeInRequested;
    public UnityEvent onTransitionCompleted;
    public BoolEvent onInteriorStateChanged;

    [Header("Disaster Response")]
    [Tooltip("此入口對應的 NavMeshLink (用於動態斷路)")]
    public NavMeshLink navLink;
    public Transform entryFocusPoint; // 門前的引導點 (解決穿牆)
    private bool isBlocked = false;

    private void Awake()
    {
        // 嘗試獲取 3D Collider
        Collider col = GetComponent<Collider>();
        if (col != null)
        {
            col.isTrigger = true;
        }

        // 嘗試獲取 2D Collider (因為你有用到 OnTriggerEnter2D)
        Collider2D col2D = GetComponent<Collider2D>();
        if (col2D != null)
        {
            col2D.isTrigger = true;
        }
        
        if (navLink == null)
        {
            navLink = GetComponent<NavMeshLink>();
        }
    }

    // 當災難系統判定此門塌陷時調用
    public void SetCollapse(bool collapsed)
    {
        isBlocked = collapsed;
        // 關鍵：停用 Link 會強制 Unity 重新計算所有經過此處的路徑
        if (navLink != null)
        {
            navLink.enabled = !collapsed;
        }
        
        if (collapsed)
        {
            Debug.Log($"[PortalTrigger] {name} has collapsed! Rerouting agents...");
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        HandleEnter(other.gameObject, null, other);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        HandleEnter(other.gameObject, other, null);
    }

    private void HandleEnter(GameObject other, Collider2D other2D, Collider other3D)
    {
        if (portal == null || other == null)
            return;

        var agent = other.GetComponent<AgentController>();
        if (agent == null)
            return;

        // 假設 Teleportable2D 是你專案中的另一個腳本
        var teleportable = other.GetComponent<Teleportable2D>();
        if (teleportable != null && teleportable.IsIgnoring)
            return;

        bool shouldTeleport = ShouldTeleport();
        if (!shouldTeleport && allowNavMeshWalkThrough)
        {
            onInteriorStateChanged?.Invoke(portal.isDoor);
            return;
        }

        onTransitionStarted?.Invoke();
        agent.PauseMovementForPortal();

        if (useFadeOnTeleport)
        {
            onFadeOutRequested?.Invoke();
        }

        // 確保 TryTeleport 參數與你的 PortalController 定義匹配
        bool teleported = portal.TryTeleport(
            other.transform,
            teleportable,
            agent,
            other.GetComponent<Rigidbody2D>(),
            other2D,
            true,
            false);

        if (teleported)
        {
            HandleNavMeshSync(other, portal.TargetPortal);
            onInteriorStateChanged?.Invoke(portal.TargetPortal != null && portal.TargetPortal.isDoor);
        }

        if (useFadeOnTeleport)
        {
            onFadeInRequested?.Invoke();
        }

        agent.ResumeMovementAfterPortal(true);
        onTransitionCompleted?.Invoke();
    }

    private bool ShouldTeleport()
    {
        if (forceTeleport)
            return true;

        if (!allowNavMeshWalkThrough)
            return true;

        if (portal == null || portal.TargetPortal == null)
            return true;

        float sqrDistance = (portal.TargetPortal.transform.position - portal.transform.position).sqrMagnitude;
        return sqrDistance >= cinematicTeleportDistance * cinematicTeleportDistance;
    }

    private void HandleNavMeshSync(GameObject mover, PortalController exitPortal)
    {
        if (mover == null || exitPortal == null)
            return;

        if (mover.TryGetComponent(out NavMeshAgent navAgent))
        {
            Transform exit = exitPortal.ExitTransform;
            // 確保 exitNudge 存在於你的 PortalController
            Vector3 destination = exit.position + exit.right * Mathf.Max(0f, portal != null ? portal.exitNudge : 0f);
            
            // 使用 Warp 移動 NavMeshAgent 避免被導航系統拉回
            navAgent.Warp(destination);
        }
    }
}
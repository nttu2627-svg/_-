using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

[RequireComponent(typeof(Collider))]
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
    [Tooltip("傳送時是否觸發淡出/淡入事件。")]
    public bool useFadeOnTeleport = true;

    [Tooltip("淡入淡出建議的時長（秒），供外部 UI / Cinemachine 事件使用。")]
    public float fadeDuration = 0.35f;

    [Header("Hooks")]
    public BoolEvent onInteriorStateChanged;
    public UnityEvent onFadeOutRequested;
    public UnityEvent onFadeInRequested;
    public UnityEvent onTransitionStarted;
    public UnityEvent onTransitionCompleted;

    private void Reset()
    {
        portal = GetComponent<PortalController>();
        var col = GetComponent<Collider>();
        if (col != null)
        {
            col.isTrigger = true;
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
            Vector3 destination = exit.position + exit.right * Mathf.Max(0f, portal != null ? portal.exitNudge : 0f);
            navAgent.Warp(destination);
        }
    }
}
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
    [Tooltip("入口對應的 PortalController，用於決定出口與冷卻。必須設定此欄位，否則傳送無法運作。")]
    public PortalController portal;

    [Tooltip("允許以 NavMesh 直接通過而不啟動遠距離傳送。適用於短距離的門檻或走廊連接。")]
    public bool allowNavMeshWalkThrough = true;

    [Tooltip("無論距離都強制使用傳送（門、遠距離或需要淡入淡出時啟用）。")]
    public bool forceTeleport = false;

    [Tooltip("當入口與出口距離超過此閾值時，啟用戲劇化傳送而非滑動穿牆。單位為 Unity 世界單位。")]
    public float cinematicTeleportDistance = 8f;

    [Header("Transitions & Fades")]
    [Tooltip("啟用傳送時使用淡入淡出效果。適合用於門戶或遠距離傳送，提供更平滑的視覺過渡。")]
    public bool useFadeOnTeleport = false;

    [Tooltip("當傳送流程開始時觸發的事件。可用於播放音效、禁用UI輸入等。")]
    public UnityEvent onTransitionStarted;

    [Tooltip("請求開始淡出效果時觸發的事件。應連接到負責淡出畫面的腳本或動畫。")]
    public UnityEvent onFadeOutRequested;

    [Tooltip("請求開始淡入效果時觸發的事件。應連接到負責淡入畫面的腳本或動畫。")]
    public UnityEvent onFadeInRequested;

    [Tooltip("傳送流程完成時觸發的事件。可用於恢復UI輸入、播放音效等。")]
    public UnityEvent onTransitionCompleted;

    [Tooltip("當代理人的室內/室外狀態改變時觸發。參數為 bool (true = 進入室內)。可用於切換環境音效或光照。")]
    public BoolEvent onInteriorStateChanged;

    [Header("Disaster Response")]
    [Tooltip("此入口對應的 NavMeshLink。災難發生時可動態禁用此 Link，強制 AI 重新尋路避開此門。")]
    public NavMeshLink navLink;

    [Tooltip("門前的引導點 Transform。代理人會先走到這個點再進行傳送，避免穿牆或卡住的問題。")]
    public Transform entryFocusPoint;
    
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
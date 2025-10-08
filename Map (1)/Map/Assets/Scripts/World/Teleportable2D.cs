using UnityEngine;

/// <summary>
/// 給所有「會被傳送」的物件使用，
/// 用來紀錄最後進入的傳送門與冷卻時間，避免來回瞬移。
/// </summary>
public class Teleportable2D : MonoBehaviour
{
    [HideInInspector] public int lastPortalId = -1;
    [HideInInspector] public float ignoreUntilTime = 0f;

    [Header("Portal Handling")]
    [SerializeField, Tooltip("傳送後與牆壁保持的最小距離")] private float interiorPadding = 0.12f;
    [SerializeField, Tooltip("到達傳送門的允許半徑，用於平移靠近時的判定")] private float portalSearchPadding = 0.12f;

    public bool IsIgnoring => Time.time < ignoreUntilTime;
    public float PortalSearchPadding => portalSearchPadding;

    public void SetIgnore(float duration, int portalId)
    {
        ignoreUntilTime = Time.time + duration;
        lastPortalId = portalId;
    }

    public Vector3 ClampPositionToInterior(Vector3 position, Collider2D interiorCollider)
    {
        if (interiorCollider == null)
        {
            return position;
        }

        Bounds bounds = interiorCollider.bounds;
        position.x = Mathf.Clamp(position.x, bounds.min.x + interiorPadding, bounds.max.x - interiorPadding);
        position.y = Mathf.Clamp(position.y, bounds.min.y + interiorPadding, bounds.max.y - interiorPadding);
        return position;
    }
}
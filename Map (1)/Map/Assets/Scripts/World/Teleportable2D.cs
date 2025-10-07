using UnityEngine;

/// <summary>
/// 給所有「會被傳送」的物件使用，
/// 用來紀錄最後進入的傳送門與冷卻時間，避免來回瞬移。
/// </summary>
public class Teleportable2D : MonoBehaviour
{
    [HideInInspector] public int lastPortalId = -1;
    [HideInInspector] public float ignoreUntilTime = 0f;

    public bool IsIgnoring => Time.time < ignoreUntilTime;

    public void SetIgnore(float duration, int portalId)
    {
        ignoreUntilTime = Time.time + duration;
        lastPortalId = portalId;
    }
}

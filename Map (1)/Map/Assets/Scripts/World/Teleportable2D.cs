///Teleportable2D.cs

using UnityEngine;

/// <summary>
// 這個檔案包含 Teleportable2D 類別，提供 2D 物件的傳送功能與冷卻管理。
/// 被傳送物件的冷卻與傳送設定。
/// 主要功能：
/// 1. 防止傳送後立刻重複觸發（冷卻時間）
/// 2. 提供 PortalSearchPadding 屬性供 AgentMovementController 使用
/// </summary>
[DisallowMultipleComponent]
public class Teleportable2D : MonoBehaviour
{
    [Header("傳送參數")]
    [Tooltip("搜尋 Portal 觸發點時的安全距離（公尺）。")]
    public float PortalSearchPadding = 0.1f;

    [Tooltip("傳送後忽略再次觸發的冷卻時間（秒）。")]
    public float defaultIgnoreDuration = 0.15f;

    private float _ignoreUntil = -1f;
    public int lastPortalId { get; private set; } = 0;

    /// <summary>
    /// 是否在冷卻中（傳送後暫時忽略下一次觸發）。
    /// </summary>
    public bool IsIgnoring => Time.time < _ignoreUntil;

    /// <summary>
    /// 設定忽略秒數與最後使用的傳送門 Id。
    /// </summary>
    public void SetIgnore(float seconds, int portalId)
    {
        _ignoreUntil = Mathf.Max(_ignoreUntil, Time.time + Mathf.Max(0f, seconds));
        lastPortalId = portalId;
    }

    private void OnDisable()
    {
        _ignoreUntil = -1f;
        lastPortalId = 0;
    }
}

using UnityEngine;
using TMPro;

/// <summary>
/// 單一日誌項目的顯示元件。
/// </summary>
public class LogEntry : MonoBehaviour
{
    [Tooltip("顯示日誌文字的 TMP 元件")]
    public TextMeshProUGUI text;

    /// <summary>
    /// 設定此項目的內容與時間戳記。
    /// </summary>
    public void Set(string message, System.DateTime timestamp)
    {
        if (text != null)
        {
            text.text = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {message}";
        }
    }

    /// <summary>
    /// 此項目的 RectTransform 便於虛擬化計算。
    /// </summary>
    public RectTransform RectTransform => (RectTransform)transform;
}
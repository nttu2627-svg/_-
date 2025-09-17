using UnityEngine;

/// <summary>
/// 基礎面板類別，提供顯示與隱藏的簡易封裝。
/// 將此腳本掛載到任意 UI 面板上，並呼叫 Show/Hide 方法即可控制顯示狀態。
/// </summary>
public class Panel : MonoBehaviour
{
    /// <summary>
    /// 顯示此面板。
    /// </summary>
    public virtual void Show()
    {
        gameObject.SetActive(true);
    }

    /// <summary>
    /// 隱藏此面板。
    /// </summary>
    public virtual void Hide()
    {
        gameObject.SetActive(false);
    }
}
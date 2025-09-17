// Scripts/UIManager.cs

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 集中管理遊戲中的主要 UI 面板。
/// 透過列舉或索引，可切換顯示不同面板，
/// 並確保同時僅有一個面板處於顯示狀態。
/// </summary>
public class UIManager : MonoBehaviour
{
    /// <summary>
    /// 所有可管理的 UI 面板類型。
    /// </summary>
    public enum PanelType
    {
        Main,
        Settings,
        Log
    }

    [System.Serializable]
    public struct PanelEntry
    {
        public PanelType type;
        public Panel panel;
    }

    [SerializeField]
    private List<PanelEntry> panels = new List<PanelEntry>();

    private Dictionary<PanelType, Panel> panelLookup;

    private void Awake()
    {
        panelLookup = new Dictionary<PanelType, Panel>();
        foreach (var entry in panels)
        {
            if (entry.panel != null && !panelLookup.ContainsKey(entry.type))
            {
                panelLookup.Add(entry.type, entry.panel);
            }
        }

        // 預設顯示主面板
        ShowPanel(PanelType.Main);
    }

    /// <summary>
    /// 顯示指定的面板，並隱藏其他面板。
    /// 可供 Tab 或 Dropdown 的事件呼叫。
    /// </summary>
    public void ShowPanel(PanelType target)
    {
        foreach (var kvp in panelLookup)
        {
            if (kvp.Key == target)
            {
                kvp.Value.Show();
            }
            else
            {
                kvp.Value.Hide();
            }
        }
    }

    /// <summary>
    /// 透過整數索引顯示面板，方便直接綁定在 Dropdown 或 Tab 的 onValueChanged。
    /// </summary>
    /// <param name="index">面板的整數枚舉值。</param>
    public void ShowPanelByIndex(int index)
    {
        var type = (PanelType)index;
        ShowPanel(type);
    }
}
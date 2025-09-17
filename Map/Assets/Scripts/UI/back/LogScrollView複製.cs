// Scripts/UI/LogScrollView.cs

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 【核心功能】
/// 使用 ScrollRect 高效顯示大量日誌。
/// 透過「虛擬化」技術，只生成少量 UI 物件 (由 poolSize 決定)，並在滾動時重複使用它們來顯示不同的日誌內容，
/// 極大地降低了 UI 物件過多所帶來的效能開銷。
/// 
/// 【設定要求】
/// 1. 將此腳本掛載在 Scroll View 的根物件上。
/// 2. 在 Inspector 中，將對應的 ScrollRect 和 LogEntry Prefab 拖入欄位。
/// 3. 【重要】Scroll View 的 Content 物件上【不能】掛載 Vertical Layout Group 或 Content Size Fitter，
///    因為此腳本會手動控制所有子物件的位置和 Content 的總高度。
/// </summary>
[RequireComponent(typeof(ScrollRect))]
public class LogScrollView_back : MonoBehaviour
{
    [Header("核心依賴 (必須賦值)")]
    [Tooltip("此腳本控制的滾動視窗元件")]
    public ScrollRect scrollRect;
    [Tooltip("單一日誌項目的 Prefab (預置件)")]
    public LogEntry entryPrefab;
    [Tooltip("物件池的大小，即同時在場景中存在的最大日誌實例數，建議值為可見數量的兩倍")]
    public int poolSize = 30;

    [Header("版面設定")]
    [Tooltip("當腳本無法從 Prefab 量測到實際高度時，使用的後備高度")]
    public float fallbackItemHeight = 160f;
    [Tooltip("每一個日誌項目之間的垂直間距")]
    public float spacing = 4f;

    // --- 私有變數 ---
    private readonly List<LogData> _allLogs = new(); // 儲存所有日誌的完整資料
    private readonly List<LogEntry> _entries = new(); // 物件池，儲存已實例化的 LogEntry 物件
    private RectTransform _content; // ScrollRect 的內容區域
    private float _itemHeight; // 單個日誌項目的高度，用於計算佈局

    // 過濾器相關
    private DateTime? _startFilter;
    private DateTime? _endFilter;

    private void Awake()
    {
        // 自動獲取必要的組件引用
        if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
        _content = scrollRect.content;

        // 健壯性檢查：如果 Viewport 沒有遮罩，滾動時內容會溢出，在此自動補上
        if (scrollRect.viewport != null && !scrollRect.viewport.TryGetComponent<RectMask2D>(out _))
        {
            scrollRect.viewport.gameObject.AddComponent<RectMask2D>();
            Debug.Log($"[LogScrollView] 已自動為 '{scrollRect.viewport.name}' 添加 RectMask2D 以實現內容裁切。", this);
        }

        // --- 初始化物件池與計算行高 ---
        if (entryPrefab != null && _content != null)
        {
            // 透過實例化一個樣本來動態測量其實際高度，這比依賴 Prefab 的靜態 Rect 更可靠
            var sampleInstance = Instantiate(entryPrefab, _content);
            ConfigureChildLayout(sampleInstance.RectTransform); // 強制設定其錨點與軸心
            sampleInstance.gameObject.SetActive(true);

            // 強制 Unity 立即更新一次畫布佈局，以便我們能準確量測到高度
            Canvas.ForceUpdateCanvases();

            _itemHeight = sampleInstance.RectTransform.rect.height;
            if (_itemHeight <= 0f)
            {
                Debug.LogWarning($"[LogScrollView] 無法從 Prefab 量測到有效高度，將使用後備高度: {fallbackItemHeight}", this);
                _itemHeight = fallbackItemHeight;
            }

            // 將測量完的樣本加入物件池
            _entries.Add(sampleInstance);
            
            // 根據 poolSize 生成剩餘的物件放入池中
            for (int i = 1; i < poolSize; i++)
            {
                var entry = Instantiate(entryPrefab, _content);
                ConfigureChildLayout(entry.RectTransform);
                entry.gameObject.SetActive(false); // 預設隱藏
                _entries.Add(entry);
            }
        }

        // 監聽滾動事件，以便在滾動時更新可見項目
        scrollRect.onValueChanged.AddListener(OnScroll);
    }

    private void OnDestroy()
    {
        // 組件銷毀時，移除監聽，防止記憶體洩漏
        if (scrollRect != null)
        {
            scrollRect.onValueChanged.RemoveListener(OnScroll);
        }
    }

    #region 公開方法 (Public API)

    /// <summary>
    /// 新增一筆日誌訊息到列表中。
    /// </summary>
    public void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _allLogs.Add(new LogData { Message = message, Timestamp = DateTime.Now });
        Refresh();
    }
    
    /// <summary>
    /// 清空日誌，並只顯示一條新的訊息。
    /// </summary>
    public void SetSingle(string message)
    {
        _allLogs.Clear();
        if(!string.IsNullOrEmpty(message))
        {
            AddLog(message);
        }
        else
        {
            Refresh();
        }
        
        // 將視圖滾動回頂部
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
    }

    /// <summary>
    /// 設定時間範圍以篩選日誌。傳入 null 則表示不限制該邊界。
    /// </summary>
    public void SetTimeFilter(DateTime? start, DateTime? end)
    {
        _startFilter = start;
        _endFilter = end;
        Refresh();
    }

    /// <summary>
    /// 清除所有日誌。
    /// </summary>
    public void Clear()
    {
        _allLogs.Clear();
        // 將視圖滾動回頂部
        if (_content != null) _content.anchoredPosition = Vector2.zero;
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
        Refresh();
    }

    #endregion

    #region 內部邏輯 (Internal Logic)

    /// <summary>
    /// 刷新整個視圖，通常在資料變更後呼叫。
    /// </summary>
    private void Refresh()
    {
        List<LogData> filteredLogs = ApplyFilter();
        UpdateContentHeight(filteredLogs);
        UpdateVisibleEntries(filteredLogs);
    }

    private List<LogData> ApplyFilter()
    {
        if (!_startFilter.HasValue && !_endFilter.HasValue) return _allLogs;
        
        return _allLogs.FindAll(log =>
            (!_startFilter.HasValue || log.Timestamp >= _startFilter.Value) &&
            (!_endFilter.HasValue || log.Timestamp <= _endFilter.Value));
    }

    /// <summary>
    /// 根據篩選後的日誌數量，更新 Content 的總高度，讓滾動條能正確反應。
    /// </summary>
    private void UpdateContentHeight(List<LogData> logs)
    {
        if (_content == null) return;
        // 計算所有項目所需的總高度
        float totalHeight = logs.Count * (_itemHeight + spacing) - spacing; // 最後一項後面不需要間距
        // 確保 Content 的高度至少和 Viewport 一樣高，避免在項目少時滾動條行為異常
        float minHeight = scrollRect.viewport != null ? scrollRect.viewport.rect.height : 0f;
        _content.sizeDelta = new Vector2(_content.sizeDelta.x, Mathf.Max(totalHeight, minHeight));
    }

    /// <summary>
    /// 當滾動發生時被呼叫。
    /// </summary>
    private void OnScroll(Vector2 position)
    {
        UpdateVisibleEntries(ApplyFilter());
    }

    /// <summary>
    /// 這是虛擬化滾動的核心。
    /// 計算當前哪些項目應該是可見的，並從物件池中取出對應的 UI 物件來顯示它們的內容。
    /// </summary>
    private void UpdateVisibleEntries(List<LogData> logs)
    {
        if (_content == null || _itemHeight <= 0f || _entries.Count == 0) return;

        float rowHeight = _itemHeight + spacing;
        float viewportHeight = scrollRect.viewport.rect.height;

        // 計算在可見範圍內的第一個日誌項目的索引
        int firstVisibleIndex = Mathf.Max(0, Mathf.FloorToInt(_content.anchoredPosition.y / rowHeight));
        // 計算視口內可以容納多少個日誌項目
        int visibleCount = Mathf.Min(poolSize, Mathf.CeilToInt(viewportHeight / rowHeight) + 1);

        // 遍歷物件池中的所有 UI 項目
        for (int i = 0; i < _entries.Count; i++)
        {
            int logIndex = firstVisibleIndex + i; // 對應到完整日誌列表中的索引
            LogEntry entry = _entries[i];

            // 檢查這個 UI 項目是否對應一個有效的、且在可見範圍內的日誌資料
            if (logIndex < logs.Count && i < visibleCount)
            {
                entry.gameObject.SetActive(true);
                
                // 設定內容和位置
                LogData data = logs[logIndex];
                entry.Set(data.Message, data.Timestamp);
                
                // 手動設定其在 Content 中的位置
                RectTransform rt = entry.RectTransform;
                rt.anchoredPosition = new Vector2(0f, -logIndex * rowHeight);
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _itemHeight);
            }
            else
            {
                // 如果超出範圍，則從物件池中隱藏它
                entry.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 輔助函式：強制設定子物件的 RectTransform，確保其錨點和軸心正確，以實現從上到下的堆疊佈局。
    /// </summary>
    private static void ConfigureChildLayout(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0, 1); // 錨點：頂部
        rt.anchorMax = new Vector2(1, 1); // 錨點：左右拉伸
        rt.pivot = new Vector2(0.5f, 1);  // 軸心：頂部中心
        
        // 清理左右的 offset，讓它完全貼合父物件寬度
        rt.offsetMin = new Vector2(0, rt.offsetMin.y);
        rt.offsetMax = new Vector2(0, rt.offsetMax.y);
    }

    #endregion

    /// <summary>
    /// 用於儲存單筆日誌資料的內部結構。
    /// </summary>
    private struct LogData
    {
        public string Message;
        public DateTime Timestamp;
    }
}
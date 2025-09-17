// Scripts/UI/LogScrollView.cs (動態高度 + 寬度拉伸強化版)

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(ScrollRect))]
public class LogScrollView : MonoBehaviour
{
    [Header("核心依賴 (必須賦值)")]
    public ScrollRect scrollRect;
    public LogEntry entryPrefab;
    [Tooltip("物件池的大小，建議值為螢幕可見數量的兩倍左右")]
    public int poolSize = 20;

    [Header("版面設定")]
    [Tooltip("當腳本無法從 Prefab 量測到實際高度時，使用的後備高度")]
    public float fallbackItemHeight = 160f;
    [Tooltip("每一個日誌項目之間的垂直間距")]
    public float spacing = 4f;

    // --- 私有變數 ---
    private readonly List<LogData> _allLogs = new();
    private readonly List<LogEntry> _entries = new();
    private RectTransform _content;
    private LogEntry _measurementTool;
    private DateTime? _startFilter;
    private DateTime? _endFilter;

    private void Awake()
    {
        if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
        _content = scrollRect.content;

        if (scrollRect.viewport != null && !scrollRect.viewport.TryGetComponent<RectMask2D>(out _))
        {
            scrollRect.viewport.gameObject.AddComponent<RectMask2D>();
        }

        if (entryPrefab != null && _content != null)
        {
            // 初始化物件池
            for (int i = 0; i < poolSize; i++)
            {
                var entry = Instantiate(entryPrefab, _content);
                ConfigureChildLayout(entry.RectTransform);
                entry.gameObject.SetActive(false);
                _entries.Add(entry);
            }

            // 初始化用於測量高度的工具
            _measurementTool = Instantiate(entryPrefab, _content);
            ConfigureChildLayout(_measurementTool.RectTransform);
            _measurementTool.gameObject.name = "MeasurementTool";
            _measurementTool.RectTransform.anchoredPosition = new Vector2(10000, 10000); // 移到螢幕外
            var canvasGroup = _measurementTool.gameObject.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        scrollRect.onValueChanged.AddListener(OnScroll);
    }

    private void OnDestroy()
    {
        if (scrollRect != null)
        {
            scrollRect.onValueChanged.RemoveListener(OnScroll);
        }
    }

    #region 公開方法 (Public API)

    public void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        float measuredHeight = MeasureLogHeight(message);
        _allLogs.Add(new LogData { Message = message, Timestamp = DateTime.Now, Height = measuredHeight });
        Refresh();
    }

    public void SetSingle(string message)
    {
        _allLogs.Clear(); // 無論如何都先清空資料
        
        if (!string.IsNullOrEmpty(message))
        {
            // 只有在訊息非空時才新增並測量
            float measuredHeight = MeasureLogHeight(message);
            _allLogs.Add(new LogData { Message = message, Timestamp = DateTime.Now, Height = measuredHeight });
        }
        
        // 刷新佈局（如果訊息為空，這會將 Content 高度重設為最小值）
        Refresh();
        
        // 將視圖滾動回頂部
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
    }

    public void SetTimeFilter(DateTime? start, DateTime? end)
    {
        _startFilter = start;
        _endFilter = end;
        Refresh();
    }

    public void Clear()
    {
        _allLogs.Clear();
        if (_content != null) _content.anchoredPosition = Vector2.zero;
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
        Refresh();
    }

    #endregion

    #region 內部邏輯 (Internal Logic)

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

    private void UpdateContentHeight(List<LogData> logs)
    {
        if (_content == null) return;
        float totalHeight = 0f;
        foreach (var log in logs)
        {
            totalHeight += log.Height + spacing;
        }
        if (logs.Count > 0) totalHeight -= spacing;

        float minHeight = scrollRect.viewport != null ? scrollRect.viewport.rect.height : 0f;
        _content.sizeDelta = new Vector2(_content.sizeDelta.x, Mathf.Max(totalHeight, minHeight));
    }

    private void OnScroll(Vector2 position)
    {
        UpdateVisibleEntries(ApplyFilter());
    }

private void UpdateVisibleEntries(List<LogData> logs)
{
    if (_content == null || _entries.Count == 0) return;

    // 確保 pivot = (0,1) 基準正確
    if (_content.pivot != new Vector2(0, 1))
    {
        _content.pivot = new Vector2(0, 1);
        _content.anchorMin = new Vector2(0, 1);
        _content.anchorMax = new Vector2(1, 1);
    }

    // 目前視窗頂端的 Y 座標
    float currentY = -_content.anchoredPosition.y;
    float viewportHeight = scrollRect.viewport.rect.height;

    // 增加上下緩衝範圍
    float buffer = 50f;

    float itemY = 0f;
    int entryIndex = 0;

    for (int i = 0; i < logs.Count; i++)
    {
        LogData data = logs[i];
        float itemHeight = data.Height;
        float itemBottomY = itemY + itemHeight;

        bool isVisible = (itemY < currentY + viewportHeight + buffer) &&
                         (itemBottomY > currentY - buffer);

        if (isVisible)
        {
            if (entryIndex < _entries.Count)
            {
                LogEntry entry = _entries[entryIndex];
                entry.gameObject.SetActive(true);
                entry.Set(data.Message, data.Timestamp);
                RectTransform rt = entry.RectTransform;
                rt.anchoredPosition = new Vector2(0f, -itemY);
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, itemHeight);
                entryIndex++;
            }
        }

        itemY += itemHeight + spacing;
    }

    // 關閉多餘的池物件
    for (int i = entryIndex; i < _entries.Count; i++)
    {
        _entries[i].gameObject.SetActive(false);
    }
}


    private float MeasureLogHeight(string message)
    {
        if (_measurementTool == null) return this.fallbackItemHeight;

        _measurementTool.Set(message, DateTime.Now);
        LayoutRebuilder.ForceRebuildLayoutImmediate(_measurementTool.RectTransform);
        float measuredHeight = _measurementTool.RectTransform.rect.height;
        return measuredHeight > 1f ? measuredHeight : this.fallbackItemHeight;
    }

    /// <summary>
    /// 【核心】強制設定子物件的 RectTransform，確保其錨點和軸心正確，
    /// 以實現「從上到下堆疊」且「寬度自動拉伸」的佈局。
    /// </summary>
    private static void ConfigureChildLayout(RectTransform rt)
    {
        // 錨點：將子物件的四個角，分別錨定在父物件(Content)的頂部左邊和頂部右邊。
        // anchorMin.x = 0 (左), anchorMax.x = 1 (右) -> 這就是實現水平拉伸的關鍵。
        // anchorMin.y = 1 (頂), anchorMax.y = 1 (頂) -> Y軸錨定在頂部。
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);

        // 軸心：將物件自己的原點(0,0)設定在頂部中心。
        // 這樣所有 Y 軸的位置計算都是相對於物件的頂部邊緣。
        rt.pivot = new Vector2(0.5f, 1);

        // 偏移量：設定與錨點的距離。
        // offsetMin.x = 0 -> 左邊緣與父物件的左邊緣距離為 0。
        // offsetMax.x = 0 -> 右邊緣與父物件的右邊緣距離為 0。
        // 這兩行程式碼會強力覆蓋掉 Inspector 中的 Left 和 Right 值，確保寬度永遠填滿。
        rt.offsetMin = new Vector2(0, rt.offsetMin.y);
        rt.offsetMax = new Vector2(0, rt.offsetMax.y);
    }

    #endregion

    private struct LogData
    {
        public string Message;
        public DateTime Timestamp;
        public float Height;
    }
}
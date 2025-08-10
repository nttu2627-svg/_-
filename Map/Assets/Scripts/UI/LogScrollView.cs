using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(ScrollRect))]
public class LogScrollView : MonoBehaviour
{
    [Tooltip("滾動視窗")]
    public ScrollRect scrollRect;
    [Tooltip("日誌項目預置件")]
    public LogEntry entryPrefab;
    [Tooltip("同時存在的最大實例數，用於虛擬化")]
    public int poolSize = 20;

    [Header("版面")]
    [Tooltip("量不到高度時的保底列高")]
    public float fallbackItemHeight = 36f;
    [Tooltip("列與列之間的間距")]
    public float spacing = 4f;

    private readonly List<LogData> _allLogs = new();
    private readonly List<LogEntry> _entries = new();
    private DateTime? _startFilter, _endFilter;
    private RectTransform _content;
    private float _itemHeight;

    private void Awake()
    {
        if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
        _content = scrollRect.content;

        // 確保有裁切
        if (scrollRect.viewport != null &&
            !scrollRect.viewport.TryGetComponent<RectMask2D>(out _))
        {
            scrollRect.viewport.gameObject.AddComponent<RectMask2D>();
        }

        // 先做一個樣本來量實際高度（Prefab 的 rect 可能是 0）
        if (entryPrefab != null && _content != null)
        {
            var first = Instantiate(entryPrefab, _content);
            ConfigureChild(first.RectTransform);
            first.gameObject.SetActive(true);
            // 讓 Unity 先跑一次版面再量尺寸
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(first.RectTransform);

            _itemHeight = first.RectTransform.rect.height;
            if (_itemHeight <= 0f) _itemHeight = fallbackItemHeight;

            _entries.Add(first);
            for (int i = 1; i < poolSize; i++)
            {
                var e = Instantiate(entryPrefab, _content);
                ConfigureChild(e.RectTransform);
                e.gameObject.SetActive(false);
                _entries.Add(e);
            }
        }

        scrollRect.onValueChanged.AddListener(OnScroll);
    }

    private void OnDestroy()
    {
        scrollRect.onValueChanged.RemoveListener(OnScroll);
    }

    public void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _allLogs.Add(new LogData { Message = message, Timestamp = DateTime.Now });
        Refresh();
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
        // 回頂部，避免 anchoredPosition 留在舊值
        if (_content != null) _content.anchoredPosition = Vector2.zero;
        Refresh();
    }

    private void Refresh()
    {
        var logs = ApplyFilter();
        UpdateContent(logs);
        UpdateVisible(logs);
    }

    private List<LogData> ApplyFilter()
    {
        if (!_startFilter.HasValue && !_endFilter.HasValue) return _allLogs;
        return _allLogs.FindAll(l =>
            (!_startFilter.HasValue || l.Timestamp >= _startFilter.Value) &&
            (!_endFilter.HasValue || l.Timestamp <= _endFilter.Value));
    }

    private void UpdateContent(List<LogData> logs)
    {
        if (_content == null) return;
        float need = logs.Count * (_itemHeight + spacing);
        float min = scrollRect.viewport != null ? scrollRect.viewport.rect.height : 0f;
        _content.sizeDelta = new Vector2(_content.sizeDelta.x, Mathf.Max(need, min));
    }

    private void OnScroll(Vector2 _)
    {
        UpdateVisible(ApplyFilter());
    }

    private void UpdateVisible(List<LogData> logs)
    {
        if (_content == null || _itemHeight <= 0f) return;

        float row = _itemHeight + spacing;
        float viewportHeight = scrollRect.viewport.rect.height;
        int firstIndex = Mathf.Max(0, Mathf.FloorToInt(_content.anchoredPosition.y / row));
        int visibleCount = Mathf.Min(_entries.Count, Mathf.CeilToInt(viewportHeight / row) + 1);

        for (int i = 0; i < _entries.Count; i++)
        {
            int index = firstIndex + i;
            var entry = _entries[i];

            if (index < logs.Count && i < visibleCount)
            {
                entry.gameObject.SetActive(true);
                var data = logs[index];
                entry.Set(data.Message, data.Timestamp);
                var rt = entry.RectTransform;
                rt.anchoredPosition = new Vector2(0f, -index * row);
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _itemHeight);
            }
            else
            {
                entry.gameObject.SetActive(false);
            }
        }
    }

    private static void ConfigureChild(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1);
        rt.offsetMin = new Vector2(0, rt.offsetMin.y);
        rt.offsetMax = new Vector2(0, rt.offsetMax.y);
    }

    private struct LogData
    {
        public string Message;
        public DateTime Timestamp;
    }
}

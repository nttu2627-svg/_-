using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 使用 ScrollRect 顯示日誌，並具備簡易虛擬化與時間範圍篩選功能。
/// </summary>
[RequireComponent(typeof(ScrollRect))]
public class LogScrollView : MonoBehaviour
{
    [Tooltip("滾動視窗")]
    public ScrollRect scrollRect;
    [Tooltip("日誌項目預置件")]
    public LogEntry entryPrefab;
    [Tooltip("同時存在的最大實例數，用於虛擬化")]
    public int poolSize = 20;

    private readonly List<LogData> _allLogs = new List<LogData>();
    private readonly List<LogEntry> _entries = new List<LogEntry>();
    private DateTime? _startFilter;
    private DateTime? _endFilter;
    private RectTransform _content;
    private float _itemHeight;

    private void Awake()
    {
        if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
        _content = scrollRect.content;

        if (entryPrefab != null)
        {
            _itemHeight = entryPrefab.RectTransform.rect.height;
            for (int i = 0; i < poolSize; i++)
            {
                var e = Instantiate(entryPrefab, _content);
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

    /// <summary>
    /// 新增一筆日誌。
    /// </summary>
    public void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _allLogs.Add(new LogData { Message = message, Timestamp = DateTime.Now });
        Refresh();
    }

    /// <summary>
    /// 設定時間範圍以篩選日誌。若為 null 則不限制。
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
        Refresh();
    }

    private void Refresh()
    {
        List<LogData> logs = ApplyFilter();
        UpdateContent(logs);
        UpdateVisible(logs);
    }

    private List<LogData> ApplyFilter()
    {
        if (!_startFilter.HasValue && !_endFilter.HasValue) return _allLogs;
        return _allLogs.FindAll(l => (!_startFilter.HasValue || l.Timestamp >= _startFilter.Value) &&
                                     (!_endFilter.HasValue || l.Timestamp <= _endFilter.Value));
    }

    private void UpdateContent(List<LogData> logs)
    {
        if (_content == null) return;
        _content.sizeDelta = new Vector2(_content.sizeDelta.x, logs.Count * _itemHeight);
    }

    private void OnScroll(Vector2 pos)
    {
        UpdateVisible(ApplyFilter());
    }

    private void UpdateVisible(List<LogData> logs)
    {
        if (entryPrefab == null) return;
        float viewportHeight = scrollRect.viewport.rect.height;
        int firstIndex = Mathf.Max(0, Mathf.FloorToInt(_content.anchoredPosition.y / _itemHeight));
        int visibleCount = Mathf.CeilToInt(viewportHeight / _itemHeight) + 1;

        for (int i = 0; i < _entries.Count; i++)
        {
            int index = firstIndex + i;
            if (index < logs.Count && i < visibleCount)
            {
                var entry = _entries[i];
                var data = logs[index];
                entry.gameObject.SetActive(true);
                entry.Set(data.Message, data.Timestamp);
                entry.RectTransform.anchoredPosition = new Vector2(0, -index * _itemHeight);
            }
            else
            {
                _entries[i].gameObject.SetActive(false);
            }
        }
    }

    private struct LogData
    {
        public string Message;
        public DateTime Timestamp;
    }
}
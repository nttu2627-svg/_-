// Scripts/UnityMainThreadDispatcher.cs (修正版)
using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();
    private static UnityMainThreadDispatcher _instance = null;

    public static UnityMainThreadDispatcher Instance()
    {
        if (_instance == null)
        {
            GameObject obj = new GameObject("UnityMainThreadDispatcher");
            _instance = obj.AddComponent<UnityMainThreadDispatcher>();
        }
        return _instance;
    }

    void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            // 确保此物件是根物件，然后再调用 DontDestroyOnLoad
            transform.SetParent(null);
            DontDestroyOnLoad(this.gameObject);
        }
        else if (_instance != this)
            // 当场景中已有 Dispatcher 时，判定谁应保留为单例
            if (_instance.GetType() == typeof(UnityMainThreadDispatcher) && GetType() != typeof(UnityMainThreadDispatcher))
            {
                // 现有实例只是基础型，优先保留当前(如 SimulationManager)
                var oldInstance = _instance;
                _instance = this;
                transform.SetParent(null);
                DontDestroyOnLoad(this.gameObject);
                Destroy(oldInstance);
            }
            else
            {
                // 其余情况仍移除重复组件即可，避免误删拥有者物件
                Destroy(this);
            }
    }

    public void Enqueue(Action action)
    {
        lock (_executionQueue)
        {
            _executionQueue.Enqueue(action);
        }
    }

    void Update()
    {
        lock (_executionQueue)
        {
            while (_executionQueue.Count > 0)
            {
                _executionQueue.Dequeue().Invoke();
            }
        }
    }
}
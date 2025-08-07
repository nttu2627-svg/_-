// Scripts/UIController.cs (攝影機控制整合最終版)

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using System;

/// <summary>
/// 負責管理遊戲中的所有主要 UI 互動。
/// 包括：讀取模擬設定、動態生成代理人選項、觸發模擬開始、更新日誌，以及【新增的】與攝影機管理器互動。
/// </summary>
public class UIController : MonoBehaviour
{
    [Header("核心依賴 (必须赋值!)")]
    [Tooltip("場景中的 CameraManager 實例，用於控制攝影機")]
    public CameraManager cameraManager; // ### 新增攝影機管理器引用 ###
    [Tooltip("場景中的 CameraController 實例，用於手動跟隨")]
    public CameraController cameraController;
    [Tooltip("場景中的 SimulationClient 實例")]
    public SimulationClient simulationClient;
    [Tooltip("包含所有代理人 Prefab 實例的父物件")]
    public Transform characterRoot;
    [Tooltip("包含所有地點 Transform 的父物件 (例如 '內飾')")]
    public Transform locationRoot;

    [Header("UI 面板 (可选，用于隐藏)")]
    public GameObject timeSettingsPanel;
    public GameObject controlPanel;

    [Header("UI 输入/输出元件 (必须赋值!)")]
    public TMP_InputField durationInput;
    public TMP_InputField stepInput;
    public TMP_InputField yearInput;
    public TMP_InputField monthInput;
    public TMP_InputField dayInput;
    public TMP_InputField hourInput;
    public TMP_InputField minuteInput;
    public Button startButton;

    [Header("动态生成UI所需 (必须赋值!)")]
    [Tooltip("一个设计好的 Toggle Prefab，用于实例化")]
    public GameObject mbtiTogglePrefab;
    [Tooltip("用于容纳所有动态生成的 Toggle 的父物件 (必须挂载布局组件，如 Grid Layout Group)")]
    public Transform mbtiToggleGroupParent;

    [Header("显示区域 (必须赋值!)")]
    public TextMeshProUGUI statusBarText;
    public LogScrollView mainLogView;
    public LogScrollView historyLogView;
    public LogScrollView llmLogView;

    [Header("日誌切換與其他按鈕 (可選)")]
    [Tooltip("包含三個日誌切換按鈕的父物件")]
    public GameObject logButtonGroup;
    [Tooltip("啟動前隱藏的攝影機按鈕面板")]
    public GameObject cameraButtonsPanel;
    public Button mainLogButton;
    public Button historyLogButton;
    public Button llmLogButton;

    [Header("事件相关 UI (可选)")]
    public Toggle eqEnabledToggle;
    public TMP_InputField eqJsonInput;
    public TMP_Dropdown eqStepDropdown;


    [Header("攝影機UI (可選, 用於自動生成)")]
    [Tooltip("用於容納所有動態生成的攝影機按鈕的父物件")]
    public Transform cameraButtonGroupParent;
    [Tooltip("一個設計好的攝影機按鈕 Prefab")]
    public GameObject cameraButtonPrefab;

    // 用于存储 UI Toggle 和 AgentController 之间的映射关系
    private readonly Dictionary<Toggle, AgentController> _agentToggleMap = new Dictionary<Toggle, AgentController>();

    // 使用一个静态布尔值来确保在整个应用生命周期中，UI 只被生成一次
    private static bool _isUIPopulated = false;

    void Awake()
    {
        if (!ValidateDependencies())
        {
            Debug.LogError("UIController 已被禁用，因为缺少一个或多个关键的 Inspector 引用。请检查 Console 中的錯誤讯息。", this);
            this.enabled = false;
            return;
        }

        // 確保 UI 生成邏輯只執行一次
        if (!_isUIPopulated)
        {
            PopulateAgentSelectionUI();
            _isUIPopulated = true;
        }
    }

    void Start()
    {
        if (startButton != null)
        {
            startButton.onClick.AddListener(OnStartButtonClick);
        }
        // 啟動前先隱藏特定 UI
        if (logButtonGroup != null) logButtonGroup.SetActive(false);
        if (cameraButtonsPanel != null) cameraButtonsPanel.SetActive(false);
        if (statusBarText != null) statusBarText.gameObject.SetActive(false);
        if (mainLogView != null) mainLogView.gameObject.SetActive(false);
        if (historyLogView != null) historyLogView.gameObject.SetActive(false);
        if (llmLogView != null) llmLogView.gameObject.SetActive(false);

        // 設定日誌切換按鈕
        if (mainLogButton != null) mainLogButton.onClick.AddListener(ShowMainLogDisplay);
        if (historyLogButton != null) historyLogButton.onClick.AddListener(ShowHistoryLogDisplay);
        if (llmLogButton != null) llmLogButton.onClick.AddListener(ShowLlmLogDisplay);

        SetDefaultValues();

        SimulationClient.OnStatusUpdate += UpdateStatusBar;
        SimulationClient.OnLogUpdate += UpdateLogs;
        // 在 Start 的最後呼叫新的函式來生成攝影機按鈕
        PopulateCameraButtonUI();
    }

    void OnDestroy()
    {
        // 移除所有監聽器，防止記憶體洩漏
        if (startButton != null) startButton.onClick.RemoveListener(OnStartButtonClick);
        if (mainLogButton != null) mainLogButton.onClick.RemoveListener(ShowMainLogDisplay);
        if (historyLogButton != null) historyLogButton.onClick.RemoveListener(ShowHistoryLogDisplay);
        if (llmLogButton != null) llmLogButton.onClick.RemoveListener(ShowLlmLogDisplay);
        SimulationClient.OnStatusUpdate -= UpdateStatusBar;
        SimulationClient.OnLogUpdate -= UpdateLogs;

        foreach (var toggle in _agentToggleMap.Keys)
        {
            if (toggle != null) { toggle.onValueChanged.RemoveAllListeners(); }
        }
    }

    /// <summary>
    /// 驗證所有必須在 Inspector 中賦值的欄位是否都已設定。
    /// </summary>
    private bool ValidateDependencies()
    {
        bool isValid = true;
        // ### 新增對 CameraManager 的檢查 ###
        if (cameraManager == null) { Debug.LogError("UIController 錯誤: 'Camera Manager' 未賦值! 攝影機跟隨功能將失效。", this); isValid = false; }
        if (cameraController == null) { Debug.LogError("UIController 错误: 'Camera Controller' 未赋值!", this); isValid = false; }
        if (simulationClient == null) { Debug.LogError("UIController 錯誤: 'Simulation Client' 未賦值!", this); isValid = false; }
        if (characterRoot == null) { Debug.LogError("UIController 錯誤: 'Character Root' 未賦值!", this); isValid = false; }
        if (locationRoot == null) { Debug.LogError("UIController 錯誤: 'Location Root' 未賦值!", this); isValid = false; }
        if (mbtiTogglePrefab == null) { Debug.LogError("UIController 錯誤: 'Mbti Toggle Prefab' 未賦值!", this); isValid = false; }
        if (mbtiToggleGroupParent == null) { Debug.LogError("UIController 錯誤: 'Mbti Toggle Group Parent' 未賦值!", this); isValid = false; }
        if (startButton == null) { Debug.LogError("UIController 錯誤: 'Start Button' 未賦值!", this); isValid = false; }
        return isValid;
    }

    /// <summary>
    /// 根據 characterRoot 下的所有代理人，動態生成可供選擇的 UI Toggle。
    /// </summary>
    private void PopulateAgentSelectionUI()
    {
        Debug.Log("[UIController] 开始动态生成代理人选择UI...");

        foreach (Transform child in mbtiToggleGroupParent) { Destroy(child.gameObject); }
        _agentToggleMap.Clear();

        ToggleGroup toggleGroup = mbtiToggleGroupParent.GetComponent<ToggleGroup>();

        // 查找所有 AgentController，並根據名字去重和排序
        var agentsInScene = characterRoot.GetComponentsInChildren<AgentController>(true)
            .Select(a =>
            {
                a.agentName = (string.IsNullOrEmpty(a.agentName) ? a.gameObject.name : a.agentName).Trim().ToUpper();
                return a;
            })
            .GroupBy(a => a.agentName)
            .Select(g => g.First())
            .OrderBy(a => a.agentName)
            .ToList();

        // 建立 UI
        foreach (AgentController agent in agentsInScene)
        {
            GameObject toggleGO = Instantiate(mbtiTogglePrefab, mbtiToggleGroupParent);
            toggleGO.name = "Toggle_" + agent.agentName;

            Toggle toggle = toggleGO.GetComponent<Toggle>();
            TextMeshProUGUI label = toggleGO.GetComponentInChildren<TextMeshProUGUI>();

            if (toggle != null && label != null)
            {
                label.text = agent.agentName;
                if (toggleGroup != null) { toggle.group = toggleGroup; }

                // 為每個 Toggle 添加監聽器，當狀態改變時調用 OnAgentToggleChanged
                toggle.onValueChanged.AddListener((isOn) => { OnAgentToggleChanged(agent, isOn); });

                _agentToggleMap.Add(toggle, agent);
                agent.gameObject.SetActive(false); // 初始時隱藏所有代理人
            }
            else
            {
                Debug.LogError($"為 {agent.agentName} 創建的 Toggle Prefab 實例結構不正確！", this);
            }
        }

        Debug.Log($"[UIController] 已成功生成 {_agentToggleMap.Count} 個代理人選項。");
    }

    /// <summary>
    /// ### 攝影機控制核心 ###
    /// 當代理人選項的 Toggle 狀態改變時被調用。
    /// </summary>
    /// <param name="agent">對應的代理人控制器</param>
    /// <param name="isOn">Toggle 是否被選中</param>
    private void OnAgentToggleChanged(AgentController agent, bool isOn)
    {
        if (agent != null)
        {
            // 啟用或禁用代理人在場景中的 GameObject
            agent.gameObject.SetActive(isOn);

            // 如果 Toggle 是被「選中」，並且 CameraManager 已賦值
            // 則命令攝影機管理器切換模式並開始跟隨此代理人
            if (isOn && cameraManager != null)
            {
                cameraController?.FollowTarget(agent.transform);

            }
        }
    }

    /// <summary>
    /// 設定 UI 輸入欄位的預設值。
    /// </summary>
    private void SetDefaultValues()
    {
        if (durationInput != null) durationInput.text = "2400";
        if (stepInput != null) stepInput.text = "30";
        if (yearInput != null) yearInput.text = "2024";
        if (monthInput != null) monthInput.text = "11";
        if (dayInput != null) dayInput.text = "18";
        if (hourInput != null) hourInput.text = "3";
        if (minuteInput != null) minuteInput.text = "0";

        // 預設選中前兩個代理人
        var toggles = _agentToggleMap.Keys.ToList();
        for (int i = 0; i < toggles.Count; i++)
        {
            // 確保只在需要時觸發 OnValueChanged，避免遊戲一開始就跟隨
            if (i < 2)
            {
                toggles[i].SetIsOnWithoutNotify(true);
                OnAgentToggleChanged(_agentToggleMap[toggles[i]], true); // 手動觸發一次以顯示物件
            }
            else
            {
                toggles[i].SetIsOnWithoutNotify(false);
            }
        }

        // 如果有多個被選中，預設跟隨第一個
        var firstSelected = _agentToggleMap.FirstOrDefault(p => p.Key.isOn);
        if (firstSelected.Value != null)
        {
            cameraManager.SetFollowTarget(firstSelected.Value.transform);
        }


        if (eqEnabledToggle != null) eqEnabledToggle.isOn = true;
        if (eqJsonInput != null) eqJsonInput.text = "[{\"time\": \"2024-11-18-11-00\", \"duration\": 30, \"intensity\": 0.75}]";
        if (eqStepDropdown != null)
        {
            int targetIndex = eqStepDropdown.options.FindIndex(option => option.text == "5");
            if (targetIndex != -1) eqStepDropdown.value = targetIndex;
        }
    }

    /// <summary>
    /// 當開始模擬按鈕被點擊時，收集所有 UI 設定並發送到後端。
    /// </summary>
    // 刪除重複的 OnStartButtonClick 方法（已在下方完整實作）

    /// <summary>
    /// 隱藏設定相關的 UI 面板。
    /// </summary>
    private void HideSettingsPanels()
    {
        if (timeSettingsPanel != null) timeSettingsPanel.SetActive(false);
        if (controlPanel != null) controlPanel.SetActive(false);
        if (startButton != null) startButton.gameObject.SetActive(false);
    }

    // ... (日誌與狀態更新函式與您提供的版本相同，功能完整故不重複貼出)
    private void OnStartButtonClick()
    {
        List<string> locationNames = new List<string>();
        if (locationRoot != null)
        {
            foreach (Transform location in locationRoot) locationNames.Add(location.name);
        }

        List<string> selectedMbti = _agentToggleMap
            .Where(pair => pair.Key != null && pair.Key.isOn)
            .Select(pair => pair.Value.agentName)
            .ToList();

        if (selectedMbti.Count == 0)
        {
            UpdateStatusBar("錯誤：请至少选择一个代理人");
            return;
        }

        int eqStepValue = 5;
        if (eqStepDropdown != null && eqStepDropdown.options.Count > 0)
        {
            int.TryParse(eqStepDropdown.options[eqStepDropdown.value].text, out eqStepValue);
        }

        var parameters = new SimulationParameters
        {
            Duration = int.TryParse(durationInput.text, out int dur) ? dur : 2400,
            Step = int.TryParse(stepInput.text, out int step) ? step : 30,
            Year = int.TryParse(yearInput.text, out int year) ? year : 2024,
            Month = int.TryParse(monthInput.text, out int month) ? month : 11,
            Day = int.TryParse(dayInput.text, out int day) ? day : 18,
            Hour = int.TryParse(hourInput.text, out int hour) ? hour : 3,
            Minute = int.TryParse(minuteInput.text, out int min) ? min : 0,
            Mbti = selectedMbti,
            Locations = locationNames,
            EqEnabled = eqEnabledToggle != null ? eqEnabledToggle.isOn : true,
            EqJson = eqJsonInput != null ? eqJsonInput.text : "[]",
            EqStep = eqStepValue
        };

        HideSettingsPanels();
        simulationClient.StartSimulation(parameters);

        // 顯示被隱藏的 UI 元件
        if (logButtonGroup != null) logButtonGroup.SetActive(true);
        if (cameraButtonsPanel != null) cameraButtonsPanel.SetActive(true);
        if (statusBarText != null) statusBarText.gameObject.SetActive(true);
        ShowMainLogDisplay();
    }

    private void UpdateStatusBar(string status)
    {
        if (statusBarText != null) statusBarText.text = $"狀態: {status}";
    }

    private void UpdateLogs(UpdateData data)
    {
        if (mainLogView != null) mainLogView.AddLog(data.MainLog);
        if (historyLogView != null) historyLogView.AddLog(data.HistoryLog);
        if (llmLogView != null) llmLogView.AddLog(data.LlmLog);
    }

    /// <summary>
    /// 依時間範圍篩選顯示的日誌。
    /// </summary>
    public void FilterLogs(DateTime? start, DateTime? end)
    {
        mainLogView?.SetTimeFilter(start, end);
        historyLogView?.SetTimeFilter(start, end);
        llmLogView?.SetTimeFilter(start, end);
    }

    private void ShowMainLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(true);
        if (historyLogView != null) historyLogView.gameObject.SetActive(false);
        if (llmLogView != null) llmLogView.gameObject.SetActive(false);
    }

    private void ShowHistoryLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(false);
        if (historyLogView != null) historyLogView.gameObject.SetActive(true);
        if (llmLogView != null) llmLogView.gameObject.SetActive(false);
    }

    private void ShowLlmLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(false);
        if (historyLogView != null) historyLogView.gameObject.SetActive(false);
        if (llmLogView != null) llmLogView.gameObject.SetActive(true);
    }
    private void PopulateCameraButtonUI()
    {
        // 檢查必要的引用是否都已設定
        if (cameraButtonGroupParent == null || cameraButtonPrefab == null || cameraManager == null)
        {
            Debug.LogWarning("[UIController] 未設定攝影機按鈕的 Parent 或 Prefab，將略過自動生成。");
            return;
        }

        // 1. 清空容器，防止重複生成
        foreach (Transform child in cameraButtonGroupParent)
        {
            Destroy(child.gameObject);
        }

        // 2. 獲取所有可用的攝影機名稱
        //    (這需要我們先去 CameraManager 中新增一個公開的方法，見步驟三)
        IEnumerable<string> cameraNames = cameraManager.GetVirtualCameraNames();

        // 3. 為每一個攝影機名稱生成一個按鈕
        foreach (string camName in cameraNames)
        {
            GameObject buttonGO = Instantiate(cameraButtonPrefab, cameraButtonGroupParent);
            buttonGO.name = "Button_" + camName;

            // 設定按鈕上的文字
            TextMeshProUGUI buttonText = buttonGO.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                // 將 "VCAM_" 前綴去掉，讓按鈕文字更簡潔
                buttonText.text = camName.Replace("VCAM_", ""); 
            }

            // **核心：為按鈕添加點擊事件監聽器**
            Button button = buttonGO.GetComponent<Button>();
            if (button != null)
            {
                // 使用 Lambda 表達式來捕獲當前的攝影機名稱 (camName)
                // 這樣每個按鈕點擊時，都會傳遞它自己的攝影機名稱
                button.onClick.AddListener(() => 
                {
                    cameraManager.SwitchToCinemachineMode(camName);
                });
            }
        }

        // 4. 額外生成一個「自由模式」的按鈕
        GameObject freeLookButtonGO = Instantiate(cameraButtonPrefab, cameraButtonGroupParent);
        freeLookButtonGO.name = "Button_FreeLook";
        
        TextMeshProUGUI freeLookButtonText = freeLookButtonGO.GetComponentInChildren<TextMeshProUGUI>();
        if (freeLookButtonText != null)
        {
            freeLookButtonText.text = "手動模式 (F)";
        }

        Button freeLookButton = freeLookButtonGO.GetComponent<Button>();
        if (freeLookButton != null)
        {
            freeLookButton.onClick.AddListener(() =>
            {
                cameraManager.SwitchToFreeLookMode();
            });
        }
        
        Debug.Log($"[UIController] 已成功生成 {cameraNames.Count() + 1} 個攝影機控制按鈕。");
    }
}
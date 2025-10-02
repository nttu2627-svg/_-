// Scripts/UIController.cs (亂碼修正 + 繁體中文最終版)

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine.Events;

/// <summary>
/// 負責管理遊戲中的所有主要 UI 互動。
/// 包括：讀取模擬設定、動態生成代理人選項、觸發模擬開始、更新日誌，以及與攝影機管理器互動。
/// </summary>
public class UIController : MonoBehaviour
{
    [Header("核心依賴 (必須賦值)")]
    [Tooltip("場景中的 CameraManager 實例，用於控制攝影機")]
    public CameraManager cameraManager;
    [Tooltip("場景中的 CameraController 實例，用於手動跟隨")]
    public CameraController cameraController;
    [Tooltip("場景中的 SimulationClient 實例")]
    public SimulationClient simulationClient;
    [Tooltip("包含所有代理人 Prefab 實例的父物件")]
    public Transform characterRoot;
    [Tooltip("包含所有地點 Transform 的父物件 (例如 '內飾')")]
    public Transform locationRoot;

    [Header("UI 面板 (可選，用於隱藏)")]
    public GameObject timeSettingsPanel;
    public GameObject controlPanel;

    [Header("UI 輸入/輸出元件 (必須賦值)")]
    public TMP_InputField durationInput;
    public TMP_InputField stepInput;
    public TMP_InputField yearInput;
    public TMP_InputField monthInput;
    public TMP_InputField dayInput;
    public TMP_InputField hourInput;
    public TMP_InputField minuteInput;
    public Button startButton;

    [Header("動態生成UI所需 (必須賦值)")]
    [Tooltip("一個設計好的 Toggle Prefab，用於實例化")]
    public GameObject mbtiTogglePrefab;
    [Tooltip("用於容納所有動態生成的 Toggle 的父物件 (必須掛載佈局組件，如 Grid Layout Group)")]
    public Transform mbtiToggleGroupParent;

    [Header("顯示區域 (必須賦值)")]
    public TextMeshProUGUI statusBarText;
    public LogScrollView mainLogView;
    public LogScrollView historyLogView;
    public LogScrollView llmLogView;
    [Header("Log 容器 (可選)")]
    [Tooltip("LogPanels 的父物件；若不指定，將以分頁還原方式個別控制三個日誌視圖")]
    public GameObject logPanelsRoot;

    private enum LogTab { None, Main, History, Llm }
    private LogTab _lastLogTab = LogTab.None;
    [Header("日誌切換與其他按鈕 (可選)")]
    [Tooltip("包含三個日誌切換按鈕的父物件")]
    public GameObject logButtonGroup;
    [Tooltip("啟動前隱藏的攝影機按鈕面板")]
    public GameObject cameraButtonsPanel;
    public Button mainLogButton;
    public Button historyLogButton;
    public Button llmLogButton;
    // 原始按鈕顏色，用於切換時恢復
    private Color _mainLogButtonColor;
    private Color _historyLogButtonColor;
    private Color _llmLogButtonColor;
    private const float ActiveDarkenFactor = 0.8f;

    [Header("事件相關 UI (可選)")]
    public TMP_InputField eqJsonInput;
    public TMP_Dropdown eqStepDropdown;

    [Header("行事曆設定")]
    public Toggle useDefaultCalendarToggle;


    [Header("攝影機UI (可選, 用於自動生成)")]
    [Tooltip("用於容納所有動態生成的攝影機按鈕的父物件")]
    public Transform cameraButtonGroupParent;
    [Tooltip("一個設計好的攝影機按鈕 Prefab")]
    public GameObject cameraButtonPrefab;
    private Button _followAgentButton;
    private int _followAgentIndex = -1;
    private Button _uiToggleButton;
    private Color _uiToggleDefaultColor;
    private bool _isUIHidden = false;

    // 用於儲存 UI Toggle 和 AgentController 之間的對應關係
    private readonly Dictionary<Toggle, AgentController> _agentToggleMap = new Dictionary<Toggle, AgentController>();
    // 使用一個靜態布林值來確保在整個應用生命週期中，UI 只被生成一次
    private static bool _isUIPopulated = false;
    // 標記模擬是否已開始，用於控制運行時的 UI 互動
    private bool _simulationStarted = false;

    void Awake()
    {
        if (!ValidateDependencies())
        {
            Debug.LogError("UIController 已被禁用，因為缺少一個或多個關鍵的 Inspector 引用。請檢查 Console 中的錯誤訊息。", this);
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
        // 記錄按鈕的預設顏色
        if (mainLogButton != null) _mainLogButtonColor = mainLogButton.image.color;
        if (historyLogButton != null) _historyLogButtonColor = historyLogButton.image.color;
        if (llmLogButton != null) _llmLogButtonColor = llmLogButton.image.color;

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
        if (_followAgentButton != null) _followAgentButton.onClick.RemoveListener(CycleFollowAgent);
        if (_uiToggleButton != null) _uiToggleButton.onClick.RemoveListener(ToggleUIVisibility);
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
        if (cameraController == null) { Debug.LogError("UIController 錯誤: 'Camera Controller' 未賦值!", this); isValid = false; }
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
        Debug.Log("[UIController] 開始動態生成代理人選擇UI...");

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
    /// 當代理人選項的 Toggle 狀態改變時被調用。
    /// </summary>
    /// <param name="agent">對應的代理人控制器</param>
    /// <param name="isOn">Toggle 是否被選中</param>
    private void OnAgentToggleChanged(AgentController agent, bool isOn)
    {
        if (agent != null && _simulationStarted)
        {
            // 僅在模擬開始後，才允許 Toggle 控制代理人的可見性
            agent.gameObject.SetActive(isOn);
        }
    }

    /// <summary>
    /// 設定 UI 輸入欄位的預設值。
    /// </summary>
    private void SetDefaultValues()
    {
        if (durationInput != null) durationInput.text = "1200";
        if (stepInput != null) stepInput.text = "30";
        if (yearInput != null) yearInput.text = "2024";
        if (monthInput != null) monthInput.text = "11";
        if (dayInput != null) dayInput.text = "18";
        if (hourInput != null) hourInput.text = "3";
        if (minuteInput != null) minuteInput.text = "0";

        // 預設選中前三個代理人
        var toggles = _agentToggleMap.Keys.ToList();
        for (int i = 0; i < toggles.Count; i++)
        {
            if (i < 3) // 您可以修改這裡的數字來決定預設選中幾個
            {
                toggles[i].isOn = true;
            }
            else
            {
                toggles[i].isOn = false;
            }
        }
        if (useDefaultCalendarToggle != null) useDefaultCalendarToggle.isOn = true;
        if (eqJsonInput != null)
        {
            eqJsonInput.text = "[{\"time\": \"2024-11-18-11-00\", \"duration\": 30, \"intensity\": 0.75}]";
        } 
        if (eqStepDropdown != null)
        {
            int targetIndex = eqStepDropdown.options.FindIndex(option => option.text == "5");
            if (targetIndex != -1) eqStepDropdown.value = targetIndex;
        }
    }

    /// <summary>
    /// 隱藏設定相關的 UI 面板。
    /// </summary>
    private void HideSettingsPanels()
    {
        if (timeSettingsPanel != null) timeSettingsPanel.SetActive(false);
        if (controlPanel != null) controlPanel.SetActive(false);
        if (startButton != null) startButton.gameObject.SetActive(false);
        if (useDefaultCalendarToggle != null) useDefaultCalendarToggle.gameObject.SetActive(false);
    }

// 在 UIController.cs 中

    private void OnStartButtonClick()
    {

        var selectedAgents = _agentToggleMap
            .Where(pair => pair.Key != null && pair.Key.isOn)
            .Select(pair => pair.Value)
            .ToList();


       foreach (var agent in selectedAgents)
        {
            agent.gameObject.SetActive(true);
        }

        // 2. 執行傳送，並獲取傳送後的結果
        var teleportResults = TeleportAgentsToApartment(selectedAgents);
        var initialPositions = teleportResults ?? new Dictionary<string, string>();

        // 3. 準備其他參數
        HashSet<string> locationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Transform child in locationRoot)
        {
            locationNames.Add(child.name);

            if (string.Equals(child.name, "LocationMarkers", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Transform marker in child)
                {
                    locationNames.Add(marker.name);
                }
            }
        }

        if (teleportResults != null)
        {
            foreach (var kvp in teleportResults)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    locationNames.Add(kvp.Value);
                }
            }
        }

        List<string> locationNameList = locationNames.ToList();
        List<string> selectedMbti = selectedAgents.Select(a => a.agentName).ToList();
        int eqStepValue = 5;
        if (eqStepDropdown != null && eqStepDropdown.options.Count > 0)
        {
            int.TryParse(eqStepDropdown.options[eqStepDropdown.value].text, out eqStepValue);
        }
        
        string earthquakeJson = (eqJsonInput != null && !string.IsNullOrEmpty(eqJsonInput.text)) 
                            ? eqJsonInput.text 
                            : "[{\"time\": \"2024-11-18-11-00\", \"duration\": 30, \"intensity\": 0.75}]";

        bool shouldUseDefaultCalendar = useDefaultCalendarToggle != null ? useDefaultCalendarToggle.isOn : false;

        var parameters = new SimulationParameters
        {
            Duration = int.TryParse(durationInput.text, out int dur) ? dur : 1200,
            Step = int.TryParse(stepInput.text, out int step) ? step : 30,
            Year = int.TryParse(yearInput.text, out int year) ? year : 2024,
            Month = int.TryParse(monthInput.text, out int month) ? month : 11,
            Day = int.TryParse(dayInput.text, out int day) ? day : 18,
            Hour = int.TryParse(hourInput.text, out int hour) ? hour : 3,
            Minute = int.TryParse(minuteInput.text, out int min) ? min : 0,
            Mbti = selectedMbti,
            Locations = locationNameList,
            EqEnabled = true,
            EqJson = earthquakeJson,
            EqStep = eqStepValue,
            UseDefaultCalendar = shouldUseDefaultCalendar,
            InitialPositions = initialPositions

        };

        HideSettingsPanels();
        simulationClient.StartSimulation(parameters);
        _simulationStarted = true;

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
        string main = CleanLog(data.MainLog);
        string history = CleanLog(data.HistoryLog);
        string llm = CleanLog(data.LlmLog);

        if (mainLogView != null)
        {
            mainLogView.SetSingle(main);
        }
        if (historyLogView != null) historyLogView.SetSingle(history);
        if (llmLogView != null) llmLogView.SetSingle(llm);
    }

    private static string CleanLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return System.Text.RegularExpressions.Regex.Replace(
            s,
            "<think>[\\s\\S]*?</think>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ).Trim();
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
    private void HighlightActiveLogButton(Button active)
    {
        if (mainLogButton != null) mainLogButton.image.color = _mainLogButtonColor;
        if (historyLogButton != null) historyLogButton.image.color = _historyLogButtonColor;
        if (llmLogButton != null) llmLogButton.image.color = _llmLogButtonColor;
        if (active != null) active.image.color *= ActiveDarkenFactor;
    }

    private void ShowMainLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(true);
        if (historyLogView != null) historyLogView.gameObject.SetActive(false);
        if (llmLogView != null) llmLogView.gameObject.SetActive(false);
        HighlightActiveLogButton(mainLogButton);
    }

    private void ShowHistoryLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(false);
        if (historyLogView != null) historyLogView.gameObject.SetActive(true);
        if (llmLogView != null) llmLogView.gameObject.SetActive(false);
        HighlightActiveLogButton(historyLogButton);
    }

    private void ShowLlmLogDisplay()
    {
        if (mainLogView != null) mainLogView.gameObject.SetActive(false);
        if (historyLogView != null) historyLogView.gameObject.SetActive(false);
        if (llmLogView != null) llmLogView.gameObject.SetActive(true);
        HighlightActiveLogButton(llmLogButton);
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
        IEnumerable<string> cameraNames = cameraManager.GetVirtualCameraNames();

        // 3. 為每一個攝影機名稱生成一個按鈕
        foreach (string camName in cameraNames)
        {
            if (camName.Equals("VCAM_FOLLOW", StringComparison.OrdinalIgnoreCase)) continue;

            GameObject buttonGO = Instantiate(cameraButtonPrefab, cameraButtonGroupParent);
            buttonGO.name = "Button_" + camName;

            TextMeshProUGUI buttonText = buttonGO.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                if (camName.Equals("VCAM_FOLLOWAGENT", StringComparison.OrdinalIgnoreCase))
                    buttonText.text = "FOLLOW AGENT";
                else
                    buttonText.text = camName.Replace("VCAM_", "");
            }

            Button button = buttonGO.GetComponent<Button>();
            if (button != null)
            {
                if (camName.Equals("VCAM_FOLLOWAGENT", StringComparison.OrdinalIgnoreCase))
                {
                    _followAgentButton = button;
                    button.onClick.AddListener(CycleFollowAgent);
                }
                else
                {
                    button.onClick.AddListener(() =>
                    {
                        cameraManager.SwitchToCinemachineMode(camName);
                    });
                }
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


        // 5. 隱藏狀態欄按鈕
        GameObject hideStatusButtonGO = Instantiate(cameraButtonPrefab, cameraButtonGroupParent);
        hideStatusButtonGO.name = "Button_ToggleStatus";
        TextMeshProUGUI hideStatusText = hideStatusButtonGO.GetComponentInChildren<TextMeshProUGUI>();
        if (hideStatusText != null) hideStatusText.text = "隱藏狀態";
        Button hideStatusButton = hideStatusButtonGO.GetComponent<Button>();
        if (hideStatusButton != null) hideStatusButton.onClick.AddListener(ToggleStatusBar);

        // 6. 隱藏所有 UI 按鈕
        GameObject hideUiButtonGO = Instantiate(cameraButtonPrefab, cameraButtonGroupParent);
        hideUiButtonGO.name = "Button_ToggleUI";
        TextMeshProUGUI hideUiText = hideUiButtonGO.GetComponentInChildren<TextMeshProUGUI>();
        if (hideUiText != null) hideUiText.text = "隱藏 UI";
        _uiToggleButton = hideUiButtonGO.GetComponent<Button>();
        if (_uiToggleButton != null)
        {
            _uiToggleDefaultColor = _uiToggleButton.image.color;
            _uiToggleButton.onClick.AddListener(ToggleUIVisibility);
        }

        Debug.Log($"[UIController] 已成功生成 {cameraNames.Count() + 3} 個攝影機控制按鈕。");
    }

    private void ToggleStatusBar()
    {
        if (statusBarText != null)
        {
            bool active = statusBarText.gameObject.activeSelf;
            statusBarText.gameObject.SetActive(!active);
        }
    }

    private void ToggleUIVisibility()
    {
        _isUIHidden = !_isUIHidden;

        if (logButtonGroup != null) logButtonGroup.SetActive(!_isUIHidden);
        if (statusBarText != null) statusBarText.gameObject.SetActive(!_isUIHidden);

        foreach (Transform child in cameraButtonGroupParent)
        {
            if (_uiToggleButton != null && child == _uiToggleButton.transform) continue;
            child.gameObject.SetActive(!_isUIHidden);
        }
        if (_isUIHidden)
        {
            // 記住目前是哪一頁在顯示（僅在未指定父物件時需要）
            if (logPanelsRoot == null)
            {
                _lastLogTab = LogTab.None;
                if (mainLogView != null && mainLogView.gameObject.activeSelf) _lastLogTab = LogTab.Main;
                else if (historyLogView != null && historyLogView.gameObject.activeSelf) _lastLogTab = LogTab.History;
                else if (llmLogView != null && llmLogView.gameObject.activeSelf) _lastLogTab = LogTab.Llm;

                if (mainLogView != null) mainLogView.gameObject.SetActive(false);
                if (historyLogView != null) historyLogView.gameObject.SetActive(false);
                if (llmLogView != null) llmLogView.gameObject.SetActive(false);
            }
            else
            {
                logPanelsRoot.SetActive(false);
            }
        }
        else
        {
            if (logPanelsRoot == null)
            {
                // 沒有父容器就還原到剛剛那一頁
                switch (_lastLogTab)
                {
                    case LogTab.Main: ShowMainLogDisplay(); break;
                    case LogTab.History: ShowHistoryLogDisplay(); break;
                    case LogTab.Llm: ShowLlmLogDisplay(); break;
                    default: ShowMainLogDisplay(); break;
                }
            }
            else
            {
                logPanelsRoot.SetActive(true);
            }
        }
        if (_uiToggleButton != null)
        {
            var img = _uiToggleButton.image;
            if (img != null) img.color = _isUIHidden ? Color.red : _uiToggleDefaultColor;
        }

    }

    private void CycleFollowAgent()
    {
        var agents = _agentToggleMap.Where(p => p.Key != null && p.Key.isOn)
            .Select(p => p.Value).ToList();
        if (agents.Count == 0 || cameraManager == null) return;

        _followAgentIndex = (_followAgentIndex + 1) % agents.Count;
        cameraManager.SetFollowTarget(agents[_followAgentIndex].transform);
    }
    private Transform FindLocationMarker(string markerName)
    {
        if (locationRoot == null || string.IsNullOrEmpty(markerName)) return null;

        Transform markersRoot = null;
        foreach (Transform child in locationRoot)
        {
            if (string.Equals(child.name, "LocationMarkers", StringComparison.OrdinalIgnoreCase))
            {
                markersRoot = child;
                break;
            }
        }

        if (markersRoot != null)
        {
            Transform marker = FindChildRecursive(markersRoot, markerName);
            if (marker != null) return marker;
        }

        return FindChildRecursive(locationRoot, markerName);
    }
    private Collider2D FindBoundsCollider(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;

        string normalized = pathOrName.Replace("\\", "/");
        GameObject target = GameObject.Find(normalized);
        if (target != null)
        {
            return target.GetComponent<Collider2D>();
        }

        string nameOnly = normalized.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(nameOnly)) return null;

        foreach (var col in FindObjectsOfType<Collider2D>())
        {
            if (col != null && string.Equals(col.gameObject.name, nameOnly, StringComparison.OrdinalIgnoreCase))
            {
                return col;
            }
        }

        return null;
    }

    private List<Vector3> GenerateSpawnPositionsWithin(Collider2D areaCollider, Transform fallback, int count)
    {
        var positions = new List<Vector3>();
        if (count <= 0) return positions;

        Vector3 referencePosition = fallback != null
            ? fallback.position
            : (areaCollider != null ? (Vector3)areaCollider.bounds.center : Vector3.zero);
        Bounds bounds = areaCollider != null
            ? areaCollider.bounds
            : new Bounds(referencePosition, Vector3.one * 6f);

        const float minDistance = 1.4f;
        const float collisionRadius = 0.45f;
        int maxAttempts = Mathf.Max(40, count * 40);
        int attempts = 0;

        while (positions.Count < count && attempts < maxAttempts)
        {
            attempts++;
            Vector2 candidate = areaCollider != null
                ? new Vector2(
                    UnityEngine.Random.Range(bounds.min.x, bounds.max.x),
                    UnityEngine.Random.Range(bounds.min.y, bounds.max.y))
                : (Vector2)referencePosition + UnityEngine.Random.insideUnitCircle * Mathf.Max(bounds.extents.x, bounds.extents.y);

            if (areaCollider != null && !areaCollider.OverlapPoint(candidate)) continue;
            if (positions.Any(p => Vector2.Distance(p, candidate) < minDistance)) continue;

            Collider2D hit = Physics2D.OverlapCircle(candidate, collisionRadius);
            if (hit != null && (areaCollider == null || (hit != areaCollider && !hit.isTrigger))) continue;

            positions.Add(new Vector3(candidate.x, candidate.y, referencePosition.z));
        }

        if (positions.Count < count)
        {
            int gridSize = Mathf.CeilToInt(Mathf.Sqrt(count));
            float spacing = minDistance;
            int safety = 0;

            while (positions.Count < count && safety < gridSize * gridSize * 2)
            {
                int row = safety / gridSize;
                int col = safety % gridSize;
                safety++;

                Vector2 candidate = (Vector2)referencePosition + new Vector2(
                    (col - (gridSize - 1) * 0.5f) * spacing,
                    (row - (gridSize - 1) * 0.5f) * spacing);

                if (areaCollider != null && !areaCollider.OverlapPoint(candidate)) continue;
                if (positions.Any(p => Vector2.Distance(p, candidate) < minDistance)) continue;

                positions.Add(new Vector3(candidate.x, candidate.y, referencePosition.z));
            }
        }

        return positions;
    }

    private Transform FindChildRecursive(Transform parent, string targetName)
    {
        if (parent == null) return null;

        foreach (Transform child in parent)
        {
            if (string.Equals(child.name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }

            Transform result = FindChildRecursive(child, targetName);
            if (result != null) return result;
        }

        return null;
    }

    private string ResolveApartmentLocationName(Transform marker, string fallback)
    {
        if (!string.IsNullOrEmpty(marker.name))
        {
            return LocationNameLocalizer.ToDisplayName(marker.name);
        }

        return LocationNameLocalizer.ToDisplayName(fallback);
    }

    /// <summary>
    /// 在模擬開始時，將選定的代理人傳送到公寓的 F1 或 F2。
    /// 一樓最多8人，超過的會被送到二樓。
    /// </summary>

    private Dictionary<string, string> TeleportAgentsToApartment(List<AgentController> agents)
    {
        var positions = new Dictionary<string, string>();
        if (agents == null || agents.Count == 0) return positions;

        Transform f1 = FindLocationMarker("Apartment_F1");
        Transform f2 = FindLocationMarker("Apartment_F2");
        Collider2D f1Bounds = FindBoundsCollider("Environment/PhysicsColliders/InteriorBounds/公寓_一樓Bounds");
        Collider2D f2Bounds = FindBoundsCollider("Environment/PhysicsColliders/InteriorBounds/公寓_二樓Bounds");

        if (f1 == null && f1Bounds == null)
        {
            Debug.LogError("[傳送失敗] 找不到名為 'Apartment_F1' 的地點標記或邊界！");
            return positions;
        }
        if (f2 == null)
        {
            Debug.LogWarning("[傳送警告] 找不到名為 'Apartment_F2' 的地點標記或邊界。所有代理人都會被傳送到 F1。");
        }

        Debug.Log($"[傳送] F1 位置: {(f1 != null ? f1.position.ToString() : "未找到")}, F2 位置: {(f2 != null ? f2.position.ToString() : "未找到")}");

        int firstFloorQuota = Mathf.Min(8, agents.Count);
        var firstFloorPositions = GenerateSpawnPositionsWithin(f1Bounds, f1, firstFloorQuota);
        var secondFloorPositions = GenerateSpawnPositionsWithin(f2Bounds, f2, Mathf.Max(0, agents.Count - firstFloorPositions.Count));

        int firstIndex = 0;
        int secondIndex = 0;

        for (int i = 0; i < agents.Count; i++)
        {
            AgentController controller = agents[i];
            Vector3 targetPosition;
            Transform baseTransform = null;

            if (firstIndex < firstFloorPositions.Count)
            {
                targetPosition = firstFloorPositions[firstIndex++];
                baseTransform = f1;
            }
            else if (secondIndex < secondFloorPositions.Count)
            {
                targetPosition = secondFloorPositions[secondIndex++];
                baseTransform = f2;
            }
            else
            {
                baseTransform = (i < 8 || f2 == null) ? f1 : f2;
                Vector3 fallbackOrigin = baseTransform != null ? baseTransform.position : Vector3.zero;
                targetPosition = fallbackOrigin + (Vector3)(UnityEngine.Random.insideUnitCircle * 1.5f);
            }

            string locationLabel = ResolveApartmentLocationName(baseTransform, "公寓");

            var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Apartment",
                "公寓",
                "Apartment_F1",
                "Apartment_F2",

            };

            LocationNameLocalizer.AppendDisplayAlias(aliasSet, "Apartment");
            LocationNameLocalizer.AppendDisplayAlias(aliasSet, "Apartment_F1");
            LocationNameLocalizer.AppendDisplayAlias(aliasSet, "Apartment_F2");
            LocationNameLocalizer.AppendDisplayAlias(aliasSet, locationLabel);

            controller.TeleportTo(targetPosition, aliasSet.ToArray());
            positions[controller.agentName] = LocationNameLocalizer.ToDisplayName(locationLabel);
            Debug.Log($"[傳送] 已將 '{controller.name}' 傳送到 {targetPosition}");
        }
        return positions;
    }
}
    

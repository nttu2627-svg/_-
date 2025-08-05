// Scripts/UIController.cs (最终完整版)

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;

public class UIController : MonoBehaviour
{
    [Header("核心依賴 (必须赋值!)")]
    [Tooltip("场景中的 SimulationClient 实例")]
    public SimulationClient simulationClient;
    [Tooltip("包含所有代理人 Prefab 实例的父物件")]
    public Transform characterRoot;
    [Tooltip("包含所有地点 Transform 的父物件 (例如 '內飾')")]
    public Transform locationRoot;

    [Header("UI 面板 (可选，用于隐藏)")]
    public GameObject timeSettingsPanel;
    public GameObject controlPanel;
    public GameObject startbutton;

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
    public TMP_InputField mainLogText;
    public TMP_InputField historyLogText;
    public TMP_InputField llmLogText;

    [Header("事件相关 UI (可选)")]
    public Toggle eqEnabledToggle;
    public TMP_InputField eqJsonInput;
    public TMP_Dropdown eqStepDropdown;

    // 用于存储 UI Toggle 和 AgentController 之间的映射关系
    private readonly Dictionary<Toggle, AgentController> _agentToggleMap = new Dictionary<Toggle, AgentController>();

    // 使用一个静态布尔值来确保在整个应用生命周期中，UI 只被生成一次
    private static bool _isUIPopulated = false;

    void Awake()
    {
        // 确保所有必要的引用都已在 Inspector 中设置
        if (!ValidateDependencies())
        {
            Debug.LogError("UIController 已被禁用，因为缺少一个或多个关键的 Inspector 引用。请检查 Console 中的错误讯息。");
            this.enabled = false; // 如果缺少关键引用，则禁用此脚本
            return;
        }

        // 将生成逻辑放在 Awake 中，确保它在 Start 之前执行，并且只执行一次
        PopulateAgentSelectionUI();
    }

    void Start()
    {
        if (startButton != null)
        {
            startButton.onClick.AddListener(OnStartButtonClick);
        }

        // 在所有 Toggle 都生成完毕后，再设置默认选中的状态
        SetDefaultValues();

        SimulationClient.OnStatusUpdate += UpdateStatusBar;
        SimulationClient.OnLogUpdate += UpdateLogs;
    }

    private bool ValidateDependencies()
    {
        bool isValid = true;
        if (simulationClient == null) { Debug.LogError("UIController 错误: 'Simulation Client' 未赋值!", this); isValid = false; }
        if (characterRoot == null) { Debug.LogError("UIController 错误: 'Character Root' 未赋值!", this); isValid = false; }
        if (locationRoot == null) { Debug.LogError("UIController 错误: 'Location Root' 未赋值!", this); isValid = false; }
        if (mbtiTogglePrefab == null) { Debug.LogError("UIController 错误: 'Mbti Toggle Prefab' 未赋值!", this); isValid = false; }
        if (mbtiToggleGroupParent == null) { Debug.LogError("UIController 错误: 'Mbti Toggle Group Parent' 未赋值!", this); isValid = false; }
        if (startButton == null) { Debug.LogError("UIController 错误: 'Start Button' 未赋值!", this); isValid = false; }
        // 您可以为其他“必须赋值”的栏位添加更多检查
        return isValid;
    }

    void OnDestroy()
    {
        if (startButton != null) startButton.onClick.RemoveListener(OnStartButtonClick);
        SimulationClient.OnStatusUpdate -= UpdateStatusBar;
        SimulationClient.OnLogUpdate -= UpdateLogs;

        // 取消所有动态添加的监听器，防止内存泄漏
        foreach (var toggle in _agentToggleMap.Keys)
        {
            if (toggle != null) { toggle.onValueChanged.RemoveAllListeners(); }
        }
    }

    private void PopulateAgentSelectionUI()
    {
        // 使用静态哨兵变量进行检查
        if (_isUIPopulated) return;

        Debug.Log("[UIController] 开始动态生成代理人选择UI...");

        // 在生成前，先彻底清空容器内的所有旧物件
        foreach (Transform child in mbtiToggleGroupParent) { Destroy(child.gameObject); }
        _agentToggleMap.Clear();

        ToggleGroup toggleGroup = mbtiToggleGroupParent.GetComponent<ToggleGroup>();
        var agentsInScene = characterRoot.GetComponentsInChildren<AgentController>(true)
            .Select(a =>
            {
                var n = string.IsNullOrEmpty(a.agentName) ? a.gameObject.name : a.agentName;
                a.agentName = n.Trim().ToUpper();
                return a;
            })
            .GroupBy(a => a.agentName)       // 同名只留一個
            .Select(g => g.First())
            .OrderBy(a => a.agentName)
            .ToList();

        // 偵測重複並提示
        foreach (var g in characterRoot.GetComponentsInChildren<AgentController>(true)
                                    .GroupBy(a => (string.IsNullOrEmpty(a.agentName) ? a.gameObject.name : a.agentName).Trim().ToUpper()))
        {
            if (g.Count() > 1)
                Debug.LogWarning($"偵測到重複代理人：{g.Key}，數量 {g.Count()}。只會建立一個 Toggle。", g.First());
        }

        foreach (AgentController agent in agentsInScene)
        {
            string agentName = string.IsNullOrEmpty(agent.agentName) ? agent.gameObject.name : agent.agentName;

            GameObject toggleGO = Instantiate(mbtiTogglePrefab, mbtiToggleGroupParent);
            toggleGO.name = agentName;

            Toggle toggle = toggleGO.GetComponent<Toggle>();
            TextMeshProUGUI label = toggleGO.GetComponentInChildren<TextMeshProUGUI>();

            if (toggle != null && label != null)
            {
                label.text = agentName;
                if (toggleGroup != null) { toggle.group = toggleGroup; }
                toggle.onValueChanged.AddListener((isOn) => { OnAgentToggleChanged(agent, isOn); });
                _agentToggleMap.Add(toggle, agent);
                agent.gameObject.SetActive(false); // 确保初始状态为隐藏
            }
            else
            {
                Debug.LogError($"为 {agentName} 创建的 Toggle Prefab 实例结构不正确！请检查 Prefab 是否包含 Toggle 组件和 TextMeshProUGUI 子物件。");
            }
        }

        _isUIPopulated = true;
        Debug.Log($"[UIController] 已成功生成 {_agentToggleMap.Count} 个代理人选项。");
    }

    private void OnAgentToggleChanged(AgentController agent, bool isOn)
    {
        if (agent != null)
        {
            agent.gameObject.SetActive(isOn);
        }
    }

    private void SetDefaultValues()
    {
        if (durationInput != null) durationInput.text = "2400";
        if (stepInput != null) stepInput.text = "30";
        if (yearInput != null) yearInput.text = "2024";
        if (monthInput != null) monthInput.text = "11";
        if (dayInput != null) dayInput.text = "18";
        if (hourInput != null) hourInput.text = "3";
        if (minuteInput != null) minuteInput.text = "0";

        var toggles = _agentToggleMap.Keys.ToList();
        for (int i = 0; i < toggles.Count; i++)
        {
            toggles[i].isOn = i < 2;
        }

        if (eqEnabledToggle != null) eqEnabledToggle.isOn = true;
        if (eqJsonInput != null) eqJsonInput.text = "[{\"time\": \"2024-11-18-11-00\", \"duration\": 30, \"intensity\": 0.75}]";
        if (eqStepDropdown != null)
        {
            int targetIndex = eqStepDropdown.options.FindIndex(option => option.text == "5");
            if (targetIndex != -1) eqStepDropdown.value = targetIndex;
        }
    }

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
            UpdateStatusBar("错误：请至少选择一个代理人");
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
    }

    private void HideSettingsPanels()
    {
        if (timeSettingsPanel != null) timeSettingsPanel.SetActive(false);
        if (controlPanel != null) controlPanel.SetActive(false);
        if (startbutton != null) startbutton.gameObject.SetActive(false);
    }

    private void UpdateStatusBar(string status)
    {
        if (statusBarText != null) statusBarText.text = $"狀態: {status}";
    }

    private void UpdateLogs(UpdateData data)
    {
        if (mainLogText != null) mainLogText.text = data.MainLog;
        if (historyLogText != null) historyLogText.text = data.HistoryLog;
        if (llmLogText != null) llmLogText.text = data.LlmLog;
    }
    
}
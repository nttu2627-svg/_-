// Scripts/SceneController.cs (最终健壮版)
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SceneController : MonoBehaviour
{
    [Header("场景物件根节点 (必须赋值!)")]
    [Tooltip("包含所有室外装饰物件的父物件 (例如 '城鎮外觀')")]
    public GameObject exteriorRoot;
    [Tooltip("包含所有室内地点物件的父物件 (例如 '內飾')")]
    public GameObject interiorRoot;

    [Header("地点定义")]
    [Tooltip("请在此处输入所有明确属于 '室外' 的地点名称")]
    public List<string> exteriorLocationNames = new List<string> { "Park", "海邊", "綠道" };

    private bool _isShowingInterior = false;
    private HashSet<string> _exteriorLocationSet;

    void Awake()
    {
        _exteriorLocationSet = new HashSet<string>(exteriorLocationNames);
        
        // ### 核心修正：在游戏一开始就强制设定正确的初始状态 ###
        // 确保 interiorRoot 被正确引用，否则无法继续
        if (interiorRoot == null || exteriorRoot == null)
        {
            Debug.LogError("[SceneController] Exterior Root 或 Interior Root 未在 Inspector 中赋值！场景切换功能将失效。");
            this.enabled = false; // 禁用此脚本
            return;
        }
        
        // 游戏开始时，默认显示室外，隐藏室内
        ShowExterior();
    }

    void Start()
    {
        SimulationClient.OnLogUpdate += HandleSimulationUpdate;
    }

    void OnDestroy()
    {
        SimulationClient.OnLogUpdate -= HandleSimulationUpdate;
    }

    private void HandleSimulationUpdate(UpdateData updateData)
    {
        Dictionary<string, AgentState> agentStates = updateData.AgentStates;
        if (agentStates == null || agentStates.Count == 0) return;
        
        bool anyAgentInside = agentStates.Values.Any(state => IsInteriorLocation(state.Location));

        if (anyAgentInside && !_isShowingInterior)
        {
            ShowInterior();
        }
        else if (!anyAgentInside && _isShowingInterior)
        {
            ShowExterior();
        }
    }

    private bool IsInteriorLocation(string location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        return !_exteriorLocationSet.Contains(location);
    }

    private void ShowInterior()
    {
        // Debug.Log("[SceneController] 切换到室内视图");
        exteriorRoot.SetActive(false);
        interiorRoot.SetActive(true);
        _isShowingInterior = true;
    }

    private void ShowExterior()
    {
        // Debug.Log("[SceneController] 切换到室外视图");
        exteriorRoot.SetActive(true);
        interiorRoot.SetActive(false);
        _isShowingInterior = false;
    }
}
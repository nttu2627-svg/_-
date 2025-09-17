// Scripts/SceneController.cs (最终健壮版)
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class SceneController : MonoBehaviour
{

    [Header("室外定義")]
    [Tooltip("请在此处输入所有明确属于 '室外' 的地点名称")]
    public List<string> exteriorLocationNames = new List<string> { "公園", "地鐵路口", "馬路" };

    private bool _isShowingInterior = false;
    private HashSet<string> _exteriorLocationSet;

    void Awake()
    {
        _exteriorLocationSet = new HashSet<string>(exteriorLocationNames);
        
        // ### 核心修正：在游戏一开始就强制设定正确的初始状态 ###
        // 确保 interiorRoot 被正确引用，否则无法继续
        
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
            _isShowingInterior = true;
        }
        else if (!anyAgentInside && _isShowingInterior)
        {
            _isShowingInterior = false;
        }
    }

    private bool IsInteriorLocation(string location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        return !_exteriorLocationSet.Contains(location);
    }


}
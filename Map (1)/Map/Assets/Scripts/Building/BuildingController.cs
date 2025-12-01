// Scripts/Building/BuildingController.cs

using UnityEngine;

public class BuildingController : MonoBehaviour
{
    [Tooltip("建築物名稱")]
    public string buildingName;

    [Header("Damage Models")]
    [Tooltip("正常狀態模型")]
    public GameObject normalState;
    [Tooltip("受損狀態模型")]
    public GameObject damagedState;
    [Tooltip("毀壞狀態模型")]
    public GameObject destroyedState;
    void Start()
    {
        if (string.IsNullOrEmpty(buildingName))
        {
            buildingName = gameObject.name;
        }
    }

    /// <summary>
    /// 從 SimulationClient 調用此方法以更新建築物的狀態
    /// </summary>
    /// <param name="state">來自後端的建築物狀態數據</param>
    public void UpdateState(BuildingState state)
    {
        if (normalState != null && damagedState != null && destroyedState != null)
        {
             float integrity = state.Integrity;

            if (integrity < 40f)
            {
                normalState.SetActive(false);
                damagedState.SetActive(false);
                destroyedState.SetActive(true);
            }
            else if (integrity < 75f)
            {
                normalState.SetActive(false);
                damagedState.SetActive(true);
                destroyedState.SetActive(false);
            }
            else
            {
                normalState.SetActive(true);
                damagedState.SetActive(false);
                destroyedState.SetActive(false);
            }
        }
    }
}
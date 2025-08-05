// Scripts/Building/BuildingController.cs

using UnityEngine;

public class BuildingController : MonoBehaviour
{
    public string buildingName;

    // 可以在這裡引用建築物的不同部分，用於顯示損壞效果
    // 例如：public GameObject normalState;
    //       public GameObject damagedState;
    //       public GameObject destroyedState;

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
        // 根據損壞度更新視覺效果
        float integrity = state.Integrity;

        // 範例：根據損壞度顯示不同狀態的模型
        // if (integrity < 40) {
        //     normalState.SetActive(false);
        //     damagedState.SetActive(false);
        //     destroyedState.SetActive(true);
        // } else if (integrity < 75) {
        //     normalState.SetActive(false);
        //     damagedState.SetActive(true);
        //     destroyedState.SetActive(false);
        // } else {
        //     normalState.SetActive(true);
        //     damagedState.SetActive(false);
        //     destroyedState.SetActive(false);
        // }

        // 或者，你也可以用 particle system 來模擬煙霧等效果
        // Debug.Log($"Building '{buildingName}' integrity updated to {integrity}%");
    }
}
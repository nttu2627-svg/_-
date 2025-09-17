// Scripts/World/PortalController.cs
using UnityEngine;

public class PortalController : MonoBehaviour
{
    [Tooltip("這個傳送門可傳送到的目標傳送門名字列表 (在 Hierarchy 中的名字)")]
    public string[] targetPortalNames;

    public string targetPortalName
    {
        get
        {
            if (targetPortalNames == null || targetPortalNames.Length == 0)
            {
                Debug.LogWarning("傳送門未配置目標傳送門");
                return string.Empty;
            }

            int index = Random.Range(0, targetPortalNames.Length);
            return targetPortalNames[index];
        }
    }   
}
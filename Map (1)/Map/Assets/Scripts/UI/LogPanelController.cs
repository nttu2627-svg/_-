using UnityEngine;

namespace DisasterSimulation
{
    /// <summary>
    /// 控制 Log Panel 顯示或隱藏。
    /// 可透過按鈕呼叫 <see cref="ToggleLog"/> 方法切換顯示狀態。
    /// </summary>
    public class LogPanelController : MonoBehaviour
    {
        [SerializeField]
        private GameObject logPanel;

        private void Reset()
        {
            if (logPanel == null)
            {
                logPanel = gameObject;
            }
        }

        /// <summary>
        /// 切換 Log Panel 的顯示或隱藏。
        /// </summary>
        public void ToggleLog()
        {
            if (logPanel != null)
            {
                logPanel.SetActive(!logPanel.activeSelf);
            }
        }
    }
}
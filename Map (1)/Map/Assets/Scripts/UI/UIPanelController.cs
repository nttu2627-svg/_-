using UnityEngine;

namespace DisasterSimulation
{
    /// <summary>
    /// 控制 UI 面板的顯示與隱藏。
    /// 掛載於具有 CanvasGroup 的面板上。
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class 面板控制器 : MonoBehaviour
    {
        private CanvasGroup _canvasGroup;

        public bool 是否可見 => _canvasGroup.alpha > 0.1f;

        private void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
        }

        /// <summary>
        /// 切換面板顯示狀態。
        /// </summary>
        public void 切換()
        {
            設定可見(!是否可見);
        }

        /// <summary>
        /// 設定面板是否可見。
        /// </summary>
        /// <param name="顯示">true 表示顯示，false 表示隱藏</param>
        public void 設定可見(bool 顯示)
        {
            _canvasGroup.alpha = 顯示 ? 1f : 0f;
            _canvasGroup.interactable = 顯示;
            _canvasGroup.blocksRaycasts = 顯示;
        }
    }
}
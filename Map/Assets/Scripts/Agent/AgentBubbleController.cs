using System.Collections;
using TMPro;
using UnityEngine;

namespace DisasterSimulation
{
    /// <summary>
    /// 控制代理人頭頂的思考/行動氣泡顯示。
    /// 此腳本應掛載在世界空間 Canvas 上，並包含 TextMeshProUGUI。
    /// </summary>
    public class 思考氣泡控制器 : MonoBehaviour
    {
        [Header("UI 組件")]
        public CanvasGroup 氣泡CanvasGroup;
        public TextMeshProUGUI 氣泡文字;

        [Header("外觀設定")]
        public float 顯示時間 = 3f;
        public Vector3 偏移 = new Vector3(0f, 2f, 0f);

        private Coroutine 隱藏協程;
        private Transform 目標;

        private void LateUpdate()
        {
            // 若有目標，讓氣泡面向主攝影機
            if (目標 != null)
            {
                transform.position = 目標.position + 偏移;
                var cam = Camera.main;
                if (cam != null)
                {
                    transform.LookAt(transform.position + cam.transform.rotation * Vector3.forward,
                                     cam.transform.rotation * Vector3.up);
                }
            }
        }

        /// <summary>
        /// 設定氣泡文字並顯示。
        /// </summary>
        /// <param name="內容">顯示內容</param>
        /// <param name="跟隨目標">要跟隨的物件（通常是代理人）</param>
        public void 顯示氣泡(string 內容, Transform 跟隨目標)
        {
            目標 = 跟隨目標;
            氣泡文字.text = 內容;
            氣泡CanvasGroup.alpha = 1f;
            氣泡CanvasGroup.interactable = true;
            氣泡CanvasGroup.blocksRaycasts = true;
            if (隱藏協程 != null)
            {
                StopCoroutine(隱藏協程);
            }
            隱藏協程 = StartCoroutine(延遲隱藏());
        }

        private IEnumerator 延遲隱藏()
        {
            yield return new WaitForSeconds(顯示時間);
            // 漸隱
            float t = 0f;
            float 起始透明度 = 氣泡CanvasGroup.alpha;
            while (t < 1f)
            {
                t += Time.deltaTime;
                氣泡CanvasGroup.alpha = Mathf.Lerp(起始透明度, 0f, t);
                yield return null;
            }
            氣泡CanvasGroup.interactable = false;
            氣泡CanvasGroup.blocksRaycasts = false;
        }
    }
}
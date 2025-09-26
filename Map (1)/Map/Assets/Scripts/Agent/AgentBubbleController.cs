using System.Collections;
using TMPro;
using UnityEngine;

namespace DisasterSimulation
{
    /// 控制代理人頭頂的思考/行動氣泡顯示（世界空間 Canvas）
    [RequireComponent(typeof(CanvasGroup))]
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

        private void Awake()
        {
            // 自動補齊參考，避免 Inspector 漏綁
            if (!氣泡CanvasGroup) 氣泡CanvasGroup = GetComponent<CanvasGroup>();
            if (!氣泡文字)
            {
                氣泡文字 = GetComponentInChildren<TextMeshProUGUI>(true);
                if (!氣泡文字)
                    Debug.LogWarning("[思考氣泡控制器] 場景中找不到 TextMeshProUGUI，請綁定。", this);
            }

            // 初始為隱藏狀態
            SetVisible(false, instant: true);
        }

        private void OnDisable()
        {
            // 物件被關閉時，停止所有協程與狀態歸零，避免殘留
            if (隱藏協程 != null)
            {
                StopCoroutine(隱藏協程);
                隱藏協程 = null;
            }
            SetVisible(false, instant: true);
        }

        private void LateUpdate()
        {
            if (!目標) return;

            // 位置與面向主相機
            transform.position = 目標.position + 偏移;
            var cam = Camera.main;
            if (cam)
            {
                transform.LookAt(transform.position + cam.transform.rotation * Vector3.forward,
                                 cam.transform.rotation * Vector3.up);
            }
        }

        /// 設定文字並顯示（安全：會在必要時自動啟用物件）
        public void 顯示氣泡(string 內容, Transform 跟隨目標)
        {
            目標 = 跟隨目標;

            // 若物件或這個腳本被關閉，先把它打開，之後再啟動協程
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!enabled) enabled = true;

            if (氣泡文字) 氣泡文字.text = 內容;
            SetVisible(true, instant: true);

            if (隱藏協程 != null) StopCoroutine(隱藏協程);
            // 這裡開始就不會再有 inactive 時啟動協程的錯誤
            隱藏協程 = StartCoroutine(延遲隱藏());
        }

        private IEnumerator 延遲隱藏()
        {
            yield return new WaitForSeconds(顯示時間);

            // 漸隱
            float t = 0f;
            float 起始透明度 = 氣泡CanvasGroup ? 氣泡CanvasGroup.alpha : 1f;
            while (t < 1f)
            {
                t += Time.deltaTime;
                if (氣泡CanvasGroup)
                    氣泡CanvasGroup.alpha = Mathf.Lerp(起始透明度, 0f, t);
                yield return null;
            }

            SetVisible(false, instant: true);
            隱藏協程 = null;
        }

        private void SetVisible(bool visible, bool instant)
        {
            if (!氣泡CanvasGroup) return;

            氣泡CanvasGroup.alpha = visible ? 1f : 0f;
            氣泡CanvasGroup.interactable = visible;
            氣泡CanvasGroup.blocksRaycasts = visible;

            // 若你希望隱藏後順便把整個物件關掉，打開下行：
            // if (!visible && instant) gameObject.SetActive(false);
        }
    }
}

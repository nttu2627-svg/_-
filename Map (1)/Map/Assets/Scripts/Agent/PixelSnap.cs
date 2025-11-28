using UnityEngine;

// 將此腳本掛載在負責顯示 Sprite 的子物件上
public class PixelSnap : MonoBehaviour
{
    [Tooltip("每單位像素量 (Pixels Per Unit)，需與 Sprite Import Settings 一致")]
    public float PPU = 16f; 

    private Transform _parentTransform;
    private Transform _transform;

    void Awake()
    {
        _transform = transform;
        _parentTransform = transform.parent;
        
        if (_parentTransform == null)
        {
            Debug.LogError("PixelSnap 必須掛載在子物件上，且父物件負責邏輯移動。");
            enabled = false;
        }
    }

    void LateUpdate()
    {
        if (_parentTransform == null) return;

        Vector3 parentPos = _parentTransform.position;

        // 計算對齊網格後的座標
        float snapX = Mathf.Round(parentPos.x * PPU) / PPU;
        float snapY = Mathf.Round(parentPos.y * PPU) / PPU;
        
        // 為了保持 Sprite 跟隨父物件，我們調整 localPosition
        // 讓 Global Position 剛好落在像素網格上
        // 公式：ChildLocal = SnappedGlobal - ParentGlobal
        
        Vector3 newLocalPos = new Vector3(
            snapX - parentPos.x,
            snapY - parentPos.y,
            _transform.localPosition.z // 保持原本的 Z 軸 (排序用)
        );

        _transform.localPosition = newLocalPos;
    }
}
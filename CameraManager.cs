// Scripts/Camera/CameraManager.cs (完整功能最終版)

using System.Collections.Generic;
using UnityEngine;
using Unity.Cinemachine; // 確保已從 Package Manager 安裝 Cinemachine

/// <summary>
/// 統一管理場景中的手動攝影機 (CameraController) 與自動攝影機 (Cinemachine)。
/// 負責在這兩種模式之間進行切換，並提供外部接口來控制攝影機行為。
/// </summary>
public class CameraManager : MonoBehaviour
{
    [Header("核心依賴 (必須賦值)")]
    [Tooltip("場景中的主攝影機，必須掛載 CameraController 和 CinemachineBrain")]
    public Camera mainCamera;

    [Header("虛擬攝影機清單")]
    [Tooltip("將場景中所有用於自動運鏡的 VCam (如跟隨、固定機位) 拖到這裡")]
    public List<CinemachineCamera> virtualCameras = new List<CinemachineCamera>();
    public IEnumerable<string> GetVirtualCameraNames()
    {
        if (_cameraDictionary != null)
        {
            // 返回字典中所有的鍵 (Key)，也就是攝影機的名稱
            return _cameraDictionary.Keys;
        }
        // 如果字典尚未初始化，返回一個空集合，避免錯誤
        return new List<string>();
    }

    // 私有組件引用
    private CameraController _cameraController;
    private CinemachineBrain _cinemachineBrain;

    // 使用字典來快速查找攝影機，鍵為攝影機物件名稱的大寫形式
    private Dictionary<string, CinemachineCamera> _cameraDictionary = new Dictionary<string, CinemachineCamera>();

    void Awake()
    {
        // --- 初始化檢查 ---
        if (mainCamera == null)
        {
            Debug.LogError("[CameraManager] Main Camera 未在 Inspector 中賦值！此腳本將無法工作。", this);
            this.enabled = false;
            return;
        }

        _cameraController = mainCamera.GetComponent<CameraController>();
        _cinemachineBrain = mainCamera.GetComponent<CinemachineBrain>();

        if (_cameraController == null || _cinemachineBrain == null)
        {
            Debug.LogError("[CameraManager] Main Camera 上缺少 CameraController 或 CinemachineBrain 組件！", this);
            this.enabled = false;
            return;
        }

        // --- 註冊所有虛擬攝影機 ---
        // 將列表中的 VCam 添加到字典中，方便之後用名字快速查找
        foreach (var vcam in virtualCameras)
        {
            if (vcam != null)
            {
                // 將名稱轉為大寫，避免因大小寫問題而找不到
                string vcamNameUpper = vcam.gameObject.name.ToUpper();
                if (!_cameraDictionary.ContainsKey(vcamNameUpper))
                {
                    _cameraDictionary[vcamNameUpper] = vcam;
                }
                else
                {
                    Debug.LogWarning($"[CameraManager] 發現同名的虛擬攝影機: {vcam.gameObject.name}。只會註冊第一個。", this);
                }
                
                // 將所有 VCam 的初始優先級設為較低的值，確保它們在遊戲開始時不會自動激活
                vcam.Priority.Value = 1;
            }
        }
        Debug.Log($"[CameraManager] 已成功註冊 {_cameraDictionary.Count} 個虛擬攝影機。");
    }

    void Start()
    {
        // 遊戲開始時，預設進入玩家手動控制的自由移動模式
        SwitchToFreeLookMode();
    }

    void Update()
    {
        // 提供一個方便的熱鍵 (F鍵)，讓玩家可以隨時從任何自動模式切換回手動模式
        if (Input.GetKeyDown(KeyCode.F))
        {
            SwitchToFreeLookMode();
            Debug.Log("已切換到手動自由控制模式 (F鍵)。");
        }
    }

    /// <summary>
    /// **[模式一]** 切換到玩家自由控制模式。
    /// 此模式下，玩家可以使用 WASD 和方向鍵自由移動及縮放鏡頭。
    /// </summary>
    public void SwitchToFreeLookMode()
    {
        Debug.Log("[CameraManager] 切換到【手動自由】模式。按 WASD 移動，方向鍵縮放。");
        
        // 啟用手動控制器
        if (_cameraController != null) _cameraController.enabled = true;
        
        // 禁用 Cinemachine 系統，將攝影機控制權完全交給手動控制器
        if (_cinemachineBrain != null) _cinemachineBrain.enabled = false;
    }

    /// <summary>
    /// **[模式二]** 切換到由 Cinemachine 控制的自動模式，並激活指定的虛擬攝影機。
    /// 此方法是所有自動運鏡的核心。
    /// </summary>
    /// <param name="cameraName">要激活的虛擬攝影機在 Hierarchy 中的物件名稱。</param>
    public void SwitchToCinemachineMode(string cameraName)
    {
        if (string.IsNullOrEmpty(cameraName)) return;

        string upperCameraName = cameraName.ToUpper();
        if (_cameraDictionary.TryGetValue(upperCameraName, out CinemachineCamera targetCamera))
        {
            Debug.Log($"[CameraManager] 切換到【自動 Cinemachine】模式，激活攝影機: '{cameraName}'。");

            // 禁用手動控制器，避免衝突
            if (_cameraController != null) _cameraController.enabled = false;

            // 啟用 Cinemachine 系統，讓它開始工作
            if (_cinemachineBrain != null) _cinemachineBrain.enabled = true;

            // --- 核心切換邏輯：提高目標鏡頭的優先級 ---
            // Cinemachine 會自動選擇優先級最高的 VCam 作為當前活動鏡頭
            foreach (var cam in _cameraDictionary.Values)
            {
                cam.Priority.Value = (cam == targetCamera) ? 20 : 1;
            }
        }
        else
        {
            Debug.LogWarning($"[CameraManager] 嘗試切換攝影機失敗，未在註冊清單中找到名為 '{cameraName}' 的虛擬攝影機!", this);
        }
    }

    /// <summary>
    /// **[便捷功能]** 讓攝影機開始跟隨一個指定的目標。
    /// 這是一個特殊用途的切換函式，專門用於跟隨代理人。
    /// </summary>
    /// <param name="target">要跟隨的目標物件的 Transform。</param>
    public void SetFollowTarget(Transform target)
    {
        // 在 Hierarchy 中，您的跟隨鏡頭物件必須命名為 "VCam_FollowAgent"
        const string followCamName = "VCAM_FOLLOWAGENT";

        string upperFollowCamName = followCamName.ToUpper();
        if (_cameraDictionary.TryGetValue(upperFollowCamName, out CinemachineCamera followCam))
        {
            if (target != null)
            {
                Debug.Log($"[CameraManager] 設定跟隨目標為: {target.name}");
                // 設定 CinemachineCamera 的 Follow 和 LookAt 屬性
                followCam.Follow = target;
                followCam.LookAt = target;

                // 調用統一的切換方法來激活此攝影機
                SwitchToCinemachineMode(followCamName);
            }
            else
            {
                // 如果目標為空，則直接切換回自由模式
                 Debug.LogWarning($"[CameraManager] 跟隨目標為 null，切換回自由模式。", this);
                 SwitchToFreeLookMode();
            }
        }
        else
        {
            Debug.LogWarning($"[CameraManager] 找不到用於跟隨的虛擬攝影機，請確保場景中有名為 '{followCamName}' 的攝影機並已添加到清單中。", this);
        }
    }
}
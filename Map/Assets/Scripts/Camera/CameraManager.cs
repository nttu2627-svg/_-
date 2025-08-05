// Scripts/Camera/CameraManager.cs (最终模式切换版)

using System.Collections.Generic;
using UnityEngine;
using Unity.Cinemachine;

public class CameraManager : MonoBehaviour
{
    [Header("核心依赖")]
    [Tooltip("场景中的主摄影机，必须挂载 CameraController 和 CinemachineBrain")]
    public Camera mainCamera;

    [Header("虚拟摄影机清单")]
    [Tooltip("将场景中所有用于自动运镜的 VCam (如跟随、固定机位) 拖到这里")]
    public List<CinemachineCamera> virtualCameras = new List<CinemachineCamera>();
    
    // 私有组件引用
    private CameraController _cameraController;
    private CinemachineBrain _cinemachineBrain;
    private Dictionary<string, CinemachineCamera> _cameraDictionary = new Dictionary<string, CinemachineCamera>();

    void Awake()
    {
        if (mainCamera == null)
        {
            Debug.LogError("[CameraManager] Main Camera 未赋值！此脚本将无法工作。");
            this.enabled = false;
            return;
        }

        // 获取主摄影机上的核心组件
        _cameraController = mainCamera.GetComponent<CameraController>();
        _cinemachineBrain = mainCamera.GetComponent<CinemachineBrain>();

        if (_cameraController == null || _cinemachineBrain == null)
        {
            Debug.LogError("[CameraManager] Main Camera 上缺少 CameraController 或 CinemachineBrain 组件！");
            this.enabled = false;
            return;
        }

        // 注册所有虚拟摄影机
        foreach (var vcam in virtualCameras)
        {
            if (vcam != null)
            {
                _cameraDictionary[vcam.gameObject.name.ToUpper()] = vcam;
                vcam.Priority.Value = 1; // 初始为低优先级
            }
        }
    }

    void Start()
    {
        // 游戏开始时，默认进入自由移动模式
        SwitchToFreeLookMode(); 
    }

    void Update()
    {
        // 增加一个热键，让玩家可以随时切回自由模式
        if (Input.GetKeyDown(KeyCode.F))
        {
            SwitchToFreeLookMode();
        }
    }

    /// <summary>
    /// 切换到玩家自由控制模式。
    /// </summary>
    public void SwitchToFreeLookMode()
    {
        Debug.Log("[CameraManager] 切换到自由移动模式。");
        if(_cinemachineBrain != null) _cinemachineBrain.enabled = false; // 禁用 Cinemachine，交出控制权
        if(_cameraController != null) _cameraController.enabled = true;  // 启用我们的手动控制器
    }

    /// <summary>
    /// 切换到由 Cinemachine 控制的自动模式，并激活指定的虚拟摄影机。
    /// </summary>
    /// <param name="cameraName">要激活的虚拟摄影机的名字</param>
    public void SwitchToCinemachineMode(string cameraName)
    {
        string upperCameraName = cameraName.ToUpper();
        if (_cameraDictionary.TryGetValue(upperCameraName, out CinemachineCamera targetCamera))
        {
            Debug.Log($"[CameraManager] 切换到 Cinemachine 模式，激活 '{cameraName}'。");
            
            if(_cameraController != null) _cameraController.enabled = false; // 禁用我们的手动控制器
            if(_cinemachineBrain != null) _cinemachineBrain.enabled = true;  // 启用 Cinemachine，收回控制权

            // 提高目标镜头的优先级，使其成为当前活动镜头
            foreach (var cam in _cameraDictionary.Values)
            {
                cam.Priority.Value = (cam == targetCamera) ? 20 : 1;
            }
        }
        else
        {
            Debug.LogWarning($"[CameraManager] 未找到名为 '{cameraName}' 的虚拟摄影机!");
        }
    }

    /// <summary>
    /// 设置跟随目标并切换到跟随镜头。
    /// </summary>
    public void SetFollowTarget(Transform target)
    {
        // 假设跟随镜头的名字总是 "VCAM_FOLLOWAGENT"
        string followCamName = "VCAM_FOLLOWAGENT";
        if (_cameraDictionary.TryGetValue(followCamName, out CinemachineCamera followCam))
        {
             followCam.Follow = target;
             followCam.LookAt = target;
             SwitchToCinemachineMode(followCamName); // 调用统一的切换方法
        }
        else
        {
            Debug.LogWarning($"[CameraManager] 未找到跟随镜头 '{followCamName}'!");
        }
    }
}
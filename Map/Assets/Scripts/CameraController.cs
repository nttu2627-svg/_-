// Scripts/CameraController.cs (最终直接控制版)

using UnityEngine;
using UnityEngine.EventSystems;

// 强制要求此脚本必须与一个摄影机挂载在同一个物件上
[RequireComponent(typeof(Camera))]
public class CameraController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float keyMoveSpeed = 25f;
    [Tooltip("场景边界（建议是含 Composite Collider 2D 的 Tilemap）")]
    public Collider2D mapBounds;

    [Header("Zoom Settings")]
    public float zoomSpeed = 10f;
    public float minZoom = 2f;
    public float maxZoom = 20f;
    public float zoomSmoothing = 5f;

    // 私有变量
    private Camera cam;
    private Transform camTransform;
    private float _targetZoom;

    void Awake()
    {
        cam = GetComponent<Camera>();
        camTransform = transform; // 缓存 Transform
    }

    void Start()
    {
        // 设置初始位置和缩放
        camTransform.position = new Vector3(0, 0, camTransform.position.z);
        cam.orthographicSize = 15f;
        _targetZoom = cam.orthographicSize;
    }

    void Update()
    {
        // 只有当此脚本被启用时，才处理玩家输入
        HandleKeyInput(Time.unscaledDeltaTime);
        ApplySmoothZoom(Time.unscaledDeltaTime);
        ClampCameraPosition();
    }

    private void HandleKeyInput(float dt)
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) 
            return;
        
        // --- 处理移动输入 (WASD) ---
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += Vector3.up;
        if (Input.GetKey(KeyCode.S)) move += Vector3.down;
        if (Input.GetKey(KeyCode.A)) move += Vector3.left;
        if (Input.GetKey(KeyCode.D)) move += Vector3.right;
        if (move != Vector3.zero)
        {
            camTransform.Translate(move * keyMoveSpeed * dt, Space.World);
        }
        
        // --- 处理缩放输入 (方向键) ---
        float zoomChange = 0f;
        if (Input.GetKey(KeyCode.UpArrow)) zoomChange = -1f; 
        else if (Input.GetKey(KeyCode.DownArrow)) zoomChange = 1f;
        if (zoomChange != 0f)
        {
            _targetZoom += zoomChange * zoomSpeed * dt;
            _targetZoom = Mathf.Clamp(_targetZoom, minZoom, maxZoom);
        }
    }

    private void ApplySmoothZoom(float dt)
    {
        // 平滑地应用缩放
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, _targetZoom, dt * zoomSmoothing);
    }

    private void ClampCameraPosition()
    {
        if (mapBounds == null) return;
        float camHeight = cam.orthographicSize;
        float camWidth  = camHeight * cam.aspect;
        Bounds b = mapBounds.bounds;
        float minX = b.min.x + camWidth;
        float maxX = b.max.x - camWidth;
        float minY = b.min.y + camHeight;
        float maxY = b.max.y - camHeight;
        if (minX > maxX) { minX = maxX = b.center.x; }
        if (minY > maxY) { minY = maxY = b.center.y; }
        Vector3 pos = camTransform.position;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.y = Mathf.Clamp(pos.y, minY, maxY);
        camTransform.position = pos;
    }
}
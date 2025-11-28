// AgentVisualController.cs
using UnityEngine; // 新增：解決 MonoBehaviour, Animator 等錯誤

public class AgentVisualController : MonoBehaviour {
    private Animator _animator;
    private SpriteRenderer _renderer;
    
    // 使用 Hash 提高效能，避免字串比對開銷
    private static readonly int HashMoveX = Animator.StringToHash("MoveX");
    private static readonly int HashMoveY = Animator.StringToHash("MoveY");
    private static readonly int HashIsMoving = Animator.StringToHash("IsMoving");

    void Awake() // 建議補上 Awake 獲取組件
    {
        _animator = GetComponent<Animator>();
        _renderer = GetComponent<SpriteRenderer>();
    }

    public void UpdateVisuals(Vector3 velocity) {
        if (_animator == null || _renderer == null) return;

        bool isMoving = velocity.sqrMagnitude > 0.01f;
        
        _animator.SetBool(HashIsMoving, isMoving);
        
        if (isMoving) {
            // 正規化方向向量，用於 Blend Tree
            Vector3 dir = velocity.normalized;
            _animator.SetFloat(HashMoveX, dir.x);
            _animator.SetFloat(HashMoveY, dir.y);
            
            // 處理朝向翻轉 (對於左右對稱的 Sprite)
            if (Mathf.Abs(dir.x) > 0.1f) {
                _renderer.flipX = dir.x < 0;
            }
        }
    }
    
    public void PlayEmote(string emoteName) {
        if (_animator != null)
            _animator.SetTrigger(emoteName);
    }
}
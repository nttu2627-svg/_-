// AgentVisualController.cs
// [修復] 加入安全檢查，避免在沒有 Animator 或參數不存在時報錯
using UnityEngine;

public class AgentVisualController : MonoBehaviour {
    private Animator _animator;
    private SpriteRenderer _renderer;

    // 使用 Hash 提高效能，避免字串比對開銷
    private static readonly int HashMoveX = Animator.StringToHash("MoveX");
    private static readonly int HashMoveY = Animator.StringToHash("MoveY");
    private static readonly int HashIsMoving = Animator.StringToHash("IsMoving");
    private static readonly int HashIsWaiting = Animator.StringToHash("IsWaiting");
    private static readonly int HashIsPerformingAction = Animator.StringToHash("IsPerformingAction");
    private static readonly int HashIdleLoop = Animator.StringToHash("IdleLoop");
    private static readonly int HashActionLoop = Animator.StringToHash("ActionLoop");

    void Awake()
    {
        _animator = GetComponent<Animator>();
        _renderer = GetComponent<SpriteRenderer>();
    }

    public void UpdateVisuals(Vector3 velocity) {
        // [修復] 如果沒有 Renderer，直接返回不報錯
        if (_renderer == null) return;

        bool isMoving = velocity.sqrMagnitude > 0.01f;

        // 處理朝向翻轉 (對於左右對稱的 Sprite)
        if (isMoving && Mathf.Abs(velocity.normalized.x) > 0.1f) {
            _renderer.flipX = velocity.normalized.x < 0;
        }

        // [修復] 只有在 Animator 存在時才嘗試設定參數，且使用安全方法
        if (_animator == null) return;

        TrySetBool(HashIsMoving, isMoving);

        if (isMoving) {
            Vector3 dir = velocity.normalized;
            TrySetFloat(HashMoveX, dir.x);
            TrySetFloat(HashMoveY, dir.y);
        }
    }

    public void EnterWaitingLoop(bool performingAction)
    {
        if (_animator == null) return;

        TrySetBool(HashIsMoving, false);
        TrySetBool(HashIsWaiting, !performingAction);
        TrySetBool(HashIsPerformingAction, performingAction);

        if (performingAction)
        {
            TrySetTrigger(HashActionLoop);
        }
        else
        {
            TrySetTrigger(HashIdleLoop);
        }
    }

    public void PlayEmote(string emoteName) {
        if (_animator != null)
        {
            // 使用 TrySetTrigger 的字串版本
            int hash = Animator.StringToHash(emoteName);
            TrySetTrigger(hash);
        }
    }

    /// <summary>
    /// 安全地設定 Float 參數，如果參數不存在則不報錯
    /// </summary>
    private void TrySetFloat(int hash, float value)
    {
        if (_animator == null) return;
        foreach (var param in _animator.parameters)
        {
            if (param.nameHash == hash && param.type == AnimatorControllerParameterType.Float)
            {
                _animator.SetFloat(hash, value);
                return;
            }
        }
        // 參數不存在時靜默忽略，不報錯
    }

    private void TrySetBool(int hash, bool value)
    {
        if (_animator == null) return;
        foreach (var param in _animator.parameters)
        {
            if (param.nameHash == hash && param.type == AnimatorControllerParameterType.Bool)
            {
                _animator.SetBool(hash, value);
                return;
            }
        }
        // 參數不存在時靜默忽略，不報錯
    }

    private void TrySetTrigger(int hash)
    {
        if (_animator == null) return;
        foreach (var param in _animator.parameters)
        {
            if (param.nameHash == hash && param.type == AnimatorControllerParameterType.Trigger)
            {
                _animator.SetTrigger(hash);
                return;
            }
        }
        // 參數不存在時靜默忽略，不報錯
    }
}
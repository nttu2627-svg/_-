using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 2f;
    private Rigidbody2D rb;
    private Animator animator;
    private Vector2 movement;
    private bool isMoving; // ✅ 移出成為全域變數

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        animator = GetComponent<Animator>();
    }

    private void Start()
    {
        animator.SetBool("IsMoving", false);
        animator.SetFloat("MoveX", 0);
        animator.SetFloat("MoveY", -1);
    }

    void Update()
    {
        movement = Vector2.zero;
        movement.x = Input.GetAxisRaw("Horizontal");
        movement.y = Input.GetAxisRaw("Vertical");

        isMoving = movement != Vector2.zero;
        animator.SetBool("IsMoving", isMoving);

        animator.SetFloat("MoveX", movement.x);
        animator.SetFloat("MoveY", movement.y);
    }

    private void FixedUpdate()
    {
        //Debug.Log("movement: " + movement);
        //Debug.Log("position: " + rb.position);
        Debug.Log("IsMoving: " + isMoving);
        Debug.Log("MoveX: " + movement.x + ", MoveY: " + movement.y);

        if (movement != Vector2.zero)
        {
            rb.MovePosition(rb.position + movement.normalized * moveSpeed * Time.fixedDeltaTime);
        }
        else
        {
            rb.linearVelocity = Vector2.zero;
        }
    }
}

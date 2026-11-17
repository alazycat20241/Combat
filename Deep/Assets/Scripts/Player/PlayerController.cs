using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    [Header("移动参数")]
    [SerializeField] float moveSpeed = 5f;
    [SerializeField] float rotationSpeed = 10f; // 角色转向平滑度

    [Header("摄像机引用")]
    [SerializeField] Transform cameraTransform;

    [Header("动画控制")]
    [SerializeField] Animator animator; 
    [SerializeField] float animationDampTime = 0.1f; // 动画过渡的平滑时间

    private PlayerControls playerControls;
    private Vector2 moveInput; // 存储移动输入 (WASD 或 左摇杆)

    // 用于计算动画混合树的数值
    private float currentMoveAmount = 0f;
    private float targetMoveAmount = 0f;

    // 用于 FixedUpdate 计算的缓存
    private Vector3 currentMoveDirection;
    private Quaternion targetRotation;

    private void Awake()
    {
        playerControls = new PlayerControls();

        // 如果没手动拖摄像机，自动找主摄像机
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        // 如果没拖 Animator，尝试在自身获取
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        // 帧同步预备：固定逻辑帧率（例如 20 帧/秒，即 0.05 秒更新一次逻辑）
        Time.fixedDeltaTime = 0.05f;
    }

    private void OnEnable()
    {
        playerControls.Gameplay.Enable();

        // 绑定移动输入（WASD / 左摇杆）
        playerControls.Gameplay.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        playerControls.Gameplay.Move.canceled += ctx => moveInput = Vector2.zero;
    }

    private void OnDisable()
    {
        playerControls.Gameplay.Disable();
    }

    private void Update()
    {
        // 1. 获取摄像机的前方和右方方向（压平 Y 轴，防止摄像机俯视导致角色往地里钻）
        Vector3 cameraForward = cameraTransform.forward;
        cameraForward.y = 0;
        cameraForward.Normalize();

        Vector3 cameraRight = cameraTransform.right;
        cameraRight.y = 0;
        cameraRight.Normalize();

        // 2. 计算基于摄像机的移动方向 (WASD -> 世界坐标方向)
        currentMoveDirection = (cameraForward * moveInput.y + cameraRight * moveInput.x).normalized;

        // 计算动画所需的强度（输入摇杆的幅度，0 到 1 之间）
        targetMoveAmount = moveInput.magnitude;

        // 缓存目标旋转（让角色面朝移动方向，而不是平移）
        if (currentMoveDirection.magnitude >= 0.1f)
        {
            targetRotation = Quaternion.LookRotation(currentMoveDirection);
        }

        // 表现层（动画）留在 Update，保证高刷新率下的视觉平滑
        HandleAnimation();
    }

    // 逻辑层（移动）放在 FixedUpdate，保证未来帧同步的双端一致性
    private void FixedUpdate()
    {
        // 3. 执行移动 (直接修改 transform.position)
        if (currentMoveDirection.magnitude >= 0.1f)
        {
            // 移动：直接在当前坐标上加上方向和速度（使用 Time.fixedDeltaTime）
            transform.position += currentMoveDirection * (moveSpeed * targetMoveAmount) * Time.fixedDeltaTime;

            // 4. 角色平滑转向（使用 Time.fixedDeltaTime）
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.fixedDeltaTime);
        }
    }

    private void HandleAnimation()
    {
        if (animator == null) return;

        // 使用 Mathf.Lerp 让动画的过渡更平滑，避免按键瞬间动画硬切
        // 这样 currentMoveAmount 会慢慢追上 targetMoveAmount
        currentMoveAmount = Mathf.Lerp(currentMoveAmount, targetMoveAmount, Time.deltaTime * (1f / animationDampTime));

        // 将计算好的值传给 Animator 的 Blend 树
        // 注意："moveAmount" 这个名字必须和你 Animator 面板里 Parameters 里的参数名一模一样
        animator.SetFloat("moveAmount", currentMoveAmount);
    }
}
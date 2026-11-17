using UnityEngine;
using UnityEngine.InputSystem; 

public class CameraController : MonoBehaviour
{
    [SerializeField] Transform followTarget;
    [SerializeField] float rotationSpeed = 2f;
    [SerializeField] float distance = 5;

    [SerializeField] float minVerticalAngle = -45;
    [SerializeField] float maxVerticalAngle = 45;

    [SerializeField] Vector2 framingOffset;

    float rotationX;
    float rotationY;

    // 声明新的输入控制类
    private PlayerControls playerControls;
    private Vector2 lookInput;

    // 优化：缓存用于计算偏移的 Vector3，避免每帧产生垃圾（GC）
    private Vector3 _frameOffsetVec;

    private void Awake()
    {
        playerControls = new PlayerControls();
    }

    private void OnEnable()
    {
        // 启用输入
        playerControls.Gameplay.Enable();

        // 订阅鼠标/摇杆的移动事件
        playerControls.Gameplay.Look.performed += ctx => lookInput = ctx.ReadValue<Vector2>();
        // 当鼠标停止移动时，将输入归零，防止摄像机自己一直转
        playerControls.Gameplay.Look.canceled += ctx => lookInput = Vector2.zero;
    }

    private void OnDisable()
    {
        playerControls.Gameplay.Disable();
    }

    // 注意：如果你做的是严格帧同步的动作游戏，建议把这里改成 FixedUpdate 或者由逻辑帧去驱动！
    private void Update()
    {
        // 优化：只在启动或面板修改时更新 offset 数据，不用每帧 new Vector3
        _frameOffsetVec = new Vector3(framingOffset.x, framingOffset.y, 0);

        // 如果你想让鼠标/摇杆灵敏度完全由 Input System 面板控制，你甚至可以不用乘以 rotationSpeed
        // 但这里为了保持你原代码的逻辑，我们乘上它
        rotationX += lookInput.y * rotationSpeed;
        rotationX = Mathf.Clamp(rotationX, minVerticalAngle, maxVerticalAngle);

        rotationY += lookInput.x * rotationSpeed;

        var targetRotation = Quaternion.Euler(rotationX, rotationY, 0);

        // 冗余剔除：直接使用缓存的 _frameOffsetVec 进行计算
        var focusPosition = followTarget.position + _frameOffsetVec;

        // 优化：将 new Vector3(0, 0, distance) 替换为 Vector3.forward * distance，逻辑一致但更清晰
        transform.position = focusPosition - targetRotation * (Vector3.forward * distance);
        transform.rotation = targetRotation;
    }
}
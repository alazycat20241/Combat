using Combat.Core;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 第三人称跟随摄像机。
///
/// 摄像机始终跟随服务端分给本客户端的 PlayerId 对应角色。
/// 这样客户端 A 分到 1 时跟随 Player1，客户端 B 分到 2 时自动跟随 Player2。
/// </summary>
[DefaultExecutionOrder(20000)]
public sealed class CameraController : MonoBehaviour
{
    [Header("跟随目标")]
    [SerializeField] private Transform followTarget;
    [SerializeField] private bool autoFollowLocalPlayer = true;

    [Header("轨道参数")]
    [SerializeField, Min(0.01f)] private float rotationSpeed = 0.2f;
    [SerializeField, Min(0.1f)] private float distance = 5f;
    [SerializeField, Min(0f)] private float positionFollowSharpness = 30f;
    [SerializeField] private float minVerticalAngle = -45f;
    [SerializeField] private float maxVerticalAngle = 45f;
    [SerializeField] private Vector2 framingOffset;

    private PlayerControls playerControls;
    private Vector2 lookInput;
    private float rotationX;
    private float rotationY;
    private bool cameraInitialized;
    private Transform currentResolvedTarget;

    private void Awake()
    {
        playerControls = new PlayerControls();
    }

    private void OnEnable()
    {
        playerControls.Gameplay.Enable();
        playerControls.Gameplay.Look.performed += OnLookPerformed;
        playerControls.Gameplay.Look.canceled += OnLookCanceled;
    }

    private void OnDisable()
    {
        playerControls.Gameplay.Look.performed -= OnLookPerformed;
        playerControls.Gameplay.Look.canceled -= OnLookCanceled;
        playerControls.Gameplay.Disable();
    }

    private void Update()
    {
        ResolveLocalFollowTarget();

        if (followTarget == null)
        {
            return;
        }

        rotationX += lookInput.y * rotationSpeed;
        rotationX = Mathf.Clamp(
            rotationX,
            minVerticalAngle,
            maxVerticalAngle);

        rotationY += lookInput.x * rotationSpeed;

        Quaternion targetRotation =
            Quaternion.Euler(rotationX, rotationY, 0f);
        Vector3 focusPosition =
            followTarget.position +
            new Vector3(framingOffset.x, framingOffset.y, 0f);
        Vector3 desiredPosition =
            focusPosition -
            targetRotation * (Vector3.forward * distance);

        if (!cameraInitialized)
        {
            transform.SetPositionAndRotation(
                desiredPosition,
                targetRotation);
            cameraInitialized = true;
            return;
        }

        float interpolation =
            1f - Mathf.Exp(
                -positionFollowSharpness * Time.deltaTime);

        transform.position = Vector3.Lerp(
            transform.position,
            desiredPosition,
            interpolation);
        transform.rotation = targetRotation;
    }

    private void ResolveLocalFollowTarget()
    {
        if (!autoFollowLocalPlayer)
        {
            return;
        }

        LockstepManager lockstep = CombatSimulationRunner.Lockstep;
        if (lockstep == null ||
            !lockstep.HasServerAssignedPlayerId)
        {
            return;
        }

        int localPlayerId = lockstep.LocalClientId;
        if (currentResolvedTarget != null)
        {
            PlayerController currentPlayer =
                currentResolvedTarget.GetComponent<PlayerController>();

            if (currentPlayer != null &&
                currentPlayer.Entity != null &&
                currentPlayer.Entity.EntityId == localPlayerId)
            {
                return;
            }
        }

        PlayerController[] players =
            FindObjectsOfType<PlayerController>();

        for (int i = 0; i < players.Length; i++)
        {
            PlayerController player = players[i];
            if (player.Entity != null &&
                player.Entity.EntityId == localPlayerId)
            {
                SetFollowTarget(player.transform);
                return;
            }
        }
    }

    /// <summary>
    /// 外部也可以显式切换跟随目标。切换时会重置摄像机位置，
    /// 避免从 Player1 平滑飞越整张地图到 Player2。
    /// </summary>
    public void SetFollowTarget(Transform target)
    {
        if (followTarget == target)
        {
            return;
        }

        followTarget = target;
        currentResolvedTarget = target;
        cameraInitialized = false;

        if (target != null)
        {
            // 首次切换到本地角色时，让镜头默认站在角色背后，
            // 而不是让 PlayerId=2 的客户端从 Player1 的方向看 Player2。
            rotationY = target.eulerAngles.y;
        }
    }

    private void OnLookPerformed(InputAction.CallbackContext context)
    {
        lookInput = context.ReadValue<Vector2>();
    }

    private void OnLookCanceled(InputAction.CallbackContext context)
    {
        lookInput = Vector2.zero;
    }

    private void OnValidate()
    {
        rotationSpeed = Mathf.Max(0.01f, rotationSpeed);
        distance = Mathf.Max(0.1f, distance);
        positionFollowSharpness =
            Mathf.Max(0f, positionFollowSharpness);
        minVerticalAngle =
            Mathf.Min(minVerticalAngle, maxVerticalAngle);
    }
}

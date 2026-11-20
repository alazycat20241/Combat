using System;
using Combat.Config;
using Combat.Core;
using LubanIntegration;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Unity 表现和输入适配器。
///
/// 战斗结果全部由纯 C# PlayerEntity 决定。这个 MonoBehaviour 只负责：
/// 1. 把 New Input System 的输入翻译成 CombatInputFrame。
/// 2. 调用 PlayerEntity.Tick。
/// 3. 监听实体战斗事件并触发 Animator Trigger。
/// 4. 把逻辑位置平滑同步到 Transform。
///
/// 这里的类没有任何 HP、攻击帧、AABB、受击速度等战斗状态。
/// </summary>
public sealed class PlayerController : MonoBehaviour
{
    private static int nextRuntimeEntityId = 1;

    [Header("逻辑配置（Luban 接入前使用 POCO 默认值）")]
    [SerializeField] private bool useLubanConfig = true;
    [SerializeField] private int lubanPlayerId = 1001;
    [SerializeField] private string playerCombatResource = "Data/TbPlayerCombat";
    [SerializeField] private string combatAttackResource = "Data/TbCombatAttack";
    [SerializeField] private PlayerCombatConfig config = CombatConfigDefaults.CreatePlayer();

    [Header("场景引用")]
    [Tooltip("可手动指定目标。为空时由实体自动寻找其他 PlayerEntity")]
    public PlayerController combatTarget;

    [Tooltip("取消勾选后这个角色只接收受击，不读取输入，适合训练木桩")]
    [SerializeField] private bool acceptPlayerInput = true;

    [SerializeField] private Animator animator;

    [Header("输入 Action（留空自动读取键鼠）")]
    [SerializeField] private InputActionReference moveInputReference;
    [SerializeField] private InputActionReference primaryAttackInputReference;
    [SerializeField] private InputActionReference secondaryAttackInputReference;
    [SerializeField] private InputActionReference ultimateInputReference;
        [Tooltip("WASD 相对哪个 Transform 解释。留空时使用 Camera.main")]
        [SerializeField] private Transform movementReference;

    [Header("表现平滑")]
    [SerializeField, Min(0f)] private float visualFollowSharpness = 25f;
    [SerializeField, Min(0f)] private float animationDampTime = 0.2f;

    [Header("调试信息")]
    [Tooltip("联机时由服务器分配的稳定实体 ID；单机为 0 时自动生成")]
    [SerializeField] private int networkEntityId;
    [SerializeField] private int runtimeEntityId;

    private PlayerEntity entity;
    private Vector2 moveInput;
    private bool primaryAttackInputLatched;
    private bool secondaryAttackInputLatched;
    private bool ultimateInputLatched;
    private int ultimateVisualSyncEndFrame = -1;
    private bool initialized;
        private Transform cachedMovementReference;

    public PlayerEntity Entity => entity;
    public PlayerCombatConfig Config => config;
    public float CurrentHP => entity != null ? entity.CurrentHP.ToFloat() : 0f;
    public bool UltimateReady => entity?.UltimateReady ?? false;
    public int ComboHitCount => entity?.ComboHitCount ?? 0;
    public bool IsInCombo => entity?.IsInCombo ?? false;

    public LogicVector3 LogicalPosition =>
        entity?.LogicalPosition ?? ToLogicVector(transform.position);

    public bool AcceptPlayerInput
    {
        get => acceptPlayerInput;
        set => acceptPlayerInput = value;
    }

    private void Awake()
    {
        bool loadedFromLuban = false;
        if (useLubanConfig &&
            LubanCombatConfigLoader.TryLoad(
                lubanPlayerId,
                playerCombatResource,
                combatAttackResource,
                out PlayerCombatConfig lubanConfig))
        {
            config = lubanConfig;
            loadedFromLuban = true;
        }
        else if (useLubanConfig)
        {
            Debug.LogWarning(
                $"未加载到 Luban 战斗配置 PlayerId={lubanPlayerId}，" +
                $"将使用 CombatConfigDefaults。检查 Resources 路径：" +
                $"{playerCombatResource}、{combatAttackResource}。",
                this);
        }

        if (config == null ||
            config.Attacks == null ||
            config.Attacks.Length == 0)
        {
            config = CombatConfigDefaults.CreatePlayer();
        }

        if (loadedFromLuban)
        {
            Debug.Log(
                $"已加载 Luban 战斗配置：{config.Name} (Id={config.Id})。",
                this);
        }

        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        if (animator != null)
        {
            // 所有位移由 PlayerEntity 决定，Animator 不能写 Transform.position。
            animator.applyRootMotion = false;
        }

        runtimeEntityId = networkEntityId > 0
            ? networkEntityId
            : nextRuntimeEntityId++;
        entity = new PlayerEntity(
            runtimeEntityId,
            config,
            ToLogicVector(transform.position),
            ToLogicRotation(transform.rotation));

        initialized = true;

        if (!Mathf.Approximately(Time.fixedDeltaTime, 0.05f))
        {
            Debug.LogWarning(
                "PlayerController 需要 20Hz 逻辑帧，请将 Fixed Timestep 设置为 0.05。",
                this);
        }
    }

    private void OnEnable()
    {
        if (entity != null)
        {
            CombatSimulationRunner.Lockstep?.RegisterPlayer(
                entity.EntityId,
                entity);
            CombatSimulationRunner.EventCommitted -= OnCombatEvent;
            CombatSimulationRunner.EventCommitted += OnCombatEvent;
        }
    }

    private void OnDisable()
    {
        if (entity == null)
        {
            return;
        }

        CombatSimulationRunner.EventCommitted -= OnCombatEvent;
        CombatSimulationRunner.Lockstep?.UnregisterPlayer(entity.EntityId);
    }

    private void Update()
    {
        if (!initialized)
        {
            return;
        }

        Vector2 sampledMoveInput = acceptPlayerInput ? ReadMoveInput() : Vector2.zero;
        moveInput = entity.CanMove ? sampledMoveInput : Vector2.zero;

        if (acceptPlayerInput && entity.CanAttack)
        {
            if (WasPrimaryAttackPressed())
            {
                primaryAttackInputLatched = true;
            }

            if (WasSecondaryAttackPressed())
            {
                secondaryAttackInputLatched = true;
            }

            if (WasUltimatePressed())
            {
                ultimateInputLatched = true;
            }
        }

        UpdateLocomotionAnimation();
        SmoothVisualTransform();
    }

    private void LateUpdate()
    {
        if (!initialized || Time.frameCount > ultimateVisualSyncEndFrame)
        {
            return;
        }

        // Animator 在 Update 之后可能完成最后一次状态切换。
        // 大招结束后多保持两个 LateUpdate 帧，避免视觉 Transform 回跳。
        transform.SetPositionAndRotation(
            ToUnityVector(entity.LogicalPosition),
            ToUnityRotation(entity.LogicalRotation));
    }

    private void FixedUpdate()
    {
        if (!initialized)
        {
            return;
        }

        FrameInputMask inputMask = BuildFrameInputMask();

        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;

        LockstepManager lockstep = CombatSimulationRunner.Lockstep;
        if (lockstep != null)
        {
            lockstep.SetPreferredTarget(
                entity.EntityId,
                combatTarget != null && combatTarget.Entity != null
                    ? combatTarget.Entity.EntityId
                    : 0);
            lockstep.SubmitLocalInput(entity.EntityId, inputMask);
        }
    }

    private void OnCombatEvent(CombatEvent combatEvent)
    {
        switch (combatEvent.Type)
        {
            case CombatEventType.AttackStarted:
                if (combatEvent.SourceEntityId == entity.EntityId)
                {
                    SetAnimatorTrigger(combatEvent.AttackState.ToString());
                }
                break;
            case CombatEventType.HitReaction:
                if (combatEvent.TargetEntityId == entity.EntityId)
                {
                    SetAnimatorTrigger("HitReaction");
                }
                break;
            case CombatEventType.Died:
                if (combatEvent.TargetEntityId == entity.EntityId)
                {
                    SetAnimatorTrigger("Death");
                    ultimateVisualSyncEndFrame = Time.frameCount + 2;
                }
                break;
        }
    }

    private FrameInputMask BuildFrameInputMask()
    {
        FrameInputMask mask = FrameInputMask.None;
        if (primaryAttackInputLatched)
        {
            mask |= FrameInputMask.PrimaryAttack;
        }

        if (secondaryAttackInputLatched)
        {
            mask |= FrameInputMask.SecondaryAttack;
        }

        if (ultimateInputLatched)
        {
            mask |= FrameInputMask.Jump;
        }

        float deadZone = config.MoveInputDeadZone.ToFloat();
        if (!entity.CanMove || moveInput.sqrMagnitude < deadZone * deadZone)
        {
            return mask;
        }

        mask |= BuildCameraRelativeMoveMask(moveInput);
        return mask;
    }

    /// <summary>
    /// 把摄像机空间的 WASD 向量转换成确定性的世界方向掩码。
    ///
    /// Lockstep 网络里只有离散方向位，没有浮点摄像机角度。因此这里把摄像机
    /// 前方/右方投影到 XZ 平面，再量化到最近的 8 个世界方向之一。
    /// 服务端和其他客户端只回放最终的世界方向位，不会依赖各自摄像机角度。
    /// </summary>
    private FrameInputMask BuildCameraRelativeMoveMask(Vector2 input)
    {
        Transform reference = ResolveMovementReference();
        Vector3 worldDirection;

        if (reference == null)
        {
            worldDirection = new Vector3(input.x, 0f, input.y);
        }
        else
        {
            Vector3 cameraForward = reference.forward;
            Vector3 cameraRight = reference.right;
            cameraForward.y = 0f;
            cameraRight.y = 0f;
            cameraForward.Normalize();
            cameraRight.Normalize();

            worldDirection =
                cameraForward * input.y +
                cameraRight * input.x;
        }

        if (worldDirection.sqrMagnitude <= 0.000001f)
        {
            return FrameInputMask.None;
        }

        FrameInputMask mask = FrameInputMask.None;
        float absX = Mathf.Abs(worldDirection.x);
        float absZ = Mathf.Abs(worldDirection.z);
        const float diagonalCutoff = 0.41421356f;

        bool pureVertical = absX < absZ * diagonalCutoff;
        bool pureHorizontal = absZ < absX * diagonalCutoff;

        if (!pureHorizontal)
        {
            mask |= worldDirection.z >= 0f
                ? FrameInputMask.MoveForward
                : FrameInputMask.MoveBackward;
        }

        if (!pureVertical)
        {
            mask |= worldDirection.x >= 0f
                ? FrameInputMask.MoveRight
                : FrameInputMask.MoveLeft;
        }

        return mask;
    }

    private Transform ResolveMovementReference()
    {
        if (movementReference != null)
        {
            cachedMovementReference = movementReference;
            return cachedMovementReference;
        }

        if (cachedMovementReference == null && Camera.main != null)
        {
            cachedMovementReference = Camera.main.transform;
        }

        return cachedMovementReference;
    }

    private void UpdateLocomotionAnimation()
    {
        if (animator == null)
        {
            return;
        }

        float moveAmount = entity.CanMove
            ? Vector2.ClampMagnitude(moveInput, 1f).magnitude
            : 0f;

        animator.SetFloat(
            "moveAmount",
            moveAmount,
            animationDampTime,
            Time.deltaTime);
    }

    private void SmoothVisualTransform()
    {
        float interpolation =
            1f - Mathf.Exp(-visualFollowSharpness * Time.deltaTime);

        transform.position = Vector3.Lerp(
            transform.position,
            ToUnityVector(entity.LogicalPosition),
            interpolation);

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            ToUnityRotation(entity.LogicalRotation),
            interpolation);
    }

    [ContextMenu("调试：恢复满血并重置战斗状态")]
    public void ResetCombatStateForTesting()
    {
        if (entity == null)
        {
            return;
        }

        entity.ResetForTesting(
            ToLogicVector(transform.position),
            ToLogicRotation(transform.rotation));

        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }
    }

    private Vector2 ReadMoveInput()
    {
        if (moveInputReference != null && moveInputReference.action != null)
        {
            return moveInputReference.action.ReadValue<Vector2>();
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return Vector2.zero;
        }

        float horizontal = 0f;
        float vertical = 0f;

        if (keyboard.aKey.isPressed) horizontal -= 1f;
        if (keyboard.dKey.isPressed) horizontal += 1f;
        if (keyboard.sKey.isPressed) vertical -= 1f;
        if (keyboard.wKey.isPressed) vertical += 1f;

        return new Vector2(horizontal, vertical);
    }

    private bool WasPrimaryAttackPressed()
    {
        if (primaryAttackInputReference != null &&
            primaryAttackInputReference.action != null)
        {
            return primaryAttackInputReference.action.WasPressedThisFrame();
        }

        return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    }

    private bool WasSecondaryAttackPressed()
    {
        if (secondaryAttackInputReference != null &&
            secondaryAttackInputReference.action != null)
        {
            return secondaryAttackInputReference.action.WasPressedThisFrame();
        }

        return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
    }

    private bool WasUltimatePressed()
    {
        if (ultimateInputReference != null &&
            ultimateInputReference.action != null)
        {
            return ultimateInputReference.action.WasPressedThisFrame();
        }

        return Keyboard.current != null &&
               Keyboard.current.spaceKey.wasPressedThisFrame;
    }

    private void SetAnimatorTrigger(string triggerName)
    {
        if (animator != null)
        {
            animator.SetTrigger(triggerName);
        }
    }

    private static Vector3 ToUnityVector(LogicVector3 value)
    {
        return new Vector3(
            value.X.ToFloat(),
            value.Y.ToFloat(),
            value.Z.ToFloat());
    }

    private static LogicVector3 ToLogicVector(Vector3 value)
    {
        return new LogicVector3(value.x, value.y, value.z);
    }

    private static Quaternion ToUnityRotation(LogicRotation rotation)
    {
        return Quaternion.Euler(
            0f,
            rotation.YawRadians.ToFloat() * Mathf.Rad2Deg,
            0f);
    }

    private static LogicRotation ToLogicRotation(Quaternion rotation)
    {
        return new LogicRotation(
            rotation.eulerAngles.y * Mathf.Deg2Rad);
    }

    private void OnValidate()
    {
        if (config == null ||
            config.Attacks == null ||
            config.Attacks.Length == 0)
        {
            config = CombatConfigDefaults.CreatePlayer();
        }

        visualFollowSharpness = Mathf.Max(0f, visualFollowSharpness);
        animationDampTime = Mathf.Max(0f, animationDampTime);
    }

    private void OnDrawGizmosSelected()
    {
        PlayerCombatConfig drawConfig =
            config ?? CombatConfigDefaults.CreatePlayer();

        LogicVector3 position = Application.isPlaying && entity != null
            ? entity.LogicalPosition
            : ToLogicVector(transform.position);

        LogicRotation rotation = Application.isPlaying && entity != null
            ? entity.LogicalRotation
            : ToLogicRotation(transform.rotation);

        Vector3 unityPosition = ToUnityVector(position);
        Vector3 unityRotationForward =
            ToUnityRotation(rotation) * Vector3.forward;
        Quaternion unityRotation = Quaternion.LookRotation(
            unityRotationForward.sqrMagnitude > 0.000001f
                ? unityRotationForward
                : Vector3.forward,
            Vector3.up);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(
            unityPosition + unityRotation * ToUnityVector(drawConfig.HurtBoxLocalOffset),
            ToUnityVector(drawConfig.HurtBoxSize));

        AttackState previewState = Application.isPlaying && entity != null &&
                                   entity.AttackState != AttackState.None
            ? entity.AttackState
            : AttackState.LeftPunch;

        CombatAttackConfig attack = drawConfig.FindAttack(previewState);
        if (attack != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(
                unityPosition + unityRotation * ToUnityVector(attack.AttackBoxLocalOffset),
                ToUnityVector(attack.AttackBoxSize));
        }

        Gizmos.color = Color.cyan;
        Vector3 mapCenter = new Vector3(
            (drawConfig.MapMinX.ToFloat() +
             drawConfig.MapMaxX.ToFloat()) * 0.5f,
            unityPosition.y,
            (drawConfig.MapMinZ.ToFloat() +
             drawConfig.MapMaxZ.ToFloat()) * 0.5f);
        Vector3 mapSize = new Vector3(
            Mathf.Abs(drawConfig.MapMaxX.ToFloat() -
                      drawConfig.MapMinX.ToFloat()),
            0.02f,
            Mathf.Abs(drawConfig.MapMaxZ.ToFloat() -
                      drawConfig.MapMinZ.ToFloat()));
        Gizmos.DrawWireCube(mapCenter, mapSize);
    }
}

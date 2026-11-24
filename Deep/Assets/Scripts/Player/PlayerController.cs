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

    [Header("移动动画")]
    [SerializeField, Min(0f)] private float moveAnimationDelay = 0.1f;
    [SerializeField, Min(0.01f)] private float moveAnimationBlendTime = 0.1f;
    [SerializeField] private string locomotionStateName = "Move";
    [SerializeField, Min(0f)] private float rollbackCrossFadeTime = 0.08f;
    [SerializeField, Min(0f)] private float actionCrossFadeTime = 0.05f;
    [SerializeField, Min(0.1f)] private float maxActionVisualDuration = 3f;

    [Header("回滚视觉包容")]
    [SerializeField, Min(0f)] private float strongRollbackFlashDistance = 0.5f;
    [SerializeField, Min(0.01f)] private float rollbackCorrectionDuration = 0.1f;
    [SerializeField] private bool enableStrongRollbackFlash = true;

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
    private long renderFrame = -1;
    private float renderAlpha = 1f;
    private Vector3 renderStartPosition;
    private Quaternion renderStartRotation;
    private float moveIntentTimer;
    private float smoothedMoveAmount;
    private bool pendingRollbackVisualCorrection;
    private bool visualErrorCorrectionActive;
    private Vector3 visualPositionError;
    private Quaternion visualRotationError = Quaternion.identity;
    private AttackState observedAttackState;
    private bool observedHurtState;
    private bool observedDeathState;
    private string activeActionStateName;
    private float activeActionStartedAt;

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

            // Move Blend Tree 位于 Base Layer。
            // Controller 资产中 Base Layer 权重曾为 0，导致 moveAmount 正常变化，
            // 但移动动画层完全不参与最终 Pose，只剩 Transform 在平移。
            if (animator.layerCount > 0)
            {
                animator.SetLayerWeight(0, 1f);
            }

            // Override Layer 用来播放攻击/受击等覆盖动作，保持满权重。
            if (animator.layerCount > 1)
            {
                animator.SetLayerWeight(1, 0f);
            }

            // 显式从 Base Layer 的 Move 状态启动，避免 Override Layer 的
            // Empty 默认状态或旧序列化状态让角色停留在无动画 Pose。
            animator.Rebind();
            animator.Play(locomotionStateName, 0, 0f);
            animator.Update(0f);

            float overrideWeight = animator.layerCount > 1
                ? animator.GetLayerWeight(1)
                : 0f;
            Debug.Log(
                $"[Animator] {name} Controller={animator.runtimeAnimatorController?.name}, " +
                $"Layers={animator.layerCount}, " +
                $"BaseWeight={animator.GetLayerWeight(0):0.00}, " +
                $"OverrideWeight={overrideWeight:0.00}",
                this);
        }

        runtimeEntityId = networkEntityId > 0
            ? networkEntityId
            : nextRuntimeEntityId++;
        entity = new PlayerEntity(
            runtimeEntityId,
            config,
            ToLogicVector(transform.position),
            ToLogicRotation(transform.rotation));
        renderStartPosition = transform.position;
        renderStartRotation = transform.rotation;

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

        ReconcileAnimatorStateFromLogic();
        UpdateAnimatorLayerWeights();
        UpdateLocomotionAnimation();
        InterpolateVisualTransform();
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
                    observedAttackState = combatEvent.AttackState;
                    PlayActionState(
                        combatEvent.AttackState.ToString(),
                        true);
                }
                break;
            case CombatEventType.HitReaction:
                if (combatEvent.TargetEntityId == entity.EntityId)
                {
                    observedHurtState = true;
                    PlayActionState("HitReaction", true);
                }
                break;
            case CombatEventType.Died:
                if (combatEvent.TargetEntityId == entity.EntityId)
                {
                    observedDeathState = true;
                    PlayActionState("Death", true);
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

    private void ReconcileAnimatorStateFromLogic()
    {
        if (animator == null)
        {
            return;
        }

        if (entity.IsDead)
        {
            if (!observedDeathState)
            {
                observedDeathState = true;
                PlayActionState("Death", false);
            }
            return;
        }

        if (entity.IsHurt)
        {
            if (!observedHurtState)
            {
                observedHurtState = true;
                PlayActionState("HitReaction", false);
            }
            return;
        }

        if (entity.AttackState != AttackState.None)
        {
            if (observedAttackState != entity.AttackState)
            {
                observedAttackState = entity.AttackState;
                PlayActionState(
                    entity.AttackState.ToString(),
                    false);
            }
            return;
        }

        bool hadActionState =
            observedDeathState ||
            observedHurtState ||
            observedAttackState != AttackState.None;

        observedDeathState = false;
        observedHurtState = false;
        observedAttackState = AttackState.None;

        bool visualActionPlaying =
            IsVisualActionPlaying();

        if ((hadActionState || activeActionStateName != null) &&
            !visualActionPlaying)
        {
            ReturnToLocomotionAfterRollback();
        }
    }

    private void PlayActionState(
        string stateName,
        bool alsoSetTrigger)
    {
        if (animator == null)
        {
            return;
        }

        SetActionLayerActive(true);
        activeActionStateName = stateName;
        activeActionStartedAt = Time.time;

        if (alsoSetTrigger)
        {
            animator.SetTrigger(stateName);
        }

        int stateHash = Animator.StringToHash(stateName);
        if (animator.layerCount > 1 &&
            animator.HasState(1, stateHash))
        {
            animator.CrossFadeInFixedTime(
                stateHash,
                actionCrossFadeTime,
                1,
                0f);
        }
    }

    private bool IsVisualActionPlaying()
    {
        if (animator == null ||
            string.IsNullOrEmpty(activeActionStateName) ||
            animator.layerCount < 2)
        {
            return false;
        }

        // 安全超时防止某个动作状态因转换配置错误永久锁住表现层。
        if (Time.time - activeActionStartedAt >
            maxActionVisualDuration)
        {
            return false;
        }

        AnimatorStateInfo stateInfo =
            animator.GetCurrentAnimatorStateInfo(1);
        return stateInfo.IsName(activeActionStateName);
    }

    private void UpdateAnimatorLayerWeights()
    {
        if (animator == null)
        {
            return;
        }

        bool actionLayerRequired =
            entity.AttackState != AttackState.None ||
            entity.IsHurt ||
            entity.IsDead ||
            IsVisualActionPlaying();

        animator.SetLayerWeight(0, 1f);

        if (animator.layerCount > 1)
        {
            animator.SetLayerWeight(
                1,
                actionLayerRequired ? 1f : 0f);
        }
    }

    private void SetActionLayerActive(bool active)
    {
        if (animator != null && animator.layerCount > 1)
        {
            animator.SetLayerWeight(1, active ? 1f : 0f);
        }
    }

    private void UpdateLocomotionAnimation()
    {
        if (animator == null)
        {
            return;
        }

        if (entity.IsMoving)
        {
            moveIntentTimer += Time.deltaTime;
        }
        else
        {
            moveIntentTimer = 0f;
        }

        float targetMoveAmount =
            entity.IsMoving &&
            moveIntentTimer >= moveAnimationDelay
                ? 1f
                : 0f;

        smoothedMoveAmount = Mathf.MoveTowards(
            smoothedMoveAmount,
            targetMoveAmount,
            Time.deltaTime /
            Mathf.Max(moveAnimationBlendTime, 0.01f));

        animator.SetFloat("moveAmount", smoothedMoveAmount);
    }

    private void InterpolateVisualTransform()
    {
        if (pendingRollbackVisualCorrection)
        {
            pendingRollbackVisualCorrection = false;
            ApplyRollbackVisualCorrection();
        }

        if (entity.CurrentFrame != renderFrame)
        {
            renderFrame = entity.CurrentFrame;
            renderAlpha = 0f;
            renderStartPosition =
                ToUnityVector(entity.PreviousLogicalPosition);
            renderStartRotation =
                ToUnityRotation(entity.PreviousLogicalRotation);
        }

        float logicFrameDuration = ResolveLogicFrameDuration();
        renderAlpha = Mathf.Clamp01(
            renderAlpha +
            Time.deltaTime / logicFrameDuration);

        Vector3 desiredPosition = Vector3.Lerp(
            renderStartPosition,
            ToUnityVector(entity.LogicalPosition),
            renderAlpha);
        Quaternion desiredRotation = Quaternion.Slerp(
            renderStartRotation,
            ToUnityRotation(entity.LogicalRotation),
            renderAlpha);

        if (visualErrorCorrectionActive)
        {
            float decay = Mathf.Clamp01(
                Time.deltaTime /
                Mathf.Max(rollbackCorrectionDuration, 0.01f));

            visualPositionError = Vector3.Lerp(
                visualPositionError,
                Vector3.zero,
                decay);
            visualRotationError = Quaternion.Slerp(
                visualRotationError,
                Quaternion.identity,
                decay);

            desiredPosition += visualPositionError;
            desiredRotation =
                desiredRotation * visualRotationError;

            if (visualPositionError.sqrMagnitude < 0.000001f &&
                Quaternion.Angle(
                    visualRotationError,
                    Quaternion.identity) < 0.05f)
            {
                visualErrorCorrectionActive = false;
                visualPositionError = Vector3.zero;
                visualRotationError = Quaternion.identity;
            }
        }

        transform.SetPositionAndRotation(
            desiredPosition,
            desiredRotation);
    }

    private float ResolveLogicFrameDuration()
    {
        CombatWorld world = CombatSimulationRunner.World;
        float duration = world != null
            ? world.FixedDeltaTime.ToFloat()
            : 0.05f;
        return Mathf.Max(duration, 0.0001f);
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

    /// <summary>
    /// 回滚重演完成事件入口。
    ///
    /// 实际视觉判断延迟到下一次 Update，因为回滚事件之后同一 FixedUpdate
    /// 可能马上又预测了下一帧，应以最终逻辑状态作为校正目标。
    /// </summary>
    public void HandleRollbackVisual(long frameId)
    {
        pendingRollbackVisualCorrection = true;
        ReturnToLocomotionAfterRollback();
    }

    private void ApplyRollbackVisualCorrection()
    {
        Vector3 logicalPosition =
            ToUnityVector(entity.LogicalPosition);
        Quaternion logicalRotation =
            ToUnityRotation(entity.LogicalRotation);
        float deviation = Vector3.Distance(
            transform.position,
            logicalPosition);

        if (deviation < 0.02f)
        {
            return;
        }

        // 保留当前看到的模型位置与新逻辑位置之间的误差，
        // 后续渲染帧通过 offset 连续衰减追平，而不是直接 Snap。
        visualPositionError =
            transform.position - logicalPosition;
        visualRotationError =
            transform.rotation *
            Quaternion.Inverse(logicalRotation);
        visualErrorCorrectionActive = true;

        ResetRenderInterpolationToCurrent();

        if (deviation > strongRollbackFlashDistance &&
            enableStrongRollbackFlash &&
            acceptPlayerInput)
        {
            RollbackScreenFlash.Trigger();
        }
    }

    private void ReturnToLocomotionAfterRollback()
    {
        if (animator == null ||
            entity.AttackState != AttackState.None ||
            entity.IsHurt ||
            entity.IsDead)
        {
            return;
        }

        animator.ResetTrigger("LeftPunch");
        animator.ResetTrigger("RightPunch");
        animator.ResetTrigger("JumpAttack");
        animator.ResetTrigger("HitReaction");
        animator.ResetTrigger("Death");

        activeActionStateName = null;
        smoothedMoveAmount = entity.IsMoving ? 1f : 0f;
        moveIntentTimer = entity.IsMoving
            ? moveAnimationDelay
            : 0f;
        animator.SetFloat("moveAmount", smoothedMoveAmount);
        SetActionLayerActive(false);
        animator.CrossFadeInFixedTime(
            locomotionStateName,
            rollbackCrossFadeTime,
            0);
    }

    private void ResetRenderInterpolationToCurrent()
    {
        renderFrame = entity.CurrentFrame;
        renderAlpha = 1f;
        renderStartPosition =
            ToUnityVector(entity.LogicalPosition);
        renderStartRotation =
            ToUnityRotation(entity.LogicalRotation);
    }

    /// <summary>
    /// 强制把视觉对象一次性同步到最终逻辑状态。
    /// 只用于很大的预测错误。
    /// </summary>
    public void SnapVisualToLogic()
    {
        if (!initialized || entity == null)
        {
            return;
        }

        ultimateVisualSyncEndFrame = -1;
        visualErrorCorrectionActive = false;
        visualPositionError = Vector3.zero;
        visualRotationError = Quaternion.identity;
        pendingRollbackVisualCorrection = false;
        ResetRenderInterpolationToCurrent();
        transform.SetPositionAndRotation(
            ToUnityVector(entity.LogicalPosition),
            ToUnityRotation(entity.LogicalRotation));
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

        moveAnimationDelay = Mathf.Max(0f, moveAnimationDelay);
        moveAnimationBlendTime =
            Mathf.Max(0.01f, moveAnimationBlendTime);
        strongRollbackFlashDistance =
            Mathf.Max(0f, strongRollbackFlashDistance);
        rollbackCorrectionDuration =
            Mathf.Max(0.01f, rollbackCorrectionDuration);
        actionCrossFadeTime =
            Mathf.Max(0f, actionCrossFadeTime);
        maxActionVisualDuration =
            Mathf.Max(0.1f, maxActionVisualDuration);
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

/// <summary>
/// 强回滚时的一次性全屏微闪。
/// 只在预测误差超过 Snap 阈值且发生在本地玩家时触发。
/// </summary>
internal sealed class RollbackScreenFlash : MonoBehaviour
{
    private const float FlashDuration = 0.08f;
    private const float FlashAlpha = 0.18f;

    private static RollbackScreenFlash instance;
    private float flashEndTime;

    public static void Trigger()
    {
        if (instance == null)
        {
            GameObject flashObject =
                new GameObject("[Rollback Screen Flash]");
            DontDestroyOnLoad(flashObject);
            instance =
                flashObject.AddComponent<RollbackScreenFlash>();
        }

        instance.flashEndTime =
            Time.unscaledTime + FlashDuration;
    }

    private void OnGUI()
    {
        float remaining = flashEndTime - Time.unscaledTime;
        if (remaining <= 0f)
        {
            return;
        }

        float alpha =
            FlashAlpha * Mathf.Clamp01(remaining / FlashDuration);
        Color previousColor = GUI.color;
        GUI.depth = -1000;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(
            new Rect(0f, 0f, Screen.width, Screen.height),
            Texture2D.whiteTexture);
        GUI.color = previousColor;
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }
}

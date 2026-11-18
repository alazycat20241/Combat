using System;
using System.Collections.Generic;
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
    private static readonly List<PlayerEntity> ActiveEntities = new List<PlayerEntity>(4);
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

    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Animator animator;

    [Header("输入 Action（留空自动读取键鼠）")]
    [SerializeField] private InputActionReference moveInputReference;
    [SerializeField] private InputActionReference primaryAttackInputReference;
    [SerializeField] private InputActionReference secondaryAttackInputReference;
    [SerializeField] private InputActionReference ultimateInputReference;

    [Header("表现平滑")]
    [SerializeField, Min(0f)] private float visualFollowSharpness = 25f;
    [SerializeField, Min(0f)] private float animationDampTime = 0.2f;

    [Header("Animator 根运动预采样")]
    [SerializeField] private bool useAnimatorRootMotion = true;
    [SerializeField, Min(0)] private int attackAnimationLayer = 1;
    [SerializeField] private Transform ultimateLandingProbe;
    [SerializeField, Min(0f)] private float ultimateLandingHeightTolerance = 0.03f;

    [Header("调试信息")]
    [SerializeField] private int runtimeEntityId;
    [SerializeField] private int ultimateRootMotionFrameCount;
    [SerializeField] private int ultimateLandingFrame;
    [SerializeField] private float ultimateRootMotionDistance;

    private PlayerEntity entity;
    private Vector2 moveInput;
    private bool primaryAttackInputLatched;
    private bool secondaryAttackInputLatched;
    private bool ultimateInputLatched;
    private int ultimateVisualSyncEndFrame = -1;
    private bool initialized;

    public PlayerEntity Entity => entity;
    public PlayerCombatConfig Config => config;
    public float CurrentHP => entity?.CurrentHP ?? 0f;
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

            if (ultimateLandingProbe == null)
            {
                ultimateLandingProbe = animator.GetBoneTransform(HumanBodyBones.Hips);
            }
        }

        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        runtimeEntityId = nextRuntimeEntityId++;
        entity = new PlayerEntity(
            runtimeEntityId,
            config,
            ToLogicVector(transform.position),
            ToLogicRotation(transform.rotation));

        SampleAndInjectAttackMotion();
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
            if (!ActiveEntities.Contains(entity))
            {
                ActiveEntities.Add(entity);
            }

            entity.CombatEvent -= OnCombatEvent;
            entity.CombatEvent += OnCombatEvent;
        }
    }

    private void OnDisable()
    {
        if (entity == null)
        {
            return;
        }

        entity.CombatEvent -= OnCombatEvent;
        ActiveEntities.Remove(entity);
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

        CombatInputFrame input = new CombatInputFrame
        {
            PrimaryAttackPressed = primaryAttackInputLatched,
            SecondaryAttackPressed = secondaryAttackInputLatched,
            UltimatePressed = ultimateInputLatched
        };

        LogicVector3 moveDirection = BuildWorldMoveDirection(moveInput);
        input.HasMoveInput = moveDirection.SqrMagnitude > 0.000001f;
        input.MoveDirection = moveDirection;

        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;

        entity.SetPreferredTarget(
            combatTarget != null && combatTarget.Entity != null
                ? combatTarget.Entity.EntityId
                : 0);

        entity.Tick(input, Time.fixedDeltaTime, ActiveEntities);
    }

    private void OnCombatEvent(CombatEvent combatEvent)
    {
        switch (combatEvent.Type)
        {
            case CombatEventType.AttackStarted:
                SetAnimatorTrigger(combatEvent.AttackState.ToString());
                break;
            case CombatEventType.HitReaction:
                SetAnimatorTrigger("HitReaction");
                break;
            case CombatEventType.Died:
                SetAnimatorTrigger("Death");
                ultimateVisualSyncEndFrame = Time.frameCount + 2;
                break;
        }
    }

    private LogicVector3 BuildWorldMoveDirection(Vector2 input)
    {
        if (!entity.CanMove || input.sqrMagnitude < config.MoveInputDeadZone * config.MoveInputDeadZone)
        {
            return LogicVector3.Zero;
        }

        input = Vector2.ClampMagnitude(input, 1f);

        Vector3 cameraForward = cameraTransform != null
            ? cameraTransform.forward
            : Vector3.forward;
        Vector3 cameraRight = cameraTransform != null
            ? cameraTransform.right
            : Vector3.right;

        cameraForward.y = 0f;
        cameraRight.y = 0f;
        cameraForward.Normalize();
        cameraRight.Normalize();

        Vector3 direction = (cameraForward * input.y) + (cameraRight * input.x);
        return ToLogicVector(direction);
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

    private void SampleAndInjectAttackMotion()
    {
        AttackMotionProfile profile = SampleAnimatorRootMotion();
        if (profile == null)
        {
            Debug.LogWarning(
                "JumpAttack 没有采样到可用根运动，PlayerEntity 将使用 Luban/默认前冲距离。",
                this);
            return;
        }

        ultimateRootMotionFrameCount = profile.FrameCount;
        ultimateLandingFrame = profile.LandingFrame;
        ultimateRootMotionDistance = profile.HorizontalDistance;

        entity.SetAttackMotionProfile(AttackState.JumpAttack, profile);

        Debug.Log(
            $"JumpAttack 根运动采样完成：{profile.FrameCount} 帧，水平净位移 " +
            $"{profile.HorizontalDistance:F3} 米，落地帧 " +
            $"{(profile.LandingFrame > 0 ? profile.LandingFrame.ToString() : "未检测到")}。",
            this);
    }

    /// <summary>
    /// Animator 数据只在表现层采样一次，最终转换成纯 LogicVector3 数组注入实体。
    /// 实体本身不知道 Animator 的存在。
    /// </summary>
    private AttackMotionProfile SampleAnimatorRootMotion()
    {
        if (!useAnimatorRootMotion ||
            animator == null ||
            animator.layerCount <= 0)
        {
            return null;
        }

        int layer = Mathf.Clamp(attackAnimationLayer, 0, animator.layerCount - 1);
        int stateHash = Animator.StringToHash(AttackState.JumpAttack.ToString());

        Vector3 savedPosition = animator.transform.position;
        Quaternion savedRotation = animator.transform.rotation;
        bool savedApplyRootMotion = animator.applyRootMotion;
        AnimatorCullingMode savedCullingMode = animator.cullingMode;

        try
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = true;
            animator.Rebind();
            animator.Play(stateHash, layer, 0f);
            animator.Update(0f);

            AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(layer);
            int maxFrameCount = Mathf.Max(
                1,
                Mathf.CeilToInt(stateInfo.length / Time.fixedDeltaTime));

            Vector3 previousRootPosition = animator.rootPosition;
            float startRootHeight = previousRootPosition.y;
            Quaternion inverseStartRotation = Quaternion.Inverse(savedRotation);

            Transform landingProbe = ultimateLandingProbe;
            if (landingProbe == null)
            {
                landingProbe = animator.GetBoneTransform(HumanBodyBones.Hips);
            }

            float previousProbeHeight = landingProbe != null
                ? landingProbe.position.y
                : startRootHeight;
            float startProbeHeight = previousProbeHeight;

            LogicVector3[] sampledFrames = new LogicVector3[maxFrameCount];
            LogicVector3 cumulativeDisplacement = LogicVector3.Zero;
            float furthestDistanceSqr = 0f;
            int furthestFrame = 0;
            int lastHorizontalFrame = 0;
            int landingFrame = 0;
            int validFrameCount = 0;
            bool rootWasAirborne = false;
            bool probeWasAirborne = false;

            for (int frameIndex = 0; frameIndex < maxFrameCount; frameIndex++)
            {
                animator.Update(Time.fixedDeltaTime);

                AnimatorStateInfo currentState = animator.GetCurrentAnimatorStateInfo(layer);
                if (currentState.shortNameHash != stateHash ||
                    animator.IsInTransition(layer))
                {
                    // 状态退出时 rootPosition 可能重置，不能把重置差值计入冲刺。
                    break;
                }

                Vector3 currentRootPosition = animator.rootPosition;
                float previousRootHeight = previousRootPosition.y;
                float currentRootHeight = currentRootPosition.y;
                float rootAirborneThreshold =
                    startRootHeight + ultimateLandingHeightTolerance;

                if (currentRootHeight > rootAirborneThreshold)
                {
                    rootWasAirborne = true;
                }
                else if (rootWasAirborne &&
                         previousRootHeight > rootAirborneThreshold &&
                         currentRootHeight <= rootAirborneThreshold)
                {
                    landingFrame = frameIndex + 1;
                    rootWasAirborne = false;
                }

                if (landingProbe != null && landingFrame <= 0)
                {
                    float currentProbeHeight = landingProbe.position.y;
                    float probeAirborneThreshold =
                        startProbeHeight + ultimateLandingHeightTolerance;

                    if (currentProbeHeight > probeAirborneThreshold)
                    {
                        probeWasAirborne = true;
                    }
                    else if (probeWasAirborne &&
                             previousProbeHeight > probeAirborneThreshold &&
                             currentProbeHeight <= probeAirborneThreshold)
                    {
                        landingFrame = frameIndex + 1;
                        probeWasAirborne = false;
                    }

                    previousProbeHeight = currentProbeHeight;
                }

                Vector3 worldDelta = currentRootPosition - previousRootPosition;
                Vector3 localDelta = inverseStartRotation * worldDelta;
                localDelta.y = 0f;

                LogicVector3 logicDelta = ToLogicVector(localDelta);
                sampledFrames[frameIndex] = logicDelta;
                cumulativeDisplacement += logicDelta;
                validFrameCount++;

                float currentDistanceSqr = cumulativeDisplacement.SqrMagnitude;
                if (currentDistanceSqr > furthestDistanceSqr)
                {
                    furthestDistanceSqr = currentDistanceSqr;
                    furthestFrame = validFrameCount;
                }

                if (logicDelta.SqrMagnitude > 0.000001f)
                {
                    lastHorizontalFrame = frameIndex + 1;
                }

                previousRootPosition = currentRootPosition;

                if (landingFrame > 0)
                {
                    break;
                }
            }

            if (validFrameCount <= 0)
            {
                return null;
            }

            int usableFrameCount = validFrameCount;
            if (landingFrame > 0)
            {
                usableFrameCount = Math.Min(landingFrame, validFrameCount);
            }
            else if (furthestFrame > 0 &&
                     cumulativeDisplacement.SqrMagnitude <
                     furthestDistanceSqr * 0.81f)
            {
                // 动画尾段回拉时只保留离起点最远的一段。
                usableFrameCount = furthestFrame;
            }

            LogicVector3[] finalFrames = new LogicVector3[usableFrameCount];
            Array.Copy(sampledFrames, finalFrames, usableFrameCount);

            cumulativeDisplacement = LogicVector3.Zero;
            for (int i = 0; i < finalFrames.Length; i++)
            {
                cumulativeDisplacement += finalFrames[i];
            }

            if (finalFrames.Length <= 0 ||
                (cumulativeDisplacement.SqrMagnitude <= 0.000001f && lastHorizontalFrame <= 0))
            {
                return null;
            }

            return new AttackMotionProfile
            {
                FrameDeltas = finalFrames,
                LandingFrame = landingFrame,
                LastHorizontalFrame = Math.Min(lastHorizontalFrame, finalFrames.Length),
                HorizontalDistance = cumulativeDisplacement.Magnitude
            };
        }
        finally
        {
            if (animator != null)
            {
                animator.applyRootMotion = savedApplyRootMotion;
                animator.cullingMode = savedCullingMode;
                animator.Rebind();
                animator.Update(0f);
                animator.transform.SetPositionAndRotation(savedPosition, savedRotation);
            }
        }
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
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static LogicVector3 ToLogicVector(Vector3 value)
    {
        return new LogicVector3(value.x, value.y, value.z);
    }

    private static Quaternion ToUnityRotation(LogicRotation rotation)
    {
        return Quaternion.Euler(0f, rotation.YawRadians * Mathf.Rad2Deg, 0f);
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
        attackAnimationLayer = Mathf.Max(0, attackAnimationLayer);
        ultimateLandingHeightTolerance =
            Mathf.Max(0f, ultimateLandingHeightTolerance);
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
            (drawConfig.MapMinX + drawConfig.MapMaxX) * 0.5f,
            unityPosition.y,
            (drawConfig.MapMinZ + drawConfig.MapMaxZ) * 0.5f);
        Vector3 mapSize = new Vector3(
            Mathf.Abs(drawConfig.MapMaxX - drawConfig.MapMinX),
            0.02f,
            Mathf.Abs(drawConfig.MapMaxZ - drawConfig.MapMinZ));
        Gizmos.DrawWireCube(mapCenter, mapSize);
    }
}

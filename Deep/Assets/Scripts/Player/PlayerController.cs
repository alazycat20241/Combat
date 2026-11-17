using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// 三段攻击的标识。
/// None 表示当前没有攻击；其余三个值同时用来匹配 Animator 的 Trigger 名称。
/// </summary>
public enum AttackState
{
    None,
    LeftPunch,
    RightPunch,
    JumpAttack
}

/// <summary>
/// 单段攻击的数据。
/// 所有时间都以“逻辑帧”为单位，而不是以渲染帧或 Animator 当前进度为单位。
/// 这样可以保证战斗节奏由 20Hz 的 FixedUpdate 逻辑帧决定，动画只负责表现。
/// </summary>
[Serializable]
public sealed class CombatAttackDefinition
{
    [Tooltip("这段数据对应哪一段攻击")]
    public AttackState attackState = AttackState.LeftPunch;

    [Header("逻辑帧时序")]
    [Min(1)]
    [Tooltip("判定开始帧，按 1 开始计数。需求中的 5 表示第 5 个逻辑帧开始判定")]
    public int activeStartFrame = 5;

    [Min(1)]
    [Tooltip("判定结束帧，包含这一帧。需求中的 10 表示第 5~10 帧都进行判定")]
    public int activeEndFrame = 10;

    [Min(0)]
    [Tooltip("后摇持续的逻辑帧数。判定最后一帧之后立即进入后摇")]
    public int recoveryFrames = 7;

    [Header("攻击自身前冲")]
    [Min(0f)]
    [Tooltip("这一段攻击总共向自身前方移动的距离，单位为米")]
    public float forwardMoveDistance = 0.35f;

    [Min(1)]
    [Tooltip("前冲开始帧，按 1 开始计数")]
    public int forwardMoveStartFrame = 1;

    [Min(1)]
    [Tooltip("前冲结束帧，包含这一帧。默认在第 1~4 帧完成，第 5 帧开始攻击判定")]
    public int forwardMoveEndFrame = 4;

    [Header("伤害与击退")]
    [Min(0f)]
    public float damage = 10f;

    [Min(0f)]
    [Tooltip("受击者获得的反向初速度，单位为米/秒")]
    public float knockbackSpeed = 5f;

    [Header("纯数学 AABB")]
    [Tooltip("攻击框的尺寸。攻击框不读取 Collider，完全由这里的数据构造")]
    public Vector3 attackBoxSize = new Vector3(1.25f, 0.9f, 1.4f);

    [Tooltip("攻击框中心相对角色逻辑位置与逻辑朝向的偏移")]
    public Vector3 attackBoxLocalOffset = new Vector3(0f, 0.9f, 0.8f);
}

/// <summary>
/// 当前角色共用的战斗脚本。
///
/// 职责分工：
/// 1. Update：只采集玩家输入，并让可见 Transform 平滑追向逻辑状态。
/// 2. FixedUpdate：按固定步长推进 logicalPosition、攻击帧、受击计时和击退速度。
/// 3. 纯数学 Bounds：负责攻击框与受击框相交，不依赖物理引擎的碰撞回调。
///
/// 当前数字仍是 float，这是单机原型的实现。未来接入 Fix64 锁步时，可把
/// logicalPosition、logicalRotation、knockbackVelocity 以及攻击计时替换为定点类型，
/// 同时保留“输入采集和逻辑推进分离”的结构。
/// </summary>
public sealed class PlayerController : MonoBehaviour
{
    private enum AttackPhase
    {
        None,
        Windup,
        Active,
        Recovery
    }

    private const float EPSILON = 0.0001f;

    [Header("引用")]
    [Tooltip("用于摄像机相对移动。留空时自动使用 Camera.main")]
    [SerializeField] private Transform cameraTransform;

    [Tooltip("Animator 留空时会尝试 GetComponent<Animator>()")]
    [SerializeField] private Animator animator;

    [Tooltip("取消勾选后，这个角色只接收受击，不读取玩家输入。适合把第二个 Prefab 当作训练木桩")]
    [SerializeField] private bool acceptPlayerInput = true;

    [Tooltip("可以手动拖入场景里的另一个 PlayerController。为空时自动寻找，并且只在找不到其他玩家时允许打自己")]
    public PlayerController combatTarget;

    [Header("基础移动")]
    [SerializeField, Min(0f)] private float moveSpeed = 6f;

    [Tooltip("输入小于此值时视为摇杆回中，避免轻微漂移")]
    [SerializeField, Range(0f, 0.5f)] private float moveInputDeadZone = 0.1f;

    [Tooltip("数值越大，角色转向目标方向越快")]
    [FormerlySerializedAs("rotationSpeed")]
    [SerializeField, Min(0f)] private float rotationSharpness = 18f;

    [Tooltip("数值越大，渲染 Transform 追向 logicalPosition 越快")]
    [SerializeField, Min(0f)] private float visualFollowSharpness = 25f;

    [FormerlySerializedAs("animationDampTime")]
    [Tooltip("移动动画参数 moveAmount 的平滑时间，沿用原有 Prefab 的 0.2")]
    [SerializeField, Min(0f)] private float animationDampTime = 0.2f;

    [Header("地图边界（X/Z 平面）")]
    [SerializeField] private Vector2 mapMin = new Vector2(-10f, -10f);
    [SerializeField] private Vector2 mapMax = new Vector2(10f, 10f);

    [Header("生命与受击")]
    [SerializeField, Min(1f)] private float maxHP = 100f;

    [Tooltip("受击硬直时间。它和动画长度相对独立，方便以后让服务端统一校验")]
    [SerializeField, Min(0f)] private float hitStunDuration = 0.4f;

    [Tooltip("击退速度每秒减少多少米/秒。使用 MoveTowards，衰减不受渲染帧率影响")]
    [SerializeField, Min(0f)] private float knockbackFriction = 28f;

    [Tooltip("角色自身受击框的大小。两个玩家是相同预制体时，用默认值即可")]
    [SerializeField] private Vector3 hurtBoxSize = new Vector3(0.8f, 1.8f, 0.8f);

    [Tooltip("受击框中心相对 logicalPosition 的偏移")]
    [SerializeField] private Vector3 hurtBoxLocalOffset = new Vector3(0f, 0.9f, 0f);

    [Tooltip("只有一个玩家时，是否允许攻击命中自己，用来快速测试战斗")]
    [SerializeField] private bool allowSelfHitWhenNoTarget = true;

    [Header("输入（可留空）")]
    [Tooltip("可选：拖入值为 Vector2 的 Move Action。留空时自动读取 WASD")]
    [SerializeField] private InputActionReference moveInputReference;

    [Tooltip("可选：拖入攻击 Button Action。留空时自动读取鼠标左键")]
    [SerializeField] private InputActionReference attackInputReference;

    [Tooltip("可选：拖入二段攻击 Button Action。留空时自动读取鼠标右键")]
    [SerializeField] private InputActionReference secondaryAttackInputReference;

    [Tooltip("可选：拖入大招 Button Action。留空时自动读取空格键")]
    [SerializeField] private InputActionReference ultimateInputReference;

    [Header("大招连击规则")]
    [Min(1)]
    [Tooltip("任意左右普攻累计命中多少次后，空格可以发动大招")]
    [SerializeField] private int requiredComboHits = 3;

    [Min(1)]
    [Tooltip("每次有效普攻命中后，连击计数还能保持多少逻辑帧")]
    [SerializeField] private int comboCounterWindowFrames = 30;

    [Header("三段攻击数据")]
    [Tooltip("按需求强制使用 5~10 帧判定；后摇可在 Inspector 中独立调整")]
    [SerializeField]
    private CombatAttackDefinition[] attackDefinitions =
    {
        new CombatAttackDefinition
        {
            attackState = AttackState.LeftPunch,
            activeStartFrame = 5,
            activeEndFrame = 10,
            recoveryFrames = 7,
            forwardMoveDistance = 0.35f,
            forwardMoveStartFrame = 1,
            forwardMoveEndFrame = 4,
            damage = 8f,
            knockbackSpeed = 4.5f,
            attackBoxSize = new Vector3(1.25f, 0.9f, 1.4f),
            attackBoxLocalOffset = new Vector3(0f, 0.9f, 0.8f)
        },
        new CombatAttackDefinition
        {
            attackState = AttackState.RightPunch,
            activeStartFrame = 5,
            activeEndFrame = 10,
            recoveryFrames = 8,
            forwardMoveDistance = 0.45f,
            forwardMoveStartFrame = 1,
            forwardMoveEndFrame = 4,
            damage = 10f,
            knockbackSpeed = 5.5f,
            attackBoxSize = new Vector3(1.35f, 0.9f, 1.45f),
            attackBoxLocalOffset = new Vector3(0f, 0.9f, 0.85f)
        },
        new CombatAttackDefinition
        {
            attackState = AttackState.JumpAttack,
            activeStartFrame = 5,
            activeEndFrame = 10,
            recoveryFrames = 10,
            forwardMoveDistance = 0.8f,
            forwardMoveStartFrame = 1,
            forwardMoveEndFrame = 5,
            damage = 18f,
            knockbackSpeed = 8f,
            attackBoxSize = new Vector3(1.7f, 1.2f, 1.8f),
            attackBoxLocalOffset = new Vector3(0f, 1.05f, 0.95f)
        }
    };

    [Header("Animator 根运动采样")]
    [Tooltip("是否用 Animator.rootPosition 采样大招每一帧的位移")]
    [SerializeField] private bool useAnimatorRootMotion = true;

    [Tooltip("攻击状态所在的 Animator Layer。当前工程中攻击状态在 Override Layer，索引为 1")]
    [SerializeField, Min(0)] private int attackAnimationLayer = 1;

    [Tooltip("第三击和大招命中的击退距离相对大招根运动距离的倍数")]
    [SerializeField, Min(0f)] private float ultimateKnockbackDistanceScale = 1f;

    [Header("大招落地判定")]
    [Tooltip("无法从 rootPosition 找到落地时，退回到动画的哪个进度触发命中")]
    [SerializeField, Range(0f, 1f)] private float ultimateHitNormalizedTime = 0.9f;

    [Tooltip("根位置 Y 回到起跳高度的这个误差范围内时，视为落地")]
    [SerializeField, Min(0f)] private float ultimateLandingHeightTolerance = 0.03f;

    [Tooltip("可选的落地检测骨骼。留空时自动使用 Humanoid 的 Hips 骨骼")]
    [SerializeField] private Transform ultimateLandingProbe;

    [Tooltip("大招判定持续几个逻辑帧。默认 1 帧，只结算一次")]
    [SerializeField, Min(1)] private int ultimateHitWindowFrames = 1;

    // 当前所有启用的玩家。用静态列表代替每次攻击都 FindObjectsOfType，避免检测时分配内存。
    private static readonly List<PlayerController> ActivePlayers = new List<PlayerController>();
    private readonly List<PlayerController> targetBuffer = new List<PlayerController>(2);
    private readonly HashSet<int> hitTargetsThisAttack = new HashSet<int>();

    // logicalPosition / logicalRotation 才是战斗逻辑真正使用的位置和朝向。
    // Transform 只是它们在某一个渲染帧上的视觉表现。
    [SerializeField] private Vector3 logicalPosition;
    [SerializeField] private Quaternion logicalRotation = Quaternion.identity;

    private Vector2 moveInput;
    private bool primaryAttackInputLatched;
    private bool secondaryAttackInputLatched;
    private bool ultimateInputLatched;

    [Header("运行时状态（运行后观察）")]
    [SerializeField] private float currentHP;
    [SerializeField] private bool isHurt;
    private float hurtTimer;
    private Vector3 knockbackVelocity;
    [SerializeField] private bool isDead;

    [SerializeField] private AttackState attackState = AttackState.None;
    private AttackPhase attackPhase = AttackPhase.None;
    private int attackFrame;

    // 缓冲的是“下一段攻击类型”，不再假设每一下都必须按左键。
    private AttackState comboBufferedAttack = AttackState.None;

    // 左键和右键都计入同一套三段连击；JumpAttack 只作为空格大招。
    [SerializeField] private int comboHitCount;
    private int comboCounterWindowRemaining;
    private bool ultimateInputBuffered;

    // Animator 采样得到的局部位移，每一项对应一个大招逻辑帧。
    private Vector3[] ultimateRootMotionPerFrame;
    [SerializeField] private int ultimateRootMotionFrameCount;
    [SerializeField] private int ultimateRootMotionLastHorizontalFrame;
    [SerializeField] private int ultimateLandingFrame;
    [SerializeField] private int ultimateResolvedHitFrame;
    [SerializeField] private float ultimateRootMotionDistance;

    private Vector3 ultimateStartLogicalPosition;
    private Vector3 ultimateFurthestLogicalPosition;
    private float ultimateFurthestDistanceSqr;
    private int ultimateVisualSyncEndFrame = -1;

    private bool initialized;

    public float HP => currentHP;
    public float MaxHP => maxHP;
    public bool IsHurt => isHurt;
    public bool IsDead => isDead;
    public AttackState CurrentAttackState => attackState;
    public int CurrentAttackFrame => attackFrame;
    public int ComboHitCount => comboHitCount;
    public bool UltimateReady => comboHitCount >= requiredComboHits;
    public float UltimateRootMotionDistance => ultimateRootMotionDistance;
    public Vector3 LogicalPosition => logicalPosition;
    public bool AcceptPlayerInput
    {
        get => acceptPlayerInput;
        set => acceptPlayerInput = value;
    }

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        // 位移完全由 logicalPosition 控制；关闭 Root Motion，避免 Animator 再写一次 Position。
        if (animator != null)
        {
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

        if (attackDefinitions == null || attackDefinitions.Length == 0)
        {
            attackDefinitions = CreateDefaultAttackDefinitions();
        }

        // 在正式战斗开始前采样一次大招根运动，避免第一次放招时才卡顿。
        SampleUltimateRootMotion();

        logicalPosition = transform.position;
        logicalRotation = transform.rotation;
        currentHP = maxHP;
        isDead = false;
        isHurt = false;
        hurtTimer = 0f;
        attackState = AttackState.None;
        attackPhase = AttackPhase.None;
        attackFrame = 0;
        comboBufferedAttack = AttackState.None;
        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;
        comboHitCount = 0;
        comboCounterWindowRemaining = 0;
        ultimateInputBuffered = false;
        knockbackVelocity = Vector3.zero;
        initialized = true;

        // 攻击帧“第 5~10 帧”依赖 FixedUpdate 的真实频率。这里只检查，不擅自修改全局 Time，
        // 避免一个角色脚本影响工程里的其他固定步长系统。
        if (!Mathf.Approximately(Time.fixedDeltaTime, 0.05f))
        {
            Debug.LogWarning(
                "PlayerController 需要 20Hz 逻辑帧。请把 Project Settings > Time > " +
                "Fixed Timestep 设置为 0.05。",
                this);
        }
    }

    private void OnEnable()
    {
        if (!ActivePlayers.Contains(this))
        {
            ActivePlayers.Add(this);
        }
    }

    private void OnDisable()
    {
        ActivePlayers.Remove(this);
    }

    /// <summary>
    /// Update 只做两件事：采集输入、让画面追向逻辑状态。
    /// 任何会影响战斗结果的计算都不放在这里，否则结果会受电脑帧率影响。
    /// </summary>
    private void Update()
    {
        if (!initialized)
        {
            return;
        }

        Vector2 sampledMoveInput = acceptPlayerInput ? ReadMoveInput() : Vector2.zero;
        if (isDead || isHurt || attackState != AttackState.None)
        {
            // 即使当前不允许移动，也把值清零，防止战斗结束后残留一个旧方向。
            moveInput = Vector2.zero;
        }
        else
        {
            moveInput = sampledMoveInput;
        }

        // 鼠标点击发生在两个 FixedUpdate 之间时，用锁存位保存到下一次逻辑帧。
        // 否则 Update 中点击、下一次 FixedUpdate 前又松开，攻击输入可能被漏掉。
        if (acceptPlayerInput && !isDead && !isHurt)
        {
            if (WasPrimaryAttackPressedThisUpdate())
            {
                primaryAttackInputLatched = true;
            }

            if (WasSecondaryAttackPressedThisUpdate())
            {
                secondaryAttackInputLatched = true;
            }

            if (WasUltimatePressedThisUpdate())
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

        // Animator 在 Update 之后还可能完成最后一次状态切换。
        // 大招结束后多保持两个 LateUpdate 帧，确保视觉 Transform 不会回跳。
        transform.SetPositionAndRotation(logicalPosition, logicalRotation);
    }

    /// <summary>
    /// FixedUpdate 是唯一推进战斗逻辑的地方。
    /// 项目应把 Edit > Project Settings > Time > Fixed Timestep 设置为 0.05 秒，
    /// 即每秒 20 个逻辑帧。
    /// </summary>
    private void FixedUpdate()
    {
        if (!initialized || isDead)
        {
            return;
        }

        bool primaryAttackPressed = primaryAttackInputLatched;
        bool secondaryAttackPressed = secondaryAttackInputLatched;
        bool ultimatePressed = ultimateInputLatched;
        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;

        // 先推进硬直计时，这样硬直恰好结束的那个逻辑帧就能接受新操作。
        UpdateHurtTimerAndApplyKnockback();

        HandleAttackInput(primaryAttackPressed, secondaryAttackPressed, ultimatePressed);
        TickAttack();
        TickComboCounterWindow();

        // 受击、攻击、死亡期间都不执行正常移动。死亡没有被取消，因此不会走到这里。
        if (isHurt || attackState != AttackState.None)
        {
            return;
        }

        ApplyCameraRelativeMovement(Time.fixedDeltaTime);
        ClampLogicalPositionToMap();
    }

    /// <summary>
    /// 新规则：
    /// 左右键都只是普通攻击，也都能参与同一套三段连击计数。
    /// 任意三次有效命中后进入 UltimateReady，按空格发动 JumpAttack 大招。
    /// 普攻中可以提前按空格，等当前攻击进入后摇时自动衔接大招。
    /// </summary>
    private void HandleAttackInput(
        bool primaryAttackPressed,
        bool secondaryAttackPressed,
        bool ultimatePressed)
    {
        if (isDead || isHurt)
        {
            return;
        }

        if (ultimatePressed && UltimateReady)
        {
            if (attackState == AttackState.None)
            {
                StartAttack(AttackState.JumpAttack);
                return;
            }

            if (attackPhase == AttackPhase.Windup || attackPhase == AttackPhase.Active)
            {
                ultimateInputBuffered = true;
                return;
            }

            if (attackPhase == AttackPhase.Recovery)
            {
                StartAttack(AttackState.JumpAttack);
                return;
            }
        }

        if (!primaryAttackPressed && !secondaryAttackPressed)
        {
            return;
        }

        // 左右键决定播放哪只手，但不会改变连击计数；两个键都能接下一次普攻。
        AttackState requestedAttack = primaryAttackPressed
            ? AttackState.LeftPunch
            : AttackState.RightPunch;

        if (attackState == AttackState.None)
        {
            StartAttack(requestedAttack);
            return;
        }

        if (attackPhase == AttackPhase.Windup || attackPhase == AttackPhase.Active)
        {
            comboBufferedAttack = requestedAttack;
        }
        else if (attackPhase == AttackPhase.Recovery)
        {
            // 后摇中继续按左右键，可以立即开始下一次普通攻击。
            StartAttack(requestedAttack);
        }
    }

    private void TickAttack()
    {
        if (attackState == AttackState.None)
        {
            return;
        }

        CombatAttackDefinition definition = GetDefinition(attackState);
        int activeStart = Mathf.Max(1, definition.activeStartFrame);
        int activeEnd = Mathf.Max(activeStart, definition.activeEndFrame);
        int recoveryEnd = activeEnd + Mathf.Max(0, definition.recoveryFrames);
        if (definition.attackState == AttackState.JumpAttack)
        {
            int animationFrameCount = ultimateRootMotionPerFrame != null
                ? ultimateRootMotionPerFrame.Length
                : Mathf.Max(recoveryEnd, activeEnd);

            // 优先使用 rootPosition/Hips 检测到的真实落地帧。
            int normalizedHitFrame = Mathf.Clamp(
                Mathf.CeilToInt(animationFrameCount * ultimateHitNormalizedTime),
                1,
                animationFrameCount);

            int landingHitFrame = Mathf.Clamp(
                ultimateLandingFrame,
                1,
                animationFrameCount);

            int horizontalEndHitFrame = Mathf.Clamp(
                ultimateRootMotionLastHorizontalFrame + 1,
                1,
                animationFrameCount);

            if (ultimateLandingFrame > 0)
            {
                activeStart = landingHitFrame;
            }
            else if (ultimateRootMotionLastHorizontalFrame > 0)
            {
                // Hips/root 都没有垂直数据时，以水平冲刺结束后的下一帧作为落地代理。
                activeStart = horizontalEndHitFrame;
            }
            else
            {
                activeStart = normalizedHitFrame;
            }

            ultimateResolvedHitFrame = activeStart;
            activeEnd = Mathf.Min(
                animationFrameCount,
                activeStart + ultimateHitWindowFrames - 1);

            // 按完整动画长度结束，确保最后一个根运动采样也写入 logicalPosition。
            recoveryEnd = Mathf.Max(recoveryEnd, animationFrameCount, activeEnd);
        }

        // 攻击帧从 1 开始更容易和“第 5 到第 10 帧”对应。
        attackFrame++;
        ApplyAttackForwardMovement(definition);

        if (attackFrame < activeStart)
        {
            attackPhase = AttackPhase.Windup;
            return;
        }

        if (attackFrame <= activeEnd)
        {
            attackPhase = AttackPhase.Active;
            DetectAttackHits(definition);
            return;
        }

        if (attackFrame <= recoveryEnd)
        {
            bool justEnteredRecovery = attackPhase != AttackPhase.Recovery;
            attackPhase = AttackPhase.Recovery;

            // 进入后摇的第一个逻辑帧消费输入。大招优先级高于下一段普攻。
            if (justEnteredRecovery)
            {
                if (ultimateInputBuffered && UltimateReady)
                {
                    StartAttack(AttackState.JumpAttack);
                    return;
                }

                if (comboBufferedAttack != AttackState.None)
                {
                    AttackState nextAttack = comboBufferedAttack;
                    comboBufferedAttack = AttackState.None;
                    StartAttack(nextAttack);
                    return;
                }
            }

            return;
        }

        FinishCurrentAttack();
    }

    private void StartAttack(AttackState nextState)
    {
        if (nextState == AttackState.None || isDead || isHurt)
        {
            return;
        }

        if (nextState == AttackState.JumpAttack)
        {
            // 大招已经消费完本次连击，立刻清空计数，避免一次普攻后重复放大招。
            comboHitCount = 0;
            comboCounterWindowRemaining = 0;
            FaceTargetForUltimate();
            ultimateStartLogicalPosition = logicalPosition;
            ultimateFurthestLogicalPosition = logicalPosition;
            ultimateFurthestDistanceSqr = 0f;
        }

        attackState = nextState;
        attackPhase = AttackPhase.Windup;
        attackFrame = 0;
        comboBufferedAttack = AttackState.None;
        ultimateInputBuffered = false;
        hitTargetsThisAttack.Clear();

        // 枚举名称正好就是需求指定的 Animator Trigger 名称。
        SetAnimatorTrigger(nextState.ToString());
    }

    /// <summary>
    /// 大招启动时把逻辑朝向转向最近的活目标。
    /// 根运动采样只记录动画自身的局部位移，实际施放时再用这个朝向旋转，
    /// 因此角色会朝敌人冲过去；最终命中仍然使用普通 AABB，打不到就不会扣血。
    /// </summary>
    private void FaceTargetForUltimate()
    {
        CollectTargets(targetBuffer);

        PlayerController nearestTarget = null;
        float nearestDistanceSqr = float.PositiveInfinity;

        for (int i = 0; i < targetBuffer.Count; i++)
        {
            PlayerController target = targetBuffer[i];
            if (target == null || target == this || target.isDead)
            {
                continue;
            }

            Vector3 toTarget = target.logicalPosition - logicalPosition;
            toTarget.y = 0f;
            float distanceSqr = toTarget.sqrMagnitude;
            if (distanceSqr > EPSILON && distanceSqr < nearestDistanceSqr)
            {
                nearestTarget = target;
                nearestDistanceSqr = distanceSqr;
            }
        }

        if (nearestTarget == null)
        {
            return;
        }

        Vector3 direction = nearestTarget.logicalPosition - logicalPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude <= EPSILON)
        {
            return;
        }

        logicalRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        transform.rotation = logicalRotation;
    }

    /// <summary>
    /// 启动前模拟播放 JumpAttack，并按逻辑帧读取 Animator.rootPosition。
    ///
    /// 采样方法：
    /// 1. 强制进入 JumpAttack 状态并把动画时间设为 0。
    /// 2. 每调用一次 Animator.Update(fixedDeltaTime)，读取一次 rootPosition。
    /// 3. 相邻两帧的 rootPosition 差值转换到角色初始朝向的局部空间。
    /// 4. 缓存每一帧的位移，真正放招时由 FixedUpdate 逐帧消费。
    ///
    /// 采样结束会恢复 Animator 和 Transform，避免预演动画在画面上闪一下。
    /// </summary>
    private void SampleUltimateRootMotion()
    {
        ultimateRootMotionPerFrame = null;
        ultimateRootMotionFrameCount = 0;
        ultimateRootMotionLastHorizontalFrame = 0;
        ultimateLandingFrame = 0;
        ultimateResolvedHitFrame = 0;
        ultimateRootMotionDistance = 0f;

        if (!useAnimatorRootMotion || animator == null || animator.layerCount <= 0)
        {
            return;
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
            int frameCount = Mathf.Max(
                1,
                Mathf.CeilToInt(stateInfo.length / Time.fixedDeltaTime));

            Vector3 previousRootPosition = animator.rootPosition;
            float startRootHeight = previousRootPosition.y;
            Transform landingProbe = ultimateLandingProbe;
            if (landingProbe == null)
            {
                landingProbe = animator.GetBoneTransform(HumanBodyBones.Hips);
            }

            float previousProbeHeight = landingProbe != null
                ? landingProbe.position.y
                : startRootHeight;
            float startProbeHeight = previousProbeHeight;
            Quaternion inverseStartRotation = Quaternion.Inverse(savedRotation);
            Vector3[] sampledFrames = new Vector3[frameCount];
            Vector3 cumulativeDisplacement = Vector3.zero;
            float furthestDistanceSqr = 0f;
            int furthestFrame = 0;
            int lastHorizontalFrame = 0;
            int validFrameCount = 0;
            bool wasAirborne = false;
            bool probeWasAirborne = false;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                animator.Update(Time.fixedDeltaTime);

                AnimatorStateInfo currentState = animator.GetCurrentAnimatorStateInfo(layer);
                if (currentState.shortNameHash != stateHash ||
                    animator.IsInTransition(layer))
                {
                    // 状态开始切回 Empty/Move 后，rootPosition 会重置。
                    // 这里必须停止采样，否则重置产生的反向位移会把前面的冲刺全部抵消。
                    break;
                }

                Vector3 currentRootPosition = animator.rootPosition;
                float previousRootHeight = previousRootPosition.y;
                float currentRootHeight = currentRootPosition.y;

                float airborneThreshold = startRootHeight + ultimateLandingHeightTolerance;
                if (currentRootHeight > airborneThreshold)
                {
                    wasAirborne = true;
                }
                else if (wasAirborne &&
                         previousRootHeight > airborneThreshold &&
                         currentRootHeight <= airborneThreshold)
                {
                    // 起跳后第一次从阈值上方回到阈值下方，就是动画的落地帧。
                    ultimateLandingFrame = frameIndex + 1;
                    wasAirborne = false;
                }

                if (landingProbe != null && ultimateLandingFrame <= 0)
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
                        // rootPosition 不包含垂直运动时，Hips 的高度曲线仍能提供落地时刻。
                        ultimateLandingFrame = frameIndex + 1;
                        probeWasAirborne = false;
                    }

                    previousProbeHeight = currentProbeHeight;
                }

                Vector3 worldDelta = currentRootPosition - previousRootPosition;
                Vector3 localDelta = inverseStartRotation * worldDelta;
                localDelta.y = 0f;

                sampledFrames[frameIndex] = localDelta;
                cumulativeDisplacement += localDelta;
                validFrameCount++;

                float currentDistanceSqr = cumulativeDisplacement.sqrMagnitude;
                if (currentDistanceSqr > furthestDistanceSqr)
                {
                    furthestDistanceSqr = currentDistanceSqr;
                    furthestFrame = validFrameCount;
                }

                if (localDelta.sqrMagnitude > EPSILON * EPSILON)
                {
                    lastHorizontalFrame = frameIndex + 1;
                }

                previousRootPosition = currentRootPosition;

                if (ultimateLandingFrame > 0)
                {
                    // 只保留到落地帧，避免随后的回到 Idle 位移污染最终坐标。
                    break;
                }
            }

            if (validFrameCount > 0)
            {
                int usableFrameCount = validFrameCount;

                // 如果没有识别到 Y 轴落地，但动画尾端把角色往起点方向拉回去，
                // 就截断到离起点最远的一帧，避免最终坐标被反向重置抵消。
                if (ultimateLandingFrame > 0)
                {
                    usableFrameCount = Mathf.Min(ultimateLandingFrame, validFrameCount);
                }
                else if (furthestFrame > 0 &&
                         cumulativeDisplacement.sqrMagnitude <
                         furthestDistanceSqr * 0.81f)
                {
                    usableFrameCount = furthestFrame;
                }

                if (usableFrameCount < sampledFrames.Length)
                {
                    Array.Resize(ref sampledFrames, usableFrameCount);
                }

                cumulativeDisplacement = Vector3.zero;
                for (int i = 0; i < sampledFrames.Length; i++)
                {
                    cumulativeDisplacement += sampledFrames[i];
                }

                ultimateRootMotionPerFrame = sampledFrames;
                ultimateRootMotionFrameCount = sampledFrames.Length;
                ultimateRootMotionLastHorizontalFrame = lastHorizontalFrame;
                ultimateRootMotionDistance = cumulativeDisplacement.magnitude;
                Debug.Log(
                    $"JumpAttack 根运动采样完成：{sampledFrames.Length} 帧，水平净位移 " +
                    $"{ultimateRootMotionDistance:F3} 米，落地判定帧 " +
                    $"{(ultimateLandingFrame > 0 ? ultimateLandingFrame.ToString() : "未检测到")}。",
                    this);
            }
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

        if (ultimateRootMotionPerFrame == null)
        {
            Debug.LogWarning(
                "JumpAttack 没有从 Animator.rootPosition 采样到水平位移，将回退到攻击数据里的前冲距离。",
                this);
        }
    }

    /// <summary>
    /// 攻击前冲完全由逻辑帧驱动。
    /// 前冲距离在配置帧区间内均摊到每一个 FixedUpdate，而不是在触发攻击时一次性瞬移，
    /// 因此总距离可控，也不会受渲染帧率影响。角色朝向在攻击期间不变，所以使用
    /// StartAttack 时已经确定的 logicalRotation 作为唯一前进方向。
    /// </summary>
    private void ApplyAttackForwardMovement(CombatAttackDefinition definition)
    {
        if (definition.attackState == AttackState.JumpAttack &&
            ultimateRootMotionPerFrame != null &&
            ultimateRootMotionDistance > EPSILON)
        {
            int sampleIndex = attackFrame - 1;
            if (sampleIndex >= 0 && sampleIndex < ultimateRootMotionPerFrame.Length)
            {
                Vector3 sampledLocalDelta = ultimateRootMotionPerFrame[sampleIndex];
                logicalPosition += logicalRotation * sampledLocalDelta;
                ClampLogicalPositionToMap();
                TrackUltimateFurthestPosition();
            }

            return;
        }

        float totalDistance = Mathf.Max(0f, definition.forwardMoveDistance);
        if (totalDistance <= EPSILON)
        {
            return;
        }

        int moveStart = Mathf.Max(1, definition.forwardMoveStartFrame);
        int moveEnd = Mathf.Max(moveStart, definition.forwardMoveEndFrame);
        if (attackFrame < moveStart || attackFrame > moveEnd)
        {
            return;
        }

        int moveFrameCount = moveEnd - moveStart + 1;
        float distanceThisFrame = totalDistance / moveFrameCount;

        Vector3 forward = logicalRotation * Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= EPSILON)
        {
            return;
        }

        logicalPosition += forward.normalized * distanceThisFrame;
        ClampLogicalPositionToMap();

        if (definition.attackState == AttackState.JumpAttack)
        {
            TrackUltimateFurthestPosition();
        }
    }

    private void TrackUltimateFurthestPosition()
    {
        Vector3 displacement = logicalPosition - ultimateStartLogicalPosition;
        displacement.y = 0f;
        float distanceSqr = displacement.sqrMagnitude;

        if (distanceSqr > ultimateFurthestDistanceSqr)
        {
            ultimateFurthestDistanceSqr = distanceSqr;
            ultimateFurthestLogicalPosition = logicalPosition;
        }
    }

    private void FinishCurrentAttack()
    {
        AttackState finishedAttack = attackState;

        attackState = AttackState.None;
        attackPhase = AttackPhase.None;
        attackFrame = 0;
        comboBufferedAttack = AttackState.None;
        ultimateInputBuffered = false;
        hitTargetsThisAttack.Clear();

        if (finishedAttack == AttackState.JumpAttack)
        {
            if (ultimateFurthestDistanceSqr > EPSILON * EPSILON)
            {
                // 最终位置取本次大招离起点最远的有效逻辑位置。
                // 即使动画尾段把 root/Hips 拉回原点，也不会覆盖已经完成的冲刺。
                logicalPosition = ultimateFurthestLogicalPosition;
                ClampLogicalPositionToMap();
            }

            // 根运动采样已经逐帧叠加到 logicalPosition；结束时立即对齐渲染位置，
            // 避免大招落地后角色还要额外滑行十几帧才追上逻辑坐标。
            transform.SetPositionAndRotation(logicalPosition, logicalRotation);
            ultimateVisualSyncEndFrame = Time.frameCount + 2;

            Debug.Log(
                $"JumpAttack 结束：起点 {ultimateStartLogicalPosition}，最终逻辑位置 " +
                $"{logicalPosition}。",
                this);
        }
    }

    private CombatAttackDefinition GetDefinition(AttackState state)
    {
        if (attackDefinitions != null)
        {
            for (int i = 0; i < attackDefinitions.Length; i++)
            {
                CombatAttackDefinition definition = attackDefinitions[i];
                if (definition != null && definition.attackState == state)
                {
                    return definition;
                }
            }
        }

        // 数据数组被 Inspector 误改时不抛异常，用安全默认值继续运行。
        return new CombatAttackDefinition { attackState = state };
    }

    /// <summary>
    /// 所有攻击都使用纯数学 AABB，JumpAttack 也不例外。
    /// 大招只有在落地判定帧时刚好打得到目标才算命中。
    /// </summary>
    private void DetectAttackHits(CombatAttackDefinition definition)
    {
        Bounds attackBounds = BuildAttackBounds(definition);
        bool connectedOrdinaryAttack = false;
        float appliedKnockbackSpeed = GetComboAwareKnockbackSpeed(definition);
        CollectTargets(targetBuffer);

        for (int i = 0; i < targetBuffer.Count; i++)
        {
            PlayerController target = targetBuffer[i];
            if (target == null || target.isDead)
            {
                continue;
            }

            int targetId = target.GetInstanceID();
            if (hitTargetsThisAttack.Contains(targetId))
            {
                continue;
            }

            Bounds hurtBounds = target.BuildHurtBounds();
            if (!attackBounds.Intersects(hurtBounds))
            {
                continue;
            }

            hitTargetsThisAttack.Add(targetId);

            Vector3 knockbackDirection = target.logicalPosition - logicalPosition;
            knockbackDirection.y = 0f;

            target.ReceiveHit(
                logicalPosition,
                definition.damage,
                knockbackDirection.normalized * appliedKnockbackSpeed);

            if (definition.attackState != AttackState.JumpAttack &&
                !target.isDead &&
                target.isHurt)
            {
                connectedOrdinaryAttack = true;
            }
        }

        if (connectedOrdinaryAttack)
        {
            RegisterComboHit();
        }
    }

    /// <summary>
    /// 单次攻击无论命中几个玩家，都只算一次“连击”。
    /// 左拳和右拳使用完全相同的计数规则。
    /// </summary>
    private void RegisterComboHit()
    {
        comboHitCount = Mathf.Min(requiredComboHits, comboHitCount + 1);
        comboCounterWindowRemaining = comboCounterWindowFrames;
    }

    /// <summary>
    /// 第三下普攻和大招命中使用 Animator 采样出的大招总位移作为击退基准。
    /// 现有击退采用“初速度 + 恒定摩擦”，所以用 v² = 2as 反推初速度。
    /// 实际距离仍可能被硬直时长或地图边界截短，但不会出现第三击只推一点点的感觉。
    /// </summary>
    private float GetComboAwareKnockbackSpeed(CombatAttackDefinition definition)
    {
        bool isThirdOrdinaryHit =
            definition.attackState != AttackState.JumpAttack &&
            comboHitCount == requiredComboHits - 1;

        bool isUltimateHit = definition.attackState == AttackState.JumpAttack;
        if ((!isThirdOrdinaryHit && !isUltimateHit) ||
            ultimateRootMotionDistance <= EPSILON ||
            knockbackFriction <= EPSILON)
        {
            return definition.knockbackSpeed;
        }

        float desiredDistance = ultimateRootMotionDistance * ultimateKnockbackDistanceScale;
        return Mathf.Sqrt(2f * knockbackFriction * desiredDistance);
    }

    private void TickComboCounterWindow()
    {
        if (comboCounterWindowRemaining <= 0)
        {
            return;
        }

        comboCounterWindowRemaining--;
        if (comboCounterWindowRemaining > 0)
        {
            return;
        }

        comboHitCount = 0;
    }

    private Bounds BuildAttackBounds(CombatAttackDefinition definition)
    {
        Vector3 worldCenter =
            logicalPosition + (logicalRotation * definition.attackBoxLocalOffset);

        return new Bounds(worldCenter, definition.attackBoxSize);
    }

    private Bounds BuildHurtBounds()
    {
        Vector3 worldCenter = logicalPosition + (logicalRotation * hurtBoxLocalOffset);
        return new Bounds(worldCenter, hurtBoxSize);
    }

    private void CollectTargets(List<PlayerController> results)
    {
        results.Clear();

        // 手动拖入的目标优先。这样以后接真敌人时无需改检测代码。
        if (combatTarget != null && !combatTarget.isDead)
        {
            results.Add(combatTarget);
            return;
        }

        for (int i = 0; i < ActivePlayers.Count; i++)
        {
            PlayerController candidate = ActivePlayers[i];
            if (candidate != null && candidate != this && !candidate.isDead)
            {
                results.Add(candidate);
            }
        }

        // 场景中只有自己时，允许“打自己”，便于不复制角色也能测试完整流程。
        if (results.Count == 0 && allowSelfHitWhenNoTarget)
        {
            results.Add(this);
        }
    }

    /// <summary>
    /// 受击入口。attackerPos 是攻击者逻辑位置，knockbackDir 已经是反向速度向量。
    /// 这个方法只改变逻辑状态；真正的位置积分在下一次 FixedUpdate 完成。
    /// </summary>
    public void ReceiveHit(Vector3 attackerPos, float damage, Vector3 knockbackDir)
    {
        if (isDead)
        {
            return;
        }

        currentHP = Mathf.Max(0f, currentHP - Mathf.Max(0f, damage));

        if (currentHP <= 0f)
        {
            Die();
            return;
        }

        // 受击会打断当前攻击，连招缓冲和本段已命中记录也一起清掉。
        CancelCurrentAttack();

        isHurt = true;
        hurtTimer = Mathf.Max(hurtTimer, hitStunDuration);

        Vector3 horizontalDirection = knockbackDir;
        horizontalDirection.y = 0f;

        if (horizontalDirection.sqrMagnitude <= EPSILON)
        {
            // 攻击者和受击者位置几乎重叠时，朝当前角色的后方击退。
            horizontalDirection = logicalRotation * Vector3.back;
        }

        // 这里保存的是“速度（米/秒）”，不是立即加到坐标上的位移。
        knockbackVelocity = horizontalDirection.normalized * knockbackDir.magnitude;

        SetAnimatorTrigger("HitReaction");
    }

    /// <summary>
    /// 在 FixedUpdate 中推进硬直和击退。
    /// 先把速度乘进位移，再根据固定 deltaTime 降低速度，因此 30fps/144fps 的渲染帧率
    /// 都不会改变击退距离。真正改变 logicalPosition 的是下面那一行乘法。
    /// </summary>
    private void UpdateHurtTimerAndApplyKnockback()
    {
        if (!isHurt)
        {
            return;
        }

        float deltaTime = Time.fixedDeltaTime;

        // v * dt：速度按固定时间积分为位移，再叠加到逻辑坐标。
        logicalPosition += knockbackVelocity * deltaTime;

        // 摩擦力使用“每秒减少多少速度”，同样乘固定 deltaTime。
        // MoveTowards 会精确停在 0，避免无限接近零的小速度造成坐标持续漂移。
        knockbackVelocity = Vector3.MoveTowards(
            knockbackVelocity,
            Vector3.zero,
            knockbackFriction * deltaTime);

        hurtTimer -= deltaTime;
        if (hurtTimer <= 0f)
        {
            hurtTimer = 0f;
            isHurt = false;
            knockbackVelocity = Vector3.zero;
        }

        ClampLogicalPositionToMap();
    }

    private void Die()
    {
        currentHP = 0f;
        isDead = true;
        isHurt = false;
        hurtTimer = 0f;
        moveInput = Vector2.zero;
        knockbackVelocity = Vector3.zero;
        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;
        CancelCurrentAttack();

        SetAnimatorTrigger("Death");
    }

    private void CancelCurrentAttack()
    {
        attackState = AttackState.None;
        attackPhase = AttackPhase.None;
        attackFrame = 0;
        comboBufferedAttack = AttackState.None;
        ultimateInputBuffered = false;
        comboHitCount = 0;
        comboCounterWindowRemaining = 0;
        hitTargetsThisAttack.Clear();
    }

    private void ApplyCameraRelativeMovement(float deltaTime)
    {
        Vector2 input = Vector2.ClampMagnitude(moveInput, 1f);
        if (input.sqrMagnitude < moveInputDeadZone * moveInputDeadZone)
        {
            return;
        }

        Vector3 cameraForward = cameraTransform != null
            ? cameraTransform.forward
            : Vector3.forward;

        Vector3 cameraRight = cameraTransform != null
            ? cameraTransform.right
            : Vector3.right;

        // 动作游戏通常只在水平面移动；如果摄像机上下俯仰，把方向投影到 XZ 平面。
        cameraForward.y = 0f;
        cameraRight.y = 0f;
        cameraForward.Normalize();
        cameraRight.Normalize();

        Vector3 moveDirection =
            (cameraForward * input.y) + (cameraRight * input.x);

        if (moveDirection.sqrMagnitude <= EPSILON)
        {
            return;
        }

        moveDirection.Normalize();

        // 用固定 deltaTime 积分，保证不同 FixedUpdate 频率下速度含义一致。
        logicalPosition += moveDirection * (moveSpeed * deltaTime);

        // 指数平滑写法与帧率无关。角色只改变 Y 轴朝向，不因摄像机俯视而倾倒。
        Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
        float rotationInterpolation = 1f - Mathf.Exp(-rotationSharpness * deltaTime);
        logicalRotation = Quaternion.Slerp(logicalRotation, targetRotation, rotationInterpolation);
    }

    /// <summary>
    /// 所有正常移动和击退最后都会调用这里。
    /// Mathf.Clamp 直接限制 logicalPosition 的 X/Z，墙壁外再多速度也不会越界。
    /// </summary>
    private void ClampLogicalPositionToMap()
    {
        float minX = Mathf.Min(mapMin.x, mapMax.x);
        float maxX = Mathf.Max(mapMin.x, mapMax.x);
        float minZ = Mathf.Min(mapMin.y, mapMax.y);
        float maxZ = Mathf.Max(mapMin.y, mapMax.y);

        logicalPosition.x = Mathf.Clamp(logicalPosition.x, minX, maxX);
        logicalPosition.z = Mathf.Clamp(logicalPosition.z, minZ, maxZ);
    }

    private void SmoothVisualTransform()
    {
        // 渲染 Transform 在 Update 里平滑追上逻辑位置；它不会反过来影响 logicalPosition。
        float interpolation = 1f - Mathf.Exp(-visualFollowSharpness * Time.deltaTime);
        transform.position = Vector3.Lerp(
            transform.position,
            logicalPosition,
            interpolation);

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            logicalRotation,
            interpolation);
    }

    /// <summary>
    /// 原有 Animator Controller 使用 moveAmount 驱动移动混合树。
    /// 战斗锁定时 moveInput 已归零，所以攻击、受击和死亡会自动把移动动画平滑收回去。
    /// </summary>
    private void UpdateLocomotionAnimation()
    {
        if (animator == null)
        {
            return;
        }

        float moveAmount = Vector2.ClampMagnitude(moveInput, 1f).magnitude;
        animator.SetFloat(
            "moveAmount",
            moveAmount,
            animationDampTime,
            Time.deltaTime);
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

    private bool WasPrimaryAttackPressedThisUpdate()
    {
        if (attackInputReference != null && attackInputReference.action != null)
        {
            return attackInputReference.action.WasPressedThisFrame();
        }

        return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    }

    private bool WasSecondaryAttackPressedThisUpdate()
    {
        if (secondaryAttackInputReference != null &&
            secondaryAttackInputReference.action != null)
        {
            return secondaryAttackInputReference.action.WasPressedThisFrame();
        }

        return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
    }

    private bool WasUltimatePressedThisUpdate()
    {
        if (ultimateInputReference != null && ultimateInputReference.action != null)
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

    /// <summary>
    /// 仅用于单机调试。可以右键组件调用，也可以从按钮事件调用。
    /// 正式网络版应由服务端权威状态决定复活，不应由客户端自己重置。
    /// </summary>
    [ContextMenu("调试：恢复满血并重置战斗状态")]
    public void ResetCombatStateForTesting()
    {
        currentHP = maxHP;
        isDead = false;
        isHurt = false;
        hurtTimer = 0f;
        knockbackVelocity = Vector3.zero;
        moveInput = Vector2.zero;
        primaryAttackInputLatched = false;
        secondaryAttackInputLatched = false;
        ultimateInputLatched = false;
        CancelCurrentAttack();

        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }
    }

    private static CombatAttackDefinition[] CreateDefaultAttackDefinitions()
    {
        return new[]
        {
            new CombatAttackDefinition
            {
                attackState = AttackState.LeftPunch,
                activeStartFrame = 5,
                activeEndFrame = 10,
                recoveryFrames = 7,
                forwardMoveDistance = 0.35f,
                forwardMoveStartFrame = 1,
                forwardMoveEndFrame = 4,
                damage = 8f,
                knockbackSpeed = 4.5f
            },
            new CombatAttackDefinition
            {
                attackState = AttackState.RightPunch,
                activeStartFrame = 5,
                activeEndFrame = 10,
                recoveryFrames = 8,
                forwardMoveDistance = 0.45f,
                forwardMoveStartFrame = 1,
                forwardMoveEndFrame = 4,
                damage = 10f,
                knockbackSpeed = 5.5f
            },
            new CombatAttackDefinition
            {
                attackState = AttackState.JumpAttack,
                activeStartFrame = 5,
                activeEndFrame = 10,
                recoveryFrames = 10,
                forwardMoveDistance = 0.8f,
                forwardMoveStartFrame = 1,
                forwardMoveEndFrame = 5,
                damage = 18f,
                knockbackSpeed = 8f,
                attackBoxSize = new Vector3(1.7f, 1.2f, 1.8f),
                attackBoxLocalOffset = new Vector3(0f, 1.05f, 0.95f)
            }
        };
    }

    private void OnValidate()
    {
        maxHP = Mathf.Max(1f, maxHP);
        hitStunDuration = Mathf.Max(0f, hitStunDuration);
        knockbackFriction = Mathf.Max(0f, knockbackFriction);
        moveSpeed = Mathf.Max(0f, moveSpeed);
        requiredComboHits = Mathf.Max(1, requiredComboHits);
        comboCounterWindowFrames = Mathf.Max(1, comboCounterWindowFrames);
        attackAnimationLayer = Mathf.Max(0, attackAnimationLayer);
        ultimateKnockbackDistanceScale = Mathf.Max(0f, ultimateKnockbackDistanceScale);
        ultimateHitNormalizedTime = Mathf.Clamp01(ultimateHitNormalizedTime);
        ultimateLandingHeightTolerance = Mathf.Max(0f, ultimateLandingHeightTolerance);
        ultimateHitWindowFrames = Mathf.Max(1, ultimateHitWindowFrames);

        if (attackDefinitions != null)
        {
            for (int i = 0; i < attackDefinitions.Length; i++)
            {
                CombatAttackDefinition definition = attackDefinitions[i];
                if (definition == null)
                {
                    continue;
                }

                definition.activeStartFrame = Mathf.Max(1, definition.activeStartFrame);
                definition.activeEndFrame = Mathf.Max(
                    definition.activeStartFrame,
                    definition.activeEndFrame);
                definition.recoveryFrames = Mathf.Max(0, definition.recoveryFrames);
                definition.forwardMoveDistance = Mathf.Max(0f, definition.forwardMoveDistance);
                definition.forwardMoveStartFrame = Mathf.Max(1, definition.forwardMoveStartFrame);
                definition.forwardMoveEndFrame = Mathf.Max(
                    definition.forwardMoveStartFrame,
                    definition.forwardMoveEndFrame);
                definition.damage = Mathf.Max(0f, definition.damage);
                definition.knockbackSpeed = Mathf.Max(0f, definition.knockbackSpeed);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 gizmoPosition = Application.isPlaying ? logicalPosition : transform.position;
        Quaternion gizmoRotation = Application.isPlaying ? logicalRotation : transform.rotation;

        // 黄色：自身受击框。
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(
            gizmoPosition + gizmoRotation * hurtBoxLocalOffset,
            hurtBoxSize);

        // 绿色：当前攻击框。未攻击时预览 LeftPunch 的配置。
        AttackState previewState = attackState == AttackState.None
            ? AttackState.LeftPunch
            : attackState;
        CombatAttackDefinition previewDefinition = GetDefinition(previewState);

        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(
            gizmoPosition + gizmoRotation * previewDefinition.attackBoxLocalOffset,
            previewDefinition.attackBoxSize);

        // 蓝色：当前地图允许的 X/Z 活动范围。
        Gizmos.color = Color.cyan;
        Vector3 mapCenter = new Vector3(
            (mapMin.x + mapMax.x) * 0.5f,
            gizmoPosition.y,
            (mapMin.y + mapMax.y) * 0.5f);
        Vector3 mapSize = new Vector3(
            Mathf.Abs(mapMax.x - mapMin.x),
            0.02f,
            Mathf.Abs(mapMax.y - mapMin.y));
        Gizmos.DrawWireCube(mapCenter, mapSize);
    }
}

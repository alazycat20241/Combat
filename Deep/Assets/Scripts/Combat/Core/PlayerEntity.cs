using System;
using System.Collections.Generic;

namespace Combat.Core
{
    /// <summary>
    /// 纯 C# 战斗实体。
    ///
    /// 这个类不引用 UnityEngine，不处理 Animator、输入设备、Transform 平滑或渲染。
    /// 客户端 PlayerController 与服务端都调用同一套 Tick/FixedUpdateLogic。
    /// </summary>
    public sealed class PlayerEntity
    {
        private const float Epsilon = 0.0001f;

        private readonly PlayerCombatConfig config;
        private readonly Dictionary<AttackState, AttackMotionProfile> attackMotionProfiles =
            new Dictionary<AttackState, AttackMotionProfile>();

        private readonly HashSet<int> hitTargetsThisAttack = new HashSet<int>();
        private readonly List<PlayerEntity> targetBuffer = new List<PlayerEntity>(4);

        private AttackState bufferedAttack = AttackState.None;
        private bool ultimateInputBuffered;
        private int comboHitCount;
        private int comboCounterWindowRemaining;

        private float hurtTimer;
        private LogicVector3 knockbackVelocity;

        private LogicVector3 ultimateStartPosition;
        private LogicVector3 ultimateFurthestPosition;
        private float ultimateFurthestDistanceSqr;

        public event Action<CombatEvent> CombatEvent;

        public PlayerEntity(
            int entityId,
            PlayerCombatConfig config,
            LogicVector3 spawnPosition,
            LogicRotation spawnRotation)
        {
            EntityId = entityId;
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            LogicalPosition = spawnPosition;
            LogicalRotation = spawnRotation;
            CurrentHP = config.MaxHP;
        }

        public int EntityId { get; }
        public int PreferredTargetId { get; set; }
        public PlayerCombatConfig Config => config;

        public LogicVector3 LogicalPosition { get; private set; }
        public LogicRotation LogicalRotation { get; private set; }
        public float CurrentHP { get; private set; }
        public AttackState AttackState { get; private set; } = AttackState.None;
        public AttackPhase AttackPhase { get; private set; } = AttackPhase.None;
        public int AttackFrame { get; private set; }
        public LogicVector3 KnockbackVelocity => knockbackVelocity;
        public bool IsDead { get; private set; }
        public bool IsHurt { get; private set; }

        public int ComboHitCount => comboHitCount;
        public bool UltimateReady => comboHitCount >= config.RequiredComboHits;
        public bool IsInCombo => AttackState != AttackState.None || comboHitCount > 0;
        public bool CanMove => !IsDead && !IsHurt && AttackState == AttackState.None;
        public bool CanAttack => !IsDead && !IsHurt;

        public void SetPreferredTarget(int targetEntityId)
        {
            PreferredTargetId = targetEntityId;
        }

        /// <summary>
        /// 注入从 Animator 预采样得到的逐帧局部位移。
        /// 正式锁步版本应把同样数据从 Luban 配置加载，而不是每个客户端各自采样 Animator。
        /// </summary>
        public void SetAttackMotionProfile(AttackState state, AttackMotionProfile profile)
        {
            if (profile == null)
            {
                attackMotionProfiles.Remove(state);
                return;
            }

            attackMotionProfiles[state] = profile;
        }

        public AttackMotionProfile GetAttackMotionProfile(AttackState state)
        {
            attackMotionProfiles.TryGetValue(state, out AttackMotionProfile profile);
            return profile;
        }

        public void ResetForTesting(
            LogicVector3 position,
            LogicRotation rotation)
        {
            LogicalPosition = position;
            LogicalRotation = rotation;
            CurrentHP = config.MaxHP;
            AttackState = AttackState.None;
            AttackPhase = AttackPhase.None;
            AttackFrame = 0;
            knockbackVelocity = LogicVector3.Zero;
            IsDead = false;
            IsHurt = false;
            hurtTimer = 0f;
            comboHitCount = 0;
            comboCounterWindowRemaining = 0;
            bufferedAttack = AttackState.None;
            ultimateInputBuffered = false;
            hitTargetsThisAttack.Clear();
        }

        /// <summary>
        /// 推进一个逻辑帧。所有输入都是逻辑语义，不是键盘或鼠标原始设备状态。
        /// </summary>
        public void Tick(
            CombatInputFrame input,
            float deltaTime,
            IReadOnlyList<PlayerEntity> combatants)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            TickComboCounterWindow();

            if (IsDead)
            {
                return;
            }

            UpdateHurtAndKnockback(deltaTime);
            HandleInput(input, combatants);
            TickAttack(combatants);

            if (IsHurt || AttackState != AttackState.None)
            {
                return;
            }

            ApplyMovement(input, deltaTime);
            ClampToMap();
        }

        public void FixedUpdateLogic(
            CombatInputFrame input,
            float deltaTime,
            IReadOnlyList<PlayerEntity> combatants)
        {
            Tick(input, deltaTime, combatants);
        }

        private void HandleInput(
            CombatInputFrame input,
            IReadOnlyList<PlayerEntity> combatants)
        {
            if (!CanAttack)
            {
                return;
            }

            if (input.UltimatePressed && UltimateReady)
            {
                if (AttackState == AttackState.None)
                {
                    StartAttack(AttackState.JumpAttack, combatants);
                    return;
                }

                if (AttackPhase == AttackPhase.Windup ||
                    AttackPhase == AttackPhase.Active)
                {
                    ultimateInputBuffered = true;
                    return;
                }

                if (AttackPhase == AttackPhase.Recovery)
                {
                    StartAttack(AttackState.JumpAttack, combatants);
                    return;
                }
            }

            if (!input.PrimaryAttackPressed && !input.SecondaryAttackPressed)
            {
                return;
            }

            AttackState requestedAttack = input.PrimaryAttackPressed
                ? AttackState.LeftPunch
                : AttackState.RightPunch;

            if (AttackState == AttackState.None)
            {
                StartAttack(requestedAttack, combatants);
                return;
            }

            if (AttackPhase == AttackPhase.Windup ||
                AttackPhase == AttackPhase.Active)
            {
                bufferedAttack = requestedAttack;
            }
            else if (AttackPhase == AttackPhase.Recovery)
            {
                StartAttack(requestedAttack, combatants);
            }
        }

        private void TickAttack(IReadOnlyList<PlayerEntity> combatants)
        {
            if (AttackState == AttackState.None)
            {
                return;
            }

            CombatAttackConfig attack = GetRequiredAttack(AttackState);
            int activeStart = Math.Max(1, attack.ActiveStartFrame);
            int activeEnd = Math.Max(activeStart, attack.ActiveEndFrame);
            int recoveryEnd = activeEnd + Math.Max(0, attack.RecoveryFrames);

            if (AttackState == AttackState.JumpAttack)
            {
                AttackMotionProfile profile = GetAttackMotionProfile(AttackState.JumpAttack);
                int animationFrameCount = profile != null && profile.FrameCount > 0
                    ? profile.FrameCount
                    : Math.Max(recoveryEnd, activeEnd);

                if (profile != null && profile.LandingFrame > 0)
                {
                    activeStart = Clamp(profile.LandingFrame, 1, animationFrameCount);
                }
                else if (profile != null && profile.LastHorizontalFrame > 0)
                {
                    activeStart = Clamp(profile.LastHorizontalFrame + 1, 1, animationFrameCount);
                }
                else if (profile != null && profile.FrameCount > 0)
                {
                    activeStart = Clamp(
                        (int)Math.Ceiling(
                            animationFrameCount *
                            Math.Max(0f, Math.Min(1f, config.UltimateHitNormalizedFallback))),
                        1,
                        animationFrameCount);
                }

                activeEnd = Math.Min(
                    animationFrameCount,
                    activeStart + Math.Max(1, config.UltimateHitWindowFrames) - 1);
                recoveryEnd = Math.Max(recoveryEnd, animationFrameCount);
            }

            AttackFrame++;
            ApplyAttackMovement(AttackState, attack);

            if (AttackFrame < activeStart)
            {
                AttackPhase = AttackPhase.Windup;
                return;
            }

            if (AttackFrame <= activeEnd)
            {
                AttackPhase = AttackPhase.Active;
                DetectAttackHits(AttackState, attack, combatants);
                return;
            }

            if (AttackFrame <= recoveryEnd)
            {
                bool justEnteredRecovery = AttackPhase != AttackPhase.Recovery;
                AttackPhase = AttackPhase.Recovery;

                if (justEnteredRecovery)
                {
                    if (ultimateInputBuffered && UltimateReady)
                    {
                        StartAttack(AttackState.JumpAttack, combatants);
                        return;
                    }

                    if (bufferedAttack != AttackState.None)
                    {
                        AttackState nextAttack = bufferedAttack;
                        bufferedAttack = AttackState.None;
                        StartAttack(nextAttack, combatants);
                        return;
                    }
                }

                return;
            }

            FinishAttack();
        }

        private void StartAttack(
            AttackState nextState,
            IReadOnlyList<PlayerEntity> combatants)
        {
            if (nextState == AttackState.None || IsDead || IsHurt)
            {
                return;
            }

            if (nextState == AttackState.JumpAttack)
            {
                comboHitCount = 0;
                comboCounterWindowRemaining = 0;
                FaceNearestTarget(combatants);
                ultimateStartPosition = LogicalPosition;
                ultimateFurthestPosition = LogicalPosition;
                ultimateFurthestDistanceSqr = 0f;
            }

            AttackState = nextState;
            AttackPhase = AttackPhase.Windup;
            AttackFrame = 0;
            bufferedAttack = AttackState.None;
            ultimateInputBuffered = false;
            hitTargetsThisAttack.Clear();
            Raise(CombatEventType.AttackStarted, nextState);
        }

        private void FinishAttack()
        {
            bool finishedUltimate = AttackState == AttackState.JumpAttack;

            AttackState = AttackState.None;
            AttackPhase = AttackPhase.None;
            AttackFrame = 0;
            bufferedAttack = AttackState.None;
            ultimateInputBuffered = false;
            hitTargetsThisAttack.Clear();

            if (finishedUltimate && ultimateFurthestDistanceSqr > Epsilon * Epsilon)
            {
                // 动画根骨骼尾帧即使回到起点，也不允许覆盖已经完成的冲刺。
                LogicalPosition = ultimateFurthestPosition;
                ClampToMap();
            }
        }

        private void ApplyAttackMovement(
            AttackState state,
            CombatAttackConfig attack)
        {
            AttackMotionProfile profile = GetAttackMotionProfile(state);
            if (state == AttackState.JumpAttack &&
                profile?.FrameDeltas != null &&
                profile.HorizontalDistance > Epsilon)
            {
                int frameIndex = AttackFrame - 1;
                if (frameIndex >= 0 && frameIndex < profile.FrameDeltas.Length)
                {
                    LogicVector3 worldDelta =
                        LogicalRotation.Rotate(profile.FrameDeltas[frameIndex]);
                    LogicalPosition += worldDelta;
                    ClampToMap();
                    TrackUltimateFurthestPosition(state);
                }

                return;
            }

            float distance = Math.Max(0f, attack.ForwardMoveDistance);
            if (distance <= Epsilon)
            {
                return;
            }

            int moveStart = Math.Max(1, attack.ForwardMoveStartFrame);
            int moveEnd = Math.Max(moveStart, attack.ForwardMoveEndFrame);
            if (AttackFrame < moveStart || AttackFrame > moveEnd)
            {
                return;
            }

            int frameCount = moveEnd - moveStart + 1;
            LogicalPosition += LogicalRotation.Forward * (distance / frameCount);
            ClampToMap();
            TrackUltimateFurthestPosition(state);
        }

        private void TrackUltimateFurthestPosition(AttackState state)
        {
            if (state != AttackState.JumpAttack)
            {
                return;
            }

            LogicVector3 displacement = (LogicalPosition - ultimateStartPosition).FlattenY();
            float distanceSqr = displacement.SqrMagnitude;
            if (distanceSqr > ultimateFurthestDistanceSqr)
            {
                ultimateFurthestDistanceSqr = distanceSqr;
                ultimateFurthestPosition = LogicalPosition;
            }
        }

        private void DetectAttackHits(
            AttackState state,
            CombatAttackConfig attack,
            IReadOnlyList<PlayerEntity> combatants)
        {
            CollectTargets(combatants, targetBuffer);
            LogicAabb attackBounds = BuildAttackBounds(attack);
            bool connectedOrdinaryAttack = false;
            float knockbackSpeed = GetComboAwareKnockbackSpeed(state, attack);

            for (int i = 0; i < targetBuffer.Count; i++)
            {
                PlayerEntity target = targetBuffer[i];
                if (target == null ||
                    target.IsDead ||
                    hitTargetsThisAttack.Contains(target.EntityId))
                {
                    continue;
                }

                if (!attackBounds.Intersects(target.BuildHurtBounds()))
                {
                    continue;
                }

                hitTargetsThisAttack.Add(target.EntityId);

                LogicVector3 knockbackDirection =
                    (target.LogicalPosition - LogicalPosition).FlattenY().Normalized();

                target.ReceiveHit(
                    this,
                    attack.Damage,
                    knockbackDirection * knockbackSpeed);

                if (state != AttackState.JumpAttack &&
                    !target.IsDead &&
                    target.IsHurt)
                {
                    connectedOrdinaryAttack = true;
                }
            }

            if (connectedOrdinaryAttack)
            {
                comboHitCount = Math.Min(
                    Math.Max(1, config.RequiredComboHits),
                    comboHitCount + 1);
                comboCounterWindowRemaining = Math.Max(
                    1,
                    config.ComboCounterWindowFrames);
            }
        }

        private LogicAabb BuildAttackBounds(CombatAttackConfig attack)
        {
            LogicVector3 center =
                LogicalPosition + LogicalRotation.Rotate(attack.AttackBoxLocalOffset);
            return new LogicAabb(center, attack.AttackBoxSize);
        }

        private LogicAabb BuildHurtBounds()
        {
            LogicVector3 center =
                LogicalPosition + LogicalRotation.Rotate(config.HurtBoxLocalOffset);
            return new LogicAabb(center, config.HurtBoxSize);
        }

        private void CollectTargets(
            IReadOnlyList<PlayerEntity> combatants,
            List<PlayerEntity> results)
        {
            results.Clear();

            if (PreferredTargetId != 0 && combatants != null)
            {
                for (int i = 0; i < combatants.Count; i++)
                {
                    PlayerEntity candidate = combatants[i];
                    if (candidate != null &&
                        candidate.EntityId == PreferredTargetId &&
                        !candidate.IsDead)
                    {
                        results.Add(candidate);
                        return;
                    }
                }
            }

            if (combatants != null)
            {
                for (int i = 0; i < combatants.Count; i++)
                {
                    PlayerEntity candidate = combatants[i];
                    if (candidate != null && candidate != this && !candidate.IsDead)
                    {
                        results.Add(candidate);
                    }
                }
            }

            if (results.Count == 0 && config.AllowSelfHitWhenNoTarget)
            {
                results.Add(this);
            }
        }

        /// <summary>
        /// 受击只修改纯逻辑状态。表现层通过 CombatEvent 收到受击事件后播放动画。
        /// </summary>
        public void ReceiveHit(
            PlayerEntity attacker,
            float damage,
            LogicVector3 incomingKnockbackVelocity)
        {
            if (IsDead)
            {
                return;
            }

            CurrentHP = Math.Max(0f, CurrentHP - Math.Max(0f, damage));
            if (CurrentHP <= 0f)
            {
                Die();
                return;
            }

            CancelAttack();
            IsHurt = true;
            hurtTimer = Math.Max(hurtTimer, Math.Max(0f, config.HitStunDuration));

            LogicVector3 horizontalVelocity = incomingKnockbackVelocity.FlattenY();
            if (horizontalVelocity.SqrMagnitude <= Epsilon * Epsilon)
            {
                horizontalVelocity = -LogicalRotation.Forward;
            }

            knockbackVelocity = horizontalVelocity;
            Raise(CombatEventType.HitReaction);
        }

        private void Die()
        {
            CurrentHP = 0f;
            IsDead = true;
            IsHurt = false;
            hurtTimer = 0f;
            knockbackVelocity = LogicVector3.Zero;
            CancelAttack();
            Raise(CombatEventType.Died);
        }

        private void CancelAttack()
        {
            AttackState = AttackState.None;
            AttackPhase = AttackPhase.None;
            AttackFrame = 0;
            bufferedAttack = AttackState.None;
            ultimateInputBuffered = false;
            comboHitCount = 0;
            comboCounterWindowRemaining = 0;
            hitTargetsThisAttack.Clear();
        }

        private void UpdateHurtAndKnockback(float deltaTime)
        {
            if (!IsHurt)
            {
                return;
            }

            LogicalPosition += knockbackVelocity * deltaTime;
            knockbackVelocity = MoveTowardsZero(
                knockbackVelocity,
                Math.Max(0f, config.KnockbackFriction) * deltaTime);

            hurtTimer -= deltaTime;
            if (hurtTimer <= 0f)
            {
                hurtTimer = 0f;
                IsHurt = false;
                knockbackVelocity = LogicVector3.Zero;
            }

            ClampToMap();
        }

        private void ApplyMovement(CombatInputFrame input, float deltaTime)
        {
            if (!input.HasMoveInput)
            {
                return;
            }

            LogicVector3 flattenedInput = input.MoveDirection.FlattenY();
            if (flattenedInput.SqrMagnitude <=
                config.MoveInputDeadZone * config.MoveInputDeadZone)
            {
                return;
            }

            LogicVector3 moveDirection = flattenedInput.Normalized();
            float speed = Math.Max(0f, config.MoveSpeed);
            LogicalPosition += moveDirection * (speed * deltaTime);
            LogicalRotation = LogicRotation.SmoothTowards(
                LogicalRotation,
                LogicRotation.LookTowards(moveDirection),
                config.RotationSharpness,
                deltaTime);
        }

        private void FaceNearestTarget(IReadOnlyList<PlayerEntity> combatants)
        {
            CollectTargets(combatants, targetBuffer);

            PlayerEntity nearest = null;
            float nearestDistanceSqr = float.PositiveInfinity;
            for (int i = 0; i < targetBuffer.Count; i++)
            {
                PlayerEntity candidate = targetBuffer[i];
                if (candidate == null || candidate == this || candidate.IsDead)
                {
                    continue;
                }

                float distanceSqr = (candidate.LogicalPosition - LogicalPosition)
                    .FlattenY()
                    .SqrMagnitude;
                if (distanceSqr > Epsilon * Epsilon && distanceSqr < nearestDistanceSqr)
                {
                    nearest = candidate;
                    nearestDistanceSqr = distanceSqr;
                }
            }

            if (nearest == null)
            {
                return;
            }

            LogicVector3 direction =
                (nearest.LogicalPosition - LogicalPosition).FlattenY();
            LogicalRotation = LogicRotation.LookTowards(direction);
        }

        private float GetComboAwareKnockbackSpeed(
            AttackState state,
            CombatAttackConfig attack)
        {
            bool isThirdOrdinaryHit =
                state != AttackState.JumpAttack &&
                comboHitCount == Math.Max(1, config.RequiredComboHits) - 1;

            bool isUltimateHit = state == AttackState.JumpAttack;
            if ((!isThirdOrdinaryHit && !isUltimateHit) ||
                config.KnockbackFriction <= Epsilon)
            {
                return Math.Max(0f, attack.KnockbackSpeed);
            }

            float movementDistance = GetUltimateMovementDistance();
            if (movementDistance <= Epsilon)
            {
                return Math.Max(0f, attack.KnockbackSpeed);
            }

            float desiredDistance =
                movementDistance * Math.Max(0f, config.UltimateKnockbackDistanceScale);
            return (float)Math.Sqrt(
                2f * config.KnockbackFriction * desiredDistance);
        }

        private float GetUltimateMovementDistance()
        {
            AttackMotionProfile profile =
                GetAttackMotionProfile(AttackState.JumpAttack);
            if (profile != null && profile.HorizontalDistance > Epsilon)
            {
                return profile.HorizontalDistance;
            }

            CombatAttackConfig jumpAttack =
                config.FindAttack(AttackState.JumpAttack);
            return jumpAttack != null
                ? Math.Max(0f, jumpAttack.ForwardMoveDistance)
                : 0f;
        }

        private void TickComboCounterWindow()
        {
            if (comboCounterWindowRemaining <= 0)
            {
                return;
            }

            comboCounterWindowRemaining--;
            if (comboCounterWindowRemaining <= 0)
            {
                comboHitCount = 0;
            }
        }

        private void ClampToMap()
        {
            LogicalPosition = new LogicVector3(
                Clamp(LogicalPosition.X, config.MapMinX, config.MapMaxX),
                LogicalPosition.Y,
                Clamp(LogicalPosition.Z, config.MapMinZ, config.MapMaxZ));
        }

        private CombatAttackConfig GetRequiredAttack(AttackState state)
        {
            CombatAttackConfig attack = config.FindAttack(state);
            if (attack == null)
            {
                throw new InvalidOperationException(
                    $"PlayerCombatConfig {config.Id} missing attack {state}.");
            }

            return attack;
        }

        private void Raise(
            CombatEventType eventType,
            AttackState attackState = AttackState.None)
        {
            CombatEvent?.Invoke(new CombatEvent(eventType, attackState));
        }

        private static LogicVector3 MoveTowardsZero(
            LogicVector3 value,
            float maxDistanceDelta)
        {
            float magnitude = value.Magnitude;
            if (magnitude <= maxDistanceDelta || magnitude <= Epsilon)
            {
                return LogicVector3.Zero;
            }

            return value * ((magnitude - maxDistanceDelta) / magnitude);
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            float low = Math.Min(min, max);
            float high = Math.Max(min, max);
            return value < low ? low : value > high ? high : value;
        }
    }
}

using System;
using System.Collections.Generic;

namespace Combat.Core
{
    public enum AttackState
    {
        None,
        LeftPunch,
        RightPunch,
        JumpAttack
    }

    public enum AttackPhase
    {
        None,
        Windup,
        Active,
        Recovery
    }

    /// <summary>
    /// Luban 单行攻击配置。
    /// 这是纯 POCO，可以被 Unity、.NET 10 服务端和锁步客户端共同反序列化。
    /// </summary>
    [Serializable]
    public sealed class CombatAttackConfig
    {
        public string Id = string.Empty;
        public int OwnerId;
        public AttackState AttackState;
        public int ActiveStartFrame = 5;
        public int ActiveEndFrame = 10;
        public int RecoveryFrames = 7;
        public float Damage = 10f;
        public float KnockbackSpeed = 5f;

        public float ForwardMoveDistance = 0.35f;
        public int ForwardMoveStartFrame = 1;
        public int ForwardMoveEndFrame = 4;

        public LogicVector3 AttackBoxSize = new LogicVector3(1.25f, 0.9f, 1.4f);
        public LogicVector3 AttackBoxLocalOffset = new LogicVector3(0f, 0.9f, 0.8f);
    }

    /// <summary>
    /// Luban 角色战斗配置。
    /// 不要在里面加入 Animator、MonoBehaviour、Vector3 等 Unity 类型。
    /// </summary>
    [Serializable]
    public sealed class PlayerCombatConfig
    {
        public int Id;
        public string Name = string.Empty;

        public float MaxHP = 100f;
        public float MoveSpeed = 5f;
        public float MoveInputDeadZone = 0.1f;
        public float RotationSharpness = 10f;

        public float MapMinX = -10f;
        public float MapMaxX = 10f;
        public float MapMinZ = -10f;
        public float MapMaxZ = 10f;

        public float HitStunDuration = 0.4f;
        public float KnockbackFriction = 28f;
        public LogicVector3 HurtBoxSize = new LogicVector3(0.8f, 1.8f, 0.8f);
        public LogicVector3 HurtBoxLocalOffset = new LogicVector3(0f, 0.9f, 0f);

        public int RequiredComboHits = 3;
        public int ComboCounterWindowFrames = 30;
        public float UltimateKnockbackDistanceScale = 1f;
        public float UltimateHitNormalizedFallback = 0.9f;
        public int UltimateHitWindowFrames = 1;
        public bool AllowSelfHitWhenNoTarget = true;

        public CombatAttackConfig[] Attacks = Array.Empty<CombatAttackConfig>();

        public CombatAttackConfig FindAttack(AttackState state)
        {
            if (Attacks == null)
            {
                return null;
            }

            for (int i = 0; i < Attacks.Length; i++)
            {
                CombatAttackConfig attack = Attacks[i];
                if (attack != null && attack.AttackState == state)
                {
                    return attack;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 可作为 Luban JSON 根对象的配置集合。
    /// 服务端加载后按 OwnerId 把 Attacks 组装进 PlayerCombatConfig.Attacks。
    /// </summary>
    [Serializable]
    public sealed class CombatConfigBundle
    {
        public PlayerCombatConfig[] Players = Array.Empty<PlayerCombatConfig>();
        public CombatAttackConfig[] Attacks = Array.Empty<CombatAttackConfig>();

        /// <summary>
        /// 把扁平的 Luban 攻击表组装到对应角色的 Attacks 数组。
        /// </summary>
        public void LinkAttacksByOwner()
        {
            if (Players == null)
            {
                return;
            }

            for (int playerIndex = 0; playerIndex < Players.Length; playerIndex++)
            {
                PlayerCombatConfig player = Players[playerIndex];
                if (player == null)
                {
                    continue;
                }

                List<CombatAttackConfig> attacks =
                    new List<CombatAttackConfig>();
                if (Attacks != null)
                {
                    for (int attackIndex = 0; attackIndex < Attacks.Length; attackIndex++)
                    {
                        CombatAttackConfig attack = Attacks[attackIndex];
                        if (attack != null && attack.OwnerId == player.Id)
                        {
                            attacks.Add(attack);
                        }
                    }
                }

                player.Attacks = attacks.ToArray();
            }
        }
    }

    /// <summary>
    /// 从 Animator 预采样后注入逻辑层的动画运动数据。
    /// 服务端版本应当把同样内容烘焙进 Luban，客户端不再运行时采样。
    /// </summary>
    public sealed class AttackMotionProfile
    {
        public LogicVector3[] FrameDeltas;
        public int LandingFrame;
        public int LastHorizontalFrame;
        public float HorizontalDistance;

        public int FrameCount => FrameDeltas?.Length ?? 0;
    }

    public struct CombatInputFrame
    {
        public bool PrimaryAttackPressed;
        public bool SecondaryAttackPressed;
        public bool UltimatePressed;
        public bool HasMoveInput;
        public LogicVector3 MoveDirection;
    }
}

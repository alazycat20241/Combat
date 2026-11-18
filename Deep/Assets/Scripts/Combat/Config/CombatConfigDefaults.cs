using Combat.Core;

namespace Combat.Config
{
    /// <summary>
    /// 在 Luban 接入前使用的原型数据。字段结构与 Luban JSON 完全一致。
    /// </summary>
    public static class CombatConfigDefaults
    {
        public static PlayerCombatConfig CreatePlayer()
        {
            return new PlayerCombatConfig
            {
                Id = 1001,
                Name = "Player",
                MaxHP = 100f,
                MoveSpeed = 5f,
                MoveInputDeadZone = 0.1f,
                RotationSharpness = 10f,
                HitStunDuration = 0.4f,
                KnockbackFriction = 28f,
                RequiredComboHits = 3,
                ComboCounterWindowFrames = 30,
                UltimateKnockbackDistanceScale = 1f,
                UltimateHitNormalizedFallback = 0.9f,
                UltimateHitWindowFrames = 1,
                Attacks = new[]
                {
                    new CombatAttackConfig
                    {
                        Id = "left_punch",
                        OwnerId = 1001,
                        AttackState = AttackState.LeftPunch,
                        ActiveStartFrame = 5,
                        ActiveEndFrame = 10,
                        RecoveryFrames = 7,
                        Damage = 8f,
                        KnockbackSpeed = 4.5f,
                        ForwardMoveDistance = 0.35f,
                        ForwardMoveStartFrame = 1,
                        ForwardMoveEndFrame = 4
                    },
                    new CombatAttackConfig
                    {
                        Id = "right_punch",
                        OwnerId = 1001,
                        AttackState = AttackState.RightPunch,
                        ActiveStartFrame = 5,
                        ActiveEndFrame = 10,
                        RecoveryFrames = 8,
                        Damage = 10f,
                        KnockbackSpeed = 5.5f,
                        ForwardMoveDistance = 0.45f,
                        ForwardMoveStartFrame = 1,
                        ForwardMoveEndFrame = 4,
                        AttackBoxSize = new LogicVector3(1.35f, 0.9f, 1.45f),
                        AttackBoxLocalOffset = new LogicVector3(0f, 0.9f, 0.85f)
                    },
                    new CombatAttackConfig
                    {
                        Id = "jump_attack",
                        OwnerId = 1001,
                        AttackState = AttackState.JumpAttack,
                        ActiveStartFrame = 5,
                        ActiveEndFrame = 10,
                        RecoveryFrames = 10,
                        Damage = 18f,
                        KnockbackSpeed = 8f,
                        ForwardMoveDistance = 0.8f,
                        ForwardMoveStartFrame = 1,
                        ForwardMoveEndFrame = 5,
                        AttackBoxSize = new LogicVector3(1.7f, 1.2f, 1.8f),
                        AttackBoxLocalOffset = new LogicVector3(0f, 1.05f, 0.95f)
                    }
                }
            };
        }
    }
}

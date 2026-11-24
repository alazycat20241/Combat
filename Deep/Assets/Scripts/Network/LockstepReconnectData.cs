using System;

namespace Game.Networking
{
    /// <summary>
    /// 纯 C# 的重连玩家快照副本。
    ///
    /// Fantasy 的协议消息会回收到对象池，因此 NetworkManager 在消息回调内
    /// 把内容复制到这些普通对象。CombatSimulationRunner 随后再把它们转换成
    /// Combat.Core 的快照，不保存任何池化消息引用。
    /// </summary>
    public sealed class ReconnectPlayerSnapshotData
    {
        public int EntityId;
        public int PreferredTargetId;
        public long PositionXRaw;
        public long PositionYRaw;
        public long PositionZRaw;
        public long PreviousPositionXRaw;
        public long PreviousPositionYRaw;
        public long PreviousPositionZRaw;
        public long RotationYawRaw;
        public long PreviousRotationYawRaw;
        public long CurrentHpRaw;
        public int AttackState;
        public int AttackPhase;
        public int AttackFrame;
        public long KnockbackVelocityXRaw;
        public long KnockbackVelocityYRaw;
        public long KnockbackVelocityZRaw;
        public bool IsDead;
        public bool IsHurt;
        public bool IsMoving;
        public int BufferedAttack;
        public bool UltimateInputBuffered;
        public int ComboHitCount;
        public int ComboCounterWindowRemaining;
        public long HurtTimerRaw;
        public long UltimateStartXRaw;
        public long UltimateStartYRaw;
        public long UltimateStartZRaw;
        public long UltimateFurthestXRaw;
        public long UltimateFurthestYRaw;
        public long UltimateFurthestZRaw;
        public long UltimateFurthestDistanceSqrRaw;
        public long CurrentFrame;
        public int[] HitTargetsThisAttack = Array.Empty<int>();
    }

    /// <summary>
    /// 服务端最后一帧已确认输入。用于恢复远端“移动方向延续”，
    /// 但不会把攻击等边沿输入错误地延续到未来帧。
    /// </summary>
    public sealed class ReconnectInputData
    {
        public int PlayerId;
        public int InputMask;
        public int TargetEntityId;
    }

    /// <summary>
    /// G2C_ReconnectState 的深拷贝版本。
    /// </summary>
    public sealed class LockstepReconnectStateData
    {
        public string RoomId = string.Empty;
        public int ConfirmedFrame;
        public int NextFrame;
        public ulong WorldHash;
        public string RulesetVersion = string.Empty;
        public ReconnectPlayerSnapshotData[] Players =
            Array.Empty<ReconnectPlayerSnapshotData>();
        public ReconnectInputData[] LastInputs =
            Array.Empty<ReconnectInputData>();
    }
}

using System;
using System.Collections.Generic;
using Combat.Config;
using Combat.Core;

namespace Hotfix.Lockstep
{
    /// <summary>
    /// 服务端权威 CombatWorld。
    ///
    /// 服务端与 Unity 客户端链接同一批 Combat.Core 源码并使用同一份 Luban JSON。
    /// 它接收已经收齐的锁步输入，以固定 50ms 步长推进世界，并为重连生成快照。
    /// </summary>
    internal sealed class AuthoritativeCombatSimulator
    {
        private readonly PlayerCombatConfig config;
        private readonly List<CombatEvent> discardedEvents =
            new List<CombatEvent>(16);

        public AuthoritativeCombatSimulator()
        {
            config = CombatConfigJsonLoader.LoadPlayer(
                1001,
                out CombatAttackConfig[] attacks);
            config.Attacks = attacks;
            Reset();
        }

        public CombatWorld World { get; private set; } = null!;

        /// <summary>
        /// 服务端权威帧 Hash。
        ///
        /// 计算时机与客户端完全一致：实体 Tick 完成后、DrainEvents 之前。
        /// 客户端 LockstepManager 也是在 Tick 后立刻调用 CalculateWorldHash。
        /// </summary>
        public ulong AuthorityHash { get; private set; }

        /// <summary>
        /// 快照状态的 Hash。服务端在每个权威帧推进后立即 DrainEvents，
        /// 因此重连 Hash 只覆盖可持续恢复的实体状态，与客户端恢复后重算一致。
        /// </summary>
        public ulong StateHash { get; private set; }

        public void Reset()
        {
            World = new CombatWorld(Fix64.FromDecimal(0.05m));
            if (!CombatSpawnRules.TryGetSpawn(
                    1,
                    out LogicVector3 playerOneSpawn,
                    out LogicRotation playerOneRotation) ||
                !CombatSpawnRules.TryGetSpawn(
                    2,
                    out LogicVector3 playerTwoSpawn,
                    out LogicRotation playerTwoRotation))
            {
                throw new InvalidOperationException(
                    "CombatSpawnRules must define spawns for PlayerId 1 and 2.");
            }

            World.RegisterEntity(new PlayerEntity(
                1,
                config,
                playerOneSpawn,
                playerOneRotation));
            World.RegisterEntity(new PlayerEntity(
                2,
                config,
                playerTwoSpawn,
                playerTwoRotation));

            discardedEvents.Clear();
            AuthorityHash = World.CalculateWorldHash();
            StateHash = AuthorityHash;
        }

        public AuthoritativeTickResult Tick(
            IReadOnlyList<KeyValuePair<
                int,
                LockstepRoomSystem.LockstepInput>> orderedInputs)
        {
            if (orderedInputs == null)
            {
                throw new ArgumentNullException(nameof(orderedInputs));
            }

            Dictionary<int, CombatInputFrame> frameInputs =
                new Dictionary<int, CombatInputFrame>(
                    orderedInputs.Count);

            for (int i = 0; i < orderedInputs.Count; i++)
            {
                KeyValuePair<
                    int,
                    LockstepRoomSystem.LockstepInput> pair =
                    orderedInputs[i];
                frameInputs.Add(
                    pair.Key,
                    ToCombatInput(pair.Value));
            }

            World.Tick(frameInputs);
            AuthorityHash = World.CalculateWorldHash();
            CombatWorld.Snapshot snapshot =
                World.CaptureSnapshot();

            World.DrainEvents(discardedEvents);
            discardedEvents.Clear();
            StateHash = World.CalculateWorldHash();

            return new AuthoritativeTickResult(
                AuthorityHash,
                StateHash,
                snapshot);
        }

        public CombatWorld.Snapshot CaptureSnapshot()
        {
            return World.CaptureSnapshot();
        }

        private static CombatInputFrame ToCombatInput(
            LockstepRoomSystem.LockstepInput input)
        {
            FrameInputMask mask =
                (FrameInputMask)(uint)input.InputMask;
            Fix64 x = Fix64.Zero;
            Fix64 z = Fix64.Zero;

            if ((mask & FrameInputMask.MoveForward) != 0)
            {
                z += Fix64.One;
            }

            if ((mask & FrameInputMask.MoveBackward) != 0)
            {
                z -= Fix64.One;
            }

            if ((mask & FrameInputMask.MoveRight) != 0)
            {
                x += Fix64.One;
            }

            if ((mask & FrameInputMask.MoveLeft) != 0)
            {
                x -= Fix64.One;
            }

            LogicVector3 direction =
                new LogicVector3(x, Fix64.Zero, z);
            return new CombatInputFrame
            {
                TargetEntityId = input.TargetEntityId,
                PrimaryAttackPressed =
                    (mask & FrameInputMask.PrimaryAttack) != 0,
                SecondaryAttackPressed =
                    (mask & FrameInputMask.SecondaryAttack) != 0,
                UltimatePressed =
                    (mask & FrameInputMask.Jump) != 0,
                HasMoveInput =
                    direction.SqrMagnitude > Fix64.Zero,
                MoveDirection = direction
            };
        }
    }

    /// <summary>
    /// 一帧权威计算的完整结果：
    /// AuthorityHash 用于和客户端比较；
    /// Snapshot/StateHash 用于重连和 Desync 诊断。
    /// </summary>
    internal readonly struct AuthoritativeTickResult
    {
        public readonly ulong AuthorityHash;
        public readonly ulong StateHash;
        public readonly CombatWorld.Snapshot Snapshot;

        public AuthoritativeTickResult(
            ulong authorityHash,
            ulong stateHash,
            CombatWorld.Snapshot snapshot)
        {
            AuthorityHash = authorityHash;
            StateHash = stateHash;
            Snapshot = snapshot;
        }
    }
}

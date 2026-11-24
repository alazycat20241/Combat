using System;
using System.Collections.Generic;

namespace Combat.Core
{
    /// <summary>
    /// 唯一的战斗逻辑世界。
    ///
    /// 所有实体都由这里按 EntityId 排序后推进，避免依赖 Unity 脚本执行顺序。
    /// 逻辑步长和实体运算均使用 Fix64。
    /// </summary>
    public sealed class CombatWorld
    {
        private readonly List<PlayerEntity> entities = new List<PlayerEntity>(8);
        private readonly List<PlayerEntity> tickOrder = new List<PlayerEntity>(8);
        private readonly Dictionary<long, Dictionary<int, CombatInputFrame>>
            pendingInputsByFrame =
                new Dictionary<long, Dictionary<int, CombatInputFrame>>();
        private readonly List<CombatEvent> committedEvents = new List<CombatEvent>(16);

        public CombatWorld(Fix64 fixedDeltaTime)
        {
            if (fixedDeltaTime <= Fix64.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(fixedDeltaTime));
            }

            FixedDeltaTime = fixedDeltaTime;
        }

        public long Frame { get; private set; }
        public long NextFrame => Frame + 1;
        public Fix64 FixedDeltaTime { get; }
        public IReadOnlyList<PlayerEntity> Entities => entities;

        public void RegisterEntity(PlayerEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (FindEntity(entity.EntityId) != null)
            {
                throw new InvalidOperationException(
                    $"Duplicate EntityId {entity.EntityId}.");
            }

            entities.Add(entity);
        }

        public bool UnregisterEntity(PlayerEntity entity)
        {
            if (entity == null)
            {
                return false;
            }

            bool removed = entities.Remove(entity);
            RemovePendingInputsFor(entity.EntityId);
            return removed;
        }

        public PlayerEntity FindEntity(int entityId)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i].EntityId == entityId)
                {
                    return entities[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 提交下一逻辑帧输入。同一个实体在一帧内重复提交时，后一次覆盖前一次。
        /// </summary>
        public void SubmitInput(int entityId, CombatInputFrame input)
        {
            SubmitInput(new CombatInputCommand(NextFrame, entityId, input));
        }

        public void SubmitInput(CombatInputCommand command)
        {
            if (command.Frame <= Frame)
            {
                throw new InvalidOperationException(
                    $"Input frame {command.Frame} is stale. Current frame is {Frame}.");
            }

            if (FindEntity(command.EntityId) == null)
            {
                throw new InvalidOperationException(
                    $"Entity {command.EntityId} is not registered.");
            }

            if (!pendingInputsByFrame.TryGetValue(
                    command.Frame,
                    out Dictionary<int, CombatInputFrame> frameInputs))
            {
                frameInputs = new Dictionary<int, CombatInputFrame>(entities.Count);
                pendingInputsByFrame.Add(command.Frame, frameInputs);
            }

            if (frameInputs.ContainsKey(command.EntityId))
            {
                throw new InvalidOperationException(
                    $"Duplicate input for EntityId={command.EntityId}, Frame={command.Frame}.");
            }

            frameInputs.Add(command.EntityId, command.Input);
        }

        /// <summary>
        /// 提交一组网络中已经确认的完整帧输入，并立刻推进一个逻辑帧。
        ///
        /// 这个入口只接收纯 C# 数据，不知道输入来自 KCP、测试程序还是录像回放。
        /// LockstepManager 必须先确认该帧号等于 NextFrame，再调用本方法，确保不会
        /// 因服务端丢帧或乱序而提前执行未来帧。
        /// </summary>
        public void Tick(
            IReadOnlyDictionary<int, CombatInputFrame> frameInputs)
        {
            if (frameInputs == null)
            {
                throw new ArgumentNullException(nameof(frameInputs));
            }

            long executingFrame = NextFrame;

            foreach (KeyValuePair<int, CombatInputFrame> pair in frameInputs)
            {
                SubmitInput(new CombatInputCommand(
                    executingFrame,
                    pair.Key,
                    pair.Value));
            }

            Step();
        }

        /// <summary>
        /// 计算当前逻辑帧的世界状态哈希。
        ///
        /// EntityId 排序后依次合并实体 Hash，确保实体在列表中的注册顺序
        /// 不会影响结果。本帧产生的 CombatEvent 也纳入 Hash，便于发现
        /// “状态看似一致，但事件派发已经分叉”的问题。
        /// </summary>
        public ulong CalculateWorldHash()
        {
            ulong hash = StateHash.Create();
            hash = StateHash.AppendInt64(hash, Frame);
            hash = StateHash.AppendFix64(hash, FixedDeltaTime);

            tickOrder.Clear();
            tickOrder.AddRange(entities);
            tickOrder.Sort(EntityIdComparer.Instance);
            hash = StateHash.AppendInt32(hash, tickOrder.Count);

            for (int i = 0; i < tickOrder.Count; i++)
            {
                PlayerEntity entity = tickOrder[i];
                hash = StateHash.AppendInt32(hash, entity.EntityId);
                hash = StateHash.AppendUInt64(
                    hash,
                    entity.CalculateHash());
            }

            hash = StateHash.AppendInt32(
                hash,
                committedEvents.Count);

            for (int i = 0; i < committedEvents.Count; i++)
            {
                CombatEvent combatEvent = committedEvents[i];
                hash = StateHash.AppendInt64(
                    hash,
                    combatEvent.Frame);
                hash = StateHash.AppendInt32(
                    hash,
                    (int)combatEvent.Type);
                hash = StateHash.AppendInt32(
                    hash,
                    combatEvent.SourceEntityId);
                hash = StateHash.AppendInt32(
                    hash,
                    combatEvent.TargetEntityId);
                hash = StateHash.AppendInt32(
                    hash,
                    (int)combatEvent.AttackState);
                hash = StateHash.AppendFix64(
                    hash,
                    combatEvent.Damage);
            }

            return hash;
        }

        public sealed class Snapshot
        {
            public readonly long Frame;
            public readonly PlayerEntity.Snapshot[] Entities;

            public Snapshot(
                long frame,
                PlayerEntity.Snapshot[] entities)
            {
                if (frame < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(frame));
                }

                Frame = frame;
                Entities = entities == null
                    ? Array.Empty<PlayerEntity.Snapshot>()
                    : (PlayerEntity.Snapshot[])entities.Clone();
            }
        }

        /// <summary>
        /// 捕获当前 World 在 Tick 完成后的完整可回滚状态。
        /// Snapshot 创建后不可修改，可安全放入预测帧环形缓冲区。
        /// </summary>
        public Snapshot CaptureSnapshot()
        {
            tickOrder.Clear();
            tickOrder.AddRange(entities);
            tickOrder.Sort(EntityIdComparer.Instance);

            PlayerEntity.Snapshot[] states =
                new PlayerEntity.Snapshot[tickOrder.Count];

            for (int i = 0; i < tickOrder.Count; i++)
            {
                states[i] = tickOrder[i].CaptureSnapshot();
            }

            return new Snapshot(Frame, states);
        }

        /// <summary>
        /// 恢复到某个预测帧执行前的完整状态。
        /// 恢复后 pendingInputsByFrame 和 committedEvents 都会被清空，
        /// 防止旧时间线的事件或输入污染重演。
        /// </summary>
        public void RestoreSnapshot(Snapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (snapshot.Entities.Length != entities.Count)
            {
                throw new InvalidOperationException(
                    "Snapshot entity count does not match CombatWorld.");
            }

            for (int i = 0; i < snapshot.Entities.Length; i++)
            {
                PlayerEntity.Snapshot state = snapshot.Entities[i];
                PlayerEntity entity = FindEntity(state.EntityId);
                if (entity == null)
                {
                    throw new InvalidOperationException(
                        $"Snapshot entity {state.EntityId} is not registered.");
                }

                entity.RestoreSnapshot(state);
            }

            Frame = snapshot.Frame;
            pendingInputsByFrame.Clear();
            committedEvents.Clear();
            tickOrder.Clear();
        }

        public void DrainEvents(List<CombatEvent> destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.AddRange(committedEvents);
            committedEvents.Clear();
        }

        /// <summary>
        /// 把世界时钟重置到指定帧，并清空所有尚未执行的输入与事件。
        /// Entity 的出生状态由上层调用 ResetForTesting 后一起恢复。
        /// </summary>
        public void ResetFrame(long frame = 0)
        {
            if (frame < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame));
            }

            Frame = frame;
            pendingInputsByFrame.Clear();
            committedEvents.Clear();
            tickOrder.Clear();
        }

        /// <summary>
        /// 推进一个逻辑帧。实体按稳定的 EntityId 升序处理。
        /// </summary>
        public void Step()
        {
            if (entities.Count == 0)
            {
                Frame++;
                committedEvents.Clear();
                return;
            }

            tickOrder.Clear();
            tickOrder.AddRange(entities);
            tickOrder.Sort(EntityIdComparer.Instance);
            committedEvents.Clear();

            long executingFrame = NextFrame;
            pendingInputsByFrame.TryGetValue(
                executingFrame,
                out Dictionary<int, CombatInputFrame> frameInputs);

            for (int i = 0; i < tickOrder.Count; i++)
            {
                tickOrder[i].BeginFrame(executingFrame);
            }

            for (int i = 0; i < tickOrder.Count; i++)
            {
                PlayerEntity entity = tickOrder[i];
                if (frameInputs == null ||
                    !frameInputs.TryGetValue(entity.EntityId, out CombatInputFrame input))
                {
                    input = default;
                }

                entity.Tick(input, FixedDeltaTime, tickOrder);
            }

            for (int i = 0; i < tickOrder.Count; i++)
            {
                tickOrder[i].DrainEvents(committedEvents);
            }

            pendingInputsByFrame.Remove(executingFrame);
            Frame = executingFrame;
        }

        private bool RemovePendingInputsFor(int entityId)
        {
            bool removed = false;
            List<long> emptyFrames = null;

            foreach (KeyValuePair<long, Dictionary<int, CombatInputFrame>> pair
                     in pendingInputsByFrame)
            {
                if (pair.Value.Remove(entityId))
                {
                    removed = true;
                }

                if (pair.Value.Count == 0)
                {
                    emptyFrames ??= new List<long>();
                    emptyFrames.Add(pair.Key);
                }
            }

            if (emptyFrames != null)
            {
                for (int i = 0; i < emptyFrames.Count; i++)
                {
                    pendingInputsByFrame.Remove(emptyFrames[i]);
                }
            }

            return removed;
        }

        private sealed class EntityIdComparer : IComparer<PlayerEntity>
        {
            public static readonly EntityIdComparer Instance =
                new EntityIdComparer();

            public int Compare(PlayerEntity x, PlayerEntity y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x == null)
                {
                    return -1;
                }

                if (y == null)
                {
                    return 1;
                }

                return x.EntityId.CompareTo(y.EntityId);
            }
        }
    }
}

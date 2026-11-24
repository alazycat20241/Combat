using System;
using System.Collections.Generic;

namespace Combat.Core
{
    /// <summary>
    /// 客户端预测 + 服务端确认校正 + 历史重演的锁步管理器。
    ///
    /// 时间线：
    /// 1. 服务端确认首帧后，客户端不再等待每一帧的网络往返。
    /// 2. 每个 FixedUpdate 立即预测下一帧。
    /// 3. 已确认帧到达后与预测输入对比。
    /// 4. 首个不一致帧从 Snapshot 恢复，并重演到当前预测时间。
    /// </summary>
    public sealed class LockstepManager
    {
        private const FrameInputMask ContinuousInputMask =
            FrameInputMask.MoveForward |
            FrameInputMask.MoveBackward |
            FrameInputMask.MoveLeft |
            FrameInputMask.MoveRight;

        private const FrameInputMask TriggerInputMask =
            FrameInputMask.PrimaryAttack |
            FrameInputMask.SecondaryAttack |
            FrameInputMask.Jump;

        private const int DefaultPredictionBufferFrames = 128;
        private const int MaxPredictionAheadFrames = 3;

        private sealed class PredictionFrame
        {
            public long Frame;
            public CombatWorld.Snapshot SnapshotBefore;
            public Dictionary<int, CombatInputFrame> Inputs;
            public ulong PredictedHash;
            public bool Valid;
        }

        private readonly struct CombatEventKey :
            IEquatable<CombatEventKey>
        {
            private readonly long frame;
            private readonly CombatEventType type;
            private readonly int sourceEntityId;
            private readonly int targetEntityId;
            private readonly AttackState attackState;

            public CombatEventKey(CombatEvent combatEvent)
            {
                frame = combatEvent.Frame;
                type = combatEvent.Type;
                sourceEntityId = combatEvent.SourceEntityId;
                targetEntityId = combatEvent.TargetEntityId;
                attackState = combatEvent.AttackState;
            }

            public bool Equals(CombatEventKey other)
            {
                return frame == other.frame &&
                       type == other.type &&
                       sourceEntityId == other.sourceEntityId &&
                       targetEntityId == other.targetEntityId &&
                       attackState == other.attackState;
            }

            public override bool Equals(object obj)
            {
                return obj is CombatEventKey other &&
                       Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)frame;
                    hash = (hash * 397) ^ (int)type;
                    hash = (hash * 397) ^ sourceEntityId;
                    hash = (hash * 397) ^ targetEntityId;
                    hash = (hash * 397) ^ (int)attackState;
                    return hash;
                }
            }
        }

        private readonly object incomingFrameLock = new object();
        private readonly CombatWorld world;
        private readonly List<int> playerIds = new List<int>(4);
        private readonly Dictionary<int, FrameInputMask> pendingInputMasks =
            new Dictionary<int, FrameInputMask>();
        private readonly Dictionary<int, int> preferredTargets =
            new Dictionary<int, int>();
        private readonly Dictionary<int, CombatInputFrame> lastKnownInputs =
            new Dictionary<int, CombatInputFrame>();
        private readonly SortedDictionary<long, FrameData> incomingFrames =
            new SortedDictionary<long, FrameData>();
        private readonly PredictionFrame[] predictionBuffer;
        private readonly List<CombatEvent> committedEvents =
            new List<CombatEvent>(16);
        private readonly List<CombatEvent> discardedReplayEvents =
            new List<CombatEvent>(32);
        private readonly HashSet<CombatEventKey> emittedCombatEvents =
            new HashSet<CombatEventKey>();

        private ILockstepNetwork network;
        private long nextInputFrameToSend = 1;
        private long lastConfirmedFrame;
        private bool predictionStarted;
        private bool hasServerAssignedPlayerId;

        public LockstepManager(
            Fix64 fixedDeltaTime,
            int predictionBufferFrames =
                DefaultPredictionBufferFrames)
        {
            if (predictionBufferFrames < 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(predictionBufferFrames),
                    "Prediction buffer must contain at least 4 frames.");
            }

            world = new CombatWorld(fixedDeltaTime);
            predictionBuffer =
                new PredictionFrame[predictionBufferFrames];
        }

        public CombatWorld World => world;
        public ILockstepNetwork Network => network;
        public event Action<long, ulong> WorldHashCalculated;

        /// <summary>
        /// 回滚重演完成时触发一次。表现层应在这时把 Transform
        /// 直接吸附到最新逻辑状态，而不是逐历史帧播放动画。
        /// </summary>
        public event Action<long> RollbackCompleted;

        /// <summary>
        /// 服务端权威重连快照恢复完成。表现层只能在此时做一次 Snap，
        /// 不能把恢复过程拆成多个历史帧交给 Animator 播放。
        /// </summary>
        public event Action<long> ReconnectStateApplied;

        public ulong CurrentWorldHash { get; private set; }
        public long CurrentFrame => world.NextFrame;

        /// <summary>
        /// 当前客户端逻辑时间线最后预测到的帧。
        /// </summary>
        public long LastCompletedFrame => world.Frame;

        /// <summary>
        /// 最近一帧服务端确认帧。
        /// </summary>
        public long LastConfirmedFrame => lastConfirmedFrame;

        public long NextInputFrameToSend => nextInputFrameToSend;
        public int LocalClientId { get; private set; }
        public bool HasServerAssignedPlayerId =>
            hasServerAssignedPlayerId;
        public bool PredictionStarted => predictionStarted;
        public IReadOnlyList<int> PlayerIds => playerIds;

        public int PendingFrameCount
        {
            get
            {
                lock (incomingFrameLock)
                {
                    return incomingFrames.Count;
                }
            }
        }

        public void SetNetwork(ILockstepNetwork network)
        {
            this.network = network ??
                throw new ArgumentNullException(nameof(network));
        }

        public void RegisterPlayer(int playerId, PlayerEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (playerId != entity.EntityId)
            {
                throw new InvalidOperationException(
                    "PlayerId must match EntityId for lockstep input routing.");
            }

            if (playerIds.Contains(playerId))
            {
                return;
            }

            world.RegisterEntity(entity);
            playerIds.Add(playerId);
            playerIds.Sort();
            pendingInputMasks[playerId] = FrameInputMask.None;
            preferredTargets[playerId] = 0;
            lastKnownInputs[playerId] = default;

            if (LocalClientId == 0)
            {
                LocalClientId = playerId;
            }
        }

        public void UnregisterPlayer(int playerId)
        {
            PlayerEntity entity = world.FindEntity(playerId);
            if (entity != null)
            {
                world.UnregisterEntity(entity);
            }

            playerIds.Remove(playerId);
            pendingInputMasks.Remove(playerId);
            preferredTargets.Remove(playerId);
            lastKnownInputs.Remove(playerId);

            if (LocalClientId == playerId)
            {
                LocalClientId = playerIds.Count > 0
                    ? playerIds[0]
                    : 0;
                hasServerAssignedPlayerId = false;
                predictionStarted = false;
            }
        }

        public void SetLocalPlayerId(int playerId)
        {
            if (!playerIds.Contains(playerId))
            {
                throw new InvalidOperationException(
                    $"Player {playerId} is not registered.");
            }

            LocalClientId = playerId;
            hasServerAssignedPlayerId = true;
            nextInputFrameToSend = CurrentFrame;

            for (int i = 0; i < playerIds.Count; i++)
            {
                pendingInputMasks[playerIds[i]] = FrameInputMask.None;
            }
        }

        public void ClearServerAssignedPlayerId()
        {
            hasServerAssignedPlayerId = false;
            predictionStarted = false;
            nextInputFrameToSend = CurrentFrame;

            for (int i = 0; i < playerIds.Count; i++)
            {
                pendingInputMasks[playerIds[i]] = FrameInputMask.None;
            }
        }

        /// <summary>
        /// 清理上一局并回到 Frame=0。
        /// 新对局必须先调用本方法，再等待服务端重新分配本局 PlayerId。
        /// </summary>
        public void ResetForNewMatch()
        {
            for (int i = 0; i < playerIds.Count; i++)
            {
                int playerId = playerIds[i];
                PlayerEntity? entity = world.FindEntity(playerId);
                if (entity != null)
                {
                    CombatSpawnRules.TryGetSpawn(
                        playerId,
                        out LogicVector3 position,
                        out LogicRotation rotation);
                    entity.ResetForTesting(position, rotation);
                }

                pendingInputMasks[playerId] = FrameInputMask.None;
                preferredTargets[playerId] = 0;
                lastKnownInputs[playerId] = default;
            }

            lock (incomingFrameLock)
            {
                incomingFrames.Clear();
            }

            for (int i = 0; i < predictionBuffer.Length; i++)
            {
                predictionBuffer[i] = null;
            }

            committedEvents.Clear();
            discardedReplayEvents.Clear();
            emittedCombatEvents.Clear();
            world.ResetFrame();

            nextInputFrameToSend = 1;
            lastConfirmedFrame = 0;
            predictionStarted = false;
            hasServerAssignedPlayerId = false;
            CurrentWorldHash = 0;
        }

        public void SubmitLocalInput(
            int playerId,
            FrameInputMask inputMask)
        {
            if (!pendingInputMasks.ContainsKey(playerId))
            {
                throw new InvalidOperationException(
                    $"Player {playerId} is not registered.");
            }

            if (playerId != LocalClientId)
            {
                pendingInputMasks[playerId] = inputMask;
                return;
            }

            FrameInputMask pending = pendingInputMasks[playerId];
            pendingInputMasks[playerId] =
                (inputMask & ContinuousInputMask) |
                (pending & TriggerInputMask) |
                (inputMask & TriggerInputMask);
        }

        public void SetPreferredTarget(
            int playerId,
            int targetPlayerId)
        {
            if (!pendingInputMasks.ContainsKey(playerId))
            {
                throw new InvalidOperationException(
                    $"Player {playerId} is not registered.");
            }

            preferredTargets[playerId] = targetPlayerId;
        }

        public void EnqueueFrameData(int frameId, FrameInput[] inputs)
        {
            if (frameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameId));
            }

            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            FrameData frameData = new FrameData(
                frameId,
                (FrameInput[])inputs.Clone());

            lock (incomingFrameLock)
            {
                if (!incomingFrames.ContainsKey(frameData.Frame))
                {
                    incomingFrames.Add(frameData.Frame, frameData);
                }
            }
        }

        /// <summary>
        /// 用服务端权威快照重建本地逻辑时间线。
        ///
        /// 成功返回后：
        /// 1. CombatWorld.Frame 等于服务端 confirmedFrame；
        /// 2. 所有旧预测、旧确认队列、旧事件哈希全部丢弃；
        /// 3. 下一帧从服务端给出的 nextFrame 继续预测。
        ///
        /// 本方法只改逻辑层，不直接写 Unity Transform 或 Animator。
        /// </summary>
        public void ApplyReconnectState(
            CombatWorld.Snapshot authoritativeSnapshot,
            int confirmedFrame,
            int nextFrame,
            IReadOnlyList<FrameInput> lastInputs,
            ulong authoritativeWorldHash)
        {
            if (authoritativeSnapshot == null)
            {
                throw new ArgumentNullException(
                    nameof(authoritativeSnapshot));
            }

            if (confirmedFrame < 0 || nextFrame != confirmedFrame + 1)
            {
                throw new ArgumentException(
                    $"Invalid reconnect frames: confirmed={confirmedFrame}, " +
                    $"next={nextFrame}.");
            }

            if (authoritativeSnapshot.Frame != confirmedFrame)
            {
                throw new InvalidOperationException(
                    $"Reconnect snapshot frame {authoritativeSnapshot.Frame} " +
                    $"does not match confirmed frame {confirmedFrame}.");
            }

            world.RestoreSnapshot(authoritativeSnapshot);
            if (world.Frame != confirmedFrame ||
                world.NextFrame != nextFrame)
            {
                throw new InvalidOperationException(
                    "CombatWorld frame did not match the reconnect state.");
            }

            lock (incomingFrameLock)
            {
                incomingFrames.Clear();
            }

            for (int i = 0; i < predictionBuffer.Length; i++)
            {
                predictionBuffer[i] = null;
            }

            committedEvents.Clear();
            discardedReplayEvents.Clear();
            emittedCombatEvents.Clear();
            lastKnownInputs.Clear();

            for (int i = 0; i < playerIds.Count; i++)
            {
                int playerId = playerIds[i];
                pendingInputMasks[playerId] = FrameInputMask.None;
                preferredTargets[playerId] = 0;
                lastKnownInputs[playerId] = default;
            }

            if (lastInputs != null)
            {
                for (int i = 0; i < lastInputs.Count; i++)
                {
                    FrameInput input = lastInputs[i];
                    if (!lastKnownInputs.ContainsKey(input.PlayerId))
                    {
                        continue;
                    }

                    lastKnownInputs[input.PlayerId] =
                        ToCombatInput(input);
                    preferredTargets[input.PlayerId] =
                        input.TargetEntityId;
                }
            }

            lastConfirmedFrame = confirmedFrame;
            nextInputFrameToSend = nextFrame;
            predictionStarted = true;
            CurrentWorldHash = authoritativeWorldHash;

            ReconnectStateApplied?.Invoke(world.Frame);
        }

        /// <summary>
        /// 每个 FixedUpdate 调用。
        ///
        /// 先使用服务端确认输入修正历史，再生成下一预测帧。
        /// 首帧确认前不预测，避免第二名玩家尚未加入时时间线无限领先。
        /// </summary>
        public void AdvanceFixedFrame()
        {
            ReconcileConfirmedFrames();

            if (!hasServerAssignedPlayerId ||
                network == null ||
                !network.IsConnected)
            {
                return;
            }

            if (!predictionStarted)
            {
                SendBootstrapInput();
                return;
            }

            // 服务端确认暂停时，客户端最多领先 8 帧。
            // 缺失这个上限会让预测时间线无限向前，最终与服务端帧号彻底脱节。
            if (lastConfirmedFrame > 0 &&
                world.Frame >=
                lastConfirmedFrame + MaxPredictionAheadFrames)
            {
                return;
            }

            PredictNextFrame();
        }

        private void SendBootstrapInput()
        {
            long frameId = nextInputFrameToSend;
            if (frameId != CurrentFrame || frameId > int.MaxValue)
            {
                return;
            }

            pendingInputMasks.TryGetValue(
                LocalClientId,
                out FrameInputMask localMask);
            preferredTargets.TryGetValue(
                LocalClientId,
                out int targetEntityId);

            if (!network.TrySendFrameInput(
                    (int)frameId,
                    localMask,
                    targetEntityId))
            {
                return;
            }

            nextInputFrameToSend = frameId + 1;
            pendingInputMasks[LocalClientId] =
                localMask & ContinuousInputMask;
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

        private void PredictNextFrame()
        {
            long frameId = world.Frame + 1;
            CombatWorld.Snapshot snapshotBefore =
                world.CaptureSnapshot();

            Dictionary<int, CombatInputFrame> inputs =
                BuildPredictedInputs(
                    frameId,
                    out FrameInputMask localMask,
                    out int localTargetEntityId);

            world.Tick(inputs);
            ulong worldHash = world.CalculateWorldHash();

            StorePrediction(new PredictionFrame
            {
                Frame = frameId,
                SnapshotBefore = snapshotBefore,
                Inputs = inputs,
                PredictedHash = worldHash,
                Valid = true
            });

            PublishHash(frameId, worldHash);
            DrainPredictionEvents();
            SendLocalInput(
                frameId,
                localMask,
                localTargetEntityId);
        }

        private Dictionary<int, CombatInputFrame> BuildPredictedInputs(
            long frameId,
            out FrameInputMask localMask,
            out int localTargetEntityId)
        {
            pendingInputMasks.TryGetValue(
                LocalClientId,
                out localMask);
            preferredTargets.TryGetValue(
                LocalClientId,
                out localTargetEntityId);

            FrameInput localWireInput = new FrameInput(
                frameId,
                LocalClientId,
                localMask,
                localTargetEntityId);
            CombatInputFrame localInput =
                ToCombatInput(localWireInput);

            Dictionary<int, CombatInputFrame> inputs =
                new Dictionary<int, CombatInputFrame>(
                    playerIds.Count);

            for (int i = 0; i < playerIds.Count; i++)
            {
                int playerId = playerIds[i];
                if (playerId == LocalClientId)
                {
                    inputs.Add(playerId, localInput);
                    continue;
                }

                lastKnownInputs.TryGetValue(
                    playerId,
                    out CombatInputFrame remoteInput);

                // 远端没有新确认输入时，只延续移动方向。
                // 攻击/跳跃是边沿输入，不能在未来预测帧中反复重复。
                remoteInput.PrimaryAttackPressed = false;
                remoteInput.SecondaryAttackPressed = false;
                remoteInput.UltimatePressed = false;
                inputs.Add(playerId, remoteInput);
            }

            lastKnownInputs[LocalClientId] = localInput;
            pendingInputMasks[LocalClientId] =
                localMask & ContinuousInputMask;
            return inputs;
        }

        private void SendLocalInput(
            long frameId,
            FrameInputMask localMask,
            int localTargetEntityId)
        {
            if (network == null ||
                !network.IsConnected ||
                frameId != nextInputFrameToSend ||
                frameId > int.MaxValue)
            {
                return;
            }

            if (network.TrySendFrameInput(
                    (int)frameId,
                    localMask,
                    localTargetEntityId))
            {
                nextInputFrameToSend = frameId + 1;
            }
        }

        private void ReconcileConfirmedFrames()
        {
            List<FrameData> confirmations = TakeIncomingFrames();
            if (confirmations == null || confirmations.Count == 0)
            {
                return;
            }

            Dictionary<long, FrameData> confirmedByFrame =
                new Dictionary<long, FrameData>(
                    confirmations.Count);
            long latestConfirmed = lastConfirmedFrame;
            long earliestMismatch = long.MaxValue;

            for (int i = 0; i < confirmations.Count; i++)
            {
                FrameData frameData = confirmations[i];
                if (frameData.Frame <= 0)
                {
                    continue;
                }

                confirmedByFrame[frameData.Frame] = frameData;
                latestConfirmed = Math.Max(
                    latestConfirmed,
                    frameData.Frame);

                Dictionary<int, CombatInputFrame> actualInputs =
                    ConvertConfirmedInputs(frameData);

                if (TryGetPrediction(
                        frameData.Frame,
                        out PredictionFrame prediction))
                {
                    if (!InputsEqual(
                            prediction.Inputs,
                            actualInputs))
                    {
                        earliestMismatch = Math.Min(
                            earliestMismatch,
                            frameData.Frame);
                    }
                }
                else
                {
                    earliestMismatch = Math.Min(
                        earliestMismatch,
                        frameData.Frame);
                }
            }

            if (confirmedByFrame.Count == 0)
            {
                return;
            }

            if (!predictionStarted &&
                earliestMismatch == long.MaxValue)
            {
                predictionStarted = true;
            }

            if (earliestMismatch == long.MaxValue)
            {
                ApplyMatchingConfirmations(confirmedByFrame);
                lastConfirmedFrame = latestConfirmed;
                predictionStarted = true;
                return;
            }

            ReplayFromEarliestMismatch(
                earliestMismatch,
                latestConfirmed,
                confirmedByFrame);
            predictionStarted = true;
        }

        private void ApplyMatchingConfirmations(
            Dictionary<long, FrameData> confirmedByFrame)
        {
            foreach (KeyValuePair<long, FrameData> pair
                     in confirmedByFrame)
            {
                Dictionary<int, CombatInputFrame> actualInputs =
                    ConvertConfirmedInputs(pair.Value);
                UpdateLastKnownInputs(actualInputs);

                if (TryGetPrediction(
                        pair.Key,
                        out PredictionFrame prediction))
                {
                    SendConfirmedHash(
                        pair.Key,
                        prediction.PredictedHash);
                }
            }
        }

        private void ReplayFromEarliestMismatch(
            long earliestMismatch,
            long latestConfirmed,
            Dictionary<long, FrameData> confirmedByFrame)
        {
            bool notifyRollback = predictionStarted;
            CombatWorld.Snapshot baseSnapshot;

            if (TryGetPrediction(
                    earliestMismatch,
                    out PredictionFrame mismatchEntry))
            {
                baseSnapshot = mismatchEntry.SnapshotBefore;
            }
            else if (earliestMismatch == world.Frame + 1)
            {
                baseSnapshot = world.CaptureSnapshot();
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot rollback Frame={earliestMismatch}: " +
                    "snapshot is no longer in the prediction buffer.");
            }

            long replayTarget = Math.Max(
                world.Frame,
                latestConfirmed);
            world.RestoreSnapshot(baseSnapshot);

            for (long frameId = earliestMismatch;
                 frameId <= replayTarget;
                 frameId++)
            {
                Dictionary<int, CombatInputFrame> replayInputs;
                bool isConfirmed = confirmedByFrame.TryGetValue(
                    frameId,
                    out FrameData confirmedFrame);

                if (isConfirmed)
                {
                    replayInputs =
                        ConvertConfirmedInputs(confirmedFrame);
                }
                else if (TryGetPrediction(
                             frameId,
                             out PredictionFrame historicalPrediction))
                {
                    replayInputs = historicalPrediction.Inputs;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Missing predicted input while replaying Frame={frameId}.");
                }

                CombatWorld.Snapshot snapshotBefore =
                    world.CaptureSnapshot();
                world.Tick(replayInputs);
                ulong worldHash = world.CalculateWorldHash();

                StorePrediction(new PredictionFrame
                {
                    Frame = frameId,
                    SnapshotBefore = snapshotBefore,
                    Inputs = CloneInputs(replayInputs),
                    PredictedHash = worldHash,
                    Valid = true
                });

                PublishHash(frameId, worldHash);

                if (isConfirmed)
                {
                    UpdateLastKnownInputs(replayInputs);
                    SendConfirmedHash(frameId, worldHash);
                }

                world.DrainEvents(discardedReplayEvents);

                // 确认帧重演产生的是权威事件，可以在最终重演完成后补发。
                // 已由预测阶段播放过的事件通过 Key 去重，避免重复 Trigger。
                if (isConfirmed)
                {
                    for (int eventIndex = 0;
                         eventIndex < discardedReplayEvents.Count;
                         eventIndex++)
                    {
                        CombatEvent combatEvent =
                            discardedReplayEvents[eventIndex];
                        CombatEventKey key =
                            new CombatEventKey(combatEvent);

                        if (emittedCombatEvents.Add(key))
                        {
                            committedEvents.Add(combatEvent);
                        }
                    }
                }

                discardedReplayEvents.Clear();
            }

            lastConfirmedFrame = Math.Max(
                lastConfirmedFrame,
                latestConfirmed);

            if (notifyRollback)
            {
                RollbackCompleted?.Invoke(world.Frame);
            }
        }

        private List<FrameData> TakeIncomingFrames()
        {
            lock (incomingFrameLock)
            {
                if (incomingFrames.Count == 0)
                {
                    return null;
                }

                List<FrameData> result =
                    new List<FrameData>(incomingFrames.Count);
                result.AddRange(incomingFrames.Values);
                incomingFrames.Clear();
                return result;
            }
        }

        private Dictionary<int, CombatInputFrame> ConvertConfirmedInputs(
            FrameData frameData)
        {
            if (frameData.Inputs.Length != playerIds.Count)
            {
                throw new InvalidOperationException(
                    $"Frame {frameData.Frame} is incomplete. " +
                    $"Expected {playerIds.Count}, " +
                    $"received {frameData.Inputs.Length}.");
            }

            Dictionary<int, CombatInputFrame> result =
                new Dictionary<int, CombatInputFrame>(
                    playerIds.Count);

            for (int i = 0; i < frameData.Inputs.Length; i++)
            {
                FrameInput input = frameData.Inputs[i];
                if (!pendingInputMasks.ContainsKey(input.PlayerId))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} contains unregistered " +
                        $"PlayerId={input.PlayerId}.");
                }

                if (!result.TryAdd(
                        input.PlayerId,
                        ToCombatInput(input)))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} contains duplicate " +
                        $"PlayerId={input.PlayerId}.");
                }
            }

            for (int i = 0; i < playerIds.Count; i++)
            {
                if (!result.ContainsKey(playerIds[i]))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} is missing " +
                        $"PlayerId={playerIds[i]}.");
                }
            }

            return result;
        }

        private void UpdateLastKnownInputs(
            Dictionary<int, CombatInputFrame> inputs)
        {
            foreach (KeyValuePair<int, CombatInputFrame> pair in inputs)
            {
                lastKnownInputs[pair.Key] = pair.Value;
            }
        }

        private void SendConfirmedHash(long frameId, ulong worldHash)
        {
            if (network == null ||
                !network.IsConnected ||
                frameId > int.MaxValue)
            {
                return;
            }

            network.TrySendFrameHash((int)frameId, worldHash);
        }

        private void PublishHash(long frameId, ulong worldHash)
        {
            CurrentWorldHash = worldHash;
            WorldHashCalculated?.Invoke(frameId, worldHash);
        }

        private void DrainPredictionEvents()
        {
            world.DrainEvents(discardedReplayEvents);

            for (int i = 0; i < discardedReplayEvents.Count; i++)
            {
                CombatEvent combatEvent = discardedReplayEvents[i];
                emittedCombatEvents.Add(
                    new CombatEventKey(combatEvent));
                committedEvents.Add(combatEvent);
            }

            discardedReplayEvents.Clear();
        }

        private void StorePrediction(PredictionFrame frame)
        {
            int index = (int)(frame.Frame % predictionBuffer.Length);
            predictionBuffer[index] = frame;
        }

        private bool TryGetPrediction(
            long frameId,
            out PredictionFrame prediction)
        {
            int index = (int)(frameId % predictionBuffer.Length);
            prediction = predictionBuffer[index];
            return prediction != null &&
                   prediction.Valid &&
                   prediction.Frame == frameId;
        }

        private static Dictionary<int, CombatInputFrame> CloneInputs(
            Dictionary<int, CombatInputFrame> inputs)
        {
            return new Dictionary<int, CombatInputFrame>(inputs);
        }

        private static bool InputsEqual(
            Dictionary<int, CombatInputFrame> predicted,
            Dictionary<int, CombatInputFrame> actual)
        {
            if (predicted.Count != actual.Count)
            {
                return false;
            }

            foreach (KeyValuePair<
                         int,
                         CombatInputFrame> pair in predicted)
            {
                if (!actual.TryGetValue(
                        pair.Key,
                        out CombatInputFrame actualInput) ||
                    !InputEqual(pair.Value, actualInput))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InputEqual(
            CombatInputFrame left,
            CombatInputFrame right)
        {
            return left.TargetEntityId == right.TargetEntityId &&
                   left.PrimaryAttackPressed ==
                   right.PrimaryAttackPressed &&
                   left.SecondaryAttackPressed ==
                   right.SecondaryAttackPressed &&
                   left.UltimatePressed == right.UltimatePressed &&
                   left.HasMoveInput == right.HasMoveInput &&
                   left.MoveDirection.X.RawValue ==
                   right.MoveDirection.X.RawValue &&
                   left.MoveDirection.Y.RawValue ==
                   right.MoveDirection.Y.RawValue &&
                   left.MoveDirection.Z.RawValue ==
                   right.MoveDirection.Z.RawValue;
        }

        private static CombatInputFrame ToCombatInput(FrameInput input)
        {
            Fix64 x = Fix64.Zero;
            Fix64 z = Fix64.Zero;

            if (input.Has(FrameInputMask.MoveForward)) z += Fix64.One;
            if (input.Has(FrameInputMask.MoveBackward)) z -= Fix64.One;
            if (input.Has(FrameInputMask.MoveRight)) x += Fix64.One;
            if (input.Has(FrameInputMask.MoveLeft)) x -= Fix64.One;

            LogicVector3 direction =
                new LogicVector3(x, Fix64.Zero, z);
            return new CombatInputFrame
            {
                TargetEntityId = input.TargetEntityId,
                PrimaryAttackPressed =
                    input.Has(FrameInputMask.PrimaryAttack),
                SecondaryAttackPressed =
                    input.Has(FrameInputMask.SecondaryAttack),
                UltimatePressed =
                    input.Has(FrameInputMask.Jump),
                HasMoveInput =
                    direction.SqrMagnitude > Fix64.Zero,
                MoveDirection = direction
            };
        }
    }
}

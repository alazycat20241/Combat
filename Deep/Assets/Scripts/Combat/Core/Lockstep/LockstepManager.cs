using System;
using System.Collections.Generic;

namespace Combat.Core
{
    /// <summary>
    /// 真实网络驱动的锁步总管理器。
    ///
    /// 它不负责读取 Unity Input，也不直接依赖 MonoBehaviour：
    /// 1. PlayerController 在 FixedUpdate 中把本地输入暂存到 SubmitLocalInput。
    /// 2. 本类通过 ILockstepNetwork 把输入提交给服务端。
    /// 3. Unity 网络适配层把 G2C_FrameData 深拷贝成纯 FrameData 后调用
    ///    EnqueueFrameData。
    /// 4. 只有当前需要执行的完整帧已经进入本地帧队列时，CombatWorld 才允许 Tick。
    /// 5. 缺少当前帧时直接等待，绝不使用本地输入“猜”一整帧继续运行。
    ///
    /// 这种“逻辑只读已确认帧”的约束，是后续客户端预测和回滚能够正确工作的基础。
    /// </summary>
    public sealed class LockstepManager
    {
        /// <summary>
        /// 仅用于启动阶段补齐输入流水线时，重复携带移动键。
        ///
        /// 攻击和终极技是“按下一次”的边沿输入，不能在补齐未来的帧时重复发送，
        /// 否则一个左键可能被服务端回放成多次攻击。
        /// </summary>
        private const FrameInputMask ContinuousInputMask =
            FrameInputMask.MoveForward |
            FrameInputMask.MoveBackward |
            FrameInputMask.MoveLeft |
            FrameInputMask.MoveRight;

        /// <summary>
        /// 按下一次的边沿输入。
        /// 网络等待期间必须累积，发送成功后才能清除。
        /// </summary>
        private const FrameInputMask TriggerInputMask =
            FrameInputMask.PrimaryAttack |
            FrameInputMask.SecondaryAttack |
            FrameInputMask.Jump;

        private readonly object incomingFrameLock = new object();
        private readonly CombatWorld world;
        private readonly List<int> playerIds = new List<int>(4);

        /// <summary>
        /// 每个玩家最新采集到、但还没塞进待发送输入帧的掩码。
        /// PlayerController 提交后，LockstepManager 在 FixedUpdate 中消费它。
        /// </summary>
        private readonly Dictionary<int, FrameInputMask> pendingInputMasks =
            new Dictionary<int, FrameInputMask>();

        private readonly Dictionary<int, int> preferredTargets =
            new Dictionary<int, int>();

        /// <summary>
        /// 服务端已经确认并广播的完整帧。
        ///
        /// 使用 SortedDictionary 而不是直接保存 G2C_FrameData：
        /// Fantasy 会在 Handler 返回后回收网络消息，所以这里保存的必须是
        /// 完全脱离消息对象和池化生命周期的内部纯数据帧。
        /// </summary>
        private readonly SortedDictionary<long, FrameData> incomingFrames =
            new SortedDictionary<long, FrameData>();

        private readonly List<CombatEvent> committedEvents =
            new List<CombatEvent>(16);

        private ILockstepNetwork network;
        private readonly int inputLeadFrames;

        /// <summary>
        /// 下一个尚未发送给服务端的输入帧号。
        ///
        /// 它通常领先 CurrentFrame 若干帧，但不能跳过中间帧。
        /// 服务端的锁步房间只推进当前帧，因此 1、2、3 未收齐时直接发帧 4，
        /// 会让服务端永远等不到帧 1，最终形成死锁。
        /// </summary>
        private long nextInputFrameToSend = 1;
        private bool hasServerAssignedPlayerId;

        public LockstepManager(
            Fix64 fixedDeltaTime,
            int inputLeadFrames = 3)
        {
            if (inputLeadFrames < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputLeadFrames));
            }

            world = new CombatWorld(fixedDeltaTime);
            this.inputLeadFrames = inputLeadFrames;
        }

        public CombatWorld World => world;
        public ILockstepNetwork Network => network;

        /// <summary>
        /// 当前等待执行、也是 CombatWorld 下一逻辑帧的帧号。
        /// </summary>
        public long CurrentFrame => world.NextFrame;

        /// <summary>
        /// 最近成功执行并确认的帧号。
        /// </summary>
        public long LastCompletedFrame => world.Frame;

        /// <summary>
        /// 下一个准备发送的输入帧号，便于调试网络流水线。
        /// </summary>
        public long NextInputFrameToSend => nextInputFrameToSend;

        public int LocalClientId { get; private set; }
        public bool HasServerAssignedPlayerId =>
            hasServerAssignedPlayerId;
        public IReadOnlyList<int> PlayerIds => playerIds;

        /// <summary>
        /// 当前缓存了多少个尚未执行的服务端完整帧。
        /// </summary>
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

        /// <summary>
        /// 注入真实网络实现。
        ///
        /// CombatSimulationRunner 在 NetworkManager 创建完成后调用。
        /// 纯逻辑程序集因此只认识 ILockstepNetwork，不认识 Unity NetworkManager。
        /// </summary>
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

            // 第一个注册实体只作为尚未收到服务端身份时的本地引用。
            // 在 SetLocalPlayerId 被调用前，本类不会发送任何帧输入，
            // 因此不会把临时槽位误当成服务端分配的联机身份。
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

            if (LocalClientId == playerId)
            {
                LocalClientId = playerIds.Count > 0 ? playerIds[0] : 0;
                hasServerAssignedPlayerId = false;
            }
        }

        /// <summary>
        /// 显式指定本客户端的玩家 ID。
        /// CombatSimulationRunner 会使用 G2C_PlayerAssigned 下发的 PlayerId
        /// 调用本方法，而不是使用客户端自增的 runtimeEntityId。
        /// </summary>
        public void SetLocalPlayerId(int playerId)
        {
            if (!playerIds.Contains(playerId))
            {
                throw new InvalidOperationException(
                    $"Player {playerId} is not registered.");
            }

            LocalClientId = playerId;
            hasServerAssignedPlayerId = true;

            // 切换本地槽位时清除尚未发送的旧角色输入，
            // 防止把切换前缓存的攻击误发给新身份。
            for (int i = 0; i < playerIds.Count; i++)
            {
                pendingInputMasks[playerIds[i]] = FrameInputMask.None;
            }
        }

        /// <summary>
        /// 断线时撤销服务端身份授权。
        /// 重连后必须重新收到 G2C_PlayerAssigned，客户端才会再次发送输入。
        /// </summary>
        public void ClearServerAssignedPlayerId()
        {
            hasServerAssignedPlayerId = false;
            nextInputFrameToSend = CurrentFrame;

            for (int i = 0; i < playerIds.Count; i++)
            {
                pendingInputMasks[playerIds[i]] = FrameInputMask.None;
            }
        }

        /// <summary>
        /// PlayerController 每个 Unity FixedUpdate 调用一次。
        /// 这里只保存输入，不立即发包，也不推进战斗世界。
        /// </summary>
        public void SubmitLocalInput(int playerId, FrameInputMask inputMask)
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

            // 移动是持续状态：用最新采样覆盖旧方向。
            // 攻击/跳跃是边沿状态：与尚未发送的旧按键取并集，避免等待帧期间丢键。
            pendingInputMasks[playerId] =
                (inputMask & ContinuousInputMask) |
                (pending & TriggerInputMask) |
                (inputMask & TriggerInputMask);
        }

        public void SetPreferredTarget(int playerId, int targetPlayerId)
        {
            if (!pendingInputMasks.ContainsKey(playerId))
            {
                throw new InvalidOperationException(
                    $"Player {playerId} is not registered.");
            }

            preferredTargets[playerId] = targetPlayerId;
        }

        /// <summary>
        /// 接收 Unity 网络层已经深拷贝过的纯数据帧。
        ///
        /// 即使调用方已经复制一次，这里仍再次 Clone，确保 LockstepManager
        /// 的队列不会和网络消息池、临时数组发生任何引用共享。
        /// </summary>
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
                // 同一帧重复到达时保留第一份。后续若要做多路冗余传输，
                // 可以在这里比较两份输入并记录不一致，而不是直接覆盖。
                if (!incomingFrames.ContainsKey(frameData.Frame))
                {
                    incomingFrames.Add(frameData.Frame, frameData);
                }
            }
        }

        /// <summary>
        /// 每个 20Hz 逻辑循环调用一次。
        ///
        /// 步骤：
        /// 1. 先消费所有“当前帧已经到达”的完整网络帧，让 CombatWorld 追上服务端。
        /// 2. 再采集本机 PlayerController 刚提交的输入。
        /// 3. 把输入顺序提交到服务端，维持 CurrentFrame + inputLeadFrames 的流水线。
        ///
        /// 如果当前帧尚未到达，第一步不会执行任何 CombatWorld.Tick；
        /// 表现层因此会停留在上一帧逻辑结果，也就是“等待网络”。
        /// </summary>
        public void AdvanceFixedFrame()
        {
            ExecuteAvailableConfirmedFrames();
            SubmitInputAndMaintainLead();
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

        private void SubmitInputAndMaintainLead()
        {
            if (LocalClientId == 0 ||
                !hasServerAssignedPlayerId)
            {
                return;
            }

            pendingInputMasks.TryGetValue(
                LocalClientId,
                out FrameInputMask currentMask);

            if (network == null || !network.IsConnected)
            {
                // 断线期间不无限缓存旧的攻击按键，避免重连后突然触发旧操作。
                // 移动方向仍由 PlayerController 下一次 SubmitLocalInput 刷新。
                pendingInputMasks[LocalClientId] =
                    currentMask & ContinuousInputMask;
                return;
            }

            // 服务端只能按顺序确认帧。若客户端已经因为重连或异常跳过了旧帧，
            // 不再尝试发送服务端早已错过的帧号。
            if (nextInputFrameToSend < CurrentFrame)
            {
                nextInputFrameToSend = CurrentFrame;
            }

            // 这是发送目标的上沿，而不是直接从这里开始发送。
            // 例如 CurrentFrame=1、Lead=3 时，首次固定帧会发送 1、2、3、4，
            // 其中 1 携带完整输入，2～4 只延续移动键作为启动填充。
            //
            // 如果第一包直接发 4，服务端会一直等待 1，帧 4 永远无法被确认。
            long targetFrame = CurrentFrame + inputLeadFrames;
            if (targetFrame > int.MaxValue)
            {
                targetFrame = int.MaxValue;
            }

            bool firstFrameInBatch = true;
            bool sentAtLeastOneFrame = false;

            while (nextInputFrameToSend <= targetFrame)
            {
                // 协议字段是 int32，不能让 long 帧号静默溢出。
                if (nextInputFrameToSend > int.MaxValue)
                {
                    break;
                }

                FrameInputMask frameMask = firstFrameInBatch
                    ? currentMask
                    : currentMask & ContinuousInputMask;

                bool sent = network.TrySendFrameInput(
                    (int)nextInputFrameToSend,
                    frameMask);

                if (!sent)
                {
                    // 保留同一个待发送帧号，等网络恢复后从这里继续。
                    break;
                }

                firstFrameInBatch = false;
                sentAtLeastOneFrame = true;
                nextInputFrameToSend++;
            }

            // 只有攻击/跳跃真正进入发送通道之后才消费。
            // 如果当前帧窗口暂时不需要发送，边沿输入会保留到下一轮。
            if (sentAtLeastOneFrame)
            {
                pendingInputMasks[LocalClientId] &=
                    ContinuousInputMask;
            }
        }

        private void ExecuteAvailableConfirmedFrames()
        {
            // 网络可能一次送来多个已确认帧。全部按顺序消费，避免客户端永久落后。
            while (TryDequeueFrame(CurrentFrame, out FrameData frameData))
            {
                ExecuteCommittedFrame(frameData);
            }
        }

        private bool TryDequeueFrame(long frameId, out FrameData frameData)
        {
            lock (incomingFrameLock)
            {
                // 已经确认并执行过的迟到包不参与回放。
                while (incomingFrames.Count > 0)
                {
                    long firstBufferedFrame = 0;
                    foreach (long bufferedFrame in incomingFrames.Keys)
                    {
                        firstBufferedFrame = bufferedFrame;
                        break;
                    }

                    if (firstBufferedFrame >= frameId)
                    {
                        break;
                    }

                    incomingFrames.Remove(firstBufferedFrame);
                }

                if (!incomingFrames.TryGetValue(
                        frameId,
                        out frameData))
                {
                    return false;
                }

                incomingFrames.Remove(frameId);
                return true;
            }
        }

        private void ExecuteCommittedFrame(FrameData frameData)
        {
            if (frameData.Frame != CurrentFrame)
            {
                throw new InvalidOperationException(
                    $"Lockstep frame mismatch. Received {frameData.Frame}, " +
                    $"expected {CurrentFrame}.");
            }

            if (frameData.Inputs.Length != playerIds.Count)
            {
                throw new InvalidOperationException(
                    $"Frame {frameData.Frame} is incomplete. " +
                    $"Expected {playerIds.Count} players, " +
                    $"received {frameData.Inputs.Length}.");
            }

            Dictionary<int, CombatInputFrame> combatInputs =
                new Dictionary<int, CombatInputFrame>(playerIds.Count);

            for (int i = 0; i < frameData.Inputs.Length; i++)
            {
                FrameInput frameInput = frameData.Inputs[i];

                if (!pendingInputMasks.ContainsKey(frameInput.PlayerId))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} contains unregistered " +
                        $"PlayerId={frameInput.PlayerId}.");
                }

                if (combatInputs.ContainsKey(frameInput.PlayerId))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} contains duplicate " +
                        $"PlayerId={frameInput.PlayerId}.");
                }

                preferredTargets.TryGetValue(
                    frameInput.PlayerId,
                    out int targetEntityId);

                combatInputs.Add(
                    frameInput.PlayerId,
                    ToCombatInput(frameInput, targetEntityId));
            }

            for (int i = 0; i < playerIds.Count; i++)
            {
                if (!combatInputs.ContainsKey(playerIds[i]))
                {
                    throw new InvalidOperationException(
                        $"Frame {frameData.Frame} is missing input for " +
                        $"PlayerId={playerIds[i]}.");
                }
            }

            world.Tick(combatInputs);
            world.DrainEvents(committedEvents);
        }

        private static CombatInputFrame ToCombatInput(
            FrameInput input,
            int targetEntityId)
        {
            Fix64 x = Fix64.Zero;
            Fix64 z = Fix64.Zero;

            if (input.Has(FrameInputMask.MoveForward)) z += Fix64.One;
            if (input.Has(FrameInputMask.MoveBackward)) z -= Fix64.One;
            if (input.Has(FrameInputMask.MoveRight)) x += Fix64.One;
            if (input.Has(FrameInputMask.MoveLeft)) x -= Fix64.One;

            LogicVector3 direction = new LogicVector3(x, Fix64.Zero, z);
            return new CombatInputFrame
            {
                TargetEntityId = targetEntityId,
                PrimaryAttackPressed =
                    input.Has(FrameInputMask.PrimaryAttack),
                SecondaryAttackPressed =
                    input.Has(FrameInputMask.SecondaryAttack),
                UltimatePressed =
                    input.Has(FrameInputMask.Jump),
                HasMoveInput = direction.SqrMagnitude > Fix64.Zero,
                MoveDirection = direction
            };
        }
    }
}

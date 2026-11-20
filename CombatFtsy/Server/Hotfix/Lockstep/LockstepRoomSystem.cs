using System;
using System.Collections.Generic;
using System.Linq;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;

namespace Hotfix.Lockstep
{
    /// <summary>
    /// 单 Gate 场景中的锁步房间系统。
    ///
    /// 职责：
    /// 1. 建立 Session 与玩家 ID 的可信绑定。
    /// 2. 按 frameId 暂存每个玩家提交的输入。
    /// 3. 每 50ms 检查当前帧是否已经收集完整。
    /// 4. 完整后向房间内所有 Session 广播 G2C_FrameData。
    ///
    /// 当前阶段只有一个战斗房间，因此系统按 Scene 延迟创建。
    /// 以后扩展为多房间时，应从匹配服务传入 roomId，并按 roomId 创建实例，
    /// 而不是继续依赖这个便捷入口。
    /// </summary>
    public sealed class LockstepRoomSystem
    {
        /// <summary>
        /// 固定逻辑帧间隔：50ms，即 20FPS。
        /// </summary>
        public const int FrameIntervalMilliseconds = 50;

        /// <summary>
        /// 单机联调固定为两名玩家。
        /// 正式房间应由匹配/加入房间流程动态设置 ExpectedPlayerCount。
        /// </summary>
        public const int DefaultExpectedPlayerCount = 2;

        /// <summary>
        /// 允许客户端最多提前提交当前帧之后的几帧。
        /// 这可以吸收“客户端采集定时器”和“服务端广播定时器”的相位差，
        /// 同时防止恶意客户端一次提交超大帧号，导致输入字典无限增长。
        /// </summary>
        private const int MaxFutureFrameLead = 3;

        private static readonly object InstanceLock = new object();
        private static readonly Dictionary<long, LockstepRoomSystem> Rooms =
            new Dictionary<long, LockstepRoomSystem>();

        private readonly object syncRoot = new object();
        private readonly Scene scene;

        /// <summary>
        /// 用户要求的锁步输入结构：帧号 -> (玩家 ID -> 输入掩码)。
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, int>> frameInputs =
            new Dictionary<int, Dictionary<int, int>>();

        /// <summary>
        /// 玩家 ID -> Session。
        /// 广播时只使用服务端保存的 Session，不接受客户端自行声明 PlayerId。
        /// </summary>
        private readonly Dictionary<int, Session> sessionsByPlayerId =
            new Dictionary<int, Session>();

        /// <summary>
        /// Session 的运行时 ID -> 玩家 ID，用于防止一条连接重复注册多个身份。
        /// </summary>
        private readonly Dictionary<long, int> playerIdsBySessionId =
            new Dictionary<long, int>();

        private int currentFrame = 1;
        private int expectedPlayerCount = DefaultExpectedPlayerCount;
        private long timerId;
        private bool started;
        private bool stopped;

        private LockstepRoomSystem(Scene scene)
        {
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
        }

        /// <summary>
        /// 当前服务端等待完整输入的帧号。
        /// </summary>
        public int CurrentFrame
        {
            get
            {
                lock (syncRoot)
                {
                    return currentFrame;
                }
            }
        }

        /// <summary>
        /// 当前房间要求提交输入的玩家数量。
        /// </summary>
        public int ExpectedPlayerCount
        {
            get
            {
                lock (syncRoot)
                {
                    return expectedPlayerCount;
                }
            }
        }

        /// <summary>
        /// 当前已绑定到房间的玩家数量。
        /// </summary>
        public int PlayerCount
        {
            get
            {
                lock (syncRoot)
                {
                    return sessionsByPlayerId.Count;
                }
            }
        }

        /// <summary>
        /// 创建或取得指定 Scene 的锁步房间。
        ///
        /// 定时器绑定到 Scene.TimerComponent.Net，因此会随 Fantasy Scene 的生命周期运行。
        /// </summary>
        public static LockstepRoomSystem GetOrCreate(
            Scene scene,
            int expectedPlayerCount = DefaultExpectedPlayerCount)
        {
            if (scene == null)
            {
                throw new ArgumentNullException(nameof(scene));
            }

            if (expectedPlayerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedPlayerCount),
                    "Expected player count must be greater than zero.");
            }

            lock (InstanceLock)
            {
                if (!Rooms.TryGetValue(
                        scene.Id,
                        out LockstepRoomSystem? room))
                {
                    room = new LockstepRoomSystem(scene);
                    Rooms.Add(scene.Id, room);
                    room.SetExpectedPlayerCount(expectedPlayerCount);
                    room.Start();
                }
                return room;
            }
        }

        /// <summary>
        /// 处理一条 C2G_SubmitFrameInput。
        ///
        /// 客户端没有传递 PlayerId，服务端用 Session 反查玩家 ID，
        /// 从而避免客户端伪造其他玩家的输入。
        /// </summary>
        public static bool SubmitFrameInput(
            Session session,
            int frameId,
            int inputMask,
            int expectedPlayerCount = DefaultExpectedPlayerCount)
        {
            if (session == null || session.IsDisposed || session.Scene == null)
            {
                return false;
            }

            return GetOrCreate(session.Scene, expectedPlayerCount)
                .SubmitInput(session, frameId, inputMask);
        }

        /// <summary>
        /// 客户端加入锁步房间并取得服务端分配的 PlayerId。
        ///
        /// 分配顺序固定为当前最小空闲 ID，当前两人房间即 1、2。
        /// 同一个 Session 重复请求时返回原 ID，保证请求具有幂等性。
        /// </summary>
        public static bool TryJoinLockstep(
            Session session,
            out int playerId,
            int expectedPlayerCount = DefaultExpectedPlayerCount)
        {
            playerId = 0;

            if (session == null ||
                session.IsDisposed ||
                session.Scene == null)
            {
                return false;
            }

            return GetOrCreate(session.Scene, expectedPlayerCount)
                .TryAssignPlayer(session, out playerId);
        }

        /// <summary>
        /// 为 Session 分配房间内唯一且空闲的 PlayerId。
        /// </summary>
        public bool TryAssignPlayer(Session session, out int playerId)
        {
            playerId = 0;

            if (session == null || session.IsDisposed)
            {
                return false;
            }

            lock (syncRoot)
            {
                if (stopped)
                {
                    return false;
                }

                // 新连接进入前先清理已经断开的旧 Session，
                // 这样旧玩家占用的 1 或 2 可以立即重新分配。
                PruneDisconnectedSessionsLocked();

                // 房间在上一次对局中可能已经推进到帧 5、帧 10……
                // 如果所有玩家都已离开，新玩家重新加入时必须从帧 1 开始，
                // 否则新客户端提交的帧 1～4 会被当成迟到帧，逻辑永远等待。
                if (sessionsByPlayerId.Count == 0)
                {
                    ResetForNewMatchLocked();
                }

                if (playerIdsBySessionId.TryGetValue(
                        session.Id,
                        out playerId))
                {
                    return true;
                }

                playerId = FindSmallestFreePlayerIdLocked();
                if (playerId == 0 ||
                    !RegisterPlayerLocked(session, playerId))
                {
                    playerId = 0;
                    return false;
                }

                Log.Info(
                    $"Lockstep room {scene.Id} assigned " +
                    $"SessionId={session.Id} to PlayerId={playerId}.");
                return true;
            }
        }

        /// <summary>
        /// 显式注册玩家。
        ///
        /// 加入房间协议内部已经使用 TryAssignPlayer 自动分配。
        /// 本方法保留给未来匹配服务指定固定槽位的场景。
        /// </summary>
        public bool RegisterPlayer(Session session, int playerId)
        {
            if (session == null || session.IsDisposed)
            {
                return false;
            }

            if (playerId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerId),
                    "PlayerId must be greater than zero.");
            }

            lock (syncRoot)
            {
                if (sessionsByPlayerId.Count >= expectedPlayerCount &&
                    !sessionsByPlayerId.ContainsKey(playerId))
                {
                    return false;
                }

                return RegisterPlayerLocked(session, playerId);
            }
        }

        /// <summary>
        /// 按玩家 ID 移除玩家。断线、退出房间或战斗结束时调用。
        /// </summary>
        public bool UnregisterPlayer(int playerId)
        {
            lock (syncRoot)
            {
                if (!sessionsByPlayerId.TryGetValue(
                        playerId,
                        out Session? session) ||
                    session == null)
                {
                    return false;
                }

                sessionsByPlayerId.Remove(playerId);
                playerIdsBySessionId.Remove(session.Id);
                RemovePlayerInputsLocked(playerId);
                return true;
            }
        }

        /// <summary>
        /// 动态修改预期玩家数。
        /// 正式房间在玩家加入、离开或从观战切换为参战时调用。
        /// </summary>
        public void SetExpectedPlayerCount(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (syncRoot)
            {
                expectedPlayerCount = count;
            }
        }

        /// <summary>
        /// 停止 50ms 定时器并清除房间状态。
        /// </summary>
        public void Stop()
        {
            long idToRemove;

            lock (syncRoot)
            {
                if (stopped)
                {
                    return;
                }

                stopped = true;
                started = false;
                idToRemove = timerId;
                timerId = 0;
                frameInputs.Clear();
                sessionsByPlayerId.Clear();
                playerIdsBySessionId.Clear();
            }

            if (idToRemove != 0)
            {
                scene.TimerComponent.Net.Remove(ref idToRemove);
            }

            lock (InstanceLock)
            {
                if (Rooms.TryGetValue(
                        scene.Id,
                        out LockstepRoomSystem? room) &&
                    ReferenceEquals(room, this))
                {
                    Rooms.Remove(scene.Id);
                }
            }
        }

        private void Start()
        {
            lock (syncRoot)
            {
                if (started || stopped)
                {
                    return;
                }

                started = true;
                timerId = scene.TimerComponent.Net.RepeatedTimer(
                    FrameIntervalMilliseconds,
                    Tick);
            }
        }

        /// <summary>
        /// 每 50ms 执行一次。
        ///
        /// 当前帧未收齐时什么也不做；收齐时只广播一次，然后 currentFrame++。
        /// 一次 Timer 回调只推进一帧，避免客户端短暂卡顿后服务端瞬间追赶多帧，
        /// 造成输入表现层和网络延迟统计失真。
        /// </summary>
        private void Tick()
        {
            List<Session>? recipients = null;
            List<KeyValuePair<int, int>>? orderedInputs = null;
            int committedFrame = 0;

            lock (syncRoot)
            {
                PruneDisconnectedSessionsLocked();

                if (TryTakeCompleteCurrentFrameLocked(
                        out committedFrame,
                        out orderedInputs))
                {
                    recipients = sessionsByPlayerId.Values.ToList();
                }
            }

            if (recipients == null || orderedInputs == null)
            {
                return;
            }

            if (committedFrame <= 8)
            {
                for (int inputIndex = 0;
                     inputIndex < orderedInputs.Count;
                     inputIndex++)
                {
                    KeyValuePair<int, int> input =
                        orderedInputs[inputIndex];
                    Log.Info(
                        $"Broadcasting lockstep Frame={committedFrame}, " +
                        $"PlayerId={input.Key}, " +
                        $"InputMask=0x{input.Value:X8}.");
                }
            }

            // 每个 Session 都创建一个独立消息对象。
            // Fantasy 发送并序列化后会回收消息；共享同一个对象给多个 Session 不安全。
            for (int i = 0; i < recipients.Count; i++)
            {
                Session session = recipients[i];
                if (session.IsDisposed)
                {
                    continue;
                }

                G2C_FrameData frameData = G2C_FrameData.Create();
                frameData.frameId = committedFrame;

                for (int inputIndex = 0;
                     inputIndex < orderedInputs.Count;
                     inputIndex++)
                {
                    KeyValuePair<int, int> input = orderedInputs[inputIndex];
                    PlayerInputData playerInput = PlayerInputData.Create();
                    playerInput.playerId = input.Key;
                    playerInput.inputMask = input.Value;
                    frameData.inputs.Add(playerInput);
                }

                session.Send(frameData);
            }
        }

        private bool SubmitInput(
            Session session,
            int frameId,
            int inputMask)
        {
            lock (syncRoot)
            {
                if (stopped || session.IsDisposed)
                {
                    return false;
                }

                PruneDisconnectedSessionsLocked();

                if (!playerIdsBySessionId.TryGetValue(
                        session.Id,
                        out int playerId))
                {
                    // 帧输入必须建立在明确加入房间之后。
                    // C2G_JoinLockstep 会先完成 Session -> PlayerId 的绑定，
                    // 这里不再根据输入包隐式猜测玩家身份。
                    Log.Warning(
                        $"Rejected input from unjoined SessionId={session.Id}. " +
                        $"Frame={frameId}.");
                    return false;
                }

                if (frameId < currentFrame)
                {
                    Log.Warning(
                        $"Rejected late lockstep input: Frame={frameId}, " +
                        $"CurrentFrame={currentFrame}, " +
                        $"PlayerId={playerId}.");
                    return false;
                }

                if (frameId > currentFrame + MaxFutureFrameLead)
                {
                    Log.Warning(
                        $"Rejected future lockstep input: Frame={frameId}, " +
                        $"CurrentFrame={currentFrame}, " +
                        $"PlayerId={playerId}.");
                    return false;
                }

                if (!frameInputs.TryGetValue(
                        frameId,
                        out Dictionary<int, int>? players) ||
                    players == null)
                {
                    players = new Dictionary<int, int>(
                        expectedPlayerCount);
                    frameInputs.Add(frameId, players);
                }

                if (players.TryGetValue(playerId, out int existingMask))
                {
                    // KCP 虽然可靠，但应用层去重仍然保留。
                    // 相同输入视为幂等成功，不同输入则拒绝，避免后包覆盖前包。
                    return existingMask == inputMask;
                }

                players.Add(playerId, inputMask);

                if (frameId <= 8)
                {
                    Log.Info(
                        $"Accepted lockstep input Frame={frameId}, " +
                        $"PlayerId={playerId}, " +
                        $"InputMask=0x{inputMask:X8}.");
                }

                return true;
            }
        }

        private bool RegisterPlayerLocked(Session session, int playerId)
        {
            if (sessionsByPlayerId.TryGetValue(
                    playerId,
                    out Session? existingSession) &&
                existingSession != null)
            {
                return existingSession.Id == session.Id;
            }

            if (playerIdsBySessionId.TryGetValue(
                    session.Id,
                    out int existingPlayerId))
            {
                return existingPlayerId == playerId;
            }

            sessionsByPlayerId.Add(playerId, session);
            playerIdsBySessionId.Add(session.Id, playerId);
            return true;
        }

        private int FindSmallestFreePlayerIdLocked()
        {
            for (int playerId = 1;
                 playerId <= expectedPlayerCount;
                 playerId++)
            {
                if (!sessionsByPlayerId.ContainsKey(playerId))
                {
                    return playerId;
                }
            }

            return 0;
        }

        private void ResetForNewMatchLocked()
        {
            if (currentFrame == 1 && frameInputs.Count == 0)
            {
                return;
            }

            Log.Info(
                $"Lockstep room {scene.Id} starts a new match. " +
                $"Reset current frame from {currentFrame} to 1.");

            currentFrame = 1;
            frameInputs.Clear();
        }

        private void PruneDisconnectedSessionsLocked()
        {
            if (sessionsByPlayerId.Count == 0)
            {
                return;
            }

            List<int>? disconnectedPlayerIds = null;

            foreach (KeyValuePair<int, Session> pair in sessionsByPlayerId)
            {
                if (pair.Value == null || pair.Value.IsDisposed)
                {
                    disconnectedPlayerIds ??= new List<int>();
                    disconnectedPlayerIds.Add(pair.Key);
                }
            }

            if (disconnectedPlayerIds == null)
            {
                return;
            }

            for (int i = 0; i < disconnectedPlayerIds.Count; i++)
            {
                int playerId = disconnectedPlayerIds[i];

                if (sessionsByPlayerId.TryGetValue(
                        playerId,
                        out Session? session) &&
                    session != null)
                {
                    playerIdsBySessionId.Remove(session.Id);
                    sessionsByPlayerId.Remove(playerId);
                }

                RemovePlayerInputsLocked(playerId);

                Log.Info(
                    $"Removed disconnected lockstep PlayerId={playerId} " +
                    $"from room {scene.Id}.");
            }
        }

        private void RemovePlayerInputsLocked(int playerId)
        {
            List<int>? emptyFrames = null;

            foreach (KeyValuePair<
                         int,
                         Dictionary<int, int>> framePair in frameInputs)
            {
                framePair.Value.Remove(playerId);

                if (framePair.Value.Count == 0)
                {
                    emptyFrames ??= new List<int>();
                    emptyFrames.Add(framePair.Key);
                }
            }

            if (emptyFrames == null)
            {
                return;
            }

            for (int i = 0; i < emptyFrames.Count; i++)
            {
                frameInputs.Remove(emptyFrames[i]);
            }
        }

        private bool TryTakeCompleteCurrentFrameLocked(
            out int frameId,
            out List<KeyValuePair<int, int>>? orderedInputs)
        {
            frameId = currentFrame;
            orderedInputs = null;

            if (sessionsByPlayerId.Count != expectedPlayerCount)
            {
                return false;
            }

            if (!frameInputs.TryGetValue(
                    currentFrame,
                    out Dictionary<int, int>? players) ||
                players == null)
            {
                return false;
            }

            foreach (int playerId in sessionsByPlayerId.Keys)
            {
                if (!players.ContainsKey(playerId))
                {
                    return false;
                }
            }

            orderedInputs = new List<KeyValuePair<int, int>>(
                players.Count);

            foreach (KeyValuePair<int, int> pair in players)
            {
                orderedInputs.Add(pair);
            }

            orderedInputs.Sort(
                (left, right) => left.Key.CompareTo(right.Key));

            frameInputs.Remove(currentFrame);
            currentFrame++;
            return true;
        }
    }

    /// <summary>
    /// 客户端连接成功后的锁步加入请求。
    /// 分配成功后立刻通过 G2C_PlayerAssigned 把唯一玩家 ID 返回客户端。
    /// </summary>
    public sealed class C2G_JoinLockstepHandler
        : Message<C2G_JoinLockstep>
    {
        protected override async FTask Run(
            Session session,
            C2G_JoinLockstep request)
        {
            if (!LockstepRoomSystem.TryJoinLockstep(
                    session,
                    out int playerId))
            {
                Log.Warning(
                    $"Lockstep room join rejected. " +
                    $"SessionId={session.Id}");
                return;
            }

            G2C_PlayerAssigned response =
                G2C_PlayerAssigned.Create();
            response.playerId = playerId;
            session.Send(response);

            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// Fantasy 外网消息处理器。
    ///
    /// C2G_SubmitFrameInput 在 proto 中被标记为 IMessage，
    /// 因此使用 Message&lt;T&gt;，而不是 MessageRPC。
    /// </summary>
    public sealed class C2G_SubmitFrameInputHandler
        : Message<C2G_SubmitFrameInput>
    {
        protected override async FTask Run(
            Session session,
            C2G_SubmitFrameInput message)
        {
            if (!LockstepRoomSystem.SubmitFrameInput(
                    session,
                    message.frameId,
                    message.inputMask))
            {
                Log.Warning(
                    $"Invalid lockstep input rejected. " +
                    $"SessionId={session.Id}, " +
                    $"Frame={message.frameId}.");
            }

            await FTask.CompletedTask;
        }
    }
}

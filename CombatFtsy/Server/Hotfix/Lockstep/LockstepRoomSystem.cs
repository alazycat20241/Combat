using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Combat.Core;
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
    /// 当前系统由匹配服务创建，并以全局唯一 roomId 管理多个战斗房间。
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
        /// 断线席位保留时间。超过后整个对局失效，避免用旧帧号污染新对局。
        /// </summary>
        public const int OfflineProtectionMilliseconds = 30000;
        public const int MatchReadyTimeoutMilliseconds = 30000;

        /// <summary>
        /// 客户端与服务端必须使用完全一致的精简规则版本。
        /// </summary>
        public const string RulesetVersion = "combat-core-v1";

        /// <summary>
        /// 允许客户端最多提前提交当前帧之后的几帧。
        /// 这可以吸收“客户端采集定时器”和“服务端广播定时器”的相位差，
        /// 同时防止恶意客户端一次提交超大帧号，导致输入字典无限增长。
        /// </summary>
        // 客户端开启预测后会领先服务端若干帧发送输入。
        // 保留足够的输入窗口，避免正常网络延迟触发“未来帧”拒绝。
        private const int MaxFutureFrameLead = 64;
        private const int HashRetentionFrames = 64;

        private enum PlayerSlotStatus
        {
            Online,
            Offline
        }

        private sealed class PlayerSlot
        {
            public int PlayerId;
            public long ProfileId;
            public string DisplayName = string.Empty;
            public Session? Session;
            public PlayerSlotStatus Status;
            public byte[] ReconnectTokenHash = Array.Empty<byte>();
            public long OfflineTimerId;
            public long OfflineGeneration;
        }

        private sealed class MatchSeat
        {
            public int PlayerId;
            public long ProfileId;
            public string DisplayName = string.Empty;
            public byte[] MatchTokenHash = Array.Empty<byte>();
            public Session? Session;
            public bool Ready;
        }

        internal sealed class MatchSettlementData
        {
            public string RoomId = string.Empty;
            public bool IsDraw;
            public int WinnerPlayerId;
            public int LoserPlayerId;
            public string WinnerName = string.Empty;
            public string LoserName = string.Empty;
            public int FinalFrame;
            public ulong WorldHash;
            public string Reason = string.Empty;
        }

        public readonly struct JoinCredentials
        {
            public readonly int PlayerId;
            public readonly string RoomId;
            public readonly string ReconnectToken;

            public JoinCredentials(
                int playerId,
                string roomId,
                string reconnectToken)
            {
                PlayerId = playerId;
                RoomId = roomId;
                ReconnectToken = reconnectToken;
            }
        }

        internal readonly struct LockstepInput
        {
            public readonly int InputMask;
            public readonly int TargetEntityId;

            public LockstepInput(int inputMask, int targetEntityId)
            {
                InputMask = inputMask;
                TargetEntityId = targetEntityId;
            }
        }

        private static readonly object InstanceLock = new object();
        private static readonly Dictionary<string, LockstepRoomSystem> Rooms =
            new Dictionary<string, LockstepRoomSystem>(
                StringComparer.Ordinal);
        private static readonly Dictionary<long, string> RoomIdBySessionId =
            new Dictionary<long, string>();

        private readonly object syncRoot = new object();
        private readonly Scene scene;
        private readonly string roomId;
        private readonly AuthoritativeCombatSimulator combat;
        private readonly Dictionary<int, MatchSeat> matchSeats =
            new Dictionary<int, MatchSeat>();

        /// <summary>
        /// 用户要求的锁步输入结构：帧号 -> (玩家 ID -> 输入掩码)。
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, LockstepInput>>
            frameInputs =
                new Dictionary<int, Dictionary<int, LockstepInput>>();

        /// <summary>
        /// 帧号 -> (PlayerId -> WorldHash)。
        /// 同一帧收齐所有玩家后立即比较并移除，避免持续占用内存。
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, ulong>> frameHashes =
            new Dictionary<int, Dictionary<int, ulong>>();

        /// <summary>
        /// 帧号 -> 服务端权威结果。保留最近 64 帧，用于和迟到的客户端
        /// Hash 比对，以及在 Desync 时导出对应帧的状态快照。
        /// </summary>
        private readonly Dictionary<int, AuthoritativeFrameRecord>
            authoritativeFrames =
                new Dictionary<int, AuthoritativeFrameRecord>();

        /// <summary>
        /// 防止同一帧、同一玩家在重复上报时反复写诊断文件。
        /// key = (frameId << 32) | playerId。
        /// </summary>
        private readonly HashSet<long> reportedAuthorityMismatches =
            new HashSet<long>();

        /// <summary>
        /// 玩家 ID -> Session。
        /// 广播时只使用服务端保存的 Session，不接受客户端自行声明 PlayerId。
        /// </summary>
        private readonly Dictionary<int, Session> sessionsByPlayerId =
            new Dictionary<int, Session>();

        /// <summary>
        /// 逻辑席位。它不随 Session 销毁，是断线重连恢复身份的唯一依据。
        /// </summary>
        private readonly Dictionary<int, PlayerSlot> playerSlots =
            new Dictionary<int, PlayerSlot>();

        /// <summary>
        /// Session 的运行时 ID -> 玩家 ID，用于防止一条连接重复注册多个身份。
        /// </summary>
        private readonly Dictionary<long, int> playerIdsBySessionId =
            new Dictionary<long, int>();

        private int currentFrame = 1;
        private int expectedPlayerCount = DefaultExpectedPlayerCount;
        private long timerId;
        private long matchReadyTimerId;
        private bool started;
        private bool stopped;
        private bool battleStarted;
        private bool matchFinished;
        private MatchSettlementData? settlement;
        private Dictionary<int, LockstepInput> lastCommittedInputs =
            new Dictionary<int, LockstepInput>();

        private LockstepRoomSystem(Scene scene, string roomId)
        {
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException(
                    "RoomId cannot be empty.",
                    nameof(roomId));
            }

            this.roomId = roomId;
            combat = new AuthoritativeCombatSimulator();
        }

        public string RoomId => roomId;

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
                    return playerSlots.Count;
                }
            }
        }

        /// <summary>
        /// 是否已经由服务端权威世界产生结算结果。
        /// </summary>
        public bool IsMatchFinished
        {
            get
            {
                lock (syncRoot)
                {
                    return matchFinished;
                }
            }
        }

        /// <summary>
        /// 由匹配系统创建唯一房间，并把服务端生成的 MatchToken 绑定到席位。
        /// </summary>
        public static bool CreateMatchRoom(
            Scene scene,
            string roomId,
            IReadOnlyList<MatchSeatAssignment> seats)
        {
            if (scene == null ||
                string.IsNullOrWhiteSpace(roomId) ||
                seats == null ||
                seats.Count != DefaultExpectedPlayerCount)
            {
                return false;
            }

            LockstepRoomSystem room =
                new LockstepRoomSystem(scene, roomId);
            room.ConfigureMatch(seats);

            lock (InstanceLock)
            {
                if (Rooms.ContainsKey(roomId))
                {
                    return false;
                }

                Rooms.Add(roomId, room);
            }

            room.ArmMatchReadyTimeout();
            return true;
        }

        public static bool TryGetRoom(
            string roomId,
            out LockstepRoomSystem? room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            lock (InstanceLock)
            {
                if (Rooms.TryGetValue(
                        roomId,
                        out LockstepRoomSystem? foundRoom))
                {
                    room = foundRoom;
                    return true;
                }

                return false;
            }
        }

        private static bool TryGetRoomForSession(
            Session session,
            out LockstepRoomSystem? room)
        {
            room = null;
            if (session == null || session.IsDisposed)
            {
                return false;
            }

            string? roomId;
            lock (InstanceLock)
            {
                if (!RoomIdBySessionId.TryGetValue(
                        session.Id,
                        out roomId))
                {
                    return false;
                }

                if (Rooms.TryGetValue(
                        roomId,
                        out LockstepRoomSystem? foundRoom))
                {
                    room = foundRoom;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 供消息 Handler 判断：当前失败是否只是结算后的迟到预测输入。
        /// </summary>
        public static bool IsSessionMatchFinished(Session session)
        {
            return TryGetRoomForSession(
                    session,
                    out LockstepRoomSystem? room) &&
                   room != null &&
                   room.IsMatchFinished;
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
            int targetEntityId)
        {
            if (!TryGetRoomForSession(
                    session,
                    out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            return room.SubmitInput(
                session,
                frameId,
                inputMask,
                targetEntityId);
        }

        /// <summary>
        /// 接收客户端执行完某帧后上报的 WorldHash。
        /// Session -> PlayerId 由服务端绑定，客户端不能伪造玩家身份。
        /// </summary>
        public static bool SubmitFrameHash(
            Session session,
            int frameId,
            ulong worldHash)
        {
            if (frameId <= 0 ||
                !TryGetRoomForSession(
                    session,
                    out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            return room.SubmitHash(session, frameId, worldHash);
        }

        /// <summary>
        /// 客户端加入锁步房间并取得服务端分配的 PlayerId。
        ///
        /// 分配顺序固定为当前最小空闲 ID，当前两人房间即 1、2。
        /// 同一个 Session 重复请求时返回原 ID，保证请求具有幂等性。
        /// </summary>
        public static bool TryJoinLockstep(
            Session session,
            string roomId,
            string matchToken,
            out JoinCredentials credentials)
        {
            credentials = default;

            if (session == null ||
                session.IsDisposed ||
                string.IsNullOrWhiteSpace(roomId) ||
                string.IsNullOrWhiteSpace(matchToken) ||
                !TryGetRoom(roomId, out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            return room.TryAssignPlayer(
                session,
                matchToken,
                out credentials);
        }

        /// <summary>
        /// 客户端重连成功后返回给协议 Handler 的完整结果。
        /// </summary>
        public sealed class ReconnectSuccess
        {
            public int PlayerId;
            public string ReconnectToken = string.Empty;
            public G2C_ReconnectState State = null!;
            internal MatchSettlementData? Settlement;
        }

        /// <summary>
        /// 用旧玩家身份重新绑定新 Session。
        ///
        /// 这里验证的是服务端保存的 Token 哈希，而不是相信客户端自报身份。
        /// 成功后立即轮换 Token，降低旧报文被重放的风险。
        /// </summary>
        public static bool TryReconnect(
            Session session,
            string roomId,
            int playerId,
            string reconnectToken,
            out ReconnectSuccess? success)
        {
            success = null;
            if (session == null ||
                session.IsDisposed ||
                session.Scene == null ||
                playerId <= 0 ||
                string.IsNullOrWhiteSpace(reconnectToken))
            {
                return false;
            }

            if (!TryGetRoom(roomId, out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            return room.TryReconnectPlayer(
                session,
                roomId,
                playerId,
                reconnectToken,
                out success);
        }

        /// <summary>
        /// 配置匹配席位。MatchToken 只在服务端保存哈希，
        /// 客户端拿到的明文令牌必须匹配自己的 ProfileId 才能进入房间。
        /// </summary>
        private void ConfigureMatch(
            IReadOnlyList<MatchSeatAssignment> assignments)
        {
            lock (syncRoot)
            {
                matchSeats.Clear();
                for (int i = 0; i < assignments.Count; i++)
                {
                    MatchSeatAssignment assignment = assignments[i];
                    matchSeats.Add(
                        assignment.PlayerId,
                        new MatchSeat
                        {
                            PlayerId = assignment.PlayerId,
                            ProfileId = assignment.ProfileId,
                            DisplayName = assignment.DisplayName,
                            MatchTokenHash = HashReconnectToken(
                                assignment.MatchToken)
                        });
                }

                expectedPlayerCount = assignments.Count;
            }
        }

        public static bool MarkReady(
            Session session,
            string roomId,
            string matchToken)
        {
            if (session == null ||
                session.IsDisposed ||
                !TryGetRoom(roomId, out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            return room.TryMarkReady(session, matchToken);
        }

        /// <summary>
        /// 客户端完成加载后准备。必须双方都 Ready 才广播 BattleStart。
        /// </summary>
        private bool TryMarkReady(
            Session session,
            string matchToken)
        {
            List<MatchSeat>? readySeats = null;
            bool allReady = false;

            lock (syncRoot)
            {
                if (stopped ||
                    session.IsDisposed ||
                    !PlayerProfileSystem.TryGetBySession(
                        session,
                        out PlayerProfileSystem.Profile? profile) ||
                    profile == null)
                {
                    return false;
                }

                byte[] tokenHash = HashReconnectToken(matchToken);
                MatchSeat? seat = null;
                foreach (MatchSeat candidate in matchSeats.Values)
                {
                    if (candidate.ProfileId == profile.ProfileId &&
                        CryptographicOperations.FixedTimeEquals(
                            candidate.MatchTokenHash,
                            tokenHash))
                    {
                        seat = candidate;
                        break;
                    }
                }

                if (seat == null)
                {
                    return false;
                }

                seat.Session = session;
                seat.Ready = true;

                if (battleStarted)
                {
                    G2C_BattleStart duplicate =
                        G2C_BattleStart.Create();
                    duplicate.roomId = RoomId;
                    duplicate.playerId = seat.PlayerId;
                    duplicate.rulesetVersion = RulesetVersion;
                    session.Send(duplicate);
                    return true;
                }

                allReady = true;
                foreach (MatchSeat candidate in matchSeats.Values)
                {
                    if (!candidate.Ready ||
                        candidate.Session == null ||
                        candidate.Session.IsDisposed)
                    {
                        allReady = false;
                        break;
                    }
                }

                if (allReady)
                {
                    battleStarted = true;
                    long setupTimer = matchReadyTimerId;
                    matchReadyTimerId = 0;
                    if (setupTimer != 0)
                    {
                        scene.TimerComponent.Net.Remove(
                            ref setupTimer);
                    }

                    readySeats =
                        matchSeats.Values
                            .OrderBy(value => value.PlayerId)
                            .ToList();
                }
            }

            if (allReady && readySeats != null)
            {
                for (int i = 0; i < readySeats.Count; i++)
                {
                    MatchSeat seat = readySeats[i];
                    G2C_BattleStart start = G2C_BattleStart.Create();
                    start.roomId = RoomId;
                    start.playerId = seat.PlayerId;
                    start.rulesetVersion = RulesetVersion;
                    seat.Session!.Send(start);
                }

                Log.Info(
                    $"Room {RoomId} is ready. " +
                    $"Broadcasting BattleStart to both players.");
            }

            return true;
        }

        /// <summary>
        /// 校验 MatchToken，并把 Session 绑定到预先分配好的席位。
        /// </summary>
        public bool TryAssignPlayer(
            Session session,
            string matchToken,
            out JoinCredentials credentials)
        {
            credentials = default;

            if (session == null || session.IsDisposed)
            {
                return false;
            }

            lock (syncRoot)
            {
                if (stopped ||
                    matchFinished ||
                    !battleStarted)
                {
                    return false;
                }

                PruneDisconnectedSessionsLocked();

                if (playerIdsBySessionId.TryGetValue(
                        session.Id,
                        out int existingPlayerId))
                {
                    if (!playerSlots.TryGetValue(
                            existingPlayerId,
                            out PlayerSlot? existingSlot))
                    {
                        return false;
                    }

                    // 当前 Session 重发 Join 时不能把明文 Token 当普通字段重复下发；
                    // 正式客户端只在第一次分配时保存 Token。
                    lock (InstanceLock)
                    {
                        RoomIdBySessionId[session.Id] = RoomId;
                    }
                    credentials = new JoinCredentials(
                        existingPlayerId,
                        RoomId,
                        RotateReconnectTokenLocked(existingSlot));
                    return true;
                }

                if (!PlayerProfileSystem.TryGetBySession(
                        session,
                        out PlayerProfileSystem.Profile? profile) ||
                    profile == null)
                {
                    return false;
                }

                byte[] tokenHash = HashReconnectToken(matchToken);
                MatchSeat? seat = null;
                foreach (MatchSeat candidate in matchSeats.Values)
                {
                    if (candidate.ProfileId == profile.ProfileId &&
                        CryptographicOperations.FixedTimeEquals(
                            candidate.MatchTokenHash,
                            tokenHash))
                    {
                        seat = candidate;
                        break;
                    }
                }

                if (seat == null ||
                    !RegisterPlayerLocked(
                        session,
                        seat,
                        out string reconnectToken))
                {
                    return false;
                }

                seat.Session = session;

                credentials = new JoinCredentials(
                    seat.PlayerId,
                    RoomId,
                    reconnectToken);
                Log.Info(
                    $"Lockstep room {RoomId} assigned " +
                    $"SessionId={session.Id} to PlayerId={seat.PlayerId}, " +
                    $"ProfileId={profile.ProfileId}.");

                if (playerSlots.Count == expectedPlayerCount)
                {
                    Start();
                }

                return true;
            }
        }

        private bool TryReconnectPlayer(
            Session session,
            string roomId,
            int playerId,
            string reconnectToken,
            out ReconnectSuccess? success)
        {
            success = null;

            lock (syncRoot)
            {
                if (stopped || session.IsDisposed)
                {
                    return false;
                }

                PruneDisconnectedSessionsLocked();

                if (!string.Equals(
                        roomId,
                        RoomId,
                        StringComparison.Ordinal))
                {
                    Log.Warning(
                        $"Reconnect rejected: RoomId={roomId} does not " +
                        $"match {RoomId}.");
                    return false;
                }

                if (!playerSlots.TryGetValue(
                        playerId,
                        out PlayerSlot? slot) ||
                    slot.Status == PlayerSlotStatus.Offline &&
                    slot.OfflineTimerId == 0)
                {
                    Log.Warning(
                        $"Reconnect rejected: PlayerId={playerId} has no " +
                        "active or protected slot.");
                    return false;
                }

                byte[] suppliedHash = HashReconnectToken(reconnectToken);
                if (!CryptographicOperations.FixedTimeEquals(
                        suppliedHash,
                        slot.ReconnectTokenHash))
                {
                    Log.Warning(
                        $"Reconnect rejected: invalid token for " +
                        $"PlayerId={playerId}.");
                    return false;
                }

                if (playerIdsBySessionId.ContainsKey(session.Id))
                {
                    Log.Warning(
                        $"Reconnect rejected: SessionId={session.Id} is " +
                        "already bound.");
                    return false;
                }

                // 服务端可能尚未收到旧 KCP 通道的 FIN，旧 Session 仍显示在线。
                // Token 已经证明身份，因此新连接可以接管并主动关闭旧 Session。
                if (slot.Status == PlayerSlotStatus.Online)
                {
                    Session? oldSession = slot.Session;
                    if (oldSession != null)
                    {
                        playerIdsBySessionId.Remove(oldSession.Id);
                        lock (InstanceLock)
                        {
                            RoomIdBySessionId.Remove(oldSession.Id);
                        }
                        sessionsByPlayerId.Remove(playerId);
                        if (!oldSession.IsDisposed &&
                            oldSession.Id != session.Id)
                        {
                            oldSession.Dispose();
                        }
                    }
                }

                // 旧连接可能已经提前提交了未来几帧。新连接接管后
                // 必须让这些输入作废，从权威 nextFrame 重新开始采集。
                RemovePlayerInputsLocked(playerId);
                RemovePlayerHashesLocked(playerId);

                long timerId = slot.OfflineTimerId;
                slot.OfflineTimerId = 0;
                if (timerId != 0)
                {
                    scene.TimerComponent.Net.Remove(ref timerId);
                }

                // 递增世代号。即使旧 30 秒定时器已经进入回调队列，
                // 它看到世代号不一致也不会误删刚刚重连的玩家。
                slot.OfflineGeneration++;

                string newToken = GenerateReconnectToken();
                slot.ReconnectTokenHash =
                    HashReconnectToken(newToken);
                slot.Session = session;
                slot.Status = PlayerSlotStatus.Online;
                sessionsByPlayerId[playerId] = session;
                playerIdsBySessionId[session.Id] = playerId;
                lock (InstanceLock)
                {
                    RoomIdBySessionId[session.Id] = RoomId;
                }

                if (matchSeats.TryGetValue(
                        playerId,
                        out MatchSeat? reconnectSeat))
                {
                    reconnectSeat.Session = session;
                }

                CombatWorld.Snapshot snapshot =
                    combat.CaptureSnapshot();
                int confirmedFrame = checked((int)snapshot.Frame);
                int nextFrame = checked((int)combat.World.NextFrame);

                G2C_ReconnectState state =
                    G2C_ReconnectState.Create();
                state.roomId = RoomId;
                state.confirmedFrame = confirmedFrame;
                state.nextFrame = nextFrame;
                state.worldHash = combat.StateHash;
                state.rulesetVersion = RulesetVersion;

                for (int i = 0; i < snapshot.Entities.Length; i++)
                {
                    PlayerEntity.SnapshotData player =
                        snapshot.Entities[i].ToData();
                    ReconnectPlayerState target =
                        ReconnectPlayerState.Create();
                    target.entityId = player.EntityId;
                    target.preferredTargetId =
                        player.PreferredTargetId;
                    target.positionXRaw =
                        player.LogicalPosition.X.RawValue;
                    target.positionYRaw =
                        player.LogicalPosition.Y.RawValue;
                    target.positionZRaw =
                        player.LogicalPosition.Z.RawValue;
                    target.previousPositionXRaw =
                        player.PreviousLogicalPosition.X.RawValue;
                    target.previousPositionYRaw =
                        player.PreviousLogicalPosition.Y.RawValue;
                    target.previousPositionZRaw =
                        player.PreviousLogicalPosition.Z.RawValue;
                    target.rotationYawRaw =
                        player.LogicalRotation.YawRadians.RawValue;
                    target.previousRotationYawRaw =
                        player.PreviousLogicalRotation.YawRadians.RawValue;
                    target.currentHpRaw = player.CurrentHP.RawValue;
                    target.attackState = (int)player.AttackState;
                    target.attackPhase = (int)player.AttackPhase;
                    target.attackFrame = player.AttackFrame;
                    target.knockbackVelocityXRaw =
                        player.KnockbackVelocity.X.RawValue;
                    target.knockbackVelocityYRaw =
                        player.KnockbackVelocity.Y.RawValue;
                    target.knockbackVelocityZRaw =
                        player.KnockbackVelocity.Z.RawValue;
                    target.isDead = player.IsDead;
                    target.isHurt = player.IsHurt;
                    target.isMoving = player.IsMoving;
                    target.bufferedAttack =
                        (int)player.BufferedAttack;
                    target.ultimateInputBuffered =
                        player.UltimateInputBuffered;
                    target.comboHitCount = player.ComboHitCount;
                    target.comboCounterWindowRemaining =
                        player.ComboCounterWindowRemaining;
                    target.hurtTimerRaw =
                        player.HurtTimer.RawValue;
                    target.ultimateStartXRaw =
                        player.UltimateStartPosition.X.RawValue;
                    target.ultimateStartYRaw =
                        player.UltimateStartPosition.Y.RawValue;
                    target.ultimateStartZRaw =
                        player.UltimateStartPosition.Z.RawValue;
                    target.ultimateFurthestXRaw =
                        player.UltimateFurthestPosition.X.RawValue;
                    target.ultimateFurthestYRaw =
                        player.UltimateFurthestPosition.Y.RawValue;
                    target.ultimateFurthestZRaw =
                        player.UltimateFurthestPosition.Z.RawValue;
                    target.ultimateFurthestDistanceSqrRaw =
                        player.UltimateFurthestDistanceSqr.RawValue;
                    target.currentFrame = player.CurrentFrame;
                    target.hitTargetsThisAttack.AddRange(
                        player.HitTargetsThisAttack);
                    state.players.Add(target);
                }

                foreach (KeyValuePair<int, LockstepInput> input
                         in lastCommittedInputs.OrderBy(
                             pair => pair.Key))
                {
                    PlayerInputData target = PlayerInputData.Create();
                    target.playerId = input.Key;
                    target.inputMask = input.Value.InputMask;
                    target.targetEntityId =
                        input.Value.TargetEntityId;
                    state.lastInputs.Add(target);
                }

                success = new ReconnectSuccess
                {
                    PlayerId = playerId,
                    ReconnectToken = newToken,
                    State = state,
                    Settlement = CloneSettlementLocked()
                };

                Log.Info(
                    $"Reconnected SessionId={session.Id} to " +
                    $"PlayerId={playerId}, " +
                    $"ConfirmedFrame={confirmedFrame}.");
                return true;
            }
        }

        /// <summary>
        /// 按玩家 ID 移除玩家。断线、退出房间或战斗结束时调用。
        /// </summary>
        public bool UnregisterPlayer(int playerId)
        {
            lock (syncRoot)
            {
                if (!playerSlots.TryGetValue(
                        playerId,
                        out PlayerSlot? slot))
                {
                    return false;
                }

                Session? session = slot.Session;
                if (session != null)
                {
                    playerIdsBySessionId.Remove(session.Id);
                    lock (InstanceLock)
                    {
                        RoomIdBySessionId.Remove(session.Id);
                    }
                }

                long timerId = slot.OfflineTimerId;
                slot.OfflineTimerId = 0;
                slot.OfflineGeneration++;
                if (timerId != 0)
                {
                    scene.TimerComponent.Net.Remove(ref timerId);
                }

                playerSlots.Remove(playerId);
                sessionsByPlayerId.Remove(playerId);
                RemovePlayerInputsLocked(playerId);
                RemovePlayerHashesLocked(playerId);
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

        private void ArmMatchReadyTimeout()
        {
            lock (syncRoot)
            {
                if (stopped || battleStarted)
                {
                    return;
                }

                matchReadyTimerId =
                    scene.TimerComponent.Net.OnceTimer(
                        MatchReadyTimeoutMilliseconds,
                        ExpireUnstartedMatch);
            }
        }

        private void ExpireUnstartedMatch()
        {
            List<Session>? sessionsToNotify = null;

            lock (syncRoot)
            {
                if (stopped || battleStarted)
                {
                    return;
                }

                sessionsToNotify = matchSeats.Values
                    .Select(seat => seat.Session)
                    .Where(session =>
                        session != null && !session.IsDisposed)
                    .Cast<Session>()
                    .ToList();
            }

            if (sessionsToNotify != null)
            {
                for (int i = 0; i < sessionsToNotify.Count; i++)
                {
                    G2C_LockstepClosed closed =
                        G2C_LockstepClosed.Create();
                    closed.reason = 4;
                    closed.message =
                        "Match preparation timed out.";
                    sessionsToNotify[i].Send(closed);
                }
            }

            Log.Warning(
                $"Room {RoomId} was not started within " +
                $"{MatchReadyTimeoutMilliseconds} ms and will be closed.");
            Stop();
        }

        /// <summary>
        /// 停止 50ms 定时器并清除房间状态。
        /// </summary>
        public void Stop()
        {
            long idToRemove;
            long setupIdToRemove;

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
                setupIdToRemove = matchReadyTimerId;
                matchReadyTimerId = 0;
                frameInputs.Clear();
                frameHashes.Clear();
                authoritativeFrames.Clear();
                reportedAuthorityMismatches.Clear();
                sessionsByPlayerId.Clear();
                foreach (long sessionId in playerIdsBySessionId.Keys)
                {
                    lock (InstanceLock)
                    {
                        RoomIdBySessionId.Remove(sessionId);
                    }
                }
                playerSlots.Clear();
                playerIdsBySessionId.Clear();
                lastCommittedInputs.Clear();
            }

            if (idToRemove != 0)
            {
                scene.TimerComponent.Net.Remove(ref idToRemove);
            }

            if (setupIdToRemove != 0)
            {
                scene.TimerComponent.Net.Remove(ref setupIdToRemove);
            }

            lock (InstanceLock)
            {
                if (Rooms.TryGetValue(
                        RoomId,
                        out LockstepRoomSystem? room) &&
                    ReferenceEquals(room, this))
                {
                    Rooms.Remove(RoomId);
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
            List<KeyValuePair<int, LockstepInput>>? orderedInputs = null;
            int committedFrame = 0;

            lock (syncRoot)
            {
                PruneDisconnectedSessionsLocked();
                PruneOldFrameHashesLocked();

                if (matchFinished)
                {
                    return;
                }

                if (TryTakeCompleteCurrentFrameLocked(
                        out committedFrame,
                        out orderedInputs))
                {
                    lastCommittedInputs =
                        orderedInputs!.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value);
                    AuthoritativeTickResult authority =
                        combat.Tick(orderedInputs!);
                    authoritativeFrames[committedFrame] =
                        new AuthoritativeFrameRecord
                        {
                            Frame = committedFrame,
                            AuthorityHash = authority.AuthorityHash,
                            StateHash = authority.StateHash,
                            Inputs = orderedInputs!.ToArray(),
                            Snapshot = authority.Snapshot
                        };
                    if (TryCreateSettlementLocked(
                            committedFrame,
                            authority.AuthorityHash,
                            out MatchSettlementData? newSettlement))
                    {
                        settlement = newSettlement;
                        matchFinished = true;
                    }

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
                    KeyValuePair<int, LockstepInput> input =
                        orderedInputs[inputIndex];
                    Log.Info(
                        $"Broadcasting lockstep Frame={committedFrame}, " +
                        $"PlayerId={input.Key}, " +
                        $"InputMask=0x{input.Value.InputMask:X8}, " +
                        $"TargetEntityId={input.Value.TargetEntityId}.");
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
                    KeyValuePair<int, LockstepInput> input =
                        orderedInputs[inputIndex];
                    PlayerInputData playerInput = PlayerInputData.Create();
                    playerInput.playerId = input.Key;
                    playerInput.inputMask = input.Value.InputMask;
                    playerInput.targetEntityId =
                        input.Value.TargetEntityId;
                    frameData.inputs.Add(playerInput);
                }

                session.Send(frameData);
            }

            MatchSettlementData? settlementToSend;
            lock (syncRoot)
            {
                settlementToSend = CloneSettlementLocked();
            }

            if (settlementToSend != null)
            {
                for (int i = 0; i < recipients.Count; i++)
                {
                    Session session = recipients[i];
                    if (!session.IsDisposed)
                    {
                        session.Send(CreateSettlementMessage(
                            settlementToSend));
                    }
                }
            }
        }

        private bool TryCreateSettlementLocked(
            int finalFrame,
            ulong worldHash,
            out MatchSettlementData? result)
        {
            result = null;
            PlayerEntity? playerOne =
                combat.World.FindEntity(1);
            PlayerEntity? playerTwo =
                combat.World.FindEntity(2);
            if (playerOne == null || playerTwo == null)
            {
                return false;
            }

            bool playerOneDead = playerOne.IsDead;
            bool playerTwoDead = playerTwo.IsDead;
            if (!playerOneDead && !playerTwoDead)
            {
                return false;
            }

            matchSeats.TryGetValue(
                1,
                out MatchSeat? seatOne);
            matchSeats.TryGetValue(
                2,
                out MatchSeat? seatTwo);

            result = new MatchSettlementData
            {
                RoomId = RoomId,
                IsDraw = playerOneDead && playerTwoDead,
                WinnerPlayerId =
                    playerOneDead == playerTwoDead
                        ? 0
                        : playerOneDead ? 2 : 1,
                LoserPlayerId =
                    playerOneDead == playerTwoDead
                        ? 0
                        : playerOneDead ? 1 : 2,
                WinnerName =
                    playerOneDead == playerTwoDead
                        ? string.Empty
                        : playerOneDead
                            ? seatTwo?.DisplayName ?? "Player2"
                            : seatOne?.DisplayName ?? "Player1",
                LoserName =
                    playerOneDead == playerTwoDead
                        ? string.Empty
                        : playerOneDead
                            ? seatOne?.DisplayName ?? "Player1"
                            : seatTwo?.DisplayName ?? "Player2",
                FinalFrame = finalFrame,
                WorldHash = worldHash,
                Reason =
                    playerOneDead && playerTwoDead
                        ? "Double KO"
                        : "HP reached zero"
            };

            Log.Info(
                $"Room {RoomId} settled at Frame={finalFrame}. " +
                $"Draw={result.IsDraw}, Winner={result.WinnerPlayerId}, " +
                $"Loser={result.LoserPlayerId}.");
            return true;
        }

        private MatchSettlementData? CloneSettlementLocked()
        {
            if (settlement == null)
            {
                return null;
            }

            return new MatchSettlementData
            {
                RoomId = settlement.RoomId,
                IsDraw = settlement.IsDraw,
                WinnerPlayerId = settlement.WinnerPlayerId,
                LoserPlayerId = settlement.LoserPlayerId,
                WinnerName = settlement.WinnerName,
                LoserName = settlement.LoserName,
                FinalFrame = settlement.FinalFrame,
                WorldHash = settlement.WorldHash,
                Reason = settlement.Reason
            };
        }

        internal static G2C_MatchSettled CreateSettlementMessage(
            MatchSettlementData result)
        {
            G2C_MatchSettled message =
                G2C_MatchSettled.Create();
            message.roomId = result.RoomId;
            message.isDraw = result.IsDraw;
            message.winnerPlayerId = result.WinnerPlayerId;
            message.loserPlayerId = result.LoserPlayerId;
            message.winnerName = result.WinnerName;
            message.loserName = result.LoserName;
            message.finalFrame = result.FinalFrame;
            message.worldHash = result.WorldHash;
            message.reason = result.Reason;
            return message;
        }

        public static bool LeaveMatch(
            Session session,
            string roomId,
            string matchToken)
        {
            if (session == null ||
                session.IsDisposed ||
                string.IsNullOrWhiteSpace(matchToken) ||
                !TryGetRoom(roomId, out LockstepRoomSystem? room) ||
                room == null)
            {
                return false;
            }

            bool closeRoom = room.TryLeaveMatch(session, matchToken);
            if (closeRoom)
            {
                room.Stop();
            }

            return true;
        }

        private bool TryLeaveMatch(
            Session session,
            string matchToken)
        {
            lock (syncRoot)
            {
                if (stopped ||
                    !PlayerProfileSystem.TryGetBySession(
                        session,
                        out PlayerProfileSystem.Profile? profile) ||
                    profile == null)
                {
                    return false;
                }

                byte[] tokenHash = HashReconnectToken(matchToken);
                MatchSeat? seat = null;
                foreach (MatchSeat candidate in matchSeats.Values)
                {
                    if (candidate.ProfileId == profile.ProfileId &&
                        CryptographicOperations.FixedTimeEquals(
                            candidate.MatchTokenHash,
                            tokenHash))
                    {
                        seat = candidate;
                        break;
                    }
                }

                if (seat == null)
                {
                    return false;
                }

                if (playerSlots.TryGetValue(
                        seat.PlayerId,
                        out PlayerSlot? slot))
                {
                    long timerId = slot.OfflineTimerId;
                    slot.OfflineTimerId = 0;
                    slot.OfflineGeneration++;
                    if (timerId != 0)
                    {
                        scene.TimerComponent.Net.Remove(ref timerId);
                    }

                    Session? boundSession = slot.Session;
                    if (boundSession != null)
                    {
                        playerIdsBySessionId.Remove(boundSession.Id);
                        sessionsByPlayerId.Remove(seat.PlayerId);
                        lock (InstanceLock)
                        {
                            RoomIdBySessionId.Remove(boundSession.Id);
                        }
                    }

                    playerSlots.Remove(seat.PlayerId);
                    RemovePlayerInputsLocked(seat.PlayerId);
                    RemovePlayerHashesLocked(seat.PlayerId);
                }

                seat.Session = null;
                seat.Ready = false;
                bool closeRoom = playerSlots.Count == 0;
                Log.Info(
                    $"PlayerId={seat.PlayerId}, ProfileId={seat.ProfileId} " +
                    $"left room {RoomId}.");
                return closeRoom;
            }
        }

        private bool SubmitHash(
            Session session,
            int frameId,
            ulong worldHash)
        {
            lock (syncRoot)
            {
                if (stopped || session.IsDisposed)
                {
                    return false;
                }

                PruneDisconnectedSessionsLocked();
                PruneOldFrameHashesLocked();

                if (!playerIdsBySessionId.TryGetValue(
                        session.Id,
                        out int playerId))
                {
                    Log.Warning(
                        $"Rejected hash from unjoined SessionId={session.Id}, " +
                        $"Frame={frameId}.");
                    return false;
                }

                if ((long)frameId + HashRetentionFrames < currentFrame)
                {
                    Log.Warning(
                        $"Rejected expired frame hash. Frame={frameId}, " +
                        $"CurrentFrame={currentFrame}, " +
                        $"PlayerId={playerId}.");
                    return false;
                }

                if (frameId > currentFrame + MaxFutureFrameLead)
                {
                    Log.Warning(
                        $"Rejected future frame hash. Frame={frameId}, " +
                        $"CurrentFrame={currentFrame}, " +
                        $"PlayerId={playerId}.");
                    return false;
                }

                if (!frameHashes.TryGetValue(
                        frameId,
                        out Dictionary<int, ulong>? playerHashes) ||
                    playerHashes == null)
                {
                    playerHashes = new Dictionary<int, ulong>(
                        expectedPlayerCount);
                    frameHashes.Add(frameId, playerHashes);
                }

                if (playerHashes.TryGetValue(
                        playerId,
                        out ulong existingHash))
                {
                    if (existingHash == worldHash)
                    {
                        return true;
                    }

                    Log.Error(
                        $"Player {playerId} submitted conflicting hashes for " +
                        $"Frame={frameId}: {existingHash} != {worldHash}.");
                    return false;
                }

                playerHashes.Add(playerId, worldHash);
                CompareClientHashToAuthorityLocked(
                    frameId,
                    playerId,
                    worldHash,
                    playerHashes);

                int onlinePlayerCount = 0;
                int comparedPlayerCount = 0;
                foreach (PlayerSlot activeSlot in playerSlots.Values)
                {
                    if (activeSlot.Status != PlayerSlotStatus.Online)
                    {
                        continue;
                    }

                    onlinePlayerCount++;
                    if (playerHashes.ContainsKey(activeSlot.PlayerId))
                    {
                        comparedPlayerCount++;
                    }
                }

                if (onlinePlayerCount < expectedPlayerCount)
                {
                    Log.Debug(
                        $"[HashAuthoritySolo] Frame={frameId}, " +
                        $"PlayerId={playerId}, " +
                        $"Client={worldHash:X16}, " +
                        $"Server=" +
                        $"{(authoritativeFrames.TryGetValue(frameId, out AuthoritativeFrameRecord? soloAuthority) ? soloAuthority.AuthorityHash.ToString("X16") : "MISSING")}.");

                    Log.Debug(
                        $"[HashWaitingPeer] Frame={frameId}, " +
                        $"Online={onlinePlayerCount}, " +
                        $"Received={comparedPlayerCount}.");
                    frameHashes.Remove(frameId);
                    return true;
                }

                if (comparedPlayerCount != onlinePlayerCount)
                {
                    return true;
                }

                if (playerHashes.Count != expectedPlayerCount)
                {
                    return true;
                }

                playerHashes.TryGetValue(1, out ulong player1Hash);
                playerHashes.TryGetValue(2, out ulong player2Hash);
                bool hasAuthority =
                    authoritativeFrames.TryGetValue(
                        frameId,
                        out AuthoritativeFrameRecord? authority);
                ulong serverHash = hasAuthority
                    ? authority!.AuthorityHash
                    : 0UL;

                if (player1Hash != player2Hash ||
                    !hasAuthority ||
                    player1Hash != serverHash ||
                    player2Hash != serverHash)
                {
                    // Error 级别会由 NLog 标记为 [ERROR]（控制台规则中为红色）。
                    // 额外使用横幅和大量感叹号，确保日志文件中也非常醒目。
                    Log.Error(
                        "\n" +
                        "!!!!!!!!!!!!!!!! DESYNC DETECTED !!!!!!!!!!!!!!!!\n" +
                        $"[Desync] Frame={frameId}, " +
                        $"Server={(hasAuthority ? serverHash.ToString("X16") : "MISSING")}, " +
                        $"Player1={player1Hash:X16}, " +
                        $"Player2={player2Hash:X16}\n" +
                        "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                }
                else
                {
                    Log.Debug(
                        $"[HashOK] Frame={frameId}, " +
                        $"Server={serverHash:X16}, " +
                        $"Player1={player1Hash:X16}, " +
                        $"Player2={player2Hash:X16}");
                }

                // 该帧已经完成裁判，移除待比对数据，避免重复告警和内存增长。
                frameHashes.Remove(frameId);
                return true;
            }
        }

        /// <summary>
        /// 服务端裁判的核心入口：每个客户端 Hash 都必须和同帧权威 Hash 比对。
        ///
        /// 两个客户端彼此一致但与服务端不一致，也属于 Desync。旧实现只比较
        /// 两份客户端 Hash，无法发现“双方一起算错”的情况。
        /// </summary>
        private void CompareClientHashToAuthorityLocked(
            int frameId,
            int playerId,
            ulong clientHash,
            Dictionary<int, ulong> knownClientHashes)
        {
            if (!authoritativeFrames.TryGetValue(
                    frameId,
                    out AuthoritativeFrameRecord? authority))
            {
                long missingKey = MakeMismatchKey(frameId, playerId);
                if (reportedAuthorityMismatches.Add(missingKey))
                {
                    Log.Error(
                        $"[HashMissingAuthority] Frame={frameId}, " +
                        $"PlayerId={playerId}, Client={clientHash:X16}. " +
                        $"AuthoritativeFrame={currentFrame}.");
                }

                return;
            }

            if (clientHash == authority.AuthorityHash)
            {
                return;
            }

            long mismatchKey = MakeMismatchKey(frameId, playerId);
            if (!reportedAuthorityMismatches.Add(mismatchKey))
            {
                return;
            }

            string reportPath;
            try
            {
                reportPath = LockstepDesyncDiagnostics.Write(
                    currentFrame,
                    playerId,
                    clientHash,
                    knownClientHashes,
                    authority);
            }
            catch (Exception exception)
            {
                reportPath = $"FAILED: {exception.Message}";
                Log.Error(
                    $"Failed to write lockstep desync report for " +
                    $"Frame={frameId}: {exception}");
            }

            Log.Error(
                "\n" +
                "!!!!!!!!!!!!!! SERVER AUTHORITY DESYNC !!!!!!!!!!!!!!\n" +
                $"[Desync] Frame={frameId}, PlayerId={playerId}\n" +
                $"ClientHash={clientHash:X16}\n" +
                $"ServerHash={authority.AuthorityHash:X16}\n" +
                $"SnapshotHash={authority.StateHash:X16}\n" +
                $"Report={reportPath}\n" +
                "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        }

        private static long MakeMismatchKey(
            int frameId,
            int playerId)
        {
            return ((long)frameId << 32) | (uint)playerId;
        }

        private bool SubmitInput(
            Session session,
            int frameId,
            int inputMask,
            int targetEntityId)
        {
            lock (syncRoot)
            {
                if (stopped || matchFinished || session.IsDisposed)
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
                        out Dictionary<int, LockstepInput>? players) ||
                    players == null)
                {
                    players = new Dictionary<int, LockstepInput>(
                        expectedPlayerCount);
                    frameInputs.Add(frameId, players);
                }

                LockstepInput input = new LockstepInput(
                    inputMask,
                    targetEntityId);

                if (players.TryGetValue(
                        playerId,
                        out LockstepInput existingInput))
                {
                    // KCP 虽然可靠，但应用层去重仍然保留。
                    // Mask 或 TargetId 不同都视为冲突，避免后包覆盖前包。
                    return existingInput.InputMask == inputMask &&
                           existingInput.TargetEntityId == targetEntityId;
                }

                players.Add(playerId, input);

                if (frameId <= 8)
                {
                    Log.Info(
                        $"Accepted lockstep input Frame={frameId}, " +
                        $"PlayerId={playerId}, " +
                        $"InputMask=0x{inputMask:X8}, " +
                        $"TargetEntityId={targetEntityId}.");
                }

                return true;
            }
        }

        private bool RegisterPlayerLocked(
            Session session,
            MatchSeat seat,
            out string reconnectToken)
        {
            reconnectToken = string.Empty;

            if (playerSlots.TryGetValue(
                    seat.PlayerId,
                    out PlayerSlot? existingSlot))
            {
                return existingSlot.Status == PlayerSlotStatus.Online &&
                       existingSlot.Session != null &&
                       existingSlot.Session.Id == session.Id;
            }

            if (playerIdsBySessionId.TryGetValue(
                    session.Id,
                    out int existingPlayerId))
            {
                return existingPlayerId == seat.PlayerId;
            }

            reconnectToken = GenerateReconnectToken();
            PlayerSlot slot = new PlayerSlot
            {
                PlayerId = seat.PlayerId,
                ProfileId = seat.ProfileId,
                DisplayName = seat.DisplayName,
                Session = session,
                Status = PlayerSlotStatus.Online,
                ReconnectTokenHash =
                    HashReconnectToken(reconnectToken)
            };

            playerSlots.Add(seat.PlayerId, slot);
            sessionsByPlayerId.Add(seat.PlayerId, session);
            playerIdsBySessionId.Add(session.Id, seat.PlayerId);
            lock (InstanceLock)
            {
                RoomIdBySessionId[session.Id] = RoomId;
            }

            return true;
        }

        private static string RotateReconnectTokenLocked(
            PlayerSlot slot)
        {
            string token = GenerateReconnectToken();
            slot.ReconnectTokenHash = HashReconnectToken(token);
            return token;
        }

        private static string GenerateReconnectToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] HashReconnectToken(string token)
        {
            return SHA256.HashData(
                Encoding.UTF8.GetBytes(token));
        }

        private void ResetForNewMatchLocked()
        {
            if (currentFrame == 1 && frameInputs.Count == 0)
            {
                return;
            }

            Log.Info(
                $"Lockstep room {RoomId} starts a new match. " +
                $"Reset current frame from {currentFrame} to 1.");

            currentFrame = 1;
            frameInputs.Clear();
            frameHashes.Clear();
            authoritativeFrames.Clear();
            reportedAuthorityMismatches.Clear();
            lastCommittedInputs.Clear();
            combat.Reset();
        }

        private void PruneDisconnectedSessionsLocked()
        {
            if (playerSlots.Count == 0)
            {
                return;
            }

            foreach (PlayerSlot slot in playerSlots.Values)
            {
                if (slot.Status != PlayerSlotStatus.Online ||
                    slot.Session == null ||
                    slot.Session.IsDisposed)
                {
                    MarkPlayerOfflineLocked(slot);
                }
            }
        }

        private void MarkPlayerOfflineLocked(PlayerSlot slot)
        {
            if (slot.Status == PlayerSlotStatus.Offline)
            {
                return;
            }

            Session? oldSession = slot.Session;
            if (oldSession != null)
            {
                playerIdsBySessionId.Remove(oldSession.Id);
                lock (InstanceLock)
                {
                    RoomIdBySessionId.Remove(oldSession.Id);
                }
            }

            sessionsByPlayerId.Remove(slot.PlayerId);
            slot.Session = null;
            slot.Status = PlayerSlotStatus.Offline;
            slot.OfflineGeneration++;

            long generation = slot.OfflineGeneration;
            long timerId = scene.TimerComponent.Net.OnceTimer(
                OfflineProtectionMilliseconds,
                () => ExpireOfflinePlayer(slot.PlayerId, generation));
            slot.OfflineTimerId = timerId;

            RemovePlayerInputsLocked(slot.PlayerId);
            RemovePlayerHashesLocked(slot.PlayerId);

            Log.Info(
                $"PlayerId={slot.PlayerId} entered offline protection " +
                $"for {OfflineProtectionMilliseconds} ms in room {RoomId}.");
        }

        private void ExpireOfflinePlayer(
            int playerId,
            long expectedGeneration)
        {
            bool expired = false;
            List<Session>? sessionsToNotify = null;

            lock (syncRoot)
            {
                if (stopped ||
                    !playerSlots.TryGetValue(
                        playerId,
                        out PlayerSlot? slot) ||
                    slot.Status != PlayerSlotStatus.Offline ||
                    slot.OfflineGeneration != expectedGeneration)
                {
                    return;
                }

                expired = true;
                sessionsToNotify = sessionsByPlayerId.Values
                    .Where(session => !session.IsDisposed)
                    .ToList();
                lock (InstanceLock)
                {
                    foreach (Session onlineSession in sessionsToNotify)
                    {
                        RoomIdBySessionId.Remove(onlineSession.Id);
                    }
                }
                playerSlots.Clear();
                sessionsByPlayerId.Clear();
                playerIdsBySessionId.Clear();
                frameInputs.Clear();
                frameHashes.Clear();
                authoritativeFrames.Clear();
                reportedAuthorityMismatches.Clear();
                lastCommittedInputs.Clear();
                combat.Reset();
            }

            if (expired)
            {
                if (sessionsToNotify != null)
                {
                    for (int i = 0; i < sessionsToNotify.Count; i++)
                    {
                        Session session = sessionsToNotify[i];
                        G2C_LockstepClosed closed =
                            G2C_LockstepClosed.Create();
                        closed.reason = 1;
                        closed.message =
                            "Offline protection expired; the match was closed.";
                        session.Send(closed);
                    }
                }

                Log.Warning(
                    $"Offline protection expired for PlayerId={playerId}. " +
                    $"Room {RoomId} has been closed.");

                // 多房间模式下，过期房间不能继续留在 Rooms 字典里，
                // 否则下一次匹配虽然使用新 roomId，旧房间仍会永久驻留。
                Stop();
            }
        }

        private void RemovePlayerInputsLocked(int playerId)
        {
            List<int>? emptyFrames = null;

            foreach (KeyValuePair<
                         int,
                         Dictionary<int, LockstepInput>> framePair
                     in frameInputs)
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

        private void RemovePlayerHashesLocked(int playerId)
        {
            List<int>? emptyFrames = null;

            foreach (KeyValuePair<
                         int,
                         Dictionary<int, ulong>> framePair in frameHashes)
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
                frameHashes.Remove(emptyFrames[i]);
            }
        }

        private void PruneOldFrameHashesLocked()
        {
            int oldestRetainedFrame =
                currentFrame - HashRetentionFrames;
            if (oldestRetainedFrame <= 0 ||
                (frameHashes.Count == 0 &&
                 authoritativeFrames.Count == 0))
            {
                return;
            }

            List<int>? expiredFrames = null;

            foreach (int frameId in frameHashes.Keys)
            {
                if (frameId < oldestRetainedFrame)
                {
                    expiredFrames ??= new List<int>();
                    expiredFrames.Add(frameId);
                }
            }

            foreach (int frameId in authoritativeFrames.Keys)
            {
                if (frameId >= oldestRetainedFrame)
                {
                    continue;
                }

                if (expiredFrames == null ||
                    !expiredFrames.Contains(frameId))
                {
                    expiredFrames ??= new List<int>();
                    expiredFrames.Add(frameId);
                }
            }

            if (expiredFrames == null)
            {
                return;
            }

            for (int i = 0; i < expiredFrames.Count; i++)
            {
                frameHashes.Remove(expiredFrames[i]);
                authoritativeFrames.Remove(expiredFrames[i]);
            }

            reportedAuthorityMismatches.RemoveWhere(
                key => (int)(key >> 32) < oldestRetainedFrame);
        }

        private bool TryTakeCompleteCurrentFrameLocked(
            out int frameId,
            out List<KeyValuePair<int, LockstepInput>>? orderedInputs)
        {
            frameId = currentFrame;
            orderedInputs = null;

            if (playerSlots.Count != expectedPlayerCount)
            {
                return false;
            }

            frameInputs.TryGetValue(
                currentFrame,
                out Dictionary<int, LockstepInput>? submittedPlayers);

            orderedInputs =
                new List<KeyValuePair<int, LockstepInput>>(
                    expectedPlayerCount);

            foreach (PlayerSlot slot in playerSlots.Values)
            {
                if (slot.Status == PlayerSlotStatus.Offline)
                {
                    // 服务端权威合成 None。客户端断线后不可能继续提供输入，
                    // 因此房间仍然按 20Hz 推进，其余玩家不会被冻结 30 秒。
                    orderedInputs.Add(new KeyValuePair<int, LockstepInput>(
                        slot.PlayerId,
                        new LockstepInput(0, 0)));
                    continue;
                }

                if (submittedPlayers == null ||
                    !submittedPlayers.TryGetValue(
                        slot.PlayerId,
                        out LockstepInput submitted))
                {
                    orderedInputs = null;
                    return false;
                }

                orderedInputs.Add(
                    new KeyValuePair<int, LockstepInput>(
                        slot.PlayerId,
                        submitted));
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
                    request.roomId,
                    request.matchToken,
                    out LockstepRoomSystem.JoinCredentials credentials))
            {
                Log.Warning(
                    $"Lockstep room join rejected. " +
                    $"SessionId={session.Id}, " +
                    $"RoomId={request.roomId}.");
                G2C_LockstepClosed rejected =
                    G2C_LockstepClosed.Create();
                rejected.reason = 3;
                rejected.message = "Match room join was rejected.";
                session.Send(rejected);
                return;
            }

            G2C_PlayerAssigned response =
                G2C_PlayerAssigned.Create();
            response.playerId = credentials.PlayerId;
            response.roomId = credentials.RoomId;
            response.reconnectToken = credentials.ReconnectToken;
            session.Send(response);

            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// 断线客户端凭旧身份和 Token 重新绑定新 Session。
    ///
    /// 两条消息必须按顺序发送：
    /// 1. 新 Token，供下一次断线继续使用；
    /// 2. 权威世界快照。
    /// 客户端在快照应用前保持逻辑暂停。
    /// </summary>
    public sealed class C2G_ReconnectLockstepHandler
        : Message<C2G_ReconnectLockstep>
    {
        protected override async FTask Run(
            Session session,
            C2G_ReconnectLockstep request)
        {
            if (!LockstepRoomSystem.TryReconnect(
                    session,
                    request.roomId,
                    request.playerId,
                    request.reconnectToken,
                    out LockstepRoomSystem.ReconnectSuccess? success) ||
                success == null)
            {
                Log.Warning(
                    $"Lockstep reconnect rejected. " +
                    $"SessionId={session.Id}, " +
                    $"PlayerId={request.playerId}, " +
                    $"RoomId={request.roomId}.");
                G2C_LockstepClosed rejected =
                    G2C_LockstepClosed.Create();
                rejected.reason = 2;
                rejected.message =
                    "Reconnect identity is no longer valid.";
                session.Send(rejected);
                return;
            }

            G2C_PlayerAssigned assigned =
                G2C_PlayerAssigned.Create();
            assigned.playerId = success.PlayerId;
            assigned.roomId = request.roomId;
            assigned.reconnectToken = success.ReconnectToken;
            session.Send(assigned);
            session.Send(success.State);
            if (success.Settlement != null)
            {
                session.Send(
                    LockstepRoomSystem.CreateSettlementMessage(
                        success.Settlement));
            }

            await FTask.CompletedTask;
        }
    }

    public sealed class C2G_MatchReadyHandler
        : Message<C2G_MatchReady>
    {
        protected override async FTask Run(
            Session session,
            C2G_MatchReady message)
        {
            if (!LockstepRoomSystem.MarkReady(
                    session,
                    message.roomId,
                    message.matchToken))
            {
                Log.Warning(
                    $"Match ready rejected. SessionId={session.Id}, " +
                    $"RoomId={message.roomId}.");
            }

            await FTask.CompletedTask;
        }
    }

    public sealed class C2G_LeaveMatchHandler
        : Message<C2G_LeaveMatch>
    {
        protected override async FTask Run(
            Session session,
            C2G_LeaveMatch message)
        {
            LockstepRoomSystem.LeaveMatch(
                session,
                message.roomId,
                message.matchToken);

            G2C_MatchmakingStatus status =
                G2C_MatchmakingStatus.Create();
            status.state = MatchmakingSystem.StatusIdle;
            status.queueSize = 0;
            status.message = "已离开战斗，可重新匹配。";
            if (!session.IsDisposed)
            {
                session.Send(status);
            }

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
            bool accepted = LockstepRoomSystem.SubmitFrameInput(
                    session,
                    message.frameId,
                    message.inputMask,
                    message.targetEntityId);

            // 结算帧广播后，客户端可能还有 1~2 个预测输入已经在网络上。
            // 房间已结算时静默丢弃这些包，不再输出误导性的 Warn。
            if (!accepted &&
                !LockstepRoomSystem.IsSessionMatchFinished(session))
            {
                Log.Warning(
                    $"Invalid lockstep input rejected. " +
                    $"SessionId={session.Id}, " +
                    $"Frame={message.frameId}.");
            }

            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// 接收客户端确认帧的 WorldHash，并交由房间裁判比对。
    /// </summary>
    public sealed class C2G_FrameHashHandler
        : Message<C2G_FrameHash>
    {
        protected override async FTask Run(
            Session session,
            C2G_FrameHash message)
        {
            if (!LockstepRoomSystem.SubmitFrameHash(
                    session,
                    message.frameId,
                    message.worldHash))
            {
                Log.Warning(
                    $"Invalid frame hash rejected. " +
                    $"SessionId={session.Id}, " +
                    $"Frame={message.frameId}, " +
                    $"Hash={message.worldHash}.");
            }

            await FTask.CompletedTask;
        }
    }
}

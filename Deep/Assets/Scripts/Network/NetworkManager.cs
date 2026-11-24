using System;
using System.Collections;
using Combat.Core;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Unity 与 Fantasy Gate 之间的网络入口。
    ///
    /// 协议代码由服务端 OuterMessage.proto 导出到 Assets/Scripts/NetworkProtocol。
    /// 这个类只负责连接生命周期、心跳、发送/RPC 和断线通知，不处理战斗逻辑。
    /// </summary>
    public sealed class NetworkManager : MonoBehaviour, ILockstepNetwork
    {
        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected
        }

        public static NetworkManager Instance { get; private set; }

        [Header("连接")]
        [SerializeField] private bool autoConnect = true;
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField, Min(1)] private int port = 20000;
        [SerializeField, Min(500)] private int connectTimeout = 5000;
        [SerializeField] private bool persistentBetweenScenes = true;
        [SerializeField] private bool enableReceiveMessageJsonLog;

        [Header("断线重连")]
        [SerializeField, Min(0.5f)] private float reconnectDelay = 1f;

        [Header("心跳")]
        [SerializeField] private bool enableHeartbeat = true;
        [SerializeField, Min(500)] private int heartbeatInterval = 2000;
        [SerializeField, Min(1000)] private int heartbeatTimeout = 30000;
        [SerializeField, Min(500)] private int heartbeatCheckInterval = 5000;
        [SerializeField, Min(1)] private int maxPingSamples = 4;

        private Session session;
        private bool intentionallyDisconnected;
        private bool matchIdentityReady;
        private bool battleInputEnabled;
        private bool reconnectAttempt;
        private int lastReceivedFrame;
        private Coroutine reconnectCoroutine;

        public event Action Connected;
        public event Action<string> ConnectFailed;
        public event Action Disconnected;
        public event Action<string> MatchClosed;
        public event Action<ProfileRegisteredData> OnProfileRegistered;
        public event Action<MatchmakingStatusData> OnMatchmakingStatus;
        public event Action<MatchFoundData> OnMatchFound;
        public event Action<BattleStartData> OnBattleStarted;
        public event Action<MatchSettledData> OnMatchSettled;

        /// <summary>
        /// 服务端分配锁步 PlayerId 后触发。
        /// 参数已经是复制出的 int，不依赖任何 Fantasy 池化消息生命周期。
        /// </summary>
        public event Action<int> OnPlayerIdAssigned;

        /// <summary>
        /// 收到服务端广播的完整锁步帧时触发。
        ///
        /// 注意：这里传递的是 Fantasy 池化的 G2C_FrameData 消息对象。
        /// 事件回调返回后，框架会回收该对象。如果需要跨帧保存，请立即复制
        /// frameId、inputs 和其中每个 PlayerInputData，不要长期保存引用。
        /// </summary>
        public event Action<G2C_FrameData> OnFrameDataReceived;

        /// <summary>
        /// 收到服务端权威重连快照。传入对象已经是深拷贝，
        /// 不依赖 Fantasy G2C_ReconnectState 的生命周期。
        /// </summary>
        public event Func<LockstepReconnectStateData, bool>
            OnReconnectStateReceived;

        public ConnectionState State { get; private set; } =
            ConnectionState.Disconnected;

        public Session Session => session;

        /// <summary>
        /// 服务端分配给当前连接的锁步玩家 ID；未分配时为 0。
        /// </summary>
        public int LocalPlayerId { get; private set; }
        public string RoomId { get; private set; } = string.Empty;
        public string ReconnectToken { get; private set; } = string.Empty;
        public string MatchToken { get; private set; } = string.Empty;
        public int MatchPlayerId { get; private set; }
        public long ProfileId { get; private set; }
        public string DisplayName { get; private set; } = string.Empty;
        public string OpponentDisplayName { get; private set; } = string.Empty;
        public bool IsTransportConnected =>
            State == ConnectionState.Connected &&
            session != null &&
            !session.IsDisposed;
        public bool HasReconnectIdentity =>
            LocalPlayerId > 0 &&
            !string.IsNullOrEmpty(RoomId) &&
            !string.IsNullOrEmpty(ReconnectToken);

        public bool IsConnected =>
            IsTransportConnected &&
            matchIdentityReady &&
            battleInputEnabled;

        public int PingMilliseconds
        {
            get
            {
                if (!enableHeartbeat || !IsConnected)
                {
                    return -1;
                }

                try
                {
                    return Fantasy.Runtime.PingMilliseconds;
                }
                catch
                {
                    return -1;
                }
            }
        }

        private void Awake()
        {
            // 联机测试通常会同时打开多个客户端窗口。
            // Unity 默认在失去焦点后暂停 Player Loop，这会导致旧客户端停止
            // Fantasy 心跳和 KCP Update，最终被服务端判定为 Session 超时。
            Application.runInBackground = true;

            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (persistentBetweenScenes)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            if (autoConnect)
            {
                Connect();
            }
        }

        public void Connect()
        {
            if (State == ConnectionState.Connecting ||
                State == ConnectionState.Connected)
            {
                return;
            }

            intentionallyDisconnected = false;
            CancelReconnectRoutine();
            ConnectAsync().Coroutine();
        }

        private async FTask ConnectAsync()
        {
            State = ConnectionState.Connecting;

            try
            {
                session = await Fantasy.Runtime.Connect(
                    host,
                    port,
                    Fantasy.FantasyRuntime.NetworkProtocolType.KCP,
                    false,
                    connectTimeout,
                    enableHeartbeat,
                    heartbeatInterval,
                    heartbeatTimeout,
                    heartbeatCheckInterval,
                    maxPingSamples,
                    HandleConnected,
                    HandleConnectFailed,
                    HandleDisconnected,
                    enableReceiveMessageJsonLog);
            }
            catch (Exception exception)
            {
                State = ConnectionState.Disconnected;
                session = null;
                matchIdentityReady = false;
                ConnectFailed?.Invoke(exception.Message);
                Debug.LogError(
                    $"[Network] KCP 连接初始化失败：" +
                    $"{host}:{port}\n{exception}");
                ScheduleReconnect();
            }
        }

        private void HandleConnected()
        {
            session = Fantasy.Runtime.Session;
            State = ConnectionState.Connected;
            matchIdentityReady = false;
            Debug.Log($"[Network] Fantasy KCP 已连接：{host}:{port}");

            reconnectAttempt = HasReconnectIdentity;
            if (reconnectAttempt)
            {
                // 旧 Session 已经销毁，不能复用它。这里只携带旧逻辑身份，
                // 由服务端验证 Token 后把新 Session 绑定到原席位。
                C2G_ReconnectLockstep request =
                    C2G_ReconnectLockstep.Create();
                request.roomId = RoomId;
                request.playerId = LocalPlayerId;
                request.reconnectToken = ReconnectToken;
                request.lastReceivedFrame = lastReceivedFrame;
                session.Send(request);
                Debug.Log(
                    $"[Network] 正在以 PlayerId={LocalPlayerId} 请求重连房间 " +
                    $"{RoomId}。");
            }

            Connected?.Invoke();
        }

        private void HandleConnectFailed()
        {
            State = ConnectionState.Disconnected;
            session = null;
            matchIdentityReady = false;
            const string message = "无法连接到 Fantasy Gate。";
            Debug.LogError($"[Network] {message} {host}:{port}");
            ConnectFailed?.Invoke(message);
            ScheduleReconnect();
        }

        private void HandleDisconnected()
        {
            State = ConnectionState.Disconnected;
            session = null;
            matchIdentityReady = false;
            battleInputEnabled = false;
            reconnectAttempt = false;

            if (intentionallyDisconnected)
            {
                Debug.Log("[Network] Fantasy 会话已主动断开。");
                return;
            }

            // 关键：不清空 PlayerId、RoomId、Token。
            // Session 已死，但逻辑席位仍在服务端保护期内，下一次连接会重新绑定。
            Debug.LogWarning(
                $"[Network] Fantasy 会话意外断开。" +
                $"{(HasReconnectIdentity ? "将自动重连。" : string.Empty)}");
            Disconnected?.Invoke();
            ScheduleReconnect();
        }

        public void Disconnect()
        {
            intentionallyDisconnected = true;
            CancelReconnectRoutine();
            State = ConnectionState.Disconnected;
            LocalPlayerId = 0;
            RoomId = string.Empty;
            ReconnectToken = string.Empty;
            MatchToken = string.Empty;
            MatchPlayerId = 0;
            ProfileId = 0;
            DisplayName = string.Empty;
            OpponentDisplayName = string.Empty;
            matchIdentityReady = false;
            battleInputEnabled = false;
            reconnectAttempt = false;
            lastReceivedFrame = 0;

            if (session != null && !session.IsDisposed)
            {
                session.Dispose();
            }

            session = null;
        }

        /// <summary>
        /// 仅用于本地断线重连测试：释放当前 KCP Session，但保留
        /// PlayerId、RoomId 和 Token，让自动重连流程真正执行。
        /// 正式退出游戏仍应调用 Disconnect()，它会主动清除身份。
        /// </summary>
        [ContextMenu("测试：模拟网络意外断开")]
        public void SimulateUnexpectedDisconnectForTesting()
        {
            intentionallyDisconnected = false;
            if (session != null && !session.IsDisposed)
            {
                session.Dispose();
                return;
            }

            HandleDisconnected();
        }

        /// <summary>
        /// 发送服务器主动消息。消息生命周期由 Fantasy 管理。
        /// </summary>
        public bool Send<T>(T message) where T : IMessage
        {
            if (!IsTransportConnected)
            {
                message?.Dispose();
                return false;
            }

            session.Send(message);
            return true;
        }

        public bool RegisterProfile(
            string displayName,
            string profileToken)
        {
            C2G_RegisterProfile message =
                C2G_RegisterProfile.Create();
            message.displayName = displayName ?? string.Empty;
            message.clientProfileToken =
                profileToken ?? string.Empty;
            return Send(message);
        }

        public bool StartMatchmaking()
        {
            return Send(C2G_StartMatchmaking.Create());
        }

        public bool CancelMatchmaking()
        {
            return Send(C2G_CancelMatchmaking.Create());
        }

        public bool SendMatchReady()
        {
            C2G_MatchReady message = C2G_MatchReady.Create();
            message.roomId = RoomId;
            message.matchToken = MatchToken;
            return Send(message);
        }

        public bool JoinLockstepMatch()
        {
            C2G_JoinLockstep message = C2G_JoinLockstep.Create();
            message.roomId = RoomId;
            message.matchToken = MatchToken;
            return Send(message);
        }

        public bool SendLeaveMatch()
        {
            C2G_LeaveMatch message = C2G_LeaveMatch.Create();
            message.roomId = RoomId;
            message.matchToken = MatchToken;
            return Send(message);
        }

        /// <summary>
        /// 结算后清理房间身份，但保留 KCP 连接和玩家资料，供下一轮匹配使用。
        /// </summary>
        public void ClearMatchIdentity()
        {
            LocalPlayerId = 0;
            RoomId = string.Empty;
            ReconnectToken = string.Empty;
            MatchToken = string.Empty;
            MatchPlayerId = 0;
            OpponentDisplayName = string.Empty;
            matchIdentityReady = false;
            battleInputEnabled = false;
            reconnectAttempt = false;
            lastReceivedFrame = 0;
        }

        /// <summary>
        /// 向服务端提交一个逻辑帧的输入。
        ///
        /// frameId 必须使用服务端当前等待的锁定帧号；inputMask 由客户端输入层生成。
        /// 消息创建后交给 Fantasy 的发送通道管理，发送完成或发送失败时会自动回收。
        /// </summary>
        public void SendFrameInput(
            int frameId,
            int inputMask,
            int targetEntityId = 0)
        {
            if (frameId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameId),
                    "Lockstep frameId must be greater than zero.");
            }

            C2G_SubmitFrameInput message =
                C2G_SubmitFrameInput.Create();
            message.frameId = frameId;
            message.inputMask = inputMask;
            message.targetEntityId = targetEntityId;
            Send(message);
        }

        /// <summary>
        /// LockstepManager 使用的无 Unity 类型依赖发送入口。
        /// 返回 false 时不会推进客户端的待发送帧号，网络恢复后会从同一帧重试。
        /// </summary>
        public bool TrySendFrameInput(
            int frameId,
            FrameInputMask inputMask,
            int targetEntityId)
        {
            if (frameId <= 0 || !IsConnected)
            {
                return false;
            }

            C2G_SubmitFrameInput message =
                C2G_SubmitFrameInput.Create();
            message.frameId = frameId;
            message.inputMask = (int)(uint)inputMask;
            message.targetEntityId = targetEntityId;

            // Send 内部会再次检查连接状态；如果恰好在这期间断线，
            // 它会 Dispose 消息并返回 false，LockstepManager 会保留该帧号重试。
            return Send(message);
        }

        /// <summary>
        /// 向服务端上报某个已执行确认帧的 WorldHash。
        /// 返回 false 表示消息没有进入发送通道，不影响本地逻辑继续执行。
        /// </summary>
        public bool TrySendFrameHash(int frameId, ulong worldHash)
        {
            if (frameId <= 0 || !IsConnected)
            {
                return false;
            }

            C2G_FrameHash message = C2G_FrameHash.Create();
            message.frameId = frameId;
            message.worldHash = worldHash;
            return Send(message);
        }

        /// <summary>
        /// 发起 RPC 请求。
        /// </summary>
        public FTask<IResponse> Call<T>(T request) where T : IRequest
        {
            if (!IsTransportConnected)
            {
                request.Dispose();
                throw new InvalidOperationException(
                    "Fantasy session is not connected.");
            }

            return session.Call(request);
        }

        private void OnDestroy()
        {
            if (Instance != this)
            {
                return;
            }

            Disconnect();
            OnFrameDataReceived = null;
            OnPlayerIdAssigned = null;
            OnReconnectStateReceived = null;
            OnProfileRegistered = null;
            OnMatchmakingStatus = null;
            OnMatchFound = null;
            OnBattleStarted = null;
            OnMatchSettled = null;
            MatchClosed = null;
            Instance = null;
        }

        /// <summary>
        /// 供外网消息 Handler 把帧数据交给当前 NetworkManager。
        /// Fantasy 消息处理结束后会回收到消息，所以事件必须同步触发。
        /// </summary>
        internal static void DispatchFrameData(G2C_FrameData frameData)
        {
            NetworkManager manager = Instance;
            if (manager == null)
            {
                return;
            }

            Action<G2C_FrameData> handlers =
                manager.OnFrameDataReceived;
            if (frameData.frameId > manager.lastReceivedFrame)
            {
                manager.lastReceivedFrame = frameData.frameId;
            }

            if (handlers == null)
            {
                return;
            }

            // 逐个执行订阅者，避免某一个表现层异常导致后续订阅者收不到本帧。
            Delegate[] callbacks = handlers.GetInvocationList();
            for (int i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    ((Action<G2C_FrameData>)callbacks[i])(frameData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// 接收服务端分配的 PlayerId，并通知所有订阅者。
        /// </summary>
        internal static void DispatchPlayerAssigned(
            G2C_PlayerAssigned message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            if (message.playerId <= 0)
            {
                Debug.LogError(
                    $"[Network] 服务端下发了非法 PlayerId：" +
                    $"{message.playerId}");
                return;
            }

            manager.LocalPlayerId = message.playerId;
            manager.RoomId = message.roomId ?? string.Empty;
            manager.ReconnectToken =
                message.reconnectToken ?? string.Empty;

            // 首次分配时身份和世界都从零开始；重连时先确认身份，
            // 但必须等 G2C_ReconnectState 恢复世界后 IsConnected 才能恢复为 true。
            manager.matchIdentityReady = !manager.reconnectAttempt;
            manager.battleInputEnabled =
                !manager.reconnectAttempt;
            Debug.Log(
                $"[Network] 服务端席位确认：PlayerId={message.playerId}, " +
                $"Room={manager.RoomId}, " +
                $"Reconnect={manager.reconnectAttempt}。");
            manager.OnPlayerIdAssigned?.Invoke(message.playerId);
        }

        /// <summary>
        /// 深拷贝服务端重连快照并通知逻辑层。
        ///
        /// PlayerAssigned 先确认身份，ReconnectState 后确认世界。
        /// 只有快照事件执行完毕后 IsConnected 才恢复为 true，
        /// 防止 LockstepManager 在旧世界状态上抢先预测一帧。
        /// </summary>
        internal static void DispatchReconnectState(
            G2C_ReconnectState message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            LockstepReconnectStateData copied =
                CopyReconnectState(message);
            Func<LockstepReconnectStateData, bool> handlers =
                manager.OnReconnectStateReceived;
            bool restored = handlers != null;

            if (handlers != null)
            {
                Delegate[] callbacks = handlers.GetInvocationList();
                for (int i = 0; i < callbacks.Length; i++)
                {
                    try
                    {
                        restored &=
                            ((Func<LockstepReconnectStateData, bool>)callbacks[i])(
                                copied);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        restored = false;
                    }
                }
            }

            manager.lastReceivedFrame = Math.Max(
                manager.lastReceivedFrame,
                copied.ConfirmedFrame);
            manager.RoomId = copied.RoomId ?? string.Empty;

            if (restored)
            {
                manager.reconnectAttempt = false;
                manager.matchIdentityReady = true;
                manager.battleInputEnabled = true;
                manager.CancelReconnectRoutine();
            }
            else
            {
                Debug.LogError(
                    "[Network] Reconnect snapshot was not applied by the " +
                    "combat layer; lockstep remains paused.");
            }
        }

        internal static void DispatchProfileRegistered(
            G2C_ProfileRegistered message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            ProfileRegisteredData copied =
                new ProfileRegisteredData
                {
                    Success = message.success,
                    Message = message.message ?? string.Empty,
                    ProfileId = message.profileId,
                    DisplayName = message.displayName ?? string.Empty,
                    ProfileToken = message.profileToken ?? string.Empty
                };

            if (copied.Success)
            {
                manager.ProfileId = copied.ProfileId;
                manager.DisplayName = copied.DisplayName;
            }

            manager.OnProfileRegistered?.Invoke(copied);
        }

        internal static void DispatchMatchmakingStatus(
            G2C_MatchmakingStatus message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            manager.OnMatchmakingStatus?.Invoke(
                new MatchmakingStatusData
                {
                    State = message.state,
                    QueueSize = message.queueSize,
                    Message = message.message ?? string.Empty
                });
        }

        internal static void DispatchMatchFound(
            G2C_MatchFound message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            MatchFoundData copied = new MatchFoundData
            {
                RoomId = message.roomId ?? string.Empty,
                PlayerId = message.playerId,
                ProfileId = message.profileId,
                MatchToken = message.matchToken ?? string.Empty,
                LocalDisplayName =
                    message.localDisplayName ?? string.Empty,
                OpponentDisplayName =
                    message.opponentDisplayName ?? string.Empty
            };

            manager.RoomId = copied.RoomId;
            manager.MatchToken = copied.MatchToken;
            manager.MatchPlayerId = copied.PlayerId;
            manager.ProfileId = copied.ProfileId;
            manager.DisplayName = copied.LocalDisplayName;
            manager.OpponentDisplayName =
                copied.OpponentDisplayName;
            manager.matchIdentityReady = false;
            manager.battleInputEnabled = false;
            manager.OnMatchFound?.Invoke(copied);
        }

        internal static void DispatchBattleStart(
            G2C_BattleStart message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            manager.MatchPlayerId = message.playerId;
            manager.battleInputEnabled = false;
            manager.OnBattleStarted?.Invoke(
                new BattleStartData
                {
                    RoomId = message.roomId ?? string.Empty,
                    PlayerId = message.playerId,
                    RulesetVersion =
                        message.rulesetVersion ?? string.Empty
                });
        }

        internal static void DispatchMatchSettled(
            G2C_MatchSettled message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            manager.battleInputEnabled = false;
            manager.OnMatchSettled?.Invoke(
                new MatchSettledData
                {
                    RoomId = message.roomId ?? string.Empty,
                    IsDraw = message.isDraw,
                    WinnerPlayerId = message.winnerPlayerId,
                    LoserPlayerId = message.loserPlayerId,
                    WinnerName = message.winnerName ?? string.Empty,
                    LoserName = message.loserName ?? string.Empty,
                    FinalFrame = message.finalFrame,
                    WorldHash = message.worldHash,
                    Reason = message.reason ?? string.Empty
                });
        }

        /// <summary>
        /// 离线保护超时，服务端已经清理旧对局。
        /// 此时旧 PlayerId 和 Token 必须作废，否则客户端会无限重试一个
        /// 已不存在的逻辑席位。
        /// </summary>
        internal static void DispatchLockstepClosed(
            G2C_LockstepClosed message)
        {
            NetworkManager manager = Instance;
            if (manager == null || message == null)
            {
                return;
            }

            string reason = string.IsNullOrWhiteSpace(message.message)
                ? "The lockstep match was closed."
                : message.message;
            Debug.LogWarning($"[Network] 对局已关闭：{reason}");

            manager.intentionallyDisconnected = true;
            manager.CancelReconnectRoutine();
            manager.State = ConnectionState.Disconnected;
            manager.ClearMatchIdentity();
            manager.ProfileId = 0;
            manager.DisplayName = string.Empty;

            if (manager.session != null &&
                !manager.session.IsDisposed)
            {
                manager.session.Dispose();
            }

            manager.session = null;
            manager.MatchClosed?.Invoke(reason);
        }

        private static LockstepReconnectStateData CopyReconnectState(
            G2C_ReconnectState message)
        {
            ReconnectPlayerSnapshotData[] players =
                new ReconnectPlayerSnapshotData[
                    message.players?.Count ?? 0];

            for (int i = 0; i < players.Length; i++)
            {
                ReconnectPlayerState source = message.players[i];
                players[i] = new ReconnectPlayerSnapshotData
                {
                    EntityId = source.entityId,
                    PreferredTargetId = source.preferredTargetId,
                    PositionXRaw = source.positionXRaw,
                    PositionYRaw = source.positionYRaw,
                    PositionZRaw = source.positionZRaw,
                    PreviousPositionXRaw =
                        source.previousPositionXRaw,
                    PreviousPositionYRaw =
                        source.previousPositionYRaw,
                    PreviousPositionZRaw =
                        source.previousPositionZRaw,
                    RotationYawRaw = source.rotationYawRaw,
                    PreviousRotationYawRaw =
                        source.previousRotationYawRaw,
                    CurrentHpRaw = source.currentHpRaw,
                    AttackState = source.attackState,
                    AttackPhase = source.attackPhase,
                    AttackFrame = source.attackFrame,
                    KnockbackVelocityXRaw =
                        source.knockbackVelocityXRaw,
                    KnockbackVelocityYRaw =
                        source.knockbackVelocityYRaw,
                    KnockbackVelocityZRaw =
                        source.knockbackVelocityZRaw,
                    IsDead = source.isDead,
                    IsHurt = source.isHurt,
                    IsMoving = source.isMoving,
                    BufferedAttack = source.bufferedAttack,
                    UltimateInputBuffered =
                        source.ultimateInputBuffered,
                    ComboHitCount = source.comboHitCount,
                    ComboCounterWindowRemaining =
                        source.comboCounterWindowRemaining,
                    HurtTimerRaw = source.hurtTimerRaw,
                    UltimateStartXRaw = source.ultimateStartXRaw,
                    UltimateStartYRaw = source.ultimateStartYRaw,
                    UltimateStartZRaw = source.ultimateStartZRaw,
                    UltimateFurthestXRaw =
                        source.ultimateFurthestXRaw,
                    UltimateFurthestYRaw =
                        source.ultimateFurthestYRaw,
                    UltimateFurthestZRaw =
                        source.ultimateFurthestZRaw,
                    UltimateFurthestDistanceSqrRaw =
                        source.ultimateFurthestDistanceSqrRaw,
                    CurrentFrame = source.currentFrame,
                    HitTargetsThisAttack =
                        source.hitTargetsThisAttack == null
                            ? Array.Empty<int>()
                            : source.hitTargetsThisAttack.ToArray()
                };
            }

            ReconnectInputData[] lastInputs =
                new ReconnectInputData[
                    message.lastInputs?.Count ?? 0];

            for (int i = 0; i < lastInputs.Length; i++)
            {
                PlayerInputData source = message.lastInputs[i];
                lastInputs[i] = new ReconnectInputData
                {
                    PlayerId = source.playerId,
                    InputMask = source.inputMask,
                    TargetEntityId = source.targetEntityId
                };
            }

            return new LockstepReconnectStateData
            {
                RoomId = message.roomId ?? string.Empty,
                ConfirmedFrame = message.confirmedFrame,
                NextFrame = message.nextFrame,
                WorldHash = message.worldHash,
                RulesetVersion = message.rulesetVersion ?? string.Empty,
                Players = players,
                LastInputs = lastInputs
            };
        }

        private void ScheduleReconnect()
        {
            if (intentionallyDisconnected ||
                !HasReconnectIdentity ||
                reconnectCoroutine != null ||
                State != ConnectionState.Disconnected)
            {
                return;
            }

            reconnectCoroutine =
                StartCoroutine(AutoReconnectRoutine());
        }

        private IEnumerator AutoReconnectRoutine()
        {
            yield return new WaitForSecondsRealtime(reconnectDelay);
            reconnectCoroutine = null;

            if (intentionallyDisconnected ||
                State != ConnectionState.Disconnected ||
                !HasReconnectIdentity)
            {
                yield break;
            }

            Debug.Log(
                $"[Network] 自动重连 PlayerId={LocalPlayerId}, " +
                $"Room={RoomId}。");
            Connect();
        }

        private void CancelReconnectRoutine()
        {
            if (reconnectCoroutine == null)
            {
                return;
            }

            StopCoroutine(reconnectCoroutine);
            reconnectCoroutine = null;
        }
    }

    /// <summary>
    /// 接收服务端分配的锁步玩家 ID。
    /// </summary>
    internal sealed class G2C_PlayerAssignedHandler
        : Message<G2C_PlayerAssigned>
    {
        protected override async FTask Run(
            Session session,
            G2C_PlayerAssigned message)
        {
            NetworkManager.DispatchPlayerAssigned(message);
            await FTask.CompletedTask;
        }
    }

    internal sealed class G2C_ProfileRegisteredHandler
        : Message<G2C_ProfileRegistered>
    {
        protected override async FTask Run(
            Session session,
            G2C_ProfileRegistered message)
        {
            NetworkManager.DispatchProfileRegistered(message);
            await FTask.CompletedTask;
        }
    }

    internal sealed class G2C_MatchmakingStatusHandler
        : Message<G2C_MatchmakingStatus>
    {
        protected override async FTask Run(
            Session session,
            G2C_MatchmakingStatus message)
        {
            NetworkManager.DispatchMatchmakingStatus(message);
            await FTask.CompletedTask;
        }
    }

    internal sealed class G2C_MatchFoundHandler
        : Message<G2C_MatchFound>
    {
        protected override async FTask Run(
            Session session,
            G2C_MatchFound message)
        {
            NetworkManager.DispatchMatchFound(message);
            await FTask.CompletedTask;
        }
    }

    internal sealed class G2C_BattleStartHandler
        : Message<G2C_BattleStart>
    {
        protected override async FTask Run(
            Session session,
            G2C_BattleStart message)
        {
            NetworkManager.DispatchBattleStart(message);
            await FTask.CompletedTask;
        }
    }

    internal sealed class G2C_MatchSettledHandler
        : Message<G2C_MatchSettled>
    {
        protected override async FTask Run(
            Session session,
            G2C_MatchSettled message)
        {
            NetworkManager.DispatchMatchSettled(message);
            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// 接收服务端广播的完整锁步帧。
    ///
    /// G2C_FrameData 在 proto 中标记为 IMessage，因此继承 Message&lt;T&gt;。
    /// Fantasy 会识别并自动注册这个消息处理器。
    /// </summary>
    internal sealed class G2C_FrameDataHandler
        : Message<G2C_FrameData>
    {
        protected override async FTask Run(
            Session session,
            G2C_FrameData message)
        {
            NetworkManager.DispatchFrameData(message);
            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// 接收服务端权威重连快照。
    /// </summary>
    internal sealed class G2C_ReconnectStateHandler
        : Message<G2C_ReconnectState>
    {
        protected override async FTask Run(
            Session session,
            G2C_ReconnectState message)
        {
            NetworkManager.DispatchReconnectState(message);
            await FTask.CompletedTask;
        }
    }

    /// <summary>
    /// 服务端已清理旧房间，客户端同时丢弃旧重连身份。
    /// </summary>
    internal sealed class G2C_LockstepClosedHandler
        : Message<G2C_LockstepClosed>
    {
        protected override async FTask Run(
            Session session,
            G2C_LockstepClosed message)
        {
            NetworkManager.DispatchLockstepClosed(message);
            await FTask.CompletedTask;
        }
    }
}

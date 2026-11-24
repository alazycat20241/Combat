using System;
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

        [Header("心跳")]
        [SerializeField] private bool enableHeartbeat = true;
        [SerializeField, Min(500)] private int heartbeatInterval = 2000;
        [SerializeField, Min(1000)] private int heartbeatTimeout = 30000;
        [SerializeField, Min(500)] private int heartbeatCheckInterval = 5000;
        [SerializeField, Min(1)] private int maxPingSamples = 4;

        private Session session;
        private bool intentionallyDisconnected;

        public event Action Connected;
        public event Action<string> ConnectFailed;
        public event Action Disconnected;

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

        public ConnectionState State { get; private set; } =
            ConnectionState.Disconnected;

        public Session Session => session;

        /// <summary>
        /// 服务端分配给当前连接的锁步玩家 ID；未分配时为 0。
        /// </summary>
        public int LocalPlayerId { get; private set; }

        public bool IsConnected =>
            State == ConnectionState.Connected &&
            session != null &&
            !session.IsDisposed;

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
            if (State == ConnectionState.Connecting || IsConnected)
            {
                return;
            }

            intentionallyDisconnected = false;
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
                ConnectFailed?.Invoke(exception.Message);
                Debug.LogError(
                    $"[Network] KCP 连接初始化失败：" +
                    $"{host}:{port}\n{exception}");
            }
        }

        private void HandleConnected()
        {
            session = Fantasy.Runtime.Session;
            State = ConnectionState.Connected;
            Debug.Log($"[Network] Fantasy KCP 已连接：{host}:{port}");

            // 连接成功后的第一条业务消息就是加入锁步房间。
            // KCP 保持发送顺序，因此后续 C2G_SubmitFrameInput 一定排在本请求之后。
            C2G_JoinLockstep request = C2G_JoinLockstep.Create();
            session.Send(request);

            Connected?.Invoke();
        }

        private void HandleConnectFailed()
        {
            State = ConnectionState.Disconnected;
            session = null;
            LocalPlayerId = 0;
            const string message = "无法连接到 Fantasy Gate。";
            Debug.LogError($"[Network] {message} {host}:{port}");
            ConnectFailed?.Invoke(message);
        }

        private void HandleDisconnected()
        {
            State = ConnectionState.Disconnected;
            session = null;
            LocalPlayerId = 0;

            if (intentionallyDisconnected)
            {
                Debug.Log("[Network] Fantasy 会话已主动断开。");
                return;
            }

            Debug.LogWarning("[Network] Fantasy 会话意外断开。");
            Disconnected?.Invoke();
        }

        public void Disconnect()
        {
            intentionallyDisconnected = true;
            State = ConnectionState.Disconnected;
            LocalPlayerId = 0;

            if (session != null && !session.IsDisposed)
            {
                session.Dispose();
            }

            session = null;
        }

        /// <summary>
        /// 发送服务器主动消息。消息生命周期由 Fantasy 管理。
        /// </summary>
        public bool Send<T>(T message) where T : IMessage
        {
            if (!IsConnected)
            {
                message?.Dispose();
                return false;
            }

            session.Send(message);
            return true;
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
            if (!IsConnected)
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
            manager.OnPlayerIdAssigned?.Invoke(message.playerId);
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
}

using System;
using System.Collections.Generic;
using Combat.Core;
using Fantasy;
using Game.Networking;
using UnityEngine;

/// <summary>
/// Unity 与纯逻辑世界之间唯一的帧驱动器。
///
/// 执行顺序设为 10000，保证普通 PlayerController 在该类之前提交输入，
/// 然后由 LockstepManager 根据服务端确认帧调用 CombatWorld.Tick()。
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class CombatSimulationRunner : MonoBehaviour
{
    private static CombatSimulationRunner instance;
    private readonly List<CombatEvent> frameEvents = new List<CombatEvent>(16);
    private NetworkManager subscribedNetworkManager;
    private int serverAssignedPlayerId;
    private int appliedServerPlayerId;
    private long lastLoggedReceivedFrame;
    private long lastLoggedExecutedFrame;

    public static LockstepManager Lockstep { get; private set; }
    public static CombatWorld World => Lockstep?.World;
    public static event Action<CombatEvent> EventCommitted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateRunner()
    {
        if (instance != null)
        {
            return;
        }

        GameObject runnerObject = new GameObject("[Combat Simulation]");
        DontDestroyOnLoad(runnerObject);
        instance = runnerObject.AddComponent<CombatSimulationRunner>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        Lockstep ??= new LockstepManager(Fix64.FromDecimal(0.05m), 3);
        TryBindNetwork();
    }

    private void OnEnable()
    {
        // Runner 在场景加载前创建，此时 NetworkManager 可能还不存在。
        // 这里先尝试一次；若仍为空，Update 会继续尝试绑定。
        TryBindNetwork();
    }

    private void OnDisable()
    {
        if (subscribedNetworkManager != null)
        {
            subscribedNetworkManager.OnFrameDataReceived -=
                OnFrameDataReceived;
            subscribedNetworkManager.OnPlayerIdAssigned -=
                OnPlayerIdAssigned;
            subscribedNetworkManager = null;
        }
    }

    private void Update()
    {
        TryBindNetwork();
    }

    private void FixedUpdate()
    {
        // PlayerController 的执行顺序较小，会先提交本帧输入；
        // 本 Runner 的执行顺序是 10000，因此这里能读取到刚采集的输入。
        TryBindNetwork();
        TryApplyServerAssignedPlayerId();
        long frameBeforeAdvance = Lockstep.LastCompletedFrame;
        Lockstep.AdvanceFixedFrame();

        if (Lockstep.LastCompletedFrame > frameBeforeAdvance &&
            Lockstep.LastCompletedFrame <= 8 &&
            Lockstep.LastCompletedFrame != lastLoggedExecutedFrame)
        {
            lastLoggedExecutedFrame = Lockstep.LastCompletedFrame;
            Debug.Log(
                $"[Combat] 已执行网络帧 " +
                $"{lastLoggedExecutedFrame}。");
        }

        Lockstep.DrainEvents(frameEvents);

        for (int i = 0; i < frameEvents.Count; i++)
        {
            EventCommitted?.Invoke(frameEvents[i]);
        }

        frameEvents.Clear();
    }

    private void TryBindNetwork()
    {
        if (Lockstep == null)
        {
            return;
        }

        NetworkManager currentNetworkManager = NetworkManager.Instance;
        if (currentNetworkManager == null)
        {
            return;
        }

        // 纯逻辑层只接收 ILockstepNetwork，不直接引用 MonoBehaviour。
        Lockstep.SetNetwork(currentNetworkManager);

        if (!currentNetworkManager.IsConnected &&
            currentNetworkManager.LocalPlayerId == 0)
        {
            serverAssignedPlayerId = 0;
            appliedServerPlayerId = 0;
            Lockstep.ClearServerAssignedPlayerId();
        }

        if (ReferenceEquals(
                subscribedNetworkManager,
                currentNetworkManager))
        {
            if (currentNetworkManager.LocalPlayerId > 0 &&
                serverAssignedPlayerId !=
                currentNetworkManager.LocalPlayerId)
            {
                OnPlayerIdAssigned(
                    currentNetworkManager.LocalPlayerId);
            }

            return;
        }

        if (subscribedNetworkManager != null)
        {
            subscribedNetworkManager.OnFrameDataReceived -=
                OnFrameDataReceived;
            subscribedNetworkManager.OnPlayerIdAssigned -=
                OnPlayerIdAssigned;
        }

        subscribedNetworkManager = currentNetworkManager;
        subscribedNetworkManager.OnFrameDataReceived +=
            OnFrameDataReceived;
        subscribedNetworkManager.OnPlayerIdAssigned +=
            OnPlayerIdAssigned;

        if (currentNetworkManager.LocalPlayerId > 0)
        {
            OnPlayerIdAssigned(
                currentNetworkManager.LocalPlayerId);
        }
    }

    private void OnPlayerIdAssigned(int playerId)
    {
        if (playerId <= 0)
        {
            return;
        }

        serverAssignedPlayerId = playerId;
        TryApplyServerAssignedPlayerId();
    }

    /// <summary>
    /// 把服务端 PlayerId 同步到纯逻辑锁步管理器，并切换本地输入控制权。
    ///
    /// 场景中的 Player1/Player2 只是两个本地逻辑实体。服务端分到 PlayerId=2
    /// 的客户端也必须控制 EntityId=2，因此不能继续使用场景 Inspector 里
    /// 固定的 acceptPlayerInput 设置。
    /// </summary>
    private void TryApplyServerAssignedPlayerId()
    {
        if (serverAssignedPlayerId <= 0 || Lockstep == null)
        {
            return;
        }

        if (appliedServerPlayerId == serverAssignedPlayerId &&
            Lockstep.LocalClientId == serverAssignedPlayerId &&
            Lockstep.HasServerAssignedPlayerId)
        {
            return;
        }

        PlayerController[] controllers =
            FindObjectsOfType<PlayerController>();
        bool foundAssignedController = false;

        for (int i = 0; i < controllers.Length; i++)
        {
            PlayerController controller = controllers[i];
            int entityId = controller.Entity != null
                ? controller.Entity.EntityId
                : 0;

            bool isLocalPlayer =
                entityId == serverAssignedPlayerId;
            controller.AcceptPlayerInput = isLocalPlayer;

            if (isLocalPlayer)
            {
                foundAssignedController = true;
            }
        }

        if (!foundAssignedController ||
            !HasRegisteredPlayer(serverAssignedPlayerId))
        {
            return;
        }

        Lockstep.SetLocalPlayerId(serverAssignedPlayerId);
        appliedServerPlayerId = serverAssignedPlayerId;

        Debug.Log(
            $"[Combat] 当前客户端控制 PlayerId=" +
            $"{serverAssignedPlayerId}。");
    }

    private bool HasRegisteredPlayer(int playerId)
    {
        for (int i = 0; i < Lockstep.PlayerIds.Count; i++)
        {
            if (Lockstep.PlayerIds[i] == playerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// G2C_FrameData 是 Fantasy 池化消息。
    ///
    /// 这个回调必须在返回前把所有需要的数据复制成纯 FrameInput。
    /// LockstepManager 的队列里绝对不能保存 message、message.inputs
    /// 或 PlayerInputData 的引用，否则消息回收后数据会被清空或复用。
    /// </summary>
    private void OnFrameDataReceived(G2C_FrameData message)
    {
        if (message == null ||
            Lockstep == null ||
            message.inputs == null)
        {
            return;
        }

        if (message.frameId <= 8 &&
            message.frameId != lastLoggedReceivedFrame)
        {
            lastLoggedReceivedFrame = message.frameId;
            Debug.Log(
                $"[Combat] 收到网络帧 {message.frameId}，" +
                $"玩家数={message.inputs.Count}。");
        }

        FrameInput[] copiedInputs =
            new FrameInput[message.inputs.Count];

        for (int i = 0; i < message.inputs.Count; i++)
        {
            PlayerInputData input = message.inputs[i];
            copiedInputs[i] = new FrameInput(
                message.frameId,
                input.playerId,
                (FrameInputMask)(uint)input.inputMask);
        }

        Lockstep.EnqueueFrameData(message.frameId, copiedInputs);
    }
}

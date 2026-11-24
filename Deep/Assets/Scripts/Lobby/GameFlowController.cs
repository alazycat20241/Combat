using System;
using Game.Networking;
using UnityEngine;

/// <summary>
/// 单机联调阶段的最小完整流程控制器：
/// 连接 -> 注册昵称 -> 大厅 -> 匹配 -> 准备 -> 战斗 -> 结算 -> 返回大厅。
///
/// 这里使用 OnGUI 只是为了让流程在没有正式美术界面前也能完整测试，
/// 后续替换为 UGUI/UI Toolkit 时不需要改网络和战斗逻辑。
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class GameFlowController : MonoBehaviour
{
    private enum FlowState
    {
        Disconnected,
        Registering,
        Lobby,
        Matching,
        ReadyToBattle,
        WaitingForBattleStart,
        Battle,
        Settled
    }

    private static GameFlowController instance;

    private NetworkManager network;
    private NetworkManager subscribedNetwork;
    private FlowState state = FlowState.Disconnected;
    private string displayName = string.Empty;
    private string statusMessage = "正在连接服务器...";
    private string opponentName = string.Empty;
    private MatchSettledData settlement;
    private string autoTestName = string.Empty;
    private bool autoTestMatch;
    private bool autoRegisterSent;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateController()
    {
        if (instance != null)
        {
            return;
        }

        GameObject flowObject =
            new GameObject("[Game Flow]");
        DontDestroyOnLoad(flowObject);
        instance = flowObject.AddComponent<GameFlowController>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        ReadCommandLineTestOptions();
    }

    private void Update()
    {
        TryBindNetwork();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            Unsubscribe();
            instance = null;
        }
    }

    private void TryBindNetwork()
    {
        NetworkManager current = NetworkManager.Instance;
        if (current == null ||
            ReferenceEquals(current, subscribedNetwork))
        {
            return;
        }

        Unsubscribe();
        subscribedNetwork = current;
        subscribedNetwork.Connected += OnConnected;
        subscribedNetwork.ConnectFailed += OnConnectFailed;
        subscribedNetwork.Disconnected += OnDisconnected;
        subscribedNetwork.MatchClosed += OnMatchClosed;
        subscribedNetwork.OnProfileRegistered += OnProfileRegistered;
        subscribedNetwork.OnMatchmakingStatus += OnMatchmakingStatus;
        subscribedNetwork.OnMatchFound += OnMatchFound;
        subscribedNetwork.OnBattleStarted += OnBattleStarted;
        subscribedNetwork.OnMatchSettled += OnMatchSettled;

        if (current.IsTransportConnected)
        {
            OnConnected();
        }
    }

    private void Unsubscribe()
    {
        if (subscribedNetwork == null)
        {
            return;
        }

        subscribedNetwork.Connected -= OnConnected;
        subscribedNetwork.ConnectFailed -= OnConnectFailed;
        subscribedNetwork.Disconnected -= OnDisconnected;
        subscribedNetwork.MatchClosed -= OnMatchClosed;
        subscribedNetwork.OnProfileRegistered -= OnProfileRegistered;
        subscribedNetwork.OnMatchmakingStatus -= OnMatchmakingStatus;
        subscribedNetwork.OnMatchFound -= OnMatchFound;
        subscribedNetwork.OnBattleStarted -= OnBattleStarted;
        subscribedNetwork.OnMatchSettled -= OnMatchSettled;
        subscribedNetwork = null;
    }

    private void OnConnected()
    {
        network = NetworkManager.Instance;
        if (network == null)
        {
            return;
        }

        if (network.HasReconnectIdentity)
        {
            state = FlowState.Battle;
            statusMessage = "正在恢复战斗...";
            return;
        }

        state = FlowState.Registering;
        statusMessage = "请输入本客户端 2-16 个字符的昵称。";

        if (!string.IsNullOrWhiteSpace(autoTestName) &&
            !autoRegisterSent)
        {
            autoRegisterSent = true;
            displayName = autoTestName;
            RegisterCurrentName();
        }
    }

    private void OnConnectFailed(string message)
    {
        state = FlowState.Disconnected;
        statusMessage = message;
    }

    private void OnDisconnected()
    {
        state = FlowState.Disconnected;
        statusMessage = network != null && network.HasReconnectIdentity
            ? "网络断开，正在自动重连..."
            : "网络已断开。";
    }

    private void OnMatchClosed(string reason)
    {
        state = FlowState.Disconnected;
        statusMessage = reason;
    }

    private void OnProfileRegistered(
        ProfileRegisteredData result)
    {
        if (result == null)
        {
            return;
        }

        if (!result.Success)
        {
            state = FlowState.Registering;
            statusMessage = result.Message;
            return;
        }

        displayName = result.DisplayName;

        state = FlowState.Lobby;
        statusMessage = $"欢迎，{displayName}。";

        if (autoTestMatch)
        {
            StartMatchmaking();
        }
    }

    private void OnMatchmakingStatus(MatchmakingStatusData result)
    {
        if (result == null)
        {
            return;
        }

        statusMessage = result.Message;
        switch (result.State)
        {
            case 1:
                state = FlowState.Matching;
                break;
            case 3:
                state = FlowState.Lobby;
                break;
            case 4:
                state = FlowState.Lobby;
                break;
            case 0:
                if (state == FlowState.Settled)
                {
                    state = FlowState.Lobby;
                }
                break;
        }
    }

    private void OnMatchFound(MatchFoundData result)
    {
        if (result == null)
        {
            return;
        }

        opponentName = result.OpponentDisplayName;
        statusMessage =
            $"匹配成功：{displayName} VS {opponentName}";

        // 新房间必须从 Frame 0 和共享出生点重新开始。
        CombatSimulationRunner.Lockstep?.ResetForNewMatch();
        PlayerController[] players =
            FindObjectsOfType<PlayerController>();
        for (int i = 0; i < players.Length; i++)
        {
            players[i].HandleReconnectVisual(0);
        }

        state = FlowState.ReadyToBattle;
        if (autoTestMatch)
        {
            EnterBattle();
        }
    }

    private void OnBattleStarted(BattleStartData result)
    {
        if (result == null ||
            network == null ||
            !network.JoinLockstepMatch())
        {
            statusMessage = "进入战斗房间失败。";
            return;
        }

        state = FlowState.Battle;
        statusMessage = "战斗开始！WASD 移动，鼠标攻击，空格大招。";
    }

    private void OnMatchSettled(MatchSettledData result)
    {
        if (result == null)
        {
            return;
        }

        settlement = result;
        state = FlowState.Settled;
        statusMessage = result.IsDraw
            ? "平局。"
            : $"胜者：{result.WinnerName}";
    }

    private void RegisterCurrentName()
    {
        network = NetworkManager.Instance;
        if (network == null ||
            !network.RegisterProfile(displayName, string.Empty))
        {
            state = FlowState.Disconnected;
            statusMessage = "当前未连接服务器。";
        }
    }

    private void StartMatchmaking()
    {
        network = NetworkManager.Instance;
        if (network != null && network.StartMatchmaking())
        {
            state = FlowState.Matching;
            statusMessage = "正在寻找对手...";
        }
    }

    private void CancelMatchmaking()
    {
        NetworkManager.Instance?.CancelMatchmaking();
    }

    private void EnterBattle()
    {
        network = NetworkManager.Instance;
        if (network == null || !network.SendMatchReady())
        {
            statusMessage = "准备失败，请重试。";
            return;
        }

        state = FlowState.WaitingForBattleStart;
        statusMessage = "等待对手准备...";
    }

    private void ReturnToLobby()
    {
        network = NetworkManager.Instance;
        if (network != null)
        {
            network.SendLeaveMatch();
            network.ClearMatchIdentity();
        }

        CombatSimulationRunner.Lockstep?.ResetForNewMatch();
        settlement = null;
        opponentName = string.Empty;
        state = FlowState.Lobby;
        statusMessage = "已返回大厅。";
    }

    private void OnGUI()
    {
        if (instance != this)
        {
            return;
        }

        float scale = Mathf.Max(
            1f,
            Screen.height / 900f);
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(scale, scale, 1f));

        GUILayout.BeginArea(
            new Rect(20f, 20f, 430f, 360f),
            GUI.skin.box);
        GUILayout.Label($"<b>Combat Lobby</b>", RichLabel());
        GUILayout.Label($"状态：{state}");
        GUILayout.Label(statusMessage);
        GUILayout.Space(8f);

        switch (state)
        {
            case FlowState.Disconnected:
                if (GUILayout.Button("重新连接"))
                {
                    NetworkManager.Instance?.Connect();
                }
                break;

            case FlowState.Registering:
                GUILayout.Label("昵称");
                displayName = GUILayout.TextField(
                    displayName ?? string.Empty,
                    16);
                if (GUILayout.Button("注册昵称"))
                {
                    RegisterCurrentName();
                }
                break;

            case FlowState.Lobby:
                GUILayout.Label($"玩家：{displayName}");
                if (GUILayout.Button("开始匹配"))
                {
                    StartMatchmaking();
                }
                break;

            case FlowState.Matching:
                GUILayout.Label("正在匹配 1v1 对手...");
                if (GUILayout.Button("取消匹配"))
                {
                    CancelMatchmaking();
                }
                break;

            case FlowState.ReadyToBattle:
                GUILayout.Label($"{displayName}  VS  {opponentName}");
                if (GUILayout.Button("进入战斗"))
                {
                    EnterBattle();
                }
                break;

            case FlowState.WaitingForBattleStart:
                GUILayout.Label("已准备，等待双方加载完成。");
                break;

            case FlowState.Battle:
                GUILayout.Label(
                    $"对手：{opponentName}\n" +
                    "WASD 移动\n左键/右键连击\n空格大招");
                break;

            case FlowState.Settled:
                if (settlement != null)
                {
                    GUILayout.Label(
                        settlement.IsDraw
                            ? "双方同时倒下"
                            : $"{settlement.WinnerName} 获胜");
                    GUILayout.Label(
                        $"结束帧：{settlement.FinalFrame}\n" +
                        $"原因：{settlement.Reason}");
                }
                if (GUILayout.Button("返回大厅"))
                {
                    ReturnToLobby();
                }
                break;
        }

        GUILayout.EndArea();
        GUI.matrix = previousMatrix;
    }

    private static GUIStyle RichLabel()
    {
        return new GUIStyle(GUI.skin.label)
        {
            richText = true,
            fontSize = 16
        };
    }

    /// <summary>
    /// 仅供自动化联调：
    /// CombatLobby.exe -autoName Bob -autoMatch
    /// 正常启动不包含这些参数时不会改变任何流程。
    /// </summary>
    private void ReadCommandLineTestOptions()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(
                    arguments[i],
                    "-autoName",
                    StringComparison.OrdinalIgnoreCase) &&
                i + 1 < arguments.Length)
            {
                autoTestName = arguments[++i];
                continue;
            }

            if (string.Equals(
                    arguments[i],
                    "-autoMatch",
                    StringComparison.OrdinalIgnoreCase))
            {
                autoTestMatch = true;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;

namespace Hotfix.Lockstep
{
    public sealed class MatchSeatAssignment
    {
        public int PlayerId;
        public long ProfileId;
        public string DisplayName = string.Empty;
        public string MatchToken = string.Empty;
    }

    /// <summary>
    /// 单 Gate 场景中的 1v1 匹配队列。
    ///
    /// 匹配只负责把两位已注册玩家配到同一个唯一 roomId，
    /// 真正的战斗、准备和结算由对应的 LockstepRoomSystem 管理。
    /// </summary>
    public sealed class MatchmakingSystem
    {
        public const int StatusIdle = 0;
        public const int StatusQueued = 1;
        public const int StatusMatched = 2;
        public const int StatusCancelled = 3;
        public const int StatusError = 4;

        private sealed class QueueEntry
        {
            public Session Session = null!;
            public PlayerProfileSystem.Profile Profile = null!;
        }

        private static readonly object InstanceLock = new object();
        private static readonly Dictionary<long, MatchmakingSystem> Instances =
            new Dictionary<long, MatchmakingSystem>();

        private readonly object syncRoot = new object();
        private readonly Scene scene;
        private readonly List<QueueEntry> queue =
            new List<QueueEntry>();
        private long nextMatchNumber = 0;

        private MatchmakingSystem(Scene scene)
        {
            this.scene = scene ??
                throw new ArgumentNullException(nameof(scene));
        }

        public static MatchmakingSystem GetOrCreate(Scene scene)
        {
            if (scene == null)
            {
                throw new ArgumentNullException(nameof(scene));
            }

            lock (InstanceLock)
            {
                if (!Instances.TryGetValue(
                        scene.Id,
                        out MatchmakingSystem? system))
                {
                    system = new MatchmakingSystem(scene);
                    Instances.Add(scene.Id, system);
                }

                return system;
            }
        }

        public static void StartMatchmaking(Session session)
        {
            if (session == null ||
                session.IsDisposed ||
                session.Scene == null)
            {
                return;
            }

            if (!PlayerProfileSystem.TryGetBySession(
                    session,
                    out PlayerProfileSystem.Profile? profile) ||
                profile == null)
            {
                SendStatus(
                    session,
                    StatusError,
                    0,
                    "请先注册昵称。");
                return;
            }

            GetOrCreate(session.Scene)
                .Enqueue(session, profile);
        }

        public static void CancelMatchmaking(Session session)
        {
            if (session == null ||
                session.IsDisposed ||
                session.Scene == null)
            {
                return;
            }

            GetOrCreate(session.Scene).Cancel(session);
        }

        private void Enqueue(
            Session session,
            PlayerProfileSystem.Profile profile)
        {
            lock (syncRoot)
            {
                RemoveDisposedEntriesLocked();

                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    if (queue[i].Session.Id == session.Id ||
                        queue[i].Profile.ProfileId == profile.ProfileId)
                    {
                        queue.RemoveAt(i);
                    }
                }

                queue.Add(new QueueEntry
                {
                    Session = session,
                    Profile = profile
                });
                SendStatus(
                    session,
                    StatusQueued,
                    queue.Count,
                    "正在匹配对手。");

                TryPairLocked();
            }
        }

        private void Cancel(Session session)
        {
            lock (syncRoot)
            {
                bool removed = false;
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    if (queue[i].Session.Id == session.Id)
                    {
                        queue.RemoveAt(i);
                        removed = true;
                    }
                }

                SendStatus(
                    session,
                    removed ? StatusCancelled : StatusIdle,
                    queue.Count,
                    removed ? "已取消匹配。" : "当前不在匹配队列中。");
            }
        }

        private void TryPairLocked()
        {
            RemoveDisposedEntriesLocked();
            if (queue.Count < LockstepRoomSystem.DefaultExpectedPlayerCount)
            {
                return;
            }

            QueueEntry playerOne = queue[0];
            QueueEntry playerTwo = queue[1];
            queue.RemoveRange(0, 2);

            string roomId =
                $"{scene.Id}-{++nextMatchNumber:D8}";
            MatchSeatAssignment[] seats =
            {
                new MatchSeatAssignment
                {
                    PlayerId = 1,
                    ProfileId = playerOne.Profile.ProfileId,
                    DisplayName = playerOne.Profile.DisplayName,
                    MatchToken = PlayerProfileSystem.GenerateToken()
                },
                new MatchSeatAssignment
                {
                    PlayerId = 2,
                    ProfileId = playerTwo.Profile.ProfileId,
                    DisplayName = playerTwo.Profile.DisplayName,
                    MatchToken = PlayerProfileSystem.GenerateToken()
                }
            };

            if (!LockstepRoomSystem.CreateMatchRoom(
                    scene,
                    roomId,
                    seats))
            {
                SendStatus(
                    playerOne.Session,
                    StatusError,
                    queue.Count,
                    "创建战斗房间失败。");
                SendStatus(
                    playerTwo.Session,
                    StatusError,
                    queue.Count,
                    "创建战斗房间失败。");
                return;
            }

            SendMatchFound(
                roomId,
                playerOne.Session,
                seats[0],
                seats[1]);
            SendMatchFound(
                roomId,
                playerTwo.Session,
                seats[1],
                seats[0]);

            Log.Info(
                $"Matchmaking created RoomId={roomId}, " +
                $"Player1={seats[0].DisplayName}" +
                $"({seats[0].ProfileId}), " +
                $"Player2={seats[1].DisplayName}" +
                $"({seats[1].ProfileId}).");
        }

        private void RemoveDisposedEntriesLocked()
        {
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                if (queue[i].Session.IsDisposed)
                {
                    queue.RemoveAt(i);
                }
            }
        }

        private static void SendMatchFound(
            string roomId,
            Session session,
            MatchSeatAssignment localSeat,
            MatchSeatAssignment opponentSeat)
        {
            if (session.IsDisposed)
            {
                return;
            }

            G2C_MatchFound message = G2C_MatchFound.Create();
            message.roomId = roomId;
            message.playerId = localSeat.PlayerId;
            message.profileId = localSeat.ProfileId;
            message.matchToken = localSeat.MatchToken;
            message.localDisplayName = localSeat.DisplayName;
            message.opponentDisplayName = opponentSeat.DisplayName;
            session.Send(message);
        }

        private static void SendStatus(
            Session session,
            int state,
            int queueSize,
            string text)
        {
            if (session == null || session.IsDisposed)
            {
                return;
            }

            G2C_MatchmakingStatus message =
                G2C_MatchmakingStatus.Create();
            message.state = state;
            message.queueSize = queueSize;
            message.message = text;
            session.Send(message);
        }
    }

    public sealed class C2G_StartMatchmakingHandler
        : Message<C2G_StartMatchmaking>
    {
        protected override async FTask Run(
            Session session,
            C2G_StartMatchmaking message)
        {
            MatchmakingSystem.StartMatchmaking(session);
            await FTask.CompletedTask;
        }
    }

    public sealed class C2G_CancelMatchmakingHandler
        : Message<C2G_CancelMatchmaking>
    {
        protected override async FTask Run(
            Session session,
            C2G_CancelMatchmaking message)
        {
            MatchmakingSystem.CancelMatchmaking(session);
            await FTask.CompletedTask;
        }
    }
}

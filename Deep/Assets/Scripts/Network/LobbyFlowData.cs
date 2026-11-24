using System;

namespace Game.Networking
{
    public sealed class ProfileRegisteredData
    {
        public bool Success;
        public string Message = string.Empty;
        public long ProfileId;
        public string DisplayName = string.Empty;
        public string ProfileToken = string.Empty;
    }

    public sealed class MatchmakingStatusData
    {
        public int State;
        public int QueueSize;
        public string Message = string.Empty;
    }

    public sealed class MatchFoundData
    {
        public string RoomId = string.Empty;
        public int PlayerId;
        public long ProfileId;
        public string MatchToken = string.Empty;
        public string LocalDisplayName = string.Empty;
        public string OpponentDisplayName = string.Empty;
    }

    public sealed class BattleStartData
    {
        public string RoomId = string.Empty;
        public int PlayerId;
        public string RulesetVersion = string.Empty;
    }

    public sealed class MatchSettledData
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
}

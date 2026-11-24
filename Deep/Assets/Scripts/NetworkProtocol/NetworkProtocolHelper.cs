using System.Runtime.CompilerServices;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using System.Collections.Generic;
#pragma warning disable CS8618
namespace Fantasy
{
   public static class NetworkProtocolHelper
   {
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_TestMessage(this Session session, C2G_TestMessage C2G_TestMessage_message)
		{
			session.Send(C2G_TestMessage_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_TestMessage(this Session session, string tag)
		{
			using var C2G_TestMessage_message = Fantasy.C2G_TestMessage.Create();
			C2G_TestMessage_message.Tag = tag;
			session.Send(C2G_TestMessage_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_TestResponse> C2G_TestRequest(this Session session, C2G_TestRequest C2G_TestRequest_request)
		{
			return (G2C_TestResponse)await session.Call(C2G_TestRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_TestResponse> C2G_TestRequest(this Session session, string tag)
		{
			using var C2G_TestRequest_request = Fantasy.C2G_TestRequest.Create();
			C2G_TestRequest_request.Tag = tag;
			return (G2C_TestResponse)await session.Call(C2G_TestRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_JoinLockstep(this Session session, C2G_JoinLockstep C2G_JoinLockstep_message)
		{
			session.Send(C2G_JoinLockstep_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_JoinLockstep(this Session session, string roomId, string matchToken)
		{
			using var C2G_JoinLockstep_message = Fantasy.C2G_JoinLockstep.Create();
			C2G_JoinLockstep_message.roomId = roomId;
			C2G_JoinLockstep_message.matchToken = matchToken;
			session.Send(C2G_JoinLockstep_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_RegisterProfile(this Session session, C2G_RegisterProfile C2G_RegisterProfile_message)
		{
			session.Send(C2G_RegisterProfile_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_RegisterProfile(this Session session, string displayName, string clientProfileToken)
		{
			using var C2G_RegisterProfile_message = Fantasy.C2G_RegisterProfile.Create();
			C2G_RegisterProfile_message.displayName = displayName;
			C2G_RegisterProfile_message.clientProfileToken = clientProfileToken;
			session.Send(C2G_RegisterProfile_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_ProfileRegistered(this Session session, G2C_ProfileRegistered G2C_ProfileRegistered_message)
		{
			session.Send(G2C_ProfileRegistered_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_ProfileRegistered(this Session session, bool success, string message, long profileId, string displayName, string profileToken)
		{
			using var G2C_ProfileRegistered_message = Fantasy.G2C_ProfileRegistered.Create();
			G2C_ProfileRegistered_message.success = success;
			G2C_ProfileRegistered_message.message = message;
			G2C_ProfileRegistered_message.profileId = profileId;
			G2C_ProfileRegistered_message.displayName = displayName;
			G2C_ProfileRegistered_message.profileToken = profileToken;
			session.Send(G2C_ProfileRegistered_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_StartMatchmaking(this Session session, C2G_StartMatchmaking C2G_StartMatchmaking_message)
		{
			session.Send(C2G_StartMatchmaking_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_StartMatchmaking(this Session session)
		{
			using var message = Fantasy.C2G_StartMatchmaking.Create();
			session.Send(message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_CancelMatchmaking(this Session session, C2G_CancelMatchmaking C2G_CancelMatchmaking_message)
		{
			session.Send(C2G_CancelMatchmaking_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_CancelMatchmaking(this Session session)
		{
			using var message = Fantasy.C2G_CancelMatchmaking.Create();
			session.Send(message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchmakingStatus(this Session session, G2C_MatchmakingStatus G2C_MatchmakingStatus_message)
		{
			session.Send(G2C_MatchmakingStatus_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchmakingStatus(this Session session, int state, int queueSize, string message)
		{
			using var G2C_MatchmakingStatus_message = Fantasy.G2C_MatchmakingStatus.Create();
			G2C_MatchmakingStatus_message.state = state;
			G2C_MatchmakingStatus_message.queueSize = queueSize;
			G2C_MatchmakingStatus_message.message = message;
			session.Send(G2C_MatchmakingStatus_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchFound(this Session session, G2C_MatchFound G2C_MatchFound_message)
		{
			session.Send(G2C_MatchFound_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchFound(this Session session, string roomId, int playerId, long profileId, string matchToken, string localDisplayName, string opponentDisplayName)
		{
			using var G2C_MatchFound_message = Fantasy.G2C_MatchFound.Create();
			G2C_MatchFound_message.roomId = roomId;
			G2C_MatchFound_message.playerId = playerId;
			G2C_MatchFound_message.profileId = profileId;
			G2C_MatchFound_message.matchToken = matchToken;
			G2C_MatchFound_message.localDisplayName = localDisplayName;
			G2C_MatchFound_message.opponentDisplayName = opponentDisplayName;
			session.Send(G2C_MatchFound_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_MatchReady(this Session session, C2G_MatchReady C2G_MatchReady_message)
		{
			session.Send(C2G_MatchReady_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_MatchReady(this Session session, string roomId, string matchToken)
		{
			using var C2G_MatchReady_message = Fantasy.C2G_MatchReady.Create();
			C2G_MatchReady_message.roomId = roomId;
			C2G_MatchReady_message.matchToken = matchToken;
			session.Send(C2G_MatchReady_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_BattleStart(this Session session, G2C_BattleStart G2C_BattleStart_message)
		{
			session.Send(G2C_BattleStart_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_BattleStart(this Session session, string roomId, int playerId, string rulesetVersion)
		{
			using var G2C_BattleStart_message = Fantasy.G2C_BattleStart.Create();
			G2C_BattleStart_message.roomId = roomId;
			G2C_BattleStart_message.playerId = playerId;
			G2C_BattleStart_message.rulesetVersion = rulesetVersion;
			session.Send(G2C_BattleStart_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchSettled(this Session session, G2C_MatchSettled G2C_MatchSettled_message)
		{
			session.Send(G2C_MatchSettled_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_MatchSettled(this Session session, string roomId, bool isDraw, int winnerPlayerId, int loserPlayerId, string winnerName, string loserName, int finalFrame, ulong worldHash, string reason)
		{
			using var G2C_MatchSettled_message = Fantasy.G2C_MatchSettled.Create();
			G2C_MatchSettled_message.roomId = roomId;
			G2C_MatchSettled_message.isDraw = isDraw;
			G2C_MatchSettled_message.winnerPlayerId = winnerPlayerId;
			G2C_MatchSettled_message.loserPlayerId = loserPlayerId;
			G2C_MatchSettled_message.winnerName = winnerName;
			G2C_MatchSettled_message.loserName = loserName;
			G2C_MatchSettled_message.finalFrame = finalFrame;
			G2C_MatchSettled_message.worldHash = worldHash;
			G2C_MatchSettled_message.reason = reason;
			session.Send(G2C_MatchSettled_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_LeaveMatch(this Session session, C2G_LeaveMatch C2G_LeaveMatch_message)
		{
			session.Send(C2G_LeaveMatch_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_LeaveMatch(this Session session, string roomId, string matchToken)
		{
			using var C2G_LeaveMatch_message = Fantasy.C2G_LeaveMatch.Create();
			C2G_LeaveMatch_message.roomId = roomId;
			C2G_LeaveMatch_message.matchToken = matchToken;
			session.Send(C2G_LeaveMatch_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_PlayerAssigned(this Session session, G2C_PlayerAssigned G2C_PlayerAssigned_message)
		{
			session.Send(G2C_PlayerAssigned_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_PlayerAssigned(this Session session, int playerId, string roomId, string reconnectToken)
		{
			using var G2C_PlayerAssigned_message = Fantasy.G2C_PlayerAssigned.Create();
			G2C_PlayerAssigned_message.playerId = playerId;
			G2C_PlayerAssigned_message.roomId = roomId;
			G2C_PlayerAssigned_message.reconnectToken = reconnectToken;
			session.Send(G2C_PlayerAssigned_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_ReconnectLockstep(this Session session, C2G_ReconnectLockstep C2G_ReconnectLockstep_message)
		{
			session.Send(C2G_ReconnectLockstep_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_ReconnectLockstep(this Session session, string roomId, int playerId, string reconnectToken, int lastReceivedFrame)
		{
			using var C2G_ReconnectLockstep_message = Fantasy.C2G_ReconnectLockstep.Create();
			C2G_ReconnectLockstep_message.roomId = roomId;
			C2G_ReconnectLockstep_message.playerId = playerId;
			C2G_ReconnectLockstep_message.reconnectToken = reconnectToken;
			C2G_ReconnectLockstep_message.lastReceivedFrame = lastReceivedFrame;
			session.Send(C2G_ReconnectLockstep_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_SubmitFrameInput(this Session session, C2G_SubmitFrameInput C2G_SubmitFrameInput_message)
		{
			session.Send(C2G_SubmitFrameInput_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_SubmitFrameInput(this Session session, int frameId, int inputMask, int targetEntityId)
		{
			using var C2G_SubmitFrameInput_message = Fantasy.C2G_SubmitFrameInput.Create();
			C2G_SubmitFrameInput_message.frameId = frameId;
			C2G_SubmitFrameInput_message.inputMask = inputMask;
			C2G_SubmitFrameInput_message.targetEntityId = targetEntityId;
			session.Send(C2G_SubmitFrameInput_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_FrameHash(this Session session, C2G_FrameHash C2G_FrameHash_message)
		{
			session.Send(C2G_FrameHash_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_FrameHash(this Session session, int frameId, ulong worldHash)
		{
			using var C2G_FrameHash_message = Fantasy.C2G_FrameHash.Create();
			C2G_FrameHash_message.frameId = frameId;
			C2G_FrameHash_message.worldHash = worldHash;
			session.Send(C2G_FrameHash_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_FrameData(this Session session, G2C_FrameData G2C_FrameData_message)
		{
			session.Send(G2C_FrameData_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_FrameData(this Session session, int frameId, List<PlayerInputData> inputs)
		{
			using var G2C_FrameData_message = Fantasy.G2C_FrameData.Create();
			G2C_FrameData_message.frameId = frameId;
			G2C_FrameData_message.inputs = inputs;
			session.Send(G2C_FrameData_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_ReconnectState(this Session session, G2C_ReconnectState G2C_ReconnectState_message)
		{
			session.Send(G2C_ReconnectState_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_ReconnectState(this Session session, string roomId, int confirmedFrame, int nextFrame, ulong worldHash, string rulesetVersion, List<ReconnectPlayerState> players, List<PlayerInputData> lastInputs)
		{
			using var G2C_ReconnectState_message = Fantasy.G2C_ReconnectState.Create();
			G2C_ReconnectState_message.roomId = roomId;
			G2C_ReconnectState_message.confirmedFrame = confirmedFrame;
			G2C_ReconnectState_message.nextFrame = nextFrame;
			G2C_ReconnectState_message.worldHash = worldHash;
			G2C_ReconnectState_message.rulesetVersion = rulesetVersion;
			G2C_ReconnectState_message.players = players;
			G2C_ReconnectState_message.lastInputs = lastInputs;
			session.Send(G2C_ReconnectState_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_LockstepClosed(this Session session, G2C_LockstepClosed G2C_LockstepClosed_message)
		{
			session.Send(G2C_LockstepClosed_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_LockstepClosed(this Session session, int reason, string message)
		{
			using var G2C_LockstepClosed_message = Fantasy.G2C_LockstepClosed.Create();
			G2C_LockstepClosed_message.reason = reason;
			G2C_LockstepClosed_message.message = message;
			session.Send(G2C_LockstepClosed_message);
		}

   }
}
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
		public static void C2G_JoinLockstep(this Session session)
		{
			using var message = Fantasy.C2G_JoinLockstep.Create();
			session.Send(message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_PlayerAssigned(this Session session, G2C_PlayerAssigned G2C_PlayerAssigned_message)
		{
			session.Send(G2C_PlayerAssigned_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_PlayerAssigned(this Session session, int playerId)
		{
			using var G2C_PlayerAssigned_message = Fantasy.G2C_PlayerAssigned.Create();
			G2C_PlayerAssigned_message.playerId = playerId;
			session.Send(G2C_PlayerAssigned_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_SubmitFrameInput(this Session session, C2G_SubmitFrameInput C2G_SubmitFrameInput_message)
		{
			session.Send(C2G_SubmitFrameInput_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_SubmitFrameInput(this Session session, int frameId, int inputMask)
		{
			using var C2G_SubmitFrameInput_message = Fantasy.C2G_SubmitFrameInput.Create();
			C2G_SubmitFrameInput_message.frameId = frameId;
			C2G_SubmitFrameInput_message.inputMask = inputMask;
			session.Send(C2G_SubmitFrameInput_message);
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

   }
}
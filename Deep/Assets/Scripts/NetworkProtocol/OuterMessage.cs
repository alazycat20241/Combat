using LightProto;
using System;
using MemoryPack;
using System.Collections.Generic;
using Fantasy;
using Fantasy.Pool;
using Fantasy.Network.Interface;
using Fantasy.Serialize;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS8618
#pragma warning disable CS8601 // Possible null reference assignment in generated message copies.
// ReSharper disable InconsistentNaming
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable PartialTypeWithSinglePart
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable PreferConcreteValueOverDefault
// ReSharper disable RedundantNameQualifier
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable CheckNamespace
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable RedundantUsingDirective
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
namespace Fantasy
{
    [Serializable]
    [ProtoContract]
    public partial class C2G_TestMessage : AMessage, IMessage
    {
        public static C2G_TestMessage Create(bool autoReturn = true)
        {
            var c2G_TestMessage = MessageObjectPool<C2G_TestMessage>.Rent();
            c2G_TestMessage.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_TestMessage.SetIsPool(false);
            }
            
            return c2G_TestMessage;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Tag = default;
            MessageObjectPool<C2G_TestMessage>.Return(this);
        }

        public void CopyTo(ref C2G_TestMessage other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Tag = Tag;

        }

        public C2G_TestMessage DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_TestMessage; } 
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_TestRequest : AMessage, IRequest
    {
        public static C2G_TestRequest Create(bool autoReturn = true)
        {
            var c2G_TestRequest = MessageObjectPool<C2G_TestRequest>.Rent();
            c2G_TestRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_TestRequest.SetIsPool(false);
            }
            
            return c2G_TestRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Tag = default;
            MessageObjectPool<C2G_TestRequest>.Return(this);
        }

        public void CopyTo(ref C2G_TestRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Tag = Tag;

        }

        public C2G_TestRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_TestRequest; } 
        [ProtoIgnore]
        public G2C_TestResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_TestResponse : AMessage, IResponse
    {
        public static G2C_TestResponse Create(bool autoReturn = true)
        {
            var g2C_TestResponse = MessageObjectPool<G2C_TestResponse>.Rent();
            g2C_TestResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_TestResponse.SetIsPool(false);
            }
            
            return g2C_TestResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            Tag = default;
            MessageObjectPool<G2C_TestResponse>.Return(this);
        }

        public void CopyTo(ref G2C_TestResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.Tag = Tag;

        }

        public G2C_TestResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_TestResponse; } 
        [ProtoMember(2)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_JoinLockstep : AMessage, IMessage
    {
        public static C2G_JoinLockstep Create(bool autoReturn = true)
        {
            var c2G_JoinLockstep = MessageObjectPool<C2G_JoinLockstep>.Rent();
            c2G_JoinLockstep.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_JoinLockstep.SetIsPool(false);
            }
            
            return c2G_JoinLockstep;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            matchToken = default;
            MessageObjectPool<C2G_JoinLockstep>.Return(this);
        }

        public void CopyTo(ref C2G_JoinLockstep other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.matchToken = matchToken;

        }

        public C2G_JoinLockstep DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_JoinLockstep; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public string matchToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_RegisterProfile : AMessage, IMessage
    {
        public static C2G_RegisterProfile Create(bool autoReturn = true)
        {
            var c2G_RegisterProfile = MessageObjectPool<C2G_RegisterProfile>.Rent();
            c2G_RegisterProfile.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_RegisterProfile.SetIsPool(false);
            }
            
            return c2G_RegisterProfile;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            displayName = default;
            clientProfileToken = default;
            MessageObjectPool<C2G_RegisterProfile>.Return(this);
        }

        public void CopyTo(ref C2G_RegisterProfile other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.displayName = displayName;
            other.clientProfileToken = clientProfileToken;

        }

        public C2G_RegisterProfile DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_RegisterProfile; } 
        [ProtoMember(1)]
        public string displayName { get; set; }
        [ProtoMember(2)]
        public string clientProfileToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_ProfileRegistered : AMessage, IMessage
    {
        public static G2C_ProfileRegistered Create(bool autoReturn = true)
        {
            var g2C_ProfileRegistered = MessageObjectPool<G2C_ProfileRegistered>.Rent();
            g2C_ProfileRegistered.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_ProfileRegistered.SetIsPool(false);
            }
            
            return g2C_ProfileRegistered;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            success = default;
            message = default;
            profileId = default;
            displayName = default;
            profileToken = default;
            MessageObjectPool<G2C_ProfileRegistered>.Return(this);
        }

        public void CopyTo(ref G2C_ProfileRegistered other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.success = success;
            other.message = message;
            other.profileId = profileId;
            other.displayName = displayName;
            other.profileToken = profileToken;

        }

        public G2C_ProfileRegistered DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_ProfileRegistered; } 
        [ProtoMember(1)]
        public bool success { get; set; }
        [ProtoMember(2)]
        public string message { get; set; }
        [ProtoMember(3)]
        public long profileId { get; set; }
        [ProtoMember(4)]
        public string displayName { get; set; }
        [ProtoMember(5)]
        public string profileToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_StartMatchmaking : AMessage, IMessage
    {
        public static C2G_StartMatchmaking Create(bool autoReturn = true)
        {
            var c2G_StartMatchmaking = MessageObjectPool<C2G_StartMatchmaking>.Rent();
            c2G_StartMatchmaking.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_StartMatchmaking.SetIsPool(false);
            }
            
            return c2G_StartMatchmaking;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            MessageObjectPool<C2G_StartMatchmaking>.Return(this);
        }

        public void CopyTo(ref C2G_StartMatchmaking other)
        {
            if (other == null)
            {
                other = Create(false);
            }

        }

        public C2G_StartMatchmaking DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_StartMatchmaking; } 
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_CancelMatchmaking : AMessage, IMessage
    {
        public static C2G_CancelMatchmaking Create(bool autoReturn = true)
        {
            var c2G_CancelMatchmaking = MessageObjectPool<C2G_CancelMatchmaking>.Rent();
            c2G_CancelMatchmaking.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_CancelMatchmaking.SetIsPool(false);
            }
            
            return c2G_CancelMatchmaking;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            MessageObjectPool<C2G_CancelMatchmaking>.Return(this);
        }

        public void CopyTo(ref C2G_CancelMatchmaking other)
        {
            if (other == null)
            {
                other = Create(false);
            }

        }

        public C2G_CancelMatchmaking DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_CancelMatchmaking; } 
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_MatchmakingStatus : AMessage, IMessage
    {
        public static G2C_MatchmakingStatus Create(bool autoReturn = true)
        {
            var g2C_MatchmakingStatus = MessageObjectPool<G2C_MatchmakingStatus>.Rent();
            g2C_MatchmakingStatus.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_MatchmakingStatus.SetIsPool(false);
            }
            
            return g2C_MatchmakingStatus;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            state = default;
            queueSize = default;
            message = default;
            MessageObjectPool<G2C_MatchmakingStatus>.Return(this);
        }

        public void CopyTo(ref G2C_MatchmakingStatus other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.state = state;
            other.queueSize = queueSize;
            other.message = message;

        }

        public G2C_MatchmakingStatus DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_MatchmakingStatus; } 
        [ProtoMember(1)]
        public int state { get; set; }
        [ProtoMember(2)]
        public int queueSize { get; set; }
        [ProtoMember(3)]
        public string message { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_MatchFound : AMessage, IMessage
    {
        public static G2C_MatchFound Create(bool autoReturn = true)
        {
            var g2C_MatchFound = MessageObjectPool<G2C_MatchFound>.Rent();
            g2C_MatchFound.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_MatchFound.SetIsPool(false);
            }
            
            return g2C_MatchFound;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            playerId = default;
            profileId = default;
            matchToken = default;
            localDisplayName = default;
            opponentDisplayName = default;
            MessageObjectPool<G2C_MatchFound>.Return(this);
        }

        public void CopyTo(ref G2C_MatchFound other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.playerId = playerId;
            other.profileId = profileId;
            other.matchToken = matchToken;
            other.localDisplayName = localDisplayName;
            other.opponentDisplayName = opponentDisplayName;

        }

        public G2C_MatchFound DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_MatchFound; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public int playerId { get; set; }
        [ProtoMember(3)]
        public long profileId { get; set; }
        [ProtoMember(4)]
        public string matchToken { get; set; }
        [ProtoMember(5)]
        public string localDisplayName { get; set; }
        [ProtoMember(6)]
        public string opponentDisplayName { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_MatchReady : AMessage, IMessage
    {
        public static C2G_MatchReady Create(bool autoReturn = true)
        {
            var c2G_MatchReady = MessageObjectPool<C2G_MatchReady>.Rent();
            c2G_MatchReady.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_MatchReady.SetIsPool(false);
            }
            
            return c2G_MatchReady;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            matchToken = default;
            MessageObjectPool<C2G_MatchReady>.Return(this);
        }

        public void CopyTo(ref C2G_MatchReady other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.matchToken = matchToken;

        }

        public C2G_MatchReady DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_MatchReady; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public string matchToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_BattleStart : AMessage, IMessage
    {
        public static G2C_BattleStart Create(bool autoReturn = true)
        {
            var g2C_BattleStart = MessageObjectPool<G2C_BattleStart>.Rent();
            g2C_BattleStart.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_BattleStart.SetIsPool(false);
            }
            
            return g2C_BattleStart;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            playerId = default;
            rulesetVersion = default;
            MessageObjectPool<G2C_BattleStart>.Return(this);
        }

        public void CopyTo(ref G2C_BattleStart other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.playerId = playerId;
            other.rulesetVersion = rulesetVersion;

        }

        public G2C_BattleStart DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_BattleStart; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public int playerId { get; set; }
        [ProtoMember(3)]
        public string rulesetVersion { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_MatchSettled : AMessage, IMessage
    {
        public static G2C_MatchSettled Create(bool autoReturn = true)
        {
            var g2C_MatchSettled = MessageObjectPool<G2C_MatchSettled>.Rent();
            g2C_MatchSettled.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_MatchSettled.SetIsPool(false);
            }
            
            return g2C_MatchSettled;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            isDraw = default;
            winnerPlayerId = default;
            loserPlayerId = default;
            winnerName = default;
            loserName = default;
            finalFrame = default;
            worldHash = default;
            reason = default;
            MessageObjectPool<G2C_MatchSettled>.Return(this);
        }

        public void CopyTo(ref G2C_MatchSettled other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.isDraw = isDraw;
            other.winnerPlayerId = winnerPlayerId;
            other.loserPlayerId = loserPlayerId;
            other.winnerName = winnerName;
            other.loserName = loserName;
            other.finalFrame = finalFrame;
            other.worldHash = worldHash;
            other.reason = reason;

        }

        public G2C_MatchSettled DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_MatchSettled; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public bool isDraw { get; set; }
        [ProtoMember(3)]
        public int winnerPlayerId { get; set; }
        [ProtoMember(4)]
        public int loserPlayerId { get; set; }
        [ProtoMember(5)]
        public string winnerName { get; set; }
        [ProtoMember(6)]
        public string loserName { get; set; }
        [ProtoMember(7)]
        public int finalFrame { get; set; }
        [ProtoMember(8)]
        public ulong worldHash { get; set; }
        [ProtoMember(9)]
        public string reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_LeaveMatch : AMessage, IMessage
    {
        public static C2G_LeaveMatch Create(bool autoReturn = true)
        {
            var c2G_LeaveMatch = MessageObjectPool<C2G_LeaveMatch>.Rent();
            c2G_LeaveMatch.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_LeaveMatch.SetIsPool(false);
            }
            
            return c2G_LeaveMatch;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            matchToken = default;
            MessageObjectPool<C2G_LeaveMatch>.Return(this);
        }

        public void CopyTo(ref C2G_LeaveMatch other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.matchToken = matchToken;

        }

        public C2G_LeaveMatch DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_LeaveMatch; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public string matchToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_PlayerAssigned : AMessage, IMessage
    {
        public static G2C_PlayerAssigned Create(bool autoReturn = true)
        {
            var g2C_PlayerAssigned = MessageObjectPool<G2C_PlayerAssigned>.Rent();
            g2C_PlayerAssigned.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_PlayerAssigned.SetIsPool(false);
            }
            
            return g2C_PlayerAssigned;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            playerId = default;
            roomId = default;
            reconnectToken = default;
            MessageObjectPool<G2C_PlayerAssigned>.Return(this);
        }

        public void CopyTo(ref G2C_PlayerAssigned other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.playerId = playerId;
            other.roomId = roomId;
            other.reconnectToken = reconnectToken;

        }

        public G2C_PlayerAssigned DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_PlayerAssigned; } 
        [ProtoMember(1)]
        public int playerId { get; set; }
        [ProtoMember(2)]
        public string roomId { get; set; }
        [ProtoMember(3)]
        public string reconnectToken { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_ReconnectLockstep : AMessage, IMessage
    {
        public static C2G_ReconnectLockstep Create(bool autoReturn = true)
        {
            var c2G_ReconnectLockstep = MessageObjectPool<C2G_ReconnectLockstep>.Rent();
            c2G_ReconnectLockstep.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_ReconnectLockstep.SetIsPool(false);
            }
            
            return c2G_ReconnectLockstep;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            playerId = default;
            reconnectToken = default;
            lastReceivedFrame = default;
            MessageObjectPool<C2G_ReconnectLockstep>.Return(this);
        }

        public void CopyTo(ref C2G_ReconnectLockstep other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.playerId = playerId;
            other.reconnectToken = reconnectToken;
            other.lastReceivedFrame = lastReceivedFrame;

        }

        public C2G_ReconnectLockstep DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_ReconnectLockstep; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public int playerId { get; set; }
        [ProtoMember(3)]
        public string reconnectToken { get; set; }
        [ProtoMember(4)]
        public int lastReceivedFrame { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_SubmitFrameInput : AMessage, IMessage
    {
        public static C2G_SubmitFrameInput Create(bool autoReturn = true)
        {
            var c2G_SubmitFrameInput = MessageObjectPool<C2G_SubmitFrameInput>.Rent();
            c2G_SubmitFrameInput.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_SubmitFrameInput.SetIsPool(false);
            }
            
            return c2G_SubmitFrameInput;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            frameId = default;
            inputMask = default;
            targetEntityId = default;
            MessageObjectPool<C2G_SubmitFrameInput>.Return(this);
        }

        public void CopyTo(ref C2G_SubmitFrameInput other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.frameId = frameId;
            other.inputMask = inputMask;
            other.targetEntityId = targetEntityId;

        }

        public C2G_SubmitFrameInput DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_SubmitFrameInput; } 
        [ProtoMember(1)]
        public int frameId { get; set; }
        [ProtoMember(2)]
        public int inputMask { get; set; }
        [ProtoMember(3)]
        public int targetEntityId { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_FrameHash : AMessage, IMessage
    {
        public static C2G_FrameHash Create(bool autoReturn = true)
        {
            var c2G_FrameHash = MessageObjectPool<C2G_FrameHash>.Rent();
            c2G_FrameHash.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_FrameHash.SetIsPool(false);
            }
            
            return c2G_FrameHash;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            frameId = default;
            worldHash = default;
            MessageObjectPool<C2G_FrameHash>.Return(this);
        }

        public void CopyTo(ref C2G_FrameHash other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.frameId = frameId;
            other.worldHash = worldHash;

        }

        public C2G_FrameHash DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_FrameHash; } 
        [ProtoMember(1)]
        public int frameId { get; set; }
        [ProtoMember(2)]
        public ulong worldHash { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class PlayerInputData : AMessage, IDisposable
    {
        public static PlayerInputData Create(bool autoReturn = true)
        {
            var playerInputData = MessageObjectPool<PlayerInputData>.Rent();
            playerInputData.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                playerInputData.SetIsPool(false);
            }
            
            return playerInputData;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            playerId = default;
            inputMask = default;
            targetEntityId = default;
            MessageObjectPool<PlayerInputData>.Return(this);
        }

        public void CopyTo(ref PlayerInputData other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.playerId = playerId;
            other.inputMask = inputMask;
            other.targetEntityId = targetEntityId;

        }

        public PlayerInputData DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public int playerId { get; set; }
        [ProtoMember(2)]
        public int inputMask { get; set; }
        [ProtoMember(3)]
        public int targetEntityId { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_FrameData : AMessage, IMessage
    {
        public static G2C_FrameData Create(bool autoReturn = true)
        {
            var g2C_FrameData = MessageObjectPool<G2C_FrameData>.Rent();
            g2C_FrameData.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_FrameData.SetIsPool(false);
            }
            
            return g2C_FrameData;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            frameId = default;
            foreach (var __t in inputs) __t.Dispose();
            inputs.Clear();
            MessageObjectPool<G2C_FrameData>.Return(this);
        }

        public void CopyTo(ref G2C_FrameData other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.frameId = frameId;
            other.inputs = new List<PlayerInputData>(inputs.Count);
            foreach (var __item in inputs) other.inputs.Add(__item.DeepCopy());

        }

        public G2C_FrameData DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_FrameData; } 
        [ProtoMember(1)]
        public int frameId { get; set; }
        [ProtoMember(2)]
        public List<PlayerInputData> inputs { get; set; } = new List<PlayerInputData>();
    }
    [Serializable]
    [ProtoContract]
    public partial class ReconnectPlayerState : AMessage, IDisposable
    {
        public static ReconnectPlayerState Create(bool autoReturn = true)
        {
            var reconnectPlayerState = MessageObjectPool<ReconnectPlayerState>.Rent();
            reconnectPlayerState.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                reconnectPlayerState.SetIsPool(false);
            }
            
            return reconnectPlayerState;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            entityId = default;
            preferredTargetId = default;
            positionXRaw = default;
            positionYRaw = default;
            positionZRaw = default;
            previousPositionXRaw = default;
            previousPositionYRaw = default;
            previousPositionZRaw = default;
            rotationYawRaw = default;
            previousRotationYawRaw = default;
            currentHpRaw = default;
            attackState = default;
            attackPhase = default;
            attackFrame = default;
            knockbackVelocityXRaw = default;
            knockbackVelocityYRaw = default;
            knockbackVelocityZRaw = default;
            isDead = default;
            isHurt = default;
            isMoving = default;
            bufferedAttack = default;
            ultimateInputBuffered = default;
            comboHitCount = default;
            comboCounterWindowRemaining = default;
            hurtTimerRaw = default;
            ultimateStartXRaw = default;
            ultimateStartYRaw = default;
            ultimateStartZRaw = default;
            ultimateFurthestXRaw = default;
            ultimateFurthestYRaw = default;
            ultimateFurthestZRaw = default;
            ultimateFurthestDistanceSqrRaw = default;
            currentFrame = default;
            hitTargetsThisAttack.Clear();
            MessageObjectPool<ReconnectPlayerState>.Return(this);
        }

        public void CopyTo(ref ReconnectPlayerState other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.entityId = entityId;
            other.preferredTargetId = preferredTargetId;
            other.positionXRaw = positionXRaw;
            other.positionYRaw = positionYRaw;
            other.positionZRaw = positionZRaw;
            other.previousPositionXRaw = previousPositionXRaw;
            other.previousPositionYRaw = previousPositionYRaw;
            other.previousPositionZRaw = previousPositionZRaw;
            other.rotationYawRaw = rotationYawRaw;
            other.previousRotationYawRaw = previousRotationYawRaw;
            other.currentHpRaw = currentHpRaw;
            other.attackState = attackState;
            other.attackPhase = attackPhase;
            other.attackFrame = attackFrame;
            other.knockbackVelocityXRaw = knockbackVelocityXRaw;
            other.knockbackVelocityYRaw = knockbackVelocityYRaw;
            other.knockbackVelocityZRaw = knockbackVelocityZRaw;
            other.isDead = isDead;
            other.isHurt = isHurt;
            other.isMoving = isMoving;
            other.bufferedAttack = bufferedAttack;
            other.ultimateInputBuffered = ultimateInputBuffered;
            other.comboHitCount = comboHitCount;
            other.comboCounterWindowRemaining = comboCounterWindowRemaining;
            other.hurtTimerRaw = hurtTimerRaw;
            other.ultimateStartXRaw = ultimateStartXRaw;
            other.ultimateStartYRaw = ultimateStartYRaw;
            other.ultimateStartZRaw = ultimateStartZRaw;
            other.ultimateFurthestXRaw = ultimateFurthestXRaw;
            other.ultimateFurthestYRaw = ultimateFurthestYRaw;
            other.ultimateFurthestZRaw = ultimateFurthestZRaw;
            other.ultimateFurthestDistanceSqrRaw = ultimateFurthestDistanceSqrRaw;
            other.currentFrame = currentFrame;
            other.hitTargetsThisAttack = new List<int>(hitTargetsThisAttack);

        }

        public ReconnectPlayerState DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public int entityId { get; set; }
        [ProtoMember(2)]
        public int preferredTargetId { get; set; }
        [ProtoMember(3)]
        public long positionXRaw { get; set; }
        [ProtoMember(4)]
        public long positionYRaw { get; set; }
        [ProtoMember(5)]
        public long positionZRaw { get; set; }
        [ProtoMember(6)]
        public long previousPositionXRaw { get; set; }
        [ProtoMember(7)]
        public long previousPositionYRaw { get; set; }
        [ProtoMember(8)]
        public long previousPositionZRaw { get; set; }
        [ProtoMember(9)]
        public long rotationYawRaw { get; set; }
        [ProtoMember(10)]
        public long previousRotationYawRaw { get; set; }
        [ProtoMember(11)]
        public long currentHpRaw { get; set; }
        [ProtoMember(12)]
        public int attackState { get; set; }
        [ProtoMember(13)]
        public int attackPhase { get; set; }
        [ProtoMember(14)]
        public int attackFrame { get; set; }
        [ProtoMember(15)]
        public long knockbackVelocityXRaw { get; set; }
        [ProtoMember(16)]
        public long knockbackVelocityYRaw { get; set; }
        [ProtoMember(17)]
        public long knockbackVelocityZRaw { get; set; }
        [ProtoMember(18)]
        public bool isDead { get; set; }
        [ProtoMember(19)]
        public bool isHurt { get; set; }
        [ProtoMember(20)]
        public bool isMoving { get; set; }
        [ProtoMember(21)]
        public int bufferedAttack { get; set; }
        [ProtoMember(22)]
        public bool ultimateInputBuffered { get; set; }
        [ProtoMember(23)]
        public int comboHitCount { get; set; }
        [ProtoMember(24)]
        public int comboCounterWindowRemaining { get; set; }
        [ProtoMember(25)]
        public long hurtTimerRaw { get; set; }
        [ProtoMember(26)]
        public long ultimateStartXRaw { get; set; }
        [ProtoMember(27)]
        public long ultimateStartYRaw { get; set; }
        [ProtoMember(28)]
        public long ultimateStartZRaw { get; set; }
        [ProtoMember(29)]
        public long ultimateFurthestXRaw { get; set; }
        [ProtoMember(30)]
        public long ultimateFurthestYRaw { get; set; }
        [ProtoMember(31)]
        public long ultimateFurthestZRaw { get; set; }
        [ProtoMember(32)]
        public long ultimateFurthestDistanceSqrRaw { get; set; }
        [ProtoMember(33)]
        public long currentFrame { get; set; }
        [ProtoMember(34)]
        public List<int> hitTargetsThisAttack { get; set; } = new List<int>();
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_ReconnectState : AMessage, IMessage
    {
        public static G2C_ReconnectState Create(bool autoReturn = true)
        {
            var g2C_ReconnectState = MessageObjectPool<G2C_ReconnectState>.Rent();
            g2C_ReconnectState.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_ReconnectState.SetIsPool(false);
            }
            
            return g2C_ReconnectState;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            roomId = default;
            confirmedFrame = default;
            nextFrame = default;
            worldHash = default;
            rulesetVersion = default;
            foreach (var __t in players) __t.Dispose();
            players.Clear();
            foreach (var __t in lastInputs) __t.Dispose();
            lastInputs.Clear();
            MessageObjectPool<G2C_ReconnectState>.Return(this);
        }

        public void CopyTo(ref G2C_ReconnectState other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.roomId = roomId;
            other.confirmedFrame = confirmedFrame;
            other.nextFrame = nextFrame;
            other.worldHash = worldHash;
            other.rulesetVersion = rulesetVersion;
            other.players = new List<ReconnectPlayerState>(players.Count);
            foreach (var __item in players) other.players.Add(__item.DeepCopy());
            other.lastInputs = new List<PlayerInputData>(lastInputs.Count);
            foreach (var __item in lastInputs) other.lastInputs.Add(__item.DeepCopy());

        }

        public G2C_ReconnectState DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_ReconnectState; } 
        [ProtoMember(1)]
        public string roomId { get; set; }
        [ProtoMember(2)]
        public int confirmedFrame { get; set; }
        [ProtoMember(3)]
        public int nextFrame { get; set; }
        [ProtoMember(4)]
        public ulong worldHash { get; set; }
        [ProtoMember(5)]
        public string rulesetVersion { get; set; }
        [ProtoMember(6)]
        public List<ReconnectPlayerState> players { get; set; } = new List<ReconnectPlayerState>();
        [ProtoMember(7)]
        public List<PlayerInputData> lastInputs { get; set; } = new List<PlayerInputData>();
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_LockstepClosed : AMessage, IMessage
    {
        public static G2C_LockstepClosed Create(bool autoReturn = true)
        {
            var g2C_LockstepClosed = MessageObjectPool<G2C_LockstepClosed>.Rent();
            g2C_LockstepClosed.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_LockstepClosed.SetIsPool(false);
            }
            
            return g2C_LockstepClosed;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            reason = default;
            message = default;
            MessageObjectPool<G2C_LockstepClosed>.Return(this);
        }

        public void CopyTo(ref G2C_LockstepClosed other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.reason = reason;
            other.message = message;

        }

        public G2C_LockstepClosed DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_LockstepClosed; } 
        [ProtoMember(1)]
        public int reason { get; set; }
        [ProtoMember(2)]
        public string message { get; set; }
    }
}
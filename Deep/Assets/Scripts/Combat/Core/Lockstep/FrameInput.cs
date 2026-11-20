using System;

namespace Combat.Core
{
    [Flags]
    public enum FrameInputMask : uint
    {
        None = 0,
        MoveForward = 1u << 0,
        MoveBackward = 1u << 1,
        MoveLeft = 1u << 2,
        MoveRight = 1u << 3,
        PrimaryAttack = 1u << 4,
        SecondaryAttack = 1u << 5,
        Jump = 1u << 6
    }

    /// <summary>
    /// 网络层最小输入单元。一个玩家每逻辑帧产生一个 FrameInput。
    /// </summary>
    public readonly struct FrameInput
    {
        public readonly long Frame;
        public readonly int PlayerId;
        public readonly FrameInputMask InputMask;

        public FrameInput(
            long frame,
            int playerId,
            FrameInputMask inputMask)
        {
            Frame = frame;
            PlayerId = playerId;
            InputMask = inputMask;
        }

        public bool Has(FrameInputMask mask)
        {
            return (InputMask & mask) != 0;
        }
    }

    /// <summary>
    /// 服务端广播的一帧完整输入。只有包含所有玩家输入时才允许推进 World。
    /// </summary>
    public sealed class FrameData
    {
        public FrameData(long frame, FrameInput[] inputs)
        {
            Frame = frame;
            Inputs = inputs ?? Array.Empty<FrameInput>();
        }

        public long Frame { get; }
        public FrameInput[] Inputs { get; }
    }
}

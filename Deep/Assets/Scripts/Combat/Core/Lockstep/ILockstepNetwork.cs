namespace Combat.Core
{
    /// <summary>
    /// LockstepManager 需要的网络能力。
    ///
    /// 这个接口位于纯 C# 的 Combat.Core 程序集中，不依赖 MonoBehaviour、
    /// UnityEngine 或具体网络协议。Unity 侧由 NetworkManager 实现它，
    /// 因此 LockstepManager 仍可以在未来的服务端、测试程序或回放工具中复用。
    /// </summary>
    public interface ILockstepNetwork
    {
        /// <summary>
        /// 当前网络是否已经连接并允许发送输入。
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 尝试发送一个逻辑帧的本地玩家输入。
        /// 返回 false 表示网络未连接或消息没有成功进入发送通道。
        /// </summary>
        bool TrySendFrameInput(
            int frameId,
            FrameInputMask inputMask,
            int targetEntityId);

        /// <summary>
        /// 上报本地执行完成帧的 WorldHash。
        /// 服务端使用两份同帧 Hash 判断客户端逻辑是否已经分叉。
        /// </summary>
        bool TrySendFrameHash(int frameId, ulong worldHash);
    }
}

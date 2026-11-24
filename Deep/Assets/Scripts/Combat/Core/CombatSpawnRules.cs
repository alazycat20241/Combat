using System;

namespace Combat.Core
{
    /// <summary>
    /// 锁步对局的最小出生规则。
    ///
    /// 出生位置和朝向必须由客户端与服务端共享，不能一边读场景 Transform、
    /// 一边使用另一套数学常量。否则即使差异只有几个 Fix64 RawValue，
    /// 第一帧的权威 Hash 也会不同。
    ///
    /// 当前两人原型使用固定席位。以后房间容量扩大时，可以把这里替换为
    /// Luban 房间出生点表，但仍应保持纯 C#、无 UnityEngine 依赖。
    /// </summary>
    public static class CombatSpawnRules
    {
        public static bool TryGetSpawn(
            int entityId,
            out LogicVector3 position,
            out LogicRotation rotation)
        {
            switch (entityId)
            {
                case 1:
                    position = new LogicVector3(0, 0, -2);
                    rotation = LogicRotation.Identity;
                    return true;

                case 2:
                    position = new LogicVector3(0, 0, 2);
                    rotation = new LogicRotation(FixMath.Pi);
                    return true;

                default:
                    position = LogicVector3.Zero;
                    rotation = LogicRotation.Identity;
                    return false;
            }
        }

        public static LogicVector3 GetPosition(int entityId)
        {
            return TryGetSpawn(
                entityId,
                out LogicVector3 position,
                out _)
                ? position
                : LogicVector3.Zero;
        }

        public static LogicRotation GetRotation(int entityId)
        {
            return TryGetSpawn(
                entityId,
                out _,
                out LogicRotation rotation)
                ? rotation
                : LogicRotation.Identity;
        }
    }
}

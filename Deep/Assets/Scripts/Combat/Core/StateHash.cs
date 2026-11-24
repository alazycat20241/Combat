using System;

namespace Combat.Core
{
    /// <summary>
    /// 确定性状态哈希工具。
    ///
    /// 使用 FNV-1a 64 位算法逐字节混合数据。调用方必须使用固定的字段顺序；
    /// 不能直接调用 object.GetHashCode()，因为它在不同运行时或进程间不保证稳定。
    /// </summary>
    internal static class StateHash
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        public static ulong Create()
        {
            return FnvOffsetBasis;
        }

        public static ulong AppendByte(ulong hash, byte value)
        {
            unchecked
            {
                hash ^= value;
                hash *= FnvPrime;
                return hash;
            }
        }

        public static ulong AppendUInt64(ulong hash, ulong value)
        {
            // 固定采用小端字节顺序，不依赖 BitConverter.IsLittleEndian。
            for (int i = 0; i < sizeof(ulong); i++)
            {
                hash = AppendByte(hash, (byte)(value & 0xFFUL));
                value >>= 8;
            }

            return hash;
        }

        public static ulong AppendInt64(ulong hash, long value)
        {
            return AppendUInt64(hash, unchecked((ulong)value));
        }

        public static ulong AppendInt32(ulong hash, int value)
        {
            return AppendUInt64(hash, unchecked((uint)value));
        }

        public static ulong AppendBoolean(ulong hash, bool value)
        {
            return AppendByte(hash, value ? (byte)1 : (byte)0);
        }

        public static ulong AppendFix64(ulong hash, Fix64 value)
        {
            return AppendInt64(hash, value.RawValue);
        }

        public static ulong AppendVector3(
            ulong hash,
            LogicVector3 value)
        {
            hash = AppendFix64(hash, value.X);
            hash = AppendFix64(hash, value.Y);
            hash = AppendFix64(hash, value.Z);
            return hash;
        }

        public static ulong AppendRotation(
            ulong hash,
            LogicRotation value)
        {
            return AppendFix64(hash, value.YawRadians);
        }

        public static ulong AppendString(
            ulong hash,
            string value)
        {
            if (value == null)
            {
                return AppendByte(hash, 0);
            }

            hash = AppendByte(hash, 1);
            hash = AppendInt32(hash, value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                hash = AppendByte(hash, (byte)(character & 0xFF));
                hash = AppendByte(hash, (byte)(character >> 8));
            }

            return hash;
        }
    }
}

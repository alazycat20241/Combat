using System;
using System.Globalization;

namespace Combat.Core
{
    /// <summary>
    /// Q32.32 有符号定点数。
    /// 所有逻辑运算都基于 long，不依赖 float/double。
    /// </summary>
    [Serializable]
    public readonly struct Fix64 :
        IEquatable<Fix64>,
        IComparable<Fix64>
    {
        private const int FractionalBits = 32;
        private const long FractionMask = 0xFFFFFFFFL;
        public const long OneRaw = 1L << FractionalBits;

        public readonly long RawValue;

        public Fix64(long rawValue)
        {
            RawValue = rawValue;
        }

        public static Fix64 Zero => new Fix64(0);
        public static Fix64 One => new Fix64(OneRaw);
        public static Fix64 Half => new Fix64(OneRaw >> 1);
        public static Fix64 MaxValue => new Fix64(long.MaxValue);

        public static Fix64 FromRaw(long rawValue)
        {
            return new Fix64(rawValue);
        }

        public static Fix64 FromInt(int value)
        {
            return new Fix64((long)value << FractionalBits);
        }

        public static Fix64 FromDecimal(decimal value)
        {
            decimal scaled = value * OneRaw;
            decimal rounded = decimal.Round(
                scaled,
                0,
                MidpointRounding.AwayFromZero);
            if (rounded > long.MaxValue || rounded < long.MinValue)
            {
                throw new OverflowException("Fix64 conversion overflow.");
            }

            return new Fix64((long)rounded);
        }

        public static Fix64 FromFloat(float value)
        {
            return FromDecimal((decimal)value);
        }

        public static Fix64 Parse(string value)
        {
            return FromDecimal(decimal.Parse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture));
        }

        public float ToFloat()
        {
            return (float)((decimal)RawValue / OneRaw);
        }

        public double ToDouble()
        {
            return (double)RawValue / OneRaw;
        }

        public int ToInt()
        {
            return (int)(RawValue >> FractionalBits);
        }

        public static Fix64 Abs(Fix64 value)
        {
            return value.RawValue < 0 ? new Fix64(-value.RawValue) : value;
        }

        public static Fix64 Min(Fix64 a, Fix64 b)
        {
            return a.RawValue < b.RawValue ? a : b;
        }

        public static Fix64 Max(Fix64 a, Fix64 b)
        {
            return a.RawValue > b.RawValue ? a : b;
        }

        public static Fix64 Clamp(Fix64 value, Fix64 min, Fix64 max)
        {
            return value < min ? min : value > max ? max : value;
        }

        public static Fix64 Sign(Fix64 value)
        {
            return value.RawValue < 0
                ? new Fix64(-OneRaw)
                : value.RawValue > 0
                    ? One
                    : Zero;
        }

        public static Fix64 Sqrt(Fix64 value)
        {
            if (value.RawValue <= 0)
            {
                return Zero;
            }

            Fix64 guess = value > One ? value * Half : One;
            for (int i = 0; i < 32; i++)
            {
                Fix64 next = (guess + (value / guess)) * Half;
                Fix64 difference = Abs(next - guess);
                guess = next;
                if (difference.RawValue <= 1)
                {
                    break;
                }
            }

            return guess;
        }

        public static Fix64 Ceiling(Fix64 value)
        {
            long raw = value.RawValue;
            return new Fix64(
                (raw + OneRaw - 1) & ~(OneRaw - 1));
        }

        public static explicit operator float(Fix64 value)
        {
            return value.ToFloat();
        }

        public static explicit operator double(Fix64 value)
        {
            return value.ToDouble();
        }

        public static implicit operator Fix64(int value)
        {
            return FromInt(value);
        }

        public static implicit operator Fix64(float value)
        {
            return FromFloat(value);
        }

        public static Fix64 operator +(Fix64 a, Fix64 b)
        {
            return new Fix64(a.RawValue + b.RawValue);
        }

        public static Fix64 operator -(Fix64 a, Fix64 b)
        {
            return new Fix64(a.RawValue - b.RawValue);
        }

        public static Fix64 operator -(Fix64 value)
        {
            return new Fix64(-value.RawValue);
        }

        public static Fix64 operator *(Fix64 a, Fix64 b)
        {
            bool negative =
                (a.RawValue < 0) ^ (b.RawValue < 0);
            ulong x = a.RawValue < 0
                ? (ulong)(-(a.RawValue + 1)) + 1UL
                : (ulong)a.RawValue;
            ulong y = b.RawValue < 0
                ? (ulong)(-(b.RawValue + 1)) + 1UL
                : (ulong)b.RawValue;
            ulong result = MultiplyRaw(x, y);
            return new Fix64(negative ? -(long)result : (long)result);
        }

        private static ulong MultiplyRaw(ulong x, ulong y)
        {
            ulong xLow = x & 0xFFFFFFFFUL;
            ulong xHigh = x >> 32;
            ulong yLow = y & 0xFFFFFFFFUL;
            ulong yHigh = y >> 32;

            ulong p0 = xLow * yLow;
            ulong p1 = xLow * yHigh;
            ulong p2 = xHigh * yLow;
            ulong p3 = xHigh * yHigh;

            ulong low = p0 & 0xFFFFFFFFUL;
            ulong middle = (p0 >> 32) +
                           (p1 & 0xFFFFFFFFUL) +
                           (p2 & 0xFFFFFFFFUL);
            ulong high = p3 +
                         (p1 >> 32) +
                         (p2 >> 32) +
                         (middle >> 32);

            middle &= 0xFFFFFFFFUL;
            if ((high >> 32) != 0)
            {
                throw new OverflowException("Fix64 multiplication overflow.");
            }

            // 产品为 128 位；右移 32 位得到 Q32.32 的 raw 值。
            ulong result = (high << 32) | middle;
            if (result > long.MaxValue)
            {
                throw new OverflowException("Fix64 multiplication overflow.");
            }

            return result;
        }

        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            if (b.RawValue == 0)
            {
                throw new DivideByZeroException();
            }

            bool negative =
                (a.RawValue < 0) ^ (b.RawValue < 0);
            ulong numerator = a.RawValue < 0
                ? (ulong)(-(a.RawValue + 1)) + 1UL
                : (ulong)a.RawValue;
            ulong denominator = b.RawValue < 0
                ? (ulong)(-(b.RawValue + 1)) + 1UL
                : (ulong)b.RawValue;

            ulong whole = numerator / denominator;
            ulong remainder = numerator % denominator;
            ulong fraction = 0UL;

            // 只靠减法逐位计算 (remainder << 32) / denominator，
            // 避免余数左移造成 64 位溢出。
            for (int bit = 0; bit < 32; bit++)
            {
                ulong doubled;
                if (remainder >= denominator - remainder)
                {
                    doubled = remainder - (denominator - remainder);
                    fraction = (fraction << 1) | 1UL;
                }
                else
                {
                    doubled = remainder << 1;
                    fraction <<= 1;
                }

                remainder = doubled;
            }

            long raw = (long)((whole << 32) + fraction);
            return new Fix64(negative ? -raw : raw);
        }

        public static bool operator ==(Fix64 a, Fix64 b)
        {
            return a.RawValue == b.RawValue;
        }

        public static bool operator !=(Fix64 a, Fix64 b)
        {
            return a.RawValue != b.RawValue;
        }

        public static bool operator <(Fix64 a, Fix64 b)
        {
            return a.RawValue < b.RawValue;
        }

        public static bool operator >(Fix64 a, Fix64 b)
        {
            return a.RawValue > b.RawValue;
        }

        public static bool operator <=(Fix64 a, Fix64 b)
        {
            return a.RawValue <= b.RawValue;
        }

        public static bool operator >=(Fix64 a, Fix64 b)
        {
            return a.RawValue >= b.RawValue;
        }

        public int CompareTo(Fix64 other)
        {
            return RawValue.CompareTo(other.RawValue);
        }

        public bool Equals(Fix64 other)
        {
            return RawValue == other.RawValue;
        }

        public override bool Equals(object obj)
        {
            return obj is Fix64 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return RawValue.GetHashCode();
        }

        public override string ToString()
        {
            return ToDouble().ToString(
                "0.######",
                CultureInfo.InvariantCulture);
        }
    }
}

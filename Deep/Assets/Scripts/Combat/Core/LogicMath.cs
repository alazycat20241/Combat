using System;

namespace Combat.Core
{
    /// <summary>
    /// 纯逻辑三维向量，所有分量和运算均为 Fix64。
    /// </summary>
    [Serializable]
    public struct LogicVector3 : IEquatable<LogicVector3>
    {
        public Fix64 X;
        public Fix64 Y;
        public Fix64 Z;

        public LogicVector3(Fix64 x, Fix64 y, Fix64 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static LogicVector3 Zero => new LogicVector3(0, 0, 0);
        public static LogicVector3 Up => new LogicVector3(0, 1, 0);
        public static LogicVector3 Forward => new LogicVector3(0, 0, 1);

        public Fix64 SqrMagnitude => (X * X) + (Y * Y) + (Z * Z);
        public Fix64 Magnitude => Fix64.Sqrt(SqrMagnitude);

        public LogicVector3 FlattenY()
        {
            return new LogicVector3(X, Fix64.Zero, Z);
        }

        public LogicVector3 Normalized()
        {
            Fix64 magnitude = Magnitude;
            return magnitude > Fix64.Zero ? this / magnitude : Zero;
        }

        public static Fix64 Dot(LogicVector3 a, LogicVector3 b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        public static Fix64 Distance(LogicVector3 a, LogicVector3 b)
        {
            return (a - b).Magnitude;
        }

        public static LogicVector3 operator +(LogicVector3 a, LogicVector3 b)
        {
            return new LogicVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static LogicVector3 operator -(LogicVector3 a, LogicVector3 b)
        {
            return new LogicVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static LogicVector3 operator -(LogicVector3 value)
        {
            return new LogicVector3(-value.X, -value.Y, -value.Z);
        }

        public static LogicVector3 operator *(LogicVector3 value, Fix64 scalar)
        {
            return new LogicVector3(
                value.X * scalar,
                value.Y * scalar,
                value.Z * scalar);
        }

        public static LogicVector3 operator *(Fix64 scalar, LogicVector3 value)
        {
            return value * scalar;
        }

        public static LogicVector3 operator /(LogicVector3 value, Fix64 scalar)
        {
            return new LogicVector3(
                value.X / scalar,
                value.Y / scalar,
                value.Z / scalar);
        }

        public bool Equals(LogicVector3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is LogicVector3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = X.GetHashCode();
                hashCode = (hashCode * 397) ^ Y.GetHashCode();
                hashCode = (hashCode * 397) ^ Z.GetHashCode();
                return hashCode;
            }
        }

        public override string ToString()
        {
            return $"({X}, {Y}, {Z})";
        }
    }

    /// <summary>
    /// 只用 Y 轴朝向的逻辑旋转，角度和三角函数均为定点实现。
    /// </summary>
    [Serializable]
    public struct LogicRotation
    {
        public Fix64 YawRadians;

        public LogicRotation(Fix64 yawRadians)
        {
            YawRadians = yawRadians;
        }

        public static LogicRotation Identity => new LogicRotation(Fix64.Zero);

        public LogicVector3 Forward
        {
            get
            {
                return new LogicVector3(
                    FixMath.Sin(YawRadians),
                    Fix64.Zero,
                    FixMath.Cos(YawRadians));
            }
        }

        public LogicVector3 Rotate(LogicVector3 localDirection)
        {
            Fix64 sin = FixMath.Sin(YawRadians);
            Fix64 cos = FixMath.Cos(YawRadians);

            return new LogicVector3(
                (localDirection.X * cos) + (localDirection.Z * sin),
                localDirection.Y,
                (-localDirection.X * sin) + (localDirection.Z * cos));
        }

        public static LogicRotation LookTowards(LogicVector3 direction)
        {
            direction.Y = Fix64.Zero;
            if (direction.SqrMagnitude <= Fix64.Zero)
            {
                return Identity;
            }

            return new LogicRotation(
                FixMath.Atan2(direction.X, direction.Z));
        }

        /// <summary>
        /// 确定性线性转向插值。sharpness * deltaTime 被截断到 [0,1]。
        /// </summary>
        public static LogicRotation SmoothTowards(
            LogicRotation current,
            LogicRotation target,
            Fix64 sharpness,
            Fix64 deltaTime)
        {
            Fix64 t = Fix64.Clamp(
                sharpness * deltaTime,
                Fix64.Zero,
                Fix64.One);
            Fix64 delta = FixMath.NormalizeAngle(
                target.YawRadians - current.YawRadians);
            return new LogicRotation(current.YawRadians + (delta * t));
        }
    }

    /// <summary>
    /// 纯数学轴对齐包围盒。
    /// </summary>
    [Serializable]
    public struct LogicAabb
    {
        public LogicVector3 Center;
        public LogicVector3 Size;

        public LogicAabb(LogicVector3 center, LogicVector3 size)
        {
            Center = center;
            Size = size;
        }

        public bool Intersects(LogicAabb other)
        {
            LogicVector3 halfA = Size * Fix64.Half;
            LogicVector3 halfB = other.Size * Fix64.Half;

            return Fix64.Abs(Center.X - other.Center.X) <= halfA.X + halfB.X &&
                   Fix64.Abs(Center.Y - other.Center.Y) <= halfA.Y + halfB.Y &&
                   Fix64.Abs(Center.Z - other.Center.Z) <= halfA.Z + halfB.Z;
        }
    }

    /// <summary>
    /// 固定精度的确定性三角函数。
    /// </summary>
    public static class FixMath
    {
        public static readonly Fix64 Pi =
            Fix64.Parse("3.1415926535897932");
        public static readonly Fix64 TwoPi =
            Fix64.Parse("6.2831853071795864");
        public static readonly Fix64 HalfPi =
            Fix64.Parse("1.5707963267948966");

        public static Fix64 NormalizeAngle(Fix64 angle)
        {
            while (angle > Pi)
            {
                angle -= TwoPi;
            }

            while (angle < -Pi)
            {
                angle += TwoPi;
            }

            return angle;
        }

        public static Fix64 Sin(Fix64 angle)
        {
            Fix64 x = NormalizeAngle(angle);
            if (x == Fix64.Zero)
            {
                return Fix64.Zero;
            }

            if (x == HalfPi)
            {
                return Fix64.One;
            }

            if (x == -HalfPi)
            {
                return -Fix64.One;
            }

            // 将输入折叠到 [-π/2, π/2]，减少高次项误差。
            if (x > HalfPi)
            {
                x = Pi - x;
            }
            else if (x < -HalfPi)
            {
                x = -Pi - x;
            }

            Fix64 x2 = x * x;
            Fix64 term = x;
            Fix64 result = x;

            term = (-term * x2) / Fix64.FromInt(6);
            result += term;
            term = (-term * x2) / Fix64.FromInt(20);
            result += term;
            term = (-term * x2) / Fix64.FromInt(42);
            result += term;
            term = (-term * x2) / Fix64.FromInt(72);
            result += term;
            term = (-term * x2) / Fix64.FromInt(110);
            result += term;
            return result;
        }

        public static Fix64 Cos(Fix64 angle)
        {
            Fix64 x = NormalizeAngle(angle);
            if (x == Fix64.Zero)
            {
                return Fix64.One;
            }

            if (x == HalfPi || x == -HalfPi)
            {
                return Fix64.Zero;
            }

            bool negative = false;
            if (x > HalfPi)
            {
                x = Pi - x;
                negative = true;
            }
            else if (x < -HalfPi)
            {
                x = -Pi - x;
                negative = true;
            }

            Fix64 x2 = x * x;
            Fix64 term = Fix64.One;
            Fix64 result = Fix64.One;

            term = (-term * x2) / Fix64.FromInt(2);
            result += term;
            term = (-term * x2) / Fix64.FromInt(12);
            result += term;
            term = (-term * x2) / Fix64.FromInt(30);
            result += term;
            term = (-term * x2) / Fix64.FromInt(56);
            result += term;
            term = (-term * x2) / Fix64.FromInt(90);
            result += term;

            return negative ? -result : result;
        }

        public static Fix64 Atan2(Fix64 y, Fix64 x)
        {
            if (x == Fix64.Zero && y == Fix64.Zero)
            {
                return Fix64.Zero;
            }

            Fix64 absX = Fix64.Abs(x);
            Fix64 absY = Fix64.Abs(y);
            Fix64 angle;

            if (absX >= absY)
            {
                Fix64 ratio = absY / absX;
                angle = AtanUnit(ratio);
                if (x < Fix64.Zero)
                {
                    angle = Pi - angle;
                }
            }
            else
            {
                Fix64 ratio = absX / absY;
                angle = HalfPi - AtanUnit(ratio);
                if (x < Fix64.Zero)
                {
                    angle = Pi - angle;
                }
            }

            return y < Fix64.Zero ? -angle : angle;
        }

        private static Fix64 AtanUnit(Fix64 value)
        {
            Fix64 x = Fix64.Clamp(value, Fix64.Zero, Fix64.One);
            Fix64 x2 = x * x;
            Fix64 result =
                Fix64.Parse("0.9998660") +
                (x2 * (
                    Fix64.Parse("-0.3302995") +
                    (x2 * (
                        Fix64.Parse("0.1801410") +
                        (x2 * (
                            Fix64.Parse("-0.0851330") +
                            (x2 * Fix64.Parse("0.0208351"))))))));
            return x * result;
        }
    }
}

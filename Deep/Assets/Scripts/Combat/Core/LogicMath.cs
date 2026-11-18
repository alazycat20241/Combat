using System;

namespace Combat.Core
{
    /// <summary>
    /// 纯逻辑三维向量。禁止依赖 UnityEngine.Vector3，未来可直接替换成 Fix64 版本。
    /// </summary>
    [Serializable]
    public struct LogicVector3 : IEquatable<LogicVector3>
    {
        public float X;
        public float Y;
        public float Z;

        public LogicVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static LogicVector3 Zero => new LogicVector3(0f, 0f, 0f);
        public static LogicVector3 Up => new LogicVector3(0f, 1f, 0f);
        public static LogicVector3 Forward => new LogicVector3(0f, 0f, 1f);

        public float SqrMagnitude => (X * X) + (Y * Y) + (Z * Z);
        public float Magnitude => (float)Math.Sqrt(SqrMagnitude);

        public LogicVector3 FlattenY()
        {
            return new LogicVector3(X, 0f, Z);
        }

        public LogicVector3 Normalized()
        {
            float magnitude = Magnitude;
            return magnitude > 0.000001f
                ? this * (1f / magnitude)
                : Zero;
        }

        public static float Dot(LogicVector3 a, LogicVector3 b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        public static float Distance(LogicVector3 a, LogicVector3 b)
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

        public static LogicVector3 operator *(LogicVector3 value, float scalar)
        {
            return new LogicVector3(value.X * scalar, value.Y * scalar, value.Z * scalar);
        }

        public static LogicVector3 operator *(float scalar, LogicVector3 value)
        {
            return value * scalar;
        }

        public bool Equals(LogicVector3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
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
            return $"({X:F2}, {Y:F2}, {Z:F2})";
        }
    }

    /// <summary>
    /// 只用 Y 轴朝向的逻辑旋转。当前动作游戏逻辑不需要俯仰和翻滚，因此避免引入完整四元数。
    /// 未来替换 Fix64 时，只需要替换角度计算。
    /// </summary>
    [Serializable]
    public struct LogicRotation
    {
        public float YawRadians;

        public LogicRotation(float yawRadians)
        {
            YawRadians = yawRadians;
        }

        public static LogicRotation Identity => new LogicRotation(0f);

        public LogicVector3 Forward
        {
            get
            {
                return new LogicVector3(
                    (float)Math.Sin(YawRadians),
                    0f,
                    (float)Math.Cos(YawRadians));
            }
        }

        public LogicVector3 Rotate(LogicVector3 localDirection)
        {
            float sin = (float)Math.Sin(YawRadians);
            float cos = (float)Math.Cos(YawRadians);

            return new LogicVector3(
                (localDirection.X * cos) + (localDirection.Z * sin),
                localDirection.Y,
                (-localDirection.X * sin) + (localDirection.Z * cos));
        }

        public static LogicRotation LookTowards(LogicVector3 direction)
        {
            direction.Y = 0f;
            if (direction.SqrMagnitude <= 0.000001f)
            {
                return Identity;
            }

            return new LogicRotation((float)Math.Atan2(direction.X, direction.Z));
        }

        public static LogicRotation SmoothTowards(
            LogicRotation current,
            LogicRotation target,
            float sharpness,
            float deltaTime)
        {
            float t = 1f - (float)Math.Exp(-Math.Max(0f, sharpness) * deltaTime);
            float delta = NormalizeAngle(target.YawRadians - current.YawRadians);
            return new LogicRotation(current.YawRadians + (delta * t));
        }

        private static float NormalizeAngle(float angle)
        {
            const float twoPi = (float)(Math.PI * 2.0);
            while (angle > Math.PI)
            {
                angle -= twoPi;
            }

            while (angle < -Math.PI)
            {
                angle += twoPi;
            }

            return angle;
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
            LogicVector3 halfA = Size * 0.5f;
            LogicVector3 halfB = other.Size * 0.5f;

            return Math.Abs(Center.X - other.Center.X) <= halfA.X + halfB.X &&
                   Math.Abs(Center.Y - other.Center.Y) <= halfA.Y + halfB.Y &&
                   Math.Abs(Center.Z - other.Center.Z) <= halfA.Z + halfB.Z;
        }
    }
}

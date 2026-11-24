using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Combat.Config;
using Combat.Core;

namespace Hotfix.Lockstep
{
    /// <summary>
    /// 读取 Unity 项目中同一份 Luban JSON。
    ///
    /// 构建时这些文件会复制到服务端输出目录的 CombatData 文件夹，
    /// 因此客户端和服务端不会各自维护一套容易漂移的数值。
    /// </summary>
    internal static class CombatConfigJsonLoader
    {
        public static PlayerCombatConfig LoadPlayer(
            int playerId,
            out CombatAttackConfig[] attacks)
        {
            string dataDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "CombatData");
            string playerPath = Path.Combine(
                dataDirectory,
                "tbplayercombat.json");
            string attackPath = Path.Combine(
                dataDirectory,
                "tbcombatattack.json");

            if (!File.Exists(playerPath) ||
                !File.Exists(attackPath))
            {
                throw new FileNotFoundException(
                    "Luban combat JSON was not copied to the server output. " +
                    $"Expected: {playerPath}, {attackPath}");
            }

            using JsonDocument playerDocument =
                JsonDocument.Parse(File.ReadAllText(playerPath));
            JsonElement? playerRow = FindRowById(
                playerDocument.RootElement,
                playerId);
            if (!playerRow.HasValue)
            {
                throw new InvalidOperationException(
                    $"PlayerCombat Id={playerId} was not found in {playerPath}.");
            }

            PlayerCombatConfig defaults =
                CombatConfigDefaults.CreatePlayer();
            JsonElement player = playerRow.Value;
            PlayerCombatConfig result = new PlayerCombatConfig
            {
                Id = GetInt(player, defaults.Id, "Id", "id"),
                Name = GetString(player, defaults.Name, "Name", "name"),
                MaxHP = GetFix64(
                    player, defaults.MaxHP, "MaxHP", "maxHP", "maxHp"),
                MoveSpeed = GetFix64(
                    player, defaults.MoveSpeed, "MoveSpeed", "moveSpeed"),
                MoveInputDeadZone = GetFix64(
                    player,
                    defaults.MoveInputDeadZone,
                    "MoveInputDeadZone",
                    "moveInputDeadZone"),
                RotationSharpness = GetFix64(
                    player,
                    defaults.RotationSharpness,
                    "RotationSharpness",
                    "rotationSharpness"),
                MapMinX = GetFix64(
                    player, defaults.MapMinX, "MapMinX", "mapMinX"),
                MapMaxX = GetFix64(
                    player, defaults.MapMaxX, "MapMaxX", "mapMaxX"),
                MapMinZ = GetFix64(
                    player, defaults.MapMinZ, "MapMinZ", "mapMinZ"),
                MapMaxZ = GetFix64(
                    player, defaults.MapMaxZ, "MapMaxZ", "mapMaxZ"),
                HitStunDuration = GetFix64(
                    player,
                    defaults.HitStunDuration,
                    "HitStunDuration",
                    "hitStunDuration"),
                KnockbackFriction = GetFix64(
                    player,
                    defaults.KnockbackFriction,
                    "KnockbackFriction",
                    "knockbackFriction"),
                HurtBoxSize = GetVector(
                    player,
                    defaults.HurtBoxSize,
                    "HurtBoxSize",
                    "hurtBoxSize"),
                HurtBoxLocalOffset = GetVector(
                    player,
                    defaults.HurtBoxLocalOffset,
                    "HurtBoxLocalOffset",
                    "hurtBoxLocalOffset"),
                RequiredComboHits = GetInt(
                    player,
                    defaults.RequiredComboHits,
                    "RequiredComboHits",
                    "requiredComboHits"),
                ComboCounterWindowFrames = GetInt(
                    player,
                    defaults.ComboCounterWindowFrames,
                    "ComboCounterWindowFrames",
                    "comboCounterWindowFrames"),
                UltimateKnockbackDistanceScale = GetFix64(
                    player,
                    defaults.UltimateKnockbackDistanceScale,
                    "UltimateKnockbackDistanceScale",
                    "ultimateKnockbackDistanceScale"),
                UltimateHitNormalizedFallback = GetFix64(
                    player,
                    defaults.UltimateHitNormalizedFallback,
                    "UltimateHitNormalizedFallback",
                    "ultimateHitNormalizedFallback"),
                UltimateHitWindowFrames = GetInt(
                    player,
                    defaults.UltimateHitWindowFrames,
                    "UltimateHitWindowFrames",
                    "ultimateHitWindowFrames"),
                AllowSelfHitWhenNoTarget = GetBool(
                    player,
                    defaults.AllowSelfHitWhenNoTarget,
                    "AllowSelfHitWhenNoTarget",
                    "allowSelfHitWhenNoTarget")
            };

            using JsonDocument attackDocument =
                JsonDocument.Parse(File.ReadAllText(attackPath));
            System.Collections.Generic.List<CombatAttackConfig> rows =
                new System.Collections.Generic.List<CombatAttackConfig>();

            foreach (JsonElement row in Rows(attackDocument.RootElement))
            {
                if (GetInt(row, 0, "OwnerId", "ownerId") != playerId)
                {
                    continue;
                }

                AttackState state = GetAttackState(row);
                CombatAttackConfig attackDefaults =
                    defaults.FindAttack(state) ??
                    new CombatAttackConfig { AttackState = state };

                rows.Add(new CombatAttackConfig
                {
                    Id = GetString(
                        row, attackDefaults.Id, "Id", "id"),
                    OwnerId = GetInt(
                        row,
                        attackDefaults.OwnerId,
                        "OwnerId",
                        "ownerId"),
                    AttackState = state,
                    ActiveStartFrame = GetInt(
                        row,
                        attackDefaults.ActiveStartFrame,
                        "ActiveStartFrame",
                        "activeStartFrame"),
                    ActiveEndFrame = GetInt(
                        row,
                        attackDefaults.ActiveEndFrame,
                        "ActiveEndFrame",
                        "activeEndFrame"),
                    RecoveryFrames = GetInt(
                        row,
                        attackDefaults.RecoveryFrames,
                        "RecoveryFrames",
                        "recoveryFrames"),
                    Damage = GetFix64(
                        row,
                        attackDefaults.Damage,
                        "Damage",
                        "damage"),
                    KnockbackSpeed = GetFix64(
                        row,
                        attackDefaults.KnockbackSpeed,
                        "KnockbackSpeed",
                        "knockbackSpeed"),
                    ForwardMoveDistance = GetFix64(
                        row,
                        attackDefaults.ForwardMoveDistance,
                        "ForwardMoveDistance",
                        "forwardMoveDistance"),
                    ForwardMoveStartFrame = GetInt(
                        row,
                        attackDefaults.ForwardMoveStartFrame,
                        "ForwardMoveStartFrame",
                        "forwardMoveStartFrame"),
                    ForwardMoveEndFrame = GetInt(
                        row,
                        attackDefaults.ForwardMoveEndFrame,
                        "ForwardMoveEndFrame",
                        "forwardMoveEndFrame"),
                    AttackBoxSize = GetVector(
                        row,
                        attackDefaults.AttackBoxSize,
                        "AttackBoxSize",
                        "attackBoxSize"),
                    AttackBoxLocalOffset = GetVector(
                        row,
                        attackDefaults.AttackBoxLocalOffset,
                        "AttackBoxLocalOffset",
                        "attackBoxLocalOffset"),
                    MotionData = GetString(
                        row,
                        attackDefaults.MotionData,
                        "MotionData",
                        "motionData"),
                    MotionLandingFrame = GetInt(
                        row,
                        attackDefaults.MotionLandingFrame,
                        "MotionLandingFrame",
                        "motionLandingFrame"),
                    MotionLastHorizontalFrame = GetInt(
                        row,
                        attackDefaults.MotionLastHorizontalFrame,
                        "MotionLastHorizontalFrame",
                        "motionLastHorizontalFrame"),
                    MotionHorizontalDistance = GetFix64(
                        row,
                        attackDefaults.MotionHorizontalDistance,
                        "MotionHorizontalDistance",
                        "motionHorizontalDistance")
                });
            }

            attacks = rows.ToArray();
            return result;
        }

        private static JsonElement? FindRowById(
            JsonElement root,
            int id)
        {
            foreach (JsonElement row in Rows(root))
            {
                if (GetInt(row, 0, "Id", "id") == id)
                {
                    return row;
                }
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<JsonElement>
            Rows(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement row in root.EnumerateArray())
            {
                yield return row;
            }
        }

        private static AttackState GetAttackState(JsonElement row)
        {
            JsonElement? value = GetNode(
                row, "AttackState", "attackState");
            if (!value.HasValue)
            {
                return AttackState.None;
            }

            if (value.Value.ValueKind == JsonValueKind.Number &&
                value.Value.TryGetInt32(out int numeric))
            {
                return Enum.IsDefined(typeof(AttackState), numeric)
                    ? (AttackState)numeric
                    : AttackState.None;
            }

            return Enum.TryParse(
                value.Value.GetString(),
                true,
                out AttackState parsed)
                ? parsed
                : AttackState.None;
        }

        private static JsonElement? GetNode(
            JsonElement row,
            params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (row.TryGetProperty(names[i], out JsonElement value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string GetString(
            JsonElement row,
            string fallback,
            params string[] names)
        {
            JsonElement? value = GetNode(row, names);
            return value.HasValue &&
                   value.Value.ValueKind == JsonValueKind.String
                ? value.Value.GetString() ?? fallback
                : fallback;
        }

        private static int GetInt(
            JsonElement row,
            int fallback,
            params string[] names)
        {
            JsonElement? value = GetNode(row, names);
            return value.HasValue &&
                   value.Value.ValueKind == JsonValueKind.Number &&
                   value.Value.TryGetInt32(out int parsed)
                ? parsed
                : fallback;
        }

        private static bool GetBool(
            JsonElement row,
            bool fallback,
            params string[] names)
        {
            JsonElement? value = GetNode(row, names);
            return value.HasValue &&
                   (value.Value.ValueKind == JsonValueKind.True ||
                    value.Value.ValueKind == JsonValueKind.False)
                ? value.Value.GetBoolean()
                : fallback;
        }

        private static Fix64 GetFix64(
            JsonElement row,
            Fix64 fallback,
            params string[] names)
        {
            JsonElement? value = GetNode(row, names);
            if (!value.HasValue)
            {
                return fallback;
            }

            if (value.Value.ValueKind == JsonValueKind.Number)
            {
                return Fix64.FromDecimal(value.Value.GetDecimal());
            }

            if (value.Value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(
                    value.Value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out decimal parsed))
            {
                return Fix64.FromDecimal(parsed);
            }

            throw new FormatException(
                $"Invalid Fix64 JSON value: {value.Value.GetRawText()}");
        }

        private static LogicVector3 GetVector(
            JsonElement row,
            LogicVector3 fallback,
            params string[] names)
        {
            JsonElement? value = GetNode(row, names);
            if (!value.HasValue ||
                value.Value.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            JsonElement vector = value.Value;
            return new LogicVector3(
                GetFix64(vector, fallback.X, "x", "X"),
                GetFix64(vector, fallback.Y, "y", "Y"),
                GetFix64(vector, fallback.Z, "z", "Z"));
        }
    }
}

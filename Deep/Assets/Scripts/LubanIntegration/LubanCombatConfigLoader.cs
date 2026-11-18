using System;
using System.Collections.Generic;
using Combat.Config;
using Combat.Core;
using Luban.SimpleJSON;
using UnityEngine;

namespace LubanIntegration
{
    /// <summary>
    /// Luban JSON 到 Combat.Core POCO 的适配器。
    ///
    /// Combat.Core 不引用 Unity。这个类位于 Unity 程序集中，只负责从 Resources 加载
    /// gen.bat 生成的 JSON，并转换成服务端也能使用的纯配置对象。
    /// </summary>
    public static class LubanCombatConfigLoader
    {
        public static bool TryLoad(
            int playerId,
            out PlayerCombatConfig config)
        {
            return TryLoad(
                playerId,
                "Data/TbPlayerCombat",
                "Data/TbCombatAttack",
                out config);
        }

        public static bool TryLoad(
            int playerId,
            string playerTableResource,
            string attackTableResource,
            out PlayerCombatConfig config)
        {
            config = null;

            JSONNode playerRows = LoadTable(playerTableResource);
            if (playerRows == null)
            {
                return false;
            }

            JSONNode playerRow = FindRowById(playerRows, playerId);
            if (playerRow == null)
            {
                Debug.LogWarning(
                    $"Luban 中找不到 PlayerCombat Id={playerId}。");
                return false;
            }

            config = ReadPlayer(playerRow);
            JSONNode attackRows = LoadTable(attackTableResource);
            List<CombatAttackConfig> attacks = new List<CombatAttackConfig>();

            if (attackRows != null)
            {
                foreach (JSONNode attackRow in Rows(attackRows))
                {
                    int ownerId = GetInt(attackRow, "OwnerId", "ownerId");
                    if (ownerId == playerId)
                    {
                        attacks.Add(ReadAttack(attackRow));
                    }
                }
            }

            config.Attacks = attacks.ToArray();
            if (config.Attacks.Length == 0)
            {
                Debug.LogWarning(
                    $"Luban 中找不到 PlayerCombat Id={playerId} 对应的 CombatAttack。");
            }

            return true;
        }

        private static JSONNode LoadTable(string resourcePath)
        {
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                return null;
            }

            string normalizedPath = resourcePath
                .Replace('\\', '/')
                .TrimEnd('/');

            TextAsset asset = Resources.Load<TextAsset>(normalizedPath);
            if (asset == null)
            {
                // MiniTemplate 生成的默认文件名可能使用 cfg_tbplayercombat 这类形式。
                string folder = GetFolder(normalizedPath);
                string fileName = GetFileName(normalizedPath);
                asset = Resources.Load<TextAsset>(
                    $"{folder}/{fileName.ToLowerInvariant()}");
                if (asset == null)
                {
                    asset = Resources.Load<TextAsset>($"{folder}/cfg_{fileName}");
                }

                if (asset == null)
                {
                    asset = Resources.Load<TextAsset>(
                        $"{folder}/cfg_{fileName.ToLowerInvariant()}");
                }
            }

            if (asset == null)
            {
                return null;
            }

            try
            {
                return JSON.Parse(asset.text);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"解析 Luban JSON 失败：{normalizedPath}\n{exception}");
                return null;
            }
        }

        private static JSONNode FindRowById(JSONNode table, int id)
        {
            foreach (JSONNode row in Rows(table))
            {
                if (GetInt(row, "Id", "id") == id)
                {
                    return row;
                }
            }

            return null;
        }

        private static IEnumerable<JSONNode> Rows(JSONNode table)
        {
            if (table == null)
            {
                yield break;
            }

            JSONNode dataNode =
                GetNode(table, "Data", "data", "Rows", "rows");
            IEnumerable<JSONNode> children =
                (dataNode ?? table).Children ?? Array.Empty<JSONNode>();

            foreach (JSONNode node in children)
            {
                yield return node;
            }
        }

        private static PlayerCombatConfig ReadPlayer(JSONNode row)
        {
            PlayerCombatConfig defaults = CombatConfigDefaults.CreatePlayer();

            return new PlayerCombatConfig
            {
                Id = GetInt(row, defaults.Id, "Id", "id"),
                Name = GetString(row, defaults.Name, "Name", "name"),
                MaxHP = GetFix64(row, defaults.MaxHP, "MaxHP", "maxHP", "maxHp"),
                MoveSpeed = GetFix64(row, defaults.MoveSpeed, "MoveSpeed", "moveSpeed"),
                MoveInputDeadZone = GetFix64(
                    row,
                    defaults.MoveInputDeadZone,
                    "MoveInputDeadZone",
                    "moveInputDeadZone"),
                RotationSharpness = GetFix64(
                    row,
                    defaults.RotationSharpness,
                    "RotationSharpness",
                    "rotationSharpness"),
                MapMinX = GetFix64(row, defaults.MapMinX, "MapMinX", "mapMinX"),
                MapMaxX = GetFix64(row, defaults.MapMaxX, "MapMaxX", "mapMaxX"),
                MapMinZ = GetFix64(row, defaults.MapMinZ, "MapMinZ", "mapMinZ"),
                MapMaxZ = GetFix64(row, defaults.MapMaxZ, "MapMaxZ", "mapMaxZ"),
                HitStunDuration = GetFix64(
                    row,
                    defaults.HitStunDuration,
                    "HitStunDuration",
                    "hitStunDuration"),
                KnockbackFriction = GetFix64(
                    row,
                    defaults.KnockbackFriction,
                    "KnockbackFriction",
                    "knockbackFriction"),
                HurtBoxSize = GetVector(
                    row,
                    defaults.HurtBoxSize,
                    "HurtBoxSize",
                    "hurtBoxSize"),
                HurtBoxLocalOffset = GetVector(
                    row,
                    defaults.HurtBoxLocalOffset,
                    "HurtBoxLocalOffset",
                    "hurtBoxLocalOffset"),
                RequiredComboHits = GetInt(
                    row,
                    defaults.RequiredComboHits,
                    "RequiredComboHits",
                    "requiredComboHits"),
                ComboCounterWindowFrames = GetInt(
                    row,
                    defaults.ComboCounterWindowFrames,
                    "ComboCounterWindowFrames",
                    "comboCounterWindowFrames"),
                UltimateKnockbackDistanceScale = GetFix64(
                    row,
                    defaults.UltimateKnockbackDistanceScale,
                    "UltimateKnockbackDistanceScale",
                    "ultimateKnockbackDistanceScale"),
                UltimateHitNormalizedFallback = GetFix64(
                    row,
                    defaults.UltimateHitNormalizedFallback,
                    "UltimateHitNormalizedFallback",
                    "ultimateHitNormalizedFallback"),
                UltimateHitWindowFrames = GetInt(
                    row,
                    defaults.UltimateHitWindowFrames,
                    "UltimateHitWindowFrames",
                    "ultimateHitWindowFrames"),
                AllowSelfHitWhenNoTarget = GetBool(
                    row,
                    defaults.AllowSelfHitWhenNoTarget,
                    "AllowSelfHitWhenNoTarget",
                    "allowSelfHitWhenNoTarget")
            };
        }

        private static CombatAttackConfig ReadAttack(JSONNode row)
        {
            AttackState state = GetAttackState(row);
            PlayerCombatConfig defaultsOwner = CombatConfigDefaults.CreatePlayer();
            CombatAttackConfig defaults =
                defaultsOwner.FindAttack(state) ?? new CombatAttackConfig
                {
                    AttackState = state
                };

            return new CombatAttackConfig
            {
                Id = GetString(row, defaults.Id, "Id", "id"),
                OwnerId = GetInt(row, defaults.OwnerId, "OwnerId", "ownerId"),
                AttackState = state,
                ActiveStartFrame = GetInt(
                    row,
                    defaults.ActiveStartFrame,
                    "ActiveStartFrame",
                    "activeStartFrame"),
                ActiveEndFrame = GetInt(
                    row,
                    defaults.ActiveEndFrame,
                    "ActiveEndFrame",
                    "activeEndFrame"),
                RecoveryFrames = GetInt(
                    row,
                    defaults.RecoveryFrames,
                    "RecoveryFrames",
                    "recoveryFrames"),
                Damage = GetFix64(row, defaults.Damage, "Damage", "damage"),
                KnockbackSpeed = GetFix64(
                    row,
                    defaults.KnockbackSpeed,
                    "KnockbackSpeed",
                    "knockbackSpeed"),
                ForwardMoveDistance = GetFix64(
                    row,
                    defaults.ForwardMoveDistance,
                    "ForwardMoveDistance",
                    "forwardMoveDistance"),
                ForwardMoveStartFrame = GetInt(
                    row,
                    defaults.ForwardMoveStartFrame,
                    "ForwardMoveStartFrame",
                    "forwardMoveStartFrame"),
                ForwardMoveEndFrame = GetInt(
                    row,
                    defaults.ForwardMoveEndFrame,
                    "ForwardMoveEndFrame",
                    "forwardMoveEndFrame"),
                AttackBoxSize = GetVector(
                    row,
                    defaults.AttackBoxSize,
                    "AttackBoxSize",
                    "attackBoxSize"),
                AttackBoxLocalOffset = GetVector(
                    row,
                    defaults.AttackBoxLocalOffset,
                    "AttackBoxLocalOffset",
                    "attackBoxLocalOffset"),
                MotionData = GetString(
                    row,
                    defaults.MotionData,
                    "MotionData",
                    "motionData"),
                MotionLandingFrame = GetInt(
                    row,
                    defaults.MotionLandingFrame,
                    "MotionLandingFrame",
                    "motionLandingFrame"),
                MotionLastHorizontalFrame = GetInt(
                    row,
                    defaults.MotionLastHorizontalFrame,
                    "MotionLastHorizontalFrame",
                    "motionLastHorizontalFrame"),
                MotionHorizontalDistance = GetFix64(
                    row,
                    defaults.MotionHorizontalDistance,
                    "MotionHorizontalDistance",
                    "motionHorizontalDistance")
            };
        }

        private static AttackState GetAttackState(JSONNode row)
        {
            JSONNode node = GetNode(row, "AttackState", "attackState");
            if (node == null)
            {
                return AttackState.None;
            }

            string value = GetString(row, "AttackState", "attackState");
            if (Enum.TryParse(value, true, out AttackState parsed))
            {
                return parsed;
            }

            int integerValue = node.AsInt;
            return Enum.IsDefined(typeof(AttackState), integerValue)
                ? (AttackState)integerValue
                : AttackState.None;
        }

        private static JSONNode GetNode(JSONNode row, params string[] keys)
        {
            if (row == null)
            {
                return null;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                JSONNode value = row[keys[i]];
                if (value != null && !value.IsNull)
                {
                    return value;
                }
            }

            return null;
        }

        private static string GetString(JSONNode row, params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? node.Value : string.Empty;
        }

        private static string GetString(
            JSONNode row,
            string fallback,
            params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? node.Value : fallback;
        }

        private static int GetInt(JSONNode row, params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? node.AsInt : 0;
        }

        private static int GetInt(
            JSONNode row,
            int fallback,
            params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? node.AsInt : fallback;
        }

        private static Fix64 GetFix64(JSONNode row, params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? Fix64.Parse(node.Value) : Fix64.Zero;
        }

        private static Fix64 GetFix64(
            JSONNode row,
            Fix64 fallback,
            params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? Fix64.Parse(node.Value) : fallback;
        }

        private static bool GetBool(
            JSONNode row,
            bool fallback,
            params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            return node != null ? node.AsBool : fallback;
        }

        private static LogicVector3 GetVector(JSONNode row, params string[] keys)
        {
            return GetVector(row, LogicVector3.Zero, keys);
        }

        private static LogicVector3 GetVector(
            JSONNode row,
            LogicVector3 fallback,
            params string[] keys)
        {
            JSONNode node = GetNode(row, keys);
            if (node == null)
            {
                return fallback;
            }

            if (node.IsArray && node.Count >= 3)
            {
                return new LogicVector3(
                    Fix64.Parse(node[0].Value),
                    Fix64.Parse(node[1].Value),
                    Fix64.Parse(node[2].Value));
            }

            JSONNode x = GetNode(node, "X", "x");
            if (x != null)
            {
                return new LogicVector3(
                    GetFix64(node, "X", "x"),
                    GetFix64(node, "Y", "y"),
                    GetFix64(node, "Z", "z"));
            }

            string[] parts = node.Value.Split(',');
            if (parts.Length >= 3)
            {
                try
                {
                    return new LogicVector3(
                        Fix64.Parse(parts[0]),
                        Fix64.Parse(parts[1]),
                        Fix64.Parse(parts[2]));
                }
                catch (FormatException)
                {
                    return fallback;
                }
            }

            return fallback;
        }

        private static string GetFolder(string path)
        {
            int index = path.LastIndexOf('/');
            return index >= 0 ? path.Substring(0, index) : string.Empty;
        }

        private static string GetFileName(string path)
        {
            int index = path.LastIndexOf('/');
            return index >= 0 ? path.Substring(index + 1) : path;
        }
    }
}

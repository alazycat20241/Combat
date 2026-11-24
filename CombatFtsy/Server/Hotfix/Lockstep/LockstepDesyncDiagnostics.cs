using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Combat.Core;

namespace Hotfix.Lockstep
{
    /// <summary>
    /// 服务端为每个权威帧保留的轻量诊断记录。
    /// Snapshot 与 AuthorityHash 都来自同一个 Tick 结束点，便于定位分叉。
    /// </summary>
    internal sealed class AuthoritativeFrameRecord
    {
        public int Frame;
        public ulong AuthorityHash;
        public ulong StateHash;
        public KeyValuePair<
            int,
            LockstepRoomSystem.LockstepInput>[] Inputs =
            Array.Empty<KeyValuePair<
                int,
                LockstepRoomSystem.LockstepInput>>();
        public CombatWorld.Snapshot Snapshot = null!;
    }

    internal static class LockstepDesyncDiagnostics
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true,
                IgnoreReadOnlyProperties = true
            };

        public static string Write(
            int currentFrame,
            int playerId,
            ulong clientHash,
            IReadOnlyDictionary<int, ulong> knownClientHashes,
            AuthoritativeFrameRecord authority)
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory,
                "Logs",
                "LockstepDesync");
            Directory.CreateDirectory(directory);

            DesyncReport report = new DesyncReport
            {
                CreatedAtUtc = DateTime.UtcNow.ToString("O"),
                RulesetVersion = LockstepRoomSystem.RulesetVersion,
                CurrentServerFrame = currentFrame,
                Frame = authority.Frame,
                MismatchingPlayerId = playerId,
                MismatchingClientHash = clientHash,
                ServerAuthorityHash = authority.AuthorityHash,
                ServerStateHash = authority.StateHash
            };

            foreach (KeyValuePair<int, ulong> pair in knownClientHashes)
            {
                report.ClientHashes.Add(new ClientHashRecord
                {
                    PlayerId = pair.Key,
                    WorldHash = pair.Value
                });
            }

            for (int i = 0; i < authority.Inputs.Length; i++)
            {
                KeyValuePair<
                    int,
                    LockstepRoomSystem.LockstepInput> input =
                    authority.Inputs[i];
                report.Inputs.Add(new InputRecord
                {
                    PlayerId = input.Key,
                    InputMask = input.Value.InputMask,
                    TargetEntityId = input.Value.TargetEntityId
                });
            }

            for (int i = 0; i < authority.Snapshot.Entities.Length; i++)
            {
                report.Players.Add(
                    authority.Snapshot.Entities[i].ToData());
            }

            string fileName =
                $"desync-frame-{authority.Frame:D8}-" +
                $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json";
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(report, JsonOptions),
                new UTF8Encoding(false));
            return path;
        }

        private sealed class DesyncReport
        {
            public string CreatedAtUtc = string.Empty;
            public string RulesetVersion = string.Empty;
            public int CurrentServerFrame;
            public int Frame;
            public int MismatchingPlayerId;
            public ulong MismatchingClientHash;
            public ulong ServerAuthorityHash;
            public ulong ServerStateHash;
            public List<ClientHashRecord> ClientHashes = new();
            public List<InputRecord> Inputs = new();
            public List<PlayerEntity.SnapshotData> Players = new();
        }

        private sealed class ClientHashRecord
        {
            public int PlayerId;
            public ulong WorldHash;
        }

        private sealed class InputRecord
        {
            public int PlayerId;
            public int InputMask;
            public int TargetEntityId;
        }
    }
}

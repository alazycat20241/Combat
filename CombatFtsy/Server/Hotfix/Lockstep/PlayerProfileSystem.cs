using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Fantasy;
using Fantasy.Network;
using Fantasy.Network.Interface;

namespace Hotfix.Lockstep
{
    /// <summary>
    /// 当前原型的进程内玩家资料系统。
    ///
    /// 它不是正式账号系统：没有密码和数据库，只保存昵称、ProfileId
    /// 与恢复 Token。以后接数据库时，协议和匹配系统不需要重写。
    /// </summary>
    public static class PlayerProfileSystem
    {
        public sealed class Profile
        {
            public long ProfileId;
            public string DisplayName = string.Empty;
            public string TokenHash = string.Empty;
        }

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<long, Profile> Profiles =
            new Dictionary<long, Profile>();
        private static readonly Dictionary<string, long> ProfileIdsByTokenHash =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<long, long> ProfileIdsBySessionId =
            new Dictionary<long, long>();
        private static long nextProfileId = 100000;

        public static bool TryRegister(
            Session session,
            string requestedName,
            string clientProfileToken,
            out Profile? profile,
            out string profileToken,
            out string error)
        {
            profile = null;
            profileToken = string.Empty;
            error = string.Empty;

            if (session == null || session.IsDisposed)
            {
                error = "Session is not available.";
                return false;
            }

            string displayName = (requestedName ?? string.Empty).Trim();
            if (!IsValidDisplayName(displayName, out error))
            {
                return false;
            }

            lock (SyncRoot)
            {
                long sessionProfileId;
                if (ProfileIdsBySessionId.TryGetValue(
                        session.Id,
                        out sessionProfileId) &&
                    Profiles.TryGetValue(
                        sessionProfileId,
                        out Profile? existingSessionProfile))
                {
                    profile = Clone(existingSessionProfile);
                    profileToken = RotateTokenLocked(existingSessionProfile);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(clientProfileToken))
                {
                    string suppliedHash =
                        HashToken(clientProfileToken);
                    if (ProfileIdsByTokenHash.TryGetValue(
                            suppliedHash,
                            out long restoredProfileId) &&
                        Profiles.TryGetValue(
                            restoredProfileId,
                            out Profile? restoredProfile))
                    {
                        ProfileIdsBySessionId[session.Id] =
                            restoredProfileId;
                        profile = Clone(restoredProfile);
                        profileToken = RotateTokenLocked(restoredProfile);
                        return true;
                    }
                }

                string newToken = GenerateToken();
                Profile created = new Profile
                {
                    ProfileId = ++nextProfileId,
                    DisplayName = displayName,
                    TokenHash = HashToken(newToken)
                };
                Profiles.Add(created.ProfileId, created);
                ProfileIdsByTokenHash.Add(created.TokenHash, created.ProfileId);
                ProfileIdsBySessionId.Add(session.Id, created.ProfileId);

                profile = Clone(created);
                profileToken = newToken;
                Log.Info(
                    $"Registered ProfileId={created.ProfileId}, " +
                    $"DisplayName={created.DisplayName}, " +
                    $"SessionId={session.Id}.");
                return true;
            }
        }

        public static bool TryGetBySession(
            Session session,
            out Profile? profile)
        {
            profile = null;
            if (session == null || session.IsDisposed)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!ProfileIdsBySessionId.TryGetValue(
                        session.Id,
                        out long profileId) ||
                    !Profiles.TryGetValue(
                        profileId,
                        out Profile? storedProfile))
                {
                    return false;
                }

                profile = Clone(storedProfile);
                return true;
            }
        }

        public static bool TryGetById(
            long profileId,
            out Profile? profile)
        {
            profile = null;
            lock (SyncRoot)
            {
                if (!Profiles.TryGetValue(
                        profileId,
                        out Profile? storedProfile))
                {
                    return false;
                }

                profile = Clone(storedProfile);
                return true;
            }
        }

        public static void ForgetSession(Session session)
        {
            if (session == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                ProfileIdsBySessionId.Remove(session.Id);
            }
        }

        private static bool IsValidDisplayName(
            string displayName,
            out string error)
        {
            if (displayName.Length < 2 || displayName.Length > 16)
            {
                error = "昵称长度必须为 2 到 16 个字符。";
                return false;
            }

            for (int i = 0; i < displayName.Length; i++)
            {
                if (char.IsControl(displayName[i]))
                {
                    error = "昵称不能包含控制字符。";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static string RotateTokenLocked(Profile profile)
        {
            ProfileIdsByTokenHash.Remove(profile.TokenHash);
            string token = GenerateToken();
            profile.TokenHash = HashToken(token);
            ProfileIdsByTokenHash[profile.TokenHash] = profile.ProfileId;
            return token;
        }

        private static Profile Clone(Profile profile)
        {
            return new Profile
            {
                ProfileId = profile.ProfileId,
                DisplayName = profile.DisplayName,
                TokenHash = profile.TokenHash
            };
        }

        internal static string GenerateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        internal static string HashToken(string token)
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash);
        }
    }

    /// <summary>
    /// 客户端的昵称注册消息。
    /// </summary>
    public sealed class C2G_RegisterProfileHandler
        : Message<C2G_RegisterProfile>
    {
        protected override async Fantasy.Async.FTask Run(
            Session session,
            C2G_RegisterProfile message)
        {
            bool success = PlayerProfileSystem.TryRegister(
                session,
                message.displayName,
                message.clientProfileToken,
                out PlayerProfileSystem.Profile? profile,
                out string profileToken,
                out string error);

            G2C_ProfileRegistered response =
                G2C_ProfileRegistered.Create();
            response.success = success;
            response.message = success ? "注册成功。" : error;

            if (success && profile != null)
            {
                response.profileId = profile.ProfileId;
                response.displayName = profile.DisplayName;
                response.profileToken = profileToken;
            }

            session.Send(response);
            await Fantasy.Async.FTask.CompletedTask;
        }
    }
}

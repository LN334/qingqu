using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZhifuWater.Core;

/// <summary>登录结果。</summary>
public sealed class LoginResult
{
    public string Token = "";
    public JsonObject User = new();
    public JsonObject Session = new();
}

/// <summary>
/// 登录、会话与权限。权限按「功能权限 + 数据范围」双重校验。对齐 <c>server/auth.js</c>。
/// </summary>
public sealed class Auth
{
    public const long SESSION_TTL_MS = 12L * 3600 * 1000;
    public const long TOUCH_INTERVAL_MS = 5L * 60 * 1000;
    private const int MaxSessions = 200;
    private const int MaxAttemptsBeforeLock = 5;
    private const long LockMs = 60_000;

    private readonly Store _store;
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    public Auth(Store store)
    {
        _store = store;
        CleanupSessions();
    }

    private JsonObject Db => _store.Data;

    private sealed class Attempt
    {
        public int Count;
        public long LockedUntil;
    }

    public void CleanupSessions()
    {
        long now = TimeUtil.Now();
        var sessions = J.A(Db, "sessions");
        var before = sessions.Count;
        var kept = J.Objs(sessions)
            .Where(s => !double.IsNaN(TimeUtil.Parse(s, "expiresAt")) && TimeUtil.Parse(s, "expiresAt") > now)
            .ToList();
        if (kept.Count != before) ReplaceSessions(kept);
    }

    private void ReplaceSessions(List<JsonObject> kept)
    {
        // 注意：JsonNode 只能有一个 parent，直接 Add 已在旧数组里的节点会抛
        // "The node already has a parent."，因此这里统一放深拷贝。
        var arr = new JsonArray();
        foreach (var s in kept) arr.Add(s.DeepClone());
        Db["sessions"] = arr;
        _store.SaveSoon();
    }

    public JsonObject RoleOf(JsonObject user)
    {
        var roles = Db["roles"] as JsonObject;
        var key = J.Str(user, "role");
        if (roles is not null && key.Length > 0 && roles[key] is JsonObject role) return role;
        return new JsonObject
        {
            ["perms"] = new JsonArray(),
            ["sites"] = "assigned",
            ["name"] = J.Str(user, "role"),
        };
    }

    public List<string> SiteScope(JsonObject user)
    {
        var role = RoleOf(user);
        if (J.Str(role, "sites") == "all" || J.Str(user, "role") == "super")
            return J.ObjsIn(Db, "sites").Select(s => J.Str(s, "id")).ToList();
        return J.Strings(user["siteIds"]);
    }

    public List<string> Permissions(JsonObject user) => J.Strings(RoleOf(user)["perms"]);

    public bool Can(JsonObject? user, string perm) =>
        user is not null && Permissions(user).Contains(perm);

    public bool CanSite(JsonObject? user, string? siteId) =>
        user is not null && siteId is not null && SiteScope(user).Contains(siteId);

    /// <summary>用户视图：去掉 password，附 roleName/perms/siteScope。</summary>
    public JsonObject? Sanitize(JsonObject? user)
    {
        if (user is null) return null;
        var role = RoleOf(user);
        var view = (JsonObject)user.DeepClone();
        view.Remove("password");
        view["roleName"] = J.Str(role, "name");
        var perms = new JsonArray();
        foreach (var p in Permissions(user)) perms.Add(p);
        view["perms"] = perms;
        var scope = new JsonArray();
        foreach (var s in SiteScope(user)) scope.Add(s);
        view["siteScope"] = scope;
        return view;
    }

    /// <summary>登录：5 次失败锁定 60 秒；成功返回 token 与用户视图。</summary>
    public LoginResult Login(string? username, string? password, string? ip = "", string? agent = "")
    {
        var key = (username ?? "").Trim().ToLowerInvariant();
        long now = TimeUtil.Now();

        if (_attempts.TryGetValue(key, out var rec) && rec.LockedUntil > now)
        {
            var wait = (long)Math.Ceiling((rec.LockedUntil - now) / 1000.0);
            throw new CoreException(429, $"尝试次数过多，请 {wait} 秒后再试。", "LOCKED");
        }

        var user = J.ObjsIn(Db, "users")
            .FirstOrDefault(u => J.Str(u, "username").ToLowerInvariant() == key);

        if (user is null || !Scrypt.VerifyPassword(password, J.StrOrNull(user, "password")))
        {
            int count = (rec?.Count ?? 0) + 1;
            long lockedUntil = count >= MaxAttemptsBeforeLock ? now + LockMs : 0;
            _attempts[key] = new Attempt
            {
                Count = count >= MaxAttemptsBeforeLock ? 0 : count,
                LockedUntil = lockedUntil,
            };
            throw new CoreException(401, "账号或密码不正确。", "BAD_CREDENTIALS");
        }

        if (J.Str(user, "status") != "active")
            throw new CoreException(403, "账号已停用，请联系管理员。", "INACTIVE");

        _attempts.Remove(key);

        var raw = Scrypt.Token(24);
        var session = new JsonObject
        {
            ["id"] = "sess-" + raw[..12],
            ["tokenHash"] = Scrypt.Sha256Hex(raw),
            ["userId"] = J.Str(user, "id"),
            ["createdAt"] = TimeUtil.Iso(now),
            ["expiresAt"] = TimeUtil.Iso(now + SESSION_TTL_MS),
            ["lastSeenAt"] = TimeUtil.Iso(now),
            ["ip"] = ip ?? "",
            ["agent"] = J.Sliced(agent ?? "", 160),
        };

        var kept = J.ObjsIn(Db, "sessions")
            .Where(s => J.Str(s, "userId") != J.Str(user, "id")
                        || TimeUtil.Parse(s, "expiresAt") > now)
            .ToList();
        kept.Add(session);
        if (kept.Count > MaxSessions) kept = kept.Skip(kept.Count - MaxSessions).ToList();
        ReplaceSessions(kept);

        user["lastLoginAt"] = TimeUtil.Iso(now);
        _store.SaveSoon();

        return new LoginResult { Token = raw, User = Sanitize(user)!, Session = (JsonObject)session.DeepClone() };
    }

    public void Logout(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return;
        var h = Scrypt.Sha256Hex(rawToken);
        var sessions = J.A(Db, "sessions");
        var before = sessions.Count;
        var kept = J.Objs(sessions).Where(s => J.Str(s, "tokenHash") != h).ToList();
        if (kept.Count != before) ReplaceSessions(kept);
    }

    /// <summary>注销某用户的全部会话（停用账号、重置口令后调用）。</summary>
    public void LogoutAllFor(string userId)
    {
        var sessions = J.A(Db, "sessions");
        var before = sessions.Count;
        var kept = J.Objs(sessions).Where(s => J.Str(s, "userId") != userId).ToList();
        if (kept.Count != before) ReplaceSessions(kept);
    }

    /// <summary>从 token 解析用户；同时续期 lastSeenAt。</summary>
    public JsonObject? UserFromToken(string? rawToken, bool touch = true)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        var h = Scrypt.Sha256Hex(rawToken);
        var s = J.ObjsIn(Db, "sessions").FirstOrDefault(x => J.Str(x, "tokenHash") == h);
        if (s is null) return null;
        long now = TimeUtil.Now();
        var exp = TimeUtil.Parse(s, "expiresAt");
        if (double.IsNaN(exp) || exp <= now) return null;
        var user = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(s, "userId"));
        if (user is null || J.Str(user, "status") != "active") return null;

        if (touch)
        {
            var lastSeen = TimeUtil.Parse(s, "lastSeenAt");
            if (double.IsNaN(lastSeen) || now - lastSeen > TOUCH_INTERVAL_MS)
            {
                s["lastSeenAt"] = TimeUtil.Iso(now);
                _store.SaveSoon();
            }
        }
        return user;
    }

    /// <summary>等价 <c>rawToken(req)</c>：优先 Bearer，其次 zfs_session Cookie。</summary>
    public static string RawToken(string? authorization, string? cookie)
    {
        var auth = authorization ?? "";
        if (Regex.IsMatch(auth, @"^Bearer\s+", RegexOptions.IgnoreCase))
            return Regex.Replace(auth, @"^Bearer\s+", "", RegexOptions.IgnoreCase).Trim();

        var c = cookie ?? "";
        var m = Regex.Match(c, @"(?:^|;\s*)zfs_session=([^;]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "";
    }

    /// <summary>测试/管理用途：清空失败计数。</summary>
    public void ResetAttempts() => _attempts.Clear();

    public int ActiveSessionCount() => J.ObjsIn(Db, "sessions").Count;
}

using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 告警查询/处置/规则维护。对齐 <c>server/api/alarms.js</c>（同一个 partial 类里的接口层）。
/// 返回的 JsonObject/JsonArray 就是 Node 版 HTTP 响应的 <c>data</c> 部分；错误抛 <see cref="CoreException"/>。
/// </summary>
public sealed partial class RulesEngine
{
    private JsonObject? FindAlarm(string id) => J.ObjsIn(Db, "alarms").FirstOrDefault(a => J.Str(a, "id") == id);

    private string UserName(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return "system";
        var u = J.ObjsIn(Db, "users").FirstOrDefault(x => J.Str(x, "id") == userId);
        var name = u is null ? "" : J.Str(u, "name");
        return string.IsNullOrEmpty(name) ? userId! : name;
    }

    private static string FirstNonEmpty(params string?[] vals) => J.FirstNonEmpty(vals);

    /// <summary>告警视图：补全等级名、单元名、站点名、点位名、处理人名、持续分钟。</summary>
    public JsonObject AlarmView(JsonObject a)
    {
        var siteId = J.Str(a, "siteId");
        var point = J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "id") == J.Str(a, "pointId"));
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        var unit = J.ObjsIn(Db, "units").FirstOrDefault(u => J.Str(u, "siteId") == siteId && J.Str(u, "code") == J.Str(a, "unitCode"));
        var level = J.Str(a, "level");
        var o = (JsonObject)a.DeepClone();
        o["levelName"] = LEVEL_NAME.TryGetValue(level, out var ln) ? ln : level;
        o["unitName"] = unit is null ? J.Str(a, "unitCode") : J.Str(unit, "name");
        o["unitShort"] = unit is null ? J.Str(a, "unitCode") : J.Str(unit, "short");
        o["siteName"] = site is null
            ? siteId
            : FirstNonEmpty(J.Str(site, "shortName"), J.Str(site, "name"), siteId);
        o["pointName"] = point is null ? J.Str(a, "pointCode") : J.Str(point, "name");
        o["pointUnit"] = point is null ? "" : J.Str(point, "unit");

        var ackBy = J.StrOrNull(a, "ackBy");
        o["ackByName"] = string.IsNullOrEmpty(ackBy)
            ? null
            : J.ObjsIn(Db, "users").Where(u => J.Str(u, "id") == ackBy).Select(u => J.Str(u, "name"))
                .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? ackBy;

        // 注意：Node 版这里是 a.resolvedBy ? (name || resolvedBy) : (resolvedBy==='system' ? '系统自动' : null)，
        // 'system' 走的是第一个分支，所以系统自动恢复的告警 resolvedByName 仍是 'system'。此处逐字保留该行为。
        var resolvedBy = J.StrOrNull(a, "resolvedBy");
        o["resolvedByName"] = string.IsNullOrEmpty(resolvedBy)
            ? null
            : J.ObjsIn(Db, "users").Where(u => J.Str(u, "id") == resolvedBy).Select(u => J.Str(u, "name"))
                .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? resolvedBy;

        var firstAt = TimeUtil.Parse(a, "firstAt");
        if (double.IsNaN(firstAt))
        {
            o["durationMin"] = null;
        }
        else
        {
            var end = J.StrOrNull(a, "resolvedAt");
            var endMs = TimeUtil.Parse(end);
            if (double.IsNaN(endMs)) endMs = TimeUtil.Now();
            o["durationMin"] = Math.Max(0, JsMath.RoundHalfUp((endMs - firstAt) / 60000.0));
        }
        return o;
    }

    /* ---------------- 列表 ---------------- */

    /// <summary>告警列表（分页）。对应 <c>GET /api/alarms</c>。</summary>
    public JsonObject ListAlarms(JsonObject? user, string? siteId = null, string? status = null,
        string? level = null, string? unitCode = null, string? from = null, string? to = null,
        string? q = null, int? page = null, int? pageSize = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "alarms").Where(a => siteIds.Contains(J.Str(a, "siteId"))).ToList();

        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(a => J.Str(a, "siteId") == siteId).ToList();
        }
        if (status == "open")
            items = items.Where(a => J.Str(a, "status") is "active" or "acknowledged").ToList();
        else if (!string.IsNullOrEmpty(status))
            items = items.Where(a => J.Str(a, "status") == status).ToList();
        if (!string.IsNullOrEmpty(level)) items = items.Where(a => J.Str(a, "level") == level).ToList();
        if (!string.IsNullOrEmpty(unitCode)) items = items.Where(a => J.Str(a, "unitCode") == unitCode).ToList();
        if (!string.IsNullOrEmpty(from))
        {
            var f = Access.ParseTime(from, 0);
            items = items.Where(a => TimeUtil.Parse(a, "firstAt") >= f).ToList();
        }
        if (!string.IsNullOrEmpty(to))
        {
            var t = Access.ParseTime(to, long.MaxValue);
            items = items.Where(a => TimeUtil.Parse(a, "firstAt") <= t).ToList();
        }
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(a =>
                $"{J.Str(a, "id")} {J.Str(a, "title")} {J.Str(a, "detail")} {J.Str(a, "pointCode")}"
                    .ToLowerInvariant().Contains(needle)).ToList();
        }

        bool IsOpen(JsonObject a) => J.Str(a, "status") is "active" or "acknowledged";
        var counts = new JsonObject
        {
            ["all"] = items.Count,
            ["active"] = items.Count(a => J.Str(a, "status") == "active"),
            ["acknowledged"] = items.Count(a => J.Str(a, "status") == "acknowledged"),
            ["resolved"] = items.Count(a => J.Str(a, "status") == "resolved"),
            ["closed"] = items.Count(a => J.Str(a, "status") == "closed"),
            ["critical"] = items.Count(a => J.Str(a, "level") == "critical" && IsOpen(a)),
            ["major"] = items.Count(a => J.Str(a, "level") == "major" && IsOpen(a)),
        };

        items = items.OrderByDescending(a => TimeUtil.Parse(a, "firstAt")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var arr = new JsonArray();
        foreach (var a in pg.Items) arr.Add(AlarmView(a));
        var levels = new JsonArray();
        foreach (var k in LEVELS) levels.Add(new JsonObject { ["key"] = k, ["name"] = LEVEL_NAME[k] });

        return new JsonObject
        {
            ["items"] = arr,
            ["total"] = pg.Total,
            ["page"] = pg.PageNo,
            ["pageSize"] = pg.PageSize,
            ["pages"] = pg.Pages,
            ["counts"] = counts,
            ["levels"] = levels,
        };
    }

    /// <summary>告警统计（按天/等级/单元/规则 + MTTA/MTTR/确认率）。对应 <c>GET /api/alarms/stats</c>。</summary>
    public JsonObject AlarmStats(JsonObject? user, string? siteId = null, int? days = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        int d = Math.Min(90, Math.Max(1, days ?? 7));
        long now = TimeUtil.Now();
        long from = TimeUtil.StartOfDay(now - (d - 1) * 86400000L);
        var list = J.ObjsIn(Db, "alarms")
            .Where(a => J.Str(a, "siteId") == sid && TimeUtil.Parse(a, "firstAt") >= from)
            .ToList();

        var byDay = new JsonArray();
        for (int i = d - 1; i >= 0; i--)
        {
            long t = TimeUtil.StartOfDay(now - i * 86400000L);
            var key = TimeUtil.DayKey(t);
            var inDay = list.Where(a => TimeUtil.DayKey((long)TimeUtil.Parse(a, "firstAt")) == key).ToList();
            byDay.Add(new JsonObject
            {
                ["day"] = key,
                ["total"] = inDay.Count,
                ["critical"] = inDay.Count(a => J.Str(a, "level") == "critical"),
                ["major"] = inDay.Count(a => J.Str(a, "level") == "major"),
                ["minor"] = inDay.Count(a => J.Str(a, "level") == "minor"),
                ["info"] = inDay.Count(a => J.Str(a, "level") == "info"),
            });
        }

        var acked = list.Where(a => !string.IsNullOrEmpty(J.StrOrNull(a, "ackAt"))).ToList();
        var resolved = list.Where(a => !string.IsNullOrEmpty(J.StrOrNull(a, "resolvedAt"))).ToList();
        long? Avg(IEnumerable<double> xs)
        {
            var arr = xs.ToList();
            return arr.Count == 0 ? null : (long?)JsMath.RoundHalfUp(arr.Sum() / arr.Count);
        }

        var byUnit = new JsonObject();
        foreach (var a in list)
        {
            var k = J.Str(a, "unitCode");
            byUnit[k] = (byUnit[k]?.GetValue<int>() ?? 0) + 1;
        }
        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in list)
        {
            var rid = J.StrOrNull(a, "ruleId");
            if (string.IsNullOrEmpty(rid)) continue;
            byRule[rid] = byRule.GetValueOrDefault(rid) + 1;
        }
        var topRules = new JsonArray();
        foreach (var kv in byRule.OrderByDescending(kv => kv.Value).Take(5))
        {
            var rule = J.ObjsIn(Db, "alarmRules").FirstOrDefault(r => J.Str(r, "id") == kv.Key);
            topRules.Add(new JsonObject
            {
                ["ruleId"] = kv.Key,
                ["name"] = rule is null ? kv.Key : J.Str(rule, "name"),
                ["count"] = kv.Value,
            });
        }

        var byLevel = new JsonObject();
        foreach (var k in LEVELS) byLevel[k] = list.Count(a => J.Str(a, "level") == k);

        return new JsonObject
        {
            ["siteId"] = sid,
            ["days"] = d,
            ["from"] = TimeUtil.Iso(from),
            ["total"] = list.Count,
            ["byDay"] = byDay,
            ["byLevel"] = byLevel,
            ["byUnit"] = byUnit,
            ["topRules"] = topRules,
            ["mttaMin"] = Avg(acked.Select(a => (TimeUtil.Parse(a, "ackAt") - TimeUtil.Parse(a, "firstAt")) / 60000.0)),
            ["mttrMin"] = Avg(resolved.Select(a => (TimeUtil.Parse(a, "resolvedAt") - TimeUtil.Parse(a, "firstAt")) / 60000.0)),
            ["ackRate"] = list.Count > 0 ? JsMath.Round(acked.Count * 100.0 / list.Count, 1) : 0,
            ["open"] = list.Count(a => J.Str(a, "status") is "active" or "acknowledged"),
        };
    }

    /* ---------------- 详情 ---------------- */

    /// <summary>告警详情：规则/点位/设备摘要 + 实时读数 + 关联工单 + 时间线。对应 <c>GET /api/alarms/:id</c>。</summary>
    public JsonObject AlarmDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var a = FindAlarm(id) ?? throw new CoreException(404, "告警不存在", "ALARM_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(a, "siteId"));
        var ruleId = J.StrOrNull(a, "ruleId");
        var rule = string.IsNullOrEmpty(ruleId)
            ? null
            : J.ObjsIn(Db, "alarmRules").FirstOrDefault(r => J.Str(r, "id") == ruleId);
        var point = J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "id") == J.Str(a, "pointId"));
        var device = point is null
            ? null
            : J.ObjsIn(Db, "devices").FirstOrDefault(x => J.Str(x, "id") == J.Str(point, "deviceId"));
        var workorders = new JsonArray();
        foreach (var w in J.ObjsIn(Db, "workorders").Where(w => J.Str(w, "alarmId") == J.Str(a, "id")))
            workorders.Add(w.DeepClone());
        var audits = J.ObjsIn(Db, "auditLogs")
            .Where(x => J.Str(x, "targetId") == J.Str(a, "id"))
            .OrderBy(x => TimeUtil.Parse(x, "at"))
            .ToList();

        var timeline = new List<JsonObject>();
        void Add(string? at, string action, string by, string? note)
        {
            if (string.IsNullOrEmpty(at)) return;
            timeline.Add(new JsonObject { ["at"] = at, ["action"] = action, ["by"] = by, ["note"] = note });
        }

        Add(J.StrOrNull(a, "firstAt"), "create", "规则引擎", $"触发条件：{J.Str(a, "detail")}");
        Add(J.StrOrNull(a, "ackAt"), "ack", UserName(J.StrOrNull(a, "ackBy")),
            string.IsNullOrEmpty(J.StrOrNull(a, "ackNote")) ? "已确认" : J.Str(a, "ackNote"));
        Add(J.StrOrNull(a, "escalatedAt"), "escalate", "系统", $"未按时确认，升级为{LEVEL_NAME.GetValueOrDefault(J.Str(a, "level"), J.Str(a, "level"))}");
        var resolvedBy = J.StrOrNull(a, "resolvedBy");
        Add(J.StrOrNull(a, "resolvedAt"), "resolve",
            resolvedBy == "system" ? "系统自动" : UserName(resolvedBy),
            string.IsNullOrEmpty(J.StrOrNull(a, "closeNote")) ? "标记恢复" : J.Str(a, "closeNote"));
        Add(J.StrOrNull(a, "closedAt"), "close", UserName(J.StrOrNull(a, "closedBy")),
            string.IsNullOrEmpty(J.StrOrNull(a, "closeNote")) ? "关闭" : J.Str(a, "closeNote"));
        foreach (var au in audits)
        {
            var act = J.Str(au, "action");
            if (act is "alarm.create" or "alarm.ack" or "alarm.resolve") continue;
            var actorName = J.StrOrNull(au, "actorName");
            timeline.Add(new JsonObject
            {
                ["at"] = J.Str(au, "at"),
                ["action"] = act,
                ["by"] = string.IsNullOrEmpty(actorName) ? "系统" : actorName,
                ["note"] = J.StrOrNull(au, "detail"),
            });
        }
        timeline = timeline.OrderBy(x => TimeUtil.Parse(x, "at")).ToList();

        var reading = point is null ? null : _telemetry.GetLatest(J.Str(point, "id"));
        var result = AlarmView(a);
        result["rule"] = rule is null ? null : new JsonObject
        {
            ["id"] = J.Str(rule, "id"),
            ["key"] = J.Str(rule, "key"),
            ["name"] = J.Str(rule, "name"),
            ["op"] = J.Str(rule, "op"),
            ["low"] = J.Get(rule, "low")?.DeepClone(),
            ["high"] = J.Get(rule, "high")?.DeepClone(),
            ["stateValues"] = J.Get(rule, "stateValues")?.DeepClone(),
            ["durationSec"] = J.Get(rule, "durationSec")?.DeepClone(),
            ["hysteresis"] = J.Get(rule, "hysteresis")?.DeepClone(),
            ["escalateSec"] = J.Get(rule, "escalateSec")?.DeepClone(),
            ["threshold"] = ThresholdText(rule),
        };
        result["point"] = point is null ? null : new JsonObject
        {
            ["id"] = J.Str(point, "id"),
            ["code"] = J.Str(point, "code"),
            ["name"] = J.Str(point, "name"),
            ["unit"] = J.Str(point, "unit"),
            ["range"] = J.Get(point, "range")?.DeepClone(),
            ["accuracy"] = J.Get(point, "accuracy")?.DeepClone(),
            ["provenance"] = J.Get(point, "provenance")?.DeepClone(),
            ["desc"] = J.Str(point, "desc"),
        };
        result["device"] = device is null ? null : new JsonObject
        {
            ["id"] = J.Str(device, "id"),
            ["code"] = J.Str(device, "code"),
            ["name"] = J.Str(device, "name"),
            ["model"] = J.Str(device, "model"),
            ["status"] = J.Str(device, "status"),
        };
        result["reading"] = reading is null ? null : new JsonObject
        {
            ["value"] = J.Get(reading, "v")?.DeepClone(),
            ["t"] = TimeUtil.IsoOrNull(reading, "t"),
            ["quality"] = J.Get(reading, "quality")?.DeepClone(),
        };
        result["workorders"] = workorders;
        var tl = new JsonArray();
        foreach (var x in timeline) tl.Add(x);
        result["timeline"] = tl;
        return result;
    }

    /* ---------------- 处置 ---------------- */

    /// <summary>确认告警。对应 <c>POST /api/alarms/:id/ack</c>（权限 alarm.handle）。</summary>
    public JsonObject AckAlarm(JsonObject? user, string id, string? note = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "alarm.handle");
        var a = FindAlarm(id) ?? throw new CoreException(404, "告警不存在", "ALARM_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(a, "siteId"));
        var updated = Acknowledge(u, id, J.Sliced(note ?? "", 300), ip);
        return AlarmView(updated);
    }

    /// <summary>标记恢复。对应 <c>POST /api/alarms/:id/resolve</c>，恢复依据必填。</summary>
    public JsonObject ResolveAlarm(JsonObject? user, string id, string? note = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "alarm.handle");
        var a = FindAlarm(id) ?? throw new CoreException(404, "告警不存在", "ALARM_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(a, "siteId"));
        var text = note ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw new CoreException(400, "请填写恢复依据或处置说明", "NOTE_REQUIRED");
        var updated = Resolve(u, id, J.Sliced(text, 300), ip);
        return AlarmView(updated);
    }

    /// <summary>关闭告警。对应 <c>POST /api/alarms/:id/close</c>，处置说明必填。</summary>
    public JsonObject CloseAlarm(JsonObject? user, string id, string? note = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "alarm.handle");
        var a = FindAlarm(id) ?? throw new CoreException(404, "告警不存在", "ALARM_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(a, "siteId"));
        var status = J.Str(a, "status");
        if (status is not ("resolved" or "active" or "acknowledged"))
            throw new CoreException(409, "该告警已关闭", "ALARM_CLOSED");
        var text = note ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw new CoreException(400, "关闭告警需填写处置说明", "NOTE_REQUIRED");
        var updated = Close(u, id, text, ip);
        return AlarmView(updated);
    }

    /* ---------------- 告警规则 ---------------- */

    /// <summary>规则视图：补阈值文案、等级名、点位名、单位、最新值、未闭环数、站点名。</summary>
    public JsonObject RuleView(JsonObject r)
    {
        var ruleSite = J.Str(r, "siteId");
        var point = J.ObjsIn(Db, "points").FirstOrDefault(p =>
            J.Str(p, "code") == J.Str(r, "pointCode") && (ruleSite == "*" || J.Str(p, "siteId") == ruleSite));
        var latest = point is null ? null : _telemetry.GetLatest(J.Str(point, "id"));
        var open = J.ObjsIn(Db, "alarms").Count(a =>
            J.Str(a, "ruleId") == J.Str(r, "id") && J.Str(a, "status") is "active" or "acknowledged");
        var o = (JsonObject)r.DeepClone();
        o["threshold"] = ThresholdText(r);
        var lv = J.Str(r, "level");
        o["levelName"] = LEVEL_NAME.TryGetValue(lv, out var ln) ? ln : lv;
        o["pointName"] = point is null ? J.Str(r, "pointCode") : J.Str(point, "name");
        o["unit"] = J.Has(r, "unit") && J.Str(r, "unit").Length > 0
            ? J.Str(r, "unit")
            : (point is null ? "" : J.Str(point, "unit"));
        o["latest"] = latest is null
            ? null
            : new JsonObject
            {
                ["value"] = J.Get(latest, "v")?.DeepClone(),
                ["t"] = TimeUtil.Iso((long)TimeUtil.Parse(latest, "t")),
            };
        o["openCount"] = open;
        if (ruleSite == "*")
        {
            o["siteName"] = "全部站点";
        }
        else
        {
            var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == ruleSite);
            var shortName = site is null ? "" : J.Str(site, "shortName");
            o["siteName"] = string.IsNullOrEmpty(shortName) ? ruleSite : shortName;
        }
        return o;
    }

    /// <summary>规则列表（含全站规则）。对应 <c>GET /api/alarm-rules</c>。</summary>
    public JsonArray ListAlarmRules(JsonObject? user, string? siteId = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var arr = new JsonArray();
        foreach (var r in J.ObjsIn(Db, "alarmRules").Where(r => J.Str(r, "siteId") == "*" || J.Str(r, "siteId") == sid))
            arr.Add(RuleView(r));
        return arr;
    }

    /// <summary>修改告警规则。对应 <c>PATCH /api/alarm-rules/:id</c>（权限 param.edit）。</summary>
    public JsonObject UpdateAlarmRule(JsonObject? user, string ruleId, JsonObject body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var r = J.ObjsIn(Db, "alarmRules").FirstOrDefault(x => J.Str(x, "id") == ruleId)
                ?? throw new CoreException(404, "规则不存在", "RULE_NOT_FOUND");
        if (J.Str(r, "siteId") != "*") Access.GetSite(_auth, u, Db, J.Str(r, "siteId"));
        else Access.GetSite(_auth, u, Db, J.Str(Access.ScopedSites(_auth, u, Db)[0], "id"));

        var before = (JsonObject)r.DeepClone();
        foreach (var k in new[] { "name", "desc", "enabled", "level", "durationSec", "escalateSec", "hysteresis", "low", "high" })
        {
            if (!body.ContainsKey(k)) continue;
            var node = body[k];
            if (k == "enabled")
            {
                r["enabled"] = JsMath.Truthy(node);
            }
            else if (k == "level")
            {
                var lv = J.AsString(node) ?? "";
                if (!LEVELS.Contains(lv)) throw new CoreException(400, "告警等级不合法", "BAD_LEVEL");
                r["level"] = lv;
            }
            else if (k is "durationSec" or "escalateSec" or "hysteresis" or "low" or "high")
            {
                var v = JsMath.NumberOrNaN(node);
                if (double.IsNaN(v) || v < 0) throw new CoreException(400, $"{k} 必须为非负数字", "BAD_VALUE");
                r[k] = v;
            }
            else
            {
                r[k] = J.Sliced(J.AsString(node) ?? "", 200);
            }
        }
        r["updatedAt"] = TimeUtil.IsoNow();
        r["updatedBy"] = J.Str(u, "id");

        if (body.ContainsKey("enabled") && !JsMath.Truthy(body["enabled"]))
        {
            foreach (var a in J.ObjsIn(Db, "alarms"))
            {
                if (J.Str(a, "ruleId") != J.Str(r, "id")) continue;
                if (J.Str(a, "status") is not ("active" or "acknowledged")) continue;
                a["status"] = "closed";
                a["closedAt"] = TimeUtil.IsoNow();
                a["closedBy"] = J.Str(u, "id");
                a["closeNote"] = "告警规则已停用，历史告警自动关闭。";
            }
        }
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "alarm.rule.update",
            TargetType = "alarmRule",
            TargetId = J.Str(r, "id"),
            SiteId = J.Str(r, "siteId") == "*" ? null : J.Str(r, "siteId"),
            Detail = $"修改规则「{J.Str(r, "name")}」",
            Before = before,
            After = r,
            Ip = ip,
        });
        return RuleView(r);
    }

    /// <summary>新建告警规则。对应 <c>POST /api/alarm-rules</c>（权限 param.edit）。</summary>
    public JsonObject CreateAlarmRule(JsonObject? user, JsonObject body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var name = J.AsString(J.Get(body, "name")) ?? "";
        var pointCode = J.AsString(J.Get(body, "pointCode")) ?? "";
        var op = J.AsString(J.Get(body, "op")) ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pointCode) || string.IsNullOrEmpty(op))
            throw new CoreException(400, "请填写规则名称、点位与判断方式", "BAD_REQUEST");
        if (op is not ("low" or "high" or "outside" or "state"))
            throw new CoreException(400, "判断方式不合法", "BAD_OP");

        var bodySite = J.AsString(J.Get(body, "siteId"));
        var siteId = !string.IsNullOrEmpty(bodySite) && bodySite != "*"
            ? J.Str(Access.GetSite(_auth, u, Db, bodySite), "id")
            : "*";

        var rules = J.A(Db, "alarmRules");
        var level = J.AsString(J.Get(body, "level")) ?? "";
        if (!LEVELS.Contains(level)) level = "minor";
        double durationSec = JsMath.NumberOrZero(J.Get(body, "durationSec"));
        if (durationSec == 0) durationSec = 300;
        double escalateSec = JsMath.NumberOrZero(J.Get(body, "escalateSec"));
        if (escalateSec == 0) escalateSec = 1800;
        var r = new JsonObject
        {
            ["id"] = "rule-" + (rules.Count + 1),
            ["key"] = !string.IsNullOrEmpty(J.AsString(J.Get(body, "key"))) ? J.AsString(J.Get(body, "key")) : "custom-" + TimeUtil.Now(),
            ["siteId"] = siteId,
            ["pointCode"] = pointCode,
            ["name"] = J.Sliced(name, 100),
            ["op"] = op,
            ["low"] = J.Has(body, "low") && body["low"] is not null ? JsMath.NumberOrNull(body["low"]) : null,
            ["high"] = J.Has(body, "high") && body["high"] is not null ? JsMath.NumberOrNull(body["high"]) : null,
            ["stateValues"] = J.Get(body, "stateValues")?.DeepClone(),
            ["level"] = level,
            ["durationSec"] = durationSec,
            ["hysteresis"] = JsMath.NumberOrZero(J.Get(body, "hysteresis")),
            ["escalateSec"] = escalateSec,
            ["unit"] = J.AsString(J.Get(body, "unit")) ?? "",
            ["enabled"] = !(body.ContainsKey("enabled") && !JsMath.Truthy(body["enabled"])),
            ["desc"] = J.Sliced(J.AsString(J.Get(body, "desc")) ?? "", 300),
            ["createdAt"] = TimeUtil.IsoNow(),
            ["updatedAt"] = TimeUtil.IsoNow(),
            ["updatedBy"] = J.Str(u, "id"),
        };
        var key = J.Str(r, "key");
        if (J.ObjsIn(Db, "alarmRules").Any(x => J.Str(x, "key") == key)) r["key"] = key + "-" + TimeUtil.Now();
        rules.Add(r);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "alarm.rule.create",
            TargetType = "alarmRule",
            TargetId = J.Str(r, "id"),
            SiteId = siteId,
            Detail = $"新建规则「{J.Str(r, "name")}」",
            After = r,
            Ip = ip,
        });
        return RuleView(r);
    }
}

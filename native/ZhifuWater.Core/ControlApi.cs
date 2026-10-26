using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 控制动作清单与命令接口。对齐 <c>server/api/control.js</c> 的控制部分。
/// </summary>
public sealed partial class CommandEngine
{
    private static readonly string[] ConstraintKeys =
    {
        "raw.pump.start", "raw.pump.stop", "raw.pump.mode.set", "baf.blower.stop",
        "baf.backwash.start", "sulfur.backwash.start", "baf.backwash.stop", "sulfur.backwash.stop",
        "baf.blower.start", "pac.dose.set", "pam.dose.set",
    };

    private static JsonArray DescribeConstraints(string key)
    {
        var arr = new JsonArray();
        void Add(params string[] xs) { foreach (var x in xs) arr.Add(x); }
        switch (key)
        {
            case "raw.pump.start":
                Add("原料罐液位 ≥ 5%（低液位保护）", "提升泵无故障信号");
                break;
            case "raw.pump.stop":
                Add("停泵后进水流量归零，注意下游液位");
                break;
            case "raw.pump.mode.set":
                Add("手动模式仅限现场操作并记录", "切换需二次确认");
                break;
            case "baf.blower.stop":
                Add("BAF 溶解氧 ≥ 2.5 mg/L（DO 联锁）");
                break;
            case "baf.backwash.start":
            case "sulfur.backwash.start":
                Add("当前无进行中的反冲洗", "废水箱液位 < 92%（排水阀联锁）", "先气冲 30 s → 气水联合 90 s → 漂洗 45 s");
                break;
            case "baf.backwash.stop":
            case "sulfur.backwash.stop":
                Add("仅在中止异常反冲洗时使用", "停止后需现场确认滤池状态");
                break;
            case "baf.blower.start":
                Add("风机无过载/故障");
                break;
            case "pac.dose.set":
            case "pam.dose.set":
                Add("投加量范围 0–" + (key.StartsWith("pac", StringComparison.Ordinal) ? "0.2" : "0.1") + " L/h",
                    "单次调整不超过 20%（工艺建议）");
                break;
        }
        return arr;
    }

    /// <summary>控制动作清单（含目标设备、相关点位实时值、参数表单、联锁说明）。对应 <c>GET /api/actions</c>。</summary>
    public JsonObject ListActions(JsonObject? user, string? siteId = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);

        var items = new JsonArray();
        foreach (var (key, a) in ACTIONS)
        {
            var device = J.ObjsIn(Db, "devices").FirstOrDefault(d => J.Str(d, "siteId") == sid && J.Str(d, "key") == a.DeviceKey);
            var points = device is null
                ? new List<JsonObject>()
                : J.ObjsIn(Db, "points").Where(p => J.Str(p, "deviceId") == J.Str(device, "id")).ToList();
            var readings = new JsonArray();
            foreach (var p in points)
            {
                var l = _telemetry.GetLatest(J.Str(p, "id"));
                readings.Add(new JsonObject
                {
                    ["code"] = J.Str(p, "code"),
                    ["name"] = J.Str(p, "name"),
                    ["unit"] = J.Str(p, "unit"),
                    ["value"] = l is null ? null : J.Get(l, "v")?.DeepClone(),
                    ["t"] = l is null ? null : TimeUtil.Iso((long)TimeUtil.Parse(l, "t")),
                });
            }
            JsonArray args;
            if (key.EndsWith(".dose.set", StringComparison.Ordinal))
            {
                args = new JsonArray
                {
                    new JsonObject
                    {
                        ["key"] = "value",
                        ["label"] = "设定值",
                        ["unit"] = "L/h",
                        ["min"] = 0,
                        ["max"] = key.StartsWith("pac", StringComparison.Ordinal) ? 0.2 : 0.1,
                        ["step"] = 0.005,
                    },
                };
            }
            else if (key == "raw.pump.mode.set")
            {
                args = new JsonArray
                {
                    new JsonObject
                    {
                        ["key"] = "mode",
                        ["label"] = "模式",
                        ["options"] = new JsonArray(
                            new JsonObject { ["value"] = 0, ["label"] = "自动" },
                            new JsonObject { ["value"] = 1, ["label"] = "手动" }),
                    },
                };
            }
            else
            {
                args = new JsonArray();
            }
            items.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = a.Label,
                ["risk"] = a.Risk,
                ["effect"] = a.Effect,
                ["deviceKey"] = a.DeviceKey,
                ["device"] = device is null ? null : new JsonObject
                {
                    ["id"] = J.Str(device, "id"),
                    ["code"] = J.Str(device, "code"),
                    ["name"] = J.Str(device, "name"),
                    ["status"] = J.Str(device, "status"),
                    ["online"] = J.Get(device, "online")?.DeepClone(),
                },
                ["readings"] = readings,
                ["needsConfirm"] = a.Risk == "high",
                ["args"] = args,
                ["constraints"] = DescribeConstraints(key),
            });
        }
        return new JsonObject
        {
            ["siteId"] = sid,
            ["status"] = _sim.Status(sid),
            ["actions"] = items,
        };
    }

    /// <summary>命令列表（分页 + 状态计数）。对应 <c>GET /api/commands</c>。</summary>
    public JsonObject ListCommands(JsonObject? user, string? siteId = null, string? status = null,
        string? action = null, string? from = null, string? q = null, int? page = null, int? pageSize = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "commands").Where(c => siteIds.Contains(J.Str(c, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(c => J.Str(c, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(status)) items = items.Where(c => J.Str(c, "status") == status).ToList();
        if (!string.IsNullOrEmpty(action)) items = items.Where(c => J.Str(c, "action") == action).ToList();
        if (!string.IsNullOrEmpty(from))
        {
            var f = Access.ParseTime(from, 0);
            items = items.Where(c => TimeUtil.Parse(c, "createdAt") >= f).ToList();
        }
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(c => $"{J.Str(c, "id")} {J.Str(c, "actionLabel")} {J.Str(c, "reason")}"
                .ToLowerInvariant().Contains(needle)).ToList();
        }
        items = items.OrderByDescending(c => TimeUtil.Parse(c, "createdAt")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var counts = new JsonObject();
        foreach (var k in STATUS_NAME.Keys) counts[k] = items.Count(c => J.Str(c, "status") == k);
        var statusNames = new JsonObject();
        foreach (var kv in STATUS_NAME) statusNames[kv.Key] = kv.Value;
        var arr = new JsonArray();
        foreach (var c in pg.Items) arr.Add(CommandView(c));
        return new JsonObject
        {
            ["items"] = arr,
            ["total"] = pg.Total,
            ["page"] = pg.PageNo,
            ["pageSize"] = pg.PageSize,
            ["pages"] = pg.Pages,
            ["counts"] = counts,
            ["statusNames"] = statusNames,
        };
    }

    /// <summary>命令详情（含审计流水，按时间升序）。对应 <c>GET /api/commands/:id</c>。</summary>
    public JsonObject CommandDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var c = Get(id) ?? throw new CoreException(404, "命令不存在", "COMMAND_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(c, "siteId"));
        var audits = new JsonArray();
        foreach (var a in J.ObjsIn(Db, "auditLogs").Where(x => J.Str(x, "targetId") == id)
                     .OrderBy(x => TimeUtil.Parse(x, "at")))
            audits.Add(a.DeepClone());
        var view = CommandView(c);
        view["audits"] = audits;
        return view;
    }

    /// <summary>创建控制命令（权限 control，操作理由必填）。对应 <c>POST /api/commands</c>。</summary>
    public JsonObject CreateCommand(JsonObject? user, JsonObject body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "control");
        var scoped = Access.ScopedSites(_auth, u, Db);
        var siteId = J.AsString(J.Get(body, "siteId")) ?? "";
        if (string.IsNullOrEmpty(siteId)) siteId = J.Str(scoped[0], "id");
        Access.GetSite(_auth, u, Db, siteId);
        var action = J.AsString(J.Get(body, "action")) ?? "";
        var args = J.O(body, "args");
        var reason = J.AsString(J.Get(body, "reason")) ?? "";
        if (string.IsNullOrWhiteSpace(reason))
            throw new CoreException(400, "下发控制命令必须填写操作理由（审计要求）", "REASON_REQUIRED");
        var r = Create(u, siteId, action, args, reason, ip);
        if (r.Blocked)
        {
            return new JsonObject
            {
                ["blocked"] = true,
                ["accepted"] = false,
                ["command"] = CommandView(r.Command!),
                ["message"] = r.Error,
            };
        }
        return new JsonObject
        {
            ["blocked"] = false,
            ["accepted"] = true,
            ["command"] = CommandView(r.Command!),
            ["needsConfirm"] = J.Str(r.Command!, "status") == "pending_confirm",
        };
    }

    /// <summary>二次确认高风险命令。对应 <c>POST /api/commands/:id/confirm</c>。</summary>
    public JsonObject ConfirmCommand(JsonObject? user, string id, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "control");
        var c = Get(id) ?? throw new CoreException(404, "命令不存在", "COMMAND_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(c, "siteId"));
        return CommandView(Confirm(u, id, ip));
    }

    /// <summary>取消命令。对应 <c>POST /api/commands/:id/cancel</c>。</summary>
    public JsonObject CancelCommand(JsonObject? user, string id, string? reason = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "control");
        var c = Get(id) ?? throw new CoreException(404, "命令不存在", "COMMAND_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(c, "siteId"));
        var text = string.IsNullOrEmpty(reason) ? "用户取消" : reason!;
        return CommandView(Cancel(u, id, text, ip));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ZhifuWater.Core;

namespace ZhifuWater.Desktop.Data;

/// <summary>
/// 生产数据源：直接调用同解决方案的业务层 <see cref="CoreServices"/>（无 HTTP、无 WebView）。
/// 只在 UI 线程上推进模拟时钟（AutoStart=false + RunTicks），避免后台线程改 JsonNode 树与界面读取竞争；
/// 所有页面仍然只依赖 <see cref="IDesktopData"/> 的强类型模型。
/// </summary>
public sealed class CoreDesktopData : IDesktopData
{
    private readonly CoreServices _core;
    private readonly object _gate = new();
    private JsonObject? _user;
    private string _token = "";
    private string _siteId = "";
    private bool _seeded;
    private DateTime _lastUpdated = DateTime.Now;
    private Dictionary<string, string>? _actorNames;

    public CoreDesktopData()
    {
        var dataDir = Environment.GetEnvironmentVariable("ZHIFU_DATA_DIR");
        var options = new CoreServicesOptions
        {
            AutoStart = false,
            CopySeedOnFirstRun = true,
        };
        if (!string.IsNullOrWhiteSpace(dataDir)) options.DataDir = dataDir;
        // 首次运行的数据快照：优先用安装目录自带的 seed/db.json，其次开发机的 native/seed 与 data/db.json。
        var seed = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "seed", "db.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "db.json"),
            @"D:\清渠平台\native\seed\db.json",
            @"D:\清渠平台\data\db.json",
        }.FirstOrDefault(File.Exists);
        if (seed is not null) options.SeedSource = seed;
        _core = new CoreServices(options);
        BackendName = "本机业务层";
    }

    public string BackendName { get; }
    public DateTime LastUpdated { get { lock (_gate) return _lastUpdated; } }
    public event EventHandler? DataChanged;

    public string CurrentSiteId => _siteId;
    public UserInfo? CurrentUser { get; private set; }

    /* ================= 登录 / 站点 ================= */

    public OpResult Login(string username, string password)
    {
        try
        {
            var result = _core.Auth.Login(username.Trim(), password, "desktop", "Qingqu WPF/.NET 8");
            lock (_gate)
            {
                _token = result.Token;
                _user = result.User;
                CurrentUser = MapUser(result.User);
                _lastUpdated = DateTime.Now;
            }

            SitesCache = MapSites(_core.Analytics.Sites(_user));
            var allowed = CurrentUser.SiteIds;
            var pick = allowed.FirstOrDefault(id => SitesCache.Any(s => s.Id == id)) ?? SitesCache.FirstOrDefault()?.Id ?? "";
            _siteId = pick;

            // 首次登录时用与 Node 版一致的 30 天小时级 / 24 小时 5 分钟级曲线回填历史，
            // 否则刚接上 Core 的新库曲线是空的（正常运行时 tick 已把历史落盘，不会重复回填）。
            if (!_seeded)
            {
                _seeded = true;
                try
                {
                    var first = _core.Analytics.PointsOfSite(_siteId).FirstOrDefault();
                    var pid = first is null ? "" : J.Str(first, "id");
                    if (pid.Length > 0 && _core.Telemetry.HistCount(pid) < 10)
                    {
                        _core.Simulator.Backfill();
                        _core.Simulator.WriteTelemetry(TimeUtil.Now());
                    }
                }
                catch (Exception) { /* 历史回填失败不阻断登录 */ }
            }

            Refresh();
            return OpResult.Done($"登录成功，欢迎 {CurrentUser.Name}（{CurrentUser.RoleName}）");
        }
        catch (CoreException ex)
        {
            return OpResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return OpResult.Fail("登录失败：" + ex.Message);
        }
    }

    public void Logout()
    {
        try { if (_token.Length > 0) _core.Auth.Logout(_token); } catch { }
        lock (_gate)
        {
            _token = "";
            _user = null;
            CurrentUser = null;
            _siteId = "";
            SitesCache = new List<SiteInfo>();
            _lastUpdated = DateTime.Now;
        }
    }

    public OpResult ChangePassword(string oldPassword, string newPassword)
    {
        if (newPassword.Length < 6) return OpResult.Fail("新密码至少 6 位");
        var u = _user is null ? null : J.ObjsIn(_core.Store.Data, "users").FirstOrDefault(x => J.Str(x, "id") == J.Str(_user, "id"));
        if (u is null) return OpResult.Fail("当前账号不存在");
        if (!Scrypt.VerifyPassword(oldPassword, J.StrOrNull(u, "password")))
            return OpResult.Fail("原密码不正确");
        // Core 尚未暴露「自助改密」接口；这里直接写同一份用户数据并让会话失效，
        // 行为与 Node 版 /api/me/password 一致，后续 Core 若提供接口再替换为此调用。
        u["password"] = Scrypt.HashPassword(newPassword);
        _core.Auth.LogoutAllFor(J.Str(u, "id"));
        _core.Store.SaveSoon();
        try
        {
            _core.Audit.Write(_user, "user.password", "user", J.Str(u, "id"), "修改本人登录密码", null!, null!, "", "success");
        }
        catch { }
        _token = _core.Auth.Login(J.Str(u, "username"), newPassword, "desktop", "Qingqu WPF/.NET 8").Token;
        return OpResult.Done("密码已修改，请使用新密码登录");
    }

    public IReadOnlyList<SiteInfo> Sites { get; private set; } = new List<SiteInfo>();
    private List<SiteInfo> SitesCache = new();

    public SiteInfo Site => Sites.FirstOrDefault(s => s.Id == _siteId) ?? Sites.FirstOrDefault() ?? new SiteInfo();

    public void SetSite(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId)) return;
        try
        {
            _core.Analytics.SiteDetail(_user, siteId);
            _siteId = siteId;
            Refresh();
        }
        catch (CoreException) { }
    }

    public void Tick()
    {
        if (_user is null) return;
        try
        {
            _core.RunTicks(1);
            lock (_gate) _lastUpdated = DateTime.Now;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (CoreException) { }
        catch (Exception) { }
    }

    private void Refresh()
    {
        if (_user is null) return;
        try
        {
            SitesCache = MapSites(_core.Analytics.Sites(_user));
            Sites = SitesCache;
        }
        catch (CoreException) { }
        lock (_gate) _lastUpdated = DateTime.Now;
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /* ================= 静态元数据 ================= */

    public IReadOnlyList<UnitInfo> Units
    {
        get
        {
            if (_user is null) return new List<UnitInfo>();
            return J.ObjsIn(_core.Store.Data, "units")
                .Where(u => J.Str(u, "siteId") == _siteId && J.Str(u, "code") != "plant")
                .OrderBy(u => J.NumOr(u, "order", 0))
                .Select(u => new UnitInfo
                {
                    Code = J.Str(u, "code"),
                    Name = J.Str(u, "name"),
                    Short = J.Str(u, "short"),
                    Kind = J.Str(u, "kind"),
                    Note = J.Str(u, "note"),
                }).ToList();
        }
    }

    private Dictionary<string, JsonObject> PointMeta()
    {
        var d = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (_user is null) return d;
        foreach (var p in J.ObjsIn(_core.Store.Data, "points").Where(p => J.Str(p, "siteId") == _siteId))
            d[J.Str(p, "code")] = p;
        return d;
    }

    public IReadOnlyList<PointDef> Points
    {
        get
        {
            if (_user is null) return new List<PointDef>();
            var meta = PointMeta();
            var list = new List<PointDef>();
            foreach (var r in Latest())
            {
                var code = J.Str(r, "code");
                if (!meta.TryGetValue(code, out var p)) continue;
                list.Add(MapPoint(p));
            }
            return list.OrderBy(p => UnitOrder(p.UnitCode)).ThenBy(p => p.Code, StringComparer.Ordinal).ToList();
        }
    }

    private int UnitOrder(string unitCode) => Units.FirstOrDefault(u => u.Code == unitCode) is { } u
        ? Units.ToList().IndexOf(u) : 99;

    private PointDef MapPoint(JsonObject p)
    {
        var range = J.Get(p, "range") as JsonArray;
        var unit = J.Str(p, "unit");
        var min = range is { Count: >= 1 } ? J.AsDouble(range[0]) : null;
        var max = range is { Count: >= 2 } ? J.AsDouble(range[1]) : null;
        var rangeText = min is null || max is null ? "" : $"{NumText(min)}–{NumText(max)} {unit}".Trim();
        return new PointDef
        {
            Id = J.Str(p, "id"),
            Code = J.Str(p, "code"),
            Name = J.Str(p, "name"),
            UnitCode = J.Str(p, "unitCode"),
            Unit = unit,
            Type = J.Str(p, "type"),
            Range = rangeText,
            Accuracy = J.Str(p, "accuracy"),
            IntervalSec = Math.Max(1, J.Int(p, "intervalSec", 10)),
            Writable = J.Str(p, "writable") is { Length: > 0 } w ? w : "read",
            Provenance = J.Str(p, "provenance"),
            Desc = J.Str(p, "desc"),
            Digits = DigitsOf(unit),
        };
    }

    private static int DigitsOf(string unit) => unit switch
    {
        "pH" => 2,
        "%" => 1,
        "MPa" => 3,
        "m³/h" => 3,
        "h" => 1,
        "次" => 0,
        "kWh" => 1,
        _ => 2,
    };

    private static string NumText(double? v) => v is null ? "—" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    public IReadOnlyList<Reading> Readings => BuildReadings();

    private List<Reading> BuildReadings()
    {
        if (_user is null) return new List<Reading>();
        var meta = PointMeta();
        var list = new List<Reading>();
        foreach (var r in Latest())
        {
            var code = J.Str(r, "code");
            if (!meta.TryGetValue(code, out var p)) continue;
            list.Add(MapReading(p, r));
        }
        return list;
    }

    private Reading MapReading(JsonObject p, JsonNode? r)
    {
        var def = MapPoint(p);
        var value = r is null ? null : J.Num(r, "value");
        if (value is null && r is not null && J.Get(r, "value") is JsonValue jv && jv.TryGetValue<double>(out var dv)) value = dv;
        var type = def.Type;
        string text;
        if (value is null) text = "待接入";
        else if (type is "state" or "run" or "alarm")
            text = StateText(def.Code, value.Value);
        else
            text = value.Value.ToString("F" + def.Digits, CultureInfo.InvariantCulture) + (def.Unit.Length > 0 ? " " + def.Unit : "");
        var t = r is null ? 0 : (long)TimeUtil.Parse(r, "t");
        return new Reading
        {
            Point = def,
            Value = value,
            Text = text,
            Quality = r is null ? "missing" : J.Str(r, "quality") is { Length: > 0 } q ? q : "good",
            T = t,
            Source = r is null ? def.Provenance : J.Str(r, "source"),
            UnitCode = def.UnitCode,
            Stale = r is not null && J.Bool(r, "stale"),
        };
    }

    private static string StateText(string code, double v)
    {
        if (code.EndsWith(".MODE", StringComparison.Ordinal)) return v > 0 ? "手动" : "自动";
        if (code.Contains("FAULT", StringComparison.Ordinal)) return v > 0 ? "故障" : "正常";
        if (code.Contains("BW.STATE", StringComparison.Ordinal)) return v > 0 ? "反冲洗" : "停止";
        return v > 0 ? "运行" : "停止";
    }

    private JsonArray Latest()
    {
        try { return _core.Analytics.LatestList(_siteId); }
        catch (CoreException) { return new JsonArray(); }
    }

    public Reading? ReadingOf(string code)
    {
        if (_user is null) return null;
        var p = PointMeta().GetValueOrDefault(code);
        if (p is null) return null;
        var reading = Latest().FirstOrDefault(x => J.Str(x, "code") == code);
        return MapReading(p, reading);
    }

    /* ================= 工艺单元 / 趋势 ================= */

    public IReadOnlyList<UnitSnapshot> UnitSnapshots()
    {
        if (_user is null) return new List<UnitSnapshot>();
        var units = Units;
        var readings = BuildReadings().ToLookup(r => r.UnitCode, StringComparer.Ordinal);
        JsonArray cards;
        try { cards = _core.Analytics.UnitCards(_siteId); } catch (CoreException) { cards = new JsonArray(); }
        var cardMap = J.Objs(cards).ToDictionary(c => J.Str(c, "code"), StringComparer.Ordinal);
        var openAlarms = Alarms("open").ToLookup(a => a.UnitCode, StringComparer.Ordinal);
        var sim = _core.Simulator.StateOf(_siteId);
        var list = new List<UnitSnapshot>();
        foreach (var unit in units)
        {
            var card = cardMap.GetValueOrDefault(unit.Code);
            var us = new UnitSnapshot
            {
                Unit = unit,
                Readings = readings[unit.Code].ToList(),
                DeviceCount = card is null ? 0 : J.Int(J.O(card, "devices", false), "total"),
                OnlineDevices = card is null ? 0 : J.Int(J.O(card, "devices", false), "online"),
                Note = unit.Note,
            };
            var health = card is null ? 100 : J.Int(J.O(card, "health", false), "score", J.Int(card, "health", 100));
            var open = openAlarms[unit.Code].Count();
            us.State = open > 0 ? "alarm"
                : us.DeviceCount == 0 ? "no-instrument"
                : us.OnlineDevices < us.DeviceCount ? "offline"
                : health < 70 ? "attention" : "normal";
            us.StateText = us.State switch
            {
                "alarm" => "告警",
                "attention" => "关注",
                "offline" => "离线",
                "no-instrument" => "无仪表",
                _ => "正常",
            };

            if (sim is not null)
            {
                var bw = unit.Code switch
                {
                    "baf" => sim.Baf,
                    "sulfur" => sim.Sulfur,
                    _ => null,
                };
                if (bw is not null && (bw.BwPhase > 0 || bw.BwRemain > 0))
                {
                    us.BwPhase = bw.BwPhase.ToString(CultureInfo.InvariantCulture);
                    us.BwText = bw.BwPhase switch { 1 => "气冲", 2 => "气水联合", 3 => "漂洗", _ => "待机" };
                    us.BwRemain = "剩余 " + Math.Max(0, (int)Math.Ceiling(bw.BwRemain)) + " s";
                }
            }
            list.Add(us);
        }
        return list;
    }

    public IReadOnlyList<TrendSeries> Trend(IEnumerable<string> codes, int hours)
    {
        var result = new List<TrendSeries>();
        if (_user is null) return result;
        var codeList = codes.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (codeList.Count == 0) return result;
        var meta = PointMeta();
        var to = TimeUtil.Now();
        var from = to - Math.Max(1, hours) * TimeUtil.HourMs;
        JsonObject series;
        try { series = _core.Analytics.Series(_siteId, codeList, from, to, Math.Max(60, (int)(to - from) / 240 / 1000)); }
        catch (CoreException) { return result; }
        var colors = new[] { "#0B6BB5", "#1C6B45", "#8A5A12", "#2A5F8A", "#A32C22", "#5C6B75" };
        var i = 0;
        foreach (var code in codeList)
        {
            var arr = J.Get(series, code) as JsonArray;
            if (arr is null) continue;
            var p = meta.GetValueOrDefault(code);
            var ts = new TrendSeries
            {
                Code = code,
                Name = p is null ? code : J.Str(p, "name"),
                Unit = p is null ? "" : J.Str(p, "unit"),
                Color = colors[i % colors.Length],
            };
            foreach (var node in arr)
            {
                var v = J.Num(node, "v") ?? J.Num(node, "value");
                if (v is null) continue;
                ts.Points.Add(new MetricPoint { T = (long)TimeUtil.Parse(node, "t"), V = v.Value });
            }
            if (ts.Points.Count > 0) result.Add(ts);
            i++;
        }
        return result;
    }

    public IReadOnlyList<string> UnitWarnings(string unitCode)
    {
        if (_user is null) return new List<string>();
        var pointCodes = PointMeta().Where(kv => J.Str(kv.Value, "unitCode") == unitCode).Select(kv => kv.Key).ToHashSet();
        return Alarms("open").Where(a => pointCodes.Count == 0 || a.UnitCode == unitCode || pointCodes.Contains(PointOfAlarm(a.Id)))
            .Select(a => a.Title).Distinct().Take(3).ToList();
    }

    private string PointOfAlarm(string alarmId) => "";

    /* ================= 驾驶舱 KPI / 健康 ================= */

    public IReadOnlyList<KpiInfo> DashboardKpis()
    {
        if (_user is null) return new List<KpiInfo>();
        var ov = Overview();
        var kpis = J.Objs(J.Get(ov, "kpis"));
        var byKey = kpis.ToDictionary(k => J.Str(k, "key"), StringComparer.Ordinal);
        var list = new List<KpiInfo>();
        if (byKey.TryGetValue("volume", out var volume))
            list.Add(MapKpi(volume, "今日处理水量", "gateway"));
        if (byKey.TryGetValue("cod", out var cod))
            list.Add(MapKpi(cod, "出水 COD（人工化验）", "manual"));
        var process = J.Objs(J.Get(ov, "process"));
        var devTotal = process.Sum(p => J.Int(J.O(p, "devices", false), "total"));
        var devOnline = process.Sum(p => J.Int(J.O(p, "devices", false), "online"));
        list.Add(new KpiInfo
        {
            Key = "devices", Label = "运行设备",
            Value = devOnline.ToString(CultureInfo.InvariantCulture) + "/" + devTotal.ToString(CultureInfo.InvariantCulture),
            Unit = "台",
            Tone = devOnline >= devTotal ? "good" : "warn",
            Hint = "在线设备 / 设备总数", Source = "gateway",
        });
        if (byKey.TryGetValue("online", out var online))
        {
            var onlineKpi = MapKpi(online, "点位在线率", "system");
            onlineKpi.Label = "点位在线率";
            list.Add(onlineKpi);
        }
        if (byKey.TryGetValue("alarms", out var alarms))
            list.Add(MapKpi(alarms, "活动告警", "system"));
        if (byKey.TryGetValue("workorders", out var wos))
            list.Add(MapKpi(wos, "待处理工单", "system"));

        var summary = Summary(1);
        var water = J.Num(J.O(summary, "water", false), "total");
        var energy = J.Num(J.O(J.O(summary, "energy", false), "perM3", false), "value")
                     ?? J.Num(J.O(summary, "energy", false), "perM3");
        var ePerM3 = energy ?? (water is > 0 ? J.Num(J.O(summary, "energy", false), "total") / water.Value : null);
        list.Add(new KpiInfo
        {
            Key = "energyPerM3", Label = "吨水电耗",
            Value = ePerM3 is null ? "—" : ePerM3.Value.ToString("F2", CultureInfo.InvariantCulture),
            Unit = "kWh/m³",
            Tone = ePerM3 is null ? "muted" : "normal",
            Hint = "电费+药耗来自运行报表汇总", Source = "gateway",
        });
        var chem = J.O(summary, "chemicals", false);
        var chemL = (J.Num(J.O(chem, "pac", false), "total") ?? 0) + (J.Num(J.O(chem, "pam", false), "total") ?? 0);
        list.Add(new KpiInfo
        {
            Key = "chemPerM3", Label = "吨水药耗",
            Value = water is > 0 ? (chemL / water.Value).ToString("F3", CultureInfo.InvariantCulture) : "—",
            Unit = "L/m³",
            Tone = water is > 0 ? "normal" : "muted",
            Hint = "PAC + PAM 投加量 / 处理水量", Source = "gateway",
        });
        return list;
    }

    private static KpiInfo MapKpi(JsonObject k, string label, string source)
    {
        var value = J.Get(k, "value");
        var digits = Math.Max(0, J.Int(k, "digits", 2));
        var v = J.AsDouble(value);
        var display = J.Str(k, "display");
        var text = !string.IsNullOrEmpty(display) ? display
            : v is null ? "—"
            : v.Value.ToString("F" + digits, CultureInfo.InvariantCulture);
        var status = J.Str(k, "status");
        var tone = status switch
        {
            "risk" => "bad",
            "attention" or "pending" => "warn",
            "ok" => "good",
            _ => "normal",
        };
        var trend = J.O(k, "trend", false);
        var delta = J.Num(trend, "value") is { } tv
            ? $"{J.Str(trend, "label")} {tv.ToString("F1", CultureInfo.InvariantCulture)}%"
            : "";
        return new KpiInfo
        {
            Key = J.Str(k, "key"),
            Label = string.IsNullOrEmpty(J.Str(k, "label")) ? label : J.Str(k, "label"),
            Value = text,
            Unit = J.Str(k, "unit"),
            Delta = delta,
            Tone = tone,
            Hint = J.Str(k, "hint"),
            Source = string.IsNullOrEmpty(J.Str(k, "source")) ? source : J.Str(k, "source"),
        };
    }

    public DataHealthInfo DataHealth()
    {
        var info = new DataHealthInfo { Notice = Notice() };
        if (_user is null) return info;
        var h = J.O(Overview(), "dataHealth", false);
        info.Gateway = J.Str(h, "gateway", "unknown");
        info.PointsTotal = J.Int(h, "pointsTotal");
        info.PointsOnline = J.Int(h, "pointsOnline");
        info.LatestAt = (long)TimeUtil.Parse(h, "latestAt");
        return info;
    }

    public string Notice() => J.Str(Overview(), "notice");

    private JsonObject _overviewCache = new();
    private long _overviewAt;

    private JsonObject Overview()
    {
        if (_user is null) return new JsonObject();
        if (TimeUtil.Now() - _overviewAt < 800 && _overviewCache.Count > 0) return _overviewCache;
        try
        {
            _overviewCache = _core.Analytics.Overview(_user, _siteId);
            _overviewAt = TimeUtil.Now();
        }
        catch (CoreException) { _overviewCache = new JsonObject(); }
        return _overviewCache;
    }

    private JsonObject _summaryCache = new();
    private long _summaryAt;
    private int _summaryDays;

    private JsonObject Summary(int days)
    {
        if (_user is null) return new JsonObject();
        if (_summaryDays == days && TimeUtil.Now() - _summaryAt < 1500) return _summaryCache;
        try
        {
            _summaryCache = _core.Reports.Summary(_user, _siteId, days);
            _summaryAt = TimeUtil.Now();
            _summaryDays = days;
        }
        catch (CoreException) { _summaryCache = new JsonObject(); }
        return _summaryCache;
    }

    /* ================= 告警 / 命令 / 控制 ================= */

    private List<AlarmInfo> MapAlarms(JsonObject list)
    {
        var arr = new List<AlarmInfo>();
        foreach (var a in J.Objs(J.Get(list, "items")))
        {
            var timeline = new List<TimelineEntry>();
            if (J.Get(a, "timeline") is JsonArray tl)
                foreach (var t in J.Objs(tl))
                    timeline.Add(new TimelineEntry
                    {
                        At = (long)TimeUtil.Parse(t, "at"),
                        Actor = ActorName(J.Str(t, "by")),
                        Text = J.Str(t, "note") is { Length: > 0 } note ? note : J.Str(t, "action"),
                    });
            arr.Add(new AlarmInfo
            {
                Id = J.Str(a, "id"),
                SiteId = J.Str(a, "siteId"),
                Level = J.Str(a, "level", "minor"),
                Title = J.Str(a, "title"),
                Detail = J.Str(a, "detail"),
                Status = J.Str(a, "status", "active"),
                Value = J.Num(a, "value"),
                Threshold = J.Str(a, "threshold"),
                UnitCode = J.Str(a, "unitCode"),
                UnitName = J.Str(a, "unitName"),
                DeviceName = J.Str(a, "pointName"),
                RuleName = J.Str(a, "ruleName"),
                At = (long)TimeUtil.Parse(a, "firstAt"),
                AckAt = J.Get(a, "ackAt") is null ? null : (long)TimeUtil.Parse(a, "ackAt"),
                ResolvedAt = J.Get(a, "resolvedAt") is null ? null : (long)TimeUtil.Parse(a, "resolvedAt"),
                AckBy = J.Str(a, "ackByName"),
                Timeline = timeline,
            });
        }
        return arr;
    }

    public IReadOnlyList<AlarmInfo> Alarms(string status = "all", string level = "all", string keyword = "")
    {
        if (_user is null) return new List<AlarmInfo>();
        try
        {
            var list = _core.Rules.ListAlarms(_user, _siteId,
                status is "all" ? null : status, level is "all" ? null : level, q: string.IsNullOrWhiteSpace(keyword) ? null : keyword, pageSize: 200);
            return MapAlarms(list);
        }
        catch (CoreException) { return new List<AlarmInfo>(); }
    }

    public AlarmStats AlarmStatistics(int days)
    {
        var stats = new AlarmStats();
        if (_user is null) return stats;
        try
        {
            var s = _core.Rules.AlarmStats(_user, _siteId, days);
            foreach (var d in J.Objs(J.Get(s, "byDay")))
                stats.ByDay.Add(new KeyValuePair<string, int>(J.Str(d, "day"), J.Int(d, "total")));
            var levels = J.O(s, "byLevel", false);
            foreach (var (key, label) in new[] { ("critical", "紧急"), ("major", "重要"), ("minor", "次要"), ("info", "提示") })
                stats.ByLevel.Add(new KeyValuePair<string, int>(label, J.Int(levels, key)));
            foreach (var u in J.Objs(J.Get(s, "byUnit")))
                stats.ByUnit.Add(new KeyValuePair<string, int>(J.Str(u, "name", J.Str(u, "unitCode")), J.Int(u, "total", J.Int(u, "count"))));
            var counts = new JObjectLikeCounts();
            stats.Counts["active"] = J.Int(s, "open");
            stats.Counts["total"] = J.Int(s, "total");
            stats.MttaMin = J.Int(s, "mttaMin");
            stats.MttrMin = J.Int(s, "mttrMin");
            stats.AckRate = (int)Math.Round(J.NumOr(s, "ackRate", 0));
        }
        catch (CoreException) { }
        return stats;
    }

    private sealed class JObjectLikeCounts { }

    public OpResult AckAlarm(string id, string note)
    {
        try
        {
            var result = _core.Rules.AckAlarm(_user, id, note, "desktop");
            Refresh();
            return OpResult.Done("告警已确认" + (J.Str(result, "id").Length > 0 ? $"（{J.Str(result, "id")}）" : ""));
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ResolveAlarm(string id, string note)
    {
        try
        {
            _core.Rules.ResolveAlarm(_user, id, note, "desktop");
            Refresh();
            return OpResult.Done("告警已标记恢复");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ToggleAlarmRule(string id)
    {
        try
        {
            var rule = J.ObjsIn(_core.Store.Data, "alarmRules").FirstOrDefault(r => J.Str(r, "id") == id);
            if (rule is null) return OpResult.Fail("告警规则不存在");
            var body = new JsonObject { ["enabled"] = !J.Bool(rule, "enabled", true) };
            _core.Rules.UpdateAlarmRule(_user, id, body, "desktop");
            Refresh();
            return OpResult.Done("规则状态已更新");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public IReadOnlyList<AlarmRuleInfo> AlarmRules
    {
        get
        {
            if (_user is null) return new List<AlarmRuleInfo>();
            return J.ObjsIn(_core.Store.Data, "alarmRules")
                .Where(r => J.Str(r, "siteId") is "*" or "" || J.Str(r, "siteId") == _siteId)
                .Select(r => new AlarmRuleInfo
                {
                    Id = J.Str(r, "id"),
                    Key = J.Str(r, "key"),
                    Name = J.Str(r, "name"),
                    PointCode = J.Str(r, "pointCode"),
                    Level = J.Str(r, "level", "minor"),
                    Condition = ConditionText(r),
                    Enabled = J.Bool(r, "enabled", true),
                    Desc = J.Str(r, "desc"),
                }).ToList();
        }
    }

    private static string ConditionText(JsonObject r)
    {
        var unit = J.Str(r, "unit");
        var op = J.Str(r, "op");
        var low = JNumText(J.Num(r, "low"));
        var high = JNumText(J.Num(r, "high"));
        return op switch
        {
            "outside" => $"{low}–{high} {unit} 之外",
            "lt" => $"< {low} {unit}",
            "lte" => $"≤ {low} {unit}",
            "gt" => $"> {high} {unit}",
            "gte" => $"≥ {high} {unit}",
            "eq" => $"= {low} {unit}",
            _ => op,
        };
    }

    /// <summary>Core 的操作人字段是账号 id（u_1…），统一显示为姓名，未知 id 显示「系统」。</summary>
    private string ActorName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "系统";
        _actorNames ??= J.Objs(J.Get(_core.Store.Data, "users"))
            .Where(u => J.Str(u, "id") is { Length: > 0 })
            .ToDictionary(u => J.Str(u, "id"), u => J.Str(u, "name"), StringComparer.Ordinal);
        return _actorNames.TryGetValue(id, out var name) && name.Length > 0 ? name : "系统";
    }

    private static string JNumText(double? v) => v is null ? "—" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    public IReadOnlyList<CommandRecord> Commands(int limit = 30)
    {
        if (_user is null) return new List<CommandRecord>();
        try
        {
            var list = _core.Commands.ListCommands(_user, _siteId, pageSize: limit);
            return J.Objs(J.Get(list, "items")).Select(c => new CommandRecord
            {
                Id = J.Str(c, "id"),
                SiteId = J.Str(c, "siteId"),
                Action = J.Str(c, "action"),
                Label = J.Str(c, "actionLabel"),
                Status = J.Str(c, "status"),
                Reason = J.Str(c, "reason"),
                By = J.Str(c, "byName", J.Str(c, "createdByName")),
                At = (long)TimeUtil.Parse(c, "createdAt"),
                Device = J.Str(c, "targetName", J.Str(J.O(c, "device", false), "name")),
                UnitCode = "",
                FailReason = J.Str(c, "failReason"),
            }).ToList();
        }
        catch (CoreException) { return new List<CommandRecord>(); }
    }

    public IReadOnlyList<ActionInfo> Actions
    {
        get
        {
            if (_user is null) return new List<ActionInfo>();
            try
            {
                var list = _core.Commands.ListActions(_user, _siteId);
                return J.Objs(J.Get(list, "actions")).Select(a =>
                {
                    var constraints = new List<string>();
                    if (J.Get(a, "constraints") is JsonArray ca)
                        foreach (var c in ca) constraints.Add(J.AsString(c) ?? "");
                    var device = J.O(a, "device", false);
                    return new ActionInfo
                    {
                        Key = J.Str(a, "key"),
                        Label = J.Str(a, "label"),
                        Device = J.Str(device, "name") is { Length: > 0 } dn ? dn : J.Str(a, "label"),
                        UnitCode = J.Str(device, "unitCode"),
                        NeedsConfirm = J.Bool(a, "needsConfirm", true),
                        Risk = J.Str(a, "risk", "normal"),
                        Constraints = constraints.Where(x => x.Length > 0).ToList(),
                        Desc = J.Str(a, "effect"),
                        Interlock = constraints.Count > 0 ? constraints[0] : "",
                        Detail = string.Join("；", constraints),
                    };
                }).ToList();
            }
            catch (CoreException) { return new List<ActionInfo>(); }
        }
    }

    public OpResult ExecuteAction(string key, string reason)
    {
        try
        {
            var body = new JsonObject
            {
                ["siteId"] = _siteId,
                ["action"] = key,
                ["reason"] = reason,
                ["args"] = new JsonObject(),
            };
            var cmd = _core.Commands.CreateCommand(_user, body, "desktop");
            Refresh();
            return OpResult.Done($"命令已创建：{J.Str(cmd, "actionLabel", key)}（{J.Str(cmd, "statusName", J.Str(cmd, "status"))}）");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ConfirmCommand(string id)
    {
        try
        {
            _core.Commands.ConfirmCommand(_user, id, "desktop");
            Refresh();
            return OpResult.Done("命令已确认下发");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult CancelCommand(string id)
    {
        try
        {
            _core.Commands.CancelCommand(_user, id, null, "desktop");
            Refresh();
            return OpResult.Done("命令已取消");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    /* ================= 参数 ================= */

    public IReadOnlyList<ParamInfo> Params
    {
        get
        {
            if (_user is null) return new List<ParamInfo>();
            try
            {
                var list = _core.Params.ListParams(_user, _siteId);
                var items = J.Objs(J.Get(list, "items")).Select(MapParam).ToList();
                // 详情（版本历史）只在参数表加载时补一次，页面用 p.Versions 做回滚下拉。
                foreach (var p in items)
                {
                    try
                    {
                        var detail = _core.Params.ParamDetail(_user, p.Id);
                        var versions = J.Objs(J.Get(detail, "versions"));
                        p.Versions = versions.Select(v => new ParamVersion
                        {
                            Version = J.Int(v, "version"),
                            Value = J.NumOr(v, "value", 0),
                            At = ShortTime(J.Str(v, "at")),
                            By = J.Str(v, "operatorName", J.Str(v, "operatorId")),
                            Reason = J.Str(v, "reason"),
                        }).ToList();
                    }
                    catch (CoreException) { }
                }
                return items;
            }
            catch (CoreException) { return new List<ParamInfo>(); }
        }
    }

    private static ParamInfo MapParam(JsonObject p) => new()
    {
        Id = J.Str(p, "id"),
        SiteId = J.Str(p, "siteId"),
        Key = J.Str(p, "key"),
        Name = J.Str(p, "name"),
        Group = J.Str(p, "group"),
        Current = J.NumOr(p, "currentValue", 0),
        Min = J.NumOr(p, "min", 0),
        Max = J.NumOr(p, "max", 0),
        Step = J.NumOr(p, "step", 0.1),
        Unit = J.Str(p, "unit"),
        WriteMode = J.Str(p, "writeMode", "cloud"),
        Version = J.Int(p, "version", 1),
        UpdatedAt = ShortTime(J.Str(p, "updatedAt")),
        UpdatedBy = J.Str(p, "updatedByName", J.Str(p, "updatedBy")),
        Desc = J.Str(p, "desc"),
    };

    public IReadOnlyList<ParamRequest> ParamRequests
    {
        get
        {
            if (_user is null) return new List<ParamRequest>();
            try
            {
                var list = _core.Params.ListRequests(_user, _siteId, "pending");
                return J.Objs(J.Get(list, "items")).Select(r => new ParamRequest
                {
                    Id = J.Str(r, "id"),
                    ParamId = J.Str(r, "paramId"),
                    ParamName = J.Str(r, "paramName"),
                    Value = J.NumOr(r, "requestedValue", 0),
                    Unit = J.Str(r, "unit"),
                    Reason = J.Str(r, "reason"),
                    Applicant = J.Str(r, "requesterName"),
                    At = (long)TimeUtil.Parse(r, "createdAt"),
                    Status = J.Str(r, "status", "pending"),
                }).ToList();
            }
            catch (CoreException) { return new List<ParamRequest>(); }
        }
    }

    public OpResult SetParam(string id, double value, string reason)
    {
        try
        {
            var r = _core.Params.ChangeParam(_user, id, JsonValue.Create(value), reason, "desktop");
            Refresh();
            return OpResult.Done(J.Str(r, "message") is { Length: > 0 } m ? m : "参数变更已提交");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult RollbackParam(string id, int version, string reason)
    {
        try
        {
            var r = _core.Params.RollbackParam(_user, id, JsonValue.Create(version), reason, "desktop");
            Refresh();
            return OpResult.Done(J.Str(r, "message") is { Length: > 0 } m ? m : $"已回滚到 v{version}");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ApproveParamRequest(string id, bool approve)
    {
        try
        {
            if (approve) _core.Params.ApproveRequest(_user, id, "桌面端审批通过", "desktop");
            else _core.Params.RejectRequest(_user, id, "桌面端审批驳回", "desktop");
            Refresh();
            return OpResult.Done(approve ? "参数申请已通过" : "参数申请已驳回");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    /* ================= 化验 / 诊断 ================= */

    public IReadOnlyList<LabMetricDef> LabMetrics { get; } = new List<LabMetricDef>
    {
        new() { Key = "COD", Name = "COD", Unit = "mg/L" },
        new() { Key = "NH3N", Name = "氨氮", Unit = "mg/L" },
        new() { Key = "TN", Name = "总氮", Unit = "mg/L" },
        new() { Key = "TP", Name = "总磷", Unit = "mg/L" },
        new() { Key = "SS", Name = "悬浮物", Unit = "mg/L" },
        new() { Key = "TURB", Name = "浊度", Unit = "NTU" },
        new() { Key = "pH", Name = "pH", Unit = "" },
    };

    public IReadOnlyList<LabRecord> Labs
    {
        get
        {
            if (_user is null) return new List<LabRecord>();
            try
            {
                var list = _core.Ops.ListLab(_user, _siteId, pageSize: 100);
                return J.Objs(J.Get(list, "items")).Select(l => new LabRecord
                {
                    Id = J.Str(l, "id"),
                    SiteId = J.Str(l, "siteId"),
                    SamplePoint = J.Str(l, "samplePoint"),
                    SamplePointName = J.Str(l, "samplePointName", J.Str(l, "samplePointText")),
                    SampledAt = (long)TimeUtil.Parse(l, "sampledAt"),
                    Status = J.Str(l, "status", "pending_review"),
                    Metrics = J.O(l, "metrics", false).ToDictionary(kv => kv.Key, kv => J.AsDouble(kv.Value)),
                    Remark = J.Str(l, "remark"),
                    Recorder = J.Str(l, "operatorName"),
                    Reviewer = J.Str(l, "reviewerName"),
                }).ToList();
            }
            catch (CoreException) { return new List<LabRecord>(); }
        }
    }

    public LabStats LabStatistics(int days)
    {
        var stats = new LabStats();
        if (_user is null) return stats;
        try
        {
            var s = _core.Ops.LabStats(_user, _siteId, days);
            var colors = new[] { "#0B6BB5", "#1C6B45", "#8A5A12", "#2A5F8A", "#A32C22" };
            var i = 0;
            foreach (var kv in J.O(s, "series", false))
            {
                var ts = new TrendSeries
                {
                    Code = kv.Key,
                    Name = (i == 0 ? "出水 " : "") + J.Str(J.O(s, "metricNames", false), kv.Key, kv.Key),
                    Unit = "mg/L",
                    Color = colors[i % colors.Length],
                };
                foreach (var p in J.Objs(kv.Value))
                {
                    var v = J.Num(p, "v");
                    if (v is null) continue;
                    ts.Points.Add(new MetricPoint { T = (long)TimeUtil.Parse(p, "t"), V = v.Value });
                }
                if (ts.Points.Count > 0) stats.Series.Add(ts);
                i++;
            }

            foreach (var point in J.O(s, "latestByPoint", false))
            {
                var rec = point.Value as JsonObject;
                if (rec is null) continue;
                foreach (var m in J.O(rec, "metrics", false))
                    stats.LatestByPoint[$"{point.Key}.{m.Key}"] = J.AsDouble(m.Value);
            }
            foreach (var kv in J.O(s, "removalRates", false))
                stats.RemovalRates[kv.Key] = J.AsDouble(kv.Value);

            var compliance = J.O(s, "compliance", false);
            var rates = new List<double>();
            foreach (var key in new[] { "COD", "NH3N" })
                if (J.Num(J.O(compliance, key, false), "rate") is { } r) rates.Add(r);
            stats.Compliance = rates.Count == 0 ? 100 : (int)Math.Round(rates.Average());
            stats.Total = J.Int(s, "total");
            stats.Reviewed = Math.Max(0, stats.Total - J.Int(s, "pending"));
        }
        catch (CoreException) { }
        return stats;
    }

    public OpResult ReviewLab(string id, bool approve)
    {
        try
        {
            _core.Ops.ReviewLab(_user, id, new JsonObject { ["approved"] = approve, ["note"] = approve ? "桌面端复核通过" : "桌面端退回重测" }, "desktop");
            Refresh();
            return OpResult.Done(approve ? "化验记录已复核通过" : "化验记录已退回");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public IReadOnlyList<DiagnosisInfo> Diagnoses
    {
        get
        {
            if (_user is null) return new List<DiagnosisInfo>();
            JsonArray items;
            try
            {
                var d = _core.Admin.Diagnosis(_user, _siteId, _core.Diagnosis);
                items = J.Get(d, "items") as JsonArray ?? new JsonArray();
            }
            catch (CoreException)
            {
                items = _core.Diagnosis.List(_siteId);
            }
            return J.Objs(items).Select(MapDiagnosis).ToList();
        }
    }

    private static DiagnosisInfo MapDiagnosis(JsonObject d)
    {
        var checks = new List<string>();
        if (J.Get(d, "checks") is JsonArray ca)
            foreach (var c in J.Objs(ca))
                checks.Add($"{J.Str(c, "item")}：{J.Str(c, "how")}（{J.Str(c, "expect")}）");
        var basis = J.O(d, "basis", false);
        var basisParts = new List<string>();
        if (J.Get(basis, "points") is JsonArray pts)
            foreach (var p in J.Objs(pts))
                basisParts.Add($"{J.Str(p, "name")} {JNumText(J.Num(p, "value"))} {J.Str(p, "unit")}");
        if (J.Str(basis, "rule") is { Length: > 0 } rule) basisParts.Add(rule);
        if (J.Str(basis, "trend") is { Length: > 0 } trend) basisParts.Add(trend);
        return new DiagnosisInfo
        {
            Id = J.Str(d, "id"),
            Level = J.Str(d, "level") switch { "high" or "critical" => "warning", "low" => "ok", _ => "attention" },
            Title = J.Str(d, "title"),
            Phenomenon = J.Str(d, "phenomenon"),
            Causes = J.Get(d, "causes") is JsonArray c2 ? J.Objs(c2).Select(x => J.AsString(x) ?? "").ToList() : new List<string>(),
            Checks = checks,
            Boundary = J.Str(d, "boundary"),
            Basis = string.Join("；", basisParts),
            Suggestion = J.Str(d, "suggestion"),
            At = ParseLocalMs(d, "generatedAt"),
        };
    }

    /// <summary>Core 的 generatedAt 是「yyyy-MM-dd HH:mm:ss」本地时间字符串，不能用 UTC 解析。</summary>
    private static long ParseLocalMs(JsonObject o, string key)
    {
        if (J.Get(o, key) is JsonValue value && value.TryGetValue<string>(out var s) &&
            DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        }
        return (long)TimeUtil.Parse(o, key);
    }

    /* ================= 分析 / 报表 ================= */

    public AnalysisData Analysis(int days)
    {
        var data = new AnalysisData();
        if (_user is null) return data;
        try
        {
            var s = _core.Reports.Summary(_user, _siteId, days);
            var water = J.O(s, "water", false);
            var energy = J.O(s, "energy", false);
            var cost = J.O(s, "cost", false);
            var alarms = J.O(s, "alarms", false);
            var wos = J.O(s, "workorders", false);
            var avail = J.O(s, "availability", false);
            string F(double? v, int d) => v is null ? "—" : v.Value.ToString("F" + d, CultureInfo.InvariantCulture);
            data.Kpis.Add(new KpiInfo { Key = "water", Label = "累计处理水量", Value = F(J.Num(water, "total"), 2), Unit = "m³", Tone = "normal", Source = "gateway" });
            data.Kpis.Add(new KpiInfo { Key = "energy", Label = "累计耗电", Value = F(J.Num(energy, "total"), 1), Unit = "kWh", Tone = "normal", Source = "gateway" });
            data.Kpis.Add(new KpiInfo { Key = "energyPerM3", Label = "吨水电耗", Value = F(J.Num(energy, "perM3"), 2), Unit = "kWh/m³", Tone = "normal", Source = "gateway" });
            data.Kpis.Add(new KpiInfo { Key = "cost", Label = "累计运行成本", Value = F(J.Num(cost, "total"), 2), Unit = "元", Tone = "normal", Source = "gateway" });
            data.Kpis.Add(new KpiInfo { Key = "costPerM3", Label = "吨水成本", Value = F(J.Num(cost, "perM3"), 2), Unit = "元/m³", Tone = "normal", Source = "gateway" });
            data.Kpis.Add(new KpiInfo { Key = "alarms", Label = "告警总数", Value = J.Int(alarms, "total").ToString(CultureInfo.InvariantCulture), Unit = "条", Tone = "warn", Source = "system" });
            data.Kpis.Add(new KpiInfo { Key = "workorders", Label = "新增工单", Value = J.Int(wos, "created").ToString(CultureInfo.InvariantCulture), Unit = "单", Tone = "normal", Source = "system" });
            data.Kpis.Add(new KpiInfo { Key = "availability", Label = "点位在线率", Value = F(J.Num(avail, "dataRate"), 1), Unit = "%", Tone = "good", Source = "system" });

            var daily = J.Objs(J.Get(s, "daily")).ToDictionary(d => J.Str(d, "day"), StringComparer.Ordinal);
            var from = TimeUtil.StartOfDay(TimeUtil.Now() - (days - 1) * TimeUtil.DayMs);
            var waterSeries = new TrendSeries { Code = "water", Name = "处理水量", Unit = "m³", Color = "#0B6BB5" };
            var energySeries = new TrendSeries { Code = "energy", Name = "耗电", Unit = "kWh", Color = "#1C6B45" };
            var costSeries = new TrendSeries { Code = "cost", Name = "运行成本", Unit = "元", Color = "#8A5A12" };
            for (var i = 0; i < days; i++)
            {
                var t = from + i * TimeUtil.DayMs;
                var key = TimeUtil.DayKey(t);
                var d = daily.GetValueOrDefault(key);
                waterSeries.Points.Add(new MetricPoint { T = t, V = d is null ? 0 : J.NumOr(d, "water", 0) });
                energySeries.Points.Add(new MetricPoint { T = t, V = d is null ? 0 : J.NumOr(d, "energy", 0) });
                costSeries.Points.Add(new MetricPoint { T = t, V = d is null ? 0 : J.NumOr(d, "cost", 0) });
            }
            data.Daily.Add(waterSeries);
            data.Daily.Add(energySeries);
            data.Daily.Add(costSeries);
            data.CostBreakdown.Add(new KeyValuePair<string, double>("电费", J.NumOr(cost, "electricity", 0)));
            data.CostBreakdown.Add(new KeyValuePair<string, double>("药剂费", J.NumOr(cost, "chemicals", 0)));

            var alarmDaily = J.O(s, "alarmDaily", false);
            foreach (var kv in alarmDaily.OrderBy(k => k.Key))
                data.AlarmDaily.Add(new KeyValuePair<string, int>(kv.Key, J.AsLong(kv.Value) is { } n ? (int)n : 0));

            var cards = _core.Analytics.UnitCards(_siteId);
            var cardMap = J.Objs(J.Get(cards, "items")).ToDictionary(c => J.Str(c, "code"), StringComparer.Ordinal);
            foreach (var u in Units)
            {
                var card = cardMap.GetValueOrDefault(u.Code);
                var rs = BuildReadings().Where(r => r.UnitCode == u.Code).Take(2).Select(r => $"{r.Name} {r.Text}");
                data.Units.Add(new UnitEfficiency
                {
                    UnitCode = u.Code,
                    UnitName = u.Name,
                    Readings = string.Join(" · ", rs),
                    Alarms = card is null ? 0 : J.Int(J.O(card, "alarms", false), "total"),
                    Health = card is null ? 100 : J.Int(J.O(card, "health", false), "score", J.Int(card, "health", 100)),
                    Note = u.Note,
                });
            }
            foreach (var n in J.Objs(J.Get(s, "notes"))) data.Notes.Add(J.AsString(n) ?? "");
        }
        catch (CoreException) { }
        return data;
    }

    public IReadOnlyList<ReportInfo> Reports
    {
        get
        {
            if (_user is null) return new List<ReportInfo>();
            try
            {
                var list = _core.Reports.ListReports(_user, _siteId, pageSize: 100);
                return J.Objs(J.Get(list, "items")).Select(r =>
                {
                    var rep = new ReportInfo
                    {
                        Id = J.Str(r, "id"),
                        SiteId = J.Str(r, "siteId"),
                        Type = J.Str(r, "type", "daily"),
                        Title = J.Str(r, "title"),
                        Status = J.Str(r, "status", "ready"),
                        CreatedAt = (long)TimeUtil.Parse(r, "createdAt"),
                        CreatedBy = J.Str(r, "creatorName", J.Str(r, "createdBy")),
                        RangeText = $"{ShortTime(J.Str(r, "periodFrom"))} ~ {ShortTime(J.Str(r, "periodTo"))}",
                    };
                    try
                    {
                        var sections = _core.Reports.Sections(r);
                        foreach (var sec in J.Objs(sections))
                        {
                            var rs = new ReportSection { Title = J.Str(sec, "title") };
                            var columns = J.Strings(J.Get(sec, "columns"));
                            if (J.Get(sec, "rows") is JsonArray rows)
                                foreach (var row in rows)
                                {
                                    if (row is not JsonArray cells) continue;
                                    var key = cells.Count > 0 ? CellText(cells[0]) : "";
                                    var rest = cells.Skip(1).Select(CellText).Where(x => x.Length > 0).ToList();
                                    rs.Rows.Add(new KeyValuePair<string, string>(key, string.Join(" · ", rest)));
                                }
                            rep.Sections.Add(rs);
                        }
                    }
                    catch (CoreException) { }
                    return rep;
                }).ToList();
            }
            catch (CoreException) { return new List<ReportInfo>(); }
        }
    }

    private static string CellText(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s ?? "";
            if (v.TryGetValue<double>(out var d)) return d.ToString("0.##", CultureInfo.InvariantCulture);
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<bool>(out var b)) return b ? "是" : "否";
        }
        return n.ToJsonString();
    }

    public OpResult GenerateReport(string type)
    {
        try
        {
            var r = _core.Reports.CreateReport(_user, new JsonObject { ["type"] = type, ["siteId"] = _siteId }, "desktop");
            Refresh();
            return OpResult.Done($"报表已生成：{J.Str(r, "title", J.Str(r, "id"))}");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ExportReportCsv(string id, string filePath)
    {
        try
        {
            var rep = Reports.FirstOrDefault(r => r.Id == id);
            if (rep is null) return OpResult.Fail("报表不存在");
            var sb = new StringBuilder();
            sb.AppendLine(rep.Title.Replace(',', '，'));
            foreach (var sec in rep.Sections)
            {
                sb.AppendLine();
                sb.AppendLine(sec.Title.Replace(',', '，'));
                foreach (var row in sec.Rows)
                    sb.AppendLine($"{row.Key.Replace(',', '，')},{row.Value.Replace(',', '，')}");
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            return OpResult.Done($"已导出 CSV：{Path.GetFileName(filePath)}");
        }
        catch (Exception ex) { return OpResult.Fail("导出失败：" + ex.Message); }
    }

    /* ================= 工单 / 巡检 / 设备 ================= */

    public IReadOnlyList<WorkOrderInfo> WorkOrders(string status = "all")
    {
        if (_user is null) return new List<WorkOrderInfo>();
        try
        {
            var list = _core.Ops.ListWorkorders(_user, _siteId, status is "all" ? null : status, pageSize: 200);
            return J.Objs(J.Get(list, "items")).Select(w =>
            {
                var timeline = new List<TimelineEntry>();
                if (J.Get(w, "timeline") is JsonArray tl)
                    foreach (var t in J.Objs(tl))
                        timeline.Add(new TimelineEntry
                        {
                            At = (long)TimeUtil.Parse(t, "at"),
                            Actor = ActorName(J.Str(t, "by")),
                            Text = J.Str(t, "note") is { Length: > 0 } note ? note : J.Str(t, "action"),
                        });
                return new WorkOrderInfo
                {
                    Id = J.Str(w, "id"),
                    SiteId = J.Str(w, "siteId"),
                    Title = J.Str(w, "title"),
                    Type = J.Str(w, "type", "repair"),
                    Priority = J.Str(w, "priority", "normal"),
                    Status = J.Str(w, "status", "pending"),
                    UnitCode = J.Str(w, "unitCode"),
                    UnitName = J.Str(w, "unitName"),
                    DeviceName = J.Str(w, "deviceName"),
                    Assignee = J.Str(w, "assigneeName"),
                    Creator = J.Str(w, "creatorName"),
                    Source = J.Str(w, "source"),
                    AlarmId = J.Str(w, "alarmId"),
                    CreatedAt = (long)TimeUtil.Parse(w, "createdAt"),
                    DueAt = J.Get(w, "dueAt") is null ? null : (long)TimeUtil.Parse(w, "dueAt"),
                    DoneAt = J.Get(w, "finishedAt") is null && J.Get(w, "doneAt") is null ? null
                        : (long)TimeUtil.Parse(J.Get(w, "finishedAt") is null ? w : J.O(w, "doneAt", false), J.Get(w, "finishedAt") is null ? "doneAt" : "finishedAt"),
                    Desc = J.Str(w, "description"),
                    Result = J.Str(w, "result"),
                    Timeline = timeline,
                };
            }).ToList();
        }
        catch (CoreException) { return new List<WorkOrderInfo>(); }
    }

    public OpResult AdvanceWorkOrder(string id, string action, string note)
    {
        try
        {
            switch (action)
            {
                case "accept":
                    _core.Ops.AcceptWorkorder(_user, id, new JsonObject { ["note"] = note }, "desktop");
                    break;
                case "process":
                    _core.Ops.StartWorkorder(_user, id, new JsonObject { ["note"] = string.IsNullOrWhiteSpace(note) ? "开始处理" : note }, "desktop");
                    break;
                case "done":
                    _core.Ops.FinishWorkorder(_user, id, new JsonObject { ["result"] = note }, "desktop");
                    break;
                case "close":
                    _core.Ops.CloseWorkorder(_user, id, new JsonObject { ["note"] = string.IsNullOrWhiteSpace(note) ? "验收关闭" : note }, "desktop");
                    break;
                default:
                    return OpResult.Fail("未知操作：" + action);
            }
            Refresh();
            return OpResult.Done(action switch
            {
                "accept" => "工单已接单",
                "process" => "工单已开始处理",
                "done" => "工单已提交处理结果，待验收",
                "close" => "工单已验收关闭",
                _ => "工单状态已更新",
            });
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public IReadOnlyList<InspectionInfo> Inspections
    {
        get
        {
            if (_user is null) return new List<InspectionInfo>();
            try
            {
                var planNames = J.Objs(_core.Ops.InspectionPlans(_user, _siteId)).ToDictionary(p => J.Str(p, "id"), p => J.Str(p, "name"), StringComparer.Ordinal);
                var list = _core.Ops.ListInspections(_user, _siteId);
                return J.Objs(J.Get(list, "items")).Select(i => new InspectionInfo
                {
                    Id = J.Str(i, "id"),
                    SiteId = J.Str(i, "siteId"),
                    Plan = planNames.GetValueOrDefault(J.Str(i, "planId"), J.Str(i, "title")),
                    Status = J.Str(i, "status", "pending"),
                    Assignee = J.Str(i, "assigneeName"),
                    DueAt = (long)TimeUtil.Parse(i, "dueAt"),
                    DoneAt = J.Get(i, "doneAt") is null ? null : (long)TimeUtil.Parse(i, "doneAt"),
                    Items = J.Objs(J.Get(i, "items")).Select(x => new InspectionItem
                    {
                        Key = J.Str(x, "key"),
                        Name = J.Str(x, "label"),
                        Result = J.Str(x, "result"),
                        Note = J.Str(x, "note"),
                    }).ToList(),
                    Summary = J.Str(i, "summary"),
                    Abnormal = J.Objs(J.Get(i, "items")).Count(x => J.Str(x, "result") == "abnormal"),
                }).ToList();
            }
            catch (CoreException) { return new List<InspectionInfo>(); }
        }
    }

    public OpResult GenerateInspection()
    {
        try
        {
            var plans = J.Objs(_core.Ops.InspectionPlans(_user, _siteId));
            if (plans.Count == 0) return OpResult.Fail("当前站点没有可用的巡检计划");
            var plan = plans[0];
            var r = _core.Ops.CreateInspection(_user, new JsonObject { ["planId"] = J.Str(plan, "id") }, "desktop");
            Refresh();
            return OpResult.Done($"已生成巡检任务：{J.Str(r, "title", J.Str(r, "id"))}");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult SubmitInspection(string id, string summary, int abnormal)
    {
        try
        {
            var detail = _core.Ops.InspectionDetail(_user, id);
            var items = new JsonArray();
            var i = 0;
            foreach (var it in J.Objs(J.Get(detail, "items")))
            {
                var result = i < abnormal ? "abnormal" : "ok";
                items.Add(new JsonObject { ["key"] = J.Str(it, "key"), ["result"] = result, ["note"] = i < abnormal ? "现场发现异常，已记录" : "" });
                i++;
            }
            _core.Ops.SubmitInspection(_user, id, new JsonObject { ["summary"] = summary, ["items"] = items }, "desktop");
            Refresh();
            return OpResult.Done("巡检结果已提交" + (abnormal > 0 ? "，异常项已自动建单" : ""));
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            if (_user is null) return new List<DeviceInfo>();
            try
            {
                var list = _core.Analytics.Devices(_user, _siteId);
                return J.Objs(J.Get(list, "items")).Select(d => new DeviceInfo
                {
                    Id = J.Str(d, "id"),
                    SiteId = J.Str(d, "siteId"),
                    Code = J.Str(d, "code"),
                    Name = J.Str(d, "name"),
                    UnitCode = J.Str(d, "unitCode"),
                    UnitName = J.Str(d, "unitName"),
                    Type = J.Str(d, "type"),
                    Model = J.Str(d, "model"),
                    Spec = J.Str(d, "spec"),
                    Status = J.Str(d, "status"),
                    Online = J.Bool(d, "online"),
                    RuntimeHours = J.NumOr(d, "runtimeHours", 0),
                    Starts = J.Int(d, "starts"),
                    MaintCycleDays = J.Int(d, "maintCycleDays", 90),
                    LastMaintAt = J.Str(d, "lastMaintAt"),
                    WarrantyUntil = J.Str(d, "warrantyUntil"),
                    PlAddress = J.Str(d, "plAddress"),
                    InstallDate = J.Str(d, "installDate"),
                    Health = J.Int(J.O(d, "health", false), "score", J.Int(d, "health", 100)),
                }).ToList();
            }
            catch (CoreException) { return new List<DeviceInfo>(); }
        }
    }

    public OpResult RegisterMaintenance(string deviceId, string note)
    {
        try
        {
            _core.Analytics.AddMaintenance(_user, deviceId, new JsonObject { ["note"] = note }, "desktop");
            Refresh();
            return OpResult.Done("维护记录已登记");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    /* ================= 系统管理 ================= */

    public IReadOnlyList<UserRowInfo> Users
    {
        get
        {
            if (_user is null) return new List<UserRowInfo>();
            try
            {
                var list = _core.Admin.Users(_user);
                return J.Objs(J.Get(list, "items")).Select(u => new UserRowInfo { User = MapUser(u) }).ToList();
            }
            catch (CoreException) { return new List<UserRowInfo>(); }
        }
    }

    private static UserInfo MapUser(JsonObject u) => new()
    {
        Id = J.Str(u, "id"),
        Username = J.Str(u, "username"),
        Name = J.Str(u, "name"),
        Role = J.Str(u, "role"),
        RoleName = J.Str(u, "roleName"),
        Title = J.Str(u, "title"),
        Phone = J.Str(u, "phone"),
        Perms = (J.Get(u, "perms") as JsonArray)?.Select(x => J.AsString(x) ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>(),
        SiteIds = (J.Get(u, "siteScope") as JsonArray ?? J.Get(u, "siteIds") as JsonArray)?.Select(x => J.AsString(x) ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>(),
        Status = J.Str(u, "status", "active"),
        LastLoginAt = ShortTime(J.Str(u, "lastLoginAt")),
    };

    public IReadOnlyList<RoleInfo> Roles
    {
        get
        {
            if (_user is null) return new List<RoleInfo>();
            try
            {
                var users = Users;
                var arr = _core.Admin.Roles(_user);
                return J.Objs(arr).Select(r => new RoleInfo
                {
                    Key = J.Str(r, "id"),
                    Name = J.Str(r, "name"),
                    Desc = J.Str(r, "desc"),
                    Perms = (J.Get(r, "perms") as JsonArray)?.Select(x => J.AsString(x) ?? "").ToList() ?? new List<string>(),
                    Sites = J.Str(r, "sites", "assigned"),
                    UserCount = users.Count(u => u.User.Role == J.Str(r, "id")),
                }).ToList();
            }
            catch (CoreException) { return new List<RoleInfo>(); }
        }
    }

    public OpResult AddUser(string username, string name, string role, string phone)
    {
        try
        {
            var r = _core.Admin.CreateUser(_user, new JsonObject
            {
                ["username"] = username,
                ["name"] = name,
                ["role"] = role,
                ["phone"] = phone,
                ["password"] = "water123",
                ["siteIds"] = new JsonArray { _siteId },
            }, "desktop");
            Refresh();
            return OpResult.Done($"用户已创建：{J.Str(r, "name")}（初始密码 water123）");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult ResetPassword(string userId)
    {
        try
        {
            _core.Admin.ResetPassword(_user, userId, new JsonObject { ["password"] = "water123" }, "desktop");
            return OpResult.Done("密码已重置为 water123，该用户旧会话已失效");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult SetUserStatus(string userId, bool active)
    {
        try
        {
            _core.Admin.UpdateUser(_user, userId, new JsonObject { ["status"] = active ? "active" : "inactive" }, "desktop");
            Refresh();
            return OpResult.Done(active ? "账号已启用" : "账号已停用");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    private JsonObject _settingsRaw = new();

    public IReadOnlyList<KeyValuePair<string, string>> Settings
    {
        get
        {
            if (_user is null) return new List<KeyValuePair<string, string>>();
            try
            {
                _settingsRaw = _core.Admin.Settings(_user);
                var list = new List<KeyValuePair<string, string>>();
                foreach (var kv in _settingsRaw)
                {
                    if (kv.Value is JsonObject nested)
                    {
                        foreach (var nk in nested)
                            if (nk.Value is JsonValue)
                                list.Add(new KeyValuePair<string, string>($"{kv.Key}.{nk.Key}", nk.Value?.ToString() ?? ""));
                        continue;
                    }
                    if (kv.Value is JsonArray || kv.Value is JsonObject) continue;
                    list.Add(new KeyValuePair<string, string>(kv.Key, kv.Value?.ToString() ?? ""));
                }
                return list;
            }
            catch (CoreException) { return new List<KeyValuePair<string, string>>(); }
        }
    }

    public OpResult SaveSetting(string key, string value)
    {
        try
        {
            JsonObject body;
            if (key.StartsWith("quietHours.", StringComparison.Ordinal) || key.StartsWith("backup.", StringComparison.Ordinal))
            {
                var idx = key.IndexOf('.');
                body = new JsonObject { [key[..idx]] = new JsonObject { [key[(idx + 1)..]] = ParseValue(value) } };
            }
            else
            {
                body = new JsonObject { [key] = ParseValue(value) };
            }
            _core.Admin.UpdateSettings(_user, body, "desktop");
            Refresh();
            return OpResult.Done("设置已保存");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    private static JsonNode? ParseValue(string value)
    {
        if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
        if (long.TryParse(value, out var l)) return JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    public IReadOnlyList<ChannelInfo> Channels
    {
        get
        {
            if (_user is null) return new List<ChannelInfo>();
            try
            {
                return J.Objs(_core.Admin.Channels(_user)).Select(c => new ChannelInfo
                {
                    Id = J.Str(c, "id"),
                    Name = J.Str(c, "name"),
                    Type = J.Str(c, "type"),
                    Enabled = J.Bool(c, "enabled"),
                    Target = J.Str(c, "target"),
                    LastTestAt = ShortTime(J.Str(c, "testedAt")),
                    LastResult = J.Str(c, "status"),
                }).ToList();
            }
            catch (CoreException) { return new List<ChannelInfo>(); }
        }
    }

    public OpResult ToggleChannel(string id)
    {
        try
        {
            var ch = J.Objs(_core.Admin.Channels(_user)).FirstOrDefault(c => J.Str(c, "id") == id);
            if (ch is null) return OpResult.Fail("通知通道不存在");
            _core.Admin.UpdateChannel(_user, id, new JsonObject { ["enabled"] = !J.Bool(ch, "enabled") }, "desktop");
            Refresh();
            return OpResult.Done("通道状态已更新");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult TestChannel(string id)
    {
        try
        {
            var r = _core.Admin.TestChannel(_user, id, "desktop");
            return OpResult.Done(J.Str(r, "message", "测试消息已发送"));
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public IReadOnlyList<AuditInfo> AuditLogs(int limit = 200)
    {
        if (_user is null) return new List<AuditInfo>();
        try
        {
            var list = _core.Admin.AuditLogs(_user, pageSize: Math.Max(1, limit));
            return J.Objs(J.Get(list, "items")).Select(a => new AuditInfo
            {
                Id = J.Str(a, "id"),
                At = (long)TimeUtil.Parse(a, "at"),
                Actor = J.Str(a, "actorName"),
                Action = J.Str(a, "actionName") is { Length: > 0 } an ? an : J.Str(a, "action"),
                TargetType = J.Str(a, "targetType"),
                TargetId = J.Str(a, "targetId"),
                Detail = J.Str(a, "detail"),
                Result = J.Str(a, "result", "success"),
                Ip = J.Str(a, "ip"),
            }).ToList();
        }
        catch (CoreException) { return new List<AuditInfo>(); }
    }

    public IReadOnlyList<BackupInfo> Backups
    {
        get
        {
            if (_user is null) return new List<BackupInfo>();
            try
            {
                var list = _core.Admin.Backups(_user);
                return J.Objs(J.Get(list, "items")).Select(b => new BackupInfo
                {
                    Id = J.Str(b, "id"),
                    At = (long)TimeUtil.Parse(b, "at"),
                    Size = FormatSize(J.Num(b, "size")),
                    By = J.Str(b, "createdBy"),
                    Note = J.Str(b, "note"),
                    IsAuto = J.Bool(b, "auto"),
                }).ToList();
            }
            catch (CoreException) { return new List<BackupInfo>(); }
        }
    }

    private static string FormatSize(double? bytes)
    {
        if (bytes is null) return "—";
        var b = bytes.Value;
        return b >= 1024 * 1024 ? (b / 1024 / 1024).ToString("F1", CultureInfo.InvariantCulture) + " MB"
            : b >= 1024 ? (b / 1024).ToString("F0", CultureInfo.InvariantCulture) + " KB"
            : b.ToString("F0", CultureInfo.InvariantCulture) + " B";
    }

    public OpResult CreateBackup()
    {
        try
        {
            var r = _core.Admin.CreateBackup(_user, new JsonObject { ["note"] = "桌面端手工备份" }, "desktop");
            Refresh();
            return OpResult.Done($"备份已创建：{J.Str(r, "file", J.Str(r, "id"))}");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    public OpResult RestoreBackup(string id)
    {
        try
        {
            _core.Admin.RestoreBackup(_user, id, new JsonObject { ["confirm"] = true }, "desktop");
            Refresh();
            return OpResult.Done("备份已恢复，界面数据已重新加载");
        }
        catch (CoreException ex) { return OpResult.Fail(ex.Message); }
    }

    /* ================= 通知 / 触摸屏 ================= */

    private JsonObject _notifications = new();
    private long _notificationsAt;

    private JsonObject NotificationsRaw()
    {
        if (_user is null) return new JsonObject();
        if (TimeUtil.Now() - _notificationsAt < 400 && _notifications.Count > 0) return _notifications;
        try
        {
            _notifications = _core.Admin.ListNotifications(_user, false, pageSize: 50);
            _notificationsAt = TimeUtil.Now();
        }
        catch (CoreException) { _notifications = new JsonObject(); }
        return _notifications;
    }

    public IReadOnlyList<NotificationInfo> Notifications =>
        J.Objs(J.Get(NotificationsRaw(), "items")).Select(n => new NotificationInfo
        {
            Id = J.Str(n, "id"),
            Title = J.Str(n, "title"),
            Body = J.Str(n, "body"),
            At = (long)TimeUtil.Parse(n, "at"),
            Read = J.Bool(n, "read"),
            Kind = J.Str(n, "type", "system"),
        }).ToList();

    public int UnreadNotifications => J.Int(NotificationsRaw(), "unread");

    public void MarkNotificationsRead()
    {
        try
        {
            _core.Admin.MarkNotificationsRead(_user, null);
            _notificationsAt = 0;
            _notifications = new JsonObject();
        }
        catch (CoreException) { }
    }

    public KioskData Kiosk()
    {
        var k = new KioskData();
        if (_user is null) return k;
        var kpis = DashboardKpis();
        k.StatusCards = kpis.Take(5).ToList();
        k.StatusCards.Add(new KpiInfo
        {
            Key = "updated", Label = "数据更新", Value = _lastUpdated.ToString("HH:mm:ss"),
            Unit = "", Tone = "good", Hint = "每 5 秒推进一次采集", Source = "system",
        });
        k.Units = UnitSnapshots().ToList();
        k.RecentAlarms = Alarms("open").Take(5).ToList();
        var actions = Actions;
        k.QuickActions = actions.Where(a => a.Key.Contains("backwash", StringComparison.OrdinalIgnoreCase))
            .Concat(actions.Where(a => !a.Key.Contains("backwash", StringComparison.OrdinalIgnoreCase)))
            .Take(4).ToList();
        return k;
    }

    /* ================= 站点 / 工具 ================= */

    private static List<SiteInfo> MapSites(JsonArray arr) => J.Objs(arr).Select(s => new SiteInfo
    {
        Id = J.Str(s, "id"),
        Name = J.Str(s, "name"),
        ShortName = J.Str(s, "shortName"),
        Region = J.Str(s, "region"),
        Area = J.Str(s, "area"),
        Address = J.Str(s, "address"),
        Process = J.Str(s, "process"),
        Capacity = J.Str(s, "capacity"),
        DeviceRef = J.Str(s, "deviceRef"),
        CommissionedAt = J.Str(s, "commissionedAt"),
        Contact = J.Str(s, "contact"),
        Status = J.Str(s, "status", "online"),
        Note = J.Str(s, "note"),
    }).ToList();

    private static List<SiteInfo> MapSites(JsonObject obj) =>
        MapSites(J.Get(obj, "items") as JsonArray ?? (obj["id"] is null ? new JsonArray() : new JsonArray()));

    private static string ShortTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        return iso;
    }
}

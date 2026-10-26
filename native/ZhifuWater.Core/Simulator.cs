using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 计时器宿主。默认按真实时间；<see cref="SpeedFactor"/> 可整体缩放，
/// 自测时可把 5 秒 tick、命令 500ms/800ms 调度压缩成几十毫秒。
/// </summary>
public sealed class TimerHost : IDisposable
{
    private readonly List<Timer> _timers = new();
    private readonly object _gate = new();
    private bool _disposed;

    public double SpeedFactor { get; set; } = 1.0;

    private long Scaled(TimeSpan d)
    {
        var ms = (long)Math.Round(d.TotalMilliseconds * SpeedFactor);
        return Math.Max(1, ms);
    }

    /// <summary>一次性定时。</summary>
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            lock (_gate) _timers.Remove(timer!);
            timer?.Dispose();
            if (_disposed) return;
            try { action(); }
            catch (Exception) { /* 定时回调异常不影响主循环 */ }
        }, null, Scaled(delay), Timeout.Infinite);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>周期定时。</summary>
    public IDisposable Every(TimeSpan period, Action action)
    {
        var timer = new Timer(_ =>
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception) { /* 同上 */ }
        }, null, Scaled(period), Scaled(period));
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    public void Cancel(IDisposable? handle)
    {
        if (handle is Timer t)
        {
            lock (_gate) _timers.Remove(t);
            t.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var t in _timers) t.Dispose();
            _timers.Clear();
        }
    }
}

/// <summary>站点工艺状态（原料罐 / BAF / PAC / PAM / 硫自养池 / 清水池 / 废水箱 / 能耗）。</summary>
public sealed class SiteState
{
    public RawState Raw = new();
    public BafState Baf = new();
    public DoseState Pac = new();
    public DoseState Pam = new();
    public BafState Sulfur = new();
    public double ClearLevel;
    public double WasteLevel;
    public double PowerKw;
    public double PowerKwh;
}

public sealed class RawState
{
    public double Level;
    public bool PumpRun;
    public int PumpMode;
    public bool Fault;
    public double Hours;
    public double Starts;
    public bool Refill;
    public double? Trip;
}

public sealed class BafState
{
    public double Ph;
    public double Do;
    /// <summary>硫自养池总磷（mg/L）；BAF 不用这个字段。</summary>
    public double P;
    public bool Blower;
    public double Dp;
    public int BwPhase;
    public double BwCount;
    public double? BwAt;
    public double BwRemain;
}

public sealed class DoseState
{
    public double Level;
    public double Dose;
    public bool Run;
    public double Total;
    public bool Mixer;
}

/// <summary>
/// 内置工艺模拟器（演示用「模拟网关 + PLC」）。对齐 <c>server/sim.js</c>。
/// 只模拟图纸已确认/演示选配点位的工程量，不伪造 TN/COD/氨氮/总磷/SS 在线值；
/// 所有输出都带时间戳与来源；控制动作执行前做联锁校验。
/// </summary>
public sealed class Simulator
{
    public const int TICK_MS = 5000;

    public static readonly (int phase, string name, int sec)[] BwPhases =
    {
        (1, "气冲", 30),
        (2, "气水联合冲洗", 90),
        (3, "漂洗", 45),
    };

    private readonly Store _store;
    private readonly Telemetry _telemetry;
    private readonly Dictionary<string, SiteState> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonObject>> _pointsBySite = new(StringComparer.Ordinal);
    private readonly List<Action<JsonObject>> _listeners = new();
    private readonly TimerHost _timers;
    private IDisposable? _timer;
    private readonly object _gate = new();

    /// <summary>发布事件（由 CoreServices 注入到事件总线）。</summary>
    public Action<JsonObject>? Publish;

    /// <summary>日志回调。</summary>
    public Action<string>? Log;

    public Simulator(Store store, Telemetry telemetry, TimerHost? timers = null)
    {
        _store = store;
        _telemetry = telemetry;
        _timers = timers ?? new TimerHost();
        BuildState();
        var db = _store.Data;
        foreach (var p in J.ObjsIn(db, "points"))
        {
            var sid = J.Str(p, "siteId");
            if (!_pointsBySite.TryGetValue(sid, out var list))
            {
                list = new List<JsonObject>();
                _pointsBySite[sid] = list;
            }
            list.Add(p);
        }
    }

    private JsonObject Db => _store.Data;

    private void BuildState()
    {
        _state["site-1"] = new SiteState
        {
            Raw = new RawState { Level = 22.6, PumpRun = true, PumpMode = 0, Fault = false, Hours = 1132.5, Starts = 187 },
            Baf = new BafState { Ph = 7.12, Do = 1.78, Blower = true, Dp = 18.2, BwPhase = 0, BwCount = 12 },
            Pac = new DoseState { Level = 18.4, Dose = 0.06, Run = true, Total = 412.6, Mixer = true },
            Pam = new DoseState { Level = 62.3, Dose = 0.02, Run = true, Total = 210.4, Mixer = true },
            Sulfur = new BafState { Ph = 6.76, Do = 0.29, P = 0.41, Dp = 16.4, BwPhase = 0, BwCount = 9 },
            ClearLevel = 58,
            WasteLevel = 44,
            PowerKw = 1.36,
            PowerKwh = 1284.6,
        };
        _state["site-2"] = new SiteState
        {
            Raw = new RawState { Level = 24.1, PumpRun = true, PumpMode = 0, Fault = false, Hours = 486.2, Starts = 92 },
            Baf = new BafState { Ph = 7.2, Do = 3.05, Blower = true, Dp = 17.1, BwPhase = 0, BwCount = 5 },
            Pac = new DoseState { Level = 55.2, Dose = 0.05, Run = true, Total = 168.2, Mixer = true },
            Pam = new DoseState { Level = 48.7, Dose = 0.02, Run = true, Total = 88.5, Mixer = true },
            Sulfur = new BafState { Ph = 7.02, Do = 0.31, P = 0.38, Dp = 15.2, BwPhase = 0, BwCount = 4 },
            ClearLevel = 51,
            WasteLevel = 31,
            PowerKw = 1.05,
            PowerKwh = 640.2,
        };
    }

    public SiteState? StateOf(string siteId) => _state.TryGetValue(siteId, out var s) ? s : null;

    public bool IsRunning => _timer is not null;

    public IDisposable On(Action<JsonObject> fn)
    {
        _listeners.Add(fn);
        return new Unsubscriber(() => _listeners.Remove(fn));
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _dispose;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    private void Emit(JsonObject evt)
    {
        foreach (var fn in _listeners.ToList())
            try { fn(evt); } catch (Exception) { /* 单个订阅者异常不影响主循环 */ }
        Publish?.Invoke(evt);
    }

    /* ---------------- 回填 ---------------- */

    /// <summary>用 30 天小时级 + 24 小时 5 分钟级回填历史，保证曲线一打开就有内容。</summary>
    public void Backfill(long? nowArg = null)
    {
        long now = nowArg ?? TimeUtil.Now();
        var rand = new Mulberry32(20261025);
        foreach (var site in J.ObjsIn(Db, "sites"))
        {
            var siteId = J.Str(site, "id");
            if (!_pointsBySite.TryGetValue(siteId, out var points)) points = new List<JsonObject>();
            if (!_state.TryGetValue(siteId, out var st)) continue;
            foreach (var p in points)
            {
                var code = J.Str(p, "code");
                var series = new List<(long t, double v)>();
                long start = now - 30 * TimeUtil.DayMs;
                // 先小时级铺 30 天（分析页 30 天窗口与逐日曲线需要）
                for (long t = start; t < now - TimeUtil.DayMs; t += TimeUtil.HourMs)
                    series.Add((t, HistValue(code, t, now, st, rand)));
                // 最近 24 小时 5 分钟级
                for (long t = now - TimeUtil.DayMs; t < now - 300_000; t += 300_000)
                    series.Add((t, HistValue(code, t, now, st, rand)));
                var pid = J.Str(p, "id");
                foreach (var (t, v) in series) _telemetry.PushHist(pid, t, JsonValue.Create(v), "good");
                // 最新值 = 当前状态，保证首屏与告警一致
                var cur = CurrentValue(siteId, p);
                if (cur is not null)
                    _telemetry.SetLatest(pid, JsonValue.Create(cur), now, "good", J.Str(p, "source"));
            }
        }
        Log?.Invoke($"[sim] 已回填 30 天历史（{J.ObjsIn(Db, "points").Count} 个点位）");
    }

    /// <summary>历史曲线：以当前状态为锚的解析序列，事件窗口用于让种子告警在曲线上可解释。</summary>
    public double HistValue(string code, long t, long now, SiteState st, Mulberry32 rand)
    {
        double ago = (now - t) / 60000.0; // 分钟
        double noise = rand.NextDouble() - 0.5;
        switch (code)
        {
            case "RAW.LVL":
            {
                double baseV = 46 + 14 * Math.Sin((t / 36000000.0) * Math.PI * 2 - 1.15)
                               + 5 * Math.Sin((t / 86400000.0) * Math.PI * 2);
                if (ago < 120) return JsMath.Round(22.6 + (ago / 120) * 21.4 + noise * 0.4, 1);
                return JsMath.Round(JsMath.Clamp(baseV + noise * 2, 15, 92), 1);
            }
            case "RAW.PUMP.RUN": return ago < 18 ? 0 : 1;
            case "RAW.PUMP.FAULT": return 0;
            case "RAW.PUMP.MODE": return 0;
            case "RAW.PUMP.HOURS": return JsMath.Round(st.Raw.Hours - (ago / 60) * 0.18, 1);
            case "RAW.PUMP.STARTS": return Math.Max(0, st.Raw.Starts - Math.Floor(ago / 180));
            case "FLOW.IN": return JsMath.Round(JsMath.Clamp(0.1 + noise * 0.012, 0.07, 0.13), 3);
            case "BAF.PH":
                if (ago < 25) return JsMath.Round(7.05 + (25 - ago) * 0.0028 + noise * 0.01, 2);
                return JsMath.Round(7.15 + 0.08 * Math.Sin((t / 28000000.0) * Math.PI * 2) + noise * 0.04, 2);
            case "BAF.DO":
                if (ago < 12) return JsMath.Round(1.78 + (12 - ago) * 0.1, 2);
                return JsMath.Round(JsMath.Clamp(3.0 + 0.3 * Math.Sin((t / 14400000.0) * Math.PI * 2) + noise * 0.15, 1.4, 6.4), 2);
            case "BAF.BLOWER.RUN": return ago < 52 * 60 && ago > 50 * 60 ? 0 : 1;
            case "BAF.BLOWER.CURRENT": return JsMath.Round(1.62 + noise * 0.06, 2);
            case "BAF.BW.STATE":
                // 26 小时前安排过反冲洗，用于解释压差回落
                if (ago > 25.9 * 60 && ago < 26.05 * 60) return 1;
                if (ago >= 25.85 * 60 && ago <= 25.9 * 60) return 2;
                return 0;
            case "BAF.BW.COUNT": return st.Baf.BwCount - (ago > 25.9 * 60 ? 1 : 0);
            case "BAF.DP":
                if (ago > 25.9 * 60) return JsMath.Round(26.2 + noise * 0.5, 1);
                return JsMath.Round(18.2 + noise * 0.8, 1);
            case "PAC.PUMP.RUN": return 1;
            case "PAC.DOSE": return 0.06;
            case "PAC.LEVEL":
                if (ago < 30) return JsMath.Round(18.4 + (30 - ago) * 0.02, 1);
                return JsMath.Round(JsMath.Clamp(58 + 32 * Math.Sin((t / 12000000.0) * Math.PI * 2) + noise * 3, 16, 92), 1);
            case "PAC.TOTAL": return JsMath.Round(st.Pac.Total - (ago / 60) * 0.06, 1);
            case "PAC.MIXER.RUN": return 1;
            case "PAM.PUMP.RUN": return 1;
            case "PAM.DOSE": return 0.02;
            case "PAM.LEVEL": return JsMath.Round(JsMath.Clamp(60 + 25 * Math.Sin((t / 15000000.0) * Math.PI * 2) + noise * 3, 15, 92), 1);
            case "PAM.TOTAL": return JsMath.Round(st.Pam.Total - (ago / 60) * 0.02, 1);
            case "PAM.MIXER.RUN": return 1;
            case "SULFUR.PH":
                if (ago < 90) return JsMath.Round(6.76 + (90 - ago) * 0.0018 + noise * 0.01, 2);
                return JsMath.Round(7.05 + 0.05 * Math.Sin((t / 30000000.0) * Math.PI * 2) + noise * 0.03, 2);
            case "SULFUR.DO": return JsMath.Round(JsMath.Clamp(0.32 + 0.04 * Math.Sin((t / 10000000.0) * Math.PI * 2) + noise * 0.02, 0.15, 0.8), 2);
            case "SULFUR.P": return JsMath.Round(JsMath.Clamp(0.4 + 0.05 * Math.Sin((t / 5000000.0) * Math.PI * 2) + noise * 0.03, 0.25, 0.58), 2);
            case "SULFUR.BW.STATE": return 0;
            case "SULFUR.BW.COUNT": return st.Sulfur.BwCount;
            case "SULFUR.DP": return JsMath.Round(16.4 + noise * 0.8, 1);
            case "CLEAR.LVL": return JsMath.Round(JsMath.Clamp(58 + 6 * Math.Sin((t / 20000000.0) * Math.PI * 2) + noise * 2, 20, 95), 1);
            case "WASTE.LVL": return JsMath.Round(JsMath.Clamp(40 + 18 * Math.Sin((t / 18000000.0) * Math.PI * 2) + noise * 3, 8, 80), 1);
            case "POWER.KW": return JsMath.Round(JsMath.Clamp(1.3 + 0.18 * Math.Sin((t / 16000000.0) * Math.PI * 2) + noise * 0.05, 0.9, 1.8), 2);
            case "ENERGY.KWH": return JsMath.Round(st.PowerKwh - (ago / 60) * 1.38, 1);
            default: return 0;
        }
    }

    /// <summary>当前点位值（由工艺状态派生）；未知点位返回 null。</summary>
    public double? CurrentValue(string siteId, JsonObject point)
    {
        if (!_state.TryGetValue(siteId, out var s)) return null;
        var code = J.Str(point, "code");
        switch (code)
        {
            case "RAW.LVL": return JsMath.Round(s.Raw.Level, 1);
            case "RAW.PUMP.RUN": return s.Raw.PumpRun ? 1 : 0;
            case "RAW.PUMP.FAULT": return s.Raw.Fault ? 1 : 0;
            case "RAW.PUMP.MODE": return s.Raw.PumpMode;
            case "RAW.PUMP.HOURS": return JsMath.Round(s.Raw.Hours, 1);
            case "RAW.PUMP.STARTS": return s.Raw.Starts;
            case "FLOW.IN":
                return s.Raw.PumpRun
                    ? JsMath.Round(0.1 + Math.Sin(TimeUtil.Now() / 4000.0) * 0.003, 3)
                    : 0;
            case "BAF.PH": return JsMath.Round(s.Baf.Ph, 2);
            case "BAF.DO": return JsMath.Round(s.Baf.Do, 2);
            case "BAF.BLOWER.RUN": return s.Baf.Blower ? 1 : 0;
            case "BAF.BLOWER.CURRENT": return s.Baf.Blower ? 1.62 : 0;
            case "BAF.BW.STATE": return s.Baf.BwPhase;
            case "BAF.BW.COUNT": return s.Baf.BwCount;
            case "BAF.DP": return JsMath.Round(s.Baf.Dp, 1);
            case "PAC.PUMP.RUN": return s.Pac.Run ? 1 : 0;
            case "PAC.DOSE": return JsMath.Round(s.Pac.Dose, 2);
            case "PAC.LEVEL": return JsMath.Round(s.Pac.Level, 1);
            case "PAC.TOTAL": return JsMath.Round(s.Pac.Total, 1);
            case "PAC.MIXER.RUN": return s.Pac.Mixer ? 1 : 0;
            case "PAM.PUMP.RUN": return s.Pam.Run ? 1 : 0;
            case "PAM.DOSE": return JsMath.Round(s.Pam.Dose, 2);
            case "PAM.LEVEL": return JsMath.Round(s.Pam.Level, 1);
            case "PAM.TOTAL": return JsMath.Round(s.Pam.Total, 1);
            case "PAM.MIXER.RUN": return s.Pam.Mixer ? 1 : 0;
            case "SULFUR.PH": return JsMath.Round(s.Sulfur.Ph, 2);
            case "SULFUR.DO": return JsMath.Round(s.Sulfur.Do, 2);
            case "SULFUR.P": return JsMath.Round(s.Sulfur.P, 2);
            case "SULFUR.BW.STATE": return s.Sulfur.BwPhase;
            case "SULFUR.BW.COUNT": return s.Sulfur.BwCount;
            case "SULFUR.DP": return JsMath.Round(s.Sulfur.Dp, 1);
            case "CLEAR.LVL": return JsMath.Round(s.ClearLevel, 1);
            case "WASTE.LVL": return JsMath.Round(s.WasteLevel, 1);
            case "POWER.KW": return JsMath.Round(s.PowerKw, 2);
            case "ENERGY.KWH": return JsMath.Round(s.PowerKwh, 1);
            default: return null;
        }
    }

    /* ---------------- 生命周期 ---------------- */

    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            Backfill();
            WriteTelemetry(TimeUtil.Now());
            _timer = _timers.Every(TimeSpan.FromMilliseconds(TICK_MS), () => Tick());
        }
        Log?.Invoke("[sim] 模拟网关已启动，采样周期 5 s");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_timer is not null) _timers.Cancel(_timer);
            _timer = null;
        }
    }

    /// <summary>推进一个采样周期。</summary>
    public void Tick(double dtSec = TICK_MS / 1000.0)
    {
        long now = TimeUtil.Now();
        var updates = new JsonArray();
        foreach (var site in J.ObjsIn(Db, "sites"))
        {
            var siteId = J.Str(site, "id");
            if (!_state.TryGetValue(siteId, out var s)) continue;
            StepSite(siteId, s, dtSec, now);
            foreach (var p in _pointsBySite.TryGetValue(siteId, out var list) ? list : new List<JsonObject>())
            {
                var v = CurrentValue(siteId, p);
                if (v is null) continue;
                var pid = J.Str(p, "id");
                _telemetry.SetLatest(pid, JsonValue.Create(v.Value), now, "good", J.Str(p, "source"));
                updates.Add(new JsonObject
                {
                    ["pointId"] = pid,
                    ["siteId"] = siteId,
                    ["code"] = J.Str(p, "code"),
                    ["v"] = v.Value,
                    ["t"] = now,
                });
                _telemetry.Record(pid, JsonValue.Create(v.Value), "good", now);
            }
        }
        Emit(new JsonObject { ["type"] = "telemetry", ["t"] = now, ["updates"] = updates });
    }

    public void StepSite(string siteId, SiteState s, double dt, long now)
    {
        // —— 原料罐与提升泵 ——
        var r = s.Raw;
        bool wasRun = r.PumpRun;
        if (r.PumpRun) r.Level -= 0.012 * dt;
        // 补水阀：低液位自动补水，到 70% 关闭
        if (r.Level < 25) r.Refill = true;
        if (r.Refill && r.Level >= 70) r.Refill = false;
        if (r.Refill && !r.PumpRun) r.Level += 0.03 * dt;
        if (r.Level <= 20 && r.PumpRun)
        {
            r.PumpRun = false;
            r.Trip = now;
        }
        if (!r.PumpRun && !r.Refill && r.Level >= 30 && r.PumpMode == 0 && !r.Fault) r.PumpRun = true;
        r.Level = JsMath.Clamp(r.Level, 0, 100);
        if (r.PumpRun) r.Hours += dt / 3600;
        if (!wasRun && r.PumpRun) r.Starts++;
        if (r.PumpRun) r.Starts = Math.Max(r.Starts, 1);

        // —— BAF ——
        var b = s.Baf;
        if (b.BwPhase != 0)
        {
            StepBackwash(b, dt, now, () =>
            {
                b.BwCount++;
                b.BwPhase = 0;
                b.Dp = Math.Max(8, b.Dp - 12);
            });
            b.Do = JsMath.Clamp(b.Do + 0.02 * dt, 0, 12);
        }
        else
        {
            double target = b.Blower ? 3.0 : 0.6;
            b.Do += (target - b.Do) * 0.012 * dt;
            b.Do = JsMath.Clamp(b.Do + (Random.Shared.NextDouble() - 0.5) * 0.01, 0, 12);
            b.Ph += (7.15 - b.Ph) * 0.0008 * dt + (Random.Shared.NextDouble() - 0.5) * 0.002;
            b.Ph = JsMath.Clamp(b.Ph, 4, 10);
            b.Dp = JsMath.Clamp(b.Dp + 0.002 * dt, 2, 45);
            if (b.Dp > 24 && now - (b.BwAt ?? 0) > 20 * 3600000L) StartBackwash(siteId, "baf");
        }

        // —— PAC / PAM ——
        foreach (var (d, max) in new[] { (s.Pac, 0.2), (s.Pam, 0.1) })
        {
            if (d.Run)
            {
                d.Level -= d.Dose * 0.006 * dt;
                d.Total += d.Dose * dt / 3600;
                if (d.Level <= 3) d.Level = 3;
            }
            d.Level = JsMath.Clamp(d.Level, 0, 100);
            d.Dose = JsMath.Clamp(d.Dose, 0, max);
        }

        // —— 硫自养池 ——
        var su = s.Sulfur;
        if (su.BwPhase != 0)
        {
            StepBackwash(su, dt, now, () =>
            {
                su.BwCount++;
                su.BwPhase = 0;
                su.Dp = Math.Max(6, su.Dp - 10);
                su.P = 0.4;
            });
            su.P = JsMath.Clamp(su.P + 0.01 * dt, 0.2, 0.85);
        }
        else
        {
            su.Ph += (7.05 - su.Ph) * 0.0006 * dt + (Random.Shared.NextDouble() - 0.5) * 0.0015;
            su.Ph = JsMath.Clamp(su.Ph, 4, 10);
            su.Do += (0.32 - su.Do) * 0.02 * dt + (Random.Shared.NextDouble() - 0.5) * 0.004;
            su.Do = JsMath.Clamp(su.Do, 0, 3);
            su.P = JsMath.Clamp(0.4 + (Random.Shared.NextDouble() - 0.5) * 0.008, 0.25, 0.6);
            su.Dp = JsMath.Clamp(su.Dp + 0.0015 * dt, 2, 40);
        }

        // —— 液位与能耗 ——
        s.ClearLevel = JsMath.Clamp(s.ClearLevel + (s.Raw.PumpRun ? 0.004 : -0.002) * dt, 0, 100);
        s.WasteLevel = JsMath.Clamp(s.WasteLevel + (s.Baf.BwPhase != 0 ? 0.02 : -0.003) * dt, 0, 100);
        double baseKw = 0.55 + (s.Baf.Blower ? 0.38 : 0) + (s.Pac.Run ? 0.12 : 0) + (s.Pam.Run ? 0.1 : 0);
        double bwKw = (s.Baf.BwPhase != 0 || s.Sulfur.BwPhase != 0) ? 0.35 : 0;
        s.PowerKw = JsMath.Clamp(baseKw + bwKw + (Random.Shared.NextDouble() - 0.5) * 0.04, 0.3, 5);
        s.PowerKwh += s.PowerKw * dt / 3600;
    }

    public void StepBackwash(BafState unit, double dt, long now, Action done)
    {
        if (unit.BwPhase == 0) return;
        if (unit.BwRemain == 0) unit.BwRemain = BwPhases[unit.BwPhase - 1].sec;
        unit.BwRemain -= dt;
        if (unit.BwRemain <= 0)
        {
            if (unit.BwPhase < 3)
            {
                unit.BwPhase += 1;
                unit.BwRemain = BwPhases[unit.BwPhase - 1].sec;
            }
            else
            {
                unit.BwRemain = 0;
                done();
            }
        }
    }

    public JsonObject StartBackwash(string siteId, string unitCode)
    {
        if (!_state.TryGetValue(siteId, out var s))
            return new JsonObject { ["ok"] = false, ["error"] = "站点不存在" };
        var unit = unitCode == "sulfur" ? s.Sulfur : s.Baf;
        if (unit.BwPhase != 0)
            return new JsonObject { ["ok"] = false, ["error"] = "反冲洗正在进行中" };
        if (s.WasteLevel >= 92)
            return new JsonObject { ["ok"] = false, ["error"] = "废水箱液位过高，排水阀联锁禁止启动反冲洗" };
        unit.BwPhase = 1;
        unit.BwRemain = BwPhases[0].sec;
        unit.BwAt = TimeUtil.Now();
        if (unitCode == "sulfur") unit.P = 0.55;
        return new JsonObject
        {
            ["ok"] = true,
            ["detail"] = $"{(unitCode == "sulfur" ? "硫自养池" : "BAF")} 反冲洗已启动：气冲 → 气水联合 → 漂洗",
        };
    }

    public JsonObject StopBackwash(string siteId, string unitCode)
    {
        if (!_state.TryGetValue(siteId, out var s))
            return new JsonObject { ["ok"] = false, ["error"] = "站点不存在" };
        var unit = unitCode == "sulfur" ? s.Sulfur : s.Baf;
        if (unit.BwPhase == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "当前没有进行中的反冲洗" };
        unit.BwPhase = 0;
        unit.BwRemain = 0;
        return new JsonObject { ["ok"] = true, ["detail"] = "反冲洗已停止（现场确认后执行）" };
    }

    /// <summary>命令执行前的联锁校验。</summary>
    public JsonObject Precheck(string siteId, string action, JsonObject? args = null)
    {
        args ??= new JsonObject();
        if (!_state.TryGetValue(siteId, out var s))
            return Fail("站点不存在");
        switch (action)
        {
            case "raw.pump.start":
                if (s.Raw.Level < 5) return Fail("原料罐液位低于 5%，低液位保护禁止启泵");
                if (s.Raw.Fault) return Fail("提升泵故障信号存在，禁止启动");
                return Ok();
            case "raw.pump.stop":
                return Ok();
            case "raw.pump.mode.set":
            {
                var mode = J.Num(args, "mode");
                if (mode is null || (mode.Value != 0 && mode.Value != 1))
                    return Fail("模式值必须是 0（自动）或 1（手动）");
                return new JsonObject
                {
                    ["ok"] = true,
                    ["warning"] = (int)mode.Value == 1 ? "切换手动模式需现场确认，平台将记录审计" : null,
                };
            }
            case "baf.blower.start":
                return Ok();
            case "baf.blower.stop":
                if (s.Baf.Do < 2.5)
                    return Fail($"当前溶解氧 {JsMath.ToFixed(s.Baf.Do, 2)} mg/L，DO 联锁禁止停曝气风机");
                return new JsonObject { ["ok"] = true, ["warning"] = "停风机将影响硝化效果，请确认工艺允许" };
            case "baf.backwash.start":
            case "sulfur.backwash.start":
            {
                var u = action.StartsWith("sulfur", StringComparison.Ordinal) ? s.Sulfur : s.Baf;
                if (u.BwPhase != 0) return Fail("反冲洗正在进行中");
                if (s.WasteLevel >= 92) return Fail("废水箱液位过高，排水阀联锁禁止启动");
                return new JsonObject { ["ok"] = true, ["warning"] = "反冲洗期间出水水质可能短时波动，请确认下游可接受" };
            }
            case "pac.dose.set":
            case "pam.dose.set":
            {
                var v = J.Num(args, "value");
                double max = action.StartsWith("pac", StringComparison.Ordinal) ? 0.2 : 0.1;
                if (v is null || double.IsNaN(v.Value) || v.Value < 0 || v.Value > max)
                    return Fail($"投加量须在 0–{J.NumText(max)} L/h 之间");
                return Ok();
            }
            default:
                return Fail($"未知控制动作：{action}");
        }
    }

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    public JsonObject Execute(string siteId, string action, JsonObject? args = null)
    {
        args ??= new JsonObject();
        var pre = Precheck(siteId, action, args);
        if (!J.Bool(pre, "ok")) return pre;
        var s = StateOf(siteId)!;
        switch (action)
        {
            case "raw.pump.start": s.Raw.PumpRun = true; break;
            case "raw.pump.stop": s.Raw.PumpRun = false; break;
            case "raw.pump.mode.set": s.Raw.PumpMode = (int)(J.Num(args, "mode") ?? 0); break;
            case "baf.blower.start": s.Baf.Blower = true; break;
            case "baf.blower.stop": s.Baf.Blower = false; break;
            case "baf.backwash.start": return StartBackwash(siteId, "baf");
            case "sulfur.backwash.start": return StartBackwash(siteId, "sulfur");
            case "baf.backwash.stop": return StopBackwash(siteId, "baf");
            case "sulfur.backwash.stop": return StopBackwash(siteId, "sulfur");
            case "pac.dose.set": s.Pac.Dose = J.Num(args, "value") ?? 0; break;
            case "pam.dose.set": s.Pam.Dose = J.Num(args, "value") ?? 0; break;
            default: return Fail("未知控制动作");
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["detail"] = "已执行",
            ["warning"] = pre["warning"]?.DeepClone(),
        };
    }

    public void WriteTelemetry(long now)
    {
        foreach (var site in J.ObjsIn(Db, "sites"))
        {
            var siteId = J.Str(site, "id");
            foreach (var p in _pointsBySite.TryGetValue(siteId, out var list) ? list : new List<JsonObject>())
            {
                var v = CurrentValue(siteId, p);
                if (v is not null)
                    _telemetry.SetLatest(J.Str(p, "id"), JsonValue.Create(v.Value), now, "good", J.Str(p, "source"));
            }
        }
    }

    /// <summary>该站点是否由内置模拟网关托管（用于「网关已连接/未知」判断）。</summary>
    /// <summary>取站点实时状态（等价 Node 的 <c>sim.state[siteId]</c>，测试与调试可直接读取）。</summary>
    public SiteState? State(string siteId) => _state.TryGetValue(siteId, out var s) ? s : null;

    public bool HasState(string siteId) => _state.ContainsKey(siteId);

    public JsonObject? Status(string siteId)
    {
        if (!_state.TryGetValue(siteId, out var s)) return null;
        return new JsonObject
        {
            ["raw"] = new JsonObject
            {
                ["level"] = s.Raw.Level, ["pumpRun"] = s.Raw.PumpRun, ["pumpMode"] = s.Raw.PumpMode,
                ["fault"] = s.Raw.Fault, ["hours"] = s.Raw.Hours, ["starts"] = s.Raw.Starts,
                ["refill"] = s.Raw.Refill, ["trip"] = s.Raw.Trip,
            },
            ["baf"] = new JsonObject
            {
                ["ph"] = s.Baf.Ph, ["do"] = s.Baf.Do, ["blower"] = s.Baf.Blower, ["dp"] = s.Baf.Dp,
                ["bwPhase"] = s.Baf.BwPhase, ["bwCount"] = s.Baf.BwCount, ["bwAt"] = s.Baf.BwAt,
                ["bwRemain"] = s.Baf.BwRemain,
            },
            ["pac"] = new JsonObject
            {
                ["level"] = s.Pac.Level, ["dose"] = s.Pac.Dose, ["run"] = s.Pac.Run,
                ["total"] = s.Pac.Total, ["mixer"] = s.Pac.Mixer,
            },
            ["pam"] = new JsonObject
            {
                ["level"] = s.Pam.Level, ["dose"] = s.Pam.Dose, ["run"] = s.Pam.Run,
                ["total"] = s.Pam.Total, ["mixer"] = s.Pam.Mixer,
            },
            ["sulfur"] = new JsonObject
            {
                ["ph"] = s.Sulfur.Ph, ["do"] = s.Sulfur.Do, ["p"] = s.Sulfur.P, ["dp"] = s.Sulfur.Dp,
                ["bwPhase"] = s.Sulfur.BwPhase, ["bwCount"] = s.Sulfur.BwCount, ["bwAt"] = s.Sulfur.BwAt,
                ["bwRemain"] = s.Sulfur.BwRemain,
            },
            ["clear"] = new JsonObject { ["level"] = s.ClearLevel },
            ["waste"] = new JsonObject { ["level"] = s.WasteLevel },
            ["power"] = new JsonObject { ["kw"] = s.PowerKw, ["kwh"] = s.PowerKwh },
        };
    }
}

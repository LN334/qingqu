using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// JSON 文件存储。对齐 <c>server/store.js</c>：
/// 单进程、原子写（tmp + rename）、400ms 节流落盘、<c>_seq</c> 编号计数。
/// </summary>
public sealed class Store : IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _dirty;
    private bool _disposed;

    public string Dir { get; }
    public string FilePath { get; }
    public JsonObject Data { get; private set; } = new();
    public long? LastSavedAt { get; private set; }

    /// <summary>保存失败时的回调（桌面端可弹提示），异常不会向外抛。</summary>
    public Action<Exception>? SaveError;

    public Store(string dataDir)
    {
        Dir = Path.GetFullPath(dataDir);
        FilePath = Path.Combine(Dir, "db.json");
    }

    /// <summary>
    /// 载入数据库。存在合法 db.json（meta.version ≥ 1）就直接用，否则用 seed 重新生成。
    /// </summary>
    public JsonObject Load(Func<JsonObject>? seedFn = null, bool reset = false)
    {
        Directory.CreateDirectory(Dir);
        JsonObject? loaded = null;
        if (!reset && File.Exists(FilePath))
        {
            try
            {
                loaded = JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) as JsonObject;
            }
            catch (Exception)
            {
                // 解析失败就按种子重建，等价 Node 版的 warn 分支
                loaded = null;
            }
        }

        if (loaded is not null && loaded["meta"] is JsonObject meta && J.Num(meta, "version") >= 1)
        {
            Data = loaded;
            Migrate();
        }
        else
        {
            Data = seedFn?.Invoke() ?? new JsonObject();
            Save(force: true);
        }

        return Data;
    }

    /// <summary>结构升级钩子：补齐 meta 与 _seq 的必备键。</summary>
    public void Migrate()
    {
        if (Data["meta"] is not JsonObject) Data["meta"] = new JsonObject();
        if (Data["_seq"] is not JsonObject) Data["_seq"] = new JsonObject();
        var seq = J.O(Data, "_seq");
        foreach (var k in new[]
                 {
                     "alarm", "wo", "cmd", "diag", "audit", "lab", "insp", "paramv", "notif", "alarmRule",
                     "report", "backup", "inspection", "workorder", "command", "labRecord", "notification",
                 })
        {
            if (seq[k] is null || (J.Num(seq, k) ?? 0) == 0) seq[k] = 1;
        }
    }

    /// <summary>取下一个自增编号，形如 <c>AL00001</c>。</summary>
    public string NextId(string kind, string prefix)
    {
        var seq = J.O(Data, "_seq");
        long n = J.AsLong(seq[kind]) ?? 1;
        seq[kind] = n + 1;
        SaveSoon();
        return prefix + n.ToString("D5");
    }

    /// <summary>数字编号（不补零位之外的格式），与 NextId 共用计数器。</summary>
    public long NextSeq(string kind)
    {
        var seq = J.O(Data, "_seq");
        long n = J.AsLong(seq[kind]) ?? 1;
        seq[kind] = n + 1;
        SaveSoon();
        return n;
    }

    /// <summary>400ms 节流保存。</summary>
    public void SaveSoon()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _dirty = true;
            _timer ??= new Timer(_ =>
            {
                lock (_gate)
                {
                    _timer?.Dispose();
                    _timer = null;
                    if (_dirty) Save();
                }
            }, null, 400, Timeout.Infinite);
        }
    }

    /// <summary>立即落盘（tmp + 原子替换）。</summary>
    public void Save(bool force = false)
    {
        JsonObject snapshot;
        lock (_gate)
        {
            _dirty = false;
            snapshot = Data;
        }

        if (snapshot.Count == 0 && !force) return;
        var text = snapshot.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var tmp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            if (force) LastSavedAt = TimeUtil.Now();
        }
        catch (Exception ex)
        {
            SaveError?.Invoke(ex);
        }
    }

    /// <summary>停止定时器并强制落盘（等价 <c>store.stop()</c>）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        Save(force: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    /* ---------------- 备份（Admin 用） ---------------- */

    public string BackupDir
    {
        get
        {
            var d = Path.Combine(Dir, "backups");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>把当前 db.json 复制到备份目录，返回 (文件路径, 字节数)。</summary>
    public (string path, long size) CopyToBackup(string fileName)
    {
        Save(force: true);
        var target = Path.Combine(BackupDir, Path.GetFileName(fileName));
        File.Copy(FilePath, target, overwrite: true);
        return (target, new FileInfo(target).Length);
    }

    /// <summary>复制备份文件到备份目录并返回路径。</summary>
    public string CopyExistingBackup(string sourcePath, string fileName)
    {
        Save(force: true);
        var target = Path.Combine(BackupDir, Path.GetFileName(fileName));
        File.Copy(FilePath, target, overwrite: true);
        return target;
    }

    public List<(string file, long size, long mtimeMs)> ListBackups()
    {
        var dir = BackupDir;
        var list = new List<(string, long, long)>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var fi = new FileInfo(f);
            list.Add((Path.GetFileName(f), fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        return list;
    }

    /// <summary>替换整个业务库（从备份恢复），保留当前 settings 的覆盖关系。</summary>
    public void ReplaceData(JsonObject payload)
    {
        var settings = (Data["settings"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var kv in J.O(payload, "settings", create: false))
            settings[kv.Key] = kv.Value?.DeepClone();
        Data = payload;
        Data["settings"] = settings;
        Data["sessions"] = new JsonArray();
        Migrate();
        Save(force: true);
    }
}

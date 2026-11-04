$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'LN334/qingqu'
$tag = 'v2.0.11'
$proxy = 'http://127.0.0.1:7890'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = @(
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Setup-2.0.11.exe'); Type = 'application/octet-stream' },
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Kiosk-Setup-2.0.11.exe'); Type = 'application/octet-stream' },
  @{ Path = (Join-Path $root 'android\dist\zhifu-water-mobile.apk'); Type = 'application/vnd.android.package-archive' }
)
foreach ($a in $assets) { if (-not (Test-Path $a.Path)) { throw "缺少文件：$($a.Path)" } }

# 从 Windows 凭据管理器取 GitHub 令牌（不会打印）
$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw '未找到 GitHub 凭据，请先在 Git 中登录 GitHub' }

function New-Client([bool]$useProxy) {
  $handler = New-Object System.Net.Http.HttpClientHandler
  $handler.UseProxy = $useProxy
  if ($useProxy) { $handler.Proxy = New-Object System.Net.WebProxy($proxy) }
  $client = New-Object System.Net.Http.HttpClient($handler)
  $client.Timeout = [TimeSpan]::FromMinutes(20)
  $client.DefaultRequestHeaders.UserAgent.ParseAdd('qingqu-release-script')
  $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
  $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
  return $client
}

$proxyUp = $false
try { $proxyUp = (Test-NetConnection -ComputerName 127.0.0.1 -Port 7890 -InformationLevel Quiet -WarningAction SilentlyContinue) } catch { }
# 先直连（本机经常可间歇直连），失败再走代理
$client = New-Client $false
try {
  $probe = $client.GetAsync("https://api.github.com/rate_limit").Result
  if (-not $probe.IsSuccessStatusCode) { throw "probe $($probe.StatusCode)" }
  Write-Host '连接方式：直连'
} catch {
  if (-not $proxyUp) { throw "直连失败且 7890 代理未监听：$($_.Exception.Message)" }
  $client.Dispose()
  $client = New-Client $true
  Write-Host '连接方式：代理 127.0.0.1:7890'
}

function Get-Release {
  $r = $client.GetAsync("https://api.github.com/repos/$repo/releases/tags/$tag").Result
  if ($r.StatusCode -eq [Net.HttpStatusCode]::NotFound) { return $null }
  if (-not $r.IsSuccessStatusCode) { throw "查询 Release 失败：$($r.StatusCode)" }
  return ($r.Content.ReadAsStringAsync().Result | ConvertFrom-Json)
}

$rel = Get-Release
if (-not $rel) {
  $body = @{
    tag_name = $tag
    target_commitish = 'main'
    name = '清渠 2.0.11'
    body = @"
## 清渠 2.0.11

新增「自动控制」：按监测数据自动执行工艺动作，并自动派单。

### 自动控制（桌面端 · 运行分组）
- 8 条内置规则：BAF 压差高自动反冲洗、硫自养压差高自动反冲洗、BAF 溶解氧低自动加曝气、原料罐低液位保护停泵、来水恢复自动启泵、PAC 药箱低液位自动停加药、紧急告警自动派发抢修工单、BAF 压差持续偏高自动派单
- 每条规则带触发条件、持续判定、冷却时间与当日次数上限；命中后走与人工操作同一条命令链路（联锁校验、二次确认、回执、审计），被联锁阻止时只记原因不执行
- 页面可看规则详情、今日与累计执行次数、上次执行结果；支持单条停用、立即执行、整体暂停，全部执行记录可查

### 桌面端
- 窗口改为自绘无边框标题栏，右上角最小化/最大化/关闭与整体风格统一
- 表格滚动修好：鼠标停在表格上滚到底会继续滚整页；宽度不够时保留可读列宽并出现横向滚动条，不再把字挤成「提升泵运…」；长表在卡片内滚动、表头常驻
- 滚动条统一为 8px 细条

### 安卓端 2.2.0 (6)
- 本轮无改动，沿用上一个包

### 说明
- 自动控制只作用于本机模拟引擎，不连接现场 PLC；所有动作仍受原有联锁与权限约束
"@
  } | ConvertTo-Json -Depth 4
  $content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, 'application/json')
  $r = $client.PostAsync("https://api.github.com/repos/$repo/releases", $content).Result
  if (-not $r.IsSuccessStatusCode) { throw "创建 Release 失败：$($r.StatusCode) $($r.Content.ReadAsStringAsync().Result)" }
  $rel = $r.Content.ReadAsStringAsync().Result | ConvertFrom-Json
  Write-Host "已创建 Release：$($rel.html_url)"
} else {
  Write-Host "Release 已存在：$($rel.html_url)"
}

$existing = @{}
if ($rel.assets) { foreach ($a in $rel.assets) { $existing[$a.name] = $true } }

foreach ($a in $assets) {
  $name = [IO.Path]::GetFileName($a.Path)
  if ($existing.ContainsKey($name)) { Write-Host "跳过已存在：$name"; continue }
  $size = [math]::Round((Get-Item $a.Path).Length / 1MB, 1)
  Write-Host "上传 $name（$size MB）…"
  $fs = [IO.File]::OpenRead($a.Path)
  try {
    $content = New-Object System.Net.Http.StreamContent($fs)
    $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue($a.Type)
    $content.Headers.ContentLength = $fs.Length
    $uri = "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets?name=$([uri]::EscapeDataString($name))"
    $r = $client.PostAsync($uri, $content).Result
    if (-not $r.IsSuccessStatusCode) { throw "上传失败：$($r.StatusCode) $($r.Content.ReadAsStringAsync().Result)" }
    Write-Host '  完成'
  } finally { $fs.Dispose() }
}

$rel = Get-Release
Write-Host ''
Write-Host "发布页：$($rel.html_url)"
foreach ($a in $rel.assets) { Write-Host ("  {0}  {1:N0} B  {2}" -f $a.name, $a.size, $a.browser_download_url) }

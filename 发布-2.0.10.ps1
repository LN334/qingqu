$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'LN334/qingqu'
$tag = 'v2.0.10'
$proxy = 'http://127.0.0.1:7890'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = @(
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Setup-2.0.10.exe'); Type = 'application/octet-stream' },
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Kiosk-Setup-2.0.10.exe'); Type = 'application/octet-stream' },
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
    name = '清渠 2.0.10'
    body = @"
## 清渠 2.0.10

本轮只做细节，不加新功能。

### 桌面端
- 图表纵轴改为整齐刻度（百分比、整数、金额不再出现 65.4%、6.0、149 这类读数）；水质趋势按指标单独查看，进水与总出水两条线同轴并带单位
- 表格列宽按内容实际测量：长文本不再被截成「平台管…」「10-26 14:…」，换数据后也会重新计算
- 补齐空态：待处理工单、工单详情占位、设备与巡检列表不再出现「只有表头」的表
- 状态量显示为中文（运行/停止、自动/手动、待机/气冲/气水联合/漂洗）；数字去掉多余的尾零（1、1132.5 h、187 次）
- 报表预览按内容分列，化验记录编号不再折行；告警页操作按钮移到表格右下；工单列表默认展示全部并自动选中首条
- 卡片头右侧的按钮与链接真正右对齐（此前只留了 1px 空白）

### 现场触摸屏
- 顶部演示说明横幅移到页脚，首屏让给运行数据
- 单元卡片读数改为大字号（数值 30px），右侧卡片按内容自适应高度
- 移除与右上角时钟重复的「数据更新」格

### 安卓端 2.1.1 (5)
- 与 2.0.8 相同构建，底部导航五等分居中，品牌统一为「清渠」

### 说明
- 版本号统一由根 package.json 提供
- 仅连接本机业务层，不连接现场 PLC
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

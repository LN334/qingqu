$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'LN334/qingqu'
$tag = 'v2.0.8'
$proxy = 'http://127.0.0.1:7891'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = @(
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Setup-2.0.8.exe'); Type = 'application/octet-stream' },
  @{ Path = (Join-Path $root 'native\installer\Qingqu-Kiosk-Setup-2.0.8.exe'); Type = 'application/octet-stream' },
  @{ Path = (Join-Path $root 'android\dist\zhifu-water-mobile.apk'); Type = 'application/vnd.android.package-archive' }
)
foreach ($a in $assets) { if (-not (Test-Path $a.Path)) { throw "缺少文件：$($a.Path)" } }

# 从 Windows 凭据管理器取 GitHub 令牌（不会打印）
$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw '未找到 GitHub 凭据，请先在 Git 中登录 GitHub' }

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.Proxy = New-Object System.Net.WebProxy($proxy)
$handler.UseProxy = $true
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromMinutes(20)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('qingqu-release-script')
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
$client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

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
    name = '清渠 2.0.8'
    body = @"
## 清渠 2.0.8

### 桌面端
- 全新壳层：图标侧栏、56px 顶栏、单块指标带、统一 20px 页边距
- 登录页改为品牌分栏 + 厂区实景照片
- 驾驶舱、设备台账、工艺监控、工单页重新排版：键值对齐、数值右对齐、去掉说明性小字
- 工单：完成/验收后待办角标立即扣减；详情新增「派发」，可选负责人并记录时间线
- 实时数据改为 3 秒刷新并加入测量噪声；无仪表单元明确标为「未布点」

### 安卓端 2.1.1 (5)
- 底部导航五等分居中，标签不再左偏/截断
- 品牌统一为「清渠」

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
    Write-Host "  完成"
  } finally { $fs.Dispose() }
}

$rel = Get-Release
Write-Host ""
Write-Host "发布页：$($rel.html_url)"
foreach ($a in $rel.assets) { Write-Host ("  {0}  {1:N0} B  {2}" -f $a.name, $a.size, $a.browser_download_url) }

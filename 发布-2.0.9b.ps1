$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'LN334/qingqu'
$tag = 'v2.0.9'
$proxy = 'http://127.0.0.1:7890'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw '未找到 GitHub 凭据' }
$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'qingqu-release'; Accept = 'application/vnd.github+json' }

$notes = @"
## 清渠 2.0.9

### 桌面端
- 与网页端功能对齐：化验录入与复核、告警规则启停、参数审批（提交审批/审批列表）、工单新建与派发、巡检按计划生成与逐项判定
- 报表新增月报与自定义日期范围，预览按列成表（指标/数值/单位/说明）
- 工单完成或验收后待办角标立即扣减；已完成巡检的提交按钮自动置灰
- 登录页品牌分栏 + 厂区实景照片；壳层、指标带与各页排版延续 2.0.8

### 安卓端 2.1.1 (5)
- 与 2.0.8 相同：底部导航五等分居中，品牌统一为「清渠」

### 说明
- 版本号统一由根 package.json 提供
- 仅连接本机业务层，不连接现场 PLC
"@

$payload = @{ tag_name = $tag; target_commitish = 'main'; name = '清渠 2.0.9'; body = $notes; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 4
$bytes = [Text.Encoding]::UTF8.GetBytes($payload)
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $bytes -Proxy $proxy
Write-Host "已创建 Release：$($rel.html_url) id=$($rel.id)"

$assets = @(
  (Join-Path $root 'native\installer\Qingqu-Setup-2.0.9.exe'),
  (Join-Path $root 'native\installer\Qingqu-Kiosk-Setup-2.0.9.exe'),
  (Join-Path $root 'android\dist\zhifu-water-mobile.apk')
)
foreach ($path in $assets) {
  if (-not (Test-Path $path)) { throw "缺少文件：$path" }
  $name = [IO.Path]::GetFileName($path)
  Write-Host "上传 $name ..."
  $up = Invoke-RestMethod -Uri "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets?name=$name" -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $path -Proxy $proxy -TimeoutSec 1200
  Write-Host "  -> $($up.state) $($up.size) bytes"
}

$latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers -Proxy $proxy
Write-Host "latest=$($latest.tag_name) assets=$($latest.assets.Count)"

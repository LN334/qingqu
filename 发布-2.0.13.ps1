\uFEFF$ErrorActionPreference = 'Stop'
$repo = 'LN334/qingqu'
$tag = 'v2.0.13'
$root = 'D:\智赋乡水平台'
$desk = Join-Path $root 'native\installer\Qingqu-Setup-2.0.13.exe'
$apk  = Join-Path $root 'android\dist\zhifu-water-mobile.apk'
foreach ($f in @($desk, $apk)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw 'no github token' }

$deskSha = (Get-FileHash $desk -Algorithm SHA256).Hash.ToLower()
$apkSha  = (Get-FileHash $apk  -Algorithm SHA256).Hash.ToLower()

$bodyText = @"
## 清渠 2.0.13

### 桌面端
- 工单详情改为居中弹窗，工单列表占满整页
- 收到被指派工单时弹出模态提醒，可直接查看工单
- 工艺页单元详情支持确认告警、派工与远程控制
- 参数版本历史完整显示时间、操作人与原因
- 24 小时趋势曲线随时间自然变化，可拖动/缩放查看

### 安卓端 2.0.13 (9)
- 版本号与桌面端统一；「我的」页新增检查更新入口

### 附件 SHA256
- Qingqu-Setup-2.0.13.exe
  SHA256: $deskSha
- zhifu-water-mobile.apk
  SHA256: $apkSha
"@

$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'qingqu-release'; Accept = 'application/vnd.github+json' }
$payload = @{ tag_name = $tag; target_commitish = 'main'; name = '清渠 2.0.13'; body = $bodyText; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 4
$rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
Write-Output ("created " + $rel.id + " " + $rel.html_url)

foreach ($file in @($desk, $apk)) {
    $name = [System.IO.Path]::GetFileName($file)
    Write-Output ("upload " + $name)
    $up = Invoke-RestMethod -Method Post -Uri ("https://uploads.github.com/repos/$repo/releases/" + $rel.id + "/assets?name=" + [uri]::EscapeDataString($name)) -Headers $headers -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 900
    Write-Output ("  -> " + $up.state + " " + $up.size + " " + $up.digest)
}

$latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
Write-Output ("latest=" + $latest.tag_name + " assets=" + $latest.assets.Count)
$latest.assets | ForEach-Object { Write-Output ("  " + $_.name + " " + $_.size + " " + $_.digest) }

$ErrorActionPreference = 'Stop'
$repo = 'LN334/qingqu'
$tag = 'v2.0.11'
$root = 'D:\智赋乡水平台'
$desk = Join-Path $root 'native\installer\Qingqu-Setup-2.0.11.exe'
$apk  = Join-Path $root 'android\dist\zhifu-water-mobile.apk'
foreach ($f in @($desk, $apk)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw 'no github token' }

$bodyText = @'
## 清渠 2.0.11

### 桌面端
- 修复表格在数据刷新后文字挤叠的问题：每次重建行都重新下发列宽
- 表格改为自绘控件：行间单线、列宽按内容比例分配
- 无系统标题栏、无页面滚动条，窗口按钮自绘
- 工艺改为序批式预氧化 + 固液分离耦合自养深度脱氮

### 安卓端 2.2.0 (6)
- 原生 Android，本地引擎可离线运行
'@

$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'qingqu-release'; Accept = 'application/vnd.github+json' }
$payload = @{ tag_name = $tag; target_commitish = 'main'; name = '清渠 2.0.11'; body = $bodyText; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 4
$rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
Write-Output ("created " + $rel.id + " " + $rel.html_url)

foreach ($file in @($desk, $apk)) {
    $name = [System.IO.Path]::GetFileName($file)
    Write-Output ("upload " + $name)
    $up = Invoke-RestMethod -Method Post -Uri ("https://uploads.github.com/repos/$repo/releases/" + $rel.id + "/assets?name=" + [uri]::EscapeDataString($name)) -Headers $headers -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 900
    Write-Output ("  -> " + $up.state + " " + $up.size)
}

$latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
Write-Output ("latest=" + $latest.tag_name + " assets=" + $latest.assets.Count)
$latest.assets | ForEach-Object { Write-Output ("  " + $_.name + " " + $_.size + " " + $_.state) }

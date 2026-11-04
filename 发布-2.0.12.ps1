$ErrorActionPreference = 'Stop'
$repo = 'LN334/qingqu'
$tag = 'v2.0.12'
$root = 'D:\智赋乡水平台'
$desk = Join-Path $root 'native\installer\Qingqu-Setup-2.0.12.exe'
$apk  = Join-Path $root 'android\dist\zhifu-water-mobile.apk'
foreach ($f in @($desk, $apk)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw 'no github token' }

$bodyText = @'
## 清渠 2.0.12

### 桌面端
- 新增自动检查更新：启动后自动检查，发现新版本可一键下载、校验、静默安装并重启
- 通知中心：支持全部标为已读、单条已读，并跳转到对应页面
- 工艺参数版本历史：补全时间、操作人与原因，改为易读的逐条列表
- 液位趋势按泵运行周期自然变化，不再等幅锯齿
- Windows 与 Android 版本号统一为 2.0.12

### 安卓端 2.0.12 (8)
- 「我的」页新增「检查更新」，可直接打开发布页
- 版本号与桌面统一

SHA256:
- Qingqu-Setup-2.0.12.exe: 8c0b78fe10432eb79360888514b0ee7489734c971c08be5e5cea0739c827b44f
- zhifu-water-mobile.apk: d1e9ec34a0987d190290adbd28a81970267e63bc4b0fb1a4790651dd999be50d
'@

$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'qingqu-release'; Accept = 'application/vnd.github+json' }
$payload = @{ tag_name = $tag; target_commitish = 'main'; name = '清渠 2.0.12'; body = $bodyText; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 4
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

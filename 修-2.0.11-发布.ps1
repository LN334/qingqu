$ErrorActionPreference = 'Stop'
$repo = 'LN334/qingqu'
$relId = 433663652
$delIds = @(708804523, 708804169)
$root = 'D:\智赋乡水平台'
$desk = Join-Path $root 'native\installer\Qingqu-Setup-2.0.11.exe'

$cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) | Out-String
$token = ([regex]::Match($cred, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
if (-not $token) { throw 'no token' }
$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'qingqu-release'; Accept = 'application/vnd.github+json' }

foreach ($id in $delIds) {
    try {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$repo/releases/assets/$id" -Headers $headers | Out-Null
        Write-Output ("deleted asset " + $id)
    } catch { Write-Output ("delete failed " + $id + ": " + $_.Exception.Message) }
}

$name = [System.IO.Path]::GetFileName($desk)
$up = Invoke-RestMethod -Method Post -Uri ("https://uploads.github.com/repos/$repo/releases/$relId/assets?name=" + [uri]::EscapeDataString($name)) -Headers $headers -ContentType 'application/octet-stream' -InFile $desk -TimeoutSec 900
Write-Output ("uploaded " + $up.name + " " + $up.size + " " + $up.state)

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
$patch = @{ body = $bodyText; name = '清渠 2.0.11' } | ConvertTo-Json -Depth 3
Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$repo/releases/$relId" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($patch)) | Out-Null

$r = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$relId" -Headers $headers
Write-Output ("assets=" + $r.assets.Count)
$r.assets | ForEach-Object { Write-Output ("  " + $_.name + " " + $_.size + " " + $_.state) }
$latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
Write-Output ("latest=" + $latest.tag_name)

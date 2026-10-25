<#
  智赋乡水 · 智慧水处理平台 —— 原生安卓客户端手工构建脚本
  ------------------------------------------------------------------
  不依赖 Gradle / AndroidX / 第三方库，直接调用本机 Android SDK 构建链：
    aapt2 compile → aapt2 link（生成 R.java + 资源包）
    → javac（Java 8 字节码，bootclasspath 指向 android.jar）
    → d8（生成 classes.dex）→ 打入 APK
    → zipalign → apksigner（debug keystore）→ dist\zhifu-water-mobile.apk
  说明：aapt2 对含中文的绝对路径支持不佳，故所有构建参数均使用相对
        路径（工作目录固定为本脚本所在目录），JDK/SDK 使用纯 ASCII 绝对路径。
  用法：powershell -ExecutionPolicy Bypass -File .\build.ps1
#>

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$root = (Resolve-Path $root).Path
Set-Location $root

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Fail([string]$text) {
    Write-Host ''
    Write-Host "[失败] $text" -ForegroundColor Red
    exit 1
}

function Invoke-Tool {
    param(
        [string]$File,
        [string[]]$Arguments,
        [string]$Label
    )
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "$Label 失败（退出码 $LASTEXITCODE）"
    }
}

# ---------------------------------------------------------------- 环境定位
Write-Step '定位 JDK / Android SDK'

$sdk = $env:ANDROID_SDK_ROOT
if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = 'D:\AndroidBuild\sdk' }
if (-not (Test-Path $sdk)) { Fail "找不到 Android SDK：$sdk" }
$sdk = (Resolve-Path $sdk).Path

$jdkCandidates = @()
if (Test-Path 'D:\AndroidBuild\jdk') {
    $jdkCandidates += Get-ChildItem 'D:\AndroidBuild\jdk' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue
}
if (-not $jdkCandidates -and $env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) {
    $jdkCandidates = @(Get-Item $env:JAVA_HOME)
}
if (-not $jdkCandidates -or $jdkCandidates.Count -eq 0) { Fail '找不到 JDK 17（期望 D:\AndroidBuild\jdk\jdk-17*）' }
$jdkDir = $jdkCandidates[0].FullName

$env:JAVA_HOME = $jdkDir
$env:PATH = (Join-Path $jdkDir 'bin') + ';' + $env:PATH

$bt = Join-Path $sdk 'build-tools\34.0.0'
$aapt2 = Join-Path $bt 'aapt2.exe'
$aapt  = Join-Path $bt 'aapt.exe'
$d8 = Join-Path $bt 'd8.bat'
$dexdump = Join-Path $bt 'dexdump.exe'
$zipalign = Join-Path $bt 'zipalign.exe'
$apksigner = Join-Path $bt 'apksigner.bat'
$androidJar = Join-Path $sdk 'platforms\android-34\android.jar'

$javac = Join-Path $jdkDir 'bin\javac.exe'
$jar   = Join-Path $jdkDir 'bin\jar.exe'
$keytool = Join-Path $jdkDir 'bin\keytool.exe'

foreach ($p in @($aapt2, $aapt, $d8, $dexdump, $zipalign, $apksigner, $androidJar, $javac, $jar, $keytool)) {
    if (-not (Test-Path $p)) { Fail "缺少构建工具：$p" }
}

Write-Host "    JDK   : $jdkDir"
Write-Host "    SDK   : $sdk"
Write-Host "    Build : $bt"
Write-Host "    工程  : $root"

# ---------------------------------------------------------------- 目录准备
Write-Step '清理并创建构建目录'
if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
foreach ($d in @('build', 'build\gen', 'build\classes', 'build\dex', 'dist', 'keystore')) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# ---------------------------------------------------------------- 1 资源编译
Write-Step 'aapt2 compile：编译 res/ 资源'
Invoke-Tool $aapt2 @('compile', '--dir', 'res', '-o', 'build\res.zip') 'aapt2 compile'

# ---------------------------------------------------------------- 2 资源链接
Write-Step 'aapt2 link：生成资源包与 R.java'
Invoke-Tool $aapt2 @(
    'link',
    '-o', 'build\unsigned.apk',
    '-I', $androidJar,
    '--manifest', 'AndroidManifest.xml',
    '--java', 'build\gen',
    '--min-sdk-version', '24',
    '--target-sdk-version', '34',
    '--version-code', '1',
    '--version-name', '1.0.0',
    '--auto-add-overlay',
    'build\res.zip'
) 'aapt2 link'

$rJava = @(Get-ChildItem 'build\gen' -Recurse -Filter 'R.java' -ErrorAction SilentlyContinue)
if ($rJava.Count -eq 0) { Fail 'aapt2 link 未生成 R.java' }

# ---------------------------------------------------------------- 3 javac
Write-Step 'javac：编译 Java 源码（source/target 8，bootclasspath=android.jar）'
$sources = @()
$sources += @(Get-ChildItem 'src' -Recurse -Filter '*.java' -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName.Substring($root.Length + 1) })
$sources += @(Get-ChildItem 'build\gen' -Recurse -Filter '*.java' -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName.Substring($root.Length + 1) })
if ($sources.Count -eq 0) { Fail '没有找到 Java 源码' }
Write-Host "    源码数量：$($sources.Count)"

$javacArgs = @(
    '-encoding', 'UTF-8',
    '-source', '8',
    '-target', '8',
    '-bootclasspath', $androidJar,
    '-d', 'build\classes'
) + $sources
Invoke-Tool $javac $javacArgs 'javac'

# ---------------------------------------------------------------- 4 d8
Write-Step 'd8：生成 classes.dex'
Push-Location 'build\classes'
try {
    Invoke-Tool $jar @('cf', '..\classes.jar', '.') 'jar 打包 class'
} finally { Pop-Location }

Invoke-Tool $d8 @('--release', '--min-api', '24', '--lib', $androidJar, '--output', 'build\dex', 'build\classes.jar') 'd8'

if (-not (Test-Path 'build\dex\classes.dex')) { Fail 'd8 未生成 classes.dex' }
Write-Host ("    classes.dex : {0:N0} 字节" -f (Get-Item 'build\dex\classes.dex').Length)

# ---------------------------------------------------------------- 5 装入 APK
Write-Step '打入 classes.dex'
Invoke-Tool $jar @('uf', 'build\unsigned.apk', '-C', 'build\dex', 'classes.dex') 'jar 写入 classes.dex'

# ---------------------------------------------------------------- 6 zipalign
Write-Step 'zipalign：4 字节对齐'
Invoke-Tool $zipalign @('-f', '-p', '4', 'build\unsigned.apk', 'build\aligned.apk') 'zipalign'

# ---------------------------------------------------------------- 7 签名
Write-Step '准备 debug keystore 并签名'
if (-not (Test-Path 'keystore\debug.jks')) {
    Invoke-Tool $keytool @(
        '-genkeypair',
        '-keystore', 'keystore\debug.jks',
        '-storepass', 'android',
        '-keypass', 'android',
        '-alias', 'androiddebugkey',
        '-keyalg', 'RSA',
        '-keysize', '2048',
        '-validity', '10000',
        '-dname', 'CN=Android Debug,O=Android,C=US',
        '-storetype', 'JKS',
        '-noprompt'
    ) 'keytool 生成 debug.jks'
} else {
    Write-Host '    复用已有 keystore：keystore\debug.jks'
}

if (Test-Path 'dist\zhifu-water-mobile.apk') { Remove-Item 'dist\zhifu-water-mobile.apk' -Force }
Invoke-Tool $apksigner @(
    'sign',
    '--ks', 'keystore\debug.jks',
    '--ks-pass', 'pass:android',
    '--key-pass', 'pass:android',
    '--ks-key-alias', 'androiddebugkey',
    '--out', 'dist\zhifu-water-mobile.apk',
    'build\aligned.apk'
) 'apksigner 签名'

# ---------------------------------------------------------------- 8 校验
Write-Step '校验签名与包信息'
& $apksigner verify --verbose 'dist\zhifu-water-mobile.apk'
if ($LASTEXITCODE -ne 0) { Fail 'apksigner verify 未通过' }

$aaptOut = @(& $aapt dump badging 'dist\zhifu-water-mobile.apk' 2>&1)
if ($LASTEXITCODE -ne 0) { Fail 'aapt dump badging 失败' }
foreach ($line in ($aaptOut | Where-Object { $_ -match '^(package:|launchable-activity:|application-label:|sdkVersion:|targetSdkVersion:)' })) {
    Write-Host "    $line"
}
if (@($aaptOut | Where-Object { $_ -match '^launchable-activity:' }).Count -eq 0) {
    Fail 'APK 中没有 launchable-activity，无法从桌面启动'
}

# 无网页视图控件兜底检查（源码 + dex）。关键字由两段拼接，避免脚本自身被 grep 命中。
$needle = 'Web' + 'View'
$hits = @(Get-ChildItem 'src' -Recurse -Include '*.java' -ErrorAction SilentlyContinue |
    Select-String -Pattern $needle -SimpleMatch -ErrorAction SilentlyContinue)
if ($hits.Count -gt 0) { Fail "源码中出现网页视图控件引用：$($hits[0].Path)" }
$dexHits = @(& $dexdump -d 'build\dex\classes.dex' 2>&1 | Select-String -Pattern $needle -SimpleMatch)
if ($dexHits.Count -gt 0) { Fail 'classes.dex 中出现网页视图控件引用' }
Write-Host '    纯原生检查：通过（源码与 dex 均无网页视图控件引用）'

# ---------------------------------------------------------------- 汇总
$apkItem = Get-Item 'dist\zhifu-water-mobile.apk'
$sha = (Get-FileHash 'dist\zhifu-water-mobile.apk' -Algorithm SHA256).Hash
Write-Host ''
Write-Host '================ 构建成功 ================' -ForegroundColor Green
Write-Host ("  产物   : {0}" -f $apkItem.FullName)
Write-Host ("  大小   : {0:N0} 字节（{1:N2} MB）" -f $apkItem.Length, ($apkItem.Length / 1MB))
Write-Host ("  SHA256 : {0}" -f $sha)
Write-Host  '  包名   : com.ln334.zhifuwater'
Write-Host  '  版本   : 1.0.0 (versionCode 1)'
Write-Host  '  签名   : keystore\debug.jks / androiddebugkey'
Write-Host '==========================================='
exit 0

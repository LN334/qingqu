<#
  智赋乡水 · 智慧水处理平台 —— 原生安卓客户端手工构建脚本
  ------------------------------------------------------------------
  不依赖 Gradle / AndroidX / 第三方库，直接调用本机 Android SDK 构建链：
    aapt2 compile → aapt2 link（生成 R.java + 资源包）
    → javac（Java 8 字节码，bootclasspath 指向 android.jar）
    → d8（生成 classes.dex）→ 打入 APK
    → zipalign → apksigner（debug keystore）→ dist\zhifu-water-mobile.apk
  用法：powershell -ExecutionPolicy Bypass -File .\build.ps1
  自测：$LASTEXITCODE / $? ；aapt dump badging；apksigner verify
#>

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$root = (Resolve-Path $root).Path

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Fail([string]$text) {
    Write-Host ""
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

$jdkDir = $null
$jdkCandidates = @()
if (Test-Path 'D:\AndroidBuild\jdk') {
    $jdkCandidates += Get-ChildItem 'D:\AndroidBuild\jdk' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue
}
if (-not $jdkCandidates -and $env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) {
    $jdkCandidates = @(Get-Item $env:JAVA_HOME)
}
if ($jdkCandidates -and $jdkCandidates.Count -gt 0) { $jdkDir = $jdkCandidates[0].FullName }
if (-not $jdkDir) { Fail '找不到 JDK 17（期望 D:\AndroidBuild\jdk\jdk-17*）' }

$env:JAVA_HOME = $jdkDir
$env:PATH = (Join-Path $jdkDir 'bin') + ';' + $env:PATH

$bt = Join-Path $sdk 'build-tools\34.0.0'
$aapt2 = Join-Path $bt 'aapt2.exe'
$aapt  = Join-Path $bt 'aapt.exe'
$d8 = Join-Path $bt 'd8.bat'
$zipalign = Join-Path $bt 'zipalign.exe'
$apksigner = Join-Path $bt 'apksigner.bat'
$androidJar = Join-Path $sdk 'platforms\android-34\android.jar'

$javac = Join-Path $jdkDir 'bin\javac.exe'
$jar   = Join-Path $jdkDir 'bin\jar.exe'
$keytool = Join-Path $jdkDir 'bin\keytool.exe'

foreach ($p in @($aapt2, $aapt, $d8, $zipalign, $apksigner, $androidJar, $javac, $jar, $keytool)) {
    if (-not (Test-Path $p)) { Fail "缺少构建工具：$p" }
}

Write-Host "    JDK   : $jdkDir"
Write-Host "    SDK   : $sdk"
Write-Host "    Build : $bt"

# ---------------------------------------------------------------- 目录准备
$buildDir = Join-Path $root 'build'
$distDir  = Join-Path $root 'dist'
$ksDir    = Join-Path $root 'keystore'
$ksFile   = Join-Path $ksDir 'debug.jks'
$apkOut   = Join-Path $distDir 'zhifu-water-mobile.apk'

Write-Step '清理并创建构建目录'
if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
foreach ($d in @($buildDir, (Join-Path $buildDir 'gen'), (Join-Path $buildDir 'classes'), (Join-Path $buildDir 'dex'), $distDir, $ksDir)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
Write-Host "    $buildDir"

# ---------------------------------------------------------------- 1 资源编译
Write-Step 'aapt2 compile：编译 res/ 资源'
$resZip = Join-Path $buildDir 'res.zip'
Invoke-Tool $aapt2 @('compile', '--dir', (Join-Path $root 'res'), '-o', $resZip) 'aapt2 compile'

# ---------------------------------------------------------------- 2 资源链接
Write-Step 'aapt2 link：生成资源包与 R.java'
$unsignedApk = Join-Path $buildDir 'unsigned.apk'
$genDir = Join-Path $buildDir 'gen'
Invoke-Tool $aapt2 @(
    'link',
    '-o', $unsignedApk,
    '-I', $androidJar,
    '--manifest', (Join-Path $root 'AndroidManifest.xml'),
    '--java', $genDir,
    '--min-sdk-version', '24',
    '--target-sdk-version', '34',
    '--version-code', '1',
    '--version-name', '1.0.0',
    '--auto-add-overlay',
    $resZip
) 'aapt2 link'

$rJava = Get-ChildItem $genDir -Recurse -Filter 'R.java' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $rJava) { Fail 'aapt2 link 未生成 R.java' }
Write-Host "    R.java : $($rJava.FullName)"

# ---------------------------------------------------------------- 3 javac
Write-Step 'javac：编译 Java 源码（source/target 8，bootclasspath=android.jar）'
$classesDir = Join-Path $buildDir 'classes'
$sources = @()
$srcRoot = Join-Path $root 'src'
$sources += Get-ChildItem -Path $srcRoot -Recurse -Filter '*.java' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
$sources += Get-ChildItem -Path $genDir -Recurse -Filter '*.java' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
if (-not $sources -or $sources.Count -eq 0) { Fail '没有找到 Java 源码' }
Write-Host "    源码数量：$($sources.Count)"

$javacArgs = @(
    '-encoding', 'UTF-8',
    '-source', '8',
    '-target', '8',
    '-bootclasspath', $androidJar,
    '-d', $classesDir
) + $sources
Invoke-Tool $javac $javacArgs 'javac'

# ---------------------------------------------------------------- 4 d8
Write-Step 'd8：生成 classes.dex'
$classesJar = Join-Path $buildDir 'classes.jar'
Push-Location $classesDir
try {
    Invoke-Tool $jar @('cf', $classesJar, '.') 'jar 打包 class'
} finally { Pop-Location }

$dexDir = Join-Path $buildDir 'dex'
Invoke-Tool $d8 @('--release', '--min-api', '24', '--lib', $androidJar, '--output', $dexDir, $classesJar) 'd8'

$dexFile = Join-Path $dexDir 'classes.dex'
if (-not (Test-Path $dexFile)) { Fail 'd8 未生成 classes.dex' }
Write-Host ("    classes.dex : {0:N0} 字节" -f (Get-Item $dexFile).Length)

# ---------------------------------------------------------------- 5 装入 APK
Write-Step '打入 classes.dex'
Push-Location $dexDir
try {
    Invoke-Tool $jar @('uf', $unsignedApk, 'classes.dex') 'jar 写入 classes.dex'
} finally { Pop-Location }

# ---------------------------------------------------------------- 6 zipalign
Write-Step 'zipalign：4 字节对齐'
$alignedApk = Join-Path $buildDir 'aligned.apk'
Invoke-Tool $zipalign @('-f', '-p', '4', $unsignedApk, $alignedApk) 'zipalign'

# ---------------------------------------------------------------- 7 签名
Write-Step '准备 debug keystore 并签名'
if (-not (Test-Path $ksFile)) {
    $dname = 'CN=Android Debug,O=Android,C=US'
    Invoke-Tool $keytool @(
        '-genkeypair',
        '-keystore', $ksFile,
        '-storepass', 'android',
        '-keypass', 'android',
        '-alias', 'androiddebugkey',
        '-keyalg', 'RSA',
        '-keysize', '2048',
        '-validity', '10000',
        '-dname', $dname,
        '-storetype', 'JKS',
        '-noprompt'
    ) 'keytool 生成 debug.jks'
} else {
    Write-Host "    复用已有 keystore：$ksFile"
}

if (Test-Path $apkOut) { Remove-Item $apkOut -Force }
Invoke-Tool $apksigner @(
    'sign',
    '--ks', $ksFile,
    '--ks-pass', 'pass:android',
    '--key-pass', 'pass:android',
    '--ks-key-alias', 'androiddebugkey',
    '--out', $apkOut,
    $alignedApk
) 'apksigner 签名'

# ---------------------------------------------------------------- 8 校验
Write-Step '校验签名与包信息'
& $apksigner verify --verbose $apkOut
if ($LASTEXITCODE -ne 0) { Fail 'apksigner verify 未通过' }

$aaptOut = & $aapt dump badging $apkOut 2>&1
if ($LASTEXITCODE -ne 0) { Fail 'aapt dump badging 失败' }
$badging = @($aaptOut | Where-Object { $_ -match '^(package:|launchable-activity:|application-label:|sdkVersion:|targetSdkVersion:)' })
foreach ($line in $badging) { Write-Host "    $line" }

$hasLauncher = @($aaptOut | Where-Object { $_ -match '^launchable-activity:' }).Count -gt 0
if (-not $hasLauncher) { Fail 'APK 中没有 launchable-activity，无法从桌面启动' }

# 反 WebView 兜底检查
$hits = @(Get-ChildItem $srcRoot -Recurse -Include '*.java' -ErrorAction SilentlyContinue |
    Select-String -Pattern 'WebView' -SimpleMatch -ErrorAction SilentlyContinue)
if ($hits.Count -gt 0) { Fail "源码中出现 WebView 引用：$($hits[0].Path)" }
$dexText = & (Join-Path $sdk 'build-tools\34.0.0\dexdump.exe') -d $dexFile 2>&1 | Select-String -Pattern 'WebView' -SimpleMatch
if ($dexText) { Fail 'classes.dex 中出现 WebView 引用' }

# ---------------------------------------------------------------- 汇总
$apkSize = (Get-Item $apkOut).Length
$sha = (Get-FileHash $apkOut -Algorithm SHA256).Hash
Write-Host ""
Write-Host "================ 构建成功 ================" -ForegroundColor Green
Write-Host ("  产物   : {0}" -f $apkOut)
Write-Host ("  大小   : {0:N0} 字节（{1:N2} MB）" -f $apkSize, ($apkSize / 1MB))
Write-Host ("  SHA256 : {0}" -f $sha)
Write-Host ("  包名   : com.ln334.zhifuwater")
Write-Host ("  版本   : 1.0.0 (versionCode 1)")
Write-Host ("  签名   : debug.jks / androiddebugkey")
Write-Host "==========================================="
exit 0

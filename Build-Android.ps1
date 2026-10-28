#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$JdkPath = '',
    [string]$BuildToolsPath = '',
    [string]$AndroidJarPath = ''
)
$ErrorActionPreference = 'Stop'
$cache = Join-Path $env:USERPROFILE '.cache\codex-audiolink-android'
if (!$JdkPath) { $JdkPath = Get-ChildItem -LiteralPath (Join-Path $cache 'jdk') -Directory | Select-Object -First 1 -ExpandProperty FullName }
if (!$BuildToolsPath) { $BuildToolsPath = Join-Path $cache 'sdk-part-1\android-15' }
if (!$AndroidJarPath) { $AndroidJarPath = Join-Path $cache 'sdk-part-0\android-35-ext15\android.jar' }
$java = Join-Path $JdkPath 'bin\java.exe'
$javac = Join-Path $JdkPath 'bin\javac.exe'
$jar = Join-Path $JdkPath 'bin\jar.exe'
$keytool = Join-Path $JdkPath 'bin\keytool.exe'
$aapt = Join-Path $BuildToolsPath 'aapt2.exe'
$zipalign = Join-Path $BuildToolsPath 'zipalign.exe'
foreach ($required in @($java,$javac,$jar,$keytool,$aapt,$zipalign,$AndroidJarPath)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing build dependency: $required" }
}
function Invoke-BuildTool([string]$Executable, [string[]]$ToolArguments) {
    & $Executable @ToolArguments
    if ($LASTEXITCODE -ne 0) { throw "Build tool failed: $Executable ($LASTEXITCODE)" }
}
$sourceRoot = Join-Path $PSScriptRoot 'android\app'
# Android native tools have inconsistent support for non-ASCII Windows paths.
$build = Join-Path $cache ('work\' + [Guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $build 'app'
$classes = Join-Path $build 'classes'
$generated = Join-Path $build 'generated'
$dex = Join-Path $build 'dex'
New-Item -ItemType Directory -Path $build,$classes,$generated,$dex,$appRoot -Force | Out-Null
Get-ChildItem -LiteralPath $sourceRoot | Copy-Item -Destination $appRoot -Recurse -Force
$resources = Join-Path $build 'resources.zip'
$unsigned = Join-Path $build 'unsigned.apk'
$aligned = Join-Path $build 'aligned.apk'
Invoke-BuildTool $aapt @('compile','--dir',(Join-Path $appRoot 'res'),'-o',$resources)
Invoke-BuildTool $aapt @('link','-I',$AndroidJarPath,'--manifest',(Join-Path $appRoot 'AndroidManifest.xml'),$resources,'--java',$generated,'--min-sdk-version','26','--target-sdk-version','35','--version-code','14000','--version-name','1.4.0','-o',$unsigned)
$javaSources = @(Get-ChildItem -LiteralPath (Join-Path $appRoot 'src'),$generated -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName)
Invoke-BuildTool $javac (@('--release','8','-encoding','UTF-8','-classpath',$AndroidJarPath,'-d',$classes) + $javaSources)
$classArchive = Join-Path $build 'classes.jar'
Invoke-BuildTool $jar @('--create','--file',$classArchive,'-C',$classes,'.')
Invoke-BuildTool $java @('-Xmx512m','-cp',(Join-Path $BuildToolsPath 'lib\d8.jar'),'com.android.tools.r8.D8','--lib',$AndroidJarPath,'--min-api','26','--output',$dex,$classArchive)
Add-Type -AssemblyName System.IO.Compression
$apkArchive = [System.IO.Compression.ZipFile]::Open($unsigned,[System.IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($dexFile in Get-ChildItem -LiteralPath $dex -Filter '*.dex') {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($apkArchive,$dexFile.FullName,$dexFile.Name,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $apkArchive.Dispose() }
Invoke-BuildTool $zipalign @('-f','4',$unsigned,$aligned)
$signing = Join-Path $cache 'signing'
New-Item -ItemType Directory -Path $signing -Force | Out-Null
$keyStore = Join-Path $signing 'audiolink.jks'
$password = Join-Path $signing 'password.txt'
if (!(Test-Path -LiteralPath $password)) { [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) | Set-Content -LiteralPath $password -NoNewline }
if (!(Test-Path -LiteralPath $keyStore)) {
    Invoke-BuildTool $keytool @('-genkeypair','-keystore',$keyStore,'-alias','audiolink','-keyalg','RSA','-keysize','3072','-validity','10000','-storepass:file',$password,'-keypass:file',$password,'-dname','CN=AudioLink Local Build, OU=AudioLink, O=AudioLink','-noprompt')
}
$signed = Join-Path $build 'AudioLink-Android.apk'
Invoke-BuildTool $java @('-Xmx256m','-jar',(Join-Path $BuildToolsPath 'lib\apksigner.jar'),'sign','--ks',$keyStore,'--ks-key-alias','audiolink','--ks-pass',('file:'+$password),'--out',$signed,$aligned)
$apk = Join-Path $PSScriptRoot 'artifacts\AudioLink-Android.apk'
Copy-Item -LiteralPath $signed -Destination $apk -Force
Write-Output "Ready: $apk"

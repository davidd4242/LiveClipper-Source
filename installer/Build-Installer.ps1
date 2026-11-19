param([string]$OutputDirectory,[string]$CacheDirectory,[switch]$NoRestore,[string]$VerifyInstallRoot,[string]$CertificateThumbprint,[ValidateNotNullOrEmpty()][string]$PublishedAppDirectory)
$ErrorActionPreference='Stop'
# 完整性标签检测：读取 SDDL 中的 mandatory label ACE（ML），按 SID 别名 LW / S-1-16-4096 判断，
# 与 Windows 显示语言无关（不再解析 icacls 的本地化文本）。
if (-not ('LcIntegrity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class LcIntegrity{
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetNamedSecurityInfoW(string name,int type,uint info,IntPtr o,IntPtr g,IntPtr d,IntPtr s,out IntPtr sd);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd,uint rev,uint info,out IntPtr str,out uint len);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
 public static string LabelSddl(string path){
  IntPtr sd;uint r=GetNamedSecurityInfoW(path,1,0x10,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,out sd);
  if(r!=0)throw new System.ComponentModel.Win32Exception((int)r);
  try{IntPtr s;uint l;if(!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd,1,0x10,out s,out l))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   try{return Marshal.PtrToStringUni(s);}finally{LocalFree(s);}}finally{LocalFree(sd);}
 }}
'@
}
function Assert-MediumIntegrity([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Integrity check: missing '$Path'" }
    try { $sddl = [LcIntegrity]::LabelSddl((Resolve-Path -LiteralPath $Path).ProviderPath) }
    catch { throw "Integrity check failed: cannot read integrity label for '$Path': $($_.Exception.Message)" }
    if ($sddl -match '\(ML;[^;]*;[^;]*;[^;]*;[^;]*;(LW|S-1-16-4096)\)') {
        throw "Integrity check failed: '$Path' carries Low Mandatory Label ($sddl). Build from a Medium IL folder, or run: icacls `"$Path`" /setintegritylevel Medium /T"
    }
}
# 按 SID 判断，不依赖本地化账户名：Everyone / Authenticated Users / BUILTIN\Users 不得对路径有写类权限。
function Get-BroadWriteAces([string]$Path) {
    $mask = [int64](0x2 -bor 0x4 -bor 0x10 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000)
    $broad = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    foreach ($ace in (Get-Acl -LiteralPath $Path).Access) {
        if ($ace.AccessControlType -ne 'Allow') { continue }
        try { $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if (($broad -contains $sid) -and (([int64][int]$ace.FileSystemRights -band $mask) -ne 0)) { "$($ace.IdentityReference) : $($ace.FileSystemRights) (inherited=$($ace.IsInherited))" }
    }
}
function Assert-NoBroadWrite([string]$Path, [switch]$WarnOnly) {
    $bad = @(Get-BroadWriteAces $Path)
    if ($bad.Count -eq 0) { return }
    $msg = "ACL check: '$Path' grants write to a broad group: " + ($bad -join '; ')
    if ($WarnOnly) { Write-Warning $msg } else { throw $msg }
}
# 可选 Authenticode 签名。-CertificateThumbprint 给了就必须签成：
# signtool 或证书缺失一律抛错退出，绝不静默产出未签名产物。
# 仓库里没有签名密钥，签名走本机普通用户证书库（Windows PowerShell 的 Cert: 提供程序）。
function Invoke-SignFile([string]$Path, [string]$Thumbprint) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "签名失败: 文件不存在 '$Path'" }
    $thumb = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($thumb -notmatch '^[0-9A-F]{40,64}$') { throw "签名失败: -CertificateThumbprint 不是合法指纹: '$Thumbprint'" }
    $candidates = @()
    $where = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($where) { $candidates += $where.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $candidates += @(Get-ChildItem -LiteralPath $kits -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | ForEach-Object { $_.FullName })
    }
    $signTool = $candidates | Select-Object -First 1
    if (-not $signTool) { throw '签名失败: 找不到 signtool.exe（不在 PATH，也不在 Windows SDK 的 Windows Kits\10\bin 下）。请先安装 Windows SDK 或把 signtool 加进 PATH；本次未生成未签名产物。' }
    $cert = Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
    if (-not $cert) { throw "签名失败: 当前用户证书库 Cert:\CurrentUser\My 里找不到指纹为 $thumb 的证书。请先导入代码签名证书；本次未生成未签名产物。" }
    if (-not $cert.HasPrivateKey) { throw "签名失败: 证书 $thumb 没有关联私钥，无法签名。" }
    # /tr 时间戳服务器 + /td sha256：没有时间戳时，签名只在证书有效期内可信，证书一过期连"当时签的"都证明不了；
    # 加了时间戳，签名在证书过期后仍然有效（校验方用时间戳那一刻的证书状态判断）。
    # /tr（RFC 3161）需要 /td 指定摘要算法，两者必须成对给。
    & $signTool 'sign' '/fd' 'sha256' '/sha1' $thumb '/s' 'My' '/tr' 'http://timestamp.digicert.com' '/td' 'sha256' '/v' $Path 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "签名失败: signtool 退出码 $LASTEXITCODE（$Path）" }
    $verifyOut = & $signTool 'verify' '/pa' '/v' $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "签名后校验失败: signtool verify 退出码 $LASTEXITCODE ($verifyOut)" }
    Write-Host "签名: 已用证书 $thumb 签名并通过 signtool verify（工具: $signTool）"
}
$projectRoot=Split-Path -Parent $PSScriptRoot
$cache=if($CacheDirectory){[IO.Path]::GetFullPath($CacheDirectory)}else{Join-Path $projectRoot '.cache\installer'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'dist'}
# A prepublished input is a self-contained Desktop publish, not an installation or a source tree.
if ($PublishedAppDirectory) {
    $publishedApp=(Get-Item -LiteralPath $PublishedAppDirectory).FullName
    $OutputDirectory=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $roots=@($publishedApp,$cache,$OutputDirectory) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\')+'\' }
    foreach ($root in $roots) {
        if (@($roots | Where-Object { $_.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) }).Count -gt 1) {
            throw 'PublishedAppDirectory, CacheDirectory, and OutputDirectory must not overlap.'
        }
    }
    foreach ($destination in @($cache,$OutputDirectory)) {
        if (Test-Path -LiteralPath $destination) { throw "Prepublished builds require new cache and output directories: $destination" }
    }
    $runtime=Get-Content -LiteralPath (Join-Path $publishedApp 'LiveClipper.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$runtime.runtimeOptions.includedFrameworks -or !(Test-Path -LiteralPath (Join-Path $publishedApp 'coreclr.dll') -PathType Leaf)) {
        throw "Expected a self-contained Desktop publish: $publishedApp"
    }
}
New-Item -ItemType Directory -Path $cache,$OutputDirectory -Force | Out-Null
$staging=Join-Path $cache ('payload-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
# 单进程宿主：只发布 Desktop；App 通过 ProjectReference 进入 Desktop，
# 不再生成 server/LiveClipper.App.exe。
if ($PublishedAppDirectory) {
    Write-Host "Packaging prepublished Desktop app: $publishedApp"
    foreach ($item in (Get-ChildItem -LiteralPath $publishedApp -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
    }
} else {
    dotnet publish (Join-Path $projectRoot 'src\LiveClipper.Desktop\LiveClipper.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $staging $(if($NoRestore){'--no-restore'})
    if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
}
Assert-MediumIntegrity $staging
Assert-MediumIntegrity (Join-Path $staging 'LiveClipper.Desktop.exe')
# 标记自包含负载：安装器据此跳过 .NET 运行时下载，装完即可启动。
Set-Content -LiteralPath (Join-Path $staging 'self-contained.flag') -Value 'self-contained' -Encoding ascii
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $PSScriptRoot '第三方组件说明.md'),(Join-Path $PSScriptRoot '安装与卸载.md') -Destination $staging -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $staging -Recurse -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadName='payload-'+[guid]::NewGuid().ToString('N')+'.zip'
$payloadZip=Join-Path $cache $payloadName
if(Test-Path -LiteralPath $payloadZip){Remove-Item -LiteralPath $payloadZip}
# 不用 ZipFile.CreateFromDirectory：Windows PowerShell 5.1（.NET Framework）会写入反斜杠条目名，
# 违反 ZIP 规范，也会被安装器 PayloadIntegrity 的路径校验拒绝。这里手工写入，条目名统一为正斜杠。
$stagingRoot=(Resolve-Path -LiteralPath $staging).ProviderPath.TrimEnd('\')
$zipStream=[System.IO.File]::Create($payloadZip)
try{
    $zipArchive=New-Object System.IO.Compression.ZipArchive($zipStream,[System.IO.Compression.ZipArchiveMode]::Create)
    try{
        foreach($item in (Get-ChildItem -LiteralPath $staging -Recurse -Force | Sort-Object FullName)){
            $rel=$item.FullName.Substring($stagingRoot.Length+1).Replace('\','/')
            if($item.PSIsContainer){ $null=$zipArchive.CreateEntry($rel+'/') }
            else{ $null=[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive,$item.FullName,$rel,[System.IO.Compression.CompressionLevel]::Optimal) }
        }
    } finally { $zipArchive.Dispose() }
} finally { $zipStream.Dispose() }
dotnet build (Join-Path $PSScriptRoot 'LiveClipper.Launcher.csproj') -c Release -o (Join-Path $cache 'launcher') $(if($NoRestore){'--no-restore'})
if($LASTEXITCODE -ne 0){throw 'Launcher build failed'}
dotnet build (Join-Path $PSScriptRoot 'LiveClipper.Installer.csproj') -c Release -o (Join-Path $cache 'setup') "-p:InstallerCacheDir=$cache" "-p:InstallerPayloadZip=$payloadName" $(if($NoRestore){'--no-restore'})
if($LASTEXITCODE -ne 0){throw 'Installer build failed'}
$exe=Join-Path $OutputDirectory 'LiveClipperSetup.exe'
$exeHash=$exe+'.sha256'
# 先删旧的 exe 与旧哈希，再复制：否则签名一失败，输出目录里会留下「新 exe + 旧 .sha256」这种半成品，
# 而旧哈希对不上新 exe，下游一旦不校验就当成"已经发布好了"。删掉后任何中途失败都不会产生可误用的产物。
foreach($stale in @($exe,$exeHash)){
    if(Test-Path -LiteralPath $stale){
        Remove-Item -LiteralPath $stale -Force
        Write-Host "已删除上次残留: $stale"
    }
}
Copy-Item -LiteralPath (Join-Path $cache 'setup\LiveClipperSetup.exe') -Destination $exe -Force
Assert-MediumIntegrity $exe
# 签名必须在算哈希之前：签名会改动文件字节，先签后算才能让 .sha256 对应最终产物。
# 签名失败时这里直接 throw，exe 与 .sha256 都不在输出目录（上面已预先删除），不会留下新 exe + 旧哈希。
if ($CertificateThumbprint) { Invoke-SignFile $exe $CertificateThumbprint } else { Write-Host '签名: 无（未提供证书）' }
$hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
Set-Content -LiteralPath $exeHash -Value ($hash+'  LiveClipperSetup.exe') -Encoding utf8
# 自校验：重新读 .sha256 与文件现算的哈希比对，不一致即失败。
$recorded=((Get-Content -LiteralPath ($exe+'.sha256') -TotalCount 1) -split '\s+')[0]
$actual=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
if($recorded -ne $actual -or $recorded -ne $hash){throw "SHA256 self-check failed for ${exe}: recorded=$recorded actual=$actual"}
Write-Host "LiveClipperSetup.exe SHA256=$hash  [哈希自校验通过；完整性只证明文件未被改动，不证明来源，来源认证需 Authenticode 签名]"
if($VerifyInstallRoot){
    Assert-NoBroadWrite $VerifyInstallRoot
    Assert-NoBroadWrite (Join-Path $VerifyInstallRoot 'app\LiveClipper.Desktop.exe')
    Write-Host "Install ACL check passed: $VerifyInstallRoot"
}
Write-Host "Ready: $exe"
& (Join-Path $PSScriptRoot 'Package-Transfer.ps1') -OutputDirectory $OutputDirectory
# 清理本次构建的临时 staging 与唯一命名的 payload（缓存里只保留 launcher/setup 编译输出）。
Remove-Item -LiteralPath $staging,$payloadZip -Recurse -Force -ErrorAction SilentlyContinue

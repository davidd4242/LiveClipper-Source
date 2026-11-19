param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$OutputDirectory){$OutputDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'}
$exe=Join-Path $OutputDirectory 'LiveClipperSetup.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build the installer first'}
if((Get-Item -LiteralPath $exe).Length -gt 500MB){throw 'Installer unexpectedly large'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundle=Join-Path $OutputDirectory 'LiveClipper-轻量安装包.zip'
if(Test-Path -LiteralPath $bundle){Remove-Item -LiteralPath $bundle}
$archive=[IO.Compression.ZipFile]::Open($bundle,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($item in @(@{Path=$exe;Name='LiveClipperSetup.exe'},@{Path=$exe+'.sha256';Name='LiveClipperSetup.exe.sha256'},@{Path=Join-Path $PSScriptRoot '安装与卸载.md';Name='安装说明.md'},@{Path=Join-Path $PSScriptRoot '第三方组件说明.md';Name='第三方组件说明.md'})) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$item.Path,$item.Name,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$bundleHash=(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
Set-Content -LiteralPath ($bundle+'.sha256') -Value ($bundleHash+'  '+[IO.Path]::GetFileName($bundle)) -Encoding utf8
# 自校验：重新读 .sha256 与文件现算的哈希比对，不一致即失败。
$recordedBundle=((Get-Content -LiteralPath ($bundle+'.sha256') -TotalCount 1) -split '\s+')[0]
$actualBundle=(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
if($recordedBundle -ne $actualBundle -or $recordedBundle -ne $bundleHash){throw "SHA256 self-check failed for ${bundle}: recorded=$recordedBundle actual=$actualBundle"}
$exeRecorded=((Get-Content -LiteralPath ($exe+'.sha256') -TotalCount 1) -split '\s+')[0]
if($exeRecorded -ne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash){throw "SHA256 self-check failed for ${exe}"}
Write-Host ('Transfer ZIP SHA256='+$bundleHash+'  [哈希自校验通过；只证明完整性，不证明来源]')
Write-Host ('Transfer ZIP: '+$bundle+' ('+[math]::Round((Get-Item -LiteralPath $bundle).Length/1MB,2)+' MB)')

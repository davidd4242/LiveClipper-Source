# 在 Windows 构建公开源码版

以下是根据发行输入、项目文件及锁文件整理的命令。此次整理仅核对文件、引用及哈希，**未执行构建或运行程序**。这些步骤不承诺生成与官方签名/正式安装包相同的二进制。

## 工具链

在 Windows x64 使用 PowerShell、官方 [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) **10.0.404**（global.json 不允许自动换版）和 [Node.js](https://nodejs.org/)（Vite 锁文件要求 **^20.19.0 或 >=22.12.0**）。准备可联网的 npm 和 NuGet 源。运行桌面外壳需要 WebView2；安装器工程另以 .NET Framework 4.8 为目标。外部语音、模型、视频处理服务需要各自的依赖；未打包，详见 ai-worker 的 requirements 文件和 installer/dependencies.json。

## 1. 恢复 HLS 浏览器库

旧前端和 Vite 的静态文件清单要求 hls.js 1.5.20 的官方 dist/hls.min.js，不在此仓内。以下在仓库根目录执行，使用新创建的临时目录，不向仓库安装第三方包：

~~~powershell
$repo = (Get-Location).Path
$tmp = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString()))
Push-Location $tmp.FullName
npm pack hls.js@1.5.20 --ignore-scripts
tar -xzf hls.js-1.5.20.tgz
$hls = Join-Path $tmp.FullName 'package/dist/hls.min.js'
$hash = (Get-FileHash $hls -Algorithm SHA256).Hash
if ($hash -ne 'D016C1230496EE59F3F5B01C16CCE4CC01B5A1D3D357ADEC200C908B131EBE49') { throw 'Unexpected hls.js artifact' }
Copy-Item $hls (Join-Path $repo 'src/LiveClipper.App/wwwroot/lib/hls.min.js')
Pop-Location
~~~

若另行分发恢复的库，请保留该**准确上游包**的完整 LICENSE 和第三方声明。仓内原有 hls.min.js.LICENSE.txt 只是版本/来源提示，不是完整许可证。

## 2. 可选：恢复原版编辑器运行文件

缺少这一步时，可以检查其他源码或构建其他部分；不要把高级编辑器功能当作完整可用。若有权使用官方 v1.3.1 的已安装程序目录（下含 wwwroot），在仓库根目录执行下例。只复制 CSV 明列、校验值相同的编解码器及编辑器运行资源；不会复制整个应用、帐号数据、旧图标或可执行程序。

~~~powershell
$installed = Read-Host 'Official v1.3.1 installed application directory'
foreach ($row in (Import-Csv './docs/editor-runtime-files.csv')) {
    $from = Join-Path $installed $row.release_path
    if ((Get-FileHash -LiteralPath $from -Algorithm SHA256 -ErrorAction Stop).Hash -ne $row.sha256) { throw "Different editor input: $($row.release_path)" }
    New-Item -ItemType Directory -Path (Split-Path $row.destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $row.destination
}
~~~

此步骤只还原**已编译/衍生的配套运行资源**。不得由此推断上游源码已完整包含、原作者授予项目品牌权，或本项目的第一方许可证覆盖这些恢复的资源。

## 3. 构建桌面程序

~~~powershell
Push-Location web
npm ci
npm run build
Pop-Location
dotnet restore ./src/LiveClipper.Desktop/LiveClipper.Desktop.csproj --locked-mode
dotnet build ./src/LiveClipper.Desktop/LiveClipper.Desktop.csproj -c Release --no-restore
~~~

Vite 在 web/dist 生成工作台；桌面工程会从那里取静态文件。公开树直接以 .csproj 为入口，不需要旧版的 .sln 或私有 SDK 路径。需要 Windows x64 的独立输出时，使用单独的空输出目录：

~~~powershell
dotnet publish ./src/LiveClipper.Desktop/LiveClipper.Desktop.csproj -c Release -r win-x64 --self-contained true -o ./out/app
~~~

安装器入口是 installer/Build-Installer.ps1，支持从另行核实的 PublishedAppDirectory 打包。必须核对依赖和第三方许可、确保发布物无开发数据且三个目录互不重叠，再决定是否生成或分发安装包。此候选没有签名证书或预生成安装器，也不验证正式安装流程。开发测试项目入口是 tests/LiveClipper.Tests/LiveClipper.Tests.csproj。本次未运行测试。

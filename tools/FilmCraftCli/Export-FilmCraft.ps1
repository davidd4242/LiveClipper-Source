param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [double]$Start = 0,
    [double]$End = 3,
    [ValidateSet('h264', 'hevc', 'prores', 'dnxhr')][string]$Format = 'h264',
    [int]$TimeoutSec = 1800,
    [string]$CliPath = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Code, [string]$Message) {
    [pscustomobject]@{ ok = $false; code = $Code; message = $Message } | ConvertTo-Json -Compress
    exit 2
}

if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Fail 'project_missing' "Project file not found: $ProjectPath"
}
if ([IO.Path]::GetExtension($ProjectPath) -ne '.fcproj') {
    Fail 'project_extension' "Project must be a .fcproj file: $ProjectPath"
}
if ($End -le $Start) {
    Fail 'range_invalid' "End must be greater than start (start=$Start end=$End)"
}
if ([IO.Path]::GetExtension($OutputPath) -notin @('.mp4', '.mov', '.mxf')) {
    Fail 'output_extension' "Output must end with .mp4/.mov/.mxf: $OutputPath"
}
$outDir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    Fail 'output_dir_missing' "Output directory does not exist: $outDir"
}

if (-not $CliPath) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'FilmCraft\filmcraft-cli.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'FilmCraft\filmcraft-cli.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $CliPath = $candidates | Select-Object -First 1
}
if (-not $CliPath -or -not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    Fail 'cli_missing' 'filmcraft-cli.exe not found; pass -CliPath'
}

$cliArgs = @(
    '--project', [IO.Path]::GetFullPath($ProjectPath),
    'export', [IO.Path]::GetFullPath($OutputPath),
    '--format', $Format,
    '--range', 'custom',
    '--start', $Start.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--end', $End.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--allow-offline'
)

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $CliPath
$psi.Arguments = (($cliArgs | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proc = [Diagnostics.Process]::Start($psi)
$stdoutTask = $proc.StandardOutput.ReadToEndAsync()
$stderrTask = $proc.StandardError.ReadToEndAsync()
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    try { $proc.Kill($true) } catch {}
    Fail 'timeout' "filmcraft-cli exceeded ${TimeoutSec}s"
}
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()

if ($proc.ExitCode -ne 0) {
    [pscustomobject]@{
        ok = $false; code = 'cli_failed'; exitCode = $proc.ExitCode
        stdout = $stdout; stderr = $stderr
    } | ConvertTo-Json -Compress
    exit 3
}
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    Fail 'output_missing' "CLI reported success but output was not created: $OutputPath"
}
$out = Get-Item -LiteralPath $OutputPath
if ($out.Length -le 0) {
    Fail 'output_empty' "Output is empty: $OutputPath"
}

[pscustomobject]@{
    ok = $true
    project = [IO.Path]::GetFullPath($ProjectPath)
    output = $out.FullName
    bytes = $out.Length
    format = $Format
    start = $Start
    end = $End
    stdout = $stdout.Trim()
    stderr = $stderr.Trim()
} | ConvertTo-Json -Compress
exit 0

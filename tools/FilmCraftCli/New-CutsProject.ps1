<#
.SYNOPSIS
  Build a cuts-only FilmCraft .fcproj from one or more media files using filmcraft-cli.
.DESCRIPTION
  Uses the CLI's own project builder (newProject -> import -> newSequence(fromItem)
  -> timeline.place) instead of hand-writing .fcproj media links. Every media file
  becomes one full-length clip, back to back on V1. The project is saved to
  -OutputProject. No transitions, effects, masks or captions are written.
.PARAMETER OutputProject
  Destination .fcproj path. Parent directory must exist.
.PARAMETER Media
  One or more local media files, in timeline order.
.PARAMETER CliPath
  Optional filmcraft-cli.exe path. Defaults to the installed FilmCraft CLI.
.PARAMETER ExportPath
  Optional .mp4 path; when set, exports the built sequence after saving.
.EXAMPLE
  .\New-CutsProject.ps1 -OutputProject C:\work\cut.fcproj -Media .\a.mp4,.\b.mp4
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$OutputProject,
  [Parameter(Mandatory = $false)][string[]]$Media = @(),
  [string]$MediaListFile,
  [string]$CliPath,
  [string]$ExportPath
)

$ErrorActionPreference = 'Stop'
$ticksPerSecond = 254016000000

if ($MediaListFile) {
  if (-not (Test-Path -LiteralPath $MediaListFile -PathType Leaf)) {
    throw "Media list file not found: $MediaListFile"
  }
  $listed = Get-Content -LiteralPath $MediaListFile -Raw -Encoding UTF8 | ConvertFrom-Json
  $Media = @($listed)
}

# -Media still accepts a,b for manual use; -MediaListFile preserves commas in paths.
$splitCommas = -not [bool]$MediaListFile
$mediaArgs = @()
foreach ($entry in $Media) {
  $parts = if ($splitCommas) { $entry -split ',' } else { @($entry) }
  foreach ($part in $parts) {
    $trimmed = $part.Trim().Trim('"')
    if ($trimmed) { $mediaArgs += $trimmed }
  }
}
$Media = $mediaArgs

function Resolve-Cli {
  param([string]$Path)
  if ($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "CLI not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
  }
  $candidates = @(
    (Join-Path ${env:ProgramFiles} 'FilmCraft\filmcraft-cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'FilmCraft\filmcraft-cli.exe')
  )
  foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return $c } }
  throw 'filmcraft-cli.exe not found; pass -CliPath'
}

function Invoke-Cli {
  param([string]$Cli, [string[]]$Arguments)
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $out = & $Cli @Arguments 2>&1
    $code = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $old
  }
  $text = ($out | Out-String).Trim()
  if ($code -ne 0) { throw "filmcraft-cli failed ($code): $text" }
  return $text
}

function Convert-JsonText {
  param([string]$Text)
  if (-not $Text) { return $null }
  try { return ($Text | ConvertFrom-Json) } catch { return $null }
}

$cli = Resolve-Cli $CliPath
if ([IO.Path]::GetExtension($OutputProject) -ne '.fcproj') { throw 'OutputProject must end with .fcproj' }
$outDir = Split-Path -Parent $OutputProject
if (-not $outDir -or -not (Test-Path -LiteralPath $outDir -PathType Container)) { throw "Output directory missing: $outDir" }

$finalProject = [IO.Path]::GetFullPath($OutputProject)
$tempProject = Join-Path $outDir ('.{0}.tmp-{1}.fcproj' -f [IO.Path]::GetFileNameWithoutExtension($OutputProject), [guid]::NewGuid().ToString('N'))
trap {
  [Console]::Error.WriteLine("cuts builder failed: " + $_.Exception.Message)
  if ($tempProject -and (Test-Path -LiteralPath $tempProject)) {
    Remove-Item -LiteralPath $tempProject -Force -ErrorAction SilentlyContinue
  }
  throw
}

$mediaPaths = @()
foreach ($m in $Media) {
  if (-not (Test-Path -LiteralPath $m -PathType Leaf)) { throw "Media not found: $m" }
  $mediaPaths += (Resolve-Path -LiteralPath $m).Path
}
if ($mediaPaths.Count -eq 0) { throw 'At least one media file is required' }

# Probe the first clip for the sequence format.
$probe = Convert-JsonText (Invoke-Cli $cli @('probe', $mediaPaths[0]))
if (-not $probe -or -not $probe.video) { throw 'First media has no video stream' }
$width = [int]$probe.video.width
$height = [int]$probe.video.height
$fps = if ($probe.video.frame_rate.den -gt 0) { [double]$probe.video.frame_rate.num / [double]$probe.video.frame_rate.den } else { 25.0 }
$fps = [Math]::Round($fps, 3)

# Start from a clean project.
if (Test-Path -LiteralPath $tempProject) { Remove-Item -LiteralPath $tempProject -Force }
$null = Invoke-Cli $cli @('--demo', '--save-as', $tempProject, 'exec', 'file.newProject', 'name=cuts')

$imported = @()
foreach ($m in $mediaPaths) {
  $r = Convert-JsonText (Invoke-Cli $cli @('--project', $tempProject, 'import', $m, '--save'))
  if (-not $r -or -not $r.items -or $r.items.Count -lt 1) { throw "import failed: $m" }
  $imported += [int]$r.items[0]
}

$seqArgs = @('--project', $tempProject, 'exec', 'file.newSequence',
  "name=Cuts", "width=$width", "height=$height", "fps=$fps", "fromItem=$($imported[0])", '--save')
$seq = Convert-JsonText (Invoke-Cli $cli $seqArgs)
if (-not $seq -or -not $seq.sequence) { throw 'newSequence failed' }
$sequenceId = [int]$seq.sequence

# Place remaining clips back to back, using tick durations from probe.
$cursor = [double]$probe.duration
$placed = @()
for ($i = 1; $i -lt $imported.Count; $i++) {
  $p = Convert-JsonText (Invoke-Cli $cli @('probe', $mediaPaths[$i]))
  if (-not $p -or -not $p.duration) { throw "probe failed: $($mediaPaths[$i])" }
  $placeArgs = @('--project', $tempProject, 'exec', 'timeline.place',
    "item=$($imported[$i])", 'track=V1', "seconds=$([Math]::Round($cursor / $ticksPerSecond, 6))",
    'sourceIn=0', "duration=$($p.duration)", '--save')
  $r = Convert-JsonText (Invoke-Cli $cli $placeArgs)
  if (-not $r -or -not $r.clips) { throw "timeline.place failed: $($mediaPaths[$i])" }
  $placed += [int]$r.clips[0]
  $cursor += [double]$p.duration
}

# The demo project can leave extra items behind; assert the saved project has
# exactly one media item per input plus the generated sequence, before exporting.
$saved = Get-Content -LiteralPath $tempProject -Raw | ConvertFrom-Json
$childCount = @($saved.project.root.children).Count
$expectedChildren = $mediaPaths.Count + 1
if ($childCount -ne $expectedChildren) {
  throw "unexpected project content: $childCount root items, expected $expectedChildren"
}

$exported = $null
if ($ExportPath) {
  if ([IO.Path]::GetExtension($ExportPath) -ne '.mp4') { throw 'ExportPath must end with .mp4' }
  $null = Invoke-Cli $cli @('--project', $tempProject, 'export', $ExportPath, '--format', 'h264', '--range', 'entire', '--allow-offline')
  if (-not (Test-Path -LiteralPath $ExportPath)) { throw 'export did not produce a file' }
  $exported = (Get-Item -LiteralPath $ExportPath).FullName
}

Move-Item -LiteralPath $tempProject -Destination $finalProject -Force
[pscustomobject]@{
  ok = $true
  project = $finalProject
  sequence = $sequenceId
  imported = $imported
  placed = $placed
  width = $width
  height = $height
  fps = $fps
  durationSeconds = [Math]::Round($cursor / $ticksPerSecond, 3)
  exported = $exported
} | ConvertTo-Json -Compress

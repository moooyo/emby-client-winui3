#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'FixtureTool.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$dependencies = Get-Content -LiteralPath (Join-Path $artifactRoot 'dependencies.json') -Raw | ConvertFrom-Json
$sourceDirectory = Join-Path $repositoryRoot 'tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds'
$sourceVideo = Join-Path $sourceDirectory 'fixture-h264-aac.mp4'
$sourceMetadata = Get-Content -LiteralPath (Join-Path $sourceDirectory 'fixture-h264-aac.json') -Raw | ConvertFrom-Json
if ($sourceMetadata.Synthetic -ne $true -or $sourceMetadata.DurationTicks -lt 590000000 -or $sourceMetadata.DurationTicks -gt 610000000 -or
    (Get-Item -LiteralPath $sourceVideo).Length -ne $sourceMetadata.FileLength) {
    throw 'The matching generated sixty-second H.264/AAC source is required.'
}
function Get-Sha256([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($dependencies.EmbyMediaTools.ServerVersion -ne '4.9.5.0' -or
    $dependencies.EmbyMediaTools.ArchiveSha256 -ne '6883356517a42316ef5b270081f81d8de4151fec384fbe1ec1b31d2cf77688f6' -or
    $dependencies.EmbyMediaTools.FfmpegSha256 -ne '969535ce1e41c35b93f62104b9099aa4b2fc803ebcfd7f2eb15c80d7e36340cf' -or
    $dependencies.EmbyMediaTools.FfprobeSha256 -ne '3d649e06a1979491d64ab91bc60d19a19ecb423792439700a409d2b09a0d87c2') {
    throw 'The dependency manifest does not identify the fixed official Emby media tools recorded in SOURCES.md.'
}
foreach ($entry in @(
    @{ Path = $dependencies.SeConv.ExecutablePath; Hash = $dependencies.SeConv.ExecutableSha256 },
    @{ Path = $dependencies.Font.Path; Hash = $dependencies.Font.Sha256 },
    @{ Path = $dependencies.EmbyMediaTools.FfmpegPath; Hash = $dependencies.EmbyMediaTools.FfmpegSha256 },
    @{ Path = $dependencies.EmbyMediaTools.FfprobePath; Hash = $dependencies.EmbyMediaTools.FfprobeSha256 }
)) {
    if ((Get-Sha256 $entry.Path) -ne $entry.Hash) { throw 'A prepared dependency changed after verification.' }
}
foreach ($entry in $dependencies.SeConv.PackageFiles) {
    if ((Get-Sha256 $entry.Path) -ne $entry.Sha256) { throw 'A prepared SeConv package file changed after verification.' }
}
$sourceHash = Get-Sha256 $sourceVideo
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDirectory = Join-Path $artifactRoot "fixtures-$runId"
$mediaRoot = Join-Path $outputDirectory 'media'
$assName = 'Styled ASS Validation (2026)'
$pgsName = 'PGS Bitmap Validation (2026)'
$assDirectory = Join-Path $mediaRoot $assName
$pgsDirectory = Join-Path $mediaRoot $pgsName
$sourceTextDirectory = Join-Path $outputDirectory 'source'
New-Item -ItemType Directory -Path $assDirectory, $pgsDirectory, $sourceTextDirectory | Out-Null
$outputDirectory | Set-Content -LiteralPath (Join-Path $artifactRoot 'last-attempt-directory.txt') -Encoding utf8
$ffmpeg = $dependencies.EmbyMediaTools.FfmpegPath
$ffprobe = $dependencies.EmbyMediaTools.FfprobePath
$seconv = $dependencies.SeConv.ExecutablePath

function Run-Ffmpeg([string] $Name, [string[]] $ToolArguments, [string] $Directory = $outputDirectory) {
    return Invoke-FixtureTool -FilePath $ffmpeg -Arguments $ToolArguments -WorkingDirectory $Directory -StateDirectory $outputDirectory -LogName $Name
}
function Read-Media([string] $Name, [string] $Path) {
    return (Invoke-FixtureTool -FilePath $ffprobe -Arguments @('-v', 'error', '-show_streams', '-show_format', '-of', 'json', $Path) -WorkingDirectory $outputDirectory -StateDirectory $outputDirectory -LogName $Name | ConvertFrom-Json)
}

$assFile = Join-Path $sourceTextDirectory 'styled.ass'
@'
[Script Info]
Title: Original styled ASS and attached-font validation
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 720
ScaledBorderAndShadow: yes
WrapStyle: 2

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Fixture,Bungee Shade,72,&H0000FF00,&H0000FF00,&H00FF00FF,&HFF000000,0,0,0,0,100,100,0,0,1,3,0,7,80,80,80,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:02.00,0:00:08.00,Fixture,,0,0,0,,{\an7\pos(80,80)}STYLE CHECK
Dialogue: 0,0:00:35.00,0:00:45.00,Fixture,,0,0,0,,{\an7\pos(80,80)}ATTACHED FONT CHECK
'@ | Set-Content -LiteralPath $assFile -Encoding utf8
$pgsText = Join-Path $sourceTextDirectory 'pgs.srt'
@'
1
00:00:02,000 --> 00:00:08,000
PGS START CHECK

2
00:00:35,000 --> 00:00:45,000
PGS BITMAP CHECK
'@ | Set-Content -LiteralPath $pgsText -Encoding utf8

$null = Invoke-FixtureTool -FilePath $seconv -Arguments @($pgsText, 'bluraysup', '--resolution:1280x720', '--font-name:Arial', '--font-size:42', '--font-color:#FFFFFFFF', '--outline-color:#FF000000', '--outline-width:3', '--alignment:bottom-center', "--output-folder:$sourceTextDirectory") -WorkingDirectory $sourceTextDirectory -StateDirectory $outputDirectory -LogName 'seconv-pgs'
$supFile = Join-Path $sourceTextDirectory 'pgs.sup'
if (-not (Test-Path -LiteralPath $supFile)) { throw 'The genuine Blu-ray SUP output is missing.' }
$supMetadata = Read-Media 'probe-sup' $supFile
$supSubtitle = @($supMetadata.streams | Where-Object { $_.codec_type -eq 'subtitle' })
if ($supSubtitle.Count -ne 1 -or $supSubtitle[0].codec_name -ne 'hdmv_pgs_subtitle') {
    throw 'The generated SUP must be genuine HDMV PGS, not DVD or DVB subtitles.'
}

$assMkv = Join-Path $assDirectory "$assName.mkv"
$null = Run-Ffmpeg 'mux-ass' @('-hide_banner', '-nostdin', '-n', '-i', $sourceVideo, '-i', $assFile, '-map', '0:v:0', '-map', '0:a:0', '-map', '1:0', '-c:v', 'copy', '-c:a', 'copy', '-c:s', 'ass', '-metadata:s:s:0', 'language=eng', '-metadata:s:s:0', 'title=ASS style and attached font', '-attach', $dependencies.Font.Path, '-metadata:s:t:0', 'mimetype=application/x-truetype-font', '-metadata:s:t:0', 'filename=BungeeShade-Regular.ttf', $assMkv)
$pgsMkv = Join-Path $pgsDirectory "$pgsName.mkv"
$null = Run-Ffmpeg 'mux-pgs' @('-hide_banner', '-nostdin', '-n', '-copyts', '-i', $sourceVideo, '-i', $supFile, '-map', '0:v:0', '-map', '0:a:0', '-map', '1:s:0', '-c', 'copy', '-metadata:s:s:0', 'language=eng', '-metadata:s:s:0', 'title=Genuine PGS bitmap captions', $pgsMkv)
$assMetadata = Read-Media 'probe-ass-mkv' $assMkv
$pgsMetadata = Read-Media 'probe-pgs-mkv' $pgsMkv
$assSubtitle = @($assMetadata.streams | Where-Object { $_.codec_type -eq 'subtitle' })
$fontAttachment = @($assMetadata.streams | Where-Object { $_.codec_type -eq 'attachment' })
$pgsSubtitle = @($pgsMetadata.streams | Where-Object { $_.codec_type -eq 'subtitle' })
if ($assSubtitle.Count -ne 1 -or $assSubtitle[0].codec_name -ne 'ass' -or $fontAttachment.Count -ne 1 -or
    $fontAttachment[0].tags.filename -ne 'BungeeShade-Regular.ttf' -or
    $pgsSubtitle.Count -ne 1 -or $pgsSubtitle[0].codec_name -ne 'hdmv_pgs_subtitle') {
    throw 'The actual MKV subtitle/attachment stream types do not match the cases.'
}
$extractedFont = Join-Path $outputDirectory 'extracted-attachment.ttf'
$null = Run-Ffmpeg 'extract-font' @('-hide_banner', '-nostdin', '-n', '-dump_attachment:t:0', $extractedFont, '-i', $assMkv, '-map', '0:v:0', '-frames:v', '1', '-an', '-f', 'null', '-')
if ((Get-Sha256 $extractedFont) -ne $dependencies.Font.Sha256) { throw 'The font attachment differs from the pinned OFL font.' }
$pgsPacketMetadata = Invoke-FixtureTool -FilePath $ffprobe -Arguments @('-v', 'error', '-select_streams', 's', '-show_packets', '-of', 'json', $pgsMkv) -WorkingDirectory $outputDirectory -StateDirectory $outputDirectory -LogName 'probe-pgs-packets' | ConvertFrom-Json
$pgsPacketTimes = @($pgsPacketMetadata.packets | ForEach-Object { [double]::Parse($_.pts_time, [Globalization.CultureInfo]::InvariantCulture) })
if (($pgsPacketTimes -join ',') -ne '2,8,35,45') { throw 'The PGS mux did not preserve both exact cue intervals.' }

$assReference = Join-Path $outputDirectory 'ass-reference.png'
$assFilter = "subtitles=filename='$assName.mkv':si=0"
$null = Run-Ffmpeg 'reference-ass' @('-hide_banner', '-loglevel', 'verbose', '-nostdin', '-n', '-i', $assMkv, '-map', '0:v:0', '-vf', $assFilter, '-ss', '41', '-frames:v', '1', '-an', '-threads', '2', $assReference) $assDirectory
$pgsReference = Join-Path $outputDirectory 'pgs-reference.png'
$null = Run-Ffmpeg 'reference-pgs' @('-hide_banner', '-loglevel', 'verbose', '-nostdin', '-n', '-fix_sub_duration', '-i', $pgsMkv, '-filter_complex', '[0:v:0][0:s:0]overlaygraphicsubs[v]', '-map', '[v]', '-ss', '41', '-frames:v', '1', '-an', '-threads', '2', $pgsReference) $pgsDirectory
$assRenderLog = Get-Content -LiteralPath (Join-Path $outputDirectory 'logs/reference-ass.stderr.txt') -Raw
if ($assRenderLog -notmatch 'BungeeShade-Regular' -or $assRenderLog -notmatch 'fontselect') {
    throw 'The ASS reference did not record selection of the attached font.'
}
foreach ($reference in @($assReference, $pgsReference)) {
    $signature = [IO.File]::ReadAllBytes($reference)[0..7]
    if (($signature -join ',') -ne '137,80,78,71,13,10,26,10') { throw 'A reference is not a real PNG.' }
}
Add-Type -AssemblyName System.Drawing
function Get-ReferencePixelCounts([string] $Path, [int] $Top, [int] $Bottom) {
    $bitmap = [Drawing.Bitmap]::new($Path)
    try {
        if ($bitmap.Width -ne 1280 -or $bitmap.Height -ne 720) { throw 'The reference dimensions changed.' }
        $counts = [ordered]@{ White = 0; Black = 0; Green = 0; Magenta = 0 }
        for ($y = $Top; $y -lt $Bottom; $y++) {
            for ($x = 40; $x -lt 1240; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -gt 225 -and $pixel.G -gt 225 -and $pixel.B -gt 225) { $counts.White++ }
                if ($pixel.R -lt 40 -and $pixel.G -lt 40 -and $pixel.B -lt 40) { $counts.Black++ }
                if ($pixel.G -gt 150 -and $pixel.R -lt 100 -and $pixel.B -lt 100) { $counts.Green++ }
                if ($pixel.R -gt 150 -and $pixel.B -gt 150 -and $pixel.G -lt 100) { $counts.Magenta++ }
            }
        }
        return $counts
    }
    finally { $bitmap.Dispose() }
}
$assPixels = Get-ReferencePixelCounts $assReference 40 240
$pgsPixels = Get-ReferencePixelCounts $pgsReference 550 715
if ($assPixels.Green -lt 100 -or $assPixels.Magenta -lt 100 -or $pgsPixels.White -lt 100 -or $pgsPixels.Black -lt 100) {
    throw 'A reference frame lacks the expected colored subtitle pixels.'
}
[ordered]@{ Purpose = 'Reference-image sanity only; not native playback evidence'; Ass = $assPixels; Pgs = $pgsPixels } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'reference-sanity.json') -Encoding utf8
if ((Get-Sha256 $sourceVideo) -ne $sourceHash) { throw 'The immutable original fixture changed.' }

function New-Case([string] $Id, [string] $Kind, [string] $Name, [string] $Path, [object] $Metadata, [object] $Subtitle, [string] $Codec, [string] $Cue, [string] $Reference, [string[]] $Features) {
    return [ordered]@{
        CaseId = $Id; Kind = $Kind
        ItemId = $null; MediaSourceId = $null; SubtitleStreamIndex = $null
        ObservedFileSubtitleStreamIndex = [int]$Subtitle.index
        ExpectedSubtitleCodec = $Codec; ExpectedItemName = $Name
        RunTimeTicks = [long][Math]::Round([double]::Parse($Metadata.format.duration, [Globalization.CultureInfo]::InvariantCulture) * 10000000)
        MediaFileName = [IO.Path]::GetRelativePath($outputDirectory, $Path)
        MediaSha256 = Get-Sha256 $Path
        PauseTargetTicks = 410000000; CueStartTicks = 350000000; CueEndTicks = 450000000
        ExpectedCue = $Cue; ExpectedVisualFeatureIds = $Features
        VisualReferenceFileName = [IO.Path]::GetFileName($Reference); VisualReferenceSha256 = Get-Sha256 $Reference
        EmbeddedFontFamily = $null; EmbeddedFontSha256 = $null
    }
}
$assCase = New-Case 'ass-styled' 'AssStyleAndEmbeddedFont' 'Styled ASS Validation' $assMkv $assMetadata $assSubtitle[0] 'ass' 'ATTACHED FONT CHECK' $assReference @('ass-green-fill', 'ass-magenta-outline', 'ass-upper-left-position', 'bungee-shade-glyphs')
$assCase.EmbeddedFontFamily = 'Bungee Shade'
$assCase.EmbeddedFontSha256 = $dependencies.Font.Sha256
$pgsCase = New-Case 'pgs-bitmap' 'PgsBitmapOverlay' 'PGS Bitmap Validation' $pgsMkv $pgsMetadata $pgsSubtitle[0] 'pgssub' 'PGS BITMAP CHECK' $pgsReference @('pgs-white-fill', 'pgs-black-outline', 'pgs-bottom-center-position')
$manifest = [ordered]@{
    FormatVersion = 1; Synthetic = $true; BindingStatus = 'UnboundGeneratedFilesOnly'
    ServerId = $null; ServerVersion = $null; RequiredServerVersion = '4.9.5.0'
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    OriginalVideoSha256 = $sourceHash
    SourceMetadataSha256 = Get-Sha256 (Join-Path $sourceDirectory 'fixture-h264-aac.json')
    DependencyManifestSha256 = Get-Sha256 (Join-Path $artifactRoot 'dependencies.json')
    MediaRootRelativePath = 'media'
    Cases = @($assCase, $pgsCase)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputDirectory 'manifest.json') -Encoding utf8
$licenseDirectory = Join-Path $outputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory | Out-Null
Copy-Item -LiteralPath $dependencies.Font.LicensePath -Destination (Join-Path $licenseDirectory 'BungeeShade-OFL.txt')
$outputDirectory | Set-Content -LiteralPath (Join-Path $artifactRoot 'last-successful-directory.txt') -Encoding utf8
Write-Output "Generated two original complex-subtitle fixtures with stream, font, and timestamp checks: $outputDirectory"
Write-Output 'Server identifiers remain null until an isolated server actually indexes these files. Reference PNGs are not native playback screenshots.'

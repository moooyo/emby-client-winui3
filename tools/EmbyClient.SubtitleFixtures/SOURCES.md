# Sources and license record

All captions and the underlying color/sine-wave clip were created for this repository. No movie, disc image, commercial subtitle sample, or downloaded entertainment media is used. The generator sources are in this directory and the existing `EmbyClient.MediaFixtures` tool. This document does not assign a new repository-wide license.

## Subtitle Edit / SeConv

- Official fixed release: [Subtitle Edit v5.1.0](https://github.com/SubtitleEdit/subtitleedit/releases/tag/v5.1.0).
- Asset: [SeConv-Windows-x64.zip](https://github.com/SubtitleEdit/subtitleedit/releases/download/v5.1.0/SeConv-Windows-x64.zip).
- Official release asset SHA-256: `ce081a6c6844d44cb1373ee501a963c9e37c788bf757c2fa0b39821ef9969839`.
- Observed `seconv.exe` SHA-256: `b3e419e5294e65a8586662a1786ed14e86f97b9b0207c7c071d295b72953ff88`.
- Official [command-line documentation](https://github.com/SubtitleEdit/subtitleedit/blob/v5.1.0/docs/reference/command-line.md) documents `bluraysup`, resolution, font, outline, and alignment options. The [Blu-ray SUP writer](https://github.com/SubtitleEdit/subtitleedit/blob/v5.1.0/src/libuilogic/Export/ExportHandlerBluRaySup.cs) implements the bitmap output.
- The downloaded package's MIT license, copyright 2026 Nikolaj Olsson, is preserved verbatim at [licenses/SeConv-MIT.txt](licenses/SeConv-MIT.txt). Its SHA-256 is `4ed40bfa9c91bf1a67fd78fa6fc4a5b4efbbed2ca2c80c0b048a82cf8e6de639`.
- The tool is used locally from the unmodified upstream package; its binaries, including bundled native dependencies, are not committed or added to the player's distribution. This record describes the package's supplied license file and does not replace any dependency attribution needed if that package is redistributed later.

## Bungee Shade font

- Official font directory: [google/fonts at commit 6ce172f74aa355ea43eb964fa4a91570a4d3064d](https://github.com/google/fonts/tree/6ce172f74aa355ea43eb964fa4a91570a4d3064d/ofl/bungeeshade).
- Font: `BungeeShade-Regular.ttf`, unmodified.
- Git blob SHA-1: `80ada80a0404713e7cd31a6b7982ff7a549acdfe`.
- File SHA-256: `665c626082682b462f4e696308eafa7071c114da8f8ff3fb73a289c7ff7df482`.
- Copyright 2023 The Bungee Project Authors (`https://github.com/djrrb/Bungee`).
- License: SIL Open Font License 1.1, preserved verbatim at [licenses/BungeeShade-OFL.txt](licenses/BungeeShade-OFL.txt) and copied into every generated fixture directory.
- License Git blob SHA-1: `6f47072f01b8698301896b247b7087c023a4da2d`; SHA-256: `d5787a50dde5be6c6daecab9ed459939b1bc37cff0d0e00257eaf1ec2cf4c16c`.
- The TTF is attached only to the generated ASS MKV. No system/user font installation occurs. Keep the accompanying OFL file when sharing that fixture.

PGS rasterization uses the already available Windows Arial family. The generated bitmap contains original cue text; it contains no copied font file. The ASS fixture's embedded font is the pinned OFL font above.

## Existing official Emby media tools

The generator reuses FFmpeg/FFprobe from the previously verified official Emby 4.9.5.0 Windows portable download recorded by [EmbyClient.ServerValidation](../EmbyClient.ServerValidation/README.md). No Emby server, service, or installer is launched by this tool.

| File | SHA-256 |
| --- | --- |
| `embyserver-win-x64-4.9.5.0.7z` | `6883356517a42316ef5b270081f81d8de4151fec384fbe1ec1b31d2cf77688f6` |
| `ffmpeg.exe` | `969535ce1e41c35b93f62104b9099aa4b2fc803ebcfd7f2eb15c80d7e36340cf` |
| `ffprobe.exe` | `3d649e06a1979491d64ab91bc60d19a19ecb423792439700a409d2b09a0d87c2` |

These binaries remain local ignored dependencies and are not included in the player distribution. Their availability does not settle licensing for a future codec/backend distribution.

## Container and rendering references

[Matroska's attachment documentation](https://www.matroska.org/technical/attachments.html) describes embedded fonts and font-name matching. The fixture uses the supported legacy `application/x-truetype-font` attachment MIME type for this server version. [Matroska's codec specification](https://www.matroska.org/technical/codec_specs.html) distinguishes `S_TEXT/ASS` from `S_HDMV/PGS`. [FFmpeg's documentation](https://ffmpeg.org/ffmpeg.html) describes attachments, timestamp preservation, and subtitle-duration correction.

Local FFprobe output is the evidence for the actual generated file types. Reference PNGs are generated comparison assets, not screenshots or evidence of a native-player acceptance pass.

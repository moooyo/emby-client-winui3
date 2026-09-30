# Lumen Fonts

The Lumen interface bundles the unmodified Google Fonts variable TrueType
releases below. No fonts are downloaded at runtime. The upstream complete font
files are retained; neither file has been subsetted or converted for this app.
Manrope supplies Latin letters and numbers; Chinese interface text uses Windows
font fallback. Noto Serif SC supplies the Simplified Chinese display headings.

Both files are licensed under the SIL Open Font License 1.1. Keep this directory,
including both copyright notices and license texts, in every product distribution.
This does not change the license of the application's own source code.

## Provenance

Retrieved on 2026-09-30 from the authoritative
[Google Fonts repository](https://github.com/google/fonts) at commit
`23e54b51ddffbc7713c583748e3bd86f62b1fa4a`.

| Bundled asset | Family name | Weight axis | Size in bytes | SHA-256 |
| --- | --- | --- | --- | --- |
| `src/EmbyClient.App/Assets/Lumen/Fonts/NotoSerifSC-Variable.ttf` | `Noto Serif SC` | 200-900 | 25,125,512 | `050080D9255A86808F2945BFFAC582B31EF32BC36411CE29563B4961670C66F9` |
| `src/EmbyClient.App/Assets/Lumen/Fonts/Manrope-Variable.ttf` | `Manrope` | 200-800 | 164,700 | `3AE11C49DB0455A3CC33E37D380F20FDB8C7F8B41DC07625C177E3D87A9D6AE6` |

The local filenames omit the upstream `[wght]` suffix for convenient asset URIs.
Only the filenames have changed; the binaries remain byte-for-byte identical.

### Noto Serif SC

- [Font binary](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/notoserifsc/NotoSerifSC%5Bwght%5D.ttf)
- [License source](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/notoserifsc/OFL.txt)
- [Family metadata](https://github.com/google/fonts/blob/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/notoserifsc/METADATA.pb)
- [Upstream project](https://github.com/notofonts/noto-cjk)
- Upstream source revision recorded by Google Fonts:
  `985fa52c81c1d6692ccdd82bc3656e8fb932fd89`.
- Font binary Git blob SHA-1: `eab063faf229160a52d3760f5555150e4eb9e5bf`.
- Copyright in the supplied OFL text: `Copyright 2012 Google Inc. All Rights Reserved.`
- Copyright recorded in the family metadata: `(c) 2017-2024 Adobe (http://www.adobe.com/).`
- Retained license: [NotoSerifSC-OFL.txt](NotoSerifSC-OFL.txt).

### Manrope

- [Font binary](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/manrope/Manrope%5Bwght%5D.ttf)
- [License source](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/manrope/OFL.txt)
- [Family metadata](https://github.com/google/fonts/blob/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/manrope/METADATA.pb)
- [Upstream project](https://github.com/googlefonts/manrope)
- Upstream source revision recorded by Google Fonts:
  `6f81ebecdf65e4463b798cc07b16a4f8d5216917`.
- Font binary Git blob SHA-1: `75274da58537d6123b14f2cd0c355ad4681fc2b3`.
- Copyright in the supplied OFL text: `Copyright 2018 The Manrope Project Authors (https://github.com/googlefonts/manrope)`.
- Copyright recorded in the family metadata: `Copyright 2019 The Manrope Project Authors (https://github.com/googlefonts/manrope)`.
- Retained license: [Manrope-OFL.txt](Manrope-OFL.txt).

## WinUI References

The packaged font references use the internal family names, not the filenames:

```xml
<FontFamily x:Key="LumenSerifFont">ms-appx:///Assets/Lumen/Fonts/NotoSerifSC-Variable.ttf#Noto Serif SC</FontFamily>
<FontFamily x:Key="LumenSansFont">ms-appx:///Assets/Lumen/Fonts/Manrope-Variable.ttf#Manrope</FontFamily>
```

Set display headings to weight 700 (`Bold`) or 900 (`Black`) as specified by the
handoff. Set interface text to weights 400-700. Use the Windows Chinese fallback
for Chinese interface text rather than trying to subset or synthesize Chinese
glyphs in Manrope. Both internal family names were confirmed by loading the
files into a Windows private font collection without installing either font.

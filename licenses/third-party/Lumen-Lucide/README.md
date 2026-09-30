# Lumen Lucide Icons

The Lumen UI bundles recolored Lucide SVG icons supplied in the approved design
handoff. They live under `src/EmbyClient.App/Assets/Lumen/Icons/{d,w,y}` and are
loaded from packaged `ms-appx` URIs; no icon is downloaded at runtime.

The source handoff identifies these as Lucide icons and records the mapping from
its historical Fluent-shaped filenames to the actual Lucide names. SVG root
classes such as `lucide lucide-arrow-left` confirm that mapping; the filenames do
not make these Microsoft Fluent icon artwork. Changes in the handoff include
dark/white/gold recoloring, a 1.7 stroke width, and filled action variants. Keep
the source SVG metadata and the retained copyright/permission notices intact.

## Retained Upstream License

[ISC.txt](ISC.txt) retains the complete upstream Lucide `LICENSE`, including its
list of Feather-derived icons and the corresponding MIT license/Cole Bemis
notice. The filename is not a statement that the MIT portion can be omitted.
Icons used by this interface include Feather-derived arrow-left, check,
chevrons, headphones, info, log-out, maximize, minus, monitor, plus, search,
server, tv, type, and x; both upstream notice sections must remain in distribution.

Retrieved on 2026-09-30 from the official
[Lucide repository](https://github.com/lucide-icons/lucide) license revision
`e715245d62667c800e7f54c94b1b023692e900a3`:

- [Original LICENSE](https://raw.githubusercontent.com/lucide-icons/lucide/e715245d62667c800e7f54c94b1b023692e900a3/LICENSE)
- [License revision](https://github.com/lucide-icons/lucide/commit/e715245d62667c800e7f54c94b1b023692e900a3)
- [Upstream licensing guidance](https://lucide.dev/license)

Retained `ISC.txt`: 3,208 bytes; SHA-256
`B495047BD93A9B06913511076F504DABA17D5BBEB3E0650F3BB53A4220329C57`.
The complete retained text matches the pinned upstream file byte-for-byte.

The handoff does not record an exact Lucide release for its modified SVGs. This
license snapshot documents the identified upstream icon project's complete
notice, not an invented package/version provenance or a byte-identity claim for
the modified artwork. Preserve this directory alongside the bundled icons.
The app project recursively copies `licenses/third-party/` into its publish
payload; packaging must retain these files. This does not choose a license for
the application's own code or make a release/distribution legal conclusion.

# Third-party components

## Bundled .NET runtime

The Windows x64 release includes Microsoft .NET and Windows Desktop runtime 10.0.12. The MIT runtime licenses and .NET third-party notices from those NuGet runtime packs are retained in `Assets/Licenses/DotNet.txt`, `DotNet-ThirdParty.txt` and `WindowsDesktop.txt`, and copied to the package's `licenses/` folder. Refresh these notices when updating the bundled runtime.

## Bundled fonts

[Sora](https://github.com/sora-xor/sora-font) Regular/SemiBold and [IBM Plex Sans](https://github.com/IBM/plex) Regular/Medium/SemiBold/Italic are bundled as WPF resources. Static TTF files were downloaded from their official repositories on September 24, 2026. Both use the SIL Open Font License 1.1. Their complete notices are retained in `Assets/Licenses/Sora.txt` and `Assets/Licenses/IBMPlex.txt` and distributed in `app/licenses/`. The fonts do not require a system installation or a network connection at runtime.

## Lucide icons

The SVG icons bundled in `src/AIHub.Desktop/Assets/Icons` come from the official [Lucide project](https://github.com/lucide-icons/lucide). Downloaded September 24, 2026. Lucide uses the ISC license; icons derived from Feather also include the MIT notice. The complete upstream license is retained in that folder and distributed as `app/licenses/Lucide.txt`.

The app renders the original path, rectangle, and circle geometry natively in WPF. Icon colors and stroke weight follow the AI Hub theme. The central animated core and application icon are original geometric assets built for AI Hub.

## Markdig 1.4.0

[Markdig](https://github.com/xoofx/markdig) parses the Markdown in messages. It is installed through NuGet at an explicit version. AI Hub renders the resulting syntax tree into native WPF documents; it does not embed a browser or execute HTML in replies. The upstream BSD-2-Clause license is included in `src/AIHub.Desktop/Assets/Licenses/Markdig.txt` and distributed as `app/licenses/Markdig.txt`.

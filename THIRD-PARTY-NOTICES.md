# Third-party notices

Hindsight itself is MIT-licensed (see `LICENSE`). It ships or uses the following components.

## FFmpeg

The recorder runs FFmpeg as a separate program (`ffmpeg\ffmpeg.exe`, `ffmpeg\ffprobe.exe` and their DLLs);
Hindsight does not link against it. The bundled build is a pinned month-end Windows "gpl-shared" build from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), licensed under the **GNU GPL v3**
(`ffmpeg\LICENSE.txt`). The exact build is named in `ffmpeg\SOURCE.txt`.
Source code: <https://github.com/FFmpeg/FFmpeg> and the build scripts at <https://github.com/BtbN/FFmpeg-Builds>.
FFmpeg is a trademark of Fabrice Bellard.

## Fonts

[Sora](https://github.com/sora-xor/sora-font) and [Inter](https://github.com/rsms/inter) are embedded in the app under the
**SIL Open Font License 1.1** (`src/Hindsight.App/Resources/Fonts/OFL-*.txt`).

## NAudio

[NAudio](https://github.com/naudio/NAudio) (MIT) — WASAPI system-audio and microphone capture.

## CommunityToolkit.Mvvm

[.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) (MIT) — MVVM helpers for the UI.

## .NET

The [.NET runtime](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf) (MIT) are included
in the self-contained build.

## Game artwork

Game covers shown in the library come from each launcher's own local cache. For Steam games without a cached
cover, Hindsight downloads the public store artwork for that app id from Steam's CDN once and caches it locally.
All game names, logos and artwork belong to their respective owners.

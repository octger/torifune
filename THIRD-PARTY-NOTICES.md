# Third-Party Notices

Torifune includes or redistributes the following third-party components in its Windows release package. Torifune itself is licensed under the MIT License; these components remain subject to their respective licenses.

## Runtime components

| Component | Version | License | Project / source |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | 12.1.1 | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| Inter font | bundled by Avalonia.Fonts.Inter | SIL Open Font License 1.1 | <https://github.com/rsms/inter> |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| LibVLCSharp, LibVLCSharp.Avalonia | 3.10.1 | LGPL-2.1-or-later | <https://github.com/videolan/libvlcsharp> |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | LGPL-2.1-or-later | <https://code.videolan.org/videolan/vlc/-/tree/3.0.23.1> |
| Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging | 10.0.11 | MIT | <https://github.com/dotnet/runtime> |
| SkiaSharp | 3.119.4 | MIT | <https://github.com/mono/SkiaSharp> |
| HarfBuzzSharp | 8.3.1.3 | MIT | <https://github.com/mono/SkiaSharp> |
| Tmds.DBus.Protocol | 0.94.1 | MIT | <https://github.com/tmds/Tmds.DBus> |

The canonical license texts are available from the SPDX license list:

- MIT: <https://spdx.org/licenses/MIT.html>
- LGPL-2.1-or-later: <https://spdx.org/licenses/LGPL-2.1-or-later.html>
- SIL Open Font License 1.1: <https://spdx.org/licenses/OFL-1.1.html>

LibVLC is distributed as separate native libraries and is dynamically linked by Torifune. Corresponding source code for the redistributed LibVLC version is available from the VideoLAN source link above.

## Downloaded external tools

Torifune does not include yt-dlp or FFmpeg in its release package. After the user reviews and accepts the dependency notice, Torifune downloads these tools from their official release locations and verifies their SHA-256 checksums.

- yt-dlp: <https://github.com/yt-dlp/yt-dlp> (Unlicense; official Windows builds may include GPL-licensed dependencies)
- FFmpeg builds for yt-dlp: <https://github.com/yt-dlp/FFmpeg-Builds> (the selected build is GPLv3-or-later)

Users should review the license information distributed by each external tool before use.

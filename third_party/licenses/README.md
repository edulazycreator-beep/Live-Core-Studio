# Third-party components

Every third-party library, SDK or asset that ships with or is linked into Live Core Studio is listed here with its license text in this folder. Nothing is added to the product without an entry.

| Component | Version | Used by | License | Shipped? | Notes |
|---|---|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.0 | `LiveCoreStudio.UI` | MIT | Yes (DLL) | NuGet package. Add `CommunityToolkit.Mvvm-LICENSE.txt` when the first release is packaged. |
| .NET 10 runtime / WPF | 10.0 | managed app | MIT | Runtime dependency | Framework-dependent deployment. |

## Planned (not yet integrated)

| Component | License to review before integration |
|---|---|
| FFmpeg (libavcodec/libavformat/…) | LGPL 2.1+ (must build **without** `--enable-gpl`/`--enable-nonfree`; dynamic linking; ship source offer) |
| NDI SDK | NDI SDK License Agreement (redistribution terms, "NDI" trademark attribution) |
| Blackmagic DeckLink SDK | Blackmagic Design SDK EULA |
| SQLite | Public domain |
| SRT (libsrt) | MPL 2.0 |
| NVIDIA Video Codec SDK / Intel VPL / AMD AMF | Vendor SDK licenses |

No source code from OBS Studio, vMix, Wirecast or any other broadcast application is used in this repository.

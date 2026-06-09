> ⚠️ This project is source-available but all rights reserved. See [LICENSE](LICENSE).

# 🔮 SilentFlow

A local web app built with ASP.NET Core (.NET 10) that lets you download videos and audio from the web — paste a URL, pick your format, done.

Powered by [yt-dlp](https://github.com/yt-dlp/yt-dlp). Runs entirely on your machine. No cloud, no tracking, no nonsense.

## Supported Platforms

- YouTube
- TikTok
- Instagram
- Facebook
- And anything else yt-dlp supports

## Formats

| Format | Output |
|--------|--------|
| MP3 | Audio only, extracted and converted |
| MP4 | Video with audio, best available quality |

## Requirements

- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) — place `yt-dlp.exe` in the project root (or use the built-in update button)
- Windows

## Getting Started

```bash
git clone https://github.com/krlaskidata/SilentFlow.git
cd SilentFlow
dotnet run
```

Then open your browser at `https://localhost:5001`.

## Legal

SilentFlow is a tool for personal use. You are responsible for complying with the terms of service of any platform you use it with and the applicable copyright laws in your country. The author assumes no liability for misuse.

---

Made by [Laura B. Kraft](https://github.com/krlaskidata)

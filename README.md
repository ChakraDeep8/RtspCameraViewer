# RTSP Camera Viewer

A Windows desktop app (WPF, .NET 8) that shows a live grid of RTSP camera streams,
with full-screen single-camera view and automatic reconnect on stream drop.

## Features
- Add any number of RTSP cameras (name + URL, optional username/password)
- Live grid view, auto-arranged — the column count is chosen to give each feed the most
  area for its actual aspect ratio, rather than assuming 16:9
- **Camera classes**: group cameras however you like (by site, floor, purpose) and switch
  the grid between one class and all of them
- Expand one camera to fill the grid with the ⛶ button, or by double-clicking its name bar
  — Esc or ✕ to go back
- Auto-reconnect: if a stream errors out or ends, it retries every 5 seconds
- Manual "⟳" reconnect and "✕" remove buttons per camera (hover to reveal)
- Camera list is saved to `%AppData%\RtspCameraViewer\cameras.json` and reloaded on next launch

## Requirements
- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or the SDK, for building)
- Internet access the first time you build, to restore the LibVLCSharp / VLC NuGet packages

## Build & run

```powershell
cd RtspCameraViewer
dotnet build -c Release
dotnet run -c Release --project RtspCameraViewer
```

Or open `RtspCameraViewer.sln` in Visual Studio and press F5.

## Publish a standalone .exe

```powershell
cd RtspCameraViewer
dotnet publish RtspCameraViewer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```

The `publish` folder will contain `RtspCameraViewer.exe` plus the VLC runtime files — copy the whole
folder to another Windows machine to run it there (no separate VLC install needed).

## Adding cameras
Click **+ Add Camera**, enter a name and the RTSP URL, e.g.:

```
rtsp://192.168.1.64:554/Streaming/Channels/101
```

If your URL doesn't already contain credentials, you can fill in the optional Username/Password
fields instead of putting them in the URL.

The **Camera class** box groups cameras: pick an existing class to add this camera to it, or
type a new name to start one. Leave it blank if you don't want the camera grouped. Once any
camera has a class, a filter bar appears above the grid for switching between them.

## Notes
- Uses [LibVLCSharp](https://github.com/videolan/libvlcsharp) (VLC engine) for RTSP decoding — this
  is the most robust way to handle the wide variety of RTSP/H.264/H.265 camera streams reliably.
- Streams are forced to RTSP-over-TCP (`:rtsp-tcp`) for more reliable delivery through routers/firewalls.
- At most 12 cameras stream at once. This is a measured limit, not a guess: running ~26 streams
  concurrently made VLC report "buffer deadlock prevented" roughly four times as often and left
  about half of the connected streams without a decoder at all — which looked like cameras
  "not opening" and torn, smeared frames on the ones that did. Tuning the decode settings did
  not help; running fewer at a time did. Above the limit, cameras show as paused — filter to a
  class to watch the ones you care about.
- Audio, subtitles and VLC's on-screen overlays are disabled per stream; surveillance feeds carry
  no audio worth playing and each enabled track costs a decoder and buffers per tile.

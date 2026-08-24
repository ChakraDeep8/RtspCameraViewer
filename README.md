# RTSP Camera Viewer

A Windows desktop app (WPF, .NET 8) that shows a live grid of RTSP camera streams,
with full-screen single-camera view and automatic reconnect on stream drop.

## Features
- Add any number of RTSP cameras (name + URL, optional username/password)
- Live grid view, auto-arranged
- Double-click (or the ⛶ button) to view a camera full screen — Esc or the button to go back
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

## Notes
- Uses [LibVLCSharp](https://github.com/videolan/libvlcsharp) (VLC engine) for RTSP decoding — this
  is the most robust way to handle the wide variety of RTSP/H.264/H.265 camera streams reliably.
- Streams are forced to RTSP-over-TCP (`:rtsp-tcp`) for more reliable delivery through routers/firewalls.

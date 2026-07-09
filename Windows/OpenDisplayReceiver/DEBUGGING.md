# Debugging Windows receiver crashes

Use the `OpenDisplayReceiver-win-x64-debug.zip` artifact when the receiver exits unexpectedly or the video window stays black.

## Logs

The receiver writes logs from process startup to:

```text
%LOCALAPPDATA%\OpenDisplayReceiver\Logs\latest.log
```

Each run also creates a timestamped log next to `latest.log`:

```text
OpenDisplayReceiver-YYYYMMDD-HHMMSS-PID.log
```

The main window has buttons to copy the current log path and open the logs folder. If the app exits before the window appears, open the folder above manually and attach `latest.log`.

## Debug package

The debug package is built with:

```powershell
dotnet publish . -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=portable -p:DebugSymbols=true
```

It is intentionally not a single-file publish so `.pdb` files stay next to the binaries and stack traces are easier to symbolicate.

## Useful switches

```powershell
OpenDisplayReceiver.exe --windowed
OpenDisplayReceiver.exe --renderer ffplay --windowed
OpenDisplayReceiver.exe --renderer native --windowed
OpenDisplayReceiver.exe --no-mdns --windowed
OpenDisplayReceiver.exe --no-embed --windowed
```

Default rendering is `ffplay`. Use `--renderer native` only to test the Media Foundation renderer. Use `--no-mdns` to isolate Bonjour/mDNS socket or firewall issues. Use `--no-embed` if ffplay logs say it is decoding frames but the embedded video surface stays black.

## H.264 startup logs

Before starting the renderer, the receiver waits for SPS, PPS, and IDR NAL units. Useful lines look like:

```text
H.264 startup frame #1: ... nals=... types=[7,8,5] sps=True pps=True idr=True
Video renderer started after H.264 sync: ffplay
```

If logs only show non-IDR types such as `types=[1]`, the receiver will send `{"type":"kf"}` to the Mac to request a keyframe. In that case, attach both the Windows `latest.log` and the Mac app log if available.

# OpenDisplay Receiver for Windows

This is a Windows 10/11 x64 receiver for the existing OpenDisplay Mac sender protocol. It listens on TCP `:9000`, sends the same `hello` control message as the iOS receiver, keeps the Mac watchdog alive with `ping`, and displays the Mac's low-latency H.264 Annex B stream.

The default renderer is the bundled `ffplay` runtime in its own SDL window. It uses `-hwaccel auto` so FFmpeg selects D3D11VA, DXVA2, QSV, CUDA, or software fallback according to the Windows machine and bundled FFmpeg build. The native Windows Media Foundation renderer remains opt-in with `--renderer native`.

The receiver requests a keyframe immediately after `hello`, retries at 250/750/1500/2500 ms until the renderer is synchronized, logs the first eight H.264 frames' NAL types, and waits for SPS (7), PPS (8), then IDR (5). It caches the small SPS/PPS NAL units so parameter sets that arrive before the IDR are still passed to ffplay.

The Mac is still the sender: it creates the virtual display with `CGVirtualDisplay`, captures it with ScreenCaptureKit, and streams H.264 frames over the existing length-prefixed TCP protocol.

## Requirements

- Windows 10 or Windows 11, x64
- Windows firewall access for TCP `9000` on private networks
- UDP `5353` allowed on the local network if you want Bonjour/mDNS discovery
- .NET 8 SDK only when building from source; the CI artifact is self-contained

## Download / release artifact

The `Windows receiver` workflow publishes `OpenDisplayReceiver-win-x64.zip`. The zip contains:

- `OpenDisplayReceiver.exe` — self-contained win-x64 app
- `ffplay.exe` and any FFmpeg runtime DLLs found on the runner
- this README

Unzip it and run `OpenDisplayReceiver.exe`.

## Debug artifact

The workflow also publishes `OpenDisplayReceiver-win-x64-debug.zip`. Use this when a Windows machine crashes or behaves differently from CI. It is a Debug configuration publish with managed DLLs and PDB symbols kept separate, plus `DEBUGGING.txt`.

Useful debug commands:

```powershell
.\OpenDisplayReceiver.exe --windowed
.\OpenDisplayReceiver.exe --windowed --renderer ffplay
.\OpenDisplayReceiver.exe --windowed --ffplay-hwaccel d3d11va
.\OpenDisplayReceiver.exe --windowed --ffplay-loglevel verbose
.\OpenDisplayReceiver.exe --windowed --ffplay-hwaccel none
.\OpenDisplayReceiver.exe --windowed --renderer native
.\OpenDisplayReceiver.exe --windowed --no-mdns
```

## Logs

The app writes a persistent log file on every run:

```text
%LOCALAPPDATA%\OpenDisplayReceiver\Logs\OpenDisplayReceiver-YYYYMMDD-HHMMSS-PID.log
%LOCALAPPDATA%\OpenDisplayReceiver\Logs\latest.log
```

The receiver window shows the current log path and has **Copy log path** and **Open log folder** buttons. Startup details, renderer selection, H.264 startup NAL types, keyframe requests, mDNS status, connection events, ffplay arguments/output/exit code, and full exception stacks are written there. Each connection has a `[conn N]` correlation id.

Every five seconds a `perf:` line records FPS, bitrate, stalls, maximum encoded-frame size, average/maximum ffplay pipe-write latency, slow writes, control RTT, receive-buffer capacity, managed memory, process working set, and Gen0/1/2 collections. Use `--ffplay-loglevel verbose` or `debug` when the default `info` output is insufficient. The on-screen log is bounded, while complete logs remain in the files.

Logs older than 14 days are removed on startup. The logger keeps file handles open instead of reopening two files for every line and flushes each completed line for crash diagnosis. If `%LOCALAPPDATA%` logging cannot be initialized, it falls back to the system temp directory instead of crashing the app.

## Build and run from source

From this directory:

```powershell
dotnet run -c Release -- --width 1920 --height 1080 --scale 2
```

Create the same self-contained exe shape that CI publishes:

```powershell
dotnet publish . -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

Useful options:

```powershell
--width 2560              # pixels announced to the Mac
--height 1440
--scale 2                # the Mac creates a HiDPI display at pixels / 2 points
--port 9000
--bind 0.0.0.0
--name "Windows Display"
--renderer ffplay        # default: bundled ffplay renderer
--renderer native        # test Media Foundation native renderer
--ffplay "C:\ffmpeg\bin\ffplay.exe"
--ffplay-hwaccel auto    # default: let FFmpeg choose D3D11VA/DXVA2/QSV/CUDA
--ffplay-hwaccel none    # diagnostic software-decoding fallback
--ffplay-loglevel info   # error|warning|info|verbose|debug|trace
--fullscreen             # default: start the external ffplay window fullscreen
--windowed               # start ffplay as a normal external window
--embed                  # experimental SDL_WINDOWID embedding; external is safer/default
--no-embed               # explicit external-window compatibility option
--no-mdns                # disable _opensidecar._tcp Bonjour advertisement
```

For the default external ffplay window, use ffplay's `F` key to toggle fullscreen. The WinForms `F11` / double-click controls apply to the native or experimental embedded renderer.

## ffplay latency and throughput

The receiver starts ffplay with raw H.264 input, `-analyzeduration 1000000`, `-probesize 1048576`, hardware acceleration, direct AVIO, no demux buffering, low-delay decode, video-clock sync, and frame dropping. `ffplay` stderr stays in the application log so the selected hardware path or software fallback can be verified on the target PC.

The steady-state receive path rents one connection-level buffer from `ArrayPool<byte>`, grows it only when required, and reuses it for every frame. A `ReadOnlyMemory<byte>` slice goes directly from `NetworkStream` to ffplay stdin through `ValueTask`, avoiding per-frame arrays, pool churn, Task conversion, and pipe flushes. Startup NAL parsing stops once SPS/PPS/IDR synchronization completes.

## Bonjour / mDNS discovery

The receiver advertises itself on the existing OpenDisplay service type:

```text
_opensidecar._tcp.local
```

The advertisement publishes PTR, SRV, TXT, and A records on multicast DNS `224.0.0.251:5353`. TXT data includes the stable install id, device name, platform, width, height, and scale. If Windows firewall, another mDNS responder, or the network blocks UDP `5353`, the app logs the failure and the manual endpoint override below still works.

The Mac and iOS app targets already declare `_opensidecar._tcp` in `NSBonjourServices`, so the Windows receiver uses the same service type instead of inventing a new one.

## Connect from the Mac

If Bonjour discovery does not show the Windows receiver yet, use the Mac sender's manual endpoint override:

```sh
defaults write com.peetzweg.opensidecar.mac host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac port "9000"
defaults write com.peetzweg.opensidecar.mac localCursor -bool false
open -a OpenDisplay
```

For Debug builds from Xcode, use the debug bundle id instead:

```sh
defaults write com.peetzweg.opensidecar.mac.debug host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac.debug port "9000"
defaults write com.peetzweg.opensidecar.mac.debug localCursor -bool false
```

Clear the override later with:

```sh
defaults delete com.peetzweg.opensidecar.mac host
defaults delete com.peetzweg.opensidecar.mac port
defaults delete com.peetzweg.opensidecar.mac localCursor
```

## USB / usbmuxd note

A passive USB-C cable between a Mac and a Windows PC is not enough for this receiver. Both machines are USB hosts, so there is no generic USB data pipe for an app or normal Windows host driver to read.

`usbmuxd` is the wrong layer for ordinary Mac-to-PC USB-C: it multiplexes connections over USB to an iOS device and exposes a host-side socket API. Porting that daemon to Windows still expects an Apple-style USB device on the other end; it does not make a Windows PC behave like an iPhone/iPad USB function.

Working wired options remain:

- Ethernet cable or USB-C Ethernet adapters on both machines.
- A USB transfer / bridge cable that exposes a network interface or ships an SDK that can be integrated explicitly.
- Hardware where the Windows side can really run USB device/gadget mode, which is uncommon on normal PCs.

Plain USB-C without Thunderbolt/USB4 networking, bridge hardware, or a real USB device/function controller is not implemented because there is no transport endpoint to bind to.

## Cursor note

The iOS receiver draws the Mac cursor locally from `cursor` / `cursorImg` control messages. This Windows receiver does not draw that local overlay yet. To make the cursor visible on Windows, tell the Mac sender to include the cursor in the captured video:

```sh
defaults write com.peetzweg.opensidecar.mac localCursor -bool false
```

Restart the Mac app after changing this setting.

## Current scope

Implemented:

- Windows 10/11 x64 receiver
- TCP receiver on `:9000`
- Bonjour/mDNS advertisement on `_opensidecar._tcp.local`
- Existing OpenDisplay length-prefixed frame protocol
- `hello`, `ping`, `pong`, and periodic `stats` control messages
- Bundled `ffplay` renderer by default
- External ffplay SDL window by default; experimental embedding with `--embed`
- Automatic ffplay H.264 hardware acceleration with a selectable override
- H.264 SPS/PPS/IDR startup sync, parameter-set priming, and scheduled keyframe requests
- One reusable pooled receive buffer per connection, `ValueTask` pipe writes, and no per-frame flush
- Correlated connection logs and five-second decoder/backpressure/memory/GC telemetry
- Native Windows H.264 rendering with Media Foundation for testing
- Persistent app and crash logs in `%LOCALAPPDATA%\OpenDisplayReceiver\Logs`
- Debug CI artifact with PDB symbols
- Fullscreen receiver window by default, with `--windowed` opt-out
- Stable install id stored in `%APPDATA%\OpenDisplayReceiver\install-id.txt`
- Manual Mac endpoint setup instructions shown in the app
- CI-produced self-contained win-x64 exe zip

Not implemented yet:

- Native local cursor overlay rendering
- Touch / pointer input back to macOS
- Direct passive USB-C Mac-to-PC transport
- Audio forwarding

# OpenDisplay Receiver for Windows

This is a first Windows 10/11 x64 receiver for the existing OpenDisplay Mac sender protocol. It listens on TCP `:9000`, sends the same `hello` control message as the iOS receiver, keeps the Mac watchdog alive with `ping`, and pipes the Mac's low-latency H.264 Annex B stream into `ffplay`.

The Mac is still the sender: it creates the virtual display with `CGVirtualDisplay`, captures it with ScreenCaptureKit, and streams H.264 frames over the existing length-prefixed TCP protocol.

## Requirements

- Windows 10 or Windows 11, x64
- FFmpeg with `ffplay.exe` available on `PATH`
- Windows firewall access for TCP `9000` on private networks
- .NET 8 SDK only when building from source; the CI artifact is self-contained

## Download / release artifact

The `Windows receiver` workflow publishes a self-contained `OpenDisplayReceiver-win-x64.zip` artifact. The zip contains `OpenDisplayReceiver.exe` plus this README. `ffplay.exe` is still expected to be installed separately or passed with `--ffplay`.

## Build and run from source

From this directory:

```powershell
dotnet run -c Release -- --width 1920 --height 1080 --scale 2
```

Create the same self-contained exe that CI publishes:

```powershell
dotnet publish . -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

Useful options:

```powershell
--width 2560          # pixels announced to the Mac
--height 1440
--scale 2            # the Mac creates a HiDPI display at pixels / 2 points
--port 9000
--name "Windows Display"
--ffplay "C:\ffmpeg\bin\ffplay.exe"
--fullscreen          # default: start the ffplay video window fullscreen
--windowed            # start as a normal window; press f in ffplay to toggle fullscreen
```

## Connect from the Mac

This first Windows receiver uses the Mac sender's manual endpoint override:

```sh
defaults write com.peetzweg.opensidecar.mac host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac port "9000"
open -a OpenDisplay
```

For Debug builds from Xcode, use the debug bundle id instead:

```sh
defaults write com.peetzweg.opensidecar.mac.debug host "WINDOWS_IP"
defaults write com.peetzweg.opensidecar.mac.debug port "9000"
```

Clear the override later with:

```sh
defaults delete com.peetzweg.opensidecar.mac host
defaults delete com.peetzweg.opensidecar.mac port
```

## USB / wired note

A passive USB-C cable between a Mac and a Windows PC is not enough for this receiver. Both machines are USB hosts, so there is no generic USB data pipe for an app or normal Windows driver to read. This receiver therefore treats wired connectivity as TCP over an actual network interface.

Working wired options:

- Ethernet cable or USB-C Ethernet adapters on both machines.
- A USB transfer / bridge cable that exposes a network interface or ships an SDK that can be integrated explicitly.
- Hardware where the Windows side can really run USB device/gadget mode, which is uncommon on normal PCs.

Plain USB-C without Thunderbolt/USB4 networking or bridge hardware is not implemented because there is no transport endpoint to bind to.

## Cursor note

The iOS receiver draws the Mac cursor locally from `cursor` / `cursorImg` control messages. This first Windows receiver relies on `ffplay`, so it cannot draw that local overlay yet. To make the cursor visible on Windows, tell the Mac sender to include the cursor in the captured video:

```sh
defaults write com.peetzweg.opensidecar.mac localCursor -bool false
```

For Debug builds:

```sh
defaults write com.peetzweg.opensidecar.mac.debug localCursor -bool false
```

Restart the Mac app after changing this setting.

## Current scope

Implemented:

- Windows 10/11 x64 receiver
- TCP receiver on `:9000`
- Existing OpenDisplay length-prefixed frame protocol
- `hello`, `ping`, `pong`, and periodic `stats` control messages
- H.264 Annex B stream forwarding to `ffplay`
- Fullscreen video window by default, with `--windowed` opt-out
- Stable install id stored in `%APPDATA%\OpenDisplayReceiver\install-id.txt`
- Manual Mac endpoint setup instructions printed at startup
- CI-produced self-contained win-x64 exe zip

Not implemented yet:

- Bonjour/mDNS discovery from the Mac device list
- Native Windows H.264 renderer
- Local cursor overlay rendering
- Touch / pointer input back to macOS
- Direct passive USB-C Mac-to-PC transport
- Audio forwarding

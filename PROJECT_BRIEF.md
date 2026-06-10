# Project Brief — Mac → iPhone/iPad Screen Extension ("free Duet/Sidecar")

> **For Claude Code:** Read this whole file plus `MacSender.swift` and `PhoneReceiver.swift`
> before doing anything. We are **at Milestone 1**. Your job is to help set up the Xcode
> projects, get the iOS app onto a physical iPhone, run the Mac app, and iterate through the
> milestones below. Ask before relitigating decisions in the "Decisions already made" section.

## 1. Goal

Build a free, self-hosted app that uses an **iPhone (now) / iPad (later)** as a **true extended
display** for a Mac — the macOS desktop genuinely treats the device as a second monitor, not a
mirror. For personal use only.

**Motivation:** Duet Display moved to a subscription (too expensive); Apple Sidecar is unusable
here because the two devices can't share one Apple ID. We are building the missing free option.

## 2. Why this is feasible

- macOS exposes a **private (undocumented) `CGVirtualDisplay` API** that makes the OS believe a
  real monitor is attached. It's well reverse-engineered and used in shipping products
  (BetterDisplay, DeskPad, SimpleDisplay, and "Tab Display" — which does exactly this iPad/phone-
  as-monitor idea via the `node-mac-virtual-display` wrapper). Use those as reference for M2.
- The **phone is just a video client**: decode an H.264 stream, render it, send touch events back.
  iOS and iPadOS use identical frameworks, so iPhone code ports to iPad with only screen-size
  changes.

## 3. Architecture / pipeline

```
MAC (sender)                                   PHONE (receiver)
CGVirtualDisplay (M2; main display in M1)
   -> ScreenCaptureKit (SCStream capture)
   -> VideoToolbox H.264 encode
   -> TCP socket  ============================> TCP listener
                                                 -> reassemble Annex B
                                                 -> AVSampleBufferDisplayLayer (decodes+renders)
   <=========================================== touch/pencil events (M3)
   -> CGEvent injection (M3)
```

**Roles:** the **phone LISTENS** (TCP server), the **Mac CONNECTS** (TCP client). This ordering is
required so the same code works over USB via `usbmux`.

**Transport is orthogonal to app code:** over WiFi the Mac connects to the phone's LAN IP; over USB
the Mac runs `iproxy 9000 9000` and connects to `127.0.0.1`. Only the host string changes. We are
doing **USB/wired first** for lower latency and zero network config.

## 4. Wire protocol (keep consistent across both sides)

- Each frame is sent as: **`[4-byte big-endian length]` + `[Annex B payload]`**.
- Annex B NAL units are separated by 4-byte start codes `00 00 00 01`.
- On keyframes, the payload is prefixed with **SPS then PPS** (also start-code delimited).
- The receiver builds a `CMVideoFormatDescription` from SPS/PPS, wraps each VCL NALU as AVCC
  (4-byte length prefix), and enqueues a `CMSampleBuffer` to `AVSampleBufferDisplayLayer` with the
  `DisplayImmediately` attachment set.

## 5. Current state — files in the repo

- **`MacSender.swift`** — macOS app target. Captures the **main display** (M1), H.264-encodes with
  low-latency VideoToolbox settings (real-time, no B-frames), converts to Annex B, sends framed
  over `NWConnection`. The single line to change for M2 is marked in a comment.
- **`PhoneReceiver.swift`** — iOS app target. `NWListener` on port 9000, deframes, reassembles
  Annex B, builds the format description, renders via `AVSampleBufferDisplayLayer`. Includes a
  `VideoView` (UIView backed by the display layer) to place full-screen in a view controller.

Both are **starting skeletons**, written without compiling. Expect to fix small things.

## 6. Milestone ladder (build in this order — it de-risks everything)

1. **M1 — Mirror main display → phone over USB.** Public APIs only; no private virtual-display
   code yet. This is ~80% of the work and all the latency tuning. **← we are here.**
2. **M2 — Extend, not mirror.** Create a `CGVirtualDisplay`, find its matching `SCDisplay` by
   `displayID`, and swap it into `MacSender` at the marked line. Now macOS has a real 2nd screen.
3. **M3 — Input back-channel.** Send touch (and later Pencil) coordinates from the phone; inject as
   mouse/tablet input on the Mac with `CGEvent`. Map phone points → virtual-display coordinates.
4. **M4 — WiFi + iPad + polish.** Add Bonjour discovery (so no hardcoded IP), switch host to LAN IP,
   deploy to iPad, handle DPI/resolution mapping, reconnect logic, and adaptive bitrate.

## 7. Environment setup (Claude Code: guide the user through these)

- **Xcode:** create a workspace with two targets — one macOS app, one iOS app. (A simple AppKit/
  SwiftUI shell for the Mac app is needed so the Screen Recording permission prompt appears.)
- **Homebrew + libimobiledevice:** `brew install libimobiledevice` (provides `iproxy`).
- **USB tunnel:** `iproxy 9000 9000` (forwards Mac `localhost:9000` → phone port 9000).
- **Screen Recording permission:** System Settings → Privacy & Security → Screen Recording → enable
  the Mac app. ScreenCaptureKit returns black frames without it.
- **Sideloading to the iPhone:** free Apple ID provisioning works but the app **expires after
  ~7 days** and must be re-deployed; a paid Apple Developer account ($99/yr) gives stable
  provisioning. After first install, trust the developer cert on the phone (Settings → General →
  VPN & Device Management).

## 8. Run sequence for M1

1. Build/run the iOS app on the iPhone so it's listening on 9000.
2. On the Mac: `iproxy 9000 9000`.
3. Run the Mac app with `host: "127.0.0.1", port: 9000`; grant Screen Recording permission.
4. Mac screen should appear on the phone.

## 9. Known gotchas / watch-items

- `AVSampleBufferDisplayLayer` **stalls until it gets an initial keyframe** (SPS/PPS). Force a
  keyframe on the first frame, or trigger one periodically.
- Black screen on the phone → log whether `formatDesc` ever gets built (SPS/PPS parsing is the
  usual first failure point).
- **`usbmux` requires the device to be the listener** — don't flip the roles.
- **ScreenCaptureKit can get confused with multiple virtual displays** (known longstanding bug);
  relevant at M2/M4 if more than one virtual display exists.
- `CGVirtualDisplay` is **private**: capped at **60 Hz**, **can break across macOS versions**, and
  **cannot ship on the Mac App Store** (fine — this is personal/sideloaded).
- Retina/DPI: the virtual display's resolution should map to the phone's point size × scale, or text
  will look wrong. Tune at M2/M4.
- Latency is the long tail: tune bitrate, `queueDepth`, frame pacing, and consider dirty-rectangle /
  partial updates later.

## 10. Decisions already made (don't relitigate without reason)

- **H.264** first (not HEVC) — simpler; revisit HEVC for quality later.
- **`AVSampleBufferDisplayLayer`** for decode+render instead of a manual `VTDecompressionSession`.
- **TCP** for M1 (not UDP/WebRTC) — simplest, and USB is reliable. Reconsider UDP/WebRTC for WiFi.
- **Wired/USB first**, WiFi later.
- **iPhone first**, iPad later (same codebase).

## 11. Future / open questions

- HEVC for better quality-per-bit.
- UDP or WebRTC transport for WiFi resilience and lower latency.
- Multi-touch + Apple Pencil pressure (iPad).
- Adaptive bitrate; reconnect/resume.
- Multiple virtual displays.
- Audio is out of scope unless requested.

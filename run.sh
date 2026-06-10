#!/bin/zsh
# Start the USB tunnel and the Mac sender app. The phone app must be running
# (it listens on :9000); the Mac app retries until the tunnel is up.
set -e
cd "$(dirname "$0")"

APP=build/Build/Products/Debug/OpenSidecar.app
if [[ ! -d $APP ]]; then
  echo "Mac app not built — run: xcodegen generate && xcodebuild -project OpenSidecar.xcodeproj -scheme OpenSidecarMac -configuration Debug -derivedDataPath build build"
  exit 1
fi

if ! pgrep -fq "iproxy 9000"; then
  echo "starting iproxy 9000 9000…"
  iproxy 9000 9000 &
  IPROXY_PID=$!
  trap "kill $IPROXY_PID 2>/dev/null" EXIT
fi

open "$APP"
echo "OpenSidecar running — logs at /tmp/opensidecar-mac.log. Ctrl-C to stop the tunnel."
wait

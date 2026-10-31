#!/usr/bin/env bash
# Uçtan uca düzeneği başlatır: yalıtılmış App (TDOWNLOADER_DATA_DIR/TDOWNLOADER_PIPE), yerel site + köprü,
# geçici profilli Brave (kullanıcının profiline dokunmaz) + köprüye bağlı eklenti kopyası.
#   tests/e2e/start.sh <çalışmaKlasörüWindowsYolu> [/direct]
set -e
W="$1"; PAGE="${2:-/}"
HERE="$(cd "$(dirname "$0")" && pwd)"
APP="$HERE/../../src/TDownloader.App/bin/Debug/net10.0-windows/TDownloader.exe"
mkdir -p "$W/data"
export TDOWNLOADER_PIPE=TDownloader.Ipc.test
(TDOWNLOADER_DATA_DIR="$W/data" nohup "$APP" --background > "$W/app.log" 2>&1 &)
sleep 3
(nohup node "$HERE/site.mjs" "$W" > "$W/site.log" 2>&1 &)
for i in $(seq 1 60); do [ -f "$W/ports.json" ] && grep -q pipe "$W/site.log" 2>/dev/null && break; sleep 1; done
node "$HERE/make-ext.mjs" "$W"
FILM=$(node -e "console.log(JSON.parse(require('fs').readFileSync(process.argv[1])).film)" "$W/ports.json")
rm -rf "$W/profile3"
(nohup "/c/Program Files/BraveSoftware/Brave-Browser/Application/brave.exe" --user-data-dir="$W/profile3" --remote-debugging-port=9333 \
  --remote-allow-origins='*' --load-extension="$W/ext" --disable-features=DisableLoadExtensionCommandLineSwitch --no-first-run \
  --no-default-browser-check --window-position=100,50 --window-size=1300,800 --autoplay-policy=no-user-gesture-required \
  "http://localhost:$FILM$PAGE" > "$W/brave.log" 2>&1 &)
echo "film port $FILM"

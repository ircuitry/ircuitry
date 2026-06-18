#!/usr/bin/env bash
# Install the ircuitry control server as a systemd service (bare-metal, no Docker).
# It restarts on failure and comes back on reboot.
#
#   ./install-server.sh [--bin DIR] [--data DIR] [--user NAME] [--port N] [--bind ADDR] [--flags "..."]
#
# Defaults: binary = this script's directory, data = ~USER/ircuitry-server-data,
# user = the human who ran sudo, port 48700, bind 0.0.0.0, flags "--web".
# Re-execs itself with sudo if not already root.
set -euo pipefail

BIN="$(cd "$(dirname "$0")" && pwd)"
DATA=""
USER_NAME="${SUDO_USER:-$USER}"
PORT=48700
BIND=0.0.0.0
FLAGS="--web"
UNIT=/etc/systemd/system/ircuitry-server.service

while [ $# -gt 0 ]; do
  case "$1" in
    --bin)   BIN="$2"; shift 2;;
    --data)  DATA="$2"; shift 2;;
    --user)  USER_NAME="$2"; shift 2;;
    --port)  PORT="$2"; shift 2;;
    --bind)  BIND="$2"; shift 2;;
    --flags) FLAGS="$2"; shift 2;;
    -h|--help) sed -n '2,12p' "$0"; exit 0;;
    *) echo "unknown arg: $1" >&2; exit 1;;
  esac
done

# need root to write the unit + manage the service
if [ "$(id -u)" -ne 0 ]; then exec sudo -E "$0" --bin "$BIN" ${DATA:+--data "$DATA"} --user "$USER_NAME" --port "$PORT" --bind "$BIND" --flags "$FLAGS"; fi

[ -x "$BIN/Ircuitry" ] || { echo "error: no executable Ircuitry binary in '$BIN' (pass --bin DIR)"; exit 1; }
id "$USER_NAME" >/dev/null 2>&1 || { echo "error: no such user '$USER_NAME'"; exit 1; }
: "${DATA:=$(getent passwd "$USER_NAME" | cut -d: -f6)/ircuitry-server-data}"

install -d -o "$USER_NAME" -g "$USER_NAME" "$DATA"
command -v bwrap >/dev/null 2>&1 || echo "note: bubblewrap not found - code nodes won't be sandboxed. Install it (apt install bubblewrap) or run with --flags '--web --no-code'."

# render the unit from the template that sits next to this script
TEMPLATE="$(dirname "$0")/ircuitry-server.service"
[ -f "$TEMPLATE" ] || { echo "error: ircuitry-server.service not found next to this script"; exit 1; }
sed -e "s#__USER__#$USER_NAME#g" -e "s#__BIN__#$BIN#g" -e "s#__DATA__#$DATA#g" \
    -e "s#__BIND__#$BIND#g" -e "s#__PORT__#$PORT#g" -e "s#__FLAGS__#$FLAGS#g" \
    "$TEMPLATE" > "$UNIT"

systemctl daemon-reload
systemctl enable --now ircuitry-server.service

echo "installed and started ircuitry-server (user=$USER_NAME data=$DATA port=$PORT)"
echo "  status:  systemctl status ircuitry-server"
echo "  logs:    journalctl -u ircuitry-server -f"
echo "  update:  replace the binary in $BIN, then: systemctl restart ircuitry-server"
sleep 2
TOKEN="$(grep -oE '"[0-9a-f]{40,}"' "$DATA/server-tokens.json" 2>/dev/null | head -1 | tr -d '"' || true)"
[ -n "$TOKEN" ] && echo "  admin token: $TOKEN" || echo "  admin token: see 'journalctl -u ircuitry-server' (printed on first start)"

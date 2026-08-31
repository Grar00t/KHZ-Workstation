#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

IMAGE='onlyoffice/documentserver@sha256:e3da62a847b9a5d51a11f73cfea1d9c13c3be3809614490d4edddcf01dcf919b'
NETWORK='khz-office-offline'
CONTAINER='khz-onlyoffice-offline'

command -v docker >/dev/null
command -v socat >/dev/null
command -v python3 >/dev/null
command -v curl >/dev/null

if ! sudo docker network inspect "$NETWORK" >/dev/null 2>&1; then
    sudo docker network create --driver bridge --internal "$NETWORK" >/dev/null
fi

if ! sudo docker container inspect "$CONTAINER" >/dev/null 2>&1; then
    sudo docker run -d \
        --name "$CONTAINER" \
        --network "$NETWORK" \
        -e JWT_ENABLED=false \
        -e ALLOW_PRIVATE_IP_ADDRESS=true \
        -e PLUGINS_ENABLED=false \
        -e METRICS_ENABLED=false \
        -e EXAMPLE_ENABLED=false \
        "$IMAGE" >/dev/null
else
    CURRENT_IMAGE="$(sudo docker inspect -f '{{.Config.Image}}' "$CONTAINER")"
    if [[ "$CURRENT_IMAGE" != "$IMAGE" ]]; then
        echo "ERROR: existing container uses unexpected image:"
        echo "$CURRENT_IMAGE"
        exit 1
    fi

    sudo docker start "$CONTAINER" >/dev/null 2>&1 || true
fi

OO_IP="$(sudo docker inspect \
    -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' \
    "$CONTAINER")"

GATEWAY_IP="$(sudo docker network inspect "$NETWORK" \
    --format '{{(index .IPAM.Config 0).Gateway}}')"

for _ in $(seq 1 60); do
    if curl -fsS "http://${OO_IP}/" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

curl -fsS "http://${OO_IP}/" >/dev/null

if ! curl -fsS "http://${GATEWAY_IP}:8090/health" >/dev/null 2>&1; then
    KHZ_ROOT="$ROOT" \
    KHZ_GATEWAY_BIND="$GATEWAY_IP" \
    nohup python3 "$ROOT/tools/office-spike/gateway.py" \
        >/tmp/khz-office-gateway.log 2>&1 &
fi

sleep 1
curl -fsS "http://${GATEWAY_IP}:8090/health" >/dev/null

if ! curl -fsS "http://127.0.0.1:8088/web-apps/apps/api/documents/api.js" \
    >/dev/null 2>&1; then
    nohup socat \
        TCP-LISTEN:8088,bind=127.0.0.1,reuseaddr,fork \
        TCP:"${OO_IP}":80 \
        >/tmp/khz-office-8088.log 2>&1 &
fi

if ! curl -fsS "http://127.0.0.1:8090/health" >/dev/null 2>&1; then
    nohup socat \
        TCP-LISTEN:8090,bind=127.0.0.1,reuseaddr,fork \
        TCP:"${GATEWAY_IP}":8090 \
        >/tmp/khz-office-8090.log 2>&1 &
fi

sleep 1

curl -fsS "http://127.0.0.1:8088/web-apps/apps/api/documents/api.js" \
    >/dev/null

curl -fsS "http://127.0.0.1:8090/health"
echo
echo "KHZ ONLYOFFICE SPIKE READY"
echo "EDITOR=http://localhost:8090/editor"
echo "NOTE=JWT_DISABLED_SPIKE_ONLY"

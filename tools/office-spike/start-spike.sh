#!/usr/bin/env bash
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUNTIME_DIR="$ROOT/tools/office-spike/.runtime"
DOCUMENT_DIR="$RUNTIME_DIR/documents"
SESSION_FILE="$RUNTIME_DIR/session.env"
GATEWAY_PID_FILE="$RUNTIME_DIR/gateway.pid"
PROXY_PID_FILE="$RUNTIME_DIR/gateway-proxy.pid"
DOCSERVER_ENV_FILE="$RUNTIME_DIR/documentserver.env"
JWT_SECRET_FILE="$RUNTIME_DIR/gateway-jwt.secret"
GATEWAY_TOKEN_FILE="$RUNTIME_DIR/gateway-token.secret"
CURL_AUTH_FILE="$RUNTIME_DIR/gateway-curl.conf"

IMAGE='onlyoffice/documentserver@sha256:e3da62a847b9a5d51a11f73cfea1d9c13c3be3809614490d4edddcf01dcf919b'
NETWORK='khz-office-offline'
CONTAINER='khz-onlyoffice-offline'

command -v docker >/dev/null
command -v socat >/dev/null
command -v python3 >/dev/null
command -v curl >/dev/null

if docker info >/dev/null 2>&1; then
    DOCKER=(docker)
elif command -v sudo >/dev/null && sudo docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
else
    echo "ERROR: Docker is unavailable to the current user." >&2
    exit 1
fi

umask 077
mkdir -p "$DOCUMENT_DIR"
rm -f \
    "$SESSION_FILE" \
    "$DOCSERVER_ENV_FILE" \
    "$JWT_SECRET_FILE" \
    "$GATEWAY_TOKEN_FILE" \
    "$CURL_AUTH_FILE"

stop_managed_process() {
    local pid_file="$1"
    local expected="$2"

    if [[ ! -f "$pid_file" ]]; then
        return
    fi

    local pid
    pid="$(<"$pid_file")"

    if [[ "$pid" =~ ^[0-9]+$ ]] && [[ -r "/proc/$pid/cmdline" ]]; then
        local command_line
        command_line="$(tr '\0' ' ' < "/proc/$pid/cmdline")"

        if [[ "$command_line" == *"$expected"* ]]; then
            kill "$pid" >/dev/null 2>&1 || true

            for _ in $(seq 1 20); do
                if ! kill -0 "$pid" >/dev/null 2>&1; then
                    break
                fi
                sleep 0.1
            done

            kill -KILL "$pid" >/dev/null 2>&1 || true
        fi
    fi

    rm -f "$pid_file"
}

stage_document() {
    local source="$1"
    local destination="$2"

    if [[ ! -f "$source" ]]; then
        echo "ERROR: missing Office fixture: $source" >&2
        exit 1
    fi

    if [[ ! -f "$destination" ]]; then
        cp -- "$source" "$destination"
    fi
}

stop_managed_process "$PROXY_PID_FILE" 'TCP-LISTEN:8090'
stop_managed_process "$GATEWAY_PID_FILE" 'tools/office-spike/gateway.py'

if "${DOCKER[@]}" container inspect "$CONTAINER" >/dev/null 2>&1; then
    "${DOCKER[@]}" rm -f "$CONTAINER" >/dev/null
fi

cleanup_on_error() {
    local status=$?
    set +e
    stop_managed_process "$PROXY_PID_FILE" 'TCP-LISTEN:8090'
    stop_managed_process "$GATEWAY_PID_FILE" 'tools/office-spike/gateway.py'
    "${DOCKER[@]}" rm -f "$CONTAINER" >/dev/null 2>&1
    rm -f \
        "$SESSION_FILE" \
        "$DOCSERVER_ENV_FILE" \
        "$JWT_SECRET_FILE" \
        "$GATEWAY_TOKEN_FILE" \
        "$CURL_AUTH_FILE"
    exit "$status"
}

trap cleanup_on_error ERR

if ! "${DOCKER[@]}" network inspect "$NETWORK" >/dev/null 2>&1; then
    "${DOCKER[@]}" network create \
        --driver bridge \
        --internal \
        "$NETWORK" >/dev/null
fi

NETWORK_INTERNAL="$(
    "${DOCKER[@]}" network inspect \
        --format '{{.Internal}}' \
        "$NETWORK"
)"

if [[ "$NETWORK_INTERNAL" != "true" ]]; then
    echo "ERROR: $NETWORK exists but is not an internal Docker network." >&2
    exit 1
fi

stage_document \
    "$ROOT/acceptance/roundtrip/InstitutionalReport.docx" \
    "$DOCUMENT_DIR/InstitutionalReport.docx"
stage_document \
    "$ROOT/acceptance/roundtrip/InstitutionalWorkbook.xlsx" \
    "$DOCUMENT_DIR/InstitutionalWorkbook.xlsx"
stage_document \
    "$ROOT/acceptance/roundtrip/InstitutionalPresentation.pptx" \
    "$DOCUMENT_DIR/InstitutionalPresentation.pptx"
stage_document \
    "$ROOT/acceptance/corpus/InstitutionalPacket.pdf" \
    "$DOCUMENT_DIR/InstitutionalPacket.pdf"

JWT_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(64))')"
GATEWAY_TOKEN="$(python3 -c 'import secrets; print(secrets.token_urlsafe(64))')"

printf '%s\n' "$JWT_SECRET" > "$JWT_SECRET_FILE"
printf '%s\n' "$GATEWAY_TOKEN" > "$GATEWAY_TOKEN_FILE"
printf 'header = "Authorization: Bearer %s"\n' \
    "$GATEWAY_TOKEN" > "$CURL_AUTH_FILE"
cat > "$DOCSERVER_ENV_FILE" <<EOF
JWT_ENABLED=true
JWT_SECRET=$JWT_SECRET
JWT_HEADER=Authorization
JWT_IN_BODY=false
ALLOW_PRIVATE_IP_ADDRESS=true
USE_UNAUTHORIZED_STORAGE=false
WOPI_ENABLED=false
PLUGINS_ENABLED=false
METRICS_ENABLED=false
EXAMPLE_ENABLED=false
EOF
chmod 600 \
    "$JWT_SECRET_FILE" \
    "$GATEWAY_TOKEN_FILE" \
    "$CURL_AUTH_FILE" \
    "$DOCSERVER_ENV_FILE"

"${DOCKER[@]}" run -d \
    --name "$CONTAINER" \
    --network "$NETWORK" \
    --publish 127.0.0.1:8088:80 \
    --pids-limit 512 \
    --restart no \
    --env-file "$DOCSERVER_ENV_FILE" \
    "$IMAGE" >/dev/null

rm -f "$DOCSERVER_ENV_FILE"

OO_IP="$(
    "${DOCKER[@]}" inspect \
        --format "{{with index .NetworkSettings.Networks \"$NETWORK\"}}{{.IPAddress}}{{end}}" \
        "$CONTAINER"
)"

GATEWAY_IP="$(
    "${DOCKER[@]}" network inspect \
        --format '{{(index .IPAM.Config 0).Gateway}}' \
        "$NETWORK"
)"

if [[ -z "$OO_IP" || -z "$GATEWAY_IP" ]]; then
    echo "ERROR: Docker did not assign the expected Office network addresses." >&2
    exit 1
fi

for _ in $(seq 1 90); do
    if curl -fsS \
        "http://127.0.0.1:8088/web-apps/apps/api/documents/api.js" \
        >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

curl -fsS \
    "http://127.0.0.1:8088/web-apps/apps/api/documents/api.js" \
    >/dev/null

export KHZ_ROOT="$ROOT"
export KHZ_OFFICE_DOCUMENT_DIR="$DOCUMENT_DIR"
export KHZ_GATEWAY_BIND="$GATEWAY_IP"
export KHZ_GATEWAY_PORT=8090
export KHZ_DOCS_BROWSER='http://localhost:8088'
export KHZ_DOCSERVER_IP="$OO_IP"
export KHZ_ONLYOFFICE_JWT_SECRET_FILE="$JWT_SECRET_FILE"
export KHZ_OFFICE_GATEWAY_TOKEN_FILE="$GATEWAY_TOKEN_FILE"

nohup python3 "$ROOT/tools/office-spike/gateway.py" \
    >"$RUNTIME_DIR/gateway.log" 2>&1 &

GATEWAY_PID=$!
printf '%s\n' "$GATEWAY_PID" > "$GATEWAY_PID_FILE"

for _ in $(seq 1 50); do
    if curl -fsS \
        --config "$CURL_AUTH_FILE" \
        "http://${GATEWAY_IP}:8090/health" \
        >/dev/null 2>&1; then
        break
    fi
    sleep 0.2
done

curl -fsS \
    --config "$CURL_AUTH_FILE" \
    "http://${GATEWAY_IP}:8090/health" \
    >/dev/null

nohup socat \
    TCP-LISTEN:8090,bind=127.0.0.1,reuseaddr,fork \
    TCP:"${GATEWAY_IP}":8090 \
    >"$RUNTIME_DIR/gateway-proxy.log" 2>&1 &

PROXY_PID=$!
printf '%s\n' "$PROXY_PID" > "$PROXY_PID_FILE"

sleep 0.5
kill -0 "$PROXY_PID"

curl -fsS \
    --config "$CURL_AUTH_FILE" \
    'http://127.0.0.1:8090/health' \
    >/dev/null

printf 'KHZ_OFFICE_GATEWAY_TOKEN=%s\n' "$GATEWAY_TOKEN" > "$SESSION_FILE"
chmod 600 "$SESSION_FILE"

unset JWT_SECRET
unset GATEWAY_TOKEN
unset KHZ_ROOT
unset KHZ_OFFICE_DOCUMENT_DIR
unset KHZ_GATEWAY_BIND
unset KHZ_GATEWAY_PORT
unset KHZ_DOCS_BROWSER
unset KHZ_DOCSERVER_IP
unset KHZ_ONLYOFFICE_JWT_SECRET_FILE
unset KHZ_OFFICE_GATEWAY_TOKEN_FILE
rm -f "$JWT_SECRET_FILE" "$GATEWAY_TOKEN_FILE" "$CURL_AUTH_FILE"
trap - ERR

echo "KHZ ONLYOFFICE SPIKE READY"
echo "AUTHENTICATION=JWT_AND_LOCAL_SESSION_REQUIRED"
echo "SESSION_FILE=$SESSION_FILE"
echo "DOCUMENTS=$DOCUMENT_DIR"
echo "START_WINDOWS_APP=tools/office-spike/start-windows-app.ps1"

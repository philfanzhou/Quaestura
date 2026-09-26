#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
# Set IMAGE_REPO=ghcr.io/philfanzhou/quaestura to run a published release instead of a local build.
IMAGE_REPO="${IMAGE_REPO:-quaestura}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
IMAGE_NAME="${IMAGE_REPO}:${IMAGE_TAG}"
CONTAINER_NAME="quaestura"
NETWORK_NAME="quaestura-net"
# HTTP listen port is hardcoded to 5007 inside the container (Program.cs).
# Port is the host port mapped to the container's 5007.
Port="5007"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-host.docker.internal:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

DB_NAME="quaestura"

docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --network "$NETWORK_NAME" \
  --add-host=host.docker.internal:host-gateway \
  -p "${Port}:5007" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e IdentityService__AppId="${IDENTITY_APP_ID:-}" \
  -e IdentityService__AppSecret="${IDENTITY_APP_SECRET:-}" \
  -e Database__Name="${DB_NAME}" \
  -e APP_TITLE="${CONTAINER_NAME}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"

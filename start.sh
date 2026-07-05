#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260501"
IMAGE_NAME="ruoyu.study.questionbank:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-questionbank"
NETWORK_NAME="ruoyu-net"

LISTEN_URL="http://+:5007"

DB_HOST="ruoyu-postgres"
DB_PORT="5432"
DB_NAME="ruoyu_study_questionbank"
DB_USER="postgres"
DB_PASS="postgres"

CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS};"

OSS_ENDPOINT="ruoyu-seaweedfs:8333"
OSS_ACCESS_KEY="seaweedfs_admin"
OSS_SECRET_KEY="seaweedfs_admin"
OSS_BUCKET="ruoyu-study"

IDENTITY_AUTHORITY="http://ruoyu-identity:5002"

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
  -e TZ=Asia/Shanghai \
  -e ASPNETCORE_URLS="${LISTEN_URL}" \
  -e ConnectionStrings__Default="${CONNECTION_STRING}" \
  -e Oss__Endpoint="${OSS_ENDPOINT}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET}" \
  -e IdentityService__Authority="${IDENTITY_AUTHORITY}" \
  -e APP_TITLE="${CONTAINER_NAME}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"

#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_DIR="$SCRIPT_DIR/../.."
DATE=$(date +%Y%m%d)
IMAGE_NAME="ruoyu.study.questionbank:${DATE}"

IMAGE_EXISTS=$(docker images -q "$IMAGE_NAME" 2>/dev/null)

if [ -n "$IMAGE_EXISTS" ]; then
    echo "Image $IMAGE_NAME already exists, attempting to remove..."
    if ! docker rmi "$IMAGE_NAME"; then
        echo "Error: Failed to remove existing image $IMAGE_NAME. It may be in use by a container."
        exit 1
    fi
    echo "Existing image removed."
fi

echo "Building image: $IMAGE_NAME"
docker build -f "$SCRIPT_DIR/Dockerfile" -t "$IMAGE_NAME" "$PROJECT_DIR"

echo "Image built: $IMAGE_NAME"

echo "Cleaning build cache..."
docker builder prune -af --force

echo "Done."

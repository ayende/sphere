#!/usr/bin/env bash
set -euo pipefail

MODEL_ID="sentence-transformers/facebook-dpr-question_encoder-single-nq-base"
CONTAINER_NAME="sphere-embedder"
PORT="8081"
IMAGE="ghcr.io/huggingface/text-embeddings-inference:cpu-latest"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required but was not found in PATH."
  echo "Install Docker and re-run this script."
  exit 1
fi

if ! docker info >/dev/null 2>&1; then
  echo "Cannot access Docker daemon (permission denied or daemon not running)."
  echo ""
  echo "Quick options:"
  echo "1) One-time with sudo:"
  echo "   sudo ./setup-embedder.sh"
  echo ""
  echo "2) Permanent (recommended): add your user to docker group, then re-login:"
  echo "   sudo usermod -aG docker \"$USER\""
  echo "   newgrp docker"
  echo ""
  echo "3) If daemon is stopped, start it:"
  echo "   sudo systemctl start docker"
  exit 1
fi

echo "Pulling ${IMAGE}..."
docker pull "${IMAGE}"

if docker ps -a --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
  echo "Removing existing container ${CONTAINER_NAME}..."
  docker rm -f "${CONTAINER_NAME}" >/dev/null
fi

echo "Starting ${CONTAINER_NAME} on port ${PORT} with model ${MODEL_ID}..."
docker run -d \
  --name "${CONTAINER_NAME}" \
  -p "${PORT}:80" \
  "${IMAGE}" \
  --model-id "${MODEL_ID}" >/dev/null

echo ""
echo "Embedder is starting. First startup can take a few minutes while model weights download."
echo "Health check: curl http://localhost:${PORT}/health"
echo ""
echo "Use these env vars before running SphereQueries:"
echo "export SPHERE_EMBEDDING_ENDPOINT=http://localhost:${PORT}/v1/embeddings"
echo "export SPHERE_EMBEDDING_MODEL=${MODEL_ID}"

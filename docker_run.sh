#!/bin/env bash

set -e

mkdir -p ./docker/data
mkdir -p ./docker/log
sudo chmod 777 -R ./docker ./cert

# Run gen_cert.sh to generate test certificates
docker run -it --rm \
  -p 8080:8080 \
  -v $(pwd)/docker/data:/data \
  -v $(pwd)/docker/log:/log \
  -v $(pwd)/docker_config.yml:/app/config.yml:ro \
  -v $(pwd)/cert:/cert:ro \
  --env TEST_USERNAME=admin \
  --env TEST_PASSWORD=admin \
  --name filesync filesync_service

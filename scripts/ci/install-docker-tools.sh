#!/bin/sh
set -eu

install -D .ci/bin/docker /usr/local/bin/docker
install -D .ci/docker/cli-plugins/docker-compose /usr/local/lib/docker/cli-plugins/docker-compose

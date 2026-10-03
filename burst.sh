#!/usr/bin/env bash
set -euo pipefail

if command -v dotnet >/dev/null 2>&1; then
  exec dotnet run -c Release --project tools/Burst -- "$@"
fi

exec docker run --rm \
  -v "$(pwd)":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run -c Release --project tools/Burst -- "$@"

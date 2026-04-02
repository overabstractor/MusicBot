#!/usr/bin/env bash
set -euo pipefail
dotnet run --project build/_build.csproj --no-launch-profile -- "$@"

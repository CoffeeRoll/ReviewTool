#!/usr/bin/env bash
# Builds the web UI, packs ReviewTool as a .NET tool and installs (or updates) it globally,
# so you can run `reviewtool` inside any git repository.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> Building the web UI"
(cd src/ReviewTool.Web && npm ci --no-audit --no-fund && npm run build)

echo "==> Packing the tool"
dotnet pack src/ReviewTool.Server -c Release -o artifacts/nupkg

echo "==> Installing"
dotnet tool uninstall --global ReviewTool >/dev/null 2>&1 || true
dotnet tool install --global ReviewTool --add-source artifacts/nupkg

echo
echo "Done. Run 'reviewtool' inside a git repository (or 'reviewtool --help')."
echo "If the command isn't found, add ~/.dotnet/tools to your PATH."

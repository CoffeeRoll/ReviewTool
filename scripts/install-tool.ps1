# Builds the web UI, packs ReviewTool as a .NET tool and installs (or updates) it globally,
# so you can run `reviewtool` inside any git repository.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '==> Building the web UI'
Push-Location src/ReviewTool.Web
npm ci --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm ci failed' }
npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' }
Pop-Location

Write-Host '==> Packing the tool'
dotnet pack src/ReviewTool.Server -c Release -o artifacts/nupkg; if ($LASTEXITCODE) { throw 'dotnet pack failed' }

Write-Host '==> Installing'
dotnet tool uninstall --global ReviewTool *> $null
dotnet tool install --global ReviewTool --add-source artifacts/nupkg; if ($LASTEXITCODE) { throw 'install failed' }

Write-Host "`nDone. Run 'reviewtool' inside a git repository (or 'reviewtool --help')."

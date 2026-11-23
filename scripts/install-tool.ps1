# Builds the web UI, packs ReviewTool as a .NET tool and installs (or updates) it globally,
# so you can run `reviewtool` inside any git repository.
# Works in Windows PowerShell 5.1 and PowerShell 7+.
$ErrorActionPreference = 'Stop'

# Runs a program and fails only on a non-zero exit code. npm and dotnet write warnings and
# "not found" messages to stderr, which Windows PowerShell 5.1 would otherwise turn into
# terminating errors under $ErrorActionPreference = 'Stop'.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
    Write-Host '==> Building the web UI'
    Push-Location src/ReviewTool.Web
    try {
        Invoke-Native 'npm ci' { npm ci --no-audit --no-fund }
        Invoke-Native 'npm run build' { npm run build }
    }
    finally { Pop-Location }

    Write-Host '==> Packing the tool'
    Invoke-Native 'dotnet pack' { dotnet pack src/ReviewTool.Server -c Release -o artifacts/nupkg }

    Write-Host '==> Installing'
    # Reinstall rather than update, so a rebuilt package with the same version is picked up.
    $installed = @(dotnet tool list --global) -match '^\s*reviewtool\s'
    if ($installed) {
        Invoke-Native 'dotnet tool uninstall' { dotnet tool uninstall --global ReviewTool }
    }
    # --source (not --add-source) installs from the local package only: no nuget.org lookup, so it works
    # offline or behind a firewall, and can't pick up an unrelated "ReviewTool" package from nuget.org.
    Invoke-Native 'dotnet tool install' { dotnet tool install --global ReviewTool --source artifacts/nupkg }

    Write-Host ''
    Write-Host "Done. Run 'reviewtool' inside a git repository (or 'reviewtool --help')."
    Write-Host 'If the command is not found, open a new terminal so PATH includes %USERPROFILE%\.dotnet\tools.'
}
finally {
    Pop-Location
}

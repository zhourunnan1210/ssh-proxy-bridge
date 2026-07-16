# Legacy compatibility entry point. New integrations should invoke
# ssh-proxy-bridge.ps1 directly.
& (Join-Path $PSScriptRoot 'ssh-proxy-bridge.ps1') @args
exit $LASTEXITCODE

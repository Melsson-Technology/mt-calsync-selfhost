# build-and-package.ps1 — publish the MT-CalSync engine (worker + self-host portal)
# for linux-x64 and bundle the schema + deploy assets into a tarball.
#
# Run from the engine/ directory:  ./scripts/build-and-package.ps1
# Produces: build/mtcalsync-engine.tar.gz  (copy it to your server, then run
#           deploy/deploy-on-server.sh there).

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $PSScriptRoot          # engine/
$build  = Join-Path $root "build"
$worker = Join-Path $build "worker-publish"
$portal = Join-Path $build "selfhost-publish"

if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force -Path $build | Out-Null

Write-Host "==> publish worker (linux-x64, framework-dependent)"
dotnet publish (Join-Path $root "Worker.MT-CalSync") -c Release -r linux-x64 --self-contained false -o $worker

Write-Host "==> publish self-host portal (linux-x64, framework-dependent)"
dotnet publish (Join-Path $root "SelfHost.MT-CalSync") -c Release -r linux-x64 --self-contained false -o $portal

Write-Host "==> bundle schema + deploy + scripts"
Copy-Item -Recurse (Join-Path $root "Core.MT-CalSync\Sql") (Join-Path $worker "sql")
Copy-Item -Recurse (Join-Path $root "deploy")  (Join-Path $build "deploy")
Copy-Item -Recurse (Join-Path $root "scripts") (Join-Path $build "scripts")

# Never ship a real settings.xml (it holds the DB connection + encryption key).
Get-ChildItem $build -Recurse -Filter settings.xml | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "==> tar"
tar -czf (Join-Path $build "mtcalsync-engine.tar.gz") -C $build worker-publish selfhost-publish deploy scripts
Write-Host "Done: $(Join-Path $build 'mtcalsync-engine.tar.gz')"

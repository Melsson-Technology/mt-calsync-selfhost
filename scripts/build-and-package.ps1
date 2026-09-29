# build-and-package.ps1: publish the MT-CalSync engine (worker + self-host portal)
# for linux-x64 and bundle the schema + deploy assets into a tarball.
#
# Run from the repository root:  ./scripts/build-and-package.ps1 [-SelfContained]
# Produces: build/mtcalsync-engine.tar.gz  (copy it to your server, then run
#           deploy/deploy-on-server.sh there). scripts/build-and-package.sh does the same
#           from bash.
#
# Framework-dependent by default: the server installs the ASP.NET Core 10 runtime once, and
# its security patches then arrive through the package manager. -SelfContained bundles the
# runtime instead (a larger tarball, no runtime on the server), for a host where installing
# one is undesirable; its runtime patches arrive only when you rebuild and redeploy.

param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $PSScriptRoot          # the repository root
$build  = Join-Path $root "build"
$worker = Join-Path $build "worker-publish"
$portal = Join-Path $build "selfhost-publish"
$sc     = if ($SelfContained) { "true" } else { "false" }
$kind   = if ($SelfContained) { "self-contained" } else { "framework-dependent" }

if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force -Path $build | Out-Null

Write-Host "==> publish worker (linux-x64, $kind)"
dotnet publish (Join-Path $root "Worker.MT-CalSync") -c Release -r linux-x64 --self-contained $sc -o $worker
if ($LASTEXITCODE -ne 0) { throw "worker publish failed" }

Write-Host "==> publish self-host portal (linux-x64, $kind)"
dotnet publish (Join-Path $root "SelfHost.MT-CalSync") -c Release -r linux-x64 --self-contained $sc -o $portal
if ($LASTEXITCODE -ne 0) { throw "portal publish failed" }

Write-Host "==> bundle schema + deploy + scripts"
Copy-Item -Recurse (Join-Path $root "Core.MT-CalSync\Sql") (Join-Path $worker "sql")
Copy-Item -Recurse (Join-Path $root "deploy")  (Join-Path $build "deploy")
Copy-Item -Recurse (Join-Path $root "scripts") (Join-Path $build "scripts")

# Never ship a real settings.xml (it holds the DB connection + encryption key).
Get-ChildItem $build -Recurse -Filter settings.xml | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "==> tar"
tar -czf (Join-Path $build "mtcalsync-engine.tar.gz") -C $build worker-publish selfhost-publish deploy scripts
Write-Host "Done: $(Join-Path $build 'mtcalsync-engine.tar.gz') ($kind)"

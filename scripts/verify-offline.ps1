param([Parameter(Mandatory)][string]$Python, [string]$Results = 'validation/offline-v0.1/final-tests')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    if (!(Test-Path -LiteralPath $Python -PathType Leaf)) { throw 'An explicit working Python interpreter is required for the pinned HGLOS validator.' }
    & $Python --version
    if ($LASTEXITCODE -ne 0) { throw 'Python probe failed' }
    $env:REVIEWGATE_TEST_PYTHON = [IO.Path]::GetFullPath($Python)
    $env:DOTNET_CLI_HOME = Join-Path $repo '.work/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $manifest = Get-Content -LiteralPath tests/test-manifest.json -Raw | ConvertFrom-Json
    $actual = @(Get-ChildItem tests -Recurse -Filter '*.csproj' | ForEach-Object { $_.BaseName } | Sort-Object)
    if (Compare-Object $actual @($manifest.suites | Sort-Object)) { throw 'Missing or unexpected test suite' }
    # One results folder holds exactly one run; mixing TRX from separate runs is refused.
    if ((Test-Path -LiteralPath $Results) -and @(Get-ChildItem -LiteralPath $Results -Force).Count -gt 0) { throw "Results folder must be new or empty: $Results" }
    New-Item -ItemType Directory -Force -Path $Results | Out-Null
    $head = (& git rev-parse HEAD).Trim()
    $dirty = @(& git status --porcelain=v1 --untracked-files=all) | Where-Object { $_ -and ($_ -notmatch [regex]::Escape($Results.Replace('\','/'))) }
    $sdk = (& dotnet --version).Trim()
    $started = [DateTimeOffset]::Now.ToString('o')
    # File identity: git blob id (line-ending normalized as Git stores it) plus the exact worktree bytes that were built.
    $paths = @(& git ls-files --cached --others --exclude-standard -- src tests schemas profiles scripts ReviewGate.sln global.json Directory.Build.props Directory.Build.targets NuGet.config) | Sort-Object -Unique
    $files = foreach ($path in $paths) { if (Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{path=$path;bytes=(Get-Item -LiteralPath $path).Length;worktree_sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();git_blob_sha1=(& git hash-object -- $path).Trim()} } }
    [ordered]@{schema_version='1.0';scope='local_integrity_reference_not_independent_trust_anchor';git_head=$head;worktree_dirty=($dirty.Count -gt 0);files=@($files)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Results 'file-manifest.json') -Encoding utf8NoBOM
    & dotnet build ReviewGate.sln --no-restore -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $rows = @()
    foreach ($suite in $manifest.suites) {
        & dotnet test "tests/$suite/$suite.csproj" --no-restore --no-build --logger "trx;LogFileName=$suite.trx" --results-directory $Results -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Test failure: $suite" }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $Results "$suite.trx") -Raw
        $c = $trx.TestRun.ResultSummary.Counters
        if ([int]$c.total -le 0 -or [int]$c.passed -ne [int]$c.total -or [int]$c.executed -ne [int]$c.total) { throw "Empty, skipped or incomplete suite: $suite" }
        $rows += [ordered]@{suite=$suite;total=[int]$c.total;passed=[int]$c.passed;failed=[int]$c.failed;skipped=([int]$c.total-[int]$c.executed)}
    }
    # Network/credential absence is a structural claim backed by a test, not a measured call count.
    [ordered]@{schema_version='1.0';execution_mode='synthetic';git_head=$head;worktree_dirty=($dirty.Count -gt 0);dotnet_sdk=$sdk;started_at=$started;finished_at=[DateTimeOffset]::Now.ToString('o');suites=$rows;total=[int]($rows.total | Measure-Object -Sum).Sum;network_and_credential_evidence='structural: OfflineE2E.ProductSourcesContainNoNetworkOrCredentialImplementation; not an OS egress measurement'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Results 'summary.json') -Encoding utf8NoBOM
    & dotnet publish src/ReviewGate.Cli/ReviewGate.Cli.csproj -c Release --no-restore -o .work/publish -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $temporary = Join-Path $repo ('.work/publish-smoke-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    Copy-Item -Path '.work/publish/*' -Destination $temporary -Recurse
    $dll = Join-Path $temporary 'ReviewGate.Cli.dll'
    $version = & dotnet $dll --version
    if ($LASTEXITCODE -ne 0 -or $version -ne 'ReviewGate 0.1.0-offline') { throw 'Packaged version smoke failed' }
    $live = (& dotnet $dll plan --execution-mode live) -join "`n"
    if ($LASTEXITCODE -ne 14 -or ($live | ConvertFrom-Json).error_code -ne 'live_not_available') { throw 'Packaged live guard failed' }
    foreach($assemblyPath in Get-ChildItem $temporary -Filter 'ReviewGate.*.dll') { [void][Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($assemblyPath.FullName) }
    foreach($name in @('ReviewGate.Core.Execution.ReviewEngine','ReviewGate.Infrastructure.StateStore')) {
        $type = [AppDomain]::CurrentDomain.GetAssemblies().GetTypes() | Where-Object { $_.FullName -eq $name }
        if (!$type -or $type.GetProperty('FaultObserver')) { throw 'Release fault seam exclusion failed' }
    }
    & ./scripts/offline-demo.ps1 -CliDll $dll | Set-Content -LiteralPath (Join-Path $Results 'publish-demo.json') -Encoding utf8NoBOM
    [ordered]@{git_head=$head;version=$version;live_exit=14;release_fault_seams='absent_by_reflection';temporary_publish_directory=$temporary;framework_dependent=$true;network_and_credential_evidence='not_measured_here_see_summary'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Results 'publish-smoke.json') -Encoding utf8NoBOM
}
finally { Pop-Location }

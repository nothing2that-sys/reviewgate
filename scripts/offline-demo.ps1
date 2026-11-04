param([string]$CliDll = (Join-Path $PSScriptRoot '../.work/publish/ReviewGate.Cli.dll'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$state = Join-Path $repo '.work/demo-state'
if (!(Test-Path -LiteralPath $CliDll)) { throw 'Publish first: dotnet publish src/ReviewGate.Cli -c Release --no-restore -o .work/publish' }
function Invoke-ReviewGate([string[]]$CliArguments) {
    $json = & dotnet $CliDll @CliArguments
    if ($LASTEXITCODE -ne 0) { throw ($json -join "`n") }
    return (($json -join "`n") | ConvertFrom-Json)
}
# This creates a SIMULATION grant only for bundled synthetic data. No credential or model API is used.
$planned = Invoke-ReviewGate @('plan', '고정 fixture로 오프라인 흐름 검증', '--config', (Join-Path $repo 'tests/fixtures/demo/config.json'))
$preview = $planned.artifacts | Where-Object { $_.path -like '*/plan.json' }
$plan = Get-Content -LiteralPath (Join-Path $state "synthetic/$($preview.path)") -Raw | ConvertFrom-Json
$grant = [ordered]@{schema_version='1.0';id=('demo_'+[guid]::NewGuid().ToString('N'));plan_hash=$plan.hash;manifest_hash=$plan.manifest_hash;expires_at=[DateTimeOffset]::UtcNow.AddHours(1).ToString('o');max_usd=1;max_requests=100;max_retries=0;execution_mode='synthetic'}
$grantFile = Join-Path $repo ('.work/demo-grant-'+[guid]::NewGuid().ToString('N')+'.json')
$grant | ConvertTo-Json | Set-Content -LiteralPath $grantFile -Encoding utf8NoBOM
$result = Invoke-ReviewGate @('exec','--state-root',$state,'--plan',$planned.plan_id,'--grant-file',$grantFile)
$export = Invoke-ReviewGate @('export','--state-root',$state,'--run',$result.run_id,'--format','json')
[ordered]@{execution_mode='synthetic';run_id=$result.run_id;status=$result.status;artifacts=$export.artifacts;state_root=$state} | ConvertTo-Json -Depth 10

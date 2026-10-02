param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$serverDll = Join-Path $ProjectRoot 'src\SourceTrail.Mcp\bin\Debug\net10.0\SourceTrail.Mcp.dll'
$solutionPath = Join-Path $ProjectRoot 'tests\Fixtures\Demo\Demo.sln'
if (-not (Test-Path -LiteralPath $serverDll)) { throw 'Build SourceTrail.sln before running this check.' }
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = 'dotnet'
$startInfo.Arguments = '"' + $serverDll + '"'
$startInfo.WorkingDirectory = $ProjectRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.EnvironmentVariables['Analysis__SolutionPath'] = ''
$startInfo.EnvironmentVariables['Database__ConnectionString'] = ''
# This is a test-only adapter contract. Production rules are configured externally.
$startInfo.EnvironmentVariables['Analysis__ProcedureCallRules__0__TypeName'] = 'Demo.Data.Db'
$startInfo.EnvironmentVariables['Analysis__ProcedureCallRules__0__MethodName'] = 'ExecuteProcedure'
$startInfo.EnvironmentVariables['Analysis__ProcedureCallRules__0__ArgumentIndex'] = '0'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
$startInfo.EnvironmentVariables['Database__Mode'] = 'LiveDatabase'
$startInfo.EnvironmentVariables['Database__SqlFolder'] = ''
$sqlFixture = Join-Path ([IO.Path]::GetTempPath()) ('SourceTrail-mcp-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($sqlFixture)
$startInfo.EnvironmentVariables['Database__CacheDirectory'] = Join-Path $sqlFixture 'cache'
[IO.File]::WriteAllText((Join-Path $sqlFixture 'objects.sql'), "CREATE TABLE dbo.Reception(Id int);`nGO`nCREATE PROCEDURE dbo.usp_TestSave AS EXEC dbo.usp_Inner;`nGO`nCREATE PROCEDURE dbo.usp_Inner AS INSERT dbo.Reception VALUES(1);")
$requestId = 0
try {
    [void]$process.Start()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    function Invoke-Request([string]$method, $arguments) {
        $script:requestId++
        $request = @{ jsonrpc = '2.0'; id = $script:requestId; method = $method; params = $arguments }
        $process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 30 -Compress))
        $process.StandardInput.Flush()
        while ($true) {
            $readTask = $process.StandardOutput.ReadLineAsync()
            if (-not $readTask.Wait(45000)) { throw "$method response timeout" }
            if ($null -eq $readTask.Result) { throw 'Server closed stdout.' }
            $response = $readTask.Result | ConvertFrom-Json
            if ($response.id -ne $script:requestId) { continue }
            if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
            return $response.result
        }
    }
    function Invoke-Tool([string]$name, $arguments) {
        $result = Invoke-Request 'tools/call' @{ name = $name; arguments = $arguments }
        if ($result.isError) { throw ($result.content | ConvertTo-Json -Depth 10 -Compress) }
        if ($null -ne $result.structuredContent) { return $result.structuredContent }
        $text = ($result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text
        try { return $text | ConvertFrom-Json } catch { return $text }
    }
    $null = Invoke-Request 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'SourceTrailSmokeCheck'; version = '1.0' }
    }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $tools = Invoke-Request 'tools/list' @{}
    $expected = @('ping','load_solution','get_analysis_status','reload_solution','find_symbol',
        'get_symbol_overview','find_references','search_text','find_procedure_usage',
        'analyze_procedure','trace_code_to_database','trace_procedure_to_ui',
        'select_database_source','get_database_status','refresh_database_source','find_sql_object')
    foreach ($name in $expected) {
        if (-not ($tools.tools | Where-Object { $_.name -eq $name })) { throw "Tool missing: $name" }
    }
    if ((Invoke-Tool 'ping' @{}) -ne 'pong') { throw 'ping failed' }
    $initial = Invoke-Tool 'get_analysis_status' @{}
    if ($initial.status -ne 'NotLoaded') { throw 'Unexpected startup status' }
    $loaded = Invoke-Tool 'load_solution' @{ solutionPath = $solutionPath }
    if ($loaded.status -ne 'Ready' -or $loaded.projects.Count -ne 2) { throw 'Solution load failed' }
    $symbols = Invoke-Tool 'find_symbol' @{ query = 'SaveClicked' }
    $symbol = @($symbols.items | Where-Object { $_.symbolKind -eq 'Method' })[0]
    if ($null -eq $symbol.id) { throw 'Symbol not found' }
    $types = Invoke-Tool 'find_symbol' @{ query = 'Demo.UI.TestForm' }
    $type = @($types.items | Where-Object { $_.symbolKind -eq 'NamedType' })[0]
    $overview = Invoke-Tool 'get_symbol_overview' @{ symbol = $type.id }
    if ($overview.constructors.Count -ne 1) { throw 'Overview failed' }
    $references = Invoke-Tool 'find_references' @{ symbol = $symbol.id }
    if ($references.total -lt 1) { throw 'References missing' }
    $text = Invoke-Tool 'search_text' @{ text = 'usp_CommentOnly' }
    if ($text.total -lt 1) { throw 'Text search failed' }
    $usages = Invoke-Tool 'find_procedure_usage' @{ procedure = 'dbo.usp_TestSave' }
    if (-not ($usages.items | Where-Object { $_.isVerifiedCall })) { throw 'Verified usage missing' }
    $forward = Invoke-Tool 'trace_code_to_database' @{ symbol = $symbol.id; maxDepth = 8 }
    if (-not ($forward.nodes | Where-Object { $_.kind -eq 'Procedure' })) { throw 'Forward flow failed' }
    $reverse = Invoke-Tool 'trace_procedure_to_ui' @{ procedure = 'dbo.usp_TestSave'; maxDepth = 8 }
    if (-not ($reverse.edges | Where-Object { $_.relationType -eq 'EventSubscription' })) { throw 'Reverse flow failed' }
    $database = Invoke-Tool 'analyze_procedure' @{ procedure = 'dbo.usp_TestSave' }
    if ($database.status -ne 'NotConfigured') { throw 'Unexpected DB state' }
    $reloaded = Invoke-Tool 'reload_solution' @{}
    if ($loaded.snapshotId -eq $reloaded.snapshotId) { throw 'Reload did not replace snapshot' }
    Write-Output ('PASS: {0} tools; load/search/overview/references/procedure usage/forward/reverse/reload verified over MCP stdio.' -f $expected.Count)
} finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
    $process.Dispose()
    $resolvedFixture = [IO.Path]::GetFullPath($sqlFixture)
    if ($resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedFixture -Leaf).StartsWith('SourceTrail-mcp-'))
        { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}

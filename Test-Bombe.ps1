<#
.SYNOPSIS
    Smoke-tests the Bombe console program by running the README commands in order.

.DESCRIPTION
    1. dotnet build -c Release
    2. help
    3. selftest
    4. bench
    5. encrypt  (README worked example, output compared to the README ciphertext)
    6. encrypt  (round trip: encrypting the ciphertext again gives the plaintext back)
       + pipes, --in/--out files, decrypt alias and --config JSON key file (example-key.json)
    7. run      (worked example narrowed to rotor order 2,4,1 and crib index 63 - fast)
    8. run      (full README search, all orders / alignments - takes minutes; only with -Full)
    9. error handling (unknown command, impossible alignment, wrong crib, missing input, bad config)

.PARAMETER Full
    Also run the full, unrestricted README search (step 8). Takes a few minutes.

.PARAMETER SkipBench
    Skip the benchmark step.

.EXAMPLE
    .\Test-Bombe.ps1
    .\Test-Bombe.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$SkipBench
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\bombe.exe'

# README worked example
$rotors    = '2,4,1'
$rings     = 'BCD'
$pos       = 'QMX'
$plugs     = 'AB CD EF GH IJ KL MN OP QR ST'
$plain     = 'HIERFUNKSTELLEPAULAEMILRICHARDXMELDEGEHORSAMSTXANXOBERKOMMANDOXKEINEBESONDERENEREIGNISSEXWETTERLAGEUNVERAENDERT'
$expCipher = 'FDGABFZOYYSCVJWCAJXDSWDPFBOHGLBUCMIMBFDQULDHJIQORRFFDSNWZZLDOIIXMDGQNHLTPUWUADCCOHSIJFRMGIBWLTYUVWMHDKDWZVPJLFJ'
$crib      = 'KEINEBESONDERENEREIGNISSE'
$expStop   = 'rotors II-IV-I (left-middle-right), core positions at crib start PMG, crib at index 63'

$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Bombe {
    param([string[]]$Arguments, [string[]]$Stdin)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = if ($null -ne $Stdin) { $Stdin | & $exe @Arguments 2>&1 | ForEach-Object { "$_" } }
           else                  { & $exe @Arguments 2>&1 | ForEach-Object { "$_" } }
    $code = $LASTEXITCODE
    $sw.Stop()
    [pscustomobject]@{ Output = ($out -join "`n"); ExitCode = $code; Seconds = $sw.Elapsed.TotalSeconds }
}

function Test-Step {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [int]$ExpectExit = 0,
        [scriptblock]$Check = { $true },  # receives the output text as $args[0]
        [string[]]$Stdin                  # piped into the program when given
    )
    Write-Host ""
    Write-Host ("=" * 100) -ForegroundColor DarkGray
    Write-Host "STEP: $Name" -ForegroundColor Cyan
    $pipe = if ($null -ne $Stdin) { "<$($Stdin.Count) line(s)> | " } else { '' }
    Write-Host "  > $($pipe)bombe $($Arguments -join ' ')" -ForegroundColor DarkGray
    $r = Invoke-Bombe -Arguments $Arguments -Stdin $Stdin
    Write-Host $r.Output

    $problems = @()
    if ($r.ExitCode -ne $ExpectExit) { $problems += "exit code $($r.ExitCode), expected $ExpectExit" }
    $checkResult = & $Check $r.Output
    if ($checkResult -is [string]) { $problems += $checkResult }
    elseif (-not $checkResult)     { $problems += 'output check failed' }

    $ok = $problems.Count -eq 0
    $msg = if ($ok) { 'PASS' } else { 'FAIL: ' + ($problems -join '; ') }
    Write-Host ("  -> {0}  ({1:F2}s)" -f $msg, $r.Seconds) -ForegroundColor ($(if ($ok) { 'Green' } else { 'Red' }))
    $results.Add([pscustomobject]@{ Step = $Name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [math]::Round($r.Seconds, 2); Detail = ($problems -join '; ') })
    return $r
}

# ------------------------------------------------------------------ 1. build
Write-Host "STEP: dotnet build -c Release" -ForegroundColor Cyan
dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host "Build failed - stopping." -ForegroundColor Red
    exit 1
}
$results.Add([pscustomobject]@{ Step = 'build'; Result = 'PASS'; Seconds = $null; Detail = '' })

# ------------------------------------------------------------------ 2. help
Test-Step -Name 'help' -Arguments @('help') -Check {
    param($o) ($o -match 'COMMANDS') -and ($o -match 'selftest') -and ($o -match 'encrypt')
} | Out-Null

# ------------------------------------------------------------------ 3. selftest
Test-Step -Name 'selftest' -Arguments @('selftest') -Check {
    param($o)
    if ($o -match '(\d+)/(\d+) trials recovered the true key') {
        if ($Matches[1] -eq $Matches[2]) { $true } else { "only $($Matches[1])/$($Matches[2]) trials recovered the key" }
    } else { 'summary line missing' }
} | Out-Null

# ------------------------------------------------------------------ 4. bench
if (-not $SkipBench) {
    Test-Step -Name 'bench' -Arguments @('bench') -Check {
        param($o) ($o -match '1 rotor order, 1 thread') -and ($o -match '60 rotor orders')
    } | Out-Null
}

# ------------------------------------------------------------------ 5. encrypt (README example)
$enc = Test-Step -Name 'encrypt (README example)' -Arguments @(
    'encrypt', '--rotors', $rotors, '--reflector', 'B', '--rings', $rings, '--pos', $pos,
    '--plugs', $plugs, '--text', $plain) -Check {
    param($o)
    if ($o.Trim() -eq $expCipher) { $true } else { "ciphertext differs from README" }
}
$cipher = $enc.Output.Trim()

# ------------------------------------------------------------------ 6. encrypt round trip
Test-Step -Name 'encrypt (round trip back to plaintext)' -Arguments @(
    'encrypt', '--rotors', $rotors, '--reflector', 'B', '--rings', $rings, '--pos', $pos,
    '--plugs', $plugs, '--text', $cipher) -Check {
    param($o)
    if ($o.Trim() -eq $plain) { $true } else { 'decrypt does not give the original plaintext' }
} | Out-Null

# ------------------------------------------------------------------ 6b. pipes, files, decrypt, --config
$keyFile = Join-Path $PSScriptRoot 'example-key.json'
$work = Join-Path ([IO.Path]::GetTempPath()) ("bombe-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$plainFile  = Join-Path $work 'plain.txt'
$cipherFile = Join-Path $work 'cipher.txt'

# Readable, multi-line message: spaces, punctuation, case and line breaks must be dropped
$readable = @(
    'Hier Funkstelle Paula-Emil-Richard x melde gehorsamst x an x Oberkommando x',
    'keine besonderen Ereignisse x Wetterlage unveraendert.'
)

Test-Step -Name 'pipe: readable text | encrypt --config' -Arguments @('encrypt', '--config', $keyFile) -Stdin $readable -Check {
    param($o) if ($o.Trim() -eq $expCipher) { $true } else { 'ciphertext differs from README' }
} | Out-Null

Test-Step -Name 'pipe: cipher | decrypt --config' -Arguments @('decrypt', '--config', $keyFile) -Stdin $cipher -Check {
    param($o) if ($o.Trim() -eq $plain) { $true } else { 'does not give the original plaintext' }
} | Out-Null

Set-Content -LiteralPath $plainFile -Value $readable
Test-Step -Name 'file: encrypt --in plain.txt --out cipher.txt' -Arguments @(
    'encrypt', '--config', $keyFile, '--in', $plainFile, '--out', $cipherFile) -Check {
    param($o)
    if ($o.Trim() -ne '') { return 'nothing should be written to stdout with --out' }
    if (-not (Test-Path $cipherFile)) { return 'output file not created' }
    if ((Get-Content -Raw $cipherFile).Trim() -ne $expCipher) { return 'output file differs from README ciphertext' }
    $true
} | Out-Null

Test-Step -Name 'file: decrypt --in cipher.txt' -Arguments @('decrypt', '--config', $keyFile, '--in', $cipherFile) -Check {
    param($o) if ($o.Trim() -eq $plain) { $true } else { 'does not give the original plaintext' }
} | Out-Null

# Command line must override the key file: --config + --pos AAA == all settings typed with --pos AAA
$expAAA = (Invoke-Bombe -Arguments @('encrypt', '--rotors', $rotors, '--reflector', 'B', '--rings', $rings, '--pos', 'AAA',
    '--plugs', $plugs, '--text', 'WETTERBERICHT')).Output.Trim()
Test-Step -Name 'config: command line overrides key file (--pos AAA)' -Arguments @(
    'encrypt', '--config', $keyFile, '--pos', 'AAA', '--text', 'WETTERBERICHT') -Check {
    param($o) if ($o.Trim() -eq $expAAA -and $expAAA -match '^[A-Z]{13}$') { $true } else { "got '$($o.Trim())', expected '$expAAA'" }
} | Out-Null

$runKey = Join-Path $work 'run.json'
Set-Content -LiteralPath $runKey -Value (@{ crib = $crib; order = @(2, 4, 1); align = 63; 'scan-middle-step' = $true } | ConvertTo-Json)
Test-Step -Name 'pipe + config: cipher | run --config run.json' -Arguments @('run', '--config', $runKey) -Stdin $cipher -Check {
    param($o) if ($o -match [regex]::Escape($expStop)) { $true } else { 'expected stop (II-IV-I / PMG / index 63) not found' }
} | Out-Null

# ------------------------------------------------------------------ 7. run (narrowed)
Test-Step -Name 'run (order 2,4,1, align 63, scan middle step)' -Arguments @(
    'run', '--cipher', $cipher, '--crib', $crib, '--order', $rotors, '--align', '63', '--scan-middle-step') -Check {
    param($o)
    if ($o -notmatch [regex]::Escape($expStop)) { return 'expected stop (II-IV-I / PMG / index 63) not found' }
    if ($o -notmatch [regex]::Escape('plugboard deduced (10 pair(s)): AB CD EF GH IJ KL MN OP QR ST')) { return 'deduced plugboard differs from README' }
    # Preview must show the crib, all letters determined
    if ($o -cnotmatch [regex]::Escape("[$crib]")) { return 'preview does not show the crib' }
    $true
} | Out-Null

# ------------------------------------------------------------------ 8. run (full README search)
if ($Full) {
    Test-Step -Name 'run (full README search: all orders, all alignments)' -Arguments @(
        'run', '--cipher', $cipher, '--crib', $crib, '--scan-middle-step') -Check {
        param($o)
        if ($o -match [regex]::Escape($expStop)) { $true } else { 'expected stop (II-IV-I / PMG / index 63) not found' }
    } | Out-Null
}

# ------------------------------------------------------------------ 9. error handling
Test-Step -Name 'error: unknown command' -Arguments @('frobnicate') -ExpectExit 2 -Check {
    param($o) $o -match 'Unknown command'
} | Out-Null

# Find an alignment where a crib letter equals the cipher letter above it (Enigma can't do that)
$badAlign = (0..($cipher.Length - $crib.Length)) | Where-Object {
    $a = $_; (0..($crib.Length - 1)) | Where-Object { $crib[$_] -eq $cipher[$a + $_] } | Select-Object -First 1
} | Select-Object -First 1
if ($null -ne $badAlign) {
    Test-Step -Name "error: impossible alignment ($badAlign)" -Arguments @(
        'run', '--cipher', $cipher, '--crib', $crib, '--align', "$badAlign") -ExpectExit 2 -Check {
        param($o) $o -match 'alignment is impossible'
    } | Out-Null
}

Test-Step -Name 'error: crib longer than 26 letters' -Arguments @(
    'run', '--cipher', $cipher, '--crib', 'ABCDEFGHIJKLMNOPQRSTUVWXYZA') -ExpectExit 2 -Check {
    param($o) $o -match 'Crib must be'
} | Out-Null

Test-Step -Name 'error: encrypt without any input' -Arguments @('encrypt', '--config', $keyFile) -Stdin @('') -ExpectExit 2 -Check {
    param($o) $o -match 'Missing --text|No letters'
} | Out-Null

Test-Step -Name 'error: config file not found' -Arguments @('encrypt', '--config', (Join-Path $work 'nope.json'), '--text', 'ABC') -ExpectExit 2 -Check {
    param($o) $o -match 'not found'
} | Out-Null

$badJson = Join-Path $work 'bad.json'
Set-Content -LiteralPath $badJson -Value '{ "rotors": [2, 4, 1 '
Test-Step -Name 'error: config file with invalid JSON' -Arguments @('encrypt', '--config', $badJson, '--text', 'ABC') -ExpectExit 2 -Check {
    param($o) $o -match 'not valid JSON'
} | Out-Null

# README limitation 1: one wrong crib letter means no stop (menu is still strong, so it is really searched)
Test-Step -Name 'one wrong crib letter gives no stops' -Arguments @(
    'run', '--cipher', $cipher, '--crib', 'KEINEBESONDERXNEREIGNISSE', '--order', $rotors, '--align', '63', '--scan-middle-step') -ExpectExit 1 -Check {
    param($o)
    if ($o -match 'Skipped') { return 'alignment was skipped instead of searched' }
    $o -match 'No stops'
} | Out-Null

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ summary
Write-Host ""
Write-Host ("=" * 100) -ForegroundColor DarkGray
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object Result -ne 'PASS').Count
if ($failed -eq 0) {
    Write-Host "All $($results.Count) steps passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed of $($results.Count) steps FAILED." -ForegroundColor Red
    exit 1
}

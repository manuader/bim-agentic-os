#Requires -Version 7.2
# Prueba del puente de Modelado 3D con un Claude falso: crear encargo, subir, ficha, aprobar, descargar, rechazos.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path ([IO.Path]::GetTempPath()) ("puente-" + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $work 'repo'; $enc = Join-Path $work 'encargos'
New-Item -ItemType Directory -Force -Path $repo, $enc | Out-Null
$fake = Join-Path $work 'claude-falso.ps1'
@'
$dir = $null; $prompt = $null
for ($i = 0; $i -lt $args.Count; $i++) { if ($args[$i] -eq '--add-dir') { $dir = $args[$i + 1] } }
$prompt = [Console]::In.ReadToEnd()
if ($prompt -like '*No toques Revit*') { Set-Content -LiteralPath (Join-Path $dir 'ficha.md') -Value '# Ficha de prueba' }
else { Set-Content -LiteralPath (Join-Path $dir 'salida/vista.png') -Value 'png' }
$resume = if ($args -contains '--resume') { 'si' } else { 'no' }
@{ session_id = 'sesion-1'; result = "ok resume=$resume"; is_error = $false } | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $fake

$port = Get-Random -Minimum 48000 -Maximum 49000
$token = 'token-de-prueba'
$pwsh = (Get-Process -Id $PID).Path
$server = Start-Process -FilePath $pwsh -PassThru -ArgumentList @('-NoProfile', '-File', "`"$root/scripts/puente-modelado.ps1`"",
    '-Root', "`"$enc`"", '-RepoRoot', "`"$repo`"", '-Port', $port, '-Token', $token, '-ClaudeCommand', "`"$fake`"",
    '-Origins', 'https://ejemplo.test')
$base = "http://127.0.0.1:$port"
$h = @{ 'X-Puente-Token' = $token }
$fail = 0
function Check([bool]$cond, [string]$what) { if ($cond) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:fail++ } }
function Code([scriptblock]$call) { try { & $call | Out-Null; return 200 } catch { return [int]$_.Exception.Response.StatusCode } }
function WaitFor([string]$id, [string]$state) {
    for ($i = 0; $i -lt 60; $i++) { $e = Invoke-RestMethod "$base/encargos/$id" -Headers $h; if ($e.estado -eq $state) { return $e }; Start-Sleep -Milliseconds 500 }
    return $e
}
try {
    for ($i = 0; $i -lt 40; $i++) { try { Invoke-RestMethod "$base/salud" -Headers $h | Out-Null; break } catch { Start-Sleep -Milliseconds 250 } }
    Check ((Invoke-RestMethod "$base/salud" -Headers $h).ok) 'salud'
    Check ((Code { Invoke-RestMethod "$base/salud" }) -eq 401) 'sin token: 401'
    Check ((Code { Invoke-RestMethod "$base/salud" -Headers (@{ 'X-Puente-Token' = $token; Origin = 'https://malo.test' }) }) -eq 403) 'origen ajeno: 403'
    $pre = Invoke-WebRequest "$base/salud" -Method Options -Headers @{ Origin = 'https://ejemplo.test' }
    Check ($pre.StatusCode -eq 204 -and "$($pre.Headers['Access-Control-Allow-Private-Network'])" -eq 'true') 'preflight CORS'

    $e = Invoke-RestMethod "$base/encargos" -Method Post -Headers $h -ContentType 'application/json' -Body (@{ nombre = 'Ascensor ENSA cilíndrico'; pedido = 'tres paradas' } | ConvertTo-Json)
    Check ($e.id -eq 'ascensor-ensa-cilindrico' -and $e.estado -eq 'nuevo') 'crear encargo con id limpio'
    $id = $e.id
    $e2 = Invoke-RestMethod "$base/encargos" -Method Post -Headers $h -ContentType 'application/json' -Body (@{ nombre = 'Ascensor ENSA cilíndrico' } | ConvertTo-Json)
    Check ($e2.id -eq 'ascensor-ensa-cilindrico-2') 'nombre repetido: id nuevo'

    $up = Invoke-RestMethod "$base/encargos/$id/entrada/ficha.pdf" -Method Put -Headers $h -Body ([byte[]](1..10))
    Check ($up.bytes -eq 10) 'subir archivo'
    Check ((Code { Invoke-RestMethod "$base/encargos/$id/entrada/virus.exe" -Method Put -Headers $h -Body 'x' }) -eq 400) 'extensión no admitida'
    Check ((Code { Invoke-RestMethod "$base/encargos/$id/entrada/CON.txt" -Method Put -Headers $h -Body 'x' }) -eq 400) 'nombre reservado de Windows'
    $c = Code { Invoke-RestMethod "$base/encargos/..%2F..%2Fx/entrada/a.pdf" -Method Put -Headers $h -Body 'x' }; Check ($c -ge 400) "id con ..: rechazado ($c)"
    Check ((Code { Invoke-RestMethod "$base/encargos/$id/aprobar" -Method Post -Headers $h }) -eq 409) 'aprobar sin ficha: 409'

    Invoke-RestMethod "$base/encargos/$id/ficha" -Method Post -Headers $h | Out-Null
    $e = WaitFor $id 'ficha-lista'
    Check ($e.estado -eq 'ficha-lista' -and $e.ficha -like '*Ficha de prueba*') 'ficha armada'
    Invoke-RestMethod "$base/encargos/$id/aprobar" -Method Post -Headers $h -ContentType 'application/json' -Body '{"comentarios":"ok \" & echo %PATH% \""}' | Out-Null
    $e = WaitFor $id 'listo'
    Check ($e.estado -eq 'listo' -and $e.resultado -eq 'ok resume=si') 'modelo con la misma sesión'
    Check (@($e.salida | Where-Object { $_.nombre -eq 'vista.png' }).Count -eq 1) 'salida listada'
    $dl = Invoke-WebRequest "$base/encargos/$id/archivo?ruta=salida/vista.png" -Headers $h
    Check ($dl.StatusCode -eq 200) 'descargar salida'
    Check ((Code { Invoke-RestMethod "$base/encargos/$id/archivo?ruta=../../repo/x" -Headers $h }) -eq 404) 'descarga fuera del encargo: 404'
    $list = Invoke-RestMethod "$base/encargos" -Headers $h
    Check (@($list.encargos).Count -eq 2) 'listar encargos'
} finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { throw "$fail comprobaciones fallaron" }
Write-Host 'puente-modelado: todo ok'

<#
.SYNOPSIS
Puente local entre la página Modelado 3D de la oficina virtual y BIM Agentic OS (Claude Code + Revit).

.DESCRIPTION
Escucha solo en 127.0.0.1 (por defecto el puerto 47610). La página web le habla con fetch desde el navegador
del dueño; cada pedido lleva el encabezado X-Puente-Token, que tiene que coincidir con .bim/puente-token.txt
(se crea solo la primera vez). Solo responde a los orígenes de .bim/puente-origenes.txt (más los de -Origins).

Rutas:
  GET  /salud                               estado del puente
  GET  /encargos                            lista de encargos
  POST /encargos            {nombre,pedido} crea el encargo (entrada\, salida\, pedido.md, estado.json)
  GET  /encargos/<id>                       estado, archivos de entrada y salida, ficha.md
  PUT  /encargos/<id>/entrada/<archivo>     sube un archivo (cuerpo crudo)
  POST /encargos/<id>/ficha                 Claude arma la ficha (no modela)
  POST /encargos/<id>/aprobar {comentarios} Claude modela con la ficha aprobada
  GET  /encargos/<id>/archivo?ruta=<rel>    descarga un archivo del encargo

Claude corre como `claude -p` en la carpeta del repo, con la habilidad modelado-3d y la carpeta del encargo
agregada. El estado de cada corrida se lee del proceso y de su salida JSON; no hace falta otro servicio.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Root,
    [int]$Port = 47610,
    [string[]]$Origins = @(),
    [string]$Token,
    [string]$ClaudeCommand = 'claude',
    [string]$RepoRoot,
    [long]$MaxFileBytes = 52428800,
    [int]$MaxFiles = 30,
    [switch]$Once
)
$ErrorActionPreference = 'Stop'
if (!$RepoRoot) { $RepoRoot = Split-Path $PSScriptRoot }
$bim = Join-Path $RepoRoot '.bim'
New-Item -ItemType Directory -Force -Path $bim | Out-Null

if (!$Root) {
    $rootFile = Join-Path $bim 'modelado-3d-root.txt'
    if (!(Test-Path -LiteralPath $rootFile)) { throw "Falta $rootFile con la carpeta de encargos." }
    $Root = (Get-Content -LiteralPath $rootFile | Where-Object { $_ -and !$_.StartsWith('#') } | Select-Object -First 1).Trim()
}
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$Root = [IO.Path]::GetFullPath($Root)

if (!$Token) {
    $tokenFile = Join-Path $bim 'puente-token.txt'
    if (!(Test-Path -LiteralPath $tokenFile)) {
        $bytes = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        Set-Content -LiteralPath $tokenFile -Value ([Convert]::ToHexString($bytes).ToLowerInvariant()) -NoNewline
    }
    $Token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
}
$originFile = Join-Path $bim 'puente-origenes.txt'
if (!(Test-Path -LiteralPath $originFile)) {
    Set-Content -LiteralPath $originFile -Value @(
        '# Orígenes web que pueden usar el puente (uno por línea).'
        'http://localhost:5180')
}
# La oficina virtual (local en el 5180 y el Site central) siempre está permitida, aunque el archivo sea de una versión vieja.
$defaultOrigins = @('http://localhost:5180', 'http://127.0.0.1:5180', 'https://oficina-virtual-ezequiel-ader.vercel.app', 'https://oficina-virtual-indol.vercel.app')
$allowed = @(Get-Content -LiteralPath $originFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and !$_.StartsWith('#') }) + $defaultOrigins + $Origins

$extensions = @('.pdf', '.dxf', '.dwg', '.png', '.jpg', '.jpeg', '.webp', '.txt', '.md')

function New-Slug([string]$text) {
    $norm = $text.Normalize([Text.NormalizationForm]::FormD)
    $plain = -join ($norm.ToCharArray() | Where-Object { [Globalization.CharUnicodeInfo]::GetUnicodeCategory($_) -ne 'NonSpacingMark' })
    $slug = ($plain.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ($slug.Length -gt 48) { $slug = $slug.Substring(0, 48).Trim('-') }
    if (!$slug) { $slug = 'encargo' }
    return $slug
}

function Get-EncargoPath([string]$id) {
    if ($id -notmatch '^[a-z0-9][a-z0-9-]{0,80}$') { throw [ArgumentException]::new('id de encargo inválido') }
    $path = Join-Path $Root $id
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw [IO.FileNotFoundException]::new('no existe el encargo') }
    return $path
}

function Read-Estado([string]$dir) {
    $file = Join-Path $dir 'estado.json'
    if (!(Test-Path -LiteralPath $file)) { return [ordered]@{ estado = 'nuevo' } }
    return (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable)
}

function Write-Estado([string]$dir, $estado) {
    $estado.actualizado = [DateTime]::UtcNow.ToString('o')
    $tmp = Join-Path $dir 'estado.json.tmp'
    Set-Content -LiteralPath $tmp -Value ($estado | ConvertTo-Json -Depth 6)
    Move-Item -LiteralPath $tmp -Destination (Join-Path $dir 'estado.json') -Force
}

function Update-Corrida([string]$dir) {
    # Si había una corrida de Claude, mira si terminó y toma session_id y resultado de su salida JSON.
    $estado = Read-Estado $dir
    if (!$estado.corrida -or $estado.corrida.terminada) { return $estado }
    $proc = Get-Process -Id $estado.corrida.pid -ErrorAction SilentlyContinue
    $mismo = $proc -and $proc.StartTime.ToUniversalTime() -le ([DateTime]::Parse($estado.corrida.inicio).ToUniversalTime().AddMinutes(1))
    if ($mismo -and !$proc.HasExited) { return $estado }
    $salida = $estado.corrida.salida
    $ok = $false
    if (Test-Path -LiteralPath $salida) {
        try {
            $json = Get-Content -LiteralPath $salida -Raw | ConvertFrom-Json -AsHashtable
            if ($json.session_id) { $estado.sesion = $json.session_id }
            $estado.resultado = "$($json.result)"
            $ok = -not $json.is_error
        } catch { $estado.resultado = 'La salida de Claude no se pudo leer.' }
    } else { $estado.resultado = 'Claude terminó sin salida.' }
    $estado.corrida.terminada = $true
    $estado.estado = if (!$ok) { 'error' } elseif ($estado.corrida.fase -eq 'ficha') { 'ficha-lista' } else { 'listo' }
    Write-Estado $dir $estado
    return $estado
}

function Start-Claude([string]$dir, [string]$fase, [string]$prompt) {
    $estado = Update-Corrida $dir
    if ($estado.corrida -and !$estado.corrida.terminada) { throw [InvalidOperationException]::new('ya hay una corrida en curso') }
    $logs = Join-Path $dir '.puente'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')
    $out = Join-Path $logs "$fase-$stamp.json"
    $err = Join-Path $logs "$fase-$stamp.err.txt"
    $promptFile = Join-Path $logs "$fase-$stamp.prompt.txt"
    Set-Content -LiteralPath $promptFile -Value $prompt -Encoding utf8NoBOM
    $claudeArgs = @('-p', '--output-format', 'json', '--add-dir', $dir, '--permission-mode', 'acceptEdits',
        '--allowedTools', 'mcp__bim-agentic-os,Read,Write,Edit,Glob,Grep,Bash')
    if ($fase -eq 'modelo' -and $estado.sesion) { $claudeArgs += @('--resume', $estado.sesion) }
    $cmd = Get-Command $ClaudeCommand -ErrorAction Stop | Select-Object -First 1
    $file = $cmd.Source; $argList = $claudeArgs
    if ($file -like '*.ps1') { $argList = @('-NoProfile', '-File', $file) + $claudeArgs; $file = (Get-Process -Id $PID).Path }
    elseif ($file -like '*.cmd' -or $file -like '*.bat') { $argList = @('/d', '/c', $file) + $claudeArgs; $file = $env:ComSpec }
    $quoted = $argList | ForEach-Object { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    $spArgs = @{ FilePath = $file; ArgumentList = ($quoted -join ' '); WorkingDirectory = $RepoRoot
        RedirectStandardInput = $promptFile; RedirectStandardOutput = $out; RedirectStandardError = $err; PassThru = $true }
    if ($IsWindows) { $spArgs.WindowStyle = 'Hidden' }
    $p = Start-Process @spArgs
    $estado.estado = if ($fase -eq 'ficha') { 'armando-ficha' } else { 'modelando' }
    $estado.corrida = [ordered]@{ fase = $fase; pid = $p.Id; salida = $out; inicio = $p.StartTime.ToUniversalTime().ToString('o'); terminada = $false }
    Write-Estado $dir $estado
    return $estado
}

function Get-Archivos([string]$dir) {
    if (!(Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -File -Recurse | ForEach-Object {
        [ordered]@{ nombre = [IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\', '/'); bytes = $_.Length }
    })
}

function Get-Encargo([string]$id) {
    $dir = Get-EncargoPath $id
    $estado = Update-Corrida $dir
    $ficha = Join-Path $dir 'ficha.md'
    return [ordered]@{
        id = $id; carpeta = $dir; estado = $estado.estado; resultado = $estado.resultado; actualizado = $estado.actualizado
        pedido = (Get-Content -LiteralPath (Join-Path $dir 'pedido.md') -Raw -ErrorAction SilentlyContinue)
        ficha = if (Test-Path -LiteralPath $ficha) { Get-Content -LiteralPath $ficha -Raw } else { $null }
        entrada = Get-Archivos (Join-Path $dir 'entrada'); salida = Get-Archivos (Join-Path $dir 'salida')
    }
}

function Send-Json($ctx, [int]$status, $body) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = 'application/json; charset=utf-8'
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
}

function Read-Body($ctx) {
    $reader = [IO.StreamReader]::new($ctx.Request.InputStream, [Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
    if (!$text) { return @{} }
    return ($text | ConvertFrom-Json -AsHashtable)
}

function Invoke-Ruta($ctx) {
    $req = $ctx.Request; $method = $req.HttpMethod
    $parts = @($req.Url.AbsolutePath.Trim('/').Split('/') | ForEach-Object { [Uri]::UnescapeDataString($_) } | Where-Object { $_ })

    if ($method -eq 'GET' -and $parts.Count -eq 1 -and $parts[0] -eq 'salud') {
        return Send-Json $ctx 200 ([ordered]@{ ok = $true; raiz = $Root; version = 1 })
    }
    if ($parts.Count -lt 1 -or $parts[0] -ne 'encargos') { return Send-Json $ctx 404 @{ error = 'ruta desconocida' } }

    if ($parts.Count -eq 1 -and $method -eq 'GET') {
        $lista = @(Get-ChildItem -LiteralPath $Root -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'pedido.md') } |
            ForEach-Object { try { $e = Update-Corrida $_.FullName } catch { $e = @{ estado = 'error'; actualizado = $null } }; [ordered]@{ id = $_.Name; estado = $e.estado; actualizado = $e.actualizado } } |
            Sort-Object { $_.actualizado } -Descending)
        return Send-Json $ctx 200 @{ encargos = $lista }
    }
    if ($parts.Count -eq 1 -and $method -eq 'POST') {
        $body = Read-Body $ctx
        $nombre = "$($body.nombre)".Trim()
        if (!$nombre) { return Send-Json $ctx 400 @{ error = 'falta el nombre del encargo' } }
        $base = New-Slug $nombre; $id = $base; $n = 2
        while (Test-Path -LiteralPath (Join-Path $Root $id)) { $id = "$base-$n"; $n++ }
        $dir = Join-Path $Root $id
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'entrada'), (Join-Path $dir 'salida') | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'pedido.md') -Value "# $nombre`n`n$($body.pedido)`n"
        Write-Estado $dir ([ordered]@{ estado = 'nuevo'; nombre = $nombre })
        return Send-Json $ctx 201 (Get-Encargo $id)
    }

    $id = $parts[1]
    if ($parts.Count -eq 2 -and $method -eq 'GET') { return Send-Json $ctx 200 (Get-Encargo $id) }

    $dir = Get-EncargoPath $id
    if ($parts.Count -eq 4 -and $parts[2] -eq 'entrada' -and $method -eq 'PUT') {
        $name = $parts[3]
        if ($name -notmatch '^[^\\/:*?"<>|]{1,120}$' -or $name.StartsWith('.') -or [IO.Path]::GetFileNameWithoutExtension($name) -match '^(con|prn|aux|nul|com\d|lpt\d)$' -or $extensions -notcontains [IO.Path]::GetExtension($name).ToLowerInvariant()) {
            return Send-Json $ctx 400 @{ error = "archivo no admitido: $name" }
        }
        $entrada = Join-Path $dir 'entrada'
        if (@(Get-ChildItem -LiteralPath $entrada -File).Count -ge $MaxFiles) { return Send-Json $ctx 400 @{ error = "máximo $MaxFiles archivos" } }
        if ($req.ContentLength64 -gt $MaxFileBytes) { return Send-Json $ctx 413 @{ error = 'archivo demasiado grande' } }
        $target = Join-Path $entrada $name
        $fs = [IO.File]::Create($target)
        try {
            $buffer = [byte[]]::new(81920); $total = 0
            while (($read = $req.InputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $total += $read
                if ($total -gt $MaxFileBytes) { throw [IO.InvalidDataException]::new('archivo demasiado grande') }
                $fs.Write($buffer, 0, $read)
            }
        } catch { $fs.Dispose(); Remove-Item -LiteralPath $target -Force; return Send-Json $ctx 413 @{ error = 'archivo demasiado grande' } }
        $fs.Dispose()
        return Send-Json $ctx 201 @{ nombre = $name; bytes = $total }
    }
    if ($parts.Count -eq 3 -and $parts[2] -eq 'ficha' -and $method -eq 'POST') {
        $prompt = "Usá la habilidad modelado-3d. Encargo en la carpeta $dir. Leé pedido.md y todo lo que hay en entrada\. " +
            "Armá ficha.md en esa carpeta siguiendo la plantilla que corresponda. No toques Revit todavía. " +
            "Terminá con un resumen corto de lo que falta confirmar."
        return Send-Json $ctx 202 (Start-Claude $dir 'ficha' $prompt)
    }
    if ($parts.Count -eq 3 -and $parts[2] -eq 'aprobar' -and $method -eq 'POST') {
        $body = Read-Body $ctx
        $estado = Update-Corrida $dir
        if (!(Test-Path -LiteralPath (Join-Path $dir 'ficha.md'))) { return Send-Json $ctx 409 @{ error = 'todavía no hay ficha' } }
        $prompt = "El dueño aprobó la ficha de $dir\ficha.md. Comentarios: $($body.comentarios). " +
            "Modelá en Revit siguiendo la habilidad modelado-3d, guardá, exportá las vistas como imágenes en $dir\salida\ " +
            "y anotá en bitacora.md qué quedó y qué falta."
        return Send-Json $ctx 202 (Start-Claude $dir 'modelo' $prompt)
    }
    if ($parts.Count -eq 3 -and $parts[2] -eq 'archivo' -and $method -eq 'GET') {
        $rel = [Web.HttpUtility]::ParseQueryString($req.Url.Query)['ruta']
        $full = [IO.Path]::GetFullPath((Join-Path $dir "$rel"))
        if (!$rel -or !$full.StartsWith($dir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $full -PathType Leaf)) {
            return Send-Json $ctx 404 @{ error = 'archivo no encontrado' }
        }
        $ctx.Response.ContentType = 'application/octet-stream'
        $ctx.Response.AddHeader('Content-Disposition', "attachment; filename=`"$([IO.Path]::GetFileName($full))`"")
        $stream = [IO.File]::OpenRead($full)
        try { $stream.CopyTo($ctx.Response.OutputStream) } finally { $stream.Dispose() }
        return
    }
    return Send-Json $ctx 404 @{ error = 'ruta desconocida' }
}

Add-Type -AssemblyName System.Web
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "Puente de Modelado 3D escuchando en http://127.0.0.1:$Port  ·  encargos en $Root"
Write-Host 'Dejá esta ventana abierta mientras uses la página de Modelado 3D.'
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        try {
            $origin = $ctx.Request.Headers['Origin']
            if ($origin -and $allowed -contains $origin) {
                $ctx.Response.AddHeader('Access-Control-Allow-Origin', $origin)
                $ctx.Response.AddHeader('Vary', 'Origin')
                $ctx.Response.AddHeader('Access-Control-Allow-Headers', 'Content-Type, X-Puente-Token')
                $ctx.Response.AddHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, OPTIONS')
                $ctx.Response.AddHeader('Access-Control-Allow-Private-Network', 'true')
            }
            if ($origin -and $allowed -notcontains $origin) { Send-Json $ctx 403 @{ error = 'origen no permitido' } }
            elseif ($ctx.Request.HttpMethod -eq 'OPTIONS') { $ctx.Response.StatusCode = 204 }
            elseif (![Security.Cryptography.CryptographicOperations]::FixedTimeEquals([Text.Encoding]::UTF8.GetBytes("$($ctx.Request.Headers['X-Puente-Token'])"), [Text.Encoding]::UTF8.GetBytes($Token))) { Send-Json $ctx 401 @{ error = 'token inválido' } }
            else { Invoke-Ruta $ctx }
        } catch [ArgumentException] { Send-Json $ctx 400 @{ error = $_.Exception.Message }
        } catch [IO.FileNotFoundException] { Send-Json $ctx 404 @{ error = $_.Exception.Message }
        } catch [InvalidOperationException] { Send-Json $ctx 409 @{ error = $_.Exception.Message }
        } catch { try { Send-Json $ctx 500 @{ error = $_.Exception.Message } } catch { }
        } finally { try { $ctx.Response.Close() } catch { } }
        if ($Once) { break }
    }
} finally { $listener.Stop(); $listener.Close() }

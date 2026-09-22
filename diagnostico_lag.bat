<# :
@echo off
title Diagnostico de Red - Gaming
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression -Command (Get-Content -LiteralPath '%~f0' -Raw)"
pause
exit /b
#>

# ======================================================================
#  DIAGNOSTICO DE LAG EN VIVO - Router/Modem e Internet
#  Panel fijo arriba (se actualiza cada segundo) + ultimas mediciones abajo.
#  Teclas: S = reporte completo y pausa, C = continuar, Q = finalizar
#  Tip: maximiza la ventana para ver mas mediciones debajo del panel.
# ======================================================================

$ErrorActionPreference = 'SilentlyContinue'

# --- Destinos ---
$dnsCloudflare = '1.1.1.1'
$dnsGoogle     = '8.8.8.8'

# --- Umbrales del diagnostico (editables) ---
$U_RouterPromedioProblema = 20    # ms: promedio al router = problema
$U_RouterJitterProblema   = 8     # ms: jitter al router = problema
$U_RouterJitterAviso      = 5     # ms: jitter al router = aviso
$U_RouterP95Aviso         = 10    # ms: el 5% de los pings llega a esto = aviso
$U_RouterP99Aviso         = 30    # ms: el 1% de los pings llega a esto = aviso
$U_RouterMaxAviso         = 150   # ms: un solo pico asi de alto = aviso

# --- El log se guarda junto a este .bat ---
$baseDir = $env:SCRIPT_DIR
if ([string]::IsNullOrEmpty($baseDir)) { $baseDir = (Get-Location).Path }
$logFile = Join-Path -Path $baseDir -ChildPath 'registro_latencia.txt'

# --- Ping preciso a nivel .NET: devuelve ms o -1 si se perdio el paquete ---
function Get-PingTime ($Address) {
    try {
        $ping = New-Object System.Net.NetworkInformation.Ping
        $reply = $ping.Send($Address, 1000)
        $tiempo = -1
        if ($reply.Status -eq 'Success') { $tiempo = $reply.RoundtripTime }
        $ping.Dispose()
        return $tiempo
    } catch {
        return -1
    }
}

# --- Detectar la puerta de enlace (router/modem) automaticamente ---
$routerIP = ''
$adapters = Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = TRUE'
foreach ($adapter in $adapters) {
    if ($adapter.DefaultIPGateway) {
        foreach ($gw in $adapter.DefaultIPGateway) {
            if (($routerIP -eq '') -and ($gw -match '^\d{1,3}(\.\d{1,3}){3}$')) { $routerIP = $gw }
        }
    }
}
if ($routerIP -eq '') {
    $routerIP = Read-Host 'No se detecto el router. Ingresa la IP manualmente (ej. 192.168.1.1)'
}

# ======================================================================
#  Estadisticas incrementales (no guardan cada muestra: usan contadores
#  y un histograma de 0 a 1000 ms, asi el panel no se vuelve lento)
# ======================================================================
function Nuevo-Stat {
    return @{ Total = 0; Ok = 0; Perdidos = 0; Suma = 0; Max = 0; Prev = -1; SumaDif = 0; DifN = 0; S80 = 0; S120 = 0; Hist = (@(0) * 1001) }
}

function Agregar-Muestra ($s, $t) {
    $s.Total = $s.Total + 1
    if ($t -lt 0) {
        $s.Perdidos = $s.Perdidos + 1
        return
    }
    $s.Ok = $s.Ok + 1
    $s.Suma = $s.Suma + $t
    if ($t -gt $s.Max) { $s.Max = $t }
    if ($s.Prev -ge 0) {
        $s.SumaDif = $s.SumaDif + [math]::Abs($t - $s.Prev)
        $s.DifN = $s.DifN + 1
    }
    $s.Prev = $t
    $idx = [int]$t
    if ($idx -gt 1000) { $idx = 1000 }
    $s.Hist[$idx] = $s.Hist[$idx] + 1
    if ($t -ge 120) { $s.S120 = $s.S120 + 1 }
    elseif ($t -ge 80) { $s.S80 = $s.S80 + 1 }
}

function Percentil ($s, $p) {
    if ($s.Ok -eq 0) { return 0 }
    $objetivo = [math]::Ceiling($s.Ok * $p / 100)
    if ($objetivo -lt 1) { $objetivo = 1 }
    $acum = 0
    for ($i = 0; $i -le 1000; $i++) {
        $acum = $acum + $s.Hist[$i]
        if ($acum -ge $objetivo) { return $i }
    }
    return 1000
}

function Resumen-Stats ($s) {
    $prom = 0
    $jit = 0
    $perd = 0
    if ($s.Ok -gt 0) { $prom = [math]::Round($s.Suma / $s.Ok, 1) }
    if ($s.DifN -gt 0) { $jit = [math]::Round($s.SumaDif / $s.DifN, 1) }
    if ($s.Total -gt 0) { $perd = [math]::Round(($s.Perdidos / $s.Total) * 100, 1) }
    return @{
        Total     = $s.Total
        Promedio  = $prom
        Mediana   = (Percentil $s 50)
        P95       = (Percentil $s 95)
        P99       = (Percentil $s 99)
        Maximo    = $s.Max
        Jitter    = $jit
        Spikes80  = $s.S80
        Spikes120 = $s.S120
        Perdida   = $perd
    }
}

# Picos relevantes = cantidad absoluta Y proporcion de las muestras:
#   3 o mas de 120 ms (y 0.5% o mas)  o  5 o mas de 80 ms (y 1% o mas)
function Hay-Picos ($x) {
    if ($x.Total -le 0) { return $false }
    $p120 = $x.Spikes120
    $pTodos = $x.Spikes80 + $x.Spikes120
    $pct120 = ($p120 / $x.Total) * 100
    $pctTodos = ($pTodos / $x.Total) * 100
    $grave = ($p120 -ge 3) -and ($pct120 -ge 0.5)
    $moderado = ($pTodos -ge 5) -and ($pctTodos -ge 1)
    return ($grave -or $moderado)
}

# ======================================================================
#  Diagnostico: OK / ATENCION / PROBLEMA (local o ISP)
# ======================================================================
function Evaluar-Diagnostico ($r, $c, $g) {
    $probLocal = @()
    $probIsp = @()
    $avisos = @()

    # --- Router / red local ---
    if ($r.Perdida -ge 5) { $probLocal += "Router/modem: perdida de paquetes de $($r.Perdida)%" }
    if ($r.Promedio -gt $U_RouterPromedioProblema) { $probLocal += "Router/modem: latencia promedio alta ($($r.Promedio) ms)" }
    if ($r.Jitter -gt $U_RouterJitterProblema) { $probLocal += "Router/modem: jitter alto ($($r.Jitter) ms)" }
    if (Hay-Picos $r) { $probLocal += "Router/modem: picos frecuentes ($($r.Spikes120) de 120 ms o mas y $($r.Spikes80) de 80-119 ms en $($r.Total) muestras)" }

    if ($r.Maximo -ge $U_RouterMaxAviso) { $avisos += "Router/modem: pico maximo de $($r.Maximo) ms en $($r.Total) muestras" }
    if ($r.P95 -gt $U_RouterP95Aviso) { $avisos += "Router/modem: el 5% de los pings llega a $($r.P95) ms o mas (P95)" }
    if ($r.P99 -gt $U_RouterP99Aviso) { $avisos += "Router/modem: el 1% de los pings llega a $($r.P99) ms o mas (P99)" }
    if (($r.Jitter -gt $U_RouterJitterAviso) -and ($r.Jitter -le $U_RouterJitterProblema)) { $avisos += "Router/modem: jitter de $($r.Jitter) ms (en una red local sana suele ser de pocos ms)" }
    if (($r.Perdida -gt 0.5) -and ($r.Perdida -lt 5)) { $avisos += "Router/modem: perdida de paquetes de $($r.Perdida)%" }

    # --- Internet (Cloudflare y Google) ---
    $objetivos = @{ 'Cloudflare' = $c; 'Google' = $g }
    foreach ($nom in @('Cloudflare', 'Google')) {
        $x = $objetivos[$nom]
        if ($x.Perdida -gt 3) { $probIsp += "${nom}: perdida de paquetes de $($x.Perdida)%" }
        if ($x.Promedio -gt 100) { $probIsp += "${nom}: latencia promedio alta ($($x.Promedio) ms)" }
        if ($x.Jitter -gt 15) { $probIsp += "${nom}: jitter alto ($($x.Jitter) ms)" }
        if (Hay-Picos $x) { $probIsp += "${nom}: picos frecuentes ($($x.Spikes120) de 120 ms o mas y $($x.Spikes80) de 80-119 ms en $($x.Total) muestras)" }

        if ($x.Maximo -ge 200) { $avisos += "${nom}: pico maximo de $($x.Maximo) ms en $($x.Total) muestras" }
        if (($x.Jitter -gt 10) -and ($x.Jitter -le 15)) { $avisos += "${nom}: jitter de $($x.Jitter) ms" }
        if (($x.Perdida -gt 1) -and ($x.Perdida -le 3)) { $avisos += "${nom}: perdida de paquetes de $($x.Perdida)%" }
    }

    $res = @{}
    if ($probLocal.Count -gt 0) {
        $res.Nivel = 'PROBLEMA'
        $res.Titulo = 'PROBLEMA EN TU RED LOCAL (CASA)'
        $res.Explicacion = 'Hay fallas en el tramo hasta el router/modem, antes de salir a Internet.'
        $res.Motivos = $probLocal + $avisos
        $res.Recomendacion = 'Repeti la prueba con cable Ethernet: si desaparece era el Wi-Fi; si sigue, sospecha del router/modem.'
    }
    elseif ($probIsp.Count -gt 0) {
        $res.Nivel = 'PROBLEMA'
        $res.Titulo = 'PROBLEMA EN TU PROVEEDOR DE INTERNET (ISP)'
        $res.Explicacion = 'Tu red local esta limpia hasta el router, pero la salida a Internet es inestable.'
        $res.Motivos = $probIsp + $avisos
        $res.Recomendacion = 'Guarda este log y usalo como evidencia para reclamarle a tu ISP.'
    }
    elseif ($avisos.Count -gt 0) {
        $res.Nivel = 'ATENCION'
        $res.Titulo = 'ATENCION - ANOMALIAS PUNTUALES'
        $res.Explicacion = 'No hay un problema sostenido, pero se registraron anomalias que pueden sentirse como tirones de lag.'
        $res.Motivos = $avisos
        $res.Recomendacion = 'Deja correr mas tiempo (idealmente mientras notas el lag) y repeti con cable Ethernet para descartar el Wi-Fi.'
    }
    else {
        $res.Nivel = 'OK'
        $res.Titulo = 'TODO EN ORDEN EN LO MEDIDO'
        $res.Explicacion = 'Sin perdida relevante, jitter bajo y sin picos frecuentes ni aislados de importancia.'
        $res.Motivos = @()
        $res.Recomendacion = ''
    }
    return $res
}

# ======================================================================
#  Estado de la sesion
# ======================================================================
$statR = Nuevo-Stat
$statC = Nuevo-Stat
$statG = Nuevo-Stat

$eventos = New-Object System.Collections.ArrayList
$colaTxt = New-Object System.Collections.ArrayList
$colaCol = New-Object System.Collections.ArrayList
$inicioSesion = Get-Date
$anchoPrevio = 0
$altoPrevio = 0

function Registrar-Evento ($nombre, $t) {
    $marca = Get-Date -Format 'HH:mm:ss'
    if ($t -lt 0) { [void]$eventos.Add("[$marca] ${nombre}: paquete perdido") }
    elseif ($t -ge 120) { [void]$eventos.Add("[$marca] ${nombre}: $t ms") }
    if ($eventos.Count -gt 30) { $eventos.RemoveAt(0) }
}

# ======================================================================
#  Reporte completo (snapshot con S, o resumen final con Q)
# ======================================================================
function Construir-Reporte ($esFinal) {
    if ($statR.Total -eq 0) { return 'Todavia no hay mediciones para mostrar.' }

    $r = Resumen-Stats $statR
    $c = Resumen-Stats $statC
    $g = Resumen-Stats $statG
    $dx = Evaluar-Diagnostico $r $c $g

    $titulo = 'SNAPSHOT PARCIAL DE CONEXION'
    $tituloDiag = 'DIAGNOSTICO PARCIAL (con lo medido hasta ahora)'
    if ($esFinal) {
        $titulo = 'RESUMEN ESTADISTICO DEFINITIVO'
        $tituloDiag = 'DIAGNOSTICO DEL SISTEMA'
    }

    $reporte = @"

=======================================================================
                   $titulo
=======================================================================
[Router/Modem ($routerIP)] - $($r.Total) muestras
- Promedio: $($r.Promedio) ms | Mediana: $($r.Mediana) ms | P95: $($r.P95) ms | P99: $($r.P99) ms | Maximo: $($r.Maximo) ms
- Jitter: $($r.Jitter) ms | Picos 80-119ms: $($r.Spikes80) | Picos >=120ms: $($r.Spikes120) | Perdida: $($r.Perdida)%

[Cloudflare DNS ($dnsCloudflare)] - $($c.Total) muestras
- Promedio: $($c.Promedio) ms | Mediana: $($c.Mediana) ms | P95: $($c.P95) ms | P99: $($c.P99) ms | Maximo: $($c.Maximo) ms
- Jitter: $($c.Jitter) ms | Picos 80-119ms: $($c.Spikes80) | Picos >=120ms: $($c.Spikes120) | Perdida: $($c.Perdida)%

[Google DNS ($dnsGoogle)] - $($g.Total) muestras
- Promedio: $($g.Promedio) ms | Mediana: $($g.Mediana) ms | P95: $($g.P95) ms | P99: $($g.P99) ms | Maximo: $($g.Maximo) ms
- Jitter: $($g.Jitter) ms | Picos 80-119ms: $($g.Spikes80) | Picos >=120ms: $($g.Spikes120) | Perdida: $($g.Perdida)%
=======================================================================
"@

    $reporte += "`r`n=======================================================================`r`n"
    $reporte += "                   $tituloDiag`r`n"
    $reporte += "=======================================================================`r`n"
    $reporte += "-> $($dx.Titulo)`r`n"
    $reporte += "   $($dx.Explicacion)`r`n"
    foreach ($m in $dx.Motivos) { $reporte += "   - $m`r`n" }
    if ($dx.Recomendacion -ne '') { $reporte += "   RECOMENDACION: $($dx.Recomendacion)`r`n" }
    if ($r.Total -lt 30) { $reporte += "   (Hay pocas muestras: deja correr mas tiempo antes de confiar en este resultado.)`r`n" }

    if ($eventos.Count -gt 0) {
        $reporte += "`r`nULTIMOS EVENTOS (picos de 120 ms o mas / paquetes perdidos):`r`n"
        foreach ($e in $eventos) { $reporte += "  $e`r`n" }
    }
    $reporte += "=======================================================================`r`n"
    return $reporte
}

# ======================================================================
#  Panel en vivo: se redibuja siempre desde la fila 0 sin borrar la pantalla
# ======================================================================
function Dibujar-Panel {
    $w = [Console]::WindowWidth
    $h = [Console]::WindowHeight
    if (($w -ne $anchoPrevio) -or ($h -ne $altoPrevio)) {
        Clear-Host
        $global:anchoPrevio = $w
        $global:altoPrevio = $h
    }

    $r = Resumen-Stats $statR
    $c = Resumen-Stats $statC
    $g = Resumen-Stats $statG
    $dx = Evaluar-Diagnostico $r $c $g

    $largoSep = [math]::Min(71, $w - 1)
    $sep = '=' * $largoSep
    $transcurrido = ((Get-Date) - $inicioSesion).ToString('hh\:mm\:ss')

    $lineas = New-Object System.Collections.ArrayList
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = '  DIAGNOSTICO DE LATENCIA Y JITTER EN VIVO (GAMING)'; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = " Router/Modem: $routerIP | Cloudflare: $dnsCloudflare | Google: $dnsGoogle"; C = 'Gray' })
    [void]$lineas.Add(@{ T = " Tiempo: $transcurrido | Log: $logFile"; C = 'Gray' })
    [void]$lineas.Add(@{ T = " [S] Reporte completo y pausa  [C] Continuar  [Q] Finalizar y ver diagnostico"; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })

    $fmt = '{0,-14}{1,7}{2,6}{3,5}{4,5}{5,5}{6,6}{7,6}{8,7}{9,6}{10,7}'
    $enc = $fmt -f 'OBJETIVO', 'MUEST', 'PROM', 'MED', 'P95', 'P99', 'MAX', 'JIT', '80-119', '>=120', 'PERD'
    $filaR = $fmt -f 'Router/Modem', $r.Total, $r.Promedio, $r.Mediana, $r.P95, $r.P99, $r.Maximo, $r.Jitter, $r.Spikes80, $r.Spikes120, "$($r.Perdida)%"
    $filaC = $fmt -f 'Cloudflare', $c.Total, $c.Promedio, $c.Mediana, $c.P95, $c.P99, $c.Maximo, $c.Jitter, $c.Spikes80, $c.Spikes120, "$($c.Perdida)%"
    $filaG = $fmt -f 'Google', $g.Total, $g.Promedio, $g.Mediana, $g.P95, $g.P99, $g.Maximo, $g.Jitter, $g.Spikes80, $g.Spikes120, "$($g.Perdida)%"
    [void]$lineas.Add(@{ T = $enc; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $filaR; C = 'White' })
    [void]$lineas.Add(@{ T = $filaC; C = 'White' })
    [void]$lineas.Add(@{ T = $filaG; C = 'White' })

    $colorEstado = 'Green'
    if ($dx.Nivel -eq 'ATENCION') { $colorEstado = 'Yellow' }
    if ($dx.Nivel -eq 'PROBLEMA') { $colorEstado = 'Red' }
    [void]$lineas.Add(@{ T = " ESTADO: $($dx.Titulo)"; C = $colorEstado })
    for ($i = 0; $i -lt 3; $i++) {
        $motivo = ''
        if ($i -lt $dx.Motivos.Count) { $motivo = "   - " + $dx.Motivos[$i] }
        [void]$lineas.Add(@{ T = $motivo; C = 'Gray' })
    }

    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = ' ULTIMAS MEDICIONES (el historial completo esta en el log)'; C = 'Cyan' })

    $libres = $h - 1 - $lineas.Count
    if ($libres -lt 1) { $libres = 1 }
    $inicio = $colaTxt.Count - $libres
    if ($inicio -lt 0) { $inicio = 0 }
    for ($i = $inicio; $i -lt $colaTxt.Count; $i++) {
        [void]$lineas.Add(@{ T = $colaTxt[$i]; C = $colaCol[$i] })
    }

    $maxFilas = $h - 1
    for ($fila = 0; $fila -lt $maxFilas; $fila++) {
        [Console]::SetCursorPosition(0, $fila)
        $txt = ''
        $col = 'Gray'
        if ($fila -lt $lineas.Count) {
            $txt = $lineas[$fila].T
            $col = $lineas[$fila].C
        }
        if ($txt.Length -gt ($w - 1)) { $txt = $txt.Substring(0, $w - 1) }
        Write-Host ($txt.PadRight($w - 1)) -NoNewline -ForegroundColor $col
    }
    [Console]::SetCursorPosition(0, $h - 1)
}

# ======================================================================
#  Pausa con reporte completo. Devuelve $true si el usuario eligio salir.
# ======================================================================
function Modo-Snapshot {
    Clear-Host
    $txt = Construir-Reporte $false
    Write-Host $txt -ForegroundColor Yellow
    Add-Content -Path $logFile -Value $txt
    Write-Host ">>> MONITOREO EN PAUSA. Presiona 'C' para continuar o 'Q' para finalizar <<<" -ForegroundColor Cyan
    while ($true) {
        if ([Console]::KeyAvailable) {
            $k = [Console]::ReadKey($true)
            if ($k.Key -eq 'C') {
                Clear-Host
                $global:anchoPrevio = 0
                return $false
            }
            if ($k.Key -eq 'Q') { return $true }
        }
        Start-Sleep -Milliseconds 100
    }
}

# ======================================================================
#  Bucle principal
# ======================================================================
Add-Content -Path $logFile -Value ("`r`n--- NUEVA SESION DE DIAGNOSTICO: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ---')
[Console]::CursorVisible = $false
Clear-Host

$salir = $false
while (-not $salir) {
    $tRouter = Get-PingTime $routerIP
    $tCloud  = Get-PingTime $dnsCloudflare
    $tGoogle = Get-PingTime $dnsGoogle

    Agregar-Muestra $statR $tRouter
    Agregar-Muestra $statC $tCloud
    Agregar-Muestra $statG $tGoogle

    Registrar-Evento 'Router/Modem' $tRouter
    Registrar-Evento 'Cloudflare' $tCloud
    Registrar-Evento 'Google' $tGoogle

    $sRouter = 'Perdido'
    if ($tRouter -ge 0) { $sRouter = "$tRouter ms" }
    $sCloud = 'Perdido'
    if ($tCloud -ge 0) { $sCloud = "$tCloud ms" }
    $sGoogle = 'Perdido'
    if ($tGoogle -ge 0) { $sGoogle = "$tGoogle ms" }

    $cuerpo = 'Router: ' + $sRouter.PadRight(8) + ' | Cloudflare: ' + $sCloud.PadRight(8) + ' | Google: ' + $sGoogle.PadRight(8)
    $lineaConsola = '[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $cuerpo
    $lineaLog = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $cuerpo

    # Rojo: algo perdido, router >=25 ms o Internet >=120 ms. Amarillo: router >=10 ms o Internet >=80 ms
    $colorLinea = 'Gray'
    $rojo = ($tRouter -lt 0) -or ($tCloud -lt 0) -or ($tGoogle -lt 0) -or ($tRouter -ge 25) -or ($tCloud -ge 120) -or ($tGoogle -ge 120)
    $amarillo = ($tRouter -ge 10) -or ($tCloud -ge 80) -or ($tGoogle -ge 80)
    if ($rojo) { $colorLinea = 'Red' }
    elseif ($amarillo) { $colorLinea = 'Yellow' }

    [void]$colaTxt.Add($lineaConsola)
    [void]$colaCol.Add($colorLinea)
    if ($colaTxt.Count -gt 300) {
        $colaTxt.RemoveAt(0)
        $colaCol.RemoveAt(0)
    }
    Add-Content -Path $logFile -Value $lineaLog

    Dibujar-Panel

    # Espera de 1 segundo revisando teclas cada 100 ms
    for ($k = 0; $k -lt 10; $k++) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq 'Q') {
                $salir = $true
                break
            }
            if ($key.Key -eq 'S') {
                $salir = Modo-Snapshot
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }
}

# --- Resumen final ---
[Console]::CursorVisible = $true
Clear-Host
$final = Construir-Reporte $true
Write-Host $final -ForegroundColor Yellow
Add-Content -Path $logFile -Value $final
Write-Host "Registro guardado en: $logFile" -ForegroundColor Cyan
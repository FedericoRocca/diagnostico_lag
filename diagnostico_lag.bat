<# :
@echo off
title Diagnostico de Red - Gaming
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression -Command (Get-Content -LiteralPath '%~f0' -Raw)"
pause
exit /b
#>

# ======================================================================
#  DIAGNOSTICO DE LAG EN VIVO - Red local / ISP / Internet
#  Panel fijo arriba (se actualiza cada segundo) + ultimas mediciones abajo.
#  Cada 1 minuto se guarda un registro periodico completo en el log.
#  Teclas: S = reporte completo y pausa, C = continuar, Q = finalizar
#  Ctrl+C tambien finaliza de forma prolija (guarda el resumen final).
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
$U_SenalWifiBaja          = 50    # %: senal Wi-Fi por debajo de esto = aviso
$U_SegundosRegistroPeriodico = 60 # segundos entre registros periodicos en el log

# --- El log se guarda junto a este .bat ---
$baseDir = $env:SCRIPT_DIR
if ([string]::IsNullOrEmpty($baseDir)) { $baseDir = (Get-Location).Path }
$logFile = Join-Path -Path $baseDir -ChildPath 'registro_latencia.txt'

# --- Formatea "N de TOTAL muestras (X%)" para cualquier conteo que se muestre ---
function Formato-Conteo ($cantidad, $total) {
    $pct = 0
    if ($total -gt 0) { $pct = [math]::Round(($cantidad / $total) * 100, 1) }
    return "$cantidad de $total muestras ($pct%)"
}

function Formato-Duracion ($desde) {
    return ((Get-Date) - $desde).ToString('hh\:mm\:ss')
}

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
#  Primer salto del ISP: el equipo justo despues del router, via tracert.
#  OJO: muchos routers intermedios responden al ping con menor prioridad
#  que al trafico que solo atraviesan, asi que estos valores pueden salir
#  inflados. Se usan como pista, no como confirmacion por si solos.
# ======================================================================
function Obtener-Saltos ($destino, $maxSaltos) {
    $saltos = @{}
    $salida = & tracert.exe -d -h $maxSaltos -w 800 $destino 2>$null
    foreach ($linea in $salida) {
        if ($linea -match '^\s*(\d+)\s') {
            $num = [int]$Matches[1]
            if ($linea -match '(\d{1,3}(?:\.\d{1,3}){3})\s*$') {
                $saltos[$num] = $Matches[1]
            }
        }
    }
    return $saltos
}

function Detectar-SaltoISP ($routerIP, $destino) {
    $saltos = Obtener-Saltos $destino 6
    for ($n = 2; $n -le 6; $n++) {
        if ($saltos.ContainsKey($n)) {
            $ip = $saltos[$n]
            if (($ip -ne $routerIP) -and ($ip -ne $destino)) { return $ip }
        }
    }
    return $null
}

Write-Host 'Detectando el primer salto de tu proveedor (ISP)...' -ForegroundColor Cyan
$ispIP = Detectar-SaltoISP $routerIP $dnsCloudflare
if ($ispIP -eq $dnsCloudflare) { $ispIP = $null }

# ======================================================================
#  Tipo de conexion (Wi-Fi/cable), SSID y senal.
#  El SSID se obtiene primero via Get-NetConnectionProfile, que NO
#  requiere permiso de ubicacion ni ser administrador. La senal (%) si
#  depende de netsh, que en Windows 10/11 exige el permiso de Ubicacion
#  y, en algunos equipos, tambien ejecutar como administrador.
# ======================================================================
function Obtener-InfoConexion {
    $info = @{ Tipo = 'No detectado'; Detalle = ''; Senal = $null; SSID = ''; Cruda = @(); PermisoFaltante = $false }
    $ruta = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object -Property RouteMetric | Select-Object -First 1
    if (-not $ruta) { return $info }

    $adaptador = Get-NetAdapter -InterfaceIndex $ruta.InterfaceIndex -ErrorAction SilentlyContinue
    if (-not $adaptador) { return $info }

    if ($adaptador.PhysicalMediaType -match '802.11') {
        $info.Tipo = 'Wi-Fi'

        # Metodo principal (sin admin, sin permiso de ubicacion): el nombre
        # del perfil de red activo. Coincide con el SSID salvo que lo hayas
        # renombrado manualmente en Windows.
        $perfil = Get-NetConnectionProfile -InterfaceIndex $ruta.InterfaceIndex -ErrorAction SilentlyContinue
        if ($perfil -and $perfil.Name) { $info.SSID = $perfil.Name }

        # Senal (y SSID de respaldo si el metodo principal fallo): netsh.
        $salidaWifi = @(& netsh.exe wlan show interfaces 2>$null)
        $info.Cruda = $salidaWifi
        $textoCompleto = ($salidaWifi -join ' ')
        if ($textoCompleto -match '(?i)elevaci|administrador|permiso de ubicaci') {
            $info.PermisoFaltante = $true
        } else {
            $pares = @{}
            foreach ($linea in $salidaWifi) {
                if ($linea -match '^\s*([^:]+?)\s*:\s*(.+?)\s*$') {
                    $clave = $Matches[1]
                    $valor = $Matches[2]
                    if (-not $pares.ContainsKey($clave)) { $pares[$clave] = $valor }
                }
            }
            foreach ($clave in $pares.Keys) {
                $claveNorm = $clave.ToLowerInvariant()
                if (($claveNorm -match 'ssid') -and ($claveNorm -notmatch 'bssid') -and ($info.SSID -eq '')) {
                    $info.SSID = $pares[$clave]
                }
                if (($claveNorm -match 'se.al' -or $claveNorm -match 'signal') -and ($pares[$clave] -match '(\d{1,3})\s*%')) {
                    $info.Senal = [int]$Matches[1]
                }
            }
        }

        if ($info.SSID -ne '') {
            $info.Detalle = "SSID: $($info.SSID)"
            if ($info.Senal -eq $null) {
                if ($info.PermisoFaltante) {
                    $info.Detalle += ' (senal no disponible: netsh pide permiso de ubicacion/admin)'
                } else {
                    $info.Detalle += ' (senal no disponible)'
                }
            }
        } elseif ($info.PermisoFaltante) {
            $info.Detalle = 'SSID/senal no disponibles: Windows pide el permiso de Ubicacion (y en este equipo tambien ejecutar como administrador). Ver nota en el log.'
        } elseif ($salidaWifi.Count -gt 0) {
            $info.Detalle = 'conectado, pero no se pudo leer el SSID (ver detalle DEBUG al inicio del log)'
        } else {
            $info.Detalle = 'no se pudo leer "netsh wlan show interfaces" (ver detalle DEBUG al inicio del log)'
        }
    } else {
        $info.Tipo = 'Cable (Ethernet)'
        $info.Detalle = $adaptador.Name
    }
    return $info
}

$conexion = Obtener-InfoConexion

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
        Total      = $s.Total
        Promedio   = $prom
        Mediana    = (Percentil $s 50)
        P95        = (Percentil $s 95)
        P99        = (Percentil $s 99)
        Maximo     = $s.Max
        Jitter     = $jit
        Spikes80   = $s.S80
        Spikes120  = $s.S120
        Perdida    = $perd
        PerdidosN  = $s.Perdidos
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
#  Diagnostico: OK / ATENCION / PROBLEMA (local, ISP o internet general)
# ======================================================================
function Evaluar-Diagnostico ($r, $i, $c, $g, $conexion) {
    $probLocal = @()
    $probIsp = @()
    $avisos = @()

    # --- Router / red local ---
    if ($r.Perdida -ge 5) { $probLocal += "Router/modem: perdida de paquetes: $(Formato-Conteo $r.PerdidosN $r.Total)" }
    if ($r.Promedio -gt $U_RouterPromedioProblema) { $probLocal += "Router/modem: latencia promedio alta ($($r.Promedio) ms sobre $($r.Total) muestras)" }
    if ($r.Jitter -gt $U_RouterJitterProblema) { $probLocal += "Router/modem: jitter alto ($($r.Jitter) ms sobre $($r.Total) muestras)" }
    if (Hay-Picos $r) { $probLocal += "Router/modem: picos frecuentes ($(Formato-Conteo $r.Spikes120 $r.Total) de 120 ms o mas, $(Formato-Conteo $r.Spikes80 $r.Total) de 80-119 ms)" }

    if ($r.Maximo -ge $U_RouterMaxAviso) { $avisos += "Router/modem: pico maximo de $($r.Maximo) ms (sobre $($r.Total) muestras totales)" }
    if ($r.P95 -gt $U_RouterP95Aviso) { $avisos += "Router/modem: el 5% de los pings mas lentos llega a $($r.P95) ms o mas (P95, sobre $($r.Total) muestras)" }
    if ($r.P99 -gt $U_RouterP99Aviso) { $avisos += "Router/modem: el 1% de los pings mas lentos llega a $($r.P99) ms o mas (P99, sobre $($r.Total) muestras)" }
    if (($r.Jitter -gt $U_RouterJitterAviso) -and ($r.Jitter -le $U_RouterJitterProblema)) { $avisos += "Router/modem: jitter de $($r.Jitter) ms (en una red local sana suele ser de pocos ms)" }
    if (($r.Perdida -gt 0.5) -and ($r.Perdida -lt 5)) { $avisos += "Router/modem: perdida de paquetes: $(Formato-Conteo $r.PerdidosN $r.Total)" }

    if (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.Senal -ne $null) -and ($conexion.Senal -lt $U_SenalWifiBaja)) {
        $avisos += "Senal Wi-Fi baja ($($conexion.Senal)%): puede ser la causa de picos intermitentes"
    }

    # --- Internet (Cloudflare y Google) ---
    $objetivos = @{ 'Cloudflare' = $c; 'Google' = $g }
    foreach ($nom in @('Cloudflare', 'Google')) {
        $x = $objetivos[$nom]
        if ($x.Perdida -gt 3) { $probIsp += "${nom}: perdida de paquetes: $(Formato-Conteo $x.PerdidosN $x.Total)" }
        if ($x.Promedio -gt 100) { $probIsp += "${nom}: latencia promedio alta ($($x.Promedio) ms sobre $($x.Total) muestras)" }
        if ($x.Jitter -gt 15) { $probIsp += "${nom}: jitter alto ($($x.Jitter) ms sobre $($x.Total) muestras)" }
        if (Hay-Picos $x) { $probIsp += "${nom}: picos frecuentes ($(Formato-Conteo $x.Spikes120 $x.Total) de 120 ms o mas, $(Formato-Conteo $x.Spikes80 $x.Total) de 80-119 ms)" }

        if ($x.Maximo -ge 200) { $avisos += "${nom}: pico maximo de $($x.Maximo) ms (sobre $($x.Total) muestras totales)" }
        if (($x.Jitter -gt 10) -and ($x.Jitter -le 15)) { $avisos += "${nom}: jitter de $($x.Jitter) ms" }
        if (($x.Perdida -gt 1) -and ($x.Perdida -le 3)) { $avisos += "${nom}: perdida de paquetes: $(Formato-Conteo $x.PerdidosN $x.Total)" }
    }

    # --- Salto ISP: solo corrobora si Internet YA muestra problemas ---
    if (($i -ne $null) -and ($probIsp.Count -gt 0)) {
        if ((Hay-Picos $i) -or ($i.Perdida -gt 3) -or ($i.Jitter -gt 15)) {
            $probIsp += "Confirmado tambien en el primer salto de tu ISP: jitter $($i.Jitter) ms, perdida $(Formato-Conteo $i.PerdidosN $i.Total), $(Formato-Conteo $i.Spikes120 $i.Total) picos de 120 ms o mas"
        }
    }

    # --- Pista: picos solo en el router, limpios rio abajo, sugiere Wi-Fi/router y no el ISP ---
    if ((Hay-Picos $r) -and (-not (Hay-Picos $c)) -and (-not (Hay-Picos $g)) -and (($i -eq $null) -or (-not (Hay-Picos $i)))) {
        $avisos += "Los picos del router NO se repiten en el salto del ISP ni en Cloudflare/Google, aunque ese trafico tambien pasa por el router: esto apunta a Wi-Fi o al propio router, no a tu proveedor"
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
$statI = $null
if ($ispIP -ne $null) { $statI = Nuevo-Stat }
$statC = Nuevo-Stat
$statG = Nuevo-Stat

$eventos = New-Object System.Collections.ArrayList
$colaTxt = New-Object System.Collections.ArrayList
$colaCol = New-Object System.Collections.ArrayList
$inicioSesion = Get-Date
$ultimoRegistroPeriodico = Get-Date
$anchoPrevio = 0
$altoPrevio = 0

function Registrar-Evento ($nombre, $t) {
    $marca = Get-Date -Format 'HH:mm:ss'
    if ($t -lt 0) { [void]$eventos.Add("[$marca] ${nombre}: paquete perdido") }
    elseif ($t -ge 120) { [void]$eventos.Add("[$marca] ${nombre}: $t ms") }
    if ($eventos.Count -gt 30) { $eventos.RemoveAt(0) }
}

# ======================================================================
#  Reporte completo. $modo: 'parcial' (tecla S), 'final' (tecla Q o Ctrl+C)
#  o 'periodico' (se guarda solo en el log cada 1 minuto, automatico)
# ======================================================================
function Construir-Reporte ($modo) {
    if ($statR.Total -eq 0) { return 'Todavia no hay mediciones para mostrar.' }

    $r = Resumen-Stats $statR
    $i = $null
    if ($statI -ne $null) { $i = Resumen-Stats $statI }
    $c = Resumen-Stats $statC
    $g = Resumen-Stats $statG
    $dx = Evaluar-Diagnostico $r $i $c $g $conexion
    $duracion = Formato-Duracion $inicioSesion

    $titulo = 'SNAPSHOT PARCIAL DE CONEXION'
    $tituloDiag = 'DIAGNOSTICO PARCIAL (con lo medido hasta ahora)'
    if ($modo -eq 'final') {
        $titulo = 'RESUMEN ESTADISTICO DEFINITIVO'
        $tituloDiag = 'DIAGNOSTICO DEL SISTEMA'
    } elseif ($modo -eq 'periodico') {
        $titulo = 'REGISTRO PERIODICO AUTOMATICO (cada 1 minuto)'
        $tituloDiag = 'DIAGNOSTICO AL MOMENTO DE ESTE REGISTRO'
    }

    $lineaConexion = "Conexion: $($conexion.Tipo)"
    if ($conexion.Detalle -ne '') { $lineaConexion += " ($($conexion.Detalle)" + $(if ($conexion.Senal -ne $null) { ", Senal: $($conexion.Senal)%)" } else { ")" }) }

    $reporte = @"

=======================================================================
                   $titulo
=======================================================================
Duracion de la sesion: $duracion
$lineaConexion
[Router/Modem ($routerIP)] - $($r.Total) muestras totales
- Promedio: $($r.Promedio) ms | Mediana: $($r.Mediana) ms | P95: $($r.P95) ms | P99: $($r.P99) ms | Maximo: $($r.Maximo) ms
- Jitter: $($r.Jitter) ms | Picos 80-119ms: $($r.Spikes80) | Picos >=120ms: $($r.Spikes120) | Perdida: $($r.Perdida)% ($($r.PerdidosN) de $($r.Total))

"@

    if ($i -ne $null) {
        $reporte += @"
[Primer salto ISP ($ispIP)] - $($i.Total) muestras totales (referencia: puede estar inflado, ver nota abajo)
- Promedio: $($i.Promedio) ms | Mediana: $($i.Mediana) ms | P95: $($i.P95) ms | P99: $($i.P99) ms | Maximo: $($i.Maximo) ms
- Jitter: $($i.Jitter) ms | Picos 80-119ms: $($i.Spikes80) | Picos >=120ms: $($i.Spikes120) | Perdida: $($i.Perdida)% ($($i.PerdidosN) de $($i.Total))

"@
    } else {
        $reporte += "[Primer salto ISP] No se pudo detectar automaticamente (tracert sin respuesta en los primeros saltos).`r`n`r`n"
    }

    $reporte += @"
[Cloudflare DNS ($dnsCloudflare)] - $($c.Total) muestras totales
- Promedio: $($c.Promedio) ms | Mediana: $($c.Mediana) ms | P95: $($c.P95) ms | P99: $($c.P99) ms | Maximo: $($c.Maximo) ms
- Jitter: $($c.Jitter) ms | Picos 80-119ms: $($c.Spikes80) | Picos >=120ms: $($c.Spikes120) | Perdida: $($c.Perdida)% ($($c.PerdidosN) de $($c.Total))

[Google DNS ($dnsGoogle)] - $($g.Total) muestras totales
- Promedio: $($g.Promedio) ms | Mediana: $($g.Mediana) ms | P95: $($g.P95) ms | P99: $($g.P99) ms | Maximo: $($g.Maximo) ms
- Jitter: $($g.Jitter) ms | Picos 80-119ms: $($g.Spikes80) | Picos >=120ms: $($g.Spikes120) | Perdida: $($g.Perdida)% ($($g.PerdidosN) de $($g.Total))
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
    if ($i -ne $null) { $reporte += "   NOTA: el 'primer salto ISP' es un equipo intermedio; muchos routers responden al ping con menor prioridad que al trafico que solo atraviesan, asi que sus numeros pueden estar inflados. Se usa solo como pista adicional.`r`n" }

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
    $i = $null
    if ($statI -ne $null) { $i = Resumen-Stats $statI }
    $c = Resumen-Stats $statC
    $g = Resumen-Stats $statG
    $dx = Evaluar-Diagnostico $r $i $c $g $conexion

    $largoSep = [math]::Min(75, $w - 1)
    $sep = '=' * $largoSep
    $transcurrido = Formato-Duracion $inicioSesion

    $lineaConexion = " Conexion: $($conexion.Tipo)"
    if ($conexion.Detalle -ne '') { $lineaConexion += " ($($conexion.Detalle)" + $(if ($conexion.Senal -ne $null) { ", Senal: $($conexion.Senal)%)" } else { ")" }) }

    $lineas = New-Object System.Collections.ArrayList
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = '  DIAGNOSTICO DE LATENCIA Y JITTER EN VIVO (GAMING)'; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = " Router/Modem: $routerIP | ISP: $(if ($ispIP -ne $null) { $ispIP } else { 'no detectado' }) | Cloudflare: $dnsCloudflare | Google: $dnsGoogle"; C = 'Gray' })
    [void]$lineas.Add(@{ T = $lineaConexion; C = 'Gray' })
    [void]$lineas.Add(@{ T = " Duracion: $transcurrido | Registro periodico cada $($U_SegundosRegistroPeriodico)s | Log: $logFile"; C = 'Gray' })
    [void]$lineas.Add(@{ T = " [S] Reporte y pausa  [C] Continuar  [Q] o Ctrl+C: Finalizar y ver diagnostico"; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })

    $fmt = '{0,-14}{1,7}{2,6}{3,5}{4,5}{5,5}{6,6}{7,6}{8,7}{9,6}{10,7}'
    $enc = $fmt -f 'OBJETIVO', 'MUEST', 'PROM', 'MED', 'P95', 'P99', 'MAX', 'JIT', '80-119', '>=120', 'PERD'
    $filaR = $fmt -f 'Router/Modem', $r.Total, $r.Promedio, $r.Mediana, $r.P95, $r.P99, $r.Maximo, $r.Jitter, $r.Spikes80, $r.Spikes120, "$($r.Perdida)%"
    $filaC = $fmt -f 'Cloudflare', $c.Total, $c.Promedio, $c.Mediana, $c.P95, $c.P99, $c.Maximo, $c.Jitter, $c.Spikes80, $c.Spikes120, "$($c.Perdida)%"
    $filaG = $fmt -f 'Google', $g.Total, $g.Promedio, $g.Mediana, $g.P95, $g.P99, $g.Maximo, $g.Jitter, $g.Spikes80, $g.Spikes120, "$($g.Perdida)%"
    [void]$lineas.Add(@{ T = $enc; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $filaR; C = 'White' })
    if ($i -ne $null) {
        $filaI = $fmt -f 'ISP (1er salto)', $i.Total, $i.Promedio, $i.Mediana, $i.P95, $i.P99, $i.Maximo, $i.Jitter, $i.Spikes80, $i.Spikes120, "$($i.Perdida)%"
        [void]$lineas.Add(@{ T = $filaI; C = 'DarkGray' })
    }
    [void]$lineas.Add(@{ T = $filaC; C = 'White' })
    [void]$lineas.Add(@{ T = $filaG; C = 'White' })

    $colorEstado = 'Green'
    if ($dx.Nivel -eq 'ATENCION') { $colorEstado = 'Yellow' }
    if ($dx.Nivel -eq 'PROBLEMA') { $colorEstado = 'Red' }
    [void]$lineas.Add(@{ T = " ESTADO: $($dx.Titulo)"; C = $colorEstado })
    for ($k = 0; $k -lt 3; $k++) {
        $motivo = ''
        if ($k -lt $dx.Motivos.Count) { $motivo = "   - " + $dx.Motivos[$k] }
        [void]$lineas.Add(@{ T = $motivo; C = 'Gray' })
    }

    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = ' ULTIMAS MEDICIONES (el historial completo esta en el log)'; C = 'Cyan' })

    $libres = $h - 1 - $lineas.Count
    if ($libres -lt 1) { $libres = 1 }
    $inicio = $colaTxt.Count - $libres
    if ($inicio -lt 0) { $inicio = 0 }
    for ($idx = $inicio; $idx -lt $colaTxt.Count; $idx++) {
        [void]$lineas.Add(@{ T = $colaTxt[$idx]; C = $colaCol[$idx] })
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

# --- Detecta si una tecla leida es Ctrl+C ---
function Es-CtrlC ($key) {
    return (($key.Modifiers -band [ConsoleModifiers]::Control) -and ($key.Key -eq 'C'))
}

# ======================================================================
#  Pausa con reporte completo. Devuelve $true si el usuario eligio salir.
# ======================================================================
function Modo-Snapshot {
    Clear-Host
    $txt = Construir-Reporte 'parcial'
    Write-Host $txt -ForegroundColor Yellow
    Add-Content -Path $logFile -Value $txt
    Write-Host ">>> MONITOREO EN PAUSA. Presiona 'C' para continuar, o 'Q'/Ctrl+C para finalizar <<<" -ForegroundColor Cyan
    while ($true) {
        if ([Console]::KeyAvailable) {
            $k = [Console]::ReadKey($true)
            if ((Es-CtrlC $k) -or ($k.Key -eq 'Q')) { return $true }
            if ($k.Key -eq 'C') {
                Clear-Host
                $global:anchoPrevio = 0
                return $false
            }
        }
        Start-Sleep -Milliseconds 100
    }
}

# ======================================================================
#  Bucle principal
# ======================================================================
Add-Content -Path $logFile -Value ("`r`n--- NUEVA SESION DE DIAGNOSTICO: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ---')
Add-Content -Path $logFile -Value ("Conexion: $($conexion.Tipo) | ISP detectado: $(if ($ispIP -ne $null) { $ispIP } else { 'no' })")

if (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.SSID -eq '') -and ($conexion.PermisoFaltante)) {
    Add-Content -Path $logFile -Value 'NOTA: no se pudo leer SSID/senal por netsh. Windows exige el permiso de Ubicacion (Configuracion > Privacidad y seguridad > Ubicacion), y en este equipo ademas pide ejecutar el .bat como administrador. El SSID por Get-NetConnectionProfile tampoco se pudo obtener en esta sesion.'
} elseif (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.SSID -eq '')) {
    Add-Content -Path $logFile -Value "DEBUG - salida cruda de 'netsh wlan show interfaces' (para ajustar la deteccion del SSID):"
    if ($conexion.Cruda.Count -eq 0) {
        Add-Content -Path $logFile -Value '  (el comando no devolvio ninguna linea)'
    } else {
        foreach ($linea in $conexion.Cruda) { Add-Content -Path $logFile -Value "  [$($linea.Length) chars] $linea" }
    }
    Add-Content -Path $logFile -Value ''
}

[Console]::CursorVisible = $false
[Console]::TreatControlCAsInput = $true
Clear-Host

$salir = $false
while (-not $salir) {
    $tRouter = Get-PingTime $routerIP
    $tIsp = -1
    if ($ispIP -ne $null) { $tIsp = Get-PingTime $ispIP }
    $tCloud  = Get-PingTime $dnsCloudflare
    $tGoogle = Get-PingTime $dnsGoogle

    Agregar-Muestra $statR $tRouter
    if ($statI -ne $null) { Agregar-Muestra $statI $tIsp }
    Agregar-Muestra $statC $tCloud
    Agregar-Muestra $statG $tGoogle

    Registrar-Evento 'Router/Modem' $tRouter
    if ($ispIP -ne $null) { Registrar-Evento 'ISP (1er salto)' $tIsp }
    Registrar-Evento 'Cloudflare' $tCloud
    Registrar-Evento 'Google' $tGoogle

    $sRouter = 'Perdido'
    if ($tRouter -ge 0) { $sRouter = "$tRouter ms" }
    $sCloud = 'Perdido'
    if ($tCloud -ge 0) { $sCloud = "$tCloud ms" }
    $sGoogle = 'Perdido'
    if ($tGoogle -ge 0) { $sGoogle = "$tGoogle ms" }

    $cuerpo = 'Router: ' + $sRouter.PadRight(8) + ' | Cloudflare: ' + $sCloud.PadRight(8) + ' | Google: ' + $sGoogle.PadRight(8)
    if ($ispIP -ne $null) {
        $sIsp = 'Perdido'
        if ($tIsp -ge 0) { $sIsp = "$tIsp ms" }
        $cuerpo = 'Router: ' + $sRouter.PadRight(8) + ' | ISP: ' + $sIsp.PadRight(8) + ' | Cloudflare: ' + $sCloud.PadRight(8) + ' | Google: ' + $sGoogle.PadRight(8)
    }
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

    # --- Registro periodico automatico en el log (no interrumpe el panel) ---
    if (((Get-Date) - $ultimoRegistroPeriodico).TotalSeconds -ge $U_SegundosRegistroPeriodico) {
        $txtPeriodico = Construir-Reporte 'periodico'
        Add-Content -Path $logFile -Value $txtPeriodico
        $ultimoRegistroPeriodico = Get-Date
    }

    Dibujar-Panel

    # Espera de 1 segundo revisando teclas cada 100 ms
    for ($k = 0; $k -lt 10; $k++) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ((Es-CtrlC $key) -or ($key.Key -eq 'Q')) {
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
[Console]::TreatControlCAsInput = $false
[Console]::CursorVisible = $true
Clear-Host
$final = Construir-Reporte 'final'
Write-Host $final -ForegroundColor Yellow
Add-Content -Path $logFile -Value $final
Write-Host "Registro guardado en: $logFile" -ForegroundColor Cyan
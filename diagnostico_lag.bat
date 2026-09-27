<# :
@echo off
title Network Diagnostics - Gaming
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression -Command (Get-Content -LiteralPath '%~f0' -Raw)"
pause
exit /b
#>

# ======================================================================
#  LIVE LAG DIAGNOSTICS - Local network / ISP / Internet / LoL server
#  Fixed live panel on top (refreshes every second) + latest samples below.
#  Every 1 minute a full periodic snapshot is saved to the log.
#  Keys: S = full report and pause, C = continue, Q = finish
#  Ctrl+C also finishes cleanly (saves the final summary first).
#  Tip: maximize the window to see more samples below the panel.
# ======================================================================

$ErrorActionPreference = 'SilentlyContinue'

# --- Targets ---
$dnsCloudflare = '1.1.1.1'
$dnsGoogle     = '8.8.8.8'
$puertoTcp     = 443   # HTTPS: should always be open on both targets

# --- Diagnosis thresholds (editable) ---
$U_RouterPromedioProblema = 20    # ms: average to router = problem
$U_RouterJitterProblema   = 8     # ms: jitter to router = problem
$U_RouterJitterAviso      = 5     # ms: jitter to router = warning
$U_RouterP95Aviso         = 10    # ms: 5% of pings reach this = warning
$U_RouterP99Aviso         = 30    # ms: 1% of pings reach this = warning
$U_RouterMaxAviso         = 150   # ms: a single spike this high = warning
$U_SenalWifiBaja          = 50    # %: Wi-Fi signal below this = warning
$U_SegundosRegistroPeriodico = 60 # seconds between periodic log entries
$U_TcpPromedioProblema    = 150   # ms: real TCP connection average = problem
$U_TcpJitterProblema      = 20    # ms: real TCP connection jitter = problem

# --- Personal baseline: taken from the first N samples of the session ---
$U_MuestrasBaseline        = 60   # ~1 minute at 1 sample/sec
$U_BaselineFactorProm      = 3    # current average must be N times the baseline...
$U_BaselineMinDeltaProm    = 5    # ...and also exceed it by at least this (ms)
$U_BaselineFactorJitter    = 3
$U_BaselineMinDeltaJitter  = 5

# --- Periodic pattern detection in events (spikes/losses) ---
$U_MinEventosPeriodicidad  = 4    # minimum events to attempt pattern detection
$U_MinEventosConfianza     = 6    # from this many events on, report with more confidence
$U_MinSegundosPeriodo      = 10   # events closer together than this aren't treated as "periodic"
$U_MaxCoefVariacion        = 0.25 # how even the intervals must be (0 = identical)

# --- Automatic LoL server detection (best effort, no admin required) ---
$U_SegundosReintentoLol = 15  # how often to retry detecting the LoL client while not found yet

# --- The log is saved next to this .bat ---
$baseDir = $env:SCRIPT_DIR
if ([string]::IsNullOrEmpty($baseDir)) { $baseDir = (Get-Location).Path }
$logFile = Join-Path -Path $baseDir -ChildPath 'registro_latencia.txt'

# --- Formats "N of TOTAL samples (X%)" for any count shown ---
function Formato-Conteo ($cantidad, $total) {
    $pct = 0
    if ($total -gt 0) { $pct = [math]::Round(($cantidad / $total) * 100, 1) }
    return "$cantidad of $total samples ($pct%)"
}

function Formato-Duracion ($desde) {
    return ((Get-Date) - $desde).ToString('hh\:mm\:ss')
}

# --- Precise ICMP ping at the .NET level: returns ms, or -1 if the packet was lost ---
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

# --- Real TCP "ping": connection time (SYN-ACK) to a port. Closer to the
#     real traffic of an app/game than ICMP, which many intermediate
#     devices prioritize differently (or don't answer at all). ---
function Get-TcpConnectTime ($Address, $Port, $TimeoutMs) {
    $cliente = $null
    try {
        $cliente = New-Object System.Net.Sockets.TcpClient
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resultado = $cliente.BeginConnect($Address, $Port, $null, $null)
        $exito = $resultado.AsyncWaitHandle.WaitOne($TimeoutMs)
        $sw.Stop()
        $tiempo = -1
        if ($exito -and $cliente.Connected) {
            $cliente.EndConnect($resultado)
            $tiempo = [int]$sw.ElapsedMilliseconds
        }
        return $tiempo
    } catch {
        return -1
    } finally {
        if ($cliente -ne $null) { $cliente.Close() }
    }
}

# --- Auto-detect the default gateway (router/modem) ---
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
    $routerIP = Read-Host 'Router not detected. Enter its IP manually (e.g. 192.168.1.1)'
}

# ======================================================================
#  ISP first hop: the device right after the router, found via tracert.
#  NOTE: many intermediate routers reply to ping with lower priority than
#  the traffic they just forward, so these numbers can look inflated.
#  Used as a clue, not as confirmation by itself.
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

Write-Host 'Detecting your ISP first hop...' -ForegroundColor Cyan
$ispIP = Detectar-SaltoISP $routerIP $dnsCloudflare
if ($ispIP -eq $dnsCloudflare) { $ispIP = $null }

# ======================================================================
#  Connection type (Wi-Fi/cable), SSID and signal.
#  The SSID is obtained first via Get-NetConnectionProfile, which does NOT
#  require the location permission or admin rights. The signal (%) does
#  depend on netsh, which on Windows 10/11 requires the Location
#  permission and, on some machines, also running as administrator.
# ======================================================================
function Obtener-InfoConexion {
    $info = @{ Tipo = 'Not detected'; Detalle = ''; Senal = $null; SSID = ''; Cruda = @(); PermisoFaltante = $false }
    $ruta = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object -Property RouteMetric | Select-Object -First 1
    if (-not $ruta) { return $info }

    $adaptador = Get-NetAdapter -InterfaceIndex $ruta.InterfaceIndex -ErrorAction SilentlyContinue
    if (-not $adaptador) { return $info }

    if ($adaptador.PhysicalMediaType -match '802.11') {
        $info.Tipo = 'Wi-Fi'

        $perfil = Get-NetConnectionProfile -InterfaceIndex $ruta.InterfaceIndex -ErrorAction SilentlyContinue
        if ($perfil -and $perfil.Name) { $info.SSID = $perfil.Name }

        $salidaWifi = @(& netsh.exe wlan show interfaces 2>$null)
        $info.Cruda = $salidaWifi
        $textoCompleto = ($salidaWifi -join ' ')
        if ($textoCompleto -match '(?i)elevation|administrator|location permission|elevaci|administrador|permiso de ubicaci') {
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
                    $info.Detalle += ' (signal unavailable: netsh requires location permission/admin)'
                } else {
                    $info.Detalle += ' (signal unavailable)'
                }
            }
        } elseif ($info.PermisoFaltante) {
            $info.Detalle = 'SSID/signal unavailable: Windows requires the Location permission (and on this machine also running as administrator). See note in the log.'
        } elseif ($salidaWifi.Count -gt 0) {
            $info.Detalle = 'connected, but could not read the SSID (see DEBUG detail at the top of the log)'
        } else {
            $info.Detalle = 'could not read "netsh wlan show interfaces" (see DEBUG detail at the top of the log)'
        }
    } else {
        $info.Tipo = 'Cable (Ethernet)'
        $info.Detalle = $adaptador.Name
    }
    return $info
}

$conexion = Obtener-InfoConexion

# ======================================================================
#  Automatic LoL server detection (best effort, no admin required).
#  League of Legends match traffic runs over UDP, which the OS does not
#  expose a remote peer for via built-in tools without admin rights.
#  As a workaround, this looks at the TCP connections the League client
#  process currently has established and uses the first public IP found
#  as an approximation of the route to Riot's infrastructure. It is NOT
#  guaranteed to be the exact game server for the current match.
# ======================================================================
function Buscar-ProcesoLoL {
    $nombres = @('League of Legends', 'LeagueClientUx', 'LeagueClient')
    foreach ($n in $nombres) {
        $p = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($p) { return $p }
    }
    return $null
}

function Buscar-EndpointLoL {
    $procesos = Buscar-ProcesoLoL
    if (-not $procesos) { return $null }
    $procIds = $procesos | Select-Object -ExpandProperty Id
    $procNombre = ($procesos | Select-Object -First 1).ProcessName
    $conexiones = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Where-Object { $procIds -contains $_.OwningProcess } |
        Sort-Object -Property CreationTime -Descending
    foreach ($c in $conexiones) {
        $ip = $c.RemoteAddress
        if ([string]::IsNullOrEmpty($ip)) { continue }
        if ($ip -match '^(10\.|127\.|169\.254\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.|::1|fe80|0\.0\.0\.0)') { continue }
        return @{ IP = $ip; Port = $c.RemotePort; Proceso = $procNombre }
    }
    return $null
}

$lolIP = $null
$lolPort = $null
$statLolIcmp = $null
$statLolTcp = $null
$ultimoIntentoLol = (Get-Date).AddSeconds(-$U_SegundosReintentoLol)

# ======================================================================
#  Incremental statistics (samples aren't stored individually: counters
#  and a 0-1000ms histogram are used instead, so the panel stays fast)
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

# Meaningful spikes = absolute count AND share of samples:
#   3+ of 120ms+ (and 0.5%+)  or  5+ of 80ms+ (and 1%+)
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
#  Periodic pattern detection: looks at the intervals between events
#  (spikes >=120ms or losses) for a given target. If the intervals are
#  even (low coefficient of variation), it's more likely a recurring
#  task (Wi-Fi DFS scan, sync, backup) than random noise. Reads
#  $eventosTiempos directly (defined further below).
# ======================================================================
function Detectar-Periodicidad ($nombre) {
    if (-not $eventosTiempos.ContainsKey($nombre)) { return $null }
    $tiempos = $eventosTiempos[$nombre]
    if ($tiempos.Count -lt $U_MinEventosPeriodicidad) { return $null }

    $intervalos = @()
    for ($idx = 1; $idx -lt $tiempos.Count; $idx++) {
        $intervalos += ($tiempos[$idx] - $tiempos[$idx - 1]).TotalSeconds
    }

    $prom = ($intervalos | Measure-Object -Average).Average
    if ($prom -lt $U_MinSegundosPeriodo) { return $null }

    $sumaCuad = 0
    foreach ($iv in $intervalos) { $sumaCuad += [math]::Pow($iv - $prom, 2) }
    $desvio = [math]::Sqrt($sumaCuad / $intervalos.Count)

    $cv = 0
    if ($prom -gt 0) { $cv = $desvio / $prom }
    if ($cv -gt $U_MaxCoefVariacion) { return $null }

    return @{ Promedio = [math]::Round($prom, 0); Desvio = [math]::Round($desvio, 0); Cantidad = $tiempos.Count }
}

# ======================================================================
#  Diagnosis: OK / ATTENTION / PROBLEM (local, ISP, internet, or LoL)
#  Reads $baseline and $eventosTiempos directly (session state, below).
# ======================================================================
function Evaluar-Diagnostico ($r, $i, $c, $g, $cTcp, $gTcp, $lolI, $lolT, $conexion) {
    $probLocal = @()
    $probIsp = @()
    $avisos = @()

    # --- Router / local network ---
    if ($r.Perdida -ge 5) { $probLocal += "Router/modem: packet loss: $(Formato-Conteo $r.PerdidosN $r.Total)" }
    if ($r.Promedio -gt $U_RouterPromedioProblema) { $probLocal += "Router/modem: high average latency ($($r.Promedio) ms over $($r.Total) samples)" }
    if ($r.Jitter -gt $U_RouterJitterProblema) { $probLocal += "Router/modem: high jitter ($($r.Jitter) ms over $($r.Total) samples)" }
    if (Hay-Picos $r) { $probLocal += "Router/modem: frequent spikes ($(Formato-Conteo $r.Spikes120 $r.Total) at 120ms+, $(Formato-Conteo $r.Spikes80 $r.Total) at 80-119ms)" }

    if ($r.Maximo -ge $U_RouterMaxAviso) { $avisos += "Router/modem: max spike of $($r.Maximo) ms (over $($r.Total) total samples)" }
    if ($r.P95 -gt $U_RouterP95Aviso) { $avisos += "Router/modem: the slowest 5% of pings reach $($r.P95) ms or more (P95, over $($r.Total) samples)" }
    if ($r.P99 -gt $U_RouterP99Aviso) { $avisos += "Router/modem: the slowest 1% of pings reach $($r.P99) ms or more (P99, over $($r.Total) samples)" }
    if (($r.Jitter -gt $U_RouterJitterAviso) -and ($r.Jitter -le $U_RouterJitterProblema)) { $avisos += "Router/modem: jitter of $($r.Jitter) ms (a healthy local network is usually just a few ms)" }
    if (($r.Perdida -gt 0.5) -and ($r.Perdida -lt 5)) { $avisos += "Router/modem: packet loss: $(Formato-Conteo $r.PerdidosN $r.Total)" }

    if (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.Senal -ne $null) -and ($conexion.Senal -lt $U_SenalWifiBaja)) {
        $avisos += "Low Wi-Fi signal ($($conexion.Senal)%): could be causing intermittent spikes"
    }

    # --- Internet: ICMP (ping) + real TCP (port 443), compared separately ---
    $objetivosIcmp = @{ 'Cloudflare' = $c; 'Google' = $g }
    $objetivosTcp  = @{ 'Cloudflare' = $cTcp; 'Google' = $gTcp }
    foreach ($nom in @('Cloudflare', 'Google')) {
        $x = $objetivosIcmp[$nom]
        $xt = $objetivosTcp[$nom]

        $probIcmp = ($x.Perdida -gt 3) -or ($x.Promedio -gt 100) -or ($x.Jitter -gt 15) -or (Hay-Picos $x)
        $probTcp  = ($xt.Perdida -gt 3) -or ($xt.Promedio -gt $U_TcpPromedioProblema) -or ($xt.Jitter -gt $U_TcpJitterProblema) -or (Hay-Picos $xt)

        if ($probIcmp -and $probTcp) {
            $probIsp += "${nom}: problem confirmed by both ICMP and real TCP on port $puertoTcp (TCP: jitter $($xt.Jitter) ms, $(Formato-Conteo $xt.Spikes120 $xt.Total) spikes >=120ms, loss $($xt.Perdida)%)"
        }
        elseif ($probTcp -and (-not $probIcmp)) {
            $probIsp += "${nom}: the real TCP connection (port $puertoTcp) has problems even though the ICMP ping is clean -- this is more representative of the real traffic a game would generate (TCP: jitter $($xt.Jitter) ms, $(Formato-Conteo $xt.Spikes120 $xt.Total) spikes >=120ms)"
        }
        elseif ($probIcmp -and (-not $probTcp)) {
            $avisos += "${nom}: the ICMP ping shows anomalies but the real TCP connection (port $puertoTcp) is clean -- this could just be ICMP being deprioritized, not necessarily affecting the game"
        }

        if ($x.Maximo -ge 200) { $avisos += "${nom} (ICMP): max spike of $($x.Maximo) ms (over $($x.Total) total samples)" }
        if ($xt.Maximo -ge 250) { $avisos += "${nom} (TCP): connection took up to $($xt.Maximo) ms (over $($xt.Total) total attempts)" }
    }

    # --- LoL server (if detected during the session) ---
    if ($lolI -ne $null) {
        $probIcmpLol = ($lolI.Perdida -gt 3) -or ($lolI.Promedio -gt 100) -or ($lolI.Jitter -gt 15) -or (Hay-Picos $lolI)
        $probTcpLol  = ($lolT.Perdida -gt 3) -or ($lolT.Promedio -gt $U_TcpPromedioProblema) -or ($lolT.Jitter -gt $U_TcpJitterProblema) -or (Hay-Picos $lolT)

        if ($probIcmpLol -and $probTcpLol) {
            $probIsp += "LoL server ($lolIP): problem confirmed by both ICMP and real TCP (TCP: jitter $($lolT.Jitter) ms, $(Formato-Conteo $lolT.Spikes120 $lolT.Total) spikes >=120ms, loss $($lolT.Perdida)%)"
        }
        elseif ($probTcpLol -and (-not $probIcmpLol)) {
            $probIsp += "LoL server ($lolIP): the real TCP connection has problems even though ICMP looks clean (TCP: jitter $($lolT.Jitter) ms, $(Formato-Conteo $lolT.Spikes120 $lolT.Total) spikes >=120ms)"
        }
        elseif ($probIcmpLol -and (-not $probTcpLol)) {
            if ($lolI.Perdida -ge 100) {
                $avisos += "LoL server ($lolIP): does not answer ICMP at all (100% loss) -- common for game servers that block ping. The TCP line for this target is the more reliable signal."
            } else {
                $avisos += "LoL server ($lolIP): ICMP shows anomalies but the real TCP connection is clean -- this could just be ICMP being deprioritized"
            }
        }
    }

    # --- ISP hop: only corroborates if Internet ALREADY shows problems ---
    if (($i -ne $null) -and ($probIsp.Count -gt 0)) {
        if ((Hay-Picos $i) -or ($i.Perdida -gt 3) -or ($i.Jitter -gt 15)) {
            $probIsp += "Also confirmed at your ISP's first hop: jitter $($i.Jitter) ms, loss $(Formato-Conteo $i.PerdidosN $i.Total), $(Formato-Conteo $i.Spikes120 $i.Total) spikes at 120ms or more"
        }
    }

    # --- Clue: spikes only on the router, clean downstream, points to Wi-Fi/router, not the ISP ---
    if ((Hay-Picos $r) -and (-not (Hay-Picos $c)) -and (-not (Hay-Picos $g)) -and (($i -eq $null) -or (-not (Hay-Picos $i)))) {
        $avisos += "The router's spikes do NOT repeat at the ISP hop or at Cloudflare/Google, even though that traffic also passes through the router: this points to Wi-Fi or the router itself, not your provider"
    }

    # --- Personal baseline (first $U_MuestrasBaseline samples of the session) ---
    if ($baseline -ne $null) {
        $bR = $baseline.Router
        if (($bR.Promedio -gt 0) -and ($r.Promedio -ge ($bR.Promedio * $U_BaselineFactorProm)) -and (($r.Promedio - $bR.Promedio) -ge $U_BaselineMinDeltaProm)) {
            $avisos += "Router/modem: current average ($($r.Promedio) ms) is much higher than your own initial baseline ($($bR.Promedio) ms, first $U_MuestrasBaseline samples)"
        }
        $umbralJR = [math]::Max($bR.Jitter * $U_BaselineFactorJitter, $bR.Jitter + $U_BaselineMinDeltaJitter)
        if (($r.Jitter -ge $umbralJR) -and ($r.Jitter -gt 3)) {
            $avisos += "Router/modem: current jitter ($($r.Jitter) ms) is much higher than your baseline ($($bR.Jitter) ms)"
        }

        $bC = $baseline.Cloud
        if (($bC.Promedio -gt 0) -and ($c.Promedio -ge ($bC.Promedio * $U_BaselineFactorProm)) -and (($c.Promedio - $bC.Promedio) -ge $U_BaselineMinDeltaProm)) {
            $avisos += "Cloudflare: current average ($($c.Promedio) ms) is much higher than your baseline ($($bC.Promedio) ms)"
        }

        $bG = $baseline.Google
        if (($bG.Promedio -gt 0) -and ($g.Promedio -ge ($bG.Promedio * $U_BaselineFactorProm)) -and (($g.Promedio - $bG.Promedio) -ge $U_BaselineMinDeltaProm)) {
            $avisos += "Google: current average ($($g.Promedio) ms) is much higher than your baseline ($($bG.Promedio) ms)"
        }
    }

    # --- Periodic patterns in each target's events ---
    foreach ($nombreObjetivo in @('Router/Modem', 'ISP (1st hop)', 'Cloudflare', 'Google', 'LoL')) {
        $patron = Detectar-Periodicidad $nombreObjetivo
        if ($patron -ne $null) {
            $confianza = ''
            if ($patron.Cantidad -lt $U_MinEventosConfianza) { $confianza = ' (still few events, take this as a hint)' }
            $avisos += "${nombreObjetivo}: periodic pattern detected -- events roughly every $($patron.Promedio)s (+/- $($patron.Desvio)s, over $($patron.Cantidad) events)$confianza. Typical of scheduled tasks, syncing, or, on 5GHz Wi-Fi, a router DFS scan; check Windows Task Scheduler and, if on Wi-Fi, try a non-DFS 5GHz channel (36/40/44/48 or 149+)"
        }
    }

    $res = @{}
    if ($probLocal.Count -gt 0) {
        $res.Nivel = 'PROBLEMA'
        $res.Titulo = 'PROBLEM IN YOUR LOCAL NETWORK (HOME)'
        $res.Explicacion = 'There are failures on the path up to your router/modem, before reaching the Internet.'
        $res.Motivos = $probLocal + $avisos
        $res.Recomendacion = 'Repeat the test with an Ethernet cable: if it disappears, it was Wi-Fi; if it persists, suspect the router/modem.'
    }
    elseif ($probIsp.Count -gt 0) {
        $res.Nivel = 'PROBLEMA'
        $res.Titulo = 'PROBLEM WITH YOUR INTERNET PROVIDER (ISP)'
        $res.Explicacion = 'Your local network is clean up to the router, but the connection to the Internet is unstable.'
        $res.Motivos = $probIsp + $avisos
        $res.Recomendacion = 'Save this log and use it as evidence to file a complaint with your ISP.'
    }
    elseif ($avisos.Count -gt 0) {
        $res.Nivel = 'ATENCION'
        $res.Titulo = 'ATTENTION - ISOLATED ANOMALIES'
        $res.Explicacion = 'There is no sustained problem, but anomalies were recorded that can feel like lag spikes.'
        $res.Motivos = $avisos
        $res.Recomendacion = 'Let it run longer (ideally while you notice the lag) and repeat with an Ethernet cable to rule out Wi-Fi.'
    }
    else {
        $res.Nivel = 'OK'
        $res.Titulo = 'EVERYTHING LOOKS FINE IN WHAT WAS MEASURED'
        $res.Explicacion = 'No meaningful packet loss, low jitter, and no frequent or significant isolated spikes.'
        $res.Motivos = @()
        $res.Recomendacion = ''
    }
    return $res
}

# ======================================================================
#  Session state
# ======================================================================
$statR = Nuevo-Stat
$statI = $null
if ($ispIP -ne $null) { $statI = Nuevo-Stat }
$statC = Nuevo-Stat
$statG = Nuevo-Stat
$statCTcp = Nuevo-Stat
$statGTcp = Nuevo-Stat

$baseline = $null
function Capturar-Baseline {
    return @{
        Router = Resumen-Stats $statR
        Cloud  = Resumen-Stats $statC
        Google = Resumen-Stats $statG
    }
}

$eventos = New-Object System.Collections.ArrayList
$eventosTiempos = @{}
$colaTxt = New-Object System.Collections.ArrayList
$colaCol = New-Object System.Collections.ArrayList
$inicioSesion = Get-Date
$ultimoRegistroPeriodico = Get-Date
$anchoPrevio = 0
$altoPrevio = 0

function Registrar-Evento ($nombre, $t) {
    $marca = Get-Date
    $marcaTxt = $marca.ToString('HH:mm:ss')
    $esAnomalia = $false
    if ($t -lt 0) { [void]$eventos.Add("[$marcaTxt] ${nombre}: packet lost"); $esAnomalia = $true }
    elseif ($t -ge 120) { [void]$eventos.Add("[$marcaTxt] ${nombre}: $t ms"); $esAnomalia = $true }
    if ($eventos.Count -gt 30) { $eventos.RemoveAt(0) }

    if ($esAnomalia) {
        if (-not $eventosTiempos.ContainsKey($nombre)) { $eventosTiempos[$nombre] = New-Object System.Collections.ArrayList }
        [void]$eventosTiempos[$nombre].Add($marca)
        if ($eventosTiempos[$nombre].Count -gt 200) { $eventosTiempos[$nombre].RemoveAt(0) }
    }
}

# ======================================================================
#  Full report. $modo: 'parcial' (S key), 'final' (Q key or Ctrl+C),
#  or 'periodico' (saved only to the log every 1 minute, automatic)
# ======================================================================
function Construir-Reporte ($modo) {
    if ($statR.Total -eq 0) { return 'No measurements to show yet.' }

    $r = Resumen-Stats $statR
    $i = $null
    if ($statI -ne $null) { $i = Resumen-Stats $statI }
    $c = Resumen-Stats $statC
    $g = Resumen-Stats $statG
    $cTcp = Resumen-Stats $statCTcp
    $gTcp = Resumen-Stats $statGTcp
    $lolI = $null
    $lolT = $null
    if ($statLolIcmp -ne $null) {
        $lolI = Resumen-Stats $statLolIcmp
        $lolT = Resumen-Stats $statLolTcp
    }
    $dx = Evaluar-Diagnostico $r $i $c $g $cTcp $gTcp $lolI $lolT $conexion
    $duracion = Formato-Duracion $inicioSesion

    $titulo = 'PARTIAL CONNECTION SNAPSHOT'
    $tituloDiag = 'PARTIAL DIAGNOSIS (based on the data so far)'
    if ($modo -eq 'final') {
        $titulo = 'FINAL STATISTICAL SUMMARY'
        $tituloDiag = 'SYSTEM DIAGNOSIS'
    } elseif ($modo -eq 'periodico') {
        $titulo = 'AUTOMATIC PERIODIC LOG ENTRY (every 1 minute)'
        $tituloDiag = 'DIAGNOSIS AT THE TIME OF THIS LOG ENTRY'
    }

    $lineaConexion = "Connection: $($conexion.Tipo)"
    if ($conexion.Detalle -ne '') { $lineaConexion += " ($($conexion.Detalle)" + $(if ($conexion.Senal -ne $null) { ", Signal: $($conexion.Senal)%)" } else { ")" }) }

    $lineaBaseline = "Baseline: not captured yet (missing $([math]::Max(0, $U_MuestrasBaseline - $r.Total)) samples)"
    if ($baseline -ne $null) {
        $lineaBaseline = "Baseline (first $U_MuestrasBaseline samples): Router $($baseline.Router.Promedio) ms / jitter $($baseline.Router.Jitter) ms | Cloudflare $($baseline.Cloud.Promedio) ms | Google $($baseline.Google.Promedio) ms"
    }

    $lineaLol = 'LoL server: not detected yet (open a match; detection is retried automatically)'
    if ($lolIP -ne $null) { $lineaLol = "LoL server (approx.): $lolIP`:$lolPort" }

    $reporte = @"

=======================================================================
                   $titulo
=======================================================================
Session duration: $duracion
$lineaConexion
$lineaBaseline
$lineaLol
[Router/Modem ($routerIP)] - $($r.Total) total samples
- Average: $($r.Promedio) ms | Median: $($r.Mediana) ms | P95: $($r.P95) ms | P99: $($r.P99) ms | Max: $($r.Maximo) ms
- Jitter: $($r.Jitter) ms | Spikes 80-119ms: $($r.Spikes80) | Spikes >=120ms: $($r.Spikes120) | Loss: $($r.Perdida)% ($($r.PerdidosN) of $($r.Total))

"@

    if ($i -ne $null) {
        $reporte += @"
[ISP first hop ($ispIP)] - $($i.Total) total samples (reference only, can look inflated, see note below)
- Average: $($i.Promedio) ms | Median: $($i.Mediana) ms | P95: $($i.P95) ms | P99: $($i.P99) ms | Max: $($i.Maximo) ms
- Jitter: $($i.Jitter) ms | Spikes 80-119ms: $($i.Spikes80) | Spikes >=120ms: $($i.Spikes120) | Loss: $($i.Perdida)% ($($i.PerdidosN) of $($i.Total))

"@
    } else {
        $reporte += "[ISP first hop] Could not auto-detect it (tracert got no reply on the first hops).`r`n`r`n"
    }

    $reporte += @"
[Cloudflare ICMP ($dnsCloudflare)] - $($c.Total) total samples
- Average: $($c.Promedio) ms | Median: $($c.Mediana) ms | P95: $($c.P95) ms | P99: $($c.P99) ms | Max: $($c.Maximo) ms
- Jitter: $($c.Jitter) ms | Spikes 80-119ms: $($c.Spikes80) | Spikes >=120ms: $($c.Spikes120) | Loss: $($c.Perdida)% ($($c.PerdidosN) of $($c.Total))

[Cloudflare TCP:$puertoTcp (real connection)] - $($cTcp.Total) total attempts
- Average: $($cTcp.Promedio) ms | Median: $($cTcp.Mediana) ms | P95: $($cTcp.P95) ms | P99: $($cTcp.P99) ms | Max: $($cTcp.Maximo) ms
- Jitter: $($cTcp.Jitter) ms | Spikes 80-119ms: $($cTcp.Spikes80) | Spikes >=120ms: $($cTcp.Spikes120) | Failed: $($cTcp.Perdida)% ($($cTcp.PerdidosN) of $($cTcp.Total))

[Google ICMP ($dnsGoogle)] - $($g.Total) total samples
- Average: $($g.Promedio) ms | Median: $($g.Mediana) ms | P95: $($g.P95) ms | P99: $($g.P99) ms | Max: $($g.Maximo) ms
- Jitter: $($g.Jitter) ms | Spikes 80-119ms: $($g.Spikes80) | Spikes >=120ms: $($g.Spikes120) | Loss: $($g.Perdida)% ($($g.PerdidosN) of $($g.Total))

[Google TCP:$puertoTcp (real connection)] - $($gTcp.Total) total attempts
- Average: $($gTcp.Promedio) ms | Median: $($gTcp.Mediana) ms | P95: $($gTcp.P95) ms | P99: $($gTcp.P99) ms | Max: $($gTcp.Maximo) ms
- Jitter: $($gTcp.Jitter) ms | Spikes 80-119ms: $($gTcp.Spikes80) | Spikes >=120ms: $($gTcp.Spikes120) | Failed: $($gTcp.Perdida)% ($($gTcp.PerdidosN) of $($gTcp.Total))
"@

    if ($lolI -ne $null) {
        $reporte += @"

[LoL server ICMP ($lolIP)] - $($lolI.Total) total samples (approximate target, see note below)
- Average: $($lolI.Promedio) ms | Median: $($lolI.Mediana) ms | P95: $($lolI.P95) ms | P99: $($lolI.P99) ms | Max: $($lolI.Maximo) ms
- Jitter: $($lolI.Jitter) ms | Spikes 80-119ms: $($lolI.Spikes80) | Spikes >=120ms: $($lolI.Spikes120) | Loss: $($lolI.Perdida)% ($($lolI.PerdidosN) of $($lolI.Total))

[LoL server TCP:$lolPort (real connection)] - $($lolT.Total) total attempts
- Average: $($lolT.Promedio) ms | Median: $($lolT.Mediana) ms | P95: $($lolT.P95) ms | P99: $($lolT.P99) ms | Max: $($lolT.Maximo) ms
- Jitter: $($lolT.Jitter) ms | Spikes 80-119ms: $($lolT.Spikes80) | Spikes >=120ms: $($lolT.Spikes120) | Failed: $($lolT.Perdida)% ($($lolT.PerdidosN) of $($lolT.Total))
"@
    }

    $reporte += "`r`n=======================================================================`r`n"
    $reporte += "                   $tituloDiag`r`n"
    $reporte += "=======================================================================`r`n"
    $reporte += "-> $($dx.Titulo)`r`n"
    $reporte += "   $($dx.Explicacion)`r`n"
    foreach ($m in $dx.Motivos) { $reporte += "   - $m`r`n" }
    if ($dx.Recomendacion -ne '') { $reporte += "   RECOMMENDATION: $($dx.Recomendacion)`r`n" }
    if ($r.Total -lt 30) { $reporte += "   (Few samples so far: let it run longer before trusting this result.)`r`n" }
    if ($i -ne $null) { $reporte += "   ISP NOTE: the 'ISP first hop' is an intermediate device; many routers reply to ping with lower priority than the traffic they just forward, so its numbers can look inflated. Used only as an extra clue.`r`n" }
    $reporte += "   TCP NOTE: the 'TCP:$puertoTcp' lines measure real connection time (TCP protocol), not ICMP. They are closer to the real traffic of an app or game than a classic ping, which some intermediate devices treat differently.`r`n"
    if ($lolIP -ne $null) { $reporte += "   LOL NOTE: the LoL server IP was detected from an active TCP connection made by the League client (League of Legends match traffic itself runs over UDP, which cannot be inspected this way without admin rights). It is an approximation of the route to Riot's infrastructure, not guaranteed to be the exact same server used in a given match.`r`n" }

    if ($eventos.Count -gt 0) {
        $reporte += "`r`nLATEST EVENTS (spikes of 120 ms or more / packet loss, ICMP):`r`n"
        foreach ($e in $eventos) { $reporte += "  $e`r`n" }
    }

    $reporte += "=======================================================================`r`n"
    return $reporte
}

# ======================================================================
#  Live panel: always redraws from row 0 without clearing the screen
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
    $cTcp = Resumen-Stats $statCTcp
    $gTcp = Resumen-Stats $statGTcp
    $lolI = $null
    $lolT = $null
    if ($statLolIcmp -ne $null) {
        $lolI = Resumen-Stats $statLolIcmp
        $lolT = Resumen-Stats $statLolTcp
    }
    $dx = Evaluar-Diagnostico $r $i $c $g $cTcp $gTcp $lolI $lolT $conexion

    $largoSep = [math]::Min(78, $w - 1)
    $sep = '=' * $largoSep
    $transcurrido = Formato-Duracion $inicioSesion

    $lineaConexion = " Connection: $($conexion.Tipo)"
    if ($conexion.Detalle -ne '') { $lineaConexion += " ($($conexion.Detalle)" + $(if ($conexion.Senal -ne $null) { ", Signal: $($conexion.Senal)%)" } else { ")" }) }

    $lolEstado = 'not detected yet'
    if ($lolIP -ne $null) { $lolEstado = "$lolIP`:$lolPort" }

    $lineas = New-Object System.Collections.ArrayList
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = '  LIVE LATENCY AND JITTER DIAGNOSTICS (GAMING)'; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = " Router/Modem: $routerIP | ISP: $(if ($ispIP -ne $null) { $ispIP } else { 'not detected' }) | Cloudflare: $dnsCloudflare | Google: $dnsGoogle"; C = 'Gray' })
    [void]$lineas.Add(@{ T = " LoL server: $lolEstado"; C = 'Gray' })
    [void]$lineas.Add(@{ T = $lineaConexion; C = 'Gray' })
    [void]$lineas.Add(@{ T = " Duration: $transcurrido | Log entry every $($U_SegundosRegistroPeriodico)s | Baseline: $(if ($baseline -ne $null) { 'ready' } else { "in $([math]::Max(0, $U_MuestrasBaseline - $r.Total))" })"; C = 'Gray' })
    [void]$lineas.Add(@{ T = " [S] Report and pause  [C] Continue  [Q] or Ctrl+C: Finish and view diagnosis"; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })

    $fmt = '{0,-16}{1,7}{2,6}{3,5}{4,5}{5,5}{6,6}{7,6}{8,7}{9,6}{10,7}'
    $enc = $fmt -f 'TARGET', 'SAMP', 'AVG', 'MED', 'P95', 'P99', 'MAX', 'JIT', '80-119', '>=120', 'LOSS'
    $filaR = $fmt -f 'Router/Modem', $r.Total, $r.Promedio, $r.Mediana, $r.P95, $r.P99, $r.Maximo, $r.Jitter, $r.Spikes80, $r.Spikes120, "$($r.Perdida)%"
    $filaC = $fmt -f 'Cloudflare-ICMP', $c.Total, $c.Promedio, $c.Mediana, $c.P95, $c.P99, $c.Maximo, $c.Jitter, $c.Spikes80, $c.Spikes120, "$($c.Perdida)%"
    $filaCTcp = $fmt -f 'Cloudflare-TCP', $cTcp.Total, $cTcp.Promedio, $cTcp.Mediana, $cTcp.P95, $cTcp.P99, $cTcp.Maximo, $cTcp.Jitter, $cTcp.Spikes80, $cTcp.Spikes120, "$($cTcp.Perdida)%"
    $filaG = $fmt -f 'Google-ICMP', $g.Total, $g.Promedio, $g.Mediana, $g.P95, $g.P99, $g.Maximo, $g.Jitter, $g.Spikes80, $g.Spikes120, "$($g.Perdida)%"
    $filaGTcp = $fmt -f 'Google-TCP', $gTcp.Total, $gTcp.Promedio, $gTcp.Mediana, $gTcp.P95, $gTcp.P99, $gTcp.Maximo, $gTcp.Jitter, $gTcp.Spikes80, $gTcp.Spikes120, "$($gTcp.Perdida)%"
    [void]$lineas.Add(@{ T = $enc; C = 'Cyan' })
    [void]$lineas.Add(@{ T = $filaR; C = 'White' })
    if ($i -ne $null) {
        $filaI = $fmt -f 'ISP (1st hop)', $i.Total, $i.Promedio, $i.Mediana, $i.P95, $i.P99, $i.Maximo, $i.Jitter, $i.Spikes80, $i.Spikes120, "$($i.Perdida)%"
        [void]$lineas.Add(@{ T = $filaI; C = 'DarkGray' })
    }
    [void]$lineas.Add(@{ T = $filaC; C = 'White' })
    [void]$lineas.Add(@{ T = $filaCTcp; C = 'DarkGray' })
    [void]$lineas.Add(@{ T = $filaG; C = 'White' })
    [void]$lineas.Add(@{ T = $filaGTcp; C = 'DarkGray' })
    if ($lolI -ne $null) {
        $filaLolI = $fmt -f 'LoL-ICMP', $lolI.Total, $lolI.Promedio, $lolI.Mediana, $lolI.P95, $lolI.P99, $lolI.Maximo, $lolI.Jitter, $lolI.Spikes80, $lolI.Spikes120, "$($lolI.Perdida)%"
        $filaLolT = $fmt -f 'LoL-TCP', $lolT.Total, $lolT.Promedio, $lolT.Mediana, $lolT.P95, $lolT.P99, $lolT.Maximo, $lolT.Jitter, $lolT.Spikes80, $lolT.Spikes120, "$($lolT.Perdida)%"
        [void]$lineas.Add(@{ T = $filaLolI; C = 'White' })
        [void]$lineas.Add(@{ T = $filaLolT; C = 'DarkGray' })
    }

    $colorEstado = 'Green'
    if ($dx.Nivel -eq 'ATENCION') { $colorEstado = 'Yellow' }
    if ($dx.Nivel -eq 'PROBLEMA') { $colorEstado = 'Red' }
    [void]$lineas.Add(@{ T = " STATUS: $($dx.Titulo)"; C = $colorEstado })
    for ($k = 0; $k -lt 3; $k++) {
        $motivo = ''
        if ($k -lt $dx.Motivos.Count) { $motivo = "   - " + $dx.Motivos[$k] }
        [void]$lineas.Add(@{ T = $motivo; C = 'Gray' })
    }

    [void]$lineas.Add(@{ T = $sep; C = 'Cyan' })
    [void]$lineas.Add(@{ T = ' LATEST ICMP MEASUREMENTS (the full history is in the log)'; C = 'Cyan' })

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

# --- Detects whether a read key is Ctrl+C ---
function Es-CtrlC ($key) {
    return (($key.Modifiers -band [ConsoleModifiers]::Control) -and ($key.Key -eq 'C'))
}

# ======================================================================
#  Pause with full report. Returns $true if the user chose to quit.
# ======================================================================
function Modo-Snapshot {
    Clear-Host
    $txt = Construir-Reporte 'parcial'
    Write-Host $txt -ForegroundColor Yellow
    Add-Content -Path $logFile -Value $txt
    Write-Host ">>> MONITORING PAUSED. Press 'C' to continue, or 'Q'/Ctrl+C to finish <<<" -ForegroundColor Cyan
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
#  Main loop
# ======================================================================
Add-Content -Path $logFile -Value ("`r`n--- NEW DIAGNOSTIC SESSION: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ---')
Add-Content -Path $logFile -Value ("Connection: $($conexion.Tipo) | ISP detected: $(if ($ispIP -ne $null) { $ispIP } else { 'no' })")

if (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.SSID -eq '') -and ($conexion.PermisoFaltante)) {
    Add-Content -Path $logFile -Value 'NOTE: could not read SSID/signal via netsh. Windows requires the Location permission (Settings > Privacy & security > Location), and on this machine it also requires running the .bat as administrator. The SSID via Get-NetConnectionProfile could not be obtained either in this session.'
} elseif (($conexion.Tipo -eq 'Wi-Fi') -and ($conexion.SSID -eq '')) {
    Add-Content -Path $logFile -Value "DEBUG - raw output of 'netsh wlan show interfaces' (to help tune SSID detection):"
    if ($conexion.Cruda.Count -eq 0) {
        Add-Content -Path $logFile -Value '  (the command returned no lines)'
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
    $tCloudTcp = Get-TcpConnectTime $dnsCloudflare $puertoTcp 1000
    $tGoogleTcp = Get-TcpConnectTime $dnsGoogle $puertoTcp 1000

    Agregar-Muestra $statR $tRouter
    if ($statI -ne $null) { Agregar-Muestra $statI $tIsp }
    Agregar-Muestra $statC $tCloud
    Agregar-Muestra $statG $tGoogle
    Agregar-Muestra $statCTcp $tCloudTcp
    Agregar-Muestra $statGTcp $tGoogleTcp

    Registrar-Evento 'Router/Modem' $tRouter
    if ($ispIP -ne $null) { Registrar-Evento 'ISP (1st hop)' $tIsp }
    Registrar-Evento 'Cloudflare' $tCloud
    Registrar-Evento 'Google' $tGoogle

    # --- Try to detect the LoL client while not found yet ---
    if (($lolIP -eq $null) -and (((Get-Date) - $ultimoIntentoLol).TotalSeconds -ge $U_SegundosReintentoLol)) {
        $ultimoIntentoLol = Get-Date
        $encontrado = Buscar-EndpointLoL
        if ($encontrado -ne $null) {
            $lolIP = $encontrado.IP
            $lolPort = $encontrado.Port
            $statLolIcmp = Nuevo-Stat
            $statLolTcp = Nuevo-Stat
            Add-Content -Path $logFile -Value "LoL server candidate detected: $lolIP`:$lolPort (from process $($encontrado.Proceso)). NOTE: League of Legends match traffic runs over UDP, which cannot be inspected without admin rights; this is a TCP connection made by the client, used as an approximation of the route to Riot's servers, not necessarily the exact same game server."
        }
    }

    $tLolIcmp = -1
    $tLolTcp = -1
    if ($lolIP -ne $null) {
        $tLolIcmp = Get-PingTime $lolIP
        $tLolTcp = Get-TcpConnectTime $lolIP $lolPort 1000
        Agregar-Muestra $statLolIcmp $tLolIcmp
        Agregar-Muestra $statLolTcp $tLolTcp
        Registrar-Evento 'LoL' $tLolIcmp
    }

    if (($baseline -eq $null) -and ($statR.Total -ge $U_MuestrasBaseline)) {
        $baseline = Capturar-Baseline
    }

    $sRouter = 'Lost'
    if ($tRouter -ge 0) { $sRouter = "$tRouter ms" }
    $sCloud = 'Lost'
    if ($tCloud -ge 0) { $sCloud = "$tCloud ms" }
    $sGoogle = 'Lost'
    if ($tGoogle -ge 0) { $sGoogle = "$tGoogle ms" }

    $cuerpo = 'Router: ' + $sRouter.PadRight(8) + ' | Cloudflare: ' + $sCloud.PadRight(8) + ' | Google: ' + $sGoogle.PadRight(8)
    if ($ispIP -ne $null) {
        $sIsp = 'Lost'
        if ($tIsp -ge 0) { $sIsp = "$tIsp ms" }
        $cuerpo = 'Router: ' + $sRouter.PadRight(8) + ' | ISP: ' + $sIsp.PadRight(8) + ' | Cloudflare: ' + $sCloud.PadRight(8) + ' | Google: ' + $sGoogle.PadRight(8)
    }
    $lineaConsola = '[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $cuerpo
    $lineaLog = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $cuerpo

    # Red: something lost, router >=25ms, or Internet >=120ms. Yellow: router >=10ms or Internet >=80ms
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

    # --- Automatic periodic log entry (does not interrupt the live panel) ---
    if (((Get-Date) - $ultimoRegistroPeriodico).TotalSeconds -ge $U_SegundosRegistroPeriodico) {
        $txtPeriodico = Construir-Reporte 'periodico'
        Add-Content -Path $logFile -Value $txtPeriodico
        $ultimoRegistroPeriodico = Get-Date
    }

    Dibujar-Panel

    # 1-second wait, checking for a keypress every 100 ms
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

# --- Final summary ---
[Console]::TreatControlCAsInput = $false
[Console]::CursorVisible = $true
Clear-Host
$final = Construir-Reporte 'final'
Write-Host $final -ForegroundColor Yellow
Add-Content -Path $logFile -Value $final
Write-Host "Log saved to: $logFile" -ForegroundColor Cyan
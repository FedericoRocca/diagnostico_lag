Script cmd (Windows 11 tested) que utiliza Powershell para intentar medir y entender donde se producen los retrasos, microcortes y diagnosticar el estado general de la red. Genera un log en la ubicación de ejecución.

Apuntado al gaming

=======================================================================
  DIAGNOSTICO DE LATENCIA Y JITTER EN VIVO (GAMING)
=======================================================================
 Router/Modem: 192.168.1.1 | Cloudflare: 1.1.1.1 | Google: 8.8.8.8
 Tiempo: 00:06:32 | Log: Path de ejecución\registro_latencia.txt
 [S] Reporte completo y pausa  [C] Continuar  [Q] Finalizar y ver diagnostico
=======================================================================
OBJETIVO        MUEST  PROM  MED  P95  P99   MAX   JIT 80-119 >=120   PERD
Router/Modem      320   3.1    2    8   19    62   2.5      0     0     0%
Cloudflare        320   6.9    6   10   26    39     3      0     0     0%
Google            320   7.9    8   11   26    50   2.9      0     0     0%
 ESTADO: TODO EN ORDEN EN LO MEDIDO



=======================================================================
 ULTIMAS MEDICIONES (el historial completo esta en el log)
[22:36:11] Router: 15 ms    | Cloudflare: 7 ms     | Google: 10 ms
[22:36:13] Router: 5 ms     | Cloudflare: 7 ms     | Google: 36 ms
[22:36:14] Router: 2 ms     | Cloudflare: 6 ms     | Google: 7 ms
[22:36:15] Router: 3 ms     | Cloudflare: 6 ms     | Google: 8 ms
[22:36:16] Router: 2 ms     | Cloudflare: 4 ms     | Google: 9 ms
[22:36:18] Router: 3 ms     | Cloudflare: 6 ms     | Google: 9 ms
[22:36:19] Router: 2 ms     | Cloudflare: 8 ms     | Google: 9 ms
[22:36:20] Router: 2 ms     | Cloudflare: 10 ms    | Google: 6 ms
[22:36:21] Router: 3 ms     | Cloudflare: 7 ms     | Google: 4 ms
[22:36:23] Router: 4 ms     | Cloudflare: 5 ms     | Google: 4 ms
[22:36:24] Router: 1 ms     | Cloudflare: 4 ms     | Google: 8 ms
[22:36:25] Router: 2 ms     | Cloudflare: 7 ms     | Google: 7 ms
[22:36:26] Router: 4 ms     | Cloudflare: 10 ms    | Google: 14 ms
[22:36:28] Router: 1 ms     | Cloudflare: 8 ms     | Google: 5 ms
[22:36:29] Router: 1 ms     | Cloudflare: 6 ms     | Google: 5 ms
[22:36:30] Router: 2 ms     | Cloudflare: 8 ms     | Google: 9 ms
[22:36:31] Router: 2 ms     | Cloudflare: 4 ms     | Google: 9 ms
[22:36:32] Router: 1 ms     | Cloudflare: 5 ms     | Google: 5 ms
[22:36:33] Router: 1 ms     | Cloudflare: 4 ms     | Google: 9 ms
[22:36:35] Router: 1 ms     | Cloudflare: 9 ms     | Google: 9 ms
[22:36:36] Router: 2 ms     | Cloudflare: 7 ms     | Google: 9 ms
[22:36:37] Router: 1 ms     | Cloudflare: 8 ms     | Google: 5 ms
[22:36:38] Router: 1 ms     | Cloudflare: 8 ms     | Google: 8 ms

Script cmd (Windows 11 tested) que utiliza Powershell para intentar medir y entender donde se producen los retrasos, microcortes y diagnosticar el estado general de la red. Genera un log en la ubicación de ejecución.

Apuntado al LoL

==============================================================================
  LIVE LATENCY AND JITTER DIAGNOSTICS (GAMING)
==============================================================================
 Router/Modem: 192.168.50.1 | ISP: 192.168.1.1 | Cloudflare: 1.1.1.1 | Google: 8.8.8.8
 LoL server: not detected yet
 Connection: Wi-Fi (SSID: XXX (signal unavailable: netsh requires location permission/admin))
 Duration: 00:00:15 | Log entry every 60s | Baseline: in 47
 [S] Report and pause  [C] Continue  [Q] or Ctrl+C: Finish and view diagnosis
==============================================================================
TARGET             SAMP   AVG  MED  P95  P99   MAX   JIT 80-119 >=120   LOSS
Router/Modem         13   1.8    1    7    7     7     1      0     0     0%
ISP (1st hop)        13   2.4    2    4    4     4   0.7      0     0     0%
Cloudflare-ICMP      13   6.6    7    9    9     9   2.5      0     0     0%
Cloudflare-TCP       13   9.8    9   18   18    18   2.1      0     0     0%
Google-ICMP          13   7.9    8   10   10    10   2.7      0     0     0%
Google-TCP           13   9.4    9   11   11    11   0.8      0     0     0%
 STATUS: EVERYTHING LOOKS FINE IN WHAT WAS MEASURED



==============================================================================
 LATEST ICMP MEASUREMENTS (the full history is in the log)
[19:25:36] Router: 7 ms     | ISP: 4 ms     | Cloudflare: 9 ms     | Google: 10 ms
[19:25:38] Router: 1 ms     | ISP: 3 ms     | Cloudflare: 9 ms     | Google: 8 ms
[19:25:39] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 6 ms     | Google: 8 ms
[19:25:40] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 8 ms     | Google: 5 ms
[19:25:42] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 4 ms     | Google: 9 ms
[19:25:43] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 8 ms     | Google: 9 ms
[19:25:44] Router: 2 ms     | ISP: 3 ms     | Cloudflare: 5 ms     | Google: 8 ms
[19:25:46] Router: 3 ms     | ISP: 3 ms     | Cloudflare: 8 ms     | Google: 10 ms
[19:25:47] Router: 1 ms     | ISP: 1 ms     | Cloudflare: 5 ms     | Google: 4 ms
[19:25:48] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 4 ms     | Google: 8 ms
[19:25:49] Router: 1 ms     | ISP: 2 ms     | Cloudflare: 7 ms     | Google: 9 ms
[19:25:50] Router: 2 ms     | ISP: 3 ms     | Cloudflare: 8 ms     | Google: 5 ms

# Diagnóstico de red para gaming

Aplicación gráfica para Windows que monitorea latencia y jitter, registra cortes y genera un informe con indicios de si el problema está en la red local o en la conexión a Internet.

## Ejecutar desde el código fuente

Requisitos: Windows y .NET 8 SDK o posterior.

```powershell
dotnet run --project .\DiagnosticoLag\DiagnosticoLag.csproj
```

También se puede abrir `DiagnosticoLag\DiagnosticoLag.csproj` desde Visual Studio y ejecutar el proyecto.

## Compilar

```powershell
dotnet build .\DiagnosticoLag\DiagnosticoLag.csproj -c Release
```

El registro se guarda en `%LocalAppData%\DiagnosticoLag\registro_latencia.txt`. Desde la aplicación se puede abrir el archivo o generar informes parciales y finales.

## Qué mide

- Ping ICMP al router, al primer salto del ISP cuando responde a traceroute y a Cloudflare y Google.
- Tiempo de establecimiento TCP en el puerto 443 a Cloudflare y Google.
- Promedio, mediana, percentiles 95/99, máximo, jitter, picos y pérdida de paquetes, además de un gráfico en vivo.
- El gráfico muestra el tiempo transcurrido desde el inicio de la sesión hasta la hora actual, con marcas de tiempo e información del destino y la latencia al pasar el cursor por una línea.
- Detección opcional de una conexión TCP del cliente de League of Legends. Es solo un destino aproximado de la infraestructura de Riot: el tráfico de partida usa UDP y puede ir a otro servidor.
- Diagnóstico por umbrales, comparación con la línea base inicial y detección de eventos periódicos.

El primer salto del ISP y las respuestas ICMP son indicios, no pruebas concluyentes: algunos routers limitan o filtran ICMP. La app no requiere privilegios de administrador; Windows puede restringir la lectura del SSID o de la señal Wi-Fi.

`diagnostico_lag.bat` se conserva como versión anterior y referencia del comportamiento original.

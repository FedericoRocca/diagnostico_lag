# Lagnostics

Aplicación de escritorio para Windows que ayuda a diagnosticar latencia, pérdida de paquetes y microcortes durante sesiones de gaming. Mide el router, el primer salto del proveedor y destinos de Internet, y muestra estadísticas y evolución en tiempo real.

![Captura de Lagnostics](https://github.com/user-attachments/assets/5163a2b7-5347-4274-a805-24a510de1f00)

## Configuración

Las preferencias se guardan en `%LOCALAPPDATA%\DiagnosticoLag\configuracion.json` y se cargan al iniciar la aplicación. Desde **Configuración** se pueden ajustar:

- Destinos externos, intervalo de muestreo e interfaz de red.
- Perfiles de juegos editables. Cada perfil define el nombre y los nombres de procesos que se detectan; se pueden activar o desactivar. Si un perfil está desactivado, no agrega mediciones a la grilla, al gráfico, a los informes ni a los CSV.
- En esta primera implementación, si se observa una conexión TCP establecida de un proceso configurado, se sondea su IP remota con ICMP como referencia aproximada de ruta. El endpoint puede ser auxiliar y no el servidor de la partida. No se presenta como ping real del juego; muchos juegos usan UDP y protocolos propios.
- Carpetas independientes para los registros de sesión y los archivos CSV; también se usan como ubicación inicial al exportar.

Los registros y CSV se crean en las carpetas configuradas. Los informes se pueden exportar desde la aplicación.

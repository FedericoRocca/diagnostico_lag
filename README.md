# Lagnostics

Aplicación de escritorio para Windows que ayuda a diagnosticar latencia, pérdida de paquetes y microcortes durante sesiones de gaming. Mide el router, el primer salto del proveedor y destinos de Internet, y muestra estadísticas y evolución en tiempo real.

![Captura de Lagnostics](https://github.com/user-attachments/assets/5163a2b7-5347-4274-a805-24a510de1f00)

## Configuración

Las preferencias se guardan en `%LOCALAPPDATA%\DiagnosticoLag\configuracion.json` y se cargan al iniciar la aplicación. Desde **Configuración** se pueden ajustar:

- En el primer inicio, el idioma se elige según el idioma de interfaz del sistema operativo: español si está configurado en español e inglés en los demás casos o si no se puede detectar. La elección queda guardada y se puede cambiar desde Configuración; se utiliza también en diagnósticos, informes y CSV.
- Destinos externos, intervalo de muestreo e interfaz de red.
- Perfiles de juegos editables. Cada perfil define el nombre y los nombres de procesos que se detectan; se pueden activar o desactivar. Si un perfil está desactivado, no agrega mediciones a la grilla, al gráfico, a los informes ni a los CSV.
- En esta primera implementación, si se observa una conexión TCP establecida de un proceso configurado, se sondea su IP remota con ICMP como referencia aproximada de ruta. El endpoint puede ser auxiliar y no el servidor de la partida. No se presenta como ping real del juego; muchos juegos usan UDP y protocolos propios.
- Carpetas independientes para los registros de sesión y los archivos CSV; también se usan como ubicación inicial al exportar.

Los registros y CSV se crean en las carpetas configuradas. Los informes se pueden exportar desde la aplicación.

Cada encabezado de la grilla incluye un indicador **(?)**: al pasar el cursor se muestra una breve descripción y al hacer clic se abre la explicación completa de esa métrica.

## Distribución para Windows

Las versiones publicadas están en [GitHub Releases](https://github.com/FedericoRocca/diagnostico_lag/releases). Cada release incluye el ZIP portable x64 y un instalador EXE autocontenido para Windows x64. El instalador se instala por usuario en `%LOCALAPPDATA%\Programs\Lagnostics`, agrega un acceso directo al menú Inicio y no requiere privilegios de administrador.

Para distribuir una versión en Microsoft Store como aplicación EXE/MSI, el ejecutable principal y el instalador deben estar firmados con Authenticode por un certificado de firma de código válido. La Store no firma los instaladores EXE/MSI en nombre del publicador. El flujo de GitHub Actions firma ambos archivos y verifica sus firmas usando los secretos `WINDOWS_SIGNING_CERT_BASE64` (certificado PFX codificado en Base64) y `WINDOWS_SIGNING_CERT_PASSWORD` (contraseña del PFX); configura ambos en **Settings → Secrets and variables → Actions** antes de crear una release. No subas el PFX ni su contraseña al repositorio. La release `v1.0.1` se publicó antes de configurar firma y **no debe enviarse a la Store**.

Una vez que publiques una release firmada, usa su URL HTTPS versionada en Partner Center, selecciona arquitectura **x64** y tipo **EXE**. Los modificadores de instalación silenciosa son `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`; desmarca la opción que indica que el instalador silencioso no requiere modificadores. Por ejemplo, para `v1.0.2`:

`https://github.com/FedericoRocca/diagnostico_lag/releases/download/v1.0.2/DiagnosticoLag-1.0.2-win-x64-setup.exe`

GitHub sirve el instalador como archivo descargable público. Para una actualización posterior, publica un nuevo tag y proporciona a Partner Center la nueva URL versionada.

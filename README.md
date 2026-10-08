# Lagnostics

[![Automated tests](https://github.com/FedericoRocca/diagnostico_lag/actions/workflows/tests.yml/badge.svg)](https://github.com/FedericoRocca/diagnostico_lag/actions/workflows/tests.yml)

Aplicación de escritorio para Windows que ayuda a diagnosticar latencia, pérdida de paquetes y microcortes durante sesiones de gaming. Mide el router, el primer salto del proveedor y destinos de Internet, y muestra estadísticas y evolución en tiempo real.

Al finalizar una sesión, muestra gráficos comparativos de latencia promedio/P95 y pérdida o fallos de conexión, junto con explicaciones de los hallazgos. El informe final de texto también se conserva en el registro y se puede exportar.

<img width="1920" height="1020" alt="4" src="https://github.com/user-attachments/assets/a0b307ea-11e8-4908-add1-88fa9107759d" />
<img width="1920" height="1020" alt="3" src="https://github.com/user-attachments/assets/ebddf7c5-da48-4853-bdfd-26c01a4d4207" />
<img width="1920" height="1020" alt="2" src="https://github.com/user-attachments/assets/4168a30f-e465-4449-9868-232d22d8dc98" />
<img width="1920" height="1020" alt="1" src="https://github.com/user-attachments/assets/e8e95b88-1da5-461b-b974-4db2a0296c4e" />
## Configuración

Las preferencias se guardan en `%LOCALAPPDATA%\DiagnosticoLag\configuracion.json` y se cargan al iniciar la aplicación. Desde **Configuración** se pueden ajustar:

- En el primer inicio, el idioma se elige según el idioma de interfaz del sistema operativo: español si está configurado en español e inglés en los demás casos o si no se puede detectar. La elección queda guardada y se puede cambiar desde Configuración; se utiliza también en diagnósticos, informes y CSV.
- Destinos externos, intervalo de muestreo e interfaz de red.
- Perfiles de juegos editables. Cada perfil define el nombre y los nombres de procesos que se detectan; se pueden activar o desactivar. Si un perfil está desactivado, no agrega mediciones a la grilla, al gráfico, a los informes ni a los CSV.
- En esta primera implementación, si se observa una conexión TCP establecida de un proceso configurado, se sondea su IP remota con ICMP como referencia aproximada de ruta. El endpoint puede ser auxiliar y no el servidor de la partida. No se presenta como ping real del juego; muchos juegos usan UDP y protocolos propios.
- Carpetas independientes para los registros de sesión y los archivos CSV; también se usan como ubicación inicial al exportar.

Los registros y CSV se crean en las carpetas configuradas. Los informes se pueden exportar desde la aplicación.

Cada encabezado de la grilla incluye un indicador **(?)**: al pasar el cursor se muestra una breve descripción y al hacer clic se abre la explicación completa de esa métrica.

## Licencia, autoría y modificaciones

El código y los materiales originales de Lagnostics están protegidos por derechos de autor de Federico Rocca y se distribuyen bajo los términos de la [Licencia de atribución y modificación de Lagnostics](./LICENSE). Se permite usar y redistribuir gratis el software sin cambios o con modificaciones cosméticas, siempre conservando la atribución al autor y señalando los cambios. Las modificaciones sustanciales pueden hacerse para uso privado, pero para publicarlas, distribuirlas o explotarlas comercialmente se requiere una licencia comercial escrita del autor, con sus condiciones y cualquier tarifa acordadas previamente.

Esta es una licencia personalizada de código disponible, no una licencia de código abierto aprobada por OSI. Los componentes de terceros conservan sus propias licencias.

## Pruebas automatizadas

La suite cubre estadísticas de latencia, validación y migración de configuración, traducciones y análisis de endpoints de juegos. Ejecutá `.\scripts\run-tests.ps1` antes de enviar cambios. El hook `pre-push` bloquea pushes locales si restore o las pruebas fallan; activalo una vez en el clon con `git config core.hooksPath .githooks`.

GitHub Actions corre las pruebas en cada push, pull request y ejecución de release. El log muestra los casos que fallan y se publica el archivo TRX como artefacto de la ejecución. Para exigir la verificación también al integrar cambios en `main`, configurá en GitHub un ruleset o una protección de rama que requiera el check **Automated tests / test**.

## Distribución para Windows

Las versiones publicadas están en [GitHub Releases](https://github.com/FedericoRocca/diagnostico_lag/releases). Cada release incluye el ZIP portable x64 y un instalador EXE autocontenido para Windows x64. El instalador se instala por usuario en `%LOCALAPPDATA%\Programs\Lagnostics`, agrega un acceso directo al menú Inicio y no requiere privilegios de administrador.

Cada release también adjunta un paquete `Lagnostics-<versión>-x64.msix` sin firma, generado con `packaging/msix/Build-Msix.ps1`, para subirlo a Microsoft Store desde el Centro de partners (Store lo firma al publicar). La identidad del paquete se toma de las variables de repositorio `MSIX_IDENTITY_NAME`, `MSIX_PUBLISHER` y `MSIX_PUBLISHER_DISPLAY_NAME`, con los valores de la aplicación actual como predeterminados.

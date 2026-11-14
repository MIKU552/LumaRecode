<p align="center">
  <img src="Brand/LumaRecode.svg" width="104" height="104" alt="Icono de LumaRecode" />
</p>
<h1 align="center">映码 · LumaRecode</h1>

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | Español

Herramienta de transcodificación de vídeo y conversión de color para Windows 11, desarrollada con C# y .NET. LumaRecode ofrece una interfaz nativa WinUI 3 y una línea de comandos para FFmpeg, con compresión de vídeo, conversión de Log de cámara a HDR / SDR y de HDR a SDR.

[Descargar](https://github.com/MIKU552/LumaRecode/releases/latest) · [Línea de comandos](docs/cli.es.md) · [Historial de cambios](CHANGELOG.md)

## Funciones

- Vista previa y recorte por fotogramas, imágenes HDR / SDR y fotos en movimiento de Android con portada seleccionada.
- Preajustes de Compartir rápido y Archivo por software para HEVC, AV1 y H.264, con salida MP4 / MKV.
- Conversión de DJI D-Log M, Samsung Log, Mi-Log, Apple Log y Apple Log 2 a HLG HDR o Rec.709 SDR; conversión de HLG / PQ a SDR.
- Revelado de ProRes RAW, detección y conversión de Dolby Vision / HDR10+ y conservación de metadatos dinámicos con codificación por software.
- Lectura de resolución, tasa de fotogramas, profundidad de bits, etiquetas de color y pistas de audio, con ajustes iniciales según el material.
- Selección de submuestreo de croma, rango Limited / Full y formato de audio, incluido FLAC sin pérdidas.
- Progreso, fps de codificación, posición, tiempo restante y verificación del formato de salida.
- Notificaciones de Windows al completar o fallar una tarea, con una casilla junto a Abrir carpeta.
- Mica, temas claro / oscuro / del sistema y colores de énfasis. Interfaz en chino simplificado, inglés, japonés y español.
- Procesamiento por lotes, archivos de configuración, vista previa de comandos y eventos JSON mediante la CLI.

## Inicio rápido

1. Descarga **LumaRecode.exe** desde [Releases](https://github.com/MIKU552/LumaRecode/releases). Los entornos de ejecución necesarios están incluidos.
2. Prepara una compilación de [FFmpeg para Windows](https://ffmpeg.org/download.html) con `ffmpeg.exe` y `ffprobe.exe`. LumaRecode busca en `PATH` al iniciarse; también puedes indicar las rutas con el botón de engranaje.
3. Arrastra un vídeo o pulsa **Elegir vídeo** y comprueba el rango dinámico, el preajuste y la conversión de color.
4. Elige la ruta de salida y pulsa **Iniciar conversión**.

Compartir rápido usa Intel QSV. Archivo por software usa la CPU.

Las fuentes Dolby Vision / HDR10+ usan HEVC por software para conservar los metadatos dinámicos de forma predeterminada. Elige HDR estándar para compartir con codificación por hardware. ProRes RAW comprueba la compatibilidad de FFmpeg y se revela con el balance de blancos, la matriz de color y la ganancia de cámara. Consulta los formatos compatibles en [Conversión de color](docs/color-conversion.md).

## Vista previa, recorte e imágenes

Abre **Vista previa y recorte por fotogramas** en la página de vídeo para desplazarte por la línea de tiempo, avanzar fotograma a fotograma y elegir el inicio y el final. Se convierte todo el vídeo de forma predeterminada. La interfaz cuenta desde 1 e incluye el último fotograma elegido.

La página **Imágenes y fotos en movimiento** exporta el fotograma seleccionado como HDR AVIF (BT.2020 / PQ, 10 o 12 bits), Ultra HDR JPEG, JPEG sRGB o PNG sRGB sin pérdida (8 bits o contenedor de 16 bits).

JPEG y Ultra HDR JPEG pueden incluir vídeo de foto en movimiento de Android. La portada y el intervalo de vídeo se eligen por separado; el intervalo inicial es de unos tres segundos alrededor de la portada. Un único archivo `MP.jpg` sigue [Google Motion Photo 1.0](https://developer.android.com/media/platform/motion-photo-format). La codificación Ultra HDR utiliza [libultrahdr](https://github.com/google/libultrahdr) de Google. El vídeo usa la CPU de forma predeterminada y también permite codificación rápida de Intel.

La vista previa se muestra en SDR. La imagen se exporta en el formato elegido. El recorte por fotogramas recodifica el audio como AAC, Opus o FLAC compatible.

El vídeo conserva la resolución original de forma predeterminada, con límites opcionales de 1080p y 720p. Los preajustes independientes para clips cortos usan x264 fast / CRF20, x265 fast / CRF23 o Intel TU6. El audio es AAC-LC a 48 kHz; conserva mono o usa estéreo. Consulta [motion-photo-presets.json](Assets/motion-photo-presets.json).

## Codificación y color

| Uso | Codificadores | Contenedor predeterminado | Audio predeterminado |
| --- | --- | --- | --- |
| Compartir rápido | Intel QSV: HEVC / AV1 / H.264 | MP4 | AAC para HEVC / H.264; Opus para AV1 |
| Archivo por software | x265 / SVT-AV1 / x264 | MKV | Copiar las pistas originales |

Puedes elegir por separado el contenedor, el submuestreo, la profundidad de bits, el rango de color y el audio. Compartir rápido usa por defecto SDR de 8 bits o HDR de 10 bits. Archivo por software conserva la profundidad del original dentro de los límites del codificador: 8 / 10 / 12 bits para HEVC y 8 / 10 bits para AVC y AV1.

El archivo con HEVC / H.264 por software conserva el submuestreo original. Las entradas RGB se convierten por defecto a YCbCr 4:4:4; AV1 y Compartir rápido usan 4:2:0. Puedes reducir el submuestreo y la profundidad dentro de los límites del original: una entrada de 8 bits 4:2:0 permanece en ese formato; una de 10 bits 4:2:2 permite elegir 10 / 8 bits y 4:2:2 / 4:2:0. Archivo por software conserva el rango Limited / Full original; Compartir rápido usa Limited. El submuestreo, la profundidad y el rango están en **Conversión de color**; el contenedor y el audio están en **Formato y audio**.

FLAC admite audio entero confirmado como sin pérdidas, incluido PCM entero, y conserva la frecuencia de muestreo, los canales y la precisión entera de cada pista. El audio FLAC usa MKV.

El material SDR se convierte por defecto a SDR; HLG / PQ conserva HDR; los perfiles Log reconocidos se convierten a HLG. **Interpretación** y **Perfil de cámara** permiten indicar manualmente el tipo de material.

El vídeo se vuelve a codificar con el preajuste elegido; el audio puede copiarse o convertirse. Los parámetros están en [recommended-presets.json](Assets/recommended-presets.json) y las mediciones en el [informe de pruebas de codificación, en inglés](docs/benchmarks/encoding-benchmark.en.md).

## Requisitos

- Windows 11 x64.
- FFmpeg / ffprobe con los codificadores y filtros necesarios, aportados por el usuario.
- Compatibilidad con Intel QSV para Compartir rápido.
- `zscale` y `lut3d` para la conversión de Log; `libplacebo` y Vulkan para HDR / Log a SDR.

El idioma y el tema siguen al sistema de forma predeterminada. Se reconocen sistemas en chino, inglés, japonés y español; los demás usan inglés. Los ajustes se guardan en `%LOCALAPPDATA%\LumaRecode\settings.json`.

En el primer inicio, LumaRecode importa el idioma, la apariencia y las rutas de herramientas de los ajustes de FrameFlow.

Source Sans 3, Source Han Sans y Source Code Pro están incluidos; no necesitas instalar fuentes.

## Línea de comandos

En PowerShell 7, conecta la salida a una canalización:

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./LumaRecode.exe --lang es --help | Out-Host
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
./LumaRecode.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

Antes del primer uso desatendido, consulta las condiciones de los componentes con `license --text` y ejecuta `license --accept-terms`. La [guía de la CLI](docs/cli.es.md) incluye todos los comandos, el procesamiento por lotes y los códigos de salida.

## Compilar desde el código fuente

Se necesitan .NET SDK 11.0.100, PowerShell 7 y las herramientas de Visual Studio C++ compatibles con Native AOT.

```powershell
git clone https://github.com/MIKU552/LumaRecode.git
cd LumaRecode
./build.ps1
```

El resultado es `dist/SelfContained/LumaRecode.exe`. Consulta las opciones de compilación y versiones de dependencias en la [documentación de desarrollo y distribución](docs/dependencies.md), y los comandos de comprobación en la [guía de QA](qa/README.md).

## Estructura del proyecto

```text
App/       Interfaz WinUI 3, apariencia, idiomas y gestión de tareas
Bundle/    Empaquetado en un único archivo
Core/      Análisis del material, reglas de formato, FFmpeg y CLI
Assets/    LUT, preajustes de codificación y registros de origen
Brand/     Archivos originales del icono y herramientas de generación
tools/     Recopilación de licencias y generación de código de idiomas
qa/        Comprobaciones automáticas y medición de memoria
docs/      Guías, documentación de desarrollo y datos de pruebas
licenses/  Licencias, avisos y procedencia de componentes de terceros
```

## Desarrollo y licencia

El código original del proyecto se desarrolló con **Codex para Windows**, usando **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** y el nivel de razonamiento **Extra high (`xhigh`)**. **[MIKU552](https://github.com/MIKU552)** define los requisitos, aporta el equipo y el material, y verifica la aplicación en dispositivos reales.

El código, la documentación, los gráficos originales y las LUT de Log de cámara generadas por el proyecto usan la **[licencia MIT](LICENSE)**. Las licencias y procedencia de las LUT, fuentes y entornos de ejecución de terceros figuran en [NOTICE](NOTICE) y en [componentes de terceros](Assets/THIRD_PARTY.md).

## Documentación

- [Línea de comandos](docs/cli.es.md)
- [Desarrollo y distribución](docs/dependencies.md)
- [Rendimiento](docs/performance.md)
- [Pruebas de codificación, en inglés](docs/benchmarks/encoding-benchmark.en.md)
- [Historial de cambios](CHANGELOG.md)

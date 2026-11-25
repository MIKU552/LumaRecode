<p align="center"><img src="../../Brand/LumaRecode.svg" width="96" height="96" alt="Icono de LumaRecode" /></p>
<h1 align="center">LumaRecode</h1>

[简体中文](../../README.md) | [English](en.md) | [日本語](ja.md) | Español

LumaRecode es una herramienta de vídeo para Windows y macOS. Permite comprimir clips, convertir grabaciones Log a HDR o SDR, recortar por fotogramas y exportar imágenes. Las dos interfaces nativas comparten el mismo motor de transcodificación basado en FFmpeg.

[Sitio del proyecto (inglés)](https://lumarecode.miku552.top/) · [Microsoft Store (Windows)](https://apps.microsoft.com/detail/9N71VF8GJT6F) · [Descargas en GitHub](https://github.com/MIKU552/LumaRecode/releases) · [Línea de comandos](../cli/es.md) · [Documentación (chino e inglés)](../README.md#english) · [Historial de cambios (chino e inglés)](../../CHANGELOG.md#english)

## Funciones

| | Windows 11 · x64 / ARM64 | macOS 27+ · Apple silicon |
| --- | --- | --- |
| Interfaz | WinUI 3 con Mica y notificaciones del sistema | SwiftUI con los colores y las fuentes del sistema |
| Vídeo | HEVC, AV1, H.264 · MP4 / MKV | HEVC, AV1, H.264 · MP4 / MKV |
| Compartir rápido | Intel QSV, NVIDIA NVENC, AMD AMF, Media Foundation | Apple VideoToolbox |
| HDR | HLG / PQ y entrada Dolby Vision y HDR10+ | HLG / PQ, entrada Dolby Vision y HDR10+, exportación HLG → Dolby Vision 8.4 |
| Imágenes | SDR JPEG / PNG, HDR AVIF, Ultra HDR JPEG | SDR JPEG, HDR HEIC, Adaptive HDR HEIC |
| Fotos en movimiento | Android Motion Photo | Apple Live Photo |

El modo de compartir rápido usa un codificador de hardware que haya superado la comprobación del dispositivo. El modo de archivo usa x264, x265 o SVT-AV1. Admite DJI D-Log M, Samsung Log, Mi-Log, Apple Log y Apple Log 2. La compatibilidad con ProRes RAW depende del decodificador de FFmpeg. [Conversión de color (chino e inglés)](../color-conversion.md#english)

## Instalación

- **Windows** · Instala desde [Microsoft Store](https://apps.microsoft.com/detail/9N71VF8GJT6F) o descarga manualmente desde GitHub Releases. Elige x64 o ARM64. Puedes abrir el EXE directamente, extraer el ZIP portátil y abrir `LumaRecode.exe`, o instalar el MSI para tu cuenta. La edición AOT funciona sin instalar el entorno de ejecución. Si la edición con .NET externo lo necesita, el aviso de inicio ofrece un enlace de descarga de Microsoft. [.NET 11 Runtime (inglés)](https://dotnet.microsoft.com/download/dotnet/11.0)
- **macOS** · Arrastra LumaRecode desde el DMG hasta Aplicaciones. La edición FFmpeg Full incluye FFmpeg, ffprobe, libplacebo y MoltenVK. [Primer inicio y configuración (chino e inglés)](../dependencies.md#english)

Las ediciones con FFmpeg externo consultan PATH y, en macOS, Homebrew. También puedes indicar dónde están `ffmpeg` y `ffprobe` desde la aplicación. La conversión de Log usa `zscale` y `lut3d`. La conversión de HDR a SDR y algunas fuentes Dolby Vision usan `libplacebo` y Vulkan.

## Vista previa, recorte e imágenes

1. Abre un vídeo, elige el modo de compartir rápido o de archivo y revisa la señal y el formato de salida.
2. Abre la línea de tiempo para reproducir el vídeo, avanzar por fotogramas y fijar el primero y el último. También puedes conservar el vídeo completo.
3. Elige el fotograma de portada en la pestaña de fotos. En Windows, el ajuste inicial de Motion Photo para material HDR es **Ultra HDR JPEG + vídeo SDR**. Puedes cambiar cada formato por separado. En macOS, Live Photo combina una imagen SDR JPEG o Adaptive HDR HEIC con un MOV.

## Línea de comandos

```powershell
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
```

```sh
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode probe clip.mov
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode convert clip.mov -o out.mkv
```

Consulta el [manual de línea de comandos](../cli/es.md) para ver las opciones, las tareas por lotes y los formatos de salida.

## Desarrollo

- **Windows** · Abre `LumaRecode.sln` con Visual Studio o ejecuta `./build.ps1` en PowerShell 7. Usa `./tools/Package-Windows.ps1` para crear los paquetes.
- **macOS** · Abre `macOS/LumaRecode.xcodeproj` con Xcode o ejecuta `sh tools/build-macos.sh Release --dmg`.
- **Motor compartido** · `engine/` contiene la ABI de C, el núcleo de transcodificación en C/C++ y los módulos de metadatos en Rust. [Arquitectura (chino e inglés)](../architecture.md#english)

[Herramientas y distribución (chino e inglés)](../dependencies.md#english) · [Pruebas de codificación (chino e inglés)](../benchmarks/encoding-benchmark.md#english) · [Guía de pruebas (chino e inglés)](../../qa/README.md#english)

## Licencia y créditos

El código original se distribuye bajo la [licencia MIT (inglés)](../../LICENSE). Las licencias y fuentes de terceros figuran en [NOTICE (inglés)](../../NOTICE).

Codex para Windows escribió el código con GPT-6.1 Sol y GPT-6.2 Sol, ambos en modo `xhigh`. [MIKU552](https://github.com/MIKU552) define los requisitos, aporta material de prueba y verifica la aplicación en equipos reales.

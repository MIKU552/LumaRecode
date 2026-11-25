<p align="center"><img src="../../Brand/LumaRecode.svg" width="96" height="96" alt="LumaRecode のアイコン" /></p>
<h1 align="center">LumaRecode</h1>

[简体中文](../../README.md) | [English](en.md) | 日本語 | [Español](es.md)

LumaRecode は Windows と macOS 向けの動画ツールです。動画の圧縮、カメラの Log 素材から HDR・SDR への変換、フレーム単位のトリミング、静止画の書き出しができます。両 OS のネイティブ UI で、共通の FFmpeg 変換エンジンを使います。

[プロジェクトサイト（英語）](https://lumarecode.miku552.top/) · [Microsoft Store（Windows）](https://apps.microsoft.com/detail/9N71VF8GJT6F) · [GitHub ダウンロード](https://github.com/MIKU552/LumaRecode/releases) · [コマンドライン](../cli/ja.md) · [ドキュメント（中国語・英語）](../README.md#english) · [変更履歴（中国語・英語）](../../CHANGELOG.md#english)

## 機能

| | Windows 11 · x64 / ARM64 | macOS 27+ · Apple シリコン |
| --- | --- | --- |
| UI | WinUI 3、Mica、システム通知 | SwiftUI、システム標準の色とフォント |
| 動画 | HEVC、AV1、H.264 · MP4 / MKV | HEVC、AV1、H.264 · MP4 / MKV |
| クイック共有 | Intel QSV、NVIDIA NVENC、AMD AMF、Media Foundation | Apple VideoToolbox |
| HDR | HLG / PQ、Dolby Vision・HDR10+ 素材 | HLG / PQ、Dolby Vision・HDR10+ 素材、HLG → Dolby Vision 8.4 |
| 静止画 | SDR JPEG / PNG、HDR AVIF、Ultra HDR JPEG | SDR JPEG、HDR HEIC、Adaptive HDR HEIC |
| 動く写真 | Android Motion Photo | Apple Live Photo |

クイック共有では動作確認に通ったハードウェアエンコーダーを使います。ソフトウェアアーカイブでは x264、x265、SVT-AV1 を使います。DJI D-Log M、Samsung Log、Mi-Log、Apple Log、Apple Log 2 に対応しています。ProRes RAW の対応状況は FFmpeg のデコーダーによって異なります。[カラー変換（中国語・英語）](../color-conversion.md#english)

## インストール

- **Windows** · [Microsoft Store](https://apps.microsoft.com/detail/9N71VF8GJT6F) からインストールするか、GitHub Releases から手動でダウンロードできます。 x64 または ARM64 を選びます。単体の EXE を起動するか、ポータブル ZIP を展開して `LumaRecode.exe` を開くか、MSI を実行してユーザー単位でインストールします。AOT 版はそのまま起動できます。外部ランタイム版で .NET 11 Runtime が見つからない場合は、起動時のダイアログからダウンロードページを開けます。[.NET 11 Runtime（英語）](https://dotnet.microsoft.com/download/dotnet/11.0)
- **macOS** · DMG から LumaRecode を「アプリケーション」にドラッグします。FFmpeg Full 版には FFmpeg、ffprobe、libplacebo、MoltenVK が含まれます。[初回起動とツールの設定（中国語・英語）](../dependencies.md#english)

外部の FFmpeg を使う版は PATH を検索します。macOS では Homebrew も検索します。アプリで `ffmpeg` と `ffprobe` の場所を指定することもできます。Log 変換には `zscale` と `lut3d`、HDR から SDR への変換や一部の Dolby Vision 素材には `libplacebo` と Vulkan を使います。

## プレビュー・トリミング・静止画

1. 動画を開き、クイック共有またはソフトウェアアーカイブを選んで、出力形式と色の設定を確認します。
2. タイムラインで再生やコマ送りを行い、開始と終了のフレームを指定します。動画全体を書き出すこともできます。
3. 写真ページでカバーにするフレームを選びます。Windows で HDR 素材から Motion Photo を作る場合の初期設定は **Ultra HDR JPEG ＋ SDR 動画**です。写真と動画の形式は個別に変更できます。macOS の Live Photo は SDR JPEG または Adaptive HDR HEIC と MOV の組み合わせです。

## コマンドライン

```powershell
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
```

```sh
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode probe clip.mov
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode convert clip.mov -o out.mkv
```

オプション、一括処理、出力形式は[コマンドラインガイド](../cli/ja.md)を参照してください。

## 開発

- **Windows** · Visual Studio で `LumaRecode.sln` を開くか、PowerShell 7 で `./build.ps1` を実行します。パッケージの作成には `./tools/Package-Windows.ps1` を使います。
- **macOS** · Xcode で `macOS/LumaRecode.xcodeproj` を開くか、`sh tools/build-macos.sh Release --dmg` を実行します。
- **共通エンジン** · `engine/` に C ABI、C/C++ の変換コア、Rust のメタデータ処理があります。[コード構成（中国語・英語）](../architecture.md#english)

[開発環境と配布（中国語・英語）](../dependencies.md#english) · [エンコード比較（中国語・英語）](../benchmarks/encoding-benchmark.md#english) · [QA ガイド（中国語・英語）](../../qa/README.md#english)

## ライセンスとクレジット

独自コードのライセンスは [MIT License（英語）](../../LICENSE) です。サードパーティーのライセンスと出典は [NOTICE（英語）](../../NOTICE) を参照してください。

Windows 版 Codex が GPT-6.1 Sol と GPT-6.2 Sol（いずれも `xhigh`）でコードを作成しました。[MIKU552](https://github.com/MIKU552) が要件を決め、テスト素材を用意し、実機で動作を確認しています。

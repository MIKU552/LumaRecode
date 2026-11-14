<p align="center">
  <img src="Brand/LumaRecode.svg" width="104" height="104" alt="LumaRecode アイコン" />
</p>
<h1 align="center">映码 · LumaRecode</h1>

[简体中文](README.md) | [English](README.en.md) | 日本語 | [Español](README.es.md)

C# / .NET で開発した Windows 11 向けの動画変換・色変換ツールです。ネイティブ WinUI 3 インターフェイスとコマンドラインを備え、FFmpeg による動画圧縮、カメラ Log から HDR / SDR への変換、HDR から SDR への変換を行います。

[ダウンロード](https://github.com/MIKU552/LumaRecode/releases/latest) · [ドキュメント](docs/README.md) · [コマンドライン](docs/cli.ja.md) · [変更履歴](CHANGELOG.md)

## 主な機能

- フレームプレビューとトリミング、HDR / SDR 画像、カバーフレームを指定した Android モーションフォト。
- クイック共有・アーカイブ用プリセット。HEVC、AV1、H.264 と MP4 / MKV に対応。
- DJI D-Log M、Samsung Log、Mi-Log、Apple Log、Apple Log 2 から HLG HDR / Rec.709 SDR への変換、HLG / PQ から SDR への変換。
- ProRes RAW の現像、Dolby Vision / HDR10+ の識別と変換、ソフトウェアエンコードによる動的メタデータの保持。
- 解像度、フレームレート、ビット深度、色タグ、音声トラックを読み取り、素材に応じた初期設定を適用。
- クロマサブサンプリング、Limited / Full レンジ、音声形式の選択。可逆音声の FLAC 変換に対応。
- 進捗、エンコード fps、処理位置、残り時間の表示と出力形式の検証。
- 変換の完了・失敗を Windows 通知でお知らせします。「フォルダーを開く」の左側で切り替えられます。
- Mica、ライト / ダーク / システムテーマ、アクセントカラー。簡体字中国語、英語、日本語、スペイン語に対応。
- CLI での一括処理、設定ファイル、コマンドのプレビュー、JSON イベント出力。

## 使い始める

1. [Releases](https://github.com/MIKU552/LumaRecode/releases) から **LumaRecode.exe** をダウンロードします。必要なランタイムは同梱されています。
2. `ffmpeg.exe` と `ffprobe.exe` を含む Windows 版 [FFmpeg](https://ffmpeg.org/download.html) を用意します。初回起動時に `PATH` を検索します。右上の歯車からパスを指定することもできます。
3. 動画をドロップするか「動画を選択」を押し、出力のダイナミックレンジ、プリセット、色変換を確認します。
4. 保存先を指定し、「変換を開始」を押します。

クイック共有は Intel QSV、アーカイブは CPU エンコードを使用します。

Dolby Vision / HDR10+ は、ソフトウェア HEVC で動的メタデータを保持する設定が初期値です。共有用にハードウェアで出力する場合は「標準 HDR」を選択します。ProRes RAW は FFmpeg の対応状況を確認し、カメラのホワイトバランス、色変換行列、ゲインで現像します。対応形式は[色変換](docs/color-conversion.md)を参照してください。

## プレビュー・トリミング・画像

動画ページの「プレビューとフレーム単位のトリミング」で、タイムラインの移動、コマ送り、開始・終了フレームの指定ができます。初期設定では動画全体を変換します。画面上のフレーム番号は 1 始まりで、終了フレームも含まれます。

「画像・モーションフォト」ページでは、選択したフレームを HDR AVIF（BT.2020 / PQ、10 または 12 ビット）、Ultra HDR JPEG、sRGB JPEG、可逆圧縮の sRGB PNG（8 ビットまたは 16 ビットコンテナー）で書き出せます。

JPEG と Ultra HDR JPEG には Android モーションフォト動画を追加できます。カバーフレームと動画範囲を個別に指定でき、初期範囲はカバー前後の約 3 秒です。[Google Motion Photo 1.0](https://developer.android.com/media/platform/motion-photo-format) に従い、単一の `MP.jpg` ファイルに保存します。Ultra HDR のエンコードには Google の [libultrahdr](https://github.com/google/libultrahdr) を使用します。動画は CPU エンコードが初期設定で、Intel による高速エンコードも選択できます。

プレビューは SDR 表示です。画像は選択した形式で書き出します。フレーム単位のトリミングでは、音声を AAC、Opus または条件に合う FLAC に再エンコードします。

動画の解像度は元のままが初期設定で、最大 1080p または 720p も選べます。短い動画用の専用プリセットは x264 fast / CRF20、x265 fast / CRF23、または Intel TU6 を使用します。音声は AAC-LC、48 kHz で、モノラルを保持し、それ以外はステレオにします。設定は [motion-photo-presets.json](Assets/motion-photo-presets.json) を参照してください。

## エンコードと色変換

| 用途 | エンコーダー | 標準のコンテナー | 標準の音声処理 |
| --- | --- | --- | --- |
| クイック共有 | Intel QSV：HEVC / AV1 / H.264 | MP4 | HEVC / H.264 は AAC、AV1 は Opus |
| アーカイブ | x265 / SVT-AV1 / x264 | MKV | 元の音声トラックをコピー |

コンテナー、サンプリング、ビット深度、カラーレンジ、音声形式は個別に選択できます。クイック共有の初期値は SDR が 8 ビット、HDR が 10 ビットです。アーカイブはエンコーダーの対応範囲内で元のビット深度を保持します。HEVC は 8 / 10 / 12 ビット、AVC と AV1 は 8 / 10 ビットに対応します。

ソフトウェア HEVC / H.264 のアーカイブでは元のサンプリングを保持します。RGB 入力は標準で 4:4:4 YCbCr に変換し、AV1 とクイック共有は 4:2:0 を使用します。サンプリングとビット深度は入力の範囲内で下げられます。8 ビット 4:2:0 はそのまま保持し、10 ビット 4:2:2 は 10 / 8 ビット、4:2:2 / 4:2:0 を選択できます。アーカイブは元の Limited / Full レンジを保持し、クイック共有は Limited を使用します。サンプリング、ビット深度、レンジは「色変換」、コンテナーと音声は「形式と音声」にあります。

FLAC は整数 PCM と、可逆形式と確認できる整数音声に対応し、各トラックのサンプルレート、チャンネル数、整数精度を保持します。FLAC 音声には MKV を使用します。

SDR 素材は SDR 出力、HLG / PQ 素材は HDR 出力が初期値です。曲線を識別できた Log 素材は HLG に変換します。「入力形式」と「カメラプロファイル」で素材の種類を手動指定できます。

動画は選択したプリセットで再エンコードします。音声はコピーまたは変換できます。設定値は [recommended-presets.json](Assets/recommended-presets.json)、測定結果は[エンコードベンチマーク（英語）](docs/benchmarks/encoding-benchmark.en.md)を参照してください。

## 動作要件

- Windows 11 x64。
- 必要なエンコーダーとフィルターを含む FFmpeg / ffprobe。
- クイック共有には対応する Intel QSV 環境。
- Log 変換には `zscale` と `lut3d`。HDR / Log から SDR への変換には `libplacebo` と Vulkan。

言語とテーマは初期状態でシステムに従います。中国語、英語、日本語、スペイン語のシステム言語に対応し、それ以外は英語になります。設定は `%LOCALAPPDATA%\LumaRecode\settings.json` に保存します。

初回起動時に、FrameFlow の言語、外観、ツールのパス設定を引き継ぎます。

Source Sans 3、源ノ角ゴシック（Source Han Sans）、Source Code Pro を同梱しているため、フォントのインストールは不要です。

## コマンドライン

PowerShell 7 で出力をパイプに接続して実行します。

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./LumaRecode.exe --lang ja --help | Out-Host
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
./LumaRecode.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

初めて無人実行する前に `license --text` でコンポーネントの利用条件を確認し、`license --accept-terms` を実行してください。全コマンド、一括処理、終了コードは[CLI ガイド](docs/cli.ja.md)に記載しています。

## ソースからビルド

.NET SDK 11.0.100、PowerShell 7、Native AOT に対応する Visual Studio C++ ツールが必要です。

```powershell
git clone https://github.com/MIKU552/LumaRecode.git
cd LumaRecode
./build.ps1
```

生成物は `dist/SelfContained/LumaRecode.exe` です。ビルド方法と依存関係は[開発・配布ドキュメント](docs/dependencies.md)、チェックコマンドは [QA ガイド](qa/README.md)を参照してください。

## ディレクトリ構成

```text
App/       WinUI 3 UI、外観、ローカライズ、ジョブ操作
Bundle/    単一ファイルへのパッケージ化
Core/      素材解析、形式の規則、FFmpeg の実行、CLI
Assets/    LUT、エンコードプリセット、配布元の記録
Brand/     アイコンのソースと生成ツール
tools/     ライセンス収集とローカライズコードの生成
qa/        自動チェックとメモリ測定
docs/      使用方法、開発ドキュメント、ベンチマーク
licenses/  第三者のライセンス、通知、配布元の記録
```

## 開発とライセンス

本プロジェクトの独自コードは、**Windows 版 Codex** で **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** と **Extra high（`xhigh`）** 推論設定を使用して開発しました。**[MIKU552](https://github.com/MIKU552)** が要件を定義し、機材と素材を提供して実機検証を行っています。

独自のコード、ドキュメント、画像、生成したカメラ Log LUT には **[MIT License](LICENSE)** が適用されます。第三者の LUT、フォント、ランタイムのライセンスと配布元は [NOTICE](NOTICE) と[第三者コンポーネント](Assets/THIRD_PARTY.md)に記載しています。

## ドキュメント

- [コマンドライン](docs/cli.ja.md)
- [開発と配布](docs/dependencies.md)
- [性能](docs/performance.md)
- [エンコードベンチマーク（英語）](docs/benchmarks/encoding-benchmark.en.md)
- [変更履歴](CHANGELOG.md)

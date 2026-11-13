<p align="center">
  <img src="Brand/FrameFlow.svg" width="104" height="104" alt="FrameFlow アイコン" />
</p>
<h1 align="center">映流 · FrameFlow</h1>

[简体中文](README.md) | [English](README.en.md) | 日本語 | [Español](README.es.md)

Windows 11 向けの動画変換・色変換ツールです。ネイティブ WinUI 3 インターフェイスとコマンドラインを備え、FFmpeg による動画圧縮、カメラ Log から HDR / SDR への変換、HDR から SDR への変換を行います。

[ダウンロード](https://github.com/MIKU552/FrameFlow/releases/latest) · [コマンドライン](docs/cli.ja.md) · [変更履歴](CHANGELOG.md)

## 主な機能

- クイック共有・アーカイブ用プリセット。HEVC、AV1、H.264 と MP4 / MKV に対応。
- Mi-Log、Pocket 3 D-Log M、Samsung Log を HLG HDR または Rec.709 SDR に変換。HLG / PQ から SDR への変換にも対応。
- 解像度、フレームレート、ビット深度、色タグ、音声トラックを読み取り、素材に応じた初期設定を適用。
- クロマサブサンプリング、Limited / Full レンジ、音声形式の選択。可逆音声の FLAC 変換に対応。
- 進捗、エンコード fps、処理位置、残り時間の表示と出力形式の検証。
- Mica、ライト / ダーク / システムテーマ、アクセントカラー。簡体字中国語、英語、日本語、スペイン語に対応。
- CLI での一括処理、設定ファイル、コマンドのプレビュー、JSON イベント出力。

## 使い始める

1. [Releases](https://github.com/MIKU552/FrameFlow/releases) から **FrameFlow.exe** をダウンロードします。必要なランタイムは同梱されています。
2. `ffmpeg.exe` と `ffprobe.exe` を含む Windows 版 [FFmpeg](https://ffmpeg.org/download.html) を用意します。初回起動時に `PATH` を検索します。右上の歯車からパスを指定することもできます。
3. 動画をドロップするか「動画を選択」を押し、出力のダイナミックレンジ、プリセット、色変換を確認します。
4. 保存先を指定し、「変換を開始」を押します。

クイック共有は Intel QSV、アーカイブは CPU エンコードを使用します。

## エンコードと色変換

| 用途 | エンコーダー | 標準のコンテナー | 標準の音声処理 |
| --- | --- | --- | --- |
| クイック共有 | Intel QSV：HEVC / AV1 / H.264 | MP4 | HEVC / H.264 は AAC、AV1 は Opus |
| アーカイブ | x265 / SVT-AV1 / x264 | MKV | 元の音声トラックをコピー |

コンテナー、サンプリング、ビット深度、カラーレンジ、音声形式は個別に選択できます。クイック共有の初期値は SDR が 8 ビット、HDR が 10 ビットです。アーカイブはエンコーダーの対応範囲内で元のビット深度を保持します。HEVC は 8 / 10 / 12 ビット、AVC と AV1 は 8 / 10 ビットに対応します。

ソフトウェア HEVC / H.264 のアーカイブでは元のサンプリングを保持します。RGB 入力は標準で 4:4:4 YCbCr に変換し、AV1 とクイック共有は 4:2:0 を使用します。サンプリングとビット深度は入力の範囲内で下げられます。8 ビット 4:2:0 はそのまま保持し、10 ビット 4:2:2 は 10 / 8 ビット、4:2:2 / 4:2:0 を選択できます。アーカイブは元の Limited / Full レンジを保持し、クイック共有は Limited を使用します。サンプリング、ビット深度、レンジは「色変換」、コンテナーと音声は「形式と音声」にあります。

FLAC は整数 PCM と、可逆形式と確認できる整数音声に対応し、各トラックのサンプルレート、チャンネル数、整数精度を保持します。FLAC 音声には MKV を使用します。

SDR 素材は SDR 出力、HLG / PQ 素材は HDR 出力が初期値です。曲線を識別できた Log 素材は HLG に変換します。「入力形式」と「カメラプロファイル」で素材の種類を手動指定できます。

動画は選択したプリセットで再エンコードします。音声はコピーまたは変換できます。設定値は [recommended-presets.json](Worker/assets/recommended-presets.json)、測定結果は[エンコードベンチマーク（英語）](docs/benchmarks/encoding-benchmark.en.md)を参照してください。

## 動作要件

- Windows 11 x64。
- 必要なエンコーダーとフィルターを含む FFmpeg / ffprobe。
- クイック共有には対応する Intel QSV 環境。
- Log 変換には `zscale` と `lut3d`。HDR / Log から SDR への変換には `libplacebo` と Vulkan。

言語とテーマは初期状態でシステムに従います。中国語、英語、日本語、スペイン語のシステム言語に対応し、それ以外は英語になります。設定は `%LOCALAPPDATA%\FrameFlow\settings.json` に保存します。

Source Sans 3、源ノ角ゴシック（Source Han Sans）、Source Code Pro を同梱しているため、フォントのインストールは不要です。

## コマンドライン

PowerShell 7 で出力をパイプに接続して実行します。

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./FrameFlow.exe --lang ja --help | Out-Host
./FrameFlow.exe probe clip.mp4 --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

初めて無人実行する前に `license --text` でコンポーネントの利用条件を確認し、`license --accept-terms` を実行してください。全コマンド、一括処理、終了コードは[CLI ガイド](docs/cli.ja.md)に記載しています。

## ソースからビルド

.NET SDK 11.0.100、PowerShell 7、Native AOT に対応する Visual Studio C++ ツールが必要です。

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
./build.ps1
```

生成物は `dist/SelfContained/FrameFlow.exe` です。ビルド方法と依存関係は[開発・配布ドキュメント](docs/dependencies.md)、チェックコマンドは [QA ガイド](qa/README.md)を参照してください。

## ディレクトリ構成

```text
App/       WinUI 3 UI、外観、ローカライズ、ジョブ操作
Bundle/    単一ファイルへのパッケージ化
Worker/    CLI、素材の解析、FFmpeg の実行、LUT
Brand/     アイコンのソースと生成ツール
tools/     ライセンス収集とローカライズコードの生成
qa/        自動チェックとメモリ測定
docs/      使用方法、開発ドキュメント、ベンチマーク
licenses/  第三者のライセンス、通知、配布元の記録
```

## 開発とライセンス

本プロジェクトの独自コードは、**Windows 版 Codex** で **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** と **Extra high（`xhigh`）** 推論設定を使用して開発しました。**[MIKU552](https://github.com/MIKU552)** が要件を定義し、機材と素材を提供して実機検証を行っています。

独自のコード、ドキュメント、画像、生成した Mi-Log / Samsung Log LUT には **[MIT License](LICENSE)** が適用されます。第三者の LUT、フォント、ランタイムのライセンスと配布元は [NOTICE](NOTICE) と[第三者コンポーネント](Worker/assets/THIRD_PARTY.md)に記載しています。

## ドキュメント

- [コマンドライン](docs/cli.ja.md)
- [開発と配布](docs/dependencies.md)
- [性能](docs/performance.md)
- [エンコードベンチマーク（英語）](docs/benchmarks/encoding-benchmark.en.md)
- [変更履歴](CHANGELOG.md)

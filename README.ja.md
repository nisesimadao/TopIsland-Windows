<p align="center">
  <img src="assets/banner.svg" alt="TopIsland — Windowsの上部に常駐するコンパクトなサーフェス" width="100%">
</p>

<p align="center">
  日本語 · <a href="README.md">English</a>
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF-0C54C2?style=flat-square">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2B-0078D4?style=flat-square&logo=windows11&logoColor=white">
</p>

# TopIsland

TopIslandは、メディア・現在の前面アプリ・軽量なシステム情報を表示する**Windows上端の常駐オーバーレイ**です。浮遊するDynamic Islandと、画面上端から直接生えるNotchを切り替えられます。

基準デザインは、**黒い1枚の面・必要な情報だけ・入れ子カードを並べない**ことを優先しています。

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="Windows上で動作するTopIslandのExpanded Notch" width="100%">
</p>

> 実際のWPFアプリを動かして撮影し、GitHubで読みやすいようTopIsland周辺だけクロップしています。

## 主な機能

- **Dynamic Island / Notch** 切替
- 横幅に引き伸ばされない小さな逆R付きNotch
- **Idle → Hover → Peek → Expanded**
- Windows GMTCから曲名・artist/source・アートワーク・進捗を取得
- Previous / Play-Pause / Next
- メディアが無い時は、偽の再生UIを出さず**現在の前面アプリ名・プロセス・アプリアイコン**を表示
- 大きい表示ではCPU / RAM / Network / 時刻 / 日付
- Authentic / Compact / Standard / Wide / Full Width
- 狭い幅では文字を縮小せず、優先度の低い情報を減らす
- 透明部分はクリック透過
- TopIslandを操作しても背後のアプリやゲームからフォーカスを奪わない
- System / Light / Dark テーマ

## システムトレイ

Windowsの通知領域にもTopIslandアイコンを常駐させています。右クリックから、オーバーレイ本体に設定カードを増やさずに変更できます。

- Show / Hide
- Expand
- Dynamic Island / Notch
- Width
- Material / Theme
- 表示するモニター
- Windows起動時に自動起動
- Quit

トレイから変更した内容も通常の設定と同じJSONへ保存されます。

## マルチモニター / DPI

トレイのDisplayから次を選べます。

- **Follow active app** — 現在の前面アプリがあるモニターへ追従
- **Primary display**
- **固定モニター** — 接続中の画面を指定

WPF側は**PerMonitorV2**で動作し、モニターごとの物理解像度とWindowsの拡大率を別々に取得します。150%のメイン画面と125%のサブ画面で、Standard / Full Widthの物理サイズと中央位置を実測確認しています。

## デザイン基準

現在は主に次の2つを参照しています。

1. **BoringNotch** — 開閉時のNotch形状、90pxアート、控えめな操作UI、黒い単一サーフェス。
2. **AppleのDynamic Island / Live Activityガイド** — 均一なマージン、外形と調和した配置、情報を詰め込まず減らす考え方。

調査した数値とレビュー基準は [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md) に固定しています。

### 現在のNotch形状

| 状態 | 上部の逆R | 下部R |
| --- | ---: | ---: |
| Closed | 6 | 14 |
| Expanded | 19 | 24 |

半径は横幅と独立しているため、Full Widthにしても肩だけ横に引き伸ばされません。

## Screenshots

<table>
  <tr>
    <td align="center"><b>Dynamic Island · Expanded</b></td>
    <td align="center"><b>Notch · Authentic</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dynamic-expanded.png" alt="Expanded Dynamic Island"></td>
    <td><img src="docs/screenshots/notch-authentic.png" alt="Authentic Notch"></td>
  </tr>
</table>

## 情報密度

- **Authentic Notch:** アート / アプリアイコン＋時刻
- **Compact:** 主タイトルを追加
- **Standard:** 余裕があればartist/sourceなどを追加
- **Peek:** 既存UIを縮めず、低優先度情報を1段だけ追加可能
- **Expanded:** アート / アプリアイコン＋主要情報＋右端の1つのステータスグループ

Expandedの中に設定カードは置きません。設定は右クリックメニューとシステムトレイへ分離しています。

## Material

標準は黒い `Solid` です。Mica / Acrylic / Glass / Material Copyはオプションとして残していますが、Appleの物理Notchをガラスモーフィズムとして再現するものではありません。

Windows 11のNative DWM backdropも検出しますが、透明WPFホストではカスタム形状の外側まで矩形で描画されるため、現在は形状を優先したレンダリングを使用しています。

## Build

必要なもの:

- Windows 10以上
- .NET 10 SDK

```powershell
dotnet build TopIsland.slnx -c Release
```

Windows x64向けPublish:

```powershell
dotnet publish TopIsland/TopIsland.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish
```

## このPCへローカルインストール

Publish後:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-local.ps1
```

`%LOCALAPPDATA%\Programs\TopIsland` へコピーし、スタートメニューのショートカットを作成して起動します。**自動起動は勝手にONにせず**、必要な場合だけトレイから有効化します。

削除する場合:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/uninstall-local.ps1
```

ユーザー設定は `%APPDATA%\TopIsland\settings.json` に残します。

## 現在の状態

まだ完成版ではなくインタラクティブプロトタイプですが、メディア / 前面アプリ連携、形状とアニメーション、幅プリセット、クリック透過、フォーカス維持、システムトレイ設定、設定保存、マルチモニター配置、PerMonitorV2、自動起動のON/OFFまでは動作しています。大きな残りは、Notch / Islandの形状を壊さずに使えるCompositionベースの本物のBlur経路です。

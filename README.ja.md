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

TopIslandは、メディアと軽量なシステム情報を表示する**Windows上端の常駐オーバーレイ**です。浮いているDynamic Island風と、画面上端から直接生えるNotchを切り替えられます。

現在の基準デザインは、**黒い1枚の面・必要な情報だけ・入れ子カードを並べない**ことを優先しています。

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="Minecraft上で動作するTopIslandのExpanded Notch" width="100%">
</p>

> 実際のWPFアプリを動かして撮影し、GitHubで読みやすいようTopIsland周辺だけクロップしています。

## 主な機能

- **Dynamic Island / Notch** 切替
- 横幅に引き伸ばされない小さな逆R付きNotch
- **Idle → Hover → Peek → Expanded**
- Windows GMTCから曲名・artist/source・アートワーク・進捗を取得
- Previous / Play-Pause / Next
- 大きい表示ではCPU / RAM / Network / 時刻 / 日付
- Authentic / Compact / Standard / Wide / Full Width
- 狭い幅では文字を縮小せず、優先度の低い情報を消す
- 透明部分はクリック透過
- TopIslandを操作しても背後のアプリやゲームからフォーカスを奪わない
- Style / Width / Material / Themeは右クリックメニューから変更
- `%APPDATA%\TopIsland\settings.json` にローカル保存

## デザイン基準

現在は主に次の2つを参照しています。

1. **BoringNotch** — 開閉時のNotch形状、90pxアート、30/40pxのメディア操作、黒い単一サーフェス、控えめなHover。
2. **AppleのDynamic Island / Live Activityガイド** — 均一なマージン、外形と同心円の配置、コンパクトな情報設計、詰め込まず情報を減らす考え方。

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

- **Authentic Notch:** アート＋時刻
- **Compact:** 曲名を追加
- **Standard:** artist/sourceを追加
- **430px以上:** Compact状態にCPU/RAMを追加可能
- **Expanded:** アート＋メディア操作＋右端の1つのステータスグループ

Expandedの中に設定カードは置きません。設定は右クリックメニューへ分離しています。

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

## 現在の状態

まだ完成版ではなくインタラクティブプロトタイプです。メディア連携、形状アニメーション、幅プリセット、クリック透過、フォーカス維持、Material切替、設定保存は動作しています。マルチモニター / Per-Monitor DPIと、形状を維持したCompositionベースのBlurは今後の改善対象です。

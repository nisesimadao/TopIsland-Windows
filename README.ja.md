<p align="center">
  <img src="assets/banner.svg" alt="TopIsland" width="100%">
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

TopIslandは、必要な時だけ情報量を増やす**Windows画面上端の常駐サーフェス**です。浮遊するDynamic Islandと、画面上端から逆RでつながるNotchを切り替えられます。

基準デザインは、**黒い1枚の面・実データ・状態に応じた情報密度・カードを並べない**ことを優先しています。

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="実際に動作しているTopIsland Expanded Notch" width="100%">
</p>

> 実際のWPFアプリを動かして撮影した画像です。GitHubで読みやすいよう、描画サーフェス付近だけクロップしています。

## 表示できる実データ

現在は次の情報を実際のWindows/アプリから取得しています。

- Windows GMTCの曲名・source・アートワーク・再生位置・Previous / Play-Pause / Next
- メディアが無い時は、現在の前面アプリのタイトル・プロセス名・exeアイコン
- **CPU / GPU / RAM** 使用率
- NetworkのDownload / Upload速度
- システムドライブの空き容量 / 総容量
- `.crdownload` / `.part` など実際に進行中のDownload。現在サイズと、取得できる場合は実測速度も表示
- 内蔵**Focus timer**。25分 / 45分、Pause-Resume、Reset
- Windows通知。許可されている場合のみ、件数・アプリ名・最新本文
- バッテリー搭載端末では残量と充電状態
- 時刻 / 日付

通知権限は勝手に要求しません。許可されていない場合は通知レーン自体を隠し、必要ならシステムトレイの **Enable notifications** からWindowsへ要求できます。

## 状態

- **Idle** — 選択中の幅に必要な最小情報
- **Hover** — 少しだけ幅と深さが増える
- **Peek** — Full Expandせずに一部の追加情報を表示
- **Expanded** — 上段のMedia/Contextと、下段の情報レーン

Expandedの下段もカードを増やさず、Focus / Downloads / Storage / Notifications / Batteryを細いdividerで区切っています。

小さい幅では文字を縮めず情報を減らします。Full Widthでは通知件数・Network・Storageなどを追加できますが、Media本文やProgressBarを画面いっぱいに引き伸ばしません。

## Screenshots

<table>
  <tr>
    <td align="center"><b>Dynamic Island · Expanded</b></td>
    <td align="center"><b>Notch · Authentic</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dynamic-expanded.png" alt="TopIsland Dynamic Island expanded"></td>
    <td><img src="docs/screenshots/notch-authentic.png" alt="TopIsland Authentic Notch"></td>
  </tr>
</table>

## システムトレイ

TopIsland本体を設定画面化せず、通知領域のTopIslandアイコンから変更できます。

- Show / Hide
- Expand
- Dynamic Island / Notch
- Width: Authentic / Compact / Standard / Wide / Full Width
- Material / Theme
- Display: Follow active app / Primary / 固定モニター
- Focus timer: 25分 / 45分 / Pause-Resume / Reset
- Windows通知が未許可の場合はEnable notifications
- Windows起動時に自動起動
- Quit

永続設定は `%APPDATA%\TopIsland\settings.json` に保存します。

## マルチモニター / DPI

WPFホストは**PerMonitorV2**です。各モニターの物理解像度とWindowsの拡大率を別々に取得します。

表示先は次から選べます。

- **Follow active app** — 現在の前面アプリが存在するモニターへ追従
- **Primary display**
- **固定モニター** — 接続中の任意の画面へ固定

Follow時は別DPIの画面を横断スライドせず、短くFade Out → 移動/DPI再計算 → Fade Inします。

開発PCでは3840×2160 / 150%と2560×1440 / 125%の2画面でStandard / Full Widthの物理サイズを確認しています。

## デザイン基準

設計監査は [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md) にまとめています。

基本ルール:

- メインサーフェスは1枚
- 基準Materialは黒い`Solid`
- 4 / 8 / 12 / 16 / 24 dipのspacing scale
- 外形に理由がない限り左右clearanceを揃える
- UIアイコンはvector。絵文字を使わない
- 狭い幅では文字を縮めず低優先度情報を隠す
- Notchの逆Rは横幅と独立
- Full Widthでも中身を無理に引き伸ばさない

Notch形状の現在値:

| 状態 | 上部の逆R | 下部R |
| --- | ---: | ---: |
| Closed | 6 | 14 |
| Expanded | 19 | 24 |

## Material

標準は黒い`Solid`です。Mica / Acrylic / Glass / Material Copyはオプションとして残しています。

Native DWM backdropも検証しましたが、透明なcustom-shape WPF hostではNotch外側の矩形まで描画されるため、安定版では形状を優先しています。Compositionベースの本物のshape-clipped blurは、動作中の本体へ無理に依存させず別の実験として続けます。

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

現在ユーザー向けにローカルインストール:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-local.ps1
```

アンインストール:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/uninstall-local.ps1
```

インストール先は `%LOCALAPPDATA%\Programs\TopIsland` で、スタートメニューにもショートカットを作成します。Windows自動起動はデフォルトOFFです。

## 現在の状態

まだ完成版ではありませんが、Media/前面アプリ連携、GPUを含むSystem情報、Focus/Download監視、許可時のWindows通知取得、形状とMotion、クリック透過、フォーカス維持、システムトレイ、設定保存、マルチモニター配置、PerMonitorV2は動作しています。

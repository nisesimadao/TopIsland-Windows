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

TopIslandは、Windowsの画面上部中央に常駐し、必要なときだけ情報量を増やすトップサーフェスです。浮いた**Dynamic Island**と、画面上端から逆Rで生える**Notch**を切り替えられます。

基準デザインは、**1枚の面・実データ・必要な幅だけ情報量を増やす**ことを重視しています。角丸カードを大量に並べるダッシュボードにはしません。

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="実データを表示したTopIsland Notch Expanded" width="100%">
</p>

> 実際に動いているWPFアプリのスクリーンショットです。GitHub上でUIが見やすいよう、表示面の周辺をクロップしています。

## 実データ

現在は次の情報を実際のWindows/アプリから取得しています。

- Windows GMTCのメディア情報、アートワーク、再生位置、実操作できるSeek、Previous / Play-Pause / Next
- 前面アプリ名、プロセス、実行ファイルアイコン。ExpandedではDownloadがない時にSESSIONとして実メモリ、スレッド数、プロセス起動時間も表示
- **CPU / GPU / RAM**
- **Discord VC** — Discord Desktopの表示中/トレイ格納中Electron windowからVC名、サーバー、参加人数、自分のMute/Deafen状態をWindows UI Automationで読み取り、Discord側がUIA操作を公開している場合はMute / Deafen / Leaveも実操作します
- NetworkのDownload / Upload速度
- システムドライブの空き容量 / 総容量
- `.crdownload` / `.part`など実際に進行しているDownloadと実測転送速度
- 25分 / 45分の**Focus Timer**、Pause / Resume / Reset
- 許可済みの場合のみWindows通知の件数、アプリ名、最新テキストと、本当に通知を消すClear all
- Battery搭載機のみ残量 / 充電状態
- 時刻 / 日付

Windows通知の権限は起動時に勝手に要求しません。許可がない場合は通知レーンを隠し、必要なときだけTrayの**Enable notifications**から要求できます。

## 表示状態

- **Idle** — Media/Active App、時計、幅に余裕があれば上へ見切れたタコメーター型CPU/RAMアークと通知/Downloadインジケータ
- **Hover** — わずかに広く・深くなる
- **Peek** — Full Expandせず、優先度の低い情報を少し追加
- **Expanded** — 上段は Context / Now Playing、中央の大きな時計＋CPU/GPU/RAM＋Power/温度＋Discord VC操作、通知がある時だけNotifications。下段はTimers、通常時はSESSION、Download中だけDOWNLOADSへ置換、右にStorage / Network / Hardwareです

幅が狭い場合は文字を無理に縮めず、優先度の低い情報を落とします。空モジュールは場所を取りません。Downloadsはpartial fileが実際に更新中の時だけ表示し、それ以外は同じ中央レーンをSESSION（前面プロセスのメモリ/スレッド/起動時間）として使います。Discord操作はVC接続中だけ、Notificationsは存在する時だけ表示します。Full Widthでは余った中央をFocus / Voice / Downloads / Network / Storageに使い、Media本文を横へ引き伸ばしません。

## Screenshots

<table>
  <tr>
    <td align="center"><b>Dynamic Island · Material You</b></td>
    <td align="center"><b>Notch · Glass / Live Blur</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dynamic-expanded.png" alt="TopIsland Dynamic Island expanded"></td>
    <td><img src="docs/screenshots/notch-expanded.png" alt="TopIsland Notch expanded with Glass live blur"></td>
  </tr>
</table>

<p align="center">
  <b>Full Width · Material You</b><br>
  <img src="docs/screenshots/fullwidth-materialyou.png" alt="TopIsland Full Width compact Material You" width="100%">
</p>

> リポジトリの画像は実動作キャプチャです。通知本文やVCの個人情報にあたる箇所だけ、commit前にピクセル化しています。

## システムトレイ

TopIsland本体を設定画面化せず、Trayアイコンの右クリックから設定できます。

- Show / Hide
- Expand
- Dynamic Island / Notch
- Width: Authentic / Compact / Standard / Wide / Full Width
- Material / Theme
- Display: Follow active app / Primary / 固定モニター
- Focus timer: 25分 / 45分 / Pause-Resume / Reset
- 必要な場合のみEnable notifications
- Launch at startup
- Quit

設定は `%APPDATA%\TopIsland\settings.json` に保存されます。

## マルチモニター / DPI

WPF本体とBlurHostは**PerMonitorV2**です。モニターごとの物理解像度とWindowsのScale Factorを独立して扱います。

Displayは次から選べます。

- **Follow active app** — 前面アプリがあるモニターを追従
- **Primary display**
- **Fixed display** — 特定の接続モニターへ固定

別DPIのモニターへ移動するときは、無理に横スライドさせず短くFade Outし、移動先DPIで再計算してFade Inします。

開発機では2560×1440 @ 150%と2048×1152 @ 125%の組み合わせで確認しています。

## デザイン基準

設計監査は [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md) に残しています。

主なルール:

- 主役のSurfaceは1枚
- `Solid`黒が基準デザイン
- 4 / 8 / 12 / 16 / 24 dipのspacing scale
- 形状上の理由がない限り左右余白は対称
- 絵文字UIではなくvector icon
- 狭い幅では文字を圧縮せず情報を減らす
- Notchの逆Rを横幅に合わせて引き伸ばさない
- Wide / Full WidthでもMedia本文やProgressBarを無意味に伸ばさない

現在のNotch形状:

| State | 上側の逆R | 下側R |
| --- | ---: | ---: |
| Closed | 6 | 14 |
| Expanded | 19 | 24 |

## Material / Live Blur

Notchの基準は`Solid`黒です。`Mica`は密度の高いオプションSurfaceです。`Acrylic` / `Glass`は、**実際の背景をshape内だけぼかすLive Blur**を使います。`Material You`は別系統で、Windowsのアクセント色をHCT seedにしてMaterial 3の`TonalSpot` schemeを生成し、`surface` / `surfaceContainerLow` / `surfaceContainerHigh` / `onSurface` / `outlineVariant` / `primary`などのsemantic roleを使います。Material YouではBlurHostを起動しません。

透明なWPF WindowへDWM backdropを直接適用すると、Notch/Island外側の矩形Window領域まで塗られてしまうため採用していません。代わりに別プロセスの**TopIsland.BlurHost**が、

1. TopIsland直下の小さな画面領域だけ取得
2. SkiaSharpでGaussian Blur
3. Dynamic Island / 逆R Notchと同じPathでper-pixel alpha clip
4. WPFの文字・操作UIの直下へ配置

という処理を行います。

これにより矩形の灰色Backdropを出さず、逆Rやクリック透過用余白を維持できます。移動/変形中は追従性を優先し、静止後はBlur更新頻度を落とします。またSkiaオブジェクトを再利用し、半解像度の作業面でBlurしてからフル解像度へ合成します。現在の開発機ではGlass Expandedの代表値として、Standardで**正規化CPU約1.8%、Working Set約55〜60MB**、Full Widthで**CPU約2.8%**でした。プロトタイプ上の実測値であり、固定性能目標ではありません。

BlurHostが見つからない/起動できない場合は、文字が読めなくならないよう従来の濃い非Blur Materialへ自動fallbackします。

## Build

必要なもの:

- Windows 10以上
- .NET 10 SDK

本体とBlurHostをBuild:

```powershell
dotnet build TopIsland.slnx -c Release
```

Windows x64向けに本体+BlurHostをまとめてPublish:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

出力例:

```text
artifacts/publish/
  TopIsland.exe
  ...
  BlurHost/
    TopIsland.BlurHost.exe
    SkiaSharp.dll
    libSkiaSharp.dll
    ...
```

現在ユーザー向けにローカルインストール:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-local.ps1
```

アンインストール:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/uninstall-local.ps1
```

インストール先は `%LOCALAPPDATA%\Programs\TopIsland`。Start Menu shortcutを作成します。Windows自動起動はデフォルトOFFで、勝手に有効化しません。

## 現在の状態

まだ完成版ではありませんが、Media/前面アプリ、CPU/GPU/RAM/Network/Storage、Focus/Download監視、許可済みWindows通知、hidden Discord windowを含むVC状態/操作、Notch/Dynamic geometry、Hover/Peek/Expanded motion、クリック透過、フォーカス維持、Tray設定、設定保存、マルチモニター、PerMonitorV2、Material You、shape-clipped Live Blurまで動作しています。

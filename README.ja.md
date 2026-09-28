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

TopIsland は、Windows の画面上部中央に常駐し、状態に応じて表示する情報量を変えるオーバーレイです。
浮遊する **Dynamic Island** と、画面上端につながる **Notch** の二つの形状を切り替えられます。

設計では、主となる面を一つに保ち、実際に取得したデータだけを表示し、必要な場合にだけ表示領域を広げることを重視しています。
複数の角丸カードを並べるダッシュボード型の構成にはしていません。

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="実データを表示したTopIsland Notch Expanded" width="100%">
</p>

> スクリーンショットは実際に動作している WPF アプリから取得しています。
> GitHub 上で UI を確認しやすいよう、表示面の周辺だけをクロップしています。

## 実データ

現在は、次の情報を Windows または対象アプリから取得しています。

- Windows GMTC のメディア情報、アートワーク、再生位置を取得し、Seek、Previous、Play / Pause、Next を操作できます。
- 前面アプリ名、プロセス、実行ファイルアイコンを表示します。
  Expanded では、Download がない場合に SESSION として前面プロセスの CPU 使用率、実メモリ、起動時間も表示します。
- **CPU / GPU / RAM** の使用状況を表示します。
- Compact の CPU / RAM は、左下から上を通って右下へ増えるタコメーター型のアークで表示します。
  実測値の更新は短い ease-out 補間で反映します。
- **Discord VC** の状態を Windows UI Automation から取得します。
  Discord Desktop の表示中またはトレイ格納中の Electron window から、VC 名、サーバー、参加人数、自分の Mute / Deafen 状態を読み取ります。
  Discord が対応する UI Automation 操作を公開している場合は、Mute / Deafen / Leave も実行できます。
- Discord VC がない場合は、Expanded の中央下に既定の Windows 出力デバイス名と Master Volume を表示します。
- Network の Download / Upload 速度を表示します。
- システムドライブの空き容量と総容量を表示します。
- `.crdownload` / `.part` など、実際に更新中の Download と実測転送速度を表示します。
- Windows の出力 Volume、Mute、Stay Awake、利用可能な Power mode を **CONTROLS** から操作できます。
- 許可されている場合だけ、Windows 通知の件数、アプリ名、最新テキストを表示し、Clear all で実際の通知を削除します。
- Battery 搭載機では残量と充電状態を表示します。
- 時刻と日付を表示します。

Windows 通知の権限は起動時に自動要求しません。
権限がない場合は通知レーンを隠し、必要な場合だけ Tray の **Enable notifications** から要求できます。

## 表示状態

- **Idle**：Media / Active App、時計、CPU / RAM の usage gauge、通知と Download のインジケータを表示します。
  Authentic 幅では CPU だけを表示します。
- **Hover**：Idle より少し広く、深い表示になります。
- **Reveal on top edge**：任意で Idle 時の表示を完全に隠し、クリック透過にできます。
  画面上端中央へポインタを移動すると再表示します。
- **Peek**：Full Expand せず、優先度の低い情報を少し追加します。
- **Expanded**：上段に Context / Now Playing、中央に大きな時計、CPU / GPU / RAM、Power / 温度、Discord VC 操作を表示します。
  VC がない場合は Output / Volume を表示します。
  通知がある場合だけ Notifications を追加します。
  下段には CONTROLS と SESSION を表示し、Download 中だけ SESSION を DOWNLOADS に置き換えます。
  右側には Storage / Network / Hardware / Uptime を表示します。

幅が狭い場合は文字を縮小して詰め込まず、優先度の低い情報から非表示にします。
空のモジュールは表示領域を取りません。
Downloads は partial file が実際に更新されている場合だけ表示します。
Downloads がない場合は、同じ領域を SESSION として使います。
Discord の操作は VC 接続中だけ、Notifications は通知が存在する場合だけ表示します。
Full Width では余った中央領域を Stay Awake / Voice / Downloads / Network / Storage に使い、Media 本文を不必要に引き伸ばしません。

## Screenshots

<table>
  <tr>
    <td align="center"><b>Compact Notch Idle · labeled CPU/RAM</b></td>
    <td align="center"><b>Hover · active state indicator</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/compact-idle.png" alt="TopIsland compact Notch idle with labeled open-right CPU and RAM meters"></td>
    <td><img src="docs/screenshots/compact-hover-focus.png" alt="TopIsland hover state indicator"></td>
  </tr>
</table>
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

> リポジトリ内の画像は実動作時のキャプチャです。
> 通知本文や VC の個人情報にあたる箇所だけ、commit 前にピクセル化しています。

## システムトレイ

TopIsland 本体には常設の設定画面を置かず、Tray アイコンの右クリックメニューから設定します。

- Show / Hide
- Expand
- Dynamic Island / Notch
- Reveal on top edge
- Width: Authentic / Compact / Standard / Wide / Full Width
- Material / Theme
- Display: Follow active app / Primary / 固定モニター
- CONTROLS: 出力 Volume、Mute、Stay Awake、利用可能な Power mode
- 必要な場合だけ Enable notifications
- Launch at startup
- Quit

設定は `%APPDATA%\TopIsland\settings.json` に保存します。

## マルチモニターと DPI

WPF 本体と BlurHost は **PerMonitorV2** です。
モニターごとの物理解像度と Windows の Scale Factor を別々に扱います。

Display は次のモードから選べます。

- **Follow active app**：前面アプリがあるモニターを追従します。
- **Primary display**：プライマリディスプレイへ固定します。
- **Fixed display**：指定した接続済みモニターへ固定します。

DPI が異なるモニターへ移動する際は、オーバーレイを横方向へ移動させません。
短く Fade Out した後、移動先の DPI でサイズと位置を再計算して Fade In します。

開発環境では、2560×1440 @ 150% と 2048×1152 @ 125% の組み合わせでも確認しています。

## デザイン基準

設計監査は [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md) に記録しています。

主なルールは次の通りです。

- 主役となる Surface は一つにします。
- `Solid` 黒を基準デザインにします。
- 4 / 8 / 12 / 16 / 24 dip の spacing scale を使います。
- 形状上の理由がない限り、左右の余白を対称にします。
- UI アイコンには絵文字ではなく vector icon を使います。
- 幅が狭い場合は文字を縮小せず、表示する情報を減らします。
- Notch の逆 R は横幅に合わせて拡大しません。
- Wide / Full Width でも Media 本文や ProgressBar を不必要に伸ばしません。

現在の Notch 形状は次の通りです。

| State | 上側の逆R | 下側R |
| --- | ---: | ---: |
| Closed | 6 | 14 |
| Expanded | 19 | 24 |

## Material と Live Blur

Notch の基準は `Solid` 黒です。
`Mica` は密度の高い別の Surface として扱います。
`Acrylic` / `Glass` は、実際の背景を形状内だけぼかす Live Blur を使います。
`Material You` は別系統で、Windows のアクセント色を HCT seed として Material 3 の `TonalSpot` scheme を生成します。
`surface` / `surfaceContainerLow` / `surfaceContainerHigh` / `onSurface` / `outlineVariant` / `primary` などの semantic role を使い、Material You では BlurHost を起動しません。

透明な WPF Window に DWM backdrop を直接適用すると、Notch / Island の外側にある矩形 Window 領域まで塗られます。
そのため、別プロセスの **TopIsland.BlurHost** が次の処理を行います。

1. TopIsland の直下にある小さな画面領域だけを取得します。
2. SkiaSharp で Gaussian Blur を適用します。
3. Dynamic Island / 逆 R Notch と同じ Path で per-pixel alpha clip を行います。
4. WPF の文字と操作 UI の直下へ配置します。

この構成により、矩形の灰色 Backdrop を表示せず、逆 R とクリック透過用の余白を維持します。
移動または変形中は追従性を優先し、静止後は Blur の更新頻度を下げます。
Skia オブジェクトを再利用し、半解像度の作業面で Blur した後にフル解像度へ合成します。

現在の開発機で測定した Glass Expanded の例では、Standard で正規化 CPU 約 1.8%、Working Set 約 55〜60 MB、Full Width で CPU 約 2.8% でした。
これはプロトタイプ上の測定値であり、固定した性能保証ではありません。

BlurHost を見つけられない場合や起動できない場合は、文字の可読性を保つため、濃い非 Blur Material へ自動でフォールバックします。

## Build

必要な環境は次の通りです。

- Windows 10 以上。
- .NET 10 SDK。

本体と BlurHost をビルドします。

```powershell
dotnet build TopIsland.slnx -c Release
```

Windows x64 向けに本体と BlurHost をまとめて Publish します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

出力例は次の通りです。

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

現在のユーザー向けにローカルインストールする場合は、次のスクリプトを実行します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-local.ps1
```

アンインストールには次のスクリプトを使います。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/uninstall-local.ps1
```

インストール先は `%LOCALAPPDATA%\Programs\TopIsland` です。
Start Menu shortcut を作成します。
Windows の自動起動は既定で無効で、インストール時に自動では有効にしません。

## 現在の状態

Media / 前面アプリ、CPU / GPU / RAM / Network / Storage、Windows Volume / Stay Awake / Power controls、Download 監視、許可済み Windows 通知、Discord VC の状態と操作、Notch / Dynamic geometry、Hover / Peek / Expanded motion、クリック透過、フォーカス維持、Tray 設定、設定保存、マルチモニター、PerMonitorV2、Material You、shape-clipped Live Blur を実装しています。
完成版ではなく、引き続き挙動と UI を調整しています。

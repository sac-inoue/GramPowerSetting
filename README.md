# GramPowerSetting (GramCooling)

LG gram の冷却モードやバッテリー設定をタスクトレイから切り替え、ファン回転数・CPU 温度などを表示する常駐ツールです。

> **非公式ツールです。** LG Electronics とは無関係で、動作の保証はありません。自己責任で使ってください。

## できること

- トレイアイコンに CPU 温度 / 負荷 / クロックのどれかを表示（背景色は冷却モード）
- 冷却モードの切り替え（サイレント / 低 / 正常 / 高）
- 「バッテリー寿命を延ばす」の切り替え（未使用 / 80%まで充電 / AIモード）
- Windows の「省エネ機能」の ON/OFF
- ファン回転数 [rpm]・CPU 温度 [℃]・CPU 負荷 [%]・CPU クロック [GHz] の表示
- ログオン時の自動起動（タスクスケジューラに「最高の権限で実行」で登録）
- 自動更新（インストール先の `update` フォルダに新しい版を置くと入れ替わる）

## 動作環境

- LG gram（17Z90TL で確認）。My gram（LG のユーティリティ）がインストールされていること
- Windows 11
- .NET 10 Desktop Runtime
- 管理者権限（LG の ACPI WMI を呼ぶため）

## ビルドとインストール

```powershell
dotnet publish -c Release -o "$env:LOCALAPPDATA\Programs\GramCooling"
& "$env:LOCALAPPDATA\Programs\GramCooling\GramCooling.exe"
```

初回起動時に自動起動のタスク（`GramCooling`）を登録します。不要ならメニューの「Windows 起動時に自動起動」で外せます。

更新は、常駐中に次のように `update` フォルダへ publish すると数秒で入れ替わります。

```powershell
dotnet build -c Release --no-incremental
dotnet publish -c Release --no-build -o "$env:LOCALAPPDATA\Programs\GramCooling\update"
```

コマンドラインからも使えます: `GramCooling.exe [silent|low|normal|high|get|fan] [--keep]`

## 仕組み

すべて My gram と同じく、BIOS が提供する ACPI WMI 経由で操作します。

| 機能 | 方法 |
|---|---|
| 冷却モード | `root\wmi` `Method_ULong2.GetULong / SetULong`（InOffset = 1036） |
| 充電上限 | `Method_ULong2`（InOffset = 268、80 / 100） |
| バッテリー AI モード | `HKCU\Software\LGE\My gram\LGSmartBattery\BatteryLifeToggled` |
| ファン回転数・CPU 温度 | `MfgFwiPackage.FwiExecL` による EC 読み取り（bank1 0xC8-0xC9 / bank0 0xC8） |
| 省エネ機能 | 設定アプリの設定ハンドラ（`SystemSettings_PowerAndBattery_EnergySaverAlwaysOn`） |

### 注意: EC を直接読まないこと

EC のポート（0x62 / 0x66）を直接読むと Windows の ACPI EC ドライバと競合し、
バッテリー残量の誤検知による強制スリープが起きることを確認しています。
このツールは EC を必ず ACPI WMI 経由（BIOS 側で排他制御される）で読みます。

## ライセンス

MIT

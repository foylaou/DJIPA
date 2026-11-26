# 現場擴音（第一版）

macOS 選單列小工具：任意麥克風 → Mac → 喇叭或耳機（以 DJI Mic 3 → HDMI 開發測試）。

## 使用
1. `./build.sh` 後開啟 `build/現場擴音.app`，選單列會出現麥克風圖示。
2. 確認麥克風與輸出裝置，**喇叭音量先調小**。
3. 按「開始擴音」，第一次請允許麥克風權限。
4. 再慢慢調高音量。

- 音量：麥克風增益。輸出上限：超過就自動壓低。
- 換裝置或拔除裝置會停止擴音，需再按開始。
- 擴音期間不會自動睡眠。
- 低頻切除：濾掉 100 Hz 以下。
- 噪音閘門：低於門檻時降 24 dB。門檻設在環境噪音與講話音量之間。
- 自動嘯叫抑制：偵測約 0.5 秒的持續單音後削掉該頻率，最多 6 個。停止擴音會清除。長音、口哨可能被誤判。
- 沒有回音消除；嘯叫抑制無法完全防止回授。請關掉 QuickTime 監聽，避免重複出聲。

## 下載版安裝
Release 的 App 以 Apple Development 憑證簽名，**未經 Apple 公證**，只支援 Apple Silicon、macOS 13 以上。
其他 Mac 開啟時會被阻擋，需到 系統設定 → 隱私權與安全性 按「仍要打開」。

## 開發
- 簽名打包：`SIGN_IDENTITY="憑證名稱" ./build.sh`
- 重新產生圖示：`swift Tools/make_icon.swift build/AppIcon.iconset && iconutil -c icns build/AppIcon.iconset -o Resources/AppIcon.icns`
- `./test.sh`：離線測試限幅、靜音與效果器（不發聲）。
- `build/現場擴音.app/Contents/MacOS/DJIPA --list`：列出音訊裝置。

## Windows 版（試作）
位於 `Windows/`，C# + WinForms + NAudio（WASAPI 共用模式），功能與 macOS 版相同：選單改為工作列通知區圖示，點圖示開啟面板。
低頻切除、噪音閘門、嘯叫抑制、限幅的演算法由 `Effects.c`／`AudioCore.c` 逐行移植到 `Windows/Dsp.cs`。
- 需求：Windows 10 以上；建置需要 .NET 8 SDK。
- 打包：在 PowerShell 執行 `Windows\build.ps1`，得到 `Windows\publish\現場擴音.exe`。
- 首次使用請到 設定 → 隱私權與安全性 → 麥克風，開啟「讓桌面應用程式存取麥克風」。
- 與 macOS 版的差異：兩個裝置各自走一條串流，中間用緩衝與內插吸收時脈差異，延遲約 70 ms（macOS 約 11 ms）；未簽名，SmartScreen 可能要按「其他資訊 → 仍要執行」。
- 單元測試：`dotnet test Windows/Tests/DJIPA.Windows.Tests.csproj`（16 項，離線不發聲，對應 macOS 的 `./test.sh`）。
- `現場擴音.exe --list` 列出裝置、`--selftest` 靜音跑 4 秒檢查音訊回呼。

## 授權
[MIT License](LICENSE)。Windows 版使用的 [NAudio](https://github.com/naudio/NAudio) 依其自身授權。

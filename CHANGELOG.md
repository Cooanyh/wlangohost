# Changelog

## 0.2.2

### 简体中文

- 修复主界面窄窗口下滚动条遮挡右侧内容的问题。
- GitHub 更新检查改为带超时的异步请求；应用每次启动时在后台检查正式版，有新版本时提供下载入口，并支持忽略当前版本直到后续版本发布。
- 更新检查失败不会阻塞界面，也不会中断网络启动；日志仅记录受限的错误类型信息。
- 保留电脑/平板/双向连接图形，扩大图标有效绘制占比并微调线宽；当前候选预览已生成，待用户验收后定稿。
- 运行时图标改为选择最接近 64px 的帧，并将主题 PNG 缩至 64px；相较 256px 位图，单张像素数据从约 256 KiB 降至 16 KiB。

### English

- Fixed the homepage scrollbar covering right-side content in a narrow window.
- GitHub release checks now use bounded asynchronous requests. WlanGO checks for stable releases in the background at startup and offers a download link when a newer version is available; users can ignore that version until a later release.
- Failed update checks no longer block the UI or network startup. Logs record only bounded error-type details.
- Preserve the monitor/tablet/two-way-link artwork while enlarging its visible canvas area and adjusting stroke weight; the candidate preview is ready for user review.
- Runtime icon loading now selects the frame closest to 64px, and theme PNGs are 64px; pixel data per bitmap drops from about 256 KiB to 16 KiB versus a 256px bitmap.

## 0.2.1

### 简体中文

- 修复应用更换安装目录后仍使用旧程序路径开机启动的问题；应用启动时会同步当前程序路径，安装更新时也会迁移已存在的启动项。

### English

- Fixed Windows startup continuing to use an old executable path after the app moves to a different install directory. WlanGO synchronizes the current executable when it starts and migrates an existing startup entry during installation.

## 0.2.0

### 简体中文

- 主界面可修改 Wi-Fi 网络名称和密码；密码校验为 8–63 个可打印 ASCII 字符，新配置仅在网络停止时保存，并于下次启动生效。
- 新安装默认名称为 `WlanGO-Direct`；密码继续随机生成，并使用当前 Windows 用户 DPAPI 加密保存。

### English

- The main window can edit the Wi-Fi network name and password. Passphrases are validated as 8–63 printable ASCII characters. Changes are saved while the network is stopped and apply on the next start.
- Fresh installs use `WlanGO-Direct` by default. The password remains random and protected with the current Windows user's DPAPI.

## 0.1.0

### 简体中文

- 首个公开版本：Windows 11 x64 Wi-Fi Direct Group Owner 桌面工具。
- 复用微软 Wi-Fi Direct Legacy AP C++ 示例；提供 WPF 主界面、系统托盘、启动/停止和状态展示。
- 支持 Windows 浅色/深色主题、简体中文/英文、可选开机启动应用和应用启动后启动网络。
- 首次运行生成随机 Wi-Fi 密码，并以当前用户 DPAPI 加密保存；日志有固定容量上限。
- 可选 SuperDisplay 快捷入口，支持自动查找、用户选择和记忆程序路径。
- 安装程序支持英语与简体中文、当前用户安装及自选安装目录。
- 设置页可手动查询 GitHub 最新正式版，并打开下载页和项目主页；不会后台检查或自动安装更新。
- WlanGO 自有代码采用 GPL-3.0-only；第三方组件继续遵循其原许可证。

### English

- First public release: a Windows 11 x64 Wi-Fi Direct Group Owner desktop utility.
- Reuses Microsoft's Wi-Fi Direct Legacy AP C++ sample with a WPF main window, system tray, start/stop controls, and live backend status.
- Supports Windows light/dark themes, Simplified Chinese/English, optional app startup, and optional network startup with the app.
- Generates a random first-use Wi-Fi password, protects it with the current user's DPAPI, and caps local log storage.
- Optional SuperDisplay shortcut with automatic discovery, browse-to-select, and remembered path.
- Installer supports English and Simplified Chinese, per-user installation, and a custom installation folder.
- Settings provides a manual GitHub stable-release check plus release and project links; no background checks or automatic installation.
- WlanGO's own code is licensed under GPL-3.0-only; third-party components retain their original licenses.

### Known limitations

- Wi-Fi Direct availability, frequency, channel, and peer count depend on the Windows build and wireless adapter/driver.
- The application does not enable Internet sharing, Mobile Hotspot, ICS, or network bridging.
- This release is unsigned; Windows may display a SmartScreen warning.

# Changelog

## 0.2.0

### 简体中文

- 主界面可修改 Wi-Fi 网络名称和密码；新配置仅在网络停止时保存，并于下次启动生效。
- 新安装默认名称为 `WlanGO-Direct`；密码继续随机生成，并使用当前 Windows 用户 DPAPI 加密保存。

### English

- The main window can edit the Wi-Fi network name and password. Changes are saved while the network is stopped and apply on the next start.
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

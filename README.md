# WlanGO

**A lightweight Windows 11 Wi-Fi Direct Group Owner app.**<br>
**Windows 11 Wi-Fi Direct Group Owner 轻量工具。**

[简体中文](#简体中文) · [English](#english)

## 简体中文

WlanGO 使用微软 Wi-Fi Direct Legacy AP 实现建立本地 Wi-Fi Direct Group Owner（GO）网络，让平板等客户端直接连接电脑。它不提供 Internet 共享。

### 获取与安装

1. 从 [GitHub Releases](https://github.com/Cooanyh/wlangohost/releases) 下载 `WlanGO-Setup-0.1.0-x64.exe`。
2. 安装时可选择目标文件夹，包括其他磁盘；安装不需要管理员权限。
3. 启动 WlanGO 并选择“启动网络”，再从客户端的 WLAN 列表连接显示的 SSID。
4. “开机启动应用”和“应用启动后启动网络”是两个独立选项，默认关闭。

安装包暂未签名；Windows SmartScreen 可能显示未知发布者提示。可将安装包 SHA-256 与同一 Release 的 `SHA256SUMS.txt` 比对。

### 功能

- 原生 WPF 桌面界面与 Windows 系统托盘控制。
- 自动跟随浅色/深色主题；支持简体中文、英文或跟随 Windows。
- 首次运行生成随机 Wi-Fi 密码，并使用当前 Windows 用户 DPAPI 加密保存。
- 关闭窗口后继续在托盘运行；选择“退出”会停止网络并清理后端进程。
- 设置中可添加任意 `.exe` 启动项、自定义名称并逐项启用或停用。多项启动时可选择逐次勾选，或一键同时打开所有已启用项目；托盘菜单提供二级启动菜单和逐项入口。
- SuperDisplay 默认作为一个启动项提供：WlanGO 会先自动查找，必要时可选取并记住程序路径；启动时隐藏主界面，异常退出后有限次数自动重启。SuperDisplay 不随 WlanGO 分发。
- 本地运行日志限制为一个 64 KiB 文件和两个滚动副本，不记录密码、设备地址或 SSID。
- 设置页可手动查询 GitHub 最新正式版，并打开版本记录、安装包或项目主页；查询只发送应用版本标识，不包含 Wi-Fi 信息或用户设置，不含后台自动更新。

### 兼容性与网络行为

- 目标平台：Windows 11 x64，且无线适配器和驱动支持 Wi-Fi Direct Group Owner。
- 频段、信道和并发能力由 Windows、无线适配器及驱动决定；WlanGO 不保证可强制指定 5 GHz 或特定信道。
- 网络仅用于本地 Wi-Fi Direct 通信。WlanGO 不启用移动热点、ICS 或桥接，也不修改防火墙、路由、DNS、网卡驱动或系统网络配置。
- 客户端数量与连接详情取决于 Windows 暴露的 Wi-Fi Direct/WLAN 事件和本机接口信息。
- 卸载会保留用户设置。设置位于 `%LOCALAPPDATA%\WlanGO`；如需清除密码密文和日志，可自行删除此目录。

### 从源码构建

开发环境和构建命令见 [`desktop/README.md`](desktop/README.md)。GitHub Actions 在 Windows runner 上构建 x64 Release 安装包；推送与项目版本一致的 `v*` 标签后，会自动创建 GitHub Release 并附上 Inno Setup 安装包和 SHA-256 校验文件。托管 runner 没有可用于验收的 Wi-Fi Direct 硬件，CI 构建不能替代实体客户端连接测试。

### 许可

WlanGO 自有代码采用 GNU GPL-3.0-only。微软 `Windows-classic-samples` 中的 Wi-Fi Direct 示例源码保留其原始 MIT 版权声明。依赖和来源见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## English

WlanGO uses Microsoft's Wi-Fi Direct Legacy AP implementation to create a local Wi-Fi Direct Group Owner (GO) network. Connect a nearby tablet or another Wi-Fi Direct client directly to the PC. WlanGO does not provide Internet sharing.

### Download and install

1. Download `WlanGO-Setup-0.1.0-x64.exe` from [GitHub Releases](https://github.com/Cooanyh/wlangohost/releases).
2. Choose an installation folder during setup, including a folder on another drive. Installation does not require administrator access.
3. Start WlanGO, select **Start network**, then connect a client to the SSID shown in the app.
4. The two startup options are independent and off by default.

The installer is unsigned. Windows SmartScreen may identify its publisher as unknown. Compare the installer SHA-256 with `SHA256SUMS.txt` attached to the same release.

### Features

- Native WPF desktop UI and Windows system-tray controls.
- Windows light/dark theme support and Simplified Chinese, English, or follow-Windows language options.
- A cryptographically random first-use Wi-Fi password protected with the current Windows user's DPAPI.
- Closing the window keeps WlanGO in the tray. Choosing **Exit** stops the network and cleans up the backend process.
- Add any `.exe` as a named launch item and enable or disable each item independently. With multiple items, choose apps each time or start all enabled items together. The tray has a second-level launch menu with individual entries.
- SuperDisplay is included as a default launch item. WlanGO searches common install locations, can remember a selected path, starts it with its main window hidden, and applies a bounded restart policy after abnormal exits. SuperDisplay is not redistributed.
- Bounded local logs: one 64 KiB file plus two rotating copies. Logs omit passwords, device addresses, and SSIDs.

### Compatibility and network behavior

- Target: Windows 11 x64 with Wi-Fi Direct Group Owner support in the wireless adapter and driver.
- The usable band, channel, and concurrency depend on Windows and the adapter/driver. WlanGO cannot guarantee a specific band or channel.
- The network is for local Wi-Fi Direct communication only. WlanGO does not enable Mobile Hotspot, ICS, or bridging, and does not modify firewall, IP routes, DNS, drivers, or system network settings.
- Client count and connection details depend on Wi-Fi Direct/WLAN events and local interface data exposed by Windows.
- Uninstall preserves user settings under `%LOCALAPPDATA%\WlanGO`. Delete that folder manually to remove the protected credential and logs.

### Build from source

See [`desktop/README.md`](desktop/README.md) for build requirements and commands. GitHub Actions builds the x64 Release installer on a Windows runner. Pushing a `v*` tag matching the project version automatically creates a GitHub Release with the Inno Setup installer and SHA-256 checksum. Hosted runners do not provide Wi-Fi Direct hardware, so CI cannot replace a real client connection test.

### License

WlanGO's own code is licensed under GNU GPL-3.0-only. The Wi-Fi Direct sample from Microsoft's `Windows-classic-samples` retains its original MIT copyright notice. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for dependency and source attributions.

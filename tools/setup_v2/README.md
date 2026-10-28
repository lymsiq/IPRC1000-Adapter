# Windows 安装工程

此预览版安装器是 .NET Framework 4.8 WinForms 引导程序，Windows 10 1903+ / 11 已提供其运行时。主程序按 Windows x64 自包含模式发布，包含 .NET 8。

安装位置可浏览选择磁盘或父文件夹，自动附加产品专用目录；未被本产品管理的非空目录不会被覆盖。安装器依次校验所有包内文件、备份已有文件、保存原蓝牙参数、写入并读回三个 DWORD、检查外部依赖，最后创建直接指向主程序的桌面快捷方式与卸载入口。修复时相同蓝牙值不会重复写入或重置重启标记。

外部组件的真实边界：

- WebView2 缺失时可从微软下载，验证 Authenticode 签名后运行。
- WinUHid 的随包证书会加入系统 Root / TrustedPublisher。点击“立即安装”会准备所需语音驱动，证书主题与指纹记录在故障日志中；保留系统权限与供应商安装流程。安装器不关闭安全启动、不禁用驱动签名。
- VB-CABLE 不随包再分发。缺失时下载官网原包，验证安装器签名后打开官方窗口；用户需要自行确认官方安装。
- 豆包输入法不随包分发；通过官网下载安装，并配置长按右 Alt 与 CABLE Output。安装器无法替用户接受第三方协议或证明语音识别质量。
- 安装器从不主动重启。必要时可为当前安装账户创建一次性最高权限登录任务，重启登录后恢复检查并移除任务。不同管理员账户的凭据提权会使恢复任务归属于该管理员账户。
- 安装检查通过与真实语音验收分开报告。未配对、睡眠的遥控器与第三方输入法需要真实测试。

日志、安装状态与首次蓝牙配置备份保存在 `%ProgramData%\IPRC1000Adapter`。在安装器中可以恢复首次安装前的三个蓝牙参数；检测到其它程序修改这些值时停止恢复，防止覆盖新改动。卸载只删除安装文件清单内的程序文件和快捷方式，保留用户数据、共享驱动、配置备份与维护程序。文件覆盖失败时恢复本次被覆盖的文件；外部驱动安装不自动回滚。

正式发放前仍需 Windows 干净虚拟机安装、重启、修复、卸载测试及实际遥控器/豆包语音测试；此工作区只运行构建与无副作用测试，未安装到当前用户电脑。Windows 安装器未进行商业代码签名，分发会有系统信任提示。

构建流程（版本只取自主程序 csproj）：

```powershell
python tools/package.py --publish-dir <self-contained-publish> --skip-publish --assets-dir <audited-upstream-assets> --set-hid-driver <SetHidDriver.exe> --output-dir <release-dir>
python tools/build_setup.py --payload <release.zip> --output-dir <release-dir> --dotnet <dotnet.exe>
<dotnet.exe> build tools/setup/tests/SetupCoreTests.csproj -c Release
tools/setup/tests/bin/Release/net48/SetupCoreTests.exe --payload <release.zip>
python tools/setup/tests/launcher_path_test.py
```

测试覆盖目录穿越、绝对路径、NTFS 数据流、保留设备名、非规范路径、大小写重复路径、哈希篡改、遗漏清单、正常解压以及状态原子写入。`--payload` 会在临时目录完整解压发布包并逐项校验，结束后清理；不写注册表或安装驱动。启动回归测试实际经过 CMD → Windows PowerShell，验证带空格、尾反斜杠的 AppDir 参数，不启动主程序。

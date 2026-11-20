DesktopTwin v{{VERSION}}
========================================

DesktopTwin-{{VERSION}}.exe
  自带 .NET 运行时；仍依赖 Windows 的 RDP ActiveX、子会话能力及系统组件。

DesktopTwin-{{VERSION}}-NET10.exe
  需要 .NET 10 Desktop Runtime：
  https://dotnet.microsoft.com/download/dotnet/10.0

两种包使用同一套功能，程序清单都要求管理员权限。
首次连接前会说明启用系统 Child Sessions 功能。
关闭并确认或退出会注销分身，结束里面的程序，请先保存。

画面仍经过子会话和显示路径，不保证零延迟、固定帧率或固定内存占用。
本版移除 UU 切换、热键、视角锁定与串流修复；升级时清理旧版热键代理。

当前使用与排查说明：
https://github.com/crazysmile-PhD/DesktopTwin/blob/main/README.md
https://github.com/crazysmile-PhD/DesktopTwin/blob/main/docs/troubleshooting.md
这些链接指向 main，可能包含此发行版本尚未具备的变更，请同时核对版本记录。

SHA256
DesktopTwin-{{VERSION}}.exe
  {{SHA256_STANDALONE}}
DesktopTwin-{{VERSION}}-NET10.exe
  {{SHA256_FRAMEWORK}}

PowerShell 校验：
  Get-FileHash ".\DesktopTwin-{{VERSION}}.exe" -Algorithm SHA256

哈希一致只表示与这里的参考文件相同，不等于安全或签名认证。

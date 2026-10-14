# 第三方组件

拾格使用标准 Windows 与 .NET 生态依赖：

| 组件 | 用途 |
|---|---|
| Microsoft.WindowsAppSDK 1.8.260921001 及传递依赖 | WinUI 3、窗口、资源与 Windows App Runtime |
| Microsoft.NETCore.App | .NET 托管运行时 |
| Windows SDK .NET 引用与 C#/WinRT | Windows API 投影及 COM 交互 |
| Windows Shell、Segoe 系统字体 | 文件操作、系统图标、文字和系统窗口能力 |

第三方依赖的许可证、版权与运行时通知仍适用。构建脚本根据实际还原的 NuGet 包，在发行目录的 `third-party/` 中按包和版本保留其提供的许可及通知文件；这些声明属于第三方运行库。拾格图标由项目内独立几何网格绘制。

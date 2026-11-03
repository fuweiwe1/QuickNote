# 晾一会 · QuickNote

一根挂在 Windows 11 桌面上的晾衣绳。把常用话术、临时灵感和图片夹起来，用时复制，用完撕掉。

原生 WPF / .NET 10 桌面应用，无浏览器壳、账户和云端服务。默认悬浮在其他普通窗口上方，可随时收起或隐藏到系统托盘。

## 下载和运行

打开本仓库的 [Actions → Build Windows app](https://github.com/fuweiwe1/QuickNote/actions/workflows/build.yml)，选择最近一次成功的构建，在页面底部的 Artifacts 中选择一个版本下载。解压后双击 `QuickNote.exe`：

| 版本 | 适用情况 |
| --- | --- |
| `QuickNote-Windows11-x64-Portable` | 推荐。自带运行环境，无需安装 .NET。 |
| `QuickNote-Windows11-x64-Small` | 体积较小，需要预先安装 [.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/10.0)。 |

支持 Windows 11 x64。首次便携版启动会在系统临时目录释放所需原生运行库。当前构建未进行商业代码签名；GitHub Actions 下载包默认保留 30 天，可以重新运行构建获取。

## 怎么用

1. 在微信、浏览器或其他软件中复制一段文字或图片。
2. 点击绳子左侧的 **草稿本**，当前剪贴板内容就会变成一张新便签，自动夹在最左边。
3. 悬停在便签上会放大；点 **复制** 取用原始文字或图片。
4. 双击便签或点 **展开** 查看全文、编辑文字，或查看图片。图片详情支持 Ctrl + 滚轮缩放。
5. 点 **×** 会把便签沿锯齿边撕成两半。8 秒内可撤销最近一次删除。

也支持从资源管理器拖入多张图片，或复制图片文件后点击草稿本。支持 PNG、JPG/JPEG、BMP、GIF 和 TIFF（动图和多页文件只收录第一帧）。图片转换为本地 PNG，单张最多 32 MB、3200 万像素。文字最多 10 万个 UTF-16 代码单元，保留原有空格和换行。

- **搜索**：按文字内容或图片文件名筛选；不包含图片 OCR。
- **排序**：把一张便签拖到另一张上，调整位置；搜索时也会更新整体顺序。
- **翻看**：鼠标滚轮、触控横向滚动或右下角箭头。
- **移动**：拖动左上角标题或晾衣绳；位置会保存。
- **置顶**：工具栏中切换。默认开启，不会被普通应用窗口盖住。系统安全桌面、独占全屏或其他置顶窗口不保证覆盖。
- **收起 / 隐藏**：收起保留工具栏；右上角横线或 Esc 隐藏到托盘，不退出。
- **新写灵感**：工具栏 `··· → 新写一张便签`。
- **退出**：工具栏 `···` 或托盘菜单中选择退出。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl + Alt + 空格 | 全局隐藏 / 呼出 |
| Ctrl + Alt + V | 全局收录一次剪贴板并展开 |
| Ctrl + F | 在主窗口中搜索 |
| Esc | 搜索时清空关键词；主窗口中隐藏；详情中关闭 |
| Ctrl + Z | 主窗口中撤销最近一次删除，搜索框内保留文本撤销 |
| Ctrl + Enter | 在文字详情中保存 |

快捷键被其他软件占用时会提示，草稿本、菜单和托盘仍可使用。应用遵循 Windows 的“动画效果”设置，关闭系统动画后不播放悬停、进场和撕纸动效。

## 本地保存和备份

所有内容保存在：`%LOCALAPPDATA%\QuickNote\`。

```text
QuickNote/
  notebook.json       文字、顺序、图片引用和窗口偏好
  notebook.json.bak   上一次保存的恢复备份
  images/             收录时复制的图片，不依赖原文件
```

无自动剪贴板监听，不会读取聊天历史，不会上传任何便签。只有点击草稿本或使用收录快捷键时才读取当前剪贴板。数据是普通本地文件，没有额外加密。

每次修改先写入临时文件并刷新，再替换主文件；写入失败时界面保留原状态。主文件损坏时尝试从上一次备份恢复并提示，损坏原文件也会保留。若两份数据都无法读取，应用停止加载，不用空白内容覆盖。

退出应用后，复制整个目录即可备份；恢复时先退出，再还原整个目录。删除会立即从当前便签清单移除，但上一份恢复备份可能暂时保留相关内容；没有当前或备份引用的图片会在下次启动时清理。完全移除全部数据时，退出应用后删除整个目录。

## 开发

Windows 11 + .NET 10 SDK：

```powershell
dotnet build src/QuickNote/QuickNote.csproj -c Release
dotnet run --project src/QuickNote
dotnet run --project tests/QuickNote.Core.Tests -c Release
./scripts/package.ps1
```

Linux 可运行存储检查，并通过 `EnableWindowsTargeting` 交叉编译 WPF；运行和界面检查需要 Windows。

`tests/QuickNote.Core.Tests` 是不依赖第三方测试包的文件集成检查，覆盖中文和换行、重启恢复、顺序、图片独立副本、原子保存边界、数据损坏、版本兼容、大小限制及目录边界。失败即返回非零退出码。

Windows 构建还会运行 `QuickNote.exe --smoke-test artifacts/smoke`，在临时数据目录中检查真实 WPF 资源、文字和图片剪贴板收录与复制、搜索、排序、删除撤销、收起呼出，并输出界面截图。打包后的自带运行环境版本也会再次执行相同检查。**此开发模式会使用当前 Windows 会话的剪贴板**，不读写正常便签目录。截图和结果位于 Actions 的 `Windows-UI-checks` 和 `Windows-Portable-checks` 下载包。

详见 [Windows 真机验收清单](docs/windows-checklist.md)。

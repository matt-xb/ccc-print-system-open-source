# CCC 资料管理／打印系统 · 公开源码版

面向 Windows 局域网的资料管理、PDF 发布、同步、预览与打印系统，当前源码版本 0.6.29。

- 管理端：读取用户配置的资料目录，转换 Word/Excel、确认 PDF 并发布资料版本。
- 打印端：发现管理端、校验与下载资料，在离线状态下搜索、预览和打印已有 PDF。
- 软件版本更新与资料版本更新分开管理。

## 下载公开版

[GitHub Releases](https://github.com/matt-xb/ccc-print-system-open-source/releases) 提供 Windows x64 管理端和打印端 ZIP，以及打印端安装器。完整解压后运行带“公开版”的 EXE，无需另装 .NET 运行时。首次发行标为预发布，尚未完成干净电脑安装和实体打印验收；程序未签名。

公开版使用独立的程序名、安装目录和本地缓存，不读取公司版缓存。管理端默认读取包内 `资料` 目录，请在运行前将 `SourcePath` 配置为自己的绝对路径；打印端将 `ServerDeviceName` 从 `YOUR-ADMIN-PC` 改为管理端电脑名。默认端口为 8010/8011，同机运行其他管理端时需配置未占用端口。

## 构建

安装 .NET 9 SDK，在 Windows 上运行：

```powershell
dotnet build CccPrint.slnx -c Release
.\scripts\publish.ps1
```

管理端和打印端输出到 `dist/0.6.29/admin` 与 `dist/0.6.29/client`。打印所需 PDFium 从声明的 NuGet 依赖获取，源码库不保存原生 DLL；运行包包含 PDFium 和对应许可。

生成打印端安装器还需安装 Inno Setup 7：

```powershell
.\scripts\build-client-installer.ps1
```

运行 `.\scripts\package-release.ps1` 同时生成两端 ZIP 和打印端安装器；ZIP 采用明确的文件清单，不复制本地运行数据或调试文件。

## 使用自己的资料

运行前按自己的环境修改管理端 `appsettings.json` 中的 `SourcePath`、服务器地址等设置。首次运行不会携带任何业务资料。

Word/Excel 原件保留在管理端；用户确认后发布的 PDF 才同步到打印端。打印端自动发现使用 UDP 8011，资料服务默认 HTTP 8010；在自己的局域网配置相应访问权限。

## 公开范围

仅包含程序源码、图标、构建脚本和安装器源码；不包含实际 CCC 文件、证书、工艺卡、员工/财务资料、发布 PDF、管理端缓存、运行日志、本机配置、完整内部交接记录或旧私有仓库历史。

资料文件的单独公开仓库需要另行确认公开清单，本源码仓库不会连带上传资料。

后续按源码检查、提交、推送、核验的流程维护；局域网运行、资料与打印实机验收需在使用方环境完成。

## 许可

公开源码采用 [MIT License](LICENSE)。实际资料和内部业务数据不在本仓库内，也不在此次开源授权范围内；NuGet 与其他第三方依赖保留各自许可。

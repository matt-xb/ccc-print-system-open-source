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

管理端和打印端输出到 `dist/Public/0.6.29/admin` 与 `dist/Public/0.6.29/client`。打印所需 PDFium 从声明的 NuGet 依赖获取，源码库不保存原生 DLL；运行包包含 PDFium 和对应许可。

生成打印端安装器还需安装 Inno Setup 7：

```powershell
.\scripts\build-client-installer.ps1
```

运行 `.\scripts\package-release.ps1` 同时生成两端 ZIP 和打印端安装器；ZIP 采用明确的文件清单，不复制本地运行数据或调试文件。

## 一份源码，两种构建

本仓库是公司版与公开版的唯一程序源码；私有分发仓库只固定引用这里的提交，并保留公司配置。默认 `Edition=Public`；公司版使用原程序名、安装 ID、单实例标识与缓存目录，不改变已有数据位置。

```powershell
dotnet build CccPrint.slnx -c Release -p:Edition=Company
dotnet run --project tools/OperationLogChecks -c Release -p:Edition=Public
dotnet run --project tools/OperationLogChecks -c Release -p:Edition=Company
.\scripts\package-release.ps1 -Edition Company -CompanyConfig <private-config-directory> -OutputRoot <new-output>
```

私有配置目录只提供 `admin.json` 和 `client.json`；打包后分别成为双端的 `appsettings.json`。公开构建拒绝公司配置参数，所有构建拒绝覆盖已有输出目录。两种编译分别使用 `bin/Public`、`bin/Company` 和对应的 obj，避免缓存混用。

`publish.ps1`、`build-client-installer.ps1`、`package-release.ps1` 均支持同样的 Edition/CompanyConfig/OutputRoot 参数。只选一个所需入口运行；后两者会自动调用前面的步骤。公司运行数据和实际资料不打入程序包。

此次已验证两种构建、安装器与打包，不更换现用程序或覆盖旧 Release；正式发布前按原流程升版并做实机验收。

## 使用自己的资料

运行前按自己的环境修改管理端 `appsettings.json` 中的 `SourcePath`、服务器地址等设置。首次运行不会携带任何业务资料。

Word/Excel 原件保留在管理端；用户确认后发布的 PDF 才同步到打印端。打印端自动发现使用 UDP 8011，资料服务默认 HTTP 8010；在自己的局域网配置相应访问权限。

## 公开范围

仅包含程序源码、图标、构建脚本和安装器源码；不包含实际 CCC 文件、证书、工艺卡、员工/财务资料、发布 PDF、管理端缓存、运行日志、本机配置、完整内部交接记录或旧私有仓库历史。

资料文件的单独公开仓库需要另行确认公开清单，本源码仓库不会连带上传资料。

后续按源码检查、提交、推送、核验的流程维护；局域网运行、资料与打印实机验收需在使用方环境完成。

## 许可

公开源码采用 [MIT License](LICENSE)。实际资料和内部业务数据不在本仓库内，也不在此次开源授权范围内；NuGet 与其他第三方依赖保留各自许可。

## 操作日志与故障排查（2026-10-13）

公司版管理端与打印端分别在 `%LOCALAPPDATA%\CCC资料打印系统\管理端\logs\operations.log` 和 `%LOCALAPPDATA%\CCC资料打印系统\打印端\logs\operations.log`。公开版使用独立的 `CCC资料打印系统公开版` 根目录，保留已有隔离。旧 `client-日期.log` 历史文件保留，新诊断改用有容量上限的 `operations.log`。

管理端记录启动、服务监听/失败、HTTP 路由模板与状态，以及资料发布开始、取消、完成和失败；HTTP 不记录查询、正文或实际文件名。打印端记录资料同步/已是最新、管理端离线、暂无发布资料、超时、取消及打印排队结果；已有异常入口统一保留异常类型和 HResult，不再写异常全文。一次同步、发布或打印用 `id` 关联阶段和耗时。

`published` 表示正式资料版本已发布；`submitted/windows_queue_only` 表示打印任务已交给 Windows，纸张是否实际打印出来需人工确认。日志测试使用临时目录，不调用真实打印机或发布公司资料。

维护验证：`dotnet run --project tools/OperationLogChecks -c Release` 检查编号关联、轮换与不可写目录容错；按业务改动范围继续使用已有编译和 CLI/HTTP 验证。

每份新操作日志约 1 MiB，保留当前文件及 `.1`～`.3` 三份轮换文件，约 4 MiB；时间使用 UTC。日志仅记录操作标签、编号、阶段、结果、计数和耗时，不记录业务正文、凭据、客户名称、文件路径或请求参数。异常记录类型或固定错误码，具体提示仍在原界面显示。磁盘不可写时诊断失败不阻断业务，因此缺少记录不能单独证明操作没有发生。

后续维护：涉及这些操作的改动，同时核对开始、成功/取消/跳过/失败的去向；提前返回写明固定原因。新增记录不得直接序列化参数、返回正文或异常全文。完成相关验证后更新本项目权威 handoff，使用说明变化时更新本节。源码及测试可随项目同步，实际日志不提交、不进入分发包。

本节对应 2026-10-13 的源码维护，现有下载包与运行进程尚未升级；新日志从使用更新源码构建的程序开始产生。旧版本不会凭空补出历史记录。

## 2026-10-13 操作诊断日志维护（源码完成，运行包未升级）

- 已补关键操作的开始、结果、跳过/失败原因、关联编号与耗时；使用本地有限轮换记录，不新增依赖。查看位置、覆盖范围、结果含义与后续维护规则见本项目使用说明的“操作日志与故障排查”。
- 新记录只写诊断元数据，不写客户/聊天/知识库正文、请求参数、文件路径或凭据；公开副本保持应用身份、空库/通用配置及独立缓存的既有适配。原业务数据与历史记录不改。
- 验证使用合成数据/临时目录及既有相关检查。源码、测试和说明按本项目原有远端/分支同步；实际日志、缓存、程序包和业务输出不上传。
- 本次是源码维护，不改变已发布版本、不覆盖已有 Release、不替换现用 EXE/APK/ASAR、不重启本机或公网服务。现用版本尚无新增记录；下次正常发布时递增版本、构建与升级后，再做真实操作和跨重启验收。

- 本项目校验：公开版双端构建与日志检查通过，独立身份与日志目录保留；现有公开包不覆盖。

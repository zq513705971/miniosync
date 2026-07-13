# MinioSync

本地文件夹到 MinIO / S3 兼容存储的实时同步工具。基于 **.NET 8** 构建，发布产物是**自包含单文件 EXE**，目标服务器**无需预装任何 .NET 运行时**。

支持文件创建/修改/删除/重命名的实时监控与同步，包含**子目录递归同步**和**目录删除批量清理**。

---

## 目录

- [项目结构](#项目结构)
- [核心组件](#核心组件)
- [运行环境](#运行环境)
- [构建](#构建)
- [配置文件](#配置文件)
- [使用方法](#使用方法)
- [同步机制说明](#同步机制说明)
- [日志](#日志)
- [常见问题](#常见问题)

---

## 项目结构

```
MinioSync/
├── MinioCommon/               # 公共类库（配置模型、日志、辅助方法）
│   ├── SyncConfig.cs          # 单个同步任务配置模型
│   ├── ConfigFile.cs          # 配置文件根模型（Version + Configs）
│   │                          #   + JsonSerializable 部分类（元数据在编译期生成）
│   ├── ConfigManager.cs       # 配置加载/校验（System.Text.Json + source generator）
│   ├── SyncHelper.cs          # Worker 启动、路径处理、扩展名过滤
│   └── Logger.cs              # 按日期切分的日志器
│
├── MinioSync/                 # 守护进程
│   ├── Program.cs             # 配置加载 + 启动 FileSystemWatcher
│   └── FolderMonitor.cs       # FileSystemWatcher + 批量定时器
│
├── SyncWorker/                # 单文件同步 Worker（独立进程）
│   ├── Program.cs             # 命令行参数解析（async Main）
│   └── MinioUploader.cs       # 上传/删除/批量删除（Minio SDK 7.0.0）
│
├── FullSync/                  # 全量同步工具
│   └── Program.cs             # 一次性遍历本地目录并发上传
│
├── Directory.Build.props      # 解决方案级 MSBuild 配置（所有 csproj 自动继承）
│
├── deploy/                    # publish 输出目录（部署用）
├── config.json                # 配置文件
└── publish.ps1                # 发布脚本（自包含 + 单文件）
```

---

## 核心组件

| 组件 | 用途 |
|---|---|
| **MinioSync.exe** | 守护进程，长期运行，监控本地文件夹变化 |
| **SyncWorker.exe** | 单文件同步进程，处理一次上传/删除后退出；由守护进程和 FullSync 调用 |
| **FullSync.exe** | 一次性全量同步工具，遍历目录并发上传所有匹配的文件 |

**进程模型**：守护进程发现文件变更 → 启动 SyncWorker 子进程处理 → Worker 退出。这样主进程不阻塞，单个 Worker 崩溃不会影响其他文件。

---

## 运行环境

**目标运行环境**（发布产物运行所需）：
- Windows 10+ / Windows Server 2016+（.NET 8 最低支持版本）
- 无需任何预装的 .NET 运行时（自包含单文件 EXE 已捆绑 .NET 8 runtime）
- MinIO 服务端，或任意 S3 兼容服务（AWS S3、阿里云 OSS、Ceph RGW 等）

**构建机**（开发者本地需要）：
- Windows 10+ / Windows Server 2019+
- .NET 8 SDK（[下载](https://dotnet.microsoft.com/download/dotnet/8.0)）
- 约 250 MB 磁盘空间

**NuGet 依赖**（在 csproj 中用 `PackageReference` 管理，存于全局 `%USERPROFILE%\.nuget\packages\`）：
- Minio 7.0.0（仅 SyncWorker 直接使用）

---

## 构建

### 使用 publish.ps1（推荐）

```powershell
.\publish.ps1
```

发布脚本会：
1. `dotnet restore MinioSync.sln`（按 `csproj` 自动还原 NuGet 包到全局缓存）
2. `dotnet build MinioSync.sln -c Release`（Release 配置编译）
3. `dotnet publish` 三个 Exe 项目（`-r win-x64`，自包含，单文件，压缩）
4. 清空 `deploy/` 目录，把所有 EXE 与 `config.json` 写入

产物：`deploy\` 下三个单文件 EXE（每个约 30–35 MB，**压缩后**），包含 .NET 8 runtime + Minio SDK + System.Text.Json + 全部依赖。

### 手动发布（等效命令）

```powershell
dotnet restore MinioSync.sln -r win-x64
dotnet build   MinioSync.sln -c Release
dotnet publish MinioSync\MinioSync.csproj -c Release -r win-x64 -o deploy\
dotnet publish SyncWorker\SyncWorker.csproj -c Release -r win-x64 -o deploy\
dotnet publish FullSync\FullSync.csproj     -c Release -r win-x64 -o deploy\
copy /Y config.json deploy\
```

> 注意：不要传 `--no-build`，否则 GenerateBundle 任务找不到 `singlefilehost.exe` 中间文件。

### 部署

把整个 `deploy\` 目录复制到目标服务器任意位置即可。**无需在目标机器安装任何 .NET 运行时**。结构：

```
deploy/
├── MinioSync.exe            # 守护进程，自包含（含全部依赖）
├── SyncWorker.exe           # Worker，自包含（含 Minio SDK 7.0.0）
├── FullSync.exe             # 全量同步工具，自包含（含全部依赖）
├── config.json              # 配置
└── *.runtimeconfig.json     # 运行时配置（一般不需修改）
```

> `MinioCommon.dll`、`Minio.dll`、`System.Reactive.dll` 等依赖已通过 `PublishSingleFile` 打包进每个 EXE 内部，deploy/ 目录不会看到额外的 DLL 文件。


---

## 配置文件

**位置**：与 `MinioSync.exe` 同目录下的 `config.json`，或通过 `--config` 参数指定。

**格式**：

```json
{
  "Version": 1,
  "Configs": [
    {
      "Id": "project-a",
      "Enable": true,
      "LocalFolderPath": "E:\\Test\\p1",
      "MinIOEndpoint": "http://192.168.52.120:9000",
      "BucketName": "dir1",
      "AccessKey": "admin",
      "SecretKey": "admin123456",
      "SyncIntervalSeconds": 60,
      "FileStabilitySeconds": 3,
      "FileExtensions": [".txt", ".csv", ".json", ".log"],
      "ExcludeSuffixes": [".tmp", ".bak", ".swp"],
      "PathPrefix": "myproject"
    }
  ]
}
```

**字段说明**：

| 字段 | 必填 | 说明 |
|---|---|---|
| `Version` | 是 | 当前固定为 `1` |
| `Configs` | 是 | 配置数组，可包含多个同步任务 |
| `Id` | 是 | 配置唯一标识，FullSync 通过 `--config-id` 指定 |
| `Enable` | 否 | 是否启用实时监控（`false` 时 FullSync 仍可使用此配置） |
| `LocalFolderPath` | 是 | 要监控的本地文件夹，需事先存在 |
| `MinIOEndpoint` | 是 | MinIO 端点 URL（含协议和端口） |
| `BucketName` | 是 | 目标存储桶 |
| `AccessKey` | 是 | MinIO 访问密钥 |
| `SecretKey` | 是 | MinIO 秘密密钥 |
| `SyncIntervalSeconds` | 否 | 批处理定时器周期，默认 `60` |
| `FileStabilitySeconds` | 否 | 文件稳定等待时长（无写事件后多久触发上传），默认 `3` |
| `FileExtensions` | 否 | 要同步的扩展名白名单；为空/null 表示全部 |
| `ExcludeSuffixes` | 否 | 要排除的文件后缀列表（如 `[".tmp", ".bak"]`），叠加在内置排除（`.tmp`、`.bak`、`.~lock`、`~$*`）之上 |
| `PathPrefix` | 否 | 上传到 MinIO 时给对象 Key 增加的前缀，如 `"myproject/"`，未设置则对象 Key 等于相对路径 |

---

## 使用方法

### 1. 启动守护进程（实时同步）

```cmd
MinioSync.exe
```

或指定自定义路径：

```cmd
MinioSync.exe --config D:\configs\myconfig.json --logs-dir D:\logs --worker-path D:\bin\SyncWorker.exe
```

**参数**：

| 参数 | 默认 | 说明 |
|---|---|---|
| `--config <path>` | `<exeDir>\config.json` | 配置文件路径 |
| `--logs-dir <path>` | `<exeDir>\logs` | 日志目录 |
| `--worker-path <path>` | `<exeDir>\SyncWorker.exe` | SyncWorker 路径 |

按 `Ctrl+C` 优雅退出。

### 2. 全量同步

```cmd
FullSync.exe --config-id project-a
```

**参数**：

| 参数 | 必填 | 说明 |
|---|---|---|
| `--config-id <id>` | 是 | 配置文件中的 `Id` |
| `--config <path>` | 否 | 配置文件路径（默认 `<exeDir>\config.json`） |
| `--concurrency` / `-c <n>` | 否 | 并发 Worker 数，默认 `10`，传 `0` 表示不限制 |
| `--worker-path <path>` | 否 | SyncWorker 路径 |
| `--logs-dir <path>` | 否 | 日志目录 |

### 3. SyncWorker（一般不直接调用）

Worker 是守护进程和 FullSync 的子进程，由父进程传入参数：

```cmd
SyncWorker.exe ^
  --endpoint "http://192.168.52.120:9000" ^
  --bucket "dir1" ^
  --access-key "admin" ^
  --secret-key "admin123456" ^
  --file "E:\Test\p1\sub\doc.txt" ^
  --relative "sub\doc.txt" ^
  --action "upload" ^
  --task-id "a1b2c3d4"
```

**参数**：

| 参数 | 必填 | 说明 |
|---|---|---|
| `--endpoint` | 是 | MinIO 端点 |
| `--bucket` | 是 | 存储桶 |
| `--access-key` | 是 | 访问密钥 |
| `--secret-key` | 是 | 秘密密钥 |
| `--file` | 取决于 action | 本地文件路径（`delete-prefix` 不需要） |
| `--relative` | 是 | 相对路径（用作对象 Key） |
| `--action` | 否，默认 `upload` | `upload` / `delete` / `delete-prefix` |
| `--task-id` | 否 | 任务 ID（用于日志关联） |
| `--path-prefix` | 否 | 对象 Key 前缀（与配置中的 `PathPrefix` 一致） |

**Action 说明**：
- `upload`：上传单个文件
- `delete`：删除单个对象
- `delete-prefix`：列出指定前缀下的所有对象并逐个删除（用于目录级删除）

---

## 路径前缀（PathPrefix）与后缀排除（ExcludeSuffixes）

### PathPrefix

`PathPrefix` 给所有上传到 MinIO 的对象 Key 添加统一前缀。典型场景：多套配置共用同一个存储桶时按前缀隔离。

**示例**：

```json
{
  "Id": "project-a",
  "LocalFolderPath": "E:\\Data\\project-a",
  "BucketName": "shared-bucket",
  "PathPrefix": "project-a/",
  ...
}
```

| 本地文件 | 相对路径 | 上传后的对象 Key |
|---|---|---|
| `E:\Data\project-a\doc.txt` | `doc.txt` | `project-a/doc.txt` |
| `E:\Data\project-a\sub\a.csv` | `sub/a.csv` | `project-a/sub/a.csv` |

目录删除时也自动加前缀：本地 `sub/` 被删 → MinIO 上删除 `project-a/sub/` 下所有对象。

注意：
- 自动补 `/`：`"project-a"` 和 `"project-a/"` 等价
- 空字符串或未设置 → 对象 Key 等于相对路径（默认行为）

### ExcludeSuffixes

`ExcludeSuffixes` 列出**额外**要忽略的文件后缀，叠加在以下内置排除规则之上：

| 规则 | 说明 |
|---|---|
| `.tmp` | 临时文件 |
| `.bak` | 备份文件 |
| `.~lock` | Office 锁定文件 |
| `~$*` | Office 临时文件（如 `~$doc.xlsx`） |

**示例**：

```json
"FileExtensions": [".txt", ".csv"],
"ExcludeSuffixes": [".swp", ".partial"]
```

效果：
- 上传：仅白名单内（`.txt`、`.csv`）且不在排除列表（`.swp`、`.partial`）的文件
- 删除/重命名监控：被排除后缀命名的文件被忽略

注意：排除是**额外**的，不会覆盖内置规则。要禁用内置排除请直接修改源码 `SyncHelper.ShouldIgnore`。

---

## 同步机制说明

### 文件监控

`MinioSync.exe` 使用 .NET `FileSystemWatcher` 监控本地文件夹：

- `IncludeSubdirectories = true` 递归监控
- `NotifyFilters = FileName | LastWrite | DirectoryName`
- 监控事件：`Created`、`Changed`、`Deleted`、`Renamed`

### 文件稳定检测

文件被持续写入时，FSW 会触发大量 `Changed` 事件。**文件稳定检测**机制：

1. 每次事件更新 `_pendingChanges[key].LastEventTime = UtcNow`
2. `OnBatchTimer` 每 `SyncIntervalSeconds` 触发一次
3. 只处理 `LastEventTime` 距今 ≥ `FileStabilitySeconds` 秒的条目

这样可以避免文件还没写完就上传到 MinIO。

### 删除处理

**单文件删除**：FSW 触发 `Deleted` → 加入 `delete` 队列 → Worker 调用 `_client.DeleteObjectAsync(...)`。

**目录删除**（关键功能）：FSW 对目录删除只触发一个 `Deleted` 事件（不会为目录内的每个文件单独触发）：

1. FSW 触发目录的 `Deleted`
2. 检测到路径无扩展名 → 识别为目录删除
3. 加入 `delete-prefix` 队列（带前缀，如 `例子/`）
4. Worker 调用 `DeleteObjectsByPrefixAsync()`：
   - 用 **Minio SDK 7.0.0** 的 `ListObjectsEnumAsync` 列出该前缀下的所有对象（`IAsyncEnumerable<Item>`；该方法在 Minio SDK 6.0.3 修复了 4.0.0 时代的 SIGV4 签名 bug）
   - 用 SDK `RemoveObjectsAsync` **一次性批量删除**（最多 1000 个）；批量失败时回退到逐个删除以最大化成功率

### 并发模型

- **守护进程**：发现稳定的文件后立即 spawn Worker（无限并发，但每个 Worker 独立进程，不会阻塞主进程）
- **FullSync**：通过 `Semaphore` 控制并发数（默认 10）

---

## 日志

日志目录：`logs/`（或 `--logs-dir` 指定）

按日期切分，每个组件生成独立文件：

- `sync-YYYY-MM-DD.log` — 守护进程
- `worker-YYYY-MM-DD.log` — Worker 子进程
- `fullsync-YYYY-MM-DD.log` — FullSync 工具

每条日志格式：`<时间> [<级别>] [任务ID] <消息>`

例：
```
[2026-07-12 10:30:10.121] [信息] [2163bd8c] SyncWorker: action=delete-prefix, file=例子/
[2026-07-12 10:30:10.228] [错误] [2163bd8c] MinIO 列出对象失败: HTTP 403 Forbidden ...
```

---

## 常见问题

### 1. 构建失败：缺少 .NET 8 SDK

请安装 .NET 8 SDK：

```cmd
# 推荐使用 winget 安装（Windows 11 / Win10 最新版自带 winget）
winget install --id Microsoft.DotNet.SDK.8 -e --source winget --accept-package-agreements --accept-source-agreements
```

或直接下载：[https://dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

安装后验证：

```cmd
dotnet --version
```

应输出 `8.x.x`。

### 2. 是否需要在服务器上装 .NET？

**不需要**。`publish.ps1` 默认发布 `self-contained + single file` 模式，每个 EXE 文件已经包含 .NET 8 runtime + 全部 NuGet 依赖，目标服务器开箱即用。

如果想去掉 .NET runtime 减小体积（可去掉 ~15 MB/每个 EXE），在 `publish.ps1` 中把 `$publishProps` 里的 `'-p:SelfContained=true'` 删掉，并确保目标机器已装 .NET 8 Runtime 或更高。**一般不推荐**：自己打包比依赖系统更可控。

### 3. 子目录删除没有同步到 MinIO

检查：
- `config.json` 中 `Enable: true`
- 日志中是否出现 `SyncWorker: action=delete-prefix`
- MinIO 中该前缀下确实有对象（`delete-prefix` 通过 SDK 列出并**批量**删除该前缀下的对象）

### 4. 403 SignatureDoesNotMatch

如果上传或列表时报这个错误，说明服务端拒绝了 SigV4 签名。检查：
- AccessKey/SecretKey 是否正确
- 目标服务端时间与本机时间偏差是否过大（>15 分钟会导致签名失效）
- 如使用 MinIO，确认 `MinIOEndpoint` 是 `http://...`（非 `https`）且端口可达

### 5. Minio SDK 已升级到 7.0.0，为什么？

项目已迁移到 .NET 8，可以原生使用新版 Minio SDK：
- **Minio 7.0.0**（2025-11 发布）原生支持 `net8.0/net9.0/net10.0`
- **ListObjectsEnumAsync 签名 bug** 已在 6.0.3 修复（`IAsyncEnumerable<Item>`）；7.0.0 进一步重写为类型完善的异步序列
- 因此 `MinioUploader.cs` 中原本手写的 ~200 行 AWS SigV4 + XML 解析代码全部删除，列表完全委托给 SDK

### 6. 中文文件名/路径

完全支持中文路径。对象 Key 在 MinIO / S3 中使用 UTF-8 编码，路径分隔符统一转换为 `/`。

---

## 版本

v1.0

当前架构针对单台机器本地文件夹到 MinIO 的实时同步设计。
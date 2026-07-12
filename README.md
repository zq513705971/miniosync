# MinioSync

本地文件夹到 MinIO 存储桶的实时同步工具。基于 .NET Framework 4.6.1。

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
├── MinioCommon/           # 公共类库（配置模型、日志、辅助方法）
│   ├── SyncConfig.cs      # 单个同步任务配置模型
│   ├── ConfigFile.cs      # 配置文件根模型（Version + Configs）
│   ├── ConfigManager.cs   # 配置加载/校验
│   ├── SyncHelper.cs      # Worker 启动、路径处理、扩展名过滤
│   └── Logger.cs          # 按日期切分的日志器
│
├── MinioSync/             # 守护进程
│   └── FolderMonitor.cs   # FileSystemWatcher + 批量定时器
│
├── SyncWorker/            # 单文件同步 Worker
│   ├── Program.cs         # 命令行参数解析
│   └── MinioUploader.cs   # 上传/删除（列表用自实现 AWS SigV4 HTTP）
│
├── FullSync/              # 全量同步工具
│   └── Program.cs         # 一次性遍历本地目录并发上传
│
├── packages/              # NuGet 包还原目录
├── deploy/                # 构建产物输出目录（部署用）
├── logs/                  # 运行日志（运行时生成）
├── config.json            # 配置文件
└── build.ps1              # 构建脚本
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

- Windows 7+
- .NET Framework 4.6.1
- MSBuild 14.0（VS 2015 Build Tools）或更高
- MinIO 服务端（任意 S3 兼容实现均可）

**NuGet 依赖**（已在 `SyncWorker/packages.config` 中声明）：
- Minio 4.0.0（仅用于单文件上传/删除）
- Crc32.NET 1.2.0
- Newtonsoft.Json 13.0.1
- System.Reactive 4.0.0
- System.Reactive.Linq 4.0.0
- System.ValueTuple 4.4.0

构建时使用 `.nuget\nuget.exe` 还原依赖到 `packages/` 目录。

---

## 构建

### 使用 build.ps1

```powershell
.\build.ps1
```

构建脚本会：
1. 用 MSBuild 14.0 编译解决方案
2. 清空 `deploy/` 目录
3. 复制所有可执行文件和 DLL 到 `deploy/`

### 手动构建

```powershell
& "C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe" `
    "MinioSync.sln" /p:Configuration=Debug /t:Rebuild
```

部署时需要拷贝以下文件到同一目录：
- `MinioSync.exe`、`MinioSync.pdb`
- `SyncWorker.exe`、`SyncWorker.pdb`
- `FullSync.exe`、`FullSync.pdb`
- `MinioCommon.dll`、`MinioCommon.pdb`
- `config.json`

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
      "FileExtensions": [".txt", ".csv", ".json", ".log"]
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
| `FileExtensions` | 否 | 要同步的扩展名列表；为空/null 表示全部 |

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

**Action 说明**：
- `upload`：上传单个文件
- `delete`：删除单个对象
- `delete-prefix`：列出指定前缀下的所有对象并逐个删除（用于目录级删除）

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

**单文件删除**：FSW 触发 `Deleted` → 加入 `delete` 队列 → Worker 调用 `RemoveObjectAsync`。

**目录删除**（关键功能）：FSW 对目录删除只触发一个 `Deleted` 事件（不会为目录内的每个文件单独触发）：

1. FSW 触发目录的 `Deleted`
2. 检测到路径无扩展名 → 识别为目录删除
3. 加入 `delete-prefix` 队列（带前缀，如 `例子/`）
4. Worker 调用 `DeleteObjectsByPrefix()`：
   - **用 `HttpClient` + 手写 AWS SigV4 列出该前缀下的所有对象**（绕开 Minio SDK 4.0.0 的 `ListObjectsAsync` 签名 bug）
   - 逐个调用 `RemoveObjectAsync` 删除

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

### 1. 编译失败：缺少 SDK

本项目使用**老式 csproj + packages.config**，不依赖 .NET SDK。只需 MSBuild 14.0（VS 2015 Build Tools）+ .NET Framework 4.6.1 引用程序集。

构建前先还原 NuGet 包：

```cmd
.nuget\nuget.exe restore MinioSync.sln
```

### 2. 运行时找不到 DLL

确认 `MinioCommon.dll`、`Minio.dll`、`Newtonsoft.Json.dll`、`System.Reactive.dll` 等 NuGet 包都在 `deploy/` 目录。

### 3. 子目录删除没有同步到 MinIO

检查：
- `config.json` 中 `Enable: true`
- 日志中是否出现 `SyncWorker: action=delete-prefix`
- MinIO 中该前缀下确实有对象（`delete-prefix` 通过 HTTP 列出该前缀的对象并删除）

### 4. 403 SignatureDoesNotMatch

如果列表对象时报这个错误，说明 MinIO 服务端拒绝了 SigV4 签名。检查：
- AccessKey/SecretKey 是否正确
- MinIO 服务端时间是否与本机时间偏差过大（>15 分钟会导致签名失效）

### 5. Minio SDK 兼容性

项目使用 **Minio 4.0.0**，原因是该版本原生支持 `net46`（旧 SDK 风格），构建时无需 .NET SDK。新版 MinIO（v7+）只支持 `netstandard2.0`，需要 SDK 风格项目。

**注意**：`ListObjectsAsync` 在 Minio SDK 4.0.0 上签名计算有 bug（PUT/DELETE 都正常，唯独列表 GET 请求的签名错误）。本项目绕开这个 bug，列表改用 `HttpClient` 直接调用 MinIO REST API 并手写 AWS SigV4 签名。

### 6. 中文文件名/路径

完全支持中文路径。对象 Key 在 MinIO 中使用 UTF-8 编码，路径分隔符统一转换为 `/`。

---

## 版本

v1.0

当前架构针对单台机器本地文件夹到 MinIO 的实时同步设计。
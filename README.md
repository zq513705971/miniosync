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
├── MinioCommon/               # 公共类库（配置模型、MinIO 客户端、日志、辅助方法）
│   ├── SyncConfig.cs          # 单个同步任务配置模型（含 MinIOProfile 引用）
│   ├── ConfigFile.cs          # 配置文件根模型（Version + MinIOProfiles + Configs）
│   │                          #   + JsonSerializable 部分类（元数据在编译期生成）
│   ├── ConfigManager.cs       # 配置加载/校验（解析 MinIOProfile 引用，System.Text.Json + source generator）
│   ├── SyncHelper.cs          # 路径处理、扩展名过滤
│   ├── MinioUploader.cs       # 上传/删除/批量删除（Minio SDK 7.0.0），所有 Exe 共享
│   ├── MinioReader.cs         # 下载/列表（Minio SDK 7.0.0），供 Minio2MinioSync 使用
│   ├── Logger.cs              # 按日期切分的日志器
│   └── ErrorLog.cs            # 上传失败路径记录（按 configId+日期+PID 分文件）
│
├── MinioSync/                 # 守护进程（实时监控）→ 发布为 mms.exe
│   ├── Program.cs             # 配置加载 + 启动 FileSystemWatcher
│   └── FolderMonitor.cs       # FSW + 批量定时器 + 进程内 ThreadPool 多线程上传
│
├── SyncWorker/                # 单文件 CLI 工具（供外部脚本手工调用）→ 发布为 mss.exe
│   └── Program.cs             # 命令行参数解析（async Main），调用 MinioCommon.MinioUploader
│
├── FullSync/                  # 一次性全量同步工具 → 发布为 mfs.exe
│   └── Program.cs             # 目录扫描/文件列表(--list) + 进程内 ThreadPool 多线程上传 + 失败记录
│
├── Minio2MinioSync/           # MinIO → MinIO 一次性全量同步工具 → 发布为 m2ms.exe
│   └── Program.cs             # --source + --target 两个 config-id，跨 MinIO 复制对象
│
├── Directory.Build.props      # 解决方案级 MSBuild 配置（所有 csproj 自动继承）
│
├── deploy/                    # publish 输出目录（部署用），含 mms/mfs/mss/m2ms.exe
├── config.json                # 配置文件
└── publish.ps1                # 发布脚本（自包含 + 单文件 + 重命名为短名）
```

---

## 核心组件

| 组件 | 项目 | 发布 EXE | 场景 | 进程模型 |
|---|---|---|---|---|
| **MinioSync** | MinioSync/ | **mms.exe** (Minio Monitor Sync) | 实时监控本地文件夹，自动同步到 MinIO | 进程内多线程 |
| **FullSync** | FullSync/ | **mfs.exe** (Minio Full Sync) | 一次性全量初始化（首次部署、补传历史文件、`--list` 文件列表模式恢复特定文件集） | 进程内多线程 |
| **SyncWorker** | SyncWorker/ | **mss.exe** (Minio Single Sync) | 外部脚本/工具手工调用，一次处理一个文件 | 单进程单文件 CLI |
| **Minio2MinioSync** | Minio2MinioSync/ | **m2ms.exe** (Minio to Minio Sync) | MinIO → MinIO 一次性全量同步（迁移/灾备/双写） | 进程内多线程 |

> **命名规则**：源码项目名沿用旧名（MinioSync/FullSync/SyncWorker），发布后的 EXE 用短名便于在服务器上大量部署时识别。`publish.ps1` 自动重命名。

**关键设计**：`MinioUploader`（含 MinioClient 连接）和 `MinioReader`（源 MinIO 下载）在 MinioCommon 类库，所有 Exe 都引用。守护进程、全量同步、MinIO→MinIO 同步**全部在进程内**通过 ThreadPool + SemaphoreSlim 完成，避免每次操作都启停 .NET 进程的开销；MinioClient 复用、连接池保温，多线程间通过 `SemaphoreSlim` 限并发。SyncWorker 保留独立进程模式，仅用于**外部一次性调用**（如运维手动触发某个特定文件的上传/删除）。

四者职责互不重叠：
- **MinioSync (mms)** = 监控（持续运行）
- **FullSync (mfs)** = 本地文件夹 → MinIO 一次性初始化 + 文件列表恢复
- **Minio2MinioSync (m2ms)** = MinIO → MinIO 一次性全量同步
- **SyncWorker (mss)** = 外部调用接口（手工/脚本触发）

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
- Minio 7.0.0（引用在 MinioCommon，所有 Exe 通过 MinioCommon 共享使用）

---

## 构建

### 使用 publish.ps1（推荐）

```powershell
.\publish.ps1
```

发布脚本会：
1. `dotnet restore MinioSync.sln`（按 `csproj` 自动还原 NuGet 包到全局缓存）
2. `dotnet build MinioSync.sln -c Release`（Release 配置编译）
3. `dotnet publish` 四个 Exe 项目（`-r win-x64`，自包含，单文件，压缩）
4. 把每个发布产物的 EXE 重命名为短名（MinioSync → mms、FullSync → mfs、SyncWorker → mss、Minio2MinioSync → m2ms）
5. 清空 `deploy/` 目录，把所有 EXE 与 `config.json` 写入

产物：`deploy\` 下四个单文件 EXE（每个约 35–40 MB，**压缩后**），包含 .NET 8 runtime + Minio SDK + System.Text.Json + 全部依赖。

### 手动发布（等效命令）

```powershell
dotnet restore MinioSync.sln -r win-x64
dotnet build   MinioSync.sln -c Release
dotnet publish MinioSync\MinioSync.csproj -c Release -r win-x64 -o deploy\
dotnet publish SyncWorker\SyncWorker.csproj -c Release -r win-x64 -o deploy\
dotnet publish FullSync\FullSync.csproj     -c Release -r win-x64 -o deploy\
dotnet publish Minio2MinioSync\Minio2MinioSync.csproj -c Release -r win-x64 -o deploy\
copy /Y config.json deploy\
ren deploy\MinioSync.exe        mms.exe
ren deploy\SyncWorker.exe       mss.exe
ren deploy\FullSync.exe         mfs.exe
ren deploy\Minio2MinioSync.exe  m2ms.exe
```

> 注意：不要传 `--no-build`，否则 GenerateBundle 任务找不到 `singlefilehost.exe` 中间文件。

### 部署

把整个 `deploy\` 目录复制到目标服务器任意位置即可。**无需在目标机器安装任何 .NET 运行时**。结构：

```
deploy/
├── mms.exe                   # MinioSync 守护进程 → MinIO Monitor Sync
├── mfs.exe                   # FullSync 一次性全量同步 → Minio Full Sync
├── m2ms.exe                  # Minio2MinioSync 跨 MinIO 同步 → Minio to Minio Sync
├── mss.exe                   # SyncWorker 单文件 CLI → Minio Single Sync
├── config.json               # 配置
└── *.runtimeconfig.json      # 运行时配置（一般不需修改）
```

> `MinioCommon.dll`、`Minio.dll`、`System.Reactive.dll` 等依赖已通过 `PublishSingleFile` 打包进每个 EXE 内部，deploy/ 目录不会看到额外的 DLL 文件。


---

## 配置文件

**位置**：与各 EXE 同目录下的 `config.json`，或通过 `--config` 参数指定。

**格式**：

```json
{
  "Version": 1,

  "MinIOProfiles": {
    "default": {
      "Endpoint": "http://192.168.52.120:9000",
      "BucketName": "dir1",
      "AccessKey": "admin",
      "SecretKey": "admin123456"
    }
  },

  "Configs": [
    {
      "Id": "project-a",
      "Enable": true,
      "LocalFolderPath": "E:\\Test\\p1",
      "MinIOProfile": "default",
      "SyncIntervalSeconds": 60,
      "FileStabilitySeconds": 3,
      "FileExtensions": [".txt", ".csv", ".json", ".log"],
      "ExcludeSuffixes": [".tmp", ".bak", ".swp"],
      "PathPrefix": "myproject/",
      "MaxConcurrentUploads": 10
    },
    {
      "Id": "remote-source",
      "MinIOEndpoint": "http://minio-source.example.com:9000",
      "BucketName": "source-bucket",
      "AccessKey": "source-key",
      "SecretKey": "source-secret",
      "PathPrefix": "incoming/"
    },
    {
      "Id": "remote-target",
      "MinIOEndpoint": "http://minio-target.example.com:9000",
      "BucketName": "target-bucket",
      "AccessKey": "target-key",
      "SecretKey": "target-secret",
      "PathPrefix": "archive/"
    }
  ]
}
```

**根级字段说明**：

| 字段 | 必填 | 说明 |
|---|---|---|
| `Version` | 是 | 当前固定为 `1` |
| `MinIOProfiles` | 否 | 命名 MinIO 连接配置字典，供各 Config 通过 `MinIOProfile` 引用 |
| `Configs` | 是 | 配置数组，可包含多个同步任务（也用作 m2ms 的源/目标配置） |

**MinIOProfiles 字段说明**：

| 字段 | 必填 | 说明 |
|---|---|---|
| `Endpoint` | 是 | MinIO 端点 URL（含协议和端口） |
| `BucketName` | 是 | 目标存储桶 |
| `AccessKey` | 是 | MinIO 访问密钥 |
| `SecretKey` | 是 | MinIO 秘密密钥 |
| `Region` | 否 | 区域，默认 `us-east-1` |

**Config 字段说明**：

| 字段 | 必填 | 说明 |
|---|---|---|
| `Id` | 是 | 配置唯一标识，FullSync 通过 `--config-id` 指定 |
| `Enable` | 否 | 是否启用实时监控（`false` 时 FullSync 仍可使用此配置） |
| `LocalFolderPath` | 是 | 要监控的本地文件夹，需事先存在 |
| `MinIOProfile` | 否 | 引用 `MinIOProfiles` 中的命名配置。设置后 MinIO 连接字段（`MinIOEndpoint`、`BucketName` 等）可省略 |
| `MinIOEndpoint` | 否 | MinIO 端点 URL（仅在未引用 Profile 或需覆盖时设置） |
| `BucketName` | 否 | 目标存储桶（仅在未引用 Profile 或需覆盖时设置） |
| `AccessKey` | 否 | MinIO 访问密钥（仅在未引用 Profile 或需覆盖时设置） |
| `SecretKey` | 否 | MinIO 秘密密钥（仅在未引用 Profile 或需覆盖时设置） |
| `SyncIntervalSeconds` | 否 | 批处理定时器周期，默认 `60` |
| `FileStabilitySeconds` | 否 | 文件稳定等待时长（无写事件后多久触发上传），默认 `3` |
| `FileExtensions` | 否 | 要同步的扩展名白名单；为空/null 表示全部 |
| `ExcludeSuffixes` | 否 | 要排除的文件后缀列表（如 `[".tmp", ".bak"]`），叠加在内置排除（`.tmp`、`.bak`、`.~lock`、`~$*`）之上 |
| `PathPrefix` | 否 | 上传到 MinIO 时给对象 Key 增加的前缀，如 `"myproject/"`。支持多级路径，如 `"project-a/data/images/"`；未设置则对象 Key 等于相对路径。用于 m2ms 时是源/目标各自的前缀 |
| `MaxConcurrentUploads` | 否 | 进程内并发上传数（`SemaphoreSlim` 上限）。用于 MinioSync/FullSync/m2ms 的多线程节流，默认 `10`，传 `0` 表示不限 |

---

## 使用方法

### 1. 启动守护进程（实时同步）— `mms.exe`

```cmd
mms.exe
```

或指定自定义路径：

```cmd
mms.exe --config D:\configs\myconfig.json --logs-dir D:\logs
```

**参数**：

| 参数 | 默认 | 说明 |
|---|---|---|
| `--config <path>` | `<exeDir>\config.json` | 配置文件路径 |
| `--logs-dir <path>` | `<exeDir>\logs` | 日志目录 |

按 `Ctrl+C` 优雅退出。

### 2. 全量同步 — `mfs.exe`

**目录扫描模式**（默认）—— 扫描配置 `LocalFolderPath` 下所有匹配文件：

```cmd
mfs.exe --config-id project-a
```

**文件列表模式** —— `--list <path>` 指定一个文本文件，每行一个文件的完整路径，仅处理列表中的文件：

```cmd
mfs.exe --config-id project-a --list C:\lists\backup.txt
```

`backup.txt` 示例（空行和 `#` 开头视为注释）：

```
# 2026-07-13 待恢复文件
E:\Data\docs\report.pdf
E:\Data\docs\figure.png
E:\Backups\重要合同.docx
```

列表中不存在的文件会被跳过（warn 级别），匹配 `FileExtensions` / `ExcludeSuffixes` 的会被过滤掉。处理逻辑（限并发、MinioClient 复用、上传结果统计）跟目录模式一致；相对路径仍按配置的 `LocalFolderPath` 计算（不在目录下的文件用全路径作为对象 Key）。

**参数**：

| 参数 | 必填 | 说明 |
|---|---|---|
| `--config-id <id>` | 是 | 配置文件中的 `Id` |
| `--config <path>` | 否 | 配置文件路径（默认 `<exeDir>\config.json`） |
| `--list <path>` | 否 | 文件列表路径（每行一个文件完整路径）。未传则走目录扫描模式 |
| `--concurrency` / `-c <n>` | 否 | 进程内并发上传数；未传时用配置 `MaxConcurrentUploads`（默认 10），传 `0` 表示不限 |
| `--logs-dir <path>` | 否 | 日志目录 |

#### 上传失败重试

mfs 运行中上传失败的文件路径会自动记录到错误日志文件（路径**相对于**该配置的 `LocalFolderPath`）：

```
logs/error-YYYY-MM-DD-{configId}-{pid}.txt
```

例如：`logs/error-2026-07-13-project-a-8272.txt`。每行一个相对路径，可用作 `--list` 的参数进行重试：

```cmd
mfs.exe --config-id project-a --list logs\error-2026-07-13-project-a-8272.txt
```

`--list` 会自动将相对路径拼接上配置的 `LocalFolderPath`，也同时支持绝对路径（便于手动编辑后使用）。错误文件名包含 `configId`，多配置运行时可以从文件名直接看出是哪个配置的失败记录。

### 3. MinIO → MinIO 同步 — `m2ms.exe`

把一个 MinIO bucket 的对象全量复制到另一个 MinIO bucket。典型场景：
- 跨区域灾备（源 MinIO → 异地 MinIO）
- 跨云迁移（本地 MinIO → AWS S3 / 阿里云 OSS，需 endpoint 替换）
- 双写同步

**用法**：`--source <config-id>` 指定源 MinIO 配置，`--target <config-id>` 指定目标 MinIO 配置，两个 ID 都来自同一份 `config.json`。

```cmd
m2ms.exe --source remote-source --target remote-target
```

两个配置都引用各自独立的 MinIO 连接（`MinIOEndpoint`/`BucketName`/`AccessKey`/`SecretKey`/`PathPrefix`）。

**工作流**：
1. 用源配置的 `MinIOReader` 列出源 bucket 中所有匹配 `PathPrefix` 前缀的对象
2. 每个对象：下载到内存 → 写入临时文件 → 上传到目标 bucket 的 `PathPrefix` 前缀下
3. 上传失败的对象路径写入错误日志 `logs/error-...-{target-config-id}-{pid}.txt`

**对象 Key 转换**：

| 源对象 Key | 源 PathPrefix | 目标 PathPrefix | 目标对象 Key |
|---|---|---|---|
| `incoming/2026/07/a.csv` | `incoming/` | `archive/` | `archive/2026/07/a.csv` |
| `bronze/blobs/app1.json` | `bronze/` | (无) | `blobs/app1.json` |

即：去掉源前缀、拼上目标前缀。

**参数**：

| 参数 | 必填 | 说明 |
|---|---|---|
| `--source <id>` | 是 | 源 MinIO 连接的 `config-id` |
| `--target <id>` | 是 | 目标 MinIO 连接的 `config-id` |
| `--config <path>` | 否 | 配置文件路径（默认 `<exeDir>\config.json`） |
| `--concurrency` / `-c <n>` | 否 | 进程内并发复制数；未传时用目标配置的 `MaxConcurrentUploads`（默认 10），传 `0` 表示不限 |
| `--logs-dir <path>` | 否 | 日志目录 |

> 注：源/目标两个配置的 MinIO 连接信息都可以独立配置（同一个 `config.json` 内可有任意多个 Config 条目）。`LocalFolderPath`/`SyncIntervalSeconds`/`Enable`/`FileExtensions` 等本地文件相关字段在 m2ms 场景下**被忽略**，只有 MinIO 相关字段会被使用。

### 4. SyncWorker（供外部脚本/工具手工调用）— `mss.exe`

**注意**：守护进程 mms 和全量工具 mfs **不再调用** SyncWorker。SyncWorker 仅供外部脚本、运维手工或第三方工具做**单文件一次性操作**（上传/删除）。

```cmd
mss.exe ^
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

**多层前缀**：`PathPrefix` 直接支持多级文件夹，按需拼接即可。例如：

```json
"PathPrefix": "project-a/data/images/"
```

| 本地文件 | 相对路径 | 上传后的对象 Key |
|---|---|---|
| `E:\Data\project-a\doc.txt` | `doc.txt` | `project-a/data/images/doc.txt` |
| `E:\Data\project-a\sub\a.csv` | `sub/a.csv` | `project-a/data/images/sub/a.csv` |

目录删除时前缀同样自动叠加：本地 `sub/` 被删 → MinIO 上删除 `project-a/data/images/sub/` 下所有对象。

注意：
- 自动补 `/`：`"project-a"` 和 `"project-a/"` 等价
- 多级路径用 `/` 分隔即可，如 `"level1/level2/level3/"`
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

`mms.exe` 使用 .NET `FileSystemWatcher` 监控本地文件夹：

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
3. 入队为 `delete-prefix` 任务，带前缀（如 `例子/`）
4. 进程内调用 `MinioUploader.DeleteObjectsByPrefixAsync()`：
   - 用 **Minio SDK 7.0.0** 的 `ListObjectsEnumAsync` 列出该前缀下的所有对象（`IAsyncEnumerable<Item>`；该方法在 Minio SDK 6.0.3 修复了 4.0.0 时代的 SIGV4 签名 bug）
   - 用 SDK `RemoveObjectsAsync` **一次性批量删除**（最多 1000 个）；批量失败时回退到逐个删除以最大化成功率

### 并发模型（进程内多线程）

守护进程和 FullSync 都采用**进程内多线程**模式，**不再 spawn 任何子进程**。`MinioUploader` 实例（包括其底层的 MinioClient 连接）在进程内被多线程共享。

**守护进程**：
- `FolderMonitor.OnBatchTimer` 每个 tick（默认 60 秒）扫描稳定任务
- 每个稳定任务作为 work item 入队到 `ThreadPool`
- 通过 `SemaphoreSlim(MaxConcurrentUploads)` 限并发（默认 10）
- work item 调用共享的 `MinioUploader.UploadFileAsync` / `DeleteObjectAsync` / `DeleteObjectsByPrefixAsync`
- `MinioClient` 连接池在多次上传间复用，无需每次新建

**FullSync**：
- 启动时构建一个 `MinioUploader` 实例
- 遍历本地目录，把所有匹配文件作为 work item 入队 `ThreadPool`
- 同样通过 `SemaphoreSlim` 限并发（默认 10，可被 `--concurrency` 覆盖）
- 等所有 work item 完成

**调优 `MaxConcurrentUploads`**：

| 值 | 行为 |
|---|---|
| `10`（默认） | 适合大多数情况，CPU/网络均衡 |
| `0` | 不限并发，所有任务同时跑（受网络带宽/MinIO 服务端限制） |
| `3`~`5` | 网络较慢或 MinIO 服务端资源有限时降低并发 |

**SyncWorker** 保持独立进程模式，但**仅供外部脚本手工调用**，不参与守护进程或 FullSync 的内部流程。

---

## 日志

日志目录：`logs/`（或 `--logs-dir` 指定）

按日期切分，每个组件生成独立文件：

- `sync-YYYY-MM-DD-<pid>.log` — 守护进程（包含 FSW 事件、进程内上传/删除的所有日志）
- `fullsync-YYYY-MM-DD-<pid>.log` — FullSync 工具
- `mfs-YYYY-MM-DD-<pid>.log` — FullSync（`mfs.exe`）一次性全量同步
- `mms-YYYY-MM-DD-<pid>.log` — MinioSync（`mms.exe`）守护进程（包含 FSW 事件、进程内上传/删除的所有日志）
- `m2ms-YYYY-MM-DD-<pid>.log` — Minio2MinioSync（`m2ms.exe`）跨 MinIO 复制
- `mss-YYYY-MM-DD-<pid>.log` — SyncWorker（**仅当外部手工调用 mss.exe 时才会生成**；守护进程和 mfs 不再调用 SyncWorker，所以正常运行时不会有此文件）
- `error-YYYY-MM-DD-{configId}-<pid>.txt` — **上传失败记录**（`mfs` 和 `mms` 守护进程共享）。每行一个**相对于** `LocalFolderPath` 的路径，可配合 `mfs.exe --list` 重试

`<pid>` 是进程 ID（`Environment.ProcessId`），同一组件多次启动会生成多个文件，**不会互相覆盖**。例：`fullsync-2026-07-13-8272.log`、`fullsync-2026-07-13-4108.log`。

错误文件名中的 `{configId}` 是对应的配置标识（如 `project-a`），多配置运行时可以清晰区分不同配置的上传失败记录。例：`error-2026-07-13-project-a-8272.txt`、`error-2026-07-13-project-b-8272.txt`。

每条日志格式：`<时间> [<级别>] [任务ID] <消息>`

例（守护进程删除子目录）：
```
[2026-07-12 10:30:10.121] [信息] [2163bd8c] delete-prefix 排队: 例子/
[2026-07-12 10:30:10.250] [信息] [2163bd8c] 列出前缀 '例子/' 下的对象...
[2026-07-12 10:30:10.418] [信息] [2163bd8c] 找到 5 个对象，开始批量删除...
[2026-07-12 10:30:10.612] [信息] [2163bd8c] 批量删除完成: 成功=5, 失败=0
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
- 守护进程日志（`logs/sync-*.log`）里应出现 `delete-prefix 排队: 子目录/`，紧接着是列出和批量删除日志
- MinIO 中该前缀下确实有对象（`DeleteObjectsByPrefixAsync` 通过 SDK 的 `ListObjectsEnumAsync` 列出后用 `RemoveObjectsAsync` 批量删除；批量失败时 SDK 会回退到逐个删除）

如果日志里完全没出现 `delete-prefix`，说明 FSW 没检测到目录删除事件——这种情况多见于网络盘/共享盘，或 `IncludeSubdirectories` 被关掉。

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

### 7. 如何配置多个同步任务复用同一个 MinIO 连接？

在根级定义 `MinIOProfiles`，各 Config 通过 `MinIOProfile` 字段引用：

```json
{
  "MinIOProfiles": {
    "default": {
      "Endpoint": "http://192.168.52.120:9000",
      "BucketName": "shared-bucket",
      "AccessKey": "admin",
      "SecretKey": "admin123456"
    }
  },
  "Configs": [
    {
      "Id": "project-a",
      "MinIOProfile": "default",
      "LocalFolderPath": "E:\\Data\\a",
      ...
    },
    {
      "Id": "project-b",
      "MinIOProfile": "default",
      "LocalFolderPath": "E:\\Data\\b",
      ...
    }
  ]
}
```

如果某个 Config 需要覆盖 Profile 中的个别字段（如不同 Bucket），可以在 Config 内直接写对应字段，**inline 字段优先级高于 Profile 填充**：

```json
{
  "Id": "project-c",
  "MinIOProfile": "default",
  "BucketName": "other-bucket",
  "LocalFolderPath": "E:\\Data\\c",
  ...
}
```

### 8. 四个 Exe 的职责差异

| Exe | 项目 | 何时调用 | 模式 | 适用场景 |
|---|---|---|---|---|
| `mms.exe` | MinioSync | 长期运行（服务/守护进程） | 进程内多线程 | 实时监控本地文件夹变化 |
| `mfs.exe` | FullSync | 一次性手动执行 | 进程内多线程 | 首次部署、补传历史文件、灾难恢复 |
| `m2ms.exe` | Minio2MinioSync | 一次性手动执行 | 进程内多线程 | 跨 MinIO 迁移、双写、灾备 |
| `mss.exe` | SyncWorker | 外部脚本/工具按需调用 | 单文件独立进程 | 运维手动触发某个特定文件的上传/删除 |

**不要**让 mms/mfs 调用 mss——守护进程和全量同步都已经自己处理了。

---

## 版本

v1.0

当前架构针对单台机器本地文件夹到 MinIO 的实时同步设计。
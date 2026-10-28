# 进程名修改工具 / ProcNameTool

[![CI](https://github.com/BlackV5/ProcNameTool/actions/workflows/ci.yml/badge.svg)](https://github.com/BlackV5/ProcNameTool/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/BlackV5/ProcNameTool?include_prereleases&sort=semver)](https://github.com/BlackV5/ProcNameTool/releases)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

在进程列表里选中一个进程 → 填一个新名字 → 点一下，它就带着新名字跑起来。任务管理器、`tasklist`、`Get-Process`、WMI 和监控系统看到的都是新名字。

**纯用户态实现**：不加载内核驱动、不做进程注入、不修改内存中的 `EPROCESS` 或 `PEB`。

---

## ⚠️ 使用边界（请先读这一段）

这是一个**双重用途**工具。它的技术路线与恶意软件"进程伪装"（MITRE ATT&CK [T1036.010](https://attack.mitre.org/techniques/T1036/010/)）重叠，因此明确声明：

**可以用于**

- 运维兼容：老旧监控系统、守护脚本、采集 Agent 只认固定进程名，需要把进程适配成它认识的名字
- 开发测试：验证进程管理、单实例锁、崩溃重启等逻辑
- 教学研究：理解 Windows 映像名、版本信息资源、PE 结构之间的关系

**禁止用于**

- 绕过杀毒软件、EDR、游戏反作弊或任何安全审计
- 伪装恶意进程、隐藏真实身份、规避检测

作者不对任何滥用行为负责。在受管环境中使用前，请确认已获得授权。

---

## 它到底改的是哪个"名字"

Windows 里"进程名"至少是两层完全不同的东西，所以本工具对两者都做了处理：

| 你在哪里看到 | 数据来源 | 本工具的改法 |
|---|---|---|
| 任务管理器 **"进程"页**的"名称"列 | 主模块的版本资源 `FileDescription` | 改写副本 exe 的 `VS_VERSIONINFO` 资源 |
| 任务管理器 **"详细信息"页**、`tasklist`、`Get-Process`、WMI `Win32_Process` | **映像文件名** | 把 exe 复制成新文件名 |

`PID`、命令行、父进程等信息不受影响，也不做任何伪装。

## 功能

- **进程列表**：显示进程名 / PID / 描述 / 完整 exe 路径，支持关键字筛选，不用手敲路径
- **只操作你选中的那一个进程**，不批量、不自动匹配
- **三个动作**：生成副本并启动、只生成副本（不启动）、从 `.bak` 恢复
- **可选结束原进程**：避免双实例抢端口 / 抢文件锁，操作前有明确的二次确认
- **保活**：被改名的进程退出后自动重启，适合守护场景
- **以管理员身份重启**：改系统目录下的程序、或目标进程权限更高时使用
- **执行日志**：每次改写都会打印 `旧值 -> 新值`，便于核对和留痕
- **无界面自检**：`--selftest` 跑完整链路，可在目标机验证环境是否可用

## 快速开始

### 方式一：下载现成可执行文件

1. 到 [Releases](https://github.com/BlackV5/ProcNameTool/releases) 下载 `ProcNameTool-vX.Y.Z-win-x64.zip`
2. 解压，双击 `进程名修改工具.exe`

自包含单文件发布，**目标机不需要安装 .NET 运行时**。

> Windows 可能提示"已保护你的电脑"，因为发布包没有代码签名。点「更多信息」→「仍要运行」。

### 方式二：从源码构建

需要 Windows 10/11 + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/BlackV5/ProcNameTool.git
cd ProcNameTool

# 直接运行
dotnet run --project src/ProcNameTool.Gui

# 发布自包含单文件（产物在 out/）
dotnet publish src/ProcNameTool.Gui -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o out
```

## 使用步骤

1. **选中目标进程**：在列表里点一行。拿不到路径的进程会提示需要管理员权限。
2. **填新名字**：
   - `新进程名`：新文件名，不要带 `.exe`，例如 `AgentProbe`
   - `新显示描述`：任务管理器"进程"页显示的文字，留空则与新进程名相同
3. **点「生成副本并启动」**，然后选择是否结束原进程。

几点说明：

- 副本默认放在**源文件同目录**。这是刻意的：同目录下的 DLL 和配置文件才能被正常加载，换到别的目录大概率启动失败。
- 源文件**默认不改动**。只有"新进程名与原进程名相同"时才会就地改写，此时会强制生成 `.bak` 备份并弹出风险确认。
- 想撤销？删掉生成的副本即可；就地改写的用「从 .bak 恢复」。

## 无界面自检

```powershell
# 结果写入指定文件，RESULT: OK 表示链路正常
& ".\进程名修改工具.exe" --selftest selftest.txt
Get-Content selftest.txt
```

自检做的事和按钮完全一致：复制自身 → 改写 `FileDescription` → 用 Windows 的 `FileVersionInfo` 回读比对，并确认版本号未被破坏。CI 每次构建都会跑这一项。

## 工作原理

```
选中的进程 exe
      │
      ├─(1) 复制────────────────────────▶ 同目录 Agent.exe     ← 决定"详细信息"页的名字
      │
      ├─(2) BeginUpdateResource ────────▶ 改写 VS_VERSIONINFO
      │      UpdateResource                 FileDescription    ← 决定"进程"页的名字
      │      EndUpdateResource              顺手重算 PE 校验和
      │
      └─(3) CreateProcess ──────────────▶ 以新映像名启动
```

版本资源的序列化有两条容易踩的坑，本项目是踩过并修好的：

1. **`szKey` 的对齐基准**：必须按"含 6 字节节点头"的绝对偏移 `(6 + len) % 4` 计算，按相对偏移算会丢 padding，整个结构错位，Windows 直接读不到版本信息。
2. **padding 的语义**：padding 只用于对齐**后续**数据，叶子节点 value 之后不能补，且不计入 `wLength`；父节点只在相邻子节点之间补。

`Core/VersionResource.cs` 提供解析与序列化，可用"解析 → 序列化 → 与原字节逐字节比对"的不变式验证（开发期用它定位了上面两个 bug）。

## 项目结构

```
.
├── .github/workflows/ci.yml          # 构建 + 自检 + 发布 Release
├── src/ProcNameTool.Gui/
│   ├── Program.cs                    # 入口，含 --selftest 分支
│   ├── MainForm.cs                   # 全部界面与业务流程
│   ├── ProcessScanner.cs             # 进程枚举、路径与描述读取
│   ├── SelfTest.cs                   # 无界面自检
│   ├── Core/VersionResource.cs       # VS_VERSIONINFO 解析 / 改写 / 序列化
│   ├── Core/PeChecksum.cs            # PE 可选头校验和重算
│   ├── app.manifest                  # DPI 感知、UAC 声明
│   └── ProcNameTool.Gui.csproj
├── ProcNameTool.sln
├── LICENSE                           # GPL-3.0
└── README.md
```

依赖只有 .NET BCL 和 Win32 P/Invoke，**没有任何第三方包**。

## 已知限制与风险

| 风险 | 说明 | 应对 |
|---|---|---|
| **数字签名失效** | 改写版本资源会让原 Authenticode 签名作废，带自校验的软件可能拒绝加载 | 默认走"改副本"，源文件字节不变；失败就删副本回退 |
| **程序依赖自身文件名** | 有些软件靠文件名找配置、做单实例锁、加载同名 DLL | 上线前在测试机跑一轮完整业务 |
| **.NET 框架依赖程序改名失败** | `apphost` 会去找 `<新文件名>.dll`，改名后报 `The application to execute does not exist` | 先用 `dotnet publish --self-contained -p:PublishSingleFile=true` 打单文件再改名 |
| **保活仅在本工具存活期间有效** | 工具关闭后不再重启目标 | 需要常驻请改成 Windows 服务，或交给系统服务管理器 |
| **受保护进程无法处理** | 系统进程、反作弊保护的进程会拒绝访问 | 属于预期行为，不做绕过 |

## 贡献

Issue / PR 都欢迎。提 PR 前请确认：

- `dotnet build -c Release` 零警告零错误
- `--selftest` 输出 `RESULT: OK`
- 涉及版本资源序列化的改动，必须保持"解析 → 序列化"字节级往返一致

## License

[GNU General Public License v3.0](LICENSE) 或更新版本。

```
Copyright (C) 2026 ProcNameTool Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
```

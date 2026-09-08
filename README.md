# MonitorLarp

> **High-precision real-time file I/O activity monitor for Windows powered by Kernel ETW (Event Tracing for Windows).**

[![Platform](https://img.shields.io/badge/Platform-Windows%20(x64)-blue.svg)]()
[![Target](https://img.shields.io/badge/.NET%20Framework-4.7.2+-brightgreen.svg)]()
[![Backend](https://img.shields.io/badge/Engine-Microsoft.Diagnostics.Tracing.TraceEvent-orange.svg)]()
[![License](https://img.shields.io/badge/License-MIT-lightgrey.svg)]()

---

## 📌 Overview

**MonitorLarp** is a lightweight system utility with a graphical user interface designed for granular, real-time tracking of file read and write operations across all Windows storage volumes.

Unlike the default Windows Task Manager or Resource Monitor, which only provide coarse disk throughput graphs and aggregate totals, **MonitorLarp**:
1. Captures disk I/O events directly **at the OS kernel level** (via Kernel ETW), guaranteeing 100% capture accuracy without missing short-lived or transient file operations.
2. Displays the **exact file path**, **process name**, and **Process ID (PID)** for every operation.
3. Separately accounts for total read/written bytes, operation counts, and calculates average throughput (bytes/sec) across a 5-second sliding window.
4. **Aggregates activity by folder** — solving the visibility problem when tracking torrent clients, game launchers, archive unpackers, or build tools that access hundreds of small chunk files simultaneously.
5. Engineered for **unattended long-running sessions**: features built-in inactivity pruning (TTL), automatic rotation of closed sessions to a disk log, and asynchronous UI rendering that never freezes.

---

## ✨ Key Features

### 1. Grouping Modes (Aggregation)
- **Folder total (default)**: All files within the same directory are collapsed into a single summary row. Their current sizes, read/write byte totals, and live rates are aggregated. The folder header indicates the total count of touched files.
- **Separate files**: Detailed view listing every individual file touched.
- **Folder and process**: Distinguishes activities when multiple distinct applications interact with the same directory (e.g., a browser downloading while an archiver extracts).

### 2. Filtering & Thresholds
- **Path Restrictions**: The `Paths` field allows restricting monitoring to specific drives or directory trees (e.g., `C:\;D:\Games;E:\Torrents`). Leaving it blank monitors all system drives.
- **Threshold Modes**:
  - *All files (no threshold)* — Displays all intercepted events;
  - *Current file size* — Only files whose disk size meets or exceeds the threshold (in MB or GB);
  - *Written during session* — Only files that accumulated $\ge$ threshold written bytes since start;
  - *Read during session* — Only files that accumulated $\ge$ threshold read bytes since start;
  - *Size AND written / Size AND read* — Combined conditions.
- **Active only (hide idle)**: Hides records if no file I/O has occurred within the configured time window (default: 5 seconds).

### 3. Real-Time Activity Color Coding
Grid rows are dynamically highlighted based on recent activity:
- 🟨 **Light Yellow** — Active write operations;
- 🟩 **Light Green** — Active read operations;
- 🟥 **Light Red / Rose** — Simultaneous active read and write operations.

### 4. Long-Term History (Zero-Leak Archival)
- The application prevents unbounded RAM growth: every 30 seconds, records inactive for more than 15 minutes are evicted from memory.
- Prior to eviction, the accumulated session metrics are appended to `MonitorLarp_history.log`.
- The **"History Log"** button in the top toolbar opens the log file in the default text editor / spreadsheet software.

### 5. High Performance & Smooth UI
- Filtering, sorting, and row compilation execute on a background thread pool worker (`ThreadPool`).
- The `DataGridView` runs with `DoubleBuffered` enabled, eliminating flickering and preserving the vertical scroll position during refreshes.
- Disk queries for file sizes are cached and only queried for the top visible rows.

### 6. Built-In Self-Test
- The **"Self-Test"** button writes a 16 MB temporary test file with randomized data, reads it back, verifies throughput in the live monitor, and cleans up the file on exit.

---

## 🚀 System Requirements

- **Operating System**: Windows 7 SP1 / 8.1 / 10 / 11 (**x64 only**)
- **Runtime**: .NET Framework 4.7.2 or newer
- **Permissions**: **Administrator privileges required** (Kernel ETW session creation requires elevated rights).

---

## 📦 Quick Start

1. Download or build `MonitorLarp.exe`.
2. Launch `MonitorLarp.exe` as **Administrator** (right-click $\rightarrow$ *Run as administrator*).
3. Optionally enter target paths in `Paths (empty = all drives)` separated by semicolons (`;`).
4. Click **"Start"**.
5. Click **"Self-Test"** to verify kernel event interception immediately.

---

## 🛠️ Building from Source

No Visual Studio installation is required. Everything needed to compile the project is bundled in the repository.

### Build Steps:
1. Clone the repository:
   ```bash
   git clone https://github.com/<username>/monitorlarp.git
   cd monitorlarp
   ```
2. Execute the PowerShell build script:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```
3. The standalone binary will be created in the repository root:
   ```
   MonitorLarp.exe
   ```

The script invokes the standalone `csc.exe` compiler from `packages/` with optimization enabled (`/optimize+`), administrator manifest (`/win32manifest`), and targeting x64.

---

## 📊 History Log Format (`MonitorLarp_history.log`)

The history log is structured as Tab-Separated Values (TSV), easily opened in Excel, Notepad, or parsed by scripts:

| Column | Description |
|---|---|
| **Time** | Timestamp of last recorded activity (`yyyy-MM-dd HH:mm:ss`) |
| **Process** | Name of the process executable |
| **PID** | Process Identifier |
| **Written (bytes)** | Total bytes written during the session |
| **Read (bytes)** | Total bytes read during the session |
| **Write Ops** | Count of disk write system calls |
| **Read Ops** | Count of disk read system calls |
| **Path** | Absolute file path |

---

## ⚙️ Architecture Details

- **ETW Session Provider**: Manages a private named `TraceEventSession` activating kernel flags:
  - `KernelTraceEventParser.Keywords.FileIOInit`
  - `KernelTraceEventParser.Keywords.FileIO`
  - `KernelTraceEventParser.Keywords.DiskFileIO`
  - `KernelTraceEventParser.Keywords.Process`
- **Noise Suppression**: System-level paging files (`pagefile.sys`, `swapfile.sys`), NTFS metadata (`$LogFile`, `$Mft`), and internal ETW trace files (`*.etl`) are discarded early before acquiring locks or allocating memory structures.
- **Dictionary Safeguard**: Active in-memory records are capped at 30,000 entries (LRU eviction), preventing out-of-memory issues under heavy I/O workloads.

---

## ❓ FAQ

#### Why does clicking "Start" show "Failed to start monitoring"?
Ensure the application is run with **Administrator privileges**. Windows kernel trace sessions can only be created by elevated users. Also make sure no other kernel profilers (like older Process Monitor sessions) have locked the kernel trace provider.

#### Does it monitor network shares or virtual drives?
Yes, any volume managed by the Windows I/O subsystem that dispatches standard FileIO events through the Windows kernel is tracked.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE). Free for commercial and personal use, modification, and distribution.
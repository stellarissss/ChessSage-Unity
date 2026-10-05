# 棋圣 ChessSage · Nuitka 打包方案

> 目标：产出**含 exe 的多文件高性能游戏**（Nuitka standalone onedir），Windows 一键构建。
> 状态：方案已在 Linux 侧端到端验证通过（12 个棋类服务全部可正常启动、对局）。

---

## 一、旧打包程序为什么"完全无法使用"

原 `nuitka_build.py` + `bootstrap.py` 组合存在 **3 个致命缺陷**，任何一个都会导致产物启动即失败。

### 缺陷 1（根因）：`--include-data-dir` 会丢弃所有 `.py` 源码

Nuitka 把 `.py` 视为**代码**而非**数据**，`--include-data-dir` 在打包时会静默过滤掉它们。
实测结果对比：

| 目录 | 源码 `.py` 数 | 旧产物内 `.py` 数 |
|------|--------------|------------------|
| hub | 1 | 0 |
| shared | 13 | 0 |
| samsara | 17 | 0 |
| sandbox | 37 | 0 |
| xiangqi | 7 | 0 |
| wuziqi / weiqi / dongwuqi / heibaiqi | 各 7 | 0 |
| tiaoqi | 8 | 0 |
| **合计** | **111** | **0** |

棋类服务由**子进程按需 `runpy` 加载源码**，源码不在产物里 → 子进程必然启动失败。
而 Nuitka 的静态分析**追踪不到跨进程的 `runpy`**，所以这些源码必须显式携带。

> **这是"完全无法使用"的真正根因**，其余两点是叠加故障。

### 缺陷 2：入口选错，`bootstrap.py` 从未被编译

旧设计让 `bootstrap.py` 承担"冻结态子进程分支"逻辑，但 `nuitka_build.py` 编译的入口是 `main.py`——
`bootstrap.py` 根本不在产物内。父进程以 `[exe, <棋类>/main.py]` 拉起子进程时，子进程走到 `main()`，
又去抢 8080 端口，表现为：

```
ERROR: [Errno 98] error while attempting to bind on address ('0.0.0.0', 8080)
```

### 缺陷 3：`sys.executable` 在冻结态下语义不可靠

`start_process()` 直接用 `sys.executable` 拉起子进程。在 Nuitka standalone 下它通常指向本 exe，
但被其他进程拉起或环境异常时会回落到真实 Python 解释器，导致子进程直接跑成开发态。

### 附带缺陷 4：`_pipe_logger` 静默吞错

```python
except Exception:
    pass
```

子进程日志管道出错时无任何输出，把上面所有故障都掩盖成"日志空白"，极大增加排查难度。

---

## 二、修复方案与设计决策

### 2.1 子进程分支内联进 `main.py`

`main.py` 同时是**开发态启动器**和**冻结产物唯一入口**。新增：

```python
if __name__ == "__main__":
    if _FROZEN_ROOT and _run_as_child_script(sys.argv):
        sys.exit(0)
    main()
```

`_run_as_child_script()` 在 `argv[1]` 是存在的 `.py` 文件时，用 `runpy.run_path(..., run_name="__main__")`
就地执行，并把**脚本目录插到 `sys.path` 首位**。

> **为什么必须是子进程**：每个棋类目录下都有同名模块（`chess_ai.py` / `rule_engine.py` /
> `prompts.py` / `karma_assessor.py` / `mechanism_engine.py` / `ai_orchestrator.py`）。
> 同进程内加载会互相污染；独立进程 + 独立模块命名空间是**架构刚需**。

### 2.2 三重冻结判定

```python
_FROZEN_ROOT = (
    os.environ.get("CHESSSAGE_FROZEN") == "1"   # 显式标记，最可靠
    or bool(getattr(sys, "frozen", False))       # PyInstaller / cx_Freeze
    or ("__compiled__" in globals())             # Nuitka
)
```

父进程 `start_process()` 在拉起子进程时注入 `env["CHESSSAGE_FROZEN"] = "1"`，
子进程据此稳定识别冻结态，绕开各打包器的标记差异。

### 2.3 `_exe_path()` 兜底

```python
candidates = [sys.executable, sys.argv[0] if sys.argv else ""]
```

优先 `sys.executable`，异常时回落 `sys.argv[0]`，保证"以 exe 拉起子进程"语义稳定。

### 2.4 源码逐文件精确映射（本次关键修复）

```python
# 每个 .py 用独立 --include-data-files，目标路径与源路径完全一致
cmd.append(f"--include-data-files={rel}={rel}")
```

**不能用通配符** `--include-data-files=sandbox/**/*.py=sandbox/`：
实测会把子目录层级**压平**（`sandbox/xiangqi/main.py` 挤成 `sandbox/main.py`），互相覆盖。
逐文件精确映射可完整保留目录树。

同时 `.py` 与其它资源分两路携带：

| 内容 | 携带方式 |
|------|---------|
| `.py` 源码 | `--include-data-files=<rel>=<rel>`（逐文件） |
| 前端 / 配置等非 py 资源 | `--include-data-dir=<dir>=<dir>` |

> 纯 `.py` 目录（如 `samsara/`）不加 `--include-data-dir`，避免误导性的 `No data files` 告警。

### 2.5 `_pipe_logger` 异常可见

```python
except Exception as e:
    print(f"[{name}] 日志管道异常: {e}", file=sys.stderr)
    traceback.print_exc()
```

---

## 三、打包产物结构

```
dist/棋圣/
├── 棋圣.exe                  # 编译入口（Windows 无控制台）
├── python3*.dll / *.pyd      # Nuitka standalone 自带运行时，无需外部 Python
├── hub/ shared/ configs/ samsara/ sandbox/
├── xiangqi/ wuziqi/ weiqi/ dongwuqi/ tiaoqi/ heibaiqi/
│     └── 每个棋类目录均含完整 .py 源码 + static/ 前端资源
├── config.json               # API 密钥
├── 启动游戏.bat / start.sh
└── 使用说明.txt
```

**分发方式**：整个 `dist/棋圣` 文件夹打包发给玩家，双击 `启动游戏.bat` 即可运行。
**不可单独拷贝 exe**——它依赖同目录下的运行时与源码。

---

## 四、Windows 一键打包

```bat
build_windows.bat                          :: 默认 py -3.12 + Zig，编译完自动关机
build_windows.bat --no-shutdown            :: 编译结束后保持开机（不关机）
build_windows.bat --shutdown-delay 120     :: 关机前等待 120 秒（默认 60）
build_windows.bat --msvc                   :: 改用 Visual Studio Build Tools
build_windows.bat --python 3.12            :: 指定 Python 版本
build_windows.bat --clean                  :: 打包前清理旧产物
build_windows.bat --tmp D:\nktmp           :: 编译临时目录指到空间充足的盘
build_windows.bat --no-cache               :: 关闭 ccache
```

### 挂机编译：休眠抑制与自动关机

长时间编译最怕两件事——**系统休眠中断编译**、**编完忘了关机**。脚本已内置处理。

**休眠抑制（编译期间）**

编译开始时执行 `powercfg /change standby-timeout-ac 0`，把「交流电睡眠超时」临时设为
**永不睡眠**，编译结束后还原为 30 分钟。该方案无需管理员权限、对整机生效。

辅助地，还会为 `python.exe` 登记 `powercfg /requestsoverride ... SYSTEM` 请求
（需管理员权限，非管理员时静默跳过——主方案已足够）。

**自动关机（编译结束后）**

默认开启，**无论编译成功或失败**都会在倒计时结束后关机，适合挂机过夜编译。

```
============================================================
  [关机] 编译已结束，将在 60 秒后关闭计算机。
         想保留开机：在本窗口按任意键即可取消。
============================================================
```

- **取消方式**：倒计时内按任意键；或另开窗口执行 `shutdown /a`
- **不关机**：加 `--no-shutdown`
- **调整等待时间**：`--shutdown-delay 120`
- 失败时同样会走关机流程，因此**失败原因会先打印在窗口里**——如担心看不到，
  建议失败排查场景加 `--no-shutdown`。

### 脚本 7 步流程

1. **定位 Python** —— `py -3.12` 优先，回落 PATH 中的 `python`
2. **创建 `.venv-build` 虚拟环境** —— 与系统环境隔离，避免污染
3. **安装依赖** —— `requirements.txt` + `nuitka ordered-set zstandard`（+ `ccache`），失败自动切清华镜像
4. **选择 C 编译器** —— 默认 `--zig`（Nuitka 自动下载，免装 VS）；`--msvc` 切换
5. **清理旧产物**（仅 `--clean`）
6. **调用 `nuitka_build.py`** —— 参数完全对齐
7. **收尾** —— 解除休眠抑制 → 倒计时 → 自动关机（或按 `--no-shutdown` 保留开机）

### 前置要求

- **Windows 10/11 64 位**，Python 3.11 / 3.12（推荐 3.12）
- 安装 Python 时勾选 **"Add python.exe to PATH"** 与 **"py launcher"**
- **联网**（Nuitka 需下载 Zig 编译器；依赖需从 PyPI 安装）
- 磁盘预留 **≥ 8 GB**（产物约 500 MB，编译中间文件远大于此）
- Windows 版 `.exe` **必须且只能在 Windows 上构建**（Nuitka 不支持交叉编译）

---

## 五、性能与体积说明

| 指标 | 实测值（Linux 参照） |
|------|---------------------|
| 产物体积 | 约 494 MB |
| 编译耗时 | 首次 12-18 分钟（`--lto=no` 加快编译）～20-30 分钟（默认 `--lto=yes`） |
| 冷启动 | Hub 约 10 秒内可访问 |
| 单棋类服务 | 约 8-12 秒就绪 |
| 内存占用 | 12 服务全开约 1.3 GB；按需懒加载可大幅降低 |

已启用的优化：

- `--lto=yes` —— **默认开启**链接期优化，产物体积与编译时间略增，换取运行期最佳性能
  （生产打包推荐保持默认；如只求快速出包可传 `--no-lto` 关闭，性能略降）
- `--enable-cache` —— 启用 ccache 加速二次编译
- `--nofollow-import-to` —— 排除 `tkinter` / `matplotlib` / `numpy` / `pandas` / `scipy` /
  `PIL` / `rembg` / `onnxruntime` / `cv2` 等运行时用不到的大包

> `PIL` / `rembg` 仅被 `shared/assets/` 下的**离线美术脚本**引用，不参与游戏运行，可安全排除。
> `pywebview` **不要**手动加 `--include-package`——Nuitka 自带 webview 插件会自动处理，
> 手动再加会与之冲突并报 `FATAL`。

---

## 六、常见问题

| 现象 | 原因 | 处理 |
|------|------|------|
| 打包报 Zig 下载失败 | 网络受限 | 加 `--msvc`，或配置代理后重试 |
| 提示磁盘空间不足 | 默认临时目录在 C 盘 | 加 `--tmp D:\nktmp` |
| 产物启动后棋类服务起不来 | 源码未打进包 | 确认用本仓库最新 `nuitka_build.py`（逐文件映射 `.py`） |
| 子进程无日志 | 旧版 `_pipe_logger` 吞错 | 已修复，异常会打到 stderr |
| 想保留控制台看日志 | 默认 `--windows-console-mode=disable` | 加 `--console` |
| 打包极慢 | 首次编译 + LTO | 保留默认 `--enable-cache`，二次编译会快很多；只求快速出包可加 `--no-lto` 关闭 LTO |

---

## 七、验证清单（已通过）

在 Linux 上对产物做的端到端验证：

- [x] Hub `http://localhost:8080/` 返回 200
- [x] 6 个 RPG 棋类（8000-8005）懒加载全部 200
- [x] 6 个沙盒棋类（8010-8015）懒加载全部 200
- [x] 每次拉起均为**独立进程**（各自 PID）
- [x] **模块命名空间隔离**：8002 围棋返回 `go_state`，8000 象棋返回 `chinese_chess`，无污染
- [x] 懒加载**回收**：回收 8000 后该端口释放，其余 12 服务不受影响
- [x] **真实对局**：象棋走子 `{"success":true}`，回合 red→black，历史记录正确
- [x] 产物内含 111 个 `.py` 源码，目录层级完整（`sandbox/xiangqi/main.py` 等嵌套结构保留）

> Windows 特有部分（Zig/MSVC 编译器、`--windows-console-mode`、图标）需在 Windows 上实际构建验证，
> 逻辑已在脚本中按 Nuitka 文档实现。

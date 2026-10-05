# 工具链

Unity 版本的构建、配置同步与验证工具。所有脚本均可在无 GUI 的 Linux 沙箱中运行。

## 目录

| 路径 | 用途 |
|:---|:---|
| `unity/install-unity.sh` | 下载并安装 Unity Editor + Windows 交叉编译模块（默认 Unity China CDN） |
| `unity/activate-license.sh` | 用环境变量中的账号激活 Unity Personal 授权 |
| `sync-configs.sh` | 把 `legacy-web/` 的 JSON 配置与 Schema 同步进 `Assets/StreamingAssets/` |
| `build.sh` | EditMode 测试 + Linux/Windows 双平台构建 |
| `verify/run-player-headless.sh` | Xvfb + 软件渲染运行播放器并截图（视觉验证） |

## 典型流程

```bash
# 1. 环境（一次性）
bash tools/unity/install-unity.sh
UNITY_EMAIL=you@example.com UNITY_PASSWORD=*** bash tools/unity/activate-license.sh

# 2. 同步配置数据
bash tools/sync-configs.sh

# 3. 测试 + 构建
bash tools/build.sh all

# 4. 视觉验证
bash tools/verify/run-player-headless.sh
```

## 环境变量

| 变量 | 默认 | 说明 |
|:---|:---|:---|
| `UNITY` | `/opt/unity/editor/Editor/Unity` | Unity 可执行文件 |
| `UNITY_ROOT` | `/opt/unity` | Unity 安装根目录 |
| `UNITY_VERSION` | `2022.3.62f3c1` | Editor 版本 |
| `UNITY_REVISION` | `1623fc0bbb97` | 该版本 revision |
| `OUT` | `<repo>/Builds` | 构建输出目录 |

## 说明

- **配置即真相**：游戏全部规则/关卡/文案数据来自 `legacy-web/` 的 JSON，`sync-configs.sh` 只做拷贝，不做改写。
- **安全**：账号密码只经环境变量传入，不写入任何文件；`.gitignore` 已排除 `Builds/` 与 `Logs/`。
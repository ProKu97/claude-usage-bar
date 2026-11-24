# Claude 额度条（claude-usage-bar）

在 Claude Code 输入框上方，常驻显示 **5 小时额度** 和 **本周额度** 用了多少、几点重置。

不用再点「设置 → 用量」，一抬眼就知道还能用多少。

![额度条](docs/bar.png)

用量越高颜色越醒目：70% 以下绿色，70% 起黄色，90% 起红色。

![快用完时](docs/bar-high.png)

## 包含两部分

| 部分 | 是什么 | 必装吗 |
|---|---|---|
| `plugin/` | Claude Code 插件 `usage-bar`：在输入框上方画额度条，终端版和桌面版都能用 | 必装 |
| `overlay/` | Windows 小程序 `UsageBarOverlay.exe`：给 **Claude 桌面端** 补位，见下文 | 可选 |

数据来自 Claude Code 每次回复时官方返回的额度信息，和「设置 → 用量」是同一个来源。**只在使用 Claude 订阅（Pro / Max）登录时有数据**；用 API Key 或第三方中转时，额度条会显示「暂无数据」。

## 安装插件

在 Claude Code 终端里输入：

```
/plugin install usage-bar --marketplace ProKu97/claude-usage-bar
```

提示 `Add marketplace?` 时按 `y`，范围选第一个（用户级，所有项目都生效）。

也可以在系统终端里运行：

```
claude plugin install usage-bar --marketplace ProKu97/claude-usage-bar
```

**Claude 桌面端**：点输入框左边的 **+** → **Plugins** → **Add marketplace**，填 `ProKu97/claude-usage-bar`，再 **Add plugin**。

装好后，已经打开的会话发一次 `/reload-plugins`，新会话会自动带上。发一条消息、Claude 回复后，额度条就会出现。

## 桌面端悬浮条（可选，仅 Windows）

Claude 桌面端打开或切换会话时，要等会话激活才启动 Claude Code 后台，插件的额度条会晚几秒出现。
`overlay/UsageBarOverlay.exe` 在 Claude 窗口外面画一条一模一样的，补上这段空白：

- 贴在输入框那一栏的最上方，窗口移动、缩放、输入框变高都会跟着走
- 插件的额度条出现后自动让位，不会同时出现两条
- Claude 最小化、关闭、被别的窗口盖住、弹出菜单时跟着隐藏
- 鼠标可以直接点穿它；约 1% 单核 CPU、40~60 MB 内存

**用法**：下载 `overlay` 文件夹，双击 `UsageBarOverlay.exe`。想开机自启，把它的快捷方式放进「启动」文件夹（Win+R 输入 `shell:startup`）。关闭：运行 `stop.ps1`，或在任务管理器里结束 `UsageBarOverlay`。

它读插件写下的最新读数（`%USERPROFILE%\.claude\plugins\data\usage-bar-*\usage.json`），所以要先装插件。

**自己编译**（不想用现成的 exe）：只用 Windows 自带的 .NET Framework 4，不用装 SDK。在 `overlay` 文件夹里用 Git Bash 运行 `sh build.sh`。

已知限制：

- 按 Claude 桌面端**深色主题、英文界面**适配。界面改版后如果对不齐，改 `UsageBarOverlay.cs` 里 `Finder.Scan` 的定位和 `Theme` 的颜色。
- 靠 Windows 辅助功能（UI Automation）读取 Claude 窗口里输入框的位置。

## 常见问题

**额度条一直显示「额度：发一条消息后显示」**
额度信息跟着 Claude 的回复一起来，发一条消息就有了。

**显示「已重置」**
那个窗口已经过了重置时间。下一次回复后会显示新的用量。

**多个会话的数字不一样？**
各会话每 30 秒同步一次最新读数，以最后一次回复为准。

## 许可

MIT

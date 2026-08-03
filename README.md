# Claude Code H5 Workbench

本地运行的 .NET 8 Razor Pages 工作台，通过 Node Bridge 调用官方 `@anthropic-ai/claude-agent-sdk`，并由本机 Claude Code CLI 执行真实 Agent 会话。浏览器不会获得 API 密钥、网关配置或 Claude 会话 ID。

## 已实现功能

- 受信任工作区选择；Git 工作区可按会话分配 worktree，非 Git 工作区限制单个活动写入会话。
- 原生 Claude Code 会话、工具、技能、MCP、项目指令与权限回调。
- 会话恢复：已结束或停止的会话可用原始 Claude session ID 继续上下文。
- `/` 命令与技能菜单；命令清单在新会话建立后主动从 SDK 加载。
- 模型与思考等级选择，以及 `/model`、`/effort` 快捷命令。
- 图片选择和剪贴板粘贴；每条消息最多 5 张图片、单张最大 10 MB。
- 流式答复、可展开工具调用与结果、变更面板、活动面板。
- 可折叠的“思考与执行过程”摘要，展示任务分析、工具调用、子任务、权限等待和结果状态；SDK 返回 thinking 增量时也会显示对应内容。
- 权限模式、发送/停止状态切换，以及固定在视口底部的 composer。

## 浏览器会话历史

会话目录和可显示的文字历史保存在当前浏览器 profile 的 IndexedDB 中。刷新页面时会先显示本地快照；本机服务重启后，选择一个有 Claude session ID 的历史会话会自动通过 Agent SDK `resume` 恢复上下文，并创建新的 H5 管理会话。

右键会话项可重命名或删除。删除只会移除当前浏览器的 H5 历史并关闭仍在运行的 Bridge 会话，不会删除 Claude Code 的本地原生记录、工作区文件或 Git worktree。图片二进制、待处理的权限表单和完整原生 transcript 不会写入浏览器历史。

## 运行

前置条件：已安装 .NET 8、Node.js 和已登录可用的 `claude` CLI。

```bash
dotnet run
```

打开 `http://127.0.0.1:5080`。服务仅监听 loopback 地址。

## 配置

默认 `env` 模式继承当前终端的 Claude Code 登录状态。需要自定义网关或设置时，复制模板并保持本地文件不纳入版本控制：

```bash
cp appsettings.Local.example.json appsettings.Local.json
```

`ClaudeCode.Mode` 支持：

- `env`：继承环境变量和常规 Claude Code 配置。
- `settings`：加载 `SettingsPath` 并从子进程环境中移除继承的网关凭据。
- `isolated-settings`：同时排除用户、项目和本地设置；设置文件必须自行提供认证环境变量。

`TrustedWorkspaceRoots` 用于指定可在 UI 中选择的工作区根目录。`gateway-settings.example.json` 是脱敏示例，真实令牌必须放入未跟踪的本地配置文件。

## 安全边界

- 此应用不提供网络认证，也不应暴露到局域网或公网。
- Claude Code 的权限策略仍是最终控制点；Web 端只转发原生权限请求。
- 不会传递 `--dangerously-skip-permissions`。
- 运行中的 Agent 不会被 Web 应用以固定短超时强制终止；用户可通过停止按钮主动中断。
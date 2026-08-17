# claudecodeh5

claudecodeh5 是本地运行的 .NET 8 Razor Pages Agent 工作台。它通过 Node Bridge 调用 `@anthropic-ai/claude-agent-sdk`，并由本机 Claude Code CLI 执行真实 Agent 会话。浏览器不会获得 API 密钥、网关配置或 Claude 会话 ID。

## 文档

- [使用说明](docs/USAGE.md)：首次运行、工作区、会话和常见问题。
- [部署说明](docs/DEPLOYMENT.md)：发布、Linux systemd、Windows IIS 与安全边界。

## 已实现功能

- 前端工作区来源选择：可在同一页面切换“本地工作区”和“服务端工作区”。本地输入已存在目录会自动信任；服务端只输入项目名称，系统会在固定服务端根目录下创建。
- Git 工作区可按会话分配 worktree，非 Git 工作区限制单个活动写入会话。
- 原生 Claude Code 会话、工具、技能、MCP、项目指令与权限回调。
- 会话恢复：已结束或停止的会话可用原始 Claude session ID 继续上下文。
- `/` 命令与技能菜单；命令清单在新会话建立后主动从 SDK 加载。
- 模型选择、Claude Desktop 对应的 Mode（`Manual`、`Accept edits`、`Plan`、`Bypass permissions`）与 Effort（`Low`、`Medium`、`High`、`Extra`、`Max`、`Ultracode`），以及 `/model`、`/effort` 快捷命令。
- 图片选择和剪贴板粘贴；每条消息最多 5 张图片、单张最大 10 MB。
- 流式答复、可展开工具调用与结果、变更面板、活动面板。
- 可折叠的“思考与执行过程”摘要，展示任务分析、工具调用、子任务、权限等待和结果状态；SDK 返回 thinking 增量时也会显示对应内容。
- 权限模式、发送/停止状态切换，以及固定在视口底部的 composer。

## 浏览器会话历史

会话目录和可显示的文字历史保存在当前浏览器 profile 的 IndexedDB 中。刷新页面时会先显示本地快照；本机服务重启后，选择一个有 Claude session ID 的历史会话会自动通过 Agent SDK `resume` 恢复上下文，并创建新的 claudecodeh5 管理会话。

右键会话项可重命名或删除。删除只会移除当前浏览器中的 claudecodeh5 会话历史并关闭仍在运行的 Bridge 会话，不会删除 Claude Code 的本地原生记录、工作区文件或 Git worktree。图片二进制、待处理的权限表单和完整原生 transcript 不会写入浏览器历史。

## 运行

前置条件：已安装 .NET 8 SDK、Node.js 18+ 和已登录可用的 `claude` CLI。

```bash
dotnet restore
cd bridge && npm ci && cd ..
dotnet run --project ClaudeCodeCliHarness.csproj
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

### 工作区与项目来源

本地和服务端工作区在前端可直接切换：

- **本地工作区**：已有工作区下拉展示已配置及曾经输入并自动信任的目录。输入一个存在的绝对目录并点击“添加并信任本机目录”即可加入，不再要求预先配置父目录。
- **服务端工作区**：已有工作区下拉展示 `ServerWorkspaceRoot` 下的一级项目。新建时前端只输入项目名称；例如 `ServerWorkspaceRoot` 为 `D:\.claudecode`，输入 `example-project` 会创建 `D:\.claudecode\example-project`。
- **Git 仓库**：仅服务端工作区提供。先选择一个已有且为空的服务端工作区，再输入 GitHub/GitLab 仓库地址，代码会下载到该工作区；主机必须位于 `AllowedGitRepositoryHosts`。

工作区弹窗有两个标签：

- **工作区管理**：上方下拉选择已有工作区；下方输入新工作区。本地范围输入完整路径，服务端范围只输入名称。
- **Git 仓库**：仅服务端范围显示。先选择一个已有且为空的服务端工作区，再输入 GitHub/GitLab 仓库地址，系统将代码下载到该工作区。

聊天工具栏是会话工作区的唯一选择位置。创建会话时，后端会把该工作区解析为 Agent `cwd`（Git 工作区使用其独立 worktree）；会话处于非终态时选择器锁定，不能切换工作区。完成、停止或失败后，点击“新建会话”才能选择其他工作区。

`TrustedWorkspaceRoots` 仍用于“新建本机目录”的父目录选择。自动信任仅发生于用户在本机目录输入框中显式输入并提交的目录；服务端根由 `ServerWorkspaceRoot` 固定控制，用户不能从前端任意填写服务端路径。

手动添加、创建和克隆的工作区目录会保存到 claudecodeh5 的应用数据目录中的 `workspaces.json`。当前实际目录名为 `ClaudeCodeH5`，为兼容已有本地注册记录而保留；可通过 `WorkspaceDataPath` 改写注册表文件位置，并通过 `ManagedWorkspaceRoot` 指定 Git 克隆的受控根目录。

远端 Git 克隆只接受 HTTPS 地址，且主机必须精确列在 `AllowedGitRepositoryHosts`。例如：

```json
{
	"ClaudeCode": {
		"ServerWorkspaceRoot": "D:\\.claudecode",
		"TrustedWorkspaceRoots": ["D:\\LocalProjects"],
		"ManagedWorkspaceRoot": "D:\\.claudecode\\git-workspaces",
		"AllowedGitRepositoryHosts": ["gitlab.example.com"]
	}
}
```

当前版本不会把浏览器路径字符串伪装成服务器路径。要对用户电脑上的未提交代码进行远端协作，需要后续部署显式授权的 Local Connector；ZIP 上传只适合作为隔离的一次性快照分析，不能替代同步。

当 claudecodeh5 服务部署在远端服务器时，“本地工作区”仍只能访问该服务进程可读取的本机文件系统；浏览器电脑上的项目需要本地 Connector 才能被远端服务直接编辑，当前版本不会伪装这一能力。

## 安全边界

- 此应用不提供网络认证，也不应暴露到局域网或公网。
- Claude Code 的权限策略仍是最终控制点；Web 端只转发原生权限请求。
- 不会传递 `--dangerously-skip-permissions`。
- 运行中的 Agent 不会被 Web 应用以固定短超时强制终止；用户可通过停止按钮主动中断。
- 用户显式添加的本机目录会解析符号链接并检查读写权限，随后持久化为自动信任目录；“新建本机目录”仍只能位于配置根或已自动信任的根内，名称不能逃逸目录边界。
- 服务端工作区不接受浏览器填写的完整路径，只能在 `ServerWorkspaceRoot` 下创建或选择已有一级项目。Git 克隆禁用交互式凭据提示，且必须通过管理员配置的主机白名单。
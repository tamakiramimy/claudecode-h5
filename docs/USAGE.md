# claudecodeh5 使用说明

## 适用范围

claudecodeh5 是运行在服务主机本地的 Agent 工作台。浏览器只连接 Web 界面，实际文件访问、Git 操作和 Claude Code 会话均由服务进程所在的机器执行。

> 不要把本应用当作浏览器访问电脑本地文件的工具。服务部署在远端时，“本地工作区”指的是服务主机上的目录；浏览器电脑的目录需要单独的 Local Connector，当前版本不提供该能力。

## 首次运行

1. 安装 .NET 8 SDK、Node.js 18 或更高版本，并确认 `claude` CLI 可在终端中运行且已登录。
2. 在项目根目录执行：

   ```bash
   dotnet restore
   cd bridge && npm ci && cd ..
   dotnet run --project ClaudeCodeCliHarness.csproj
   ```

3. 打开 `http://127.0.0.1:5080`。
4. 在工作区管理窗口中选择或添加一个工作区，然后创建会话。

默认配置使用 `agent-workspace` 作为本地工作区。该目录仅用于提供安全的首次启动位置；实际任务应选择明确的项目目录。

## 配置方式

复制模板后只在本机保存配置，不要提交生成的文件：

```bash
cp appsettings.Local.example.json appsettings.Local.json
```

`appsettings.Local.json` 会覆盖默认 `appsettings.json`，并且已被 Git 和发布输出排除。常用字段如下：

- `ExecutablePath`：Claude CLI 的可执行文件或绝对路径，默认值为 `claude`。
- `NodeExecutablePath`：Node.js 可执行文件，默认值为 `node`。
- `WorkspaceMode`：`local` 或 `remote`。
- `TrustedWorkspaceRoots`：允许在界面中创建本地工作区的父目录列表。
- `ServerWorkspaceRoot`：服务端工作区的固定根目录。
- `ManagedWorkspaceRoot`：Git 克隆使用的受控目录。
- `AllowedGitRepositoryHosts`：允许克隆的 HTTPS Git 主机白名单。
- `TimeoutSeconds`：单个 Agent 会话的超时上限，范围为 30 到 3600 秒。

`Mode` 控制 Claude Code 运行时配置：

- `env`：继承当前服务进程的 Claude Code 登录与环境配置。
- `settings`：读取 `SettingsPath`，同时从子进程环境中移除继承的网关凭据。
- `isolated-settings`：只使用 `SettingsPath` 指定的设置，不加载用户、项目或本地 Claude 配置。

需要网关配置时，从 `gateway-settings.example.json` 创建一个未跟踪的本地文件，并将其路径填入 `SettingsPath`。模板中的令牌和代理均为占位值。

## 工作区

### 本地工作区

在“工作区管理”中输入服务主机上的绝对目录。已有目录会在读写权限和符号链接边界检查通过后被信任；不存在的目录只能在一个已存在的父目录下创建。

### 服务端工作区

服务端范围只接受项目名称。应用会在 `ServerWorkspaceRoot` 下创建或选择一级目录，浏览器不能提交任意服务端绝对路径。

### Git 工作区

Git 克隆只在服务端范围可用。先选择一个空的服务端工作区，再提交 HTTPS 仓库地址。主机名必须精确匹配 `AllowedGitRepositoryHosts`，交互式凭据提示被禁用。

Git 仓库会按会话创建独立 worktree；非 Git 工作区同一时间只允许一个活动写入会话。会话处于运行状态时，工作区选择器会锁定。

## 会话操作

- 创建会话后，选择模型、权限模式和 Effort，再发送任务。
- `Manual`、`Accept edits`、`Plan` 和 `Bypass permissions` 会传递给 Agent SDK；Claude Code 自身的权限策略仍是最终控制点。
- 会话停止、失败或完成后，可创建新会话并选择其他工作区。
- 刷新页面后，浏览器 IndexedDB 会先恢复可见历史。选择含原始 Claude session ID 的历史会话时，服务会尝试通过 Agent SDK 恢复上下文。
- 右键会话可重命名或删除。删除仅移除当前浏览器中的 claudecodeh5 会话历史，不会删除工作区文件、原生 Claude Code 记录或 Git worktree。

每条消息最多可附加 5 张图片，单张最大 10 MB，支持 PNG、JPEG、GIF 和 WebP。

## 安全边界

- 应用没有网络登录认证，默认只监听 `127.0.0.1`。不要直接发布到局域网或公网。
- 为每个部署环境使用专用、权限最小的操作系统账户和 Claude Code 身份。
- 工作区白名单、Git 主机白名单和 Claude Code 权限策略应同时配置；不要把它们视为彼此的替代品。
- 不要将 API Key、网关令牌、代理账号、证书或 `appsettings.Local.json` 加入 Git 或发布包。

## 常见问题

### 页面可打开，但无法创建 Agent 会话

确认服务账户能运行 `node` 和 `claude`，并且 `bridge/node_modules` 已通过 `npm ci` 安装。随后确认 `Mode` 对应的 Claude Code 登录或 `SettingsPath` 可用。

### 工作区未显示或被拒绝

确认目录位于服务主机上，服务账户对其具备读写权限，并且该目录在 `TrustedWorkspaceRoots` 或已明确添加的可信目录范围内。服务端工作区还必须位于 `ServerWorkspaceRoot` 之下。

### Git 克隆被拒绝

确认使用 HTTPS URL、目标工作区为空，且仓库主机完整匹配 `AllowedGitRepositoryHosts`。私有仓库需要由服务账户预先配置安全的非交互式访问方式；不要在浏览器界面中输入凭据。

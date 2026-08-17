# claudecodeh5 部署说明

## 部署前提与安全边界

claudecodeh5 会让服务进程代表用户运行 Claude Code、访问工作区并执行 Git 操作。它没有内置网络认证，并且应用代码固定监听 `127.0.0.1:5080`。仅在受控主机上部署；如需通过反向代理提供访问，必须先配置组织级认证、HTTPS、访问控制和网络隔离。

服务账户必须具备：

- .NET 8 运行时或 SDK。
- Node.js 18 或更高版本。
- 可执行且已登录的 `claude` CLI，或受控的 `SettingsPath` 配置。
- 对已批准工作区和应用数据目录的最小读写权限。

不要使用个人管理员账户运行服务，也不要把 API Key、网关令牌、代理凭据或浏览器会话数据写入发布包。

## 通过 GitHub Release 安装

GitHub Release 提供四种自包含发布包：`claudecodeh5-<tag>-win-x64.zip`、`claudecodeh5-<tag>-win-arm64.zip`、`claudecodeh5-<tag>-osx-x64.zip` 和 `claudecodeh5-<tag>-osx-arm64.zip`。下载与目标操作系统及 CPU 架构匹配的 ZIP 并解压到部署目录；这些包不包含本地配置或 `bridge/node_modules`。

在解压目录中先安装 Bridge 的生产依赖：

```bash
cd bridge
npm ci --omit=dev
cd ..
```

随后从示例创建 `appsettings.Local.json`，按目标主机配置后启动对应的自包含可执行文件。不要将本地配置重新归档或上传到 Release。

## 生成发布包

在项目根目录执行：

```bash
dotnet restore
dotnet publish -c Release -o ./publish
```

发布规则会包含 Bridge 脚本、Bridge 的 `package.json` 和锁文件，但会刻意排除 `appsettings.Local.json` 与本地 gateway 配置。在创建部署配置前，检查新生成的发布目录：

```bash
test ! -e ./publish/appsettings.Local.json
test ! -e ./publish/gateway-settings.local.json
test -f ./publish/bridge/src/agent-bridge.mjs
test -f ./publish/bridge/package.json
test -f ./publish/bridge/package-lock.json
```

然后进入发布目录并安装生产依赖：

```bash
cd publish/bridge
npm ci --omit=dev
cd ..
```

创建本地配置：

```bash
cp appsettings.Local.example.json appsettings.Local.json
```

按部署主机的实际目录、服务账户和 Claude Code 模式修改 `appsettings.Local.json`。这个文件必须保留在发布目录中，但不得提交、归档到公开制品或复制到其他环境。

## 本机验证

在发布目录启动 framework-dependent 发布物：

```bash
dotnet ClaudeCodeCliHarness.dll
```

打开 `http://127.0.0.1:5080`，选择一个明确授权的工作区，并创建一个只读会话。若服务进程无法找到 `node` 或 `claude`，请在服务账户的 `PATH` 中提供可执行文件，或在 `appsettings.Local.json` 中设置绝对路径。

## Linux systemd

以下示例假设发布目录为 `/opt/claudecodeh5`，服务账户为 `claudecodeh5`。先创建目录、设置所属权限，并使用该服务账户完成 Claude CLI 登录或配置受控 `SettingsPath`。

创建 `/etc/systemd/system/claudecodeh5.service`：

```ini
[Unit]
Description=claudecodeh5
After=network.target

[Service]
Type=simple
User=claudecodeh5
Group=claudecodeh5
WorkingDirectory=/opt/claudecodeh5
Environment=DOTNET_ENVIRONMENT=Production
Environment=HOME=/var/lib/claudecodeh5
Environment=PATH=/usr/local/bin:/usr/bin:/bin
ExecStart=/usr/bin/dotnet /opt/claudecodeh5/ClaudeCodeCliHarness.dll
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

然后执行：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now claudecodeh5
sudo systemctl status claudecodeh5
```

使用 `journalctl -u claudecodeh5 -f` 查看日志。若部署了反向代理，反向代理应运行在同一主机并转发至 `127.0.0.1:5080`；不要修改应用为裸露的公网监听地址。

## Windows IIS

1. 安装与发布版本匹配的 ASP.NET Core Hosting Bundle、Node.js 和 Claude CLI。
2. 使用 `dotnet publish -c Release -o publish` 生成发布目录，并在 `publish/bridge` 中运行 `npm ci --omit=dev`。
3. 创建专用 Windows 服务账户，授予发布目录、应用数据目录和批准工作区的最小权限；用该账户完成 Claude CLI 登录或配置 `SettingsPath`。
4. 在 IIS 创建站点并将物理路径指向 `publish`，应用程序池设置为 `No Managed Code`，身份设置为该专用账户。
5. 仅绑定受控地址。若经由反向代理或负载均衡器访问，先在上游启用 HTTPS 与认证，再允许访问站点。
6. 把 `appsettings.Local.json` 单独放在发布目录，不放入源代码或 CI 制品。

IIS 账户找不到 `node` 或 `claude` 时，为该账户配置系统级 PATH，或在本地配置中使用绝对可执行路径，然后回收应用程序池。

## 升级与回滚

1. 停止 systemd 服务或回收 IIS 应用程序池。
2. 备份当前发布目录中的 `appsettings.Local.json`，以及自定义的工作区注册数据路径；不要把备份上传到公开仓库。
3. 替换应用二进制和 Bridge 文件，在新的 `bridge/` 目录运行 `npm ci --omit=dev`。
4. 恢复本地配置，启动服务，并完成首页、工作区和只读会话验证。
5. 出现问题时，停止服务并恢复上一版发布目录与对应依赖；保留本地配置不变。

## 发布检查清单

- [ ] 发布目录不包含开发机自动复制的 `appsettings.Local.json` 或 gateway 配置。
- [ ] 发布目录包含 `bridge/src/agent-bridge.mjs`、`bridge/package.json` 和 `bridge/package-lock.json`。
- [ ] `bridge/node_modules` 已在目标系统通过 `npm ci --omit=dev` 安装。
- [ ] 服务账户可运行 `dotnet`、`node` 和 `claude`，且只具有所需目录权限。
- [ ] 应用只对受控网络开放，所有代理入口都具备 HTTPS 与认证。
- [ ] 真实令牌、代理凭据、证书和本地配置均未进入 Git 历史或公开制品。

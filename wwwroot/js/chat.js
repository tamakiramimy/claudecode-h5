const elements = {
    workbench: document.querySelector("#workbench"),
    workspaceSelect: document.querySelector("#workspace-select"),
    newSessionButton: document.querySelector("#new-session-button"),
    sessionList: document.querySelector("#session-list"),
    sessionCount: document.querySelector("#session-count"),
    transcript: document.querySelector("#transcript"),
    emptyState: document.querySelector("#empty-state"),
    sessionTitle: document.querySelector("#session-title"),
    sessionStatus: document.querySelector("#session-status"),
    sessionStatusDot: document.querySelector("#session-status-dot"),
    permissionMode: document.querySelector("#permission-mode-select"),
    modelSelect: document.querySelector("#model-select"),
    thinkingLevel: document.querySelector("#thinking-level-select"),
    form: document.querySelector("#chat-form"),
    input: document.querySelector("#message-input"),
    sendButton: document.querySelector("#send-button"),
    attachmentInput: document.querySelector("#attachment-input"),
    attachmentList: document.querySelector("#attachment-list"),
    commandMenu: document.querySelector("#command-menu"),
    interactionDock: document.querySelector("#interaction-dock"),
    activityList: document.querySelector("#activity-list"),
    changesList: document.querySelector("#changes-list"),
    sidebarOpen: document.querySelector("#sidebar-open-button"),
    sidebarClose: document.querySelector("#sidebar-close-button"),
    sessionContextMenu: document.querySelector("#session-context-menu"),
    sessionRenameDialog: document.querySelector("#session-rename-dialog"),
    sessionRenameForm: document.querySelector("#session-rename-form"),
    sessionRenameInput: document.querySelector("#session-rename-input"),
    sessionDeleteDialog: document.querySelector("#session-delete-dialog"),
    sessionDeleteForm: document.querySelector("#session-delete-form"),
    csrfToken: document.querySelector("input[name='__RequestVerificationToken']")
};

const state = {
    activeSessionId: null,
    attachments: [],
    eventSource: null,
    history: new Map(),
    historyWriteTimers: new Map(),
    menuSessionId: null,
    pendingDialogSessionId: null,
    settingsUpdate: Promise.resolve(),
    sessions: [],
    views: new Map(),
    workspaces: []
};

const historyDatabase = {
    name: "claude-code-h5-history",
    version: 1,
    async open() {
        if (!("indexedDB" in window)) {
            return null;
        }
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(this.name, this.version);
            request.onupgradeneeded = () => request.result.createObjectStore("sessions", { keyPath: "id" });
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    },
    async readAll() {
        const database = await this.open();
        if (!database) {
            return [];
        }
        return new Promise((resolve, reject) => {
            const request = database.transaction("sessions", "readonly").objectStore("sessions").getAll();
            request.onsuccess = () => resolve(request.result || []);
            request.onerror = () => reject(request.error);
        });
    },
    async put(record) {
        const database = await this.open();
        if (!database) {
            return;
        }
        await new Promise((resolve, reject) => {
            const request = database.transaction("sessions", "readwrite").objectStore("sessions").put(record);
            request.onsuccess = () => resolve();
            request.onerror = () => reject(request.error);
        });
    },
    async remove(id) {
        const database = await this.open();
        if (!database) {
            return;
        }
        await new Promise((resolve, reject) => {
            const request = database.transaction("sessions", "readwrite").objectStore("sessions").delete(id);
            request.onsuccess = () => resolve();
            request.onerror = () => reject(request.error);
        });
    }
};

const statusLabels = {
    starting: "正在启动",
    idle: "等待任务",
    working: "正在处理",
    "needs-input": "等待你的决定",
    stopping: "正在停止",
    stopped: "已停止",
    completed: "已完成",
    failed: "运行失败"
};

const toolLabels = {
    Agent: "启动子 Agent",
    Bash: "运行命令",
    Edit: "编辑文件",
    Glob: "匹配文件",
    Grep: "搜索代码",
    NotebookEdit: "编辑 Notebook",
    Read: "读取文件",
    Write: "写入文件"
};

void initialize();

async function initialize() {
    createIcons();
    bindEvents();
    await loadHistory();
    await Promise.all([loadWorkspaces(), loadSessions()]);
    reconcileHistory();
    if (state.sessions.length > 0) {
        selectSession(state.sessions[0].id);
    } else {
        render();
    }
}

function bindEvents() {
    elements.newSessionButton.addEventListener("click", () => void createSession());
    elements.sendButton.addEventListener("click", (event) => {
        if (elements.sendButton.dataset.action !== "interrupt") {
            return;
        }

        event.preventDefault();
        void interruptActiveSession();
    });
    elements.sidebarOpen.addEventListener("click", () => elements.workbench.classList.add("sidebar-visible"));
    elements.sidebarClose.addEventListener("click", () => elements.workbench.classList.remove("sidebar-visible"));
    elements.form.addEventListener("submit", (event) => {
        event.preventDefault();
        void submitPrompt();
    });
    elements.input.addEventListener("input", () => {
        resizeComposer();
        renderCommandMenu();
    });
    elements.input.addEventListener("keydown", handleComposerKeydown);
    elements.input.addEventListener("paste", handleComposerPaste);
    elements.attachmentInput.addEventListener("change", () => void addAttachments(elements.attachmentInput.files));
    elements.modelSelect.addEventListener("change", () => void updateSessionSettings({ model: elements.modelSelect.value }));
    elements.thinkingLevel.addEventListener("change", () => void updateSessionSettings({ maxThinkingTokens: Number(elements.thinkingLevel.value) }));
    elements.permissionMode.addEventListener("change", () => void updateSessionSettings({ permissionMode: elements.permissionMode.value }));
    document.querySelectorAll(".inspector-tab").forEach((tab) => {
        tab.addEventListener("click", () => activateInspectorTab(tab));
    });
    elements.sessionContextMenu.addEventListener("click", handleSessionContextAction);
    elements.sessionRenameForm.addEventListener("submit", (event) => void submitSessionRename(event));
    elements.sessionDeleteForm.addEventListener("submit", (event) => void submitSessionDelete(event));
    document.querySelectorAll("[data-dialog-close]").forEach((button) => button.addEventListener("click", () => button.closest("dialog")?.close()));
    document.addEventListener("click", (event) => {
        if (!elements.sessionContextMenu.contains(event.target)) {
            hideSessionContextMenu();
        }
    });
    window.addEventListener("beforeunload", () => closeEventSource());
}

async function loadHistory() {
    try {
        const records = await historyDatabase.readAll();
        for (const record of records) {
            state.history.set(record.id, record);
        }
    } catch (error) {
        console.warn("Unable to load local conversation history.", error);
    }
}

function reconcileHistory() {
    const activeIds = new Set(state.sessions.map((session) => session.id));
    for (const record of state.history.values()) {
        if (activeIds.has(record.id)) {
            restoreViewSnapshot(record.id, record.view);
            continue;
        }
        state.sessions.push({ ...record.summary, id: record.id, isHistorical: true, status: record.summary.status || "stopped" });
        restoreViewSnapshot(record.id, record.view);
    }
    state.sessions.sort((left, right) => new Date(right.updatedAt) - new Date(left.updatedAt));
}

async function loadWorkspaces() {
    try {
        state.workspaces = await requestJson("/api/workspaces");
        renderWorkspaceOptions();
    } catch (error) {
        showRuntimeError(error.message);
    }
}

async function loadSessions() {
    try {
        state.sessions = await requestJson("/api/agent/sessions");
        state.sessions.forEach((session) => ensureView(session.id));
    } catch (error) {
        showRuntimeError(error.message);
    }
}

async function createSession() {
    const workspaceId = elements.workspaceSelect.value;
    if (!workspaceId) {
        showRuntimeError("没有可用的受信任工作区。");
        return;
    }

    const existing = findActiveUnisolatedSession(workspaceId);
    if (existing) {
        selectSession(existing.id);
        showRuntimeError("该工作区已有活动会话，已切换到现有会话。");
        return;
    }

    setBusy(elements.newSessionButton, true);
    try {
        const session = await requestJson("/api/agent/sessions", {
            method: "POST",
            body: JSON.stringify({
                workspaceId,
                permissionMode: elements.permissionMode.value
            })
        });
        state.sessions = [session, ...state.sessions.filter((item) => item.id !== session.id)];
        ensureView(session.id);
        selectSession(session.id);
    } catch (error) {
        if (error.message.includes("non-isolated workspace")) {
            const conflictingSession = findActiveUnisolatedSession(workspaceId);
            if (conflictingSession) {
                selectSession(conflictingSession.id);
                showRuntimeError("该工作区已有活动会话，已切换到现有会话。");
                return;
            }
        }
        showRuntimeError(error.message);
    } finally {
        setBusy(elements.newSessionButton, false);
    }
}

function selectSession(sessionId) {
    if (!state.sessions.some((session) => session.id === sessionId)) {
        return;
    }

    const session = state.sessions.find((item) => item.id === sessionId);
    if (session?.isHistorical) {
        void restoreHistoricalSession(session);
        return;
    }

    state.activeSessionId = sessionId;
    ensureView(sessionId);
    elements.workbench.classList.remove("sidebar-visible");
    openEventStream(sessionId);
    render();
}

async function restoreHistoricalSession(historySession) {
    if (!historySession.claudeSessionId) {
        state.activeSessionId = historySession.id;
        showRuntimeError("该历史会话没有 Claude session ID，只能查看，无法继续。" );
        render();
        return;
    }

    showRuntimeError("正在恢复历史会话…");
    try {
        const previousId = historySession.id;
        const view = ensureView(previousId);
        const restored = await requestJson("/api/agent/sessions/restore", {
            method: "POST",
            body: JSON.stringify({
                workspaceId: historySession.workspaceId,
                claudeSessionId: historySession.claudeSessionId,
                name: historySession.name,
                permissionMode: historySession.permissionMode,
                model: view.model,
                maxThinkingTokens: view.maxThinkingTokens
            })
        });
        migrateHistorySession(previousId, restored.id, restored);
        selectSession(restored.id);
    } catch (error) {
        state.activeSessionId = historySession.id;
        showRuntimeError(`恢复失败：${error.message}`);
        render();
    }
}

function migrateHistorySession(previousId, nextId, summary) {
    const view = state.views.get(previousId);
    if (view) {
        state.views.delete(previousId);
        state.views.set(nextId, view);
    }
    state.history.delete(previousId);
    void historyDatabase.remove(previousId);
    state.sessions = state.sessions
        .filter((session) => session.id !== previousId)
        .concat({ ...summary, isHistorical: false });
    scheduleHistorySave(nextId);
}

function openEventStream(sessionId) {
    closeEventSource();
    const source = new EventSource(`/api/agent/sessions/${encodeURIComponent(sessionId)}/events`);
    state.eventSource = source;
    [
        "event",
        "session-created",
        "prompt-submitted",
        "prompt-queued",
        "permission-request",
        "question-request",
        "request-resolved",
        "configured",
        "session-settings-updated",
        "session-resumed",
        "session-ended",
        "session-timeout",
        "session-closed",
        "session-stopped",
        "error"
    ].forEach((eventName) => {
        source.addEventListener(eventName, (event) => handleSseEvent(sessionId, eventName, event));
    });
    source.addEventListener("error", () => {
        if (state.activeSessionId === sessionId) {
            updateSessionStatus(sessionId, "failed");
            render();
        }
    });
}

function closeEventSource() {
    state.eventSource?.close();
    state.eventSource = null;
}

function handleSseEvent(sessionId, eventName, event) {
    let wrapped;
    try {
        wrapped = JSON.parse(event.data);
    } catch {
        return;
    }

    const view = ensureView(sessionId);
    if (wrapped.sequence && view.sequences.has(wrapped.sequence)) {
        return;
    }
    if (wrapped.sequence) {
        view.sequences.add(wrapped.sequence);
        scheduleHistorySave(sessionId);
    }

    if (eventName === "event") {
        handleBridgeEvent(sessionId, wrapped.payload);
    } else if (eventName === "permission-request" || eventName === "question-request") {
        view.pendingRequest = { type: eventName, payload: wrapped.payload };
        updateSessionStatus(sessionId, "needs-input");
        updateExecutionProgress(sessionId, "等待你的确认", eventName === "question-request" ? "Claude 需要补充信息" : "Claude 请求执行权限", "attention");
        appendActivity(sessionId, eventName === "question-request" ? "Claude 需要你补充信息" : "Claude 请求执行权限", "attention");
    } else if (eventName === "session-ended") {
        updateSessionStatus(sessionId, wrapped.payload.reason === "error" ? "failed" : "completed");
        if (wrapped.payload.reason === "error" && wrapped.payload.error) {
            appendMessage(sessionId, "error", wrapped.payload.error, `bridge-error-${wrapped.sequence || Date.now()}`);
        }
        finishExecutionProgress(sessionId, wrapped.payload.reason === "error" ? "执行失败" : "会话已完成", wrapped.payload.reason === "error" ? "error" : "complete");
        appendActivity(sessionId, wrapped.payload.reason === "error" ? "会话因错误结束" : "会话已完成", "complete");
    } else if (eventName === "session-timeout") {
        updateSessionStatus(sessionId, "failed");
        appendMessage(sessionId, "error", wrapped.payload.error || "Claude 未在限定时间内返回进度。", `timeout-${wrapped.sequence || Date.now()}`);
        finishExecutionProgress(sessionId, "执行超时", "error");
        appendActivity(sessionId, "会话已因无进度自动停止", "attention");
    } else if (eventName === "session-closed" || eventName === "session-stopped") {
        updateSessionStatus(sessionId, "stopped");
        finishExecutionProgress(sessionId, "已停止", "attention");
        appendActivity(sessionId, "会话已停止", "complete");
    } else if (eventName === "prompt-submitted") {
        updateSessionStatus(sessionId, "working");
        startExecutionProgress(sessionId, "正在分析你的任务");
    } else if (eventName === "session-resumed") {
        updateSessionStatus(sessionId, "starting");
        appendActivity(sessionId, "会话已恢复，正在重新连接 Claude", "info");
    } else if (eventName === "configured" || eventName === "session-settings-updated") {
        const view = ensureView(sessionId);
        if (wrapped.payload.model) {
            view.model = wrapped.payload.model;
        }
        if (Number.isInteger(wrapped.payload.maxThinkingTokens)) {
            view.maxThinkingTokens = wrapped.payload.maxThinkingTokens;
        }
        if (wrapped.payload.permissionMode) {
            updateSession(sessionId, { permissionMode: wrapped.payload.permissionMode });
        }
        view.isConfiguring = false;
        updateSessionStatus(sessionId, "idle");
        appendActivity(sessionId, "已更新 Mode、模型或 Effort", "complete");
    } else if (eventName === "error") {
        appendMessage(sessionId, "error", wrapped.payload.error || "会话流发生错误。", `error-${wrapped.sequence || Date.now()}`);
    }

    if (state.activeSessionId === sessionId) {
        render();
    }
}

function handleBridgeEvent(sessionId, bridgePayload) {
    const envelope = bridgePayload?.event;
    if (!envelope) {
        return;
    }

    const kind = envelope.kind;
    const payload = envelope.payload || {};
    const view = ensureView(sessionId);
    markWorking(sessionId, kind);

    switch (kind) {
        case "capabilities":
            view.commands = payload.slash_commands || [];
            view.models = payload.models || [];
            updateSessionStatus(sessionId, "idle");
            appendActivity(sessionId, `已加载 ${view.commands.length} 个命令和技能`, "complete");
            if (state.activeSessionId === sessionId) {
                renderCommandMenu();
            }
            break;
        case "init":
            view.commands = payload.slash_commands || [];
            if (payload.model) {
                view.model = payload.model;
            }
            updateSession(sessionId, {
                claudeSessionId: payload.session_id || null,
                status: "idle"
            });
            appendActivity(sessionId, `已加载 ${view.commands.length} 个命令和技能`, "complete");
            if (state.activeSessionId === sessionId) {
                renderCommandMenu();
            }
            break;
        case "session_state_changed":
            {
                const status = mapAgentState(payload.state);
                if (status) {
                    updateSessionStatus(sessionId, status);
                }
            }
            break;
        case "stream_event":
            handlePartialEvent(sessionId, payload);
            break;
        case "assistant":
            handleAssistantEvent(sessionId, payload);
            break;
        case "user":
            handleUserEvent(sessionId, payload);
            break;
        case "tool_progress":
            updateExecutionProgress(sessionId, "正在调用工具", `${toolLabels[payload.tool_name] || payload.tool_name || "工具"} 正在执行`, "running");
            appendActivity(sessionId, `${toolLabels[payload.tool_name] || payload.tool_name || "工具"} 正在执行`, "running");
            break;
        case "task_started":
            updateExecutionProgress(sessionId, "正在拆分任务", payload.description || "正在启动后台任务", "running");
            appendActivity(sessionId, `后台任务：${payload.description || "正在启动"}`, "running");
            break;
        case "task_progress":
            updateExecutionProgress(sessionId, "正在处理", payload.summary || payload.description || "后台任务正在推进", "running");
            appendActivity(sessionId, payload.summary || `后台任务：${payload.description || "处理中"}`, "running");
            break;
        case "task_notification":
            updateExecutionProgress(sessionId, payload.status === "completed" ? "子任务已完成" : "后台任务更新", payload.summary || "后台任务已更新", payload.status === "completed" ? "complete" : "attention");
            appendActivity(sessionId, payload.summary || "后台任务已更新", payload.status === "completed" ? "complete" : "attention");
            break;
        case "permission_denied":
            updateExecutionProgress(sessionId, "工具被拒绝", `${payload.tool_name || "工具"}：${payload.message || ""}`, "attention");
            appendActivity(sessionId, `${payload.tool_name || "工具"} 被拒绝：${payload.message || ""}`, "attention");
            break;
        case "result":
            updateSessionStatus(sessionId, payload.is_error ? "failed" : "idle");
            finishExecutionProgress(sessionId, payload.is_error ? "执行失败" : "已完成", payload.is_error ? "error" : "complete");
            if (payload.is_error && Array.isArray(payload.errors)) {
                appendMessage(sessionId, "error", payload.errors.join("\n"), `result-error-${payload.uuid || Date.now()}`);
            }
            break;
        case "informational":
            appendActivity(sessionId, payload.content || "Agent 状态已更新", "info");
            break;
        case "api_retry":
            appendActivity(sessionId, `连接重试，第 ${payload.attempt || 1} 次`, "attention");
            break;
    }
}

function handlePartialEvent(sessionId, payload) {
    const raw = payload.event;
    if (!raw) {
        return;
    }

    const view = ensureView(sessionId);
    if (raw.type === "message_start") {
        view.activeAgentMessage = null;
    }

    if (raw.type === "content_block_start" && raw.content_block?.type === "tool_use") {
        updateExecutionProgress(sessionId, "准备调用工具", toolLabels[raw.content_block.name] || raw.content_block.name, "running");
        appendActivity(sessionId, `${toolLabels[raw.content_block.name] || raw.content_block.name}：准备中`, "running");
    }

    if (raw.type === "content_block_start" && raw.content_block?.type === "thinking") {
        view.activeThinkingMessage = createStreamingThinkingBlock(sessionId, payload.uuid);
    }

    if (raw.type === "content_block_delta" && raw.delta?.type === "thinking_delta") {
        if (!view.activeThinkingMessage) {
            view.activeThinkingMessage = createStreamingThinkingBlock(sessionId, payload.uuid);
        }
        view.activeThinkingMessage.body.textContent += raw.delta.thinking || "";
        scrollTranscript();
    }

    if (raw.type === "content_block_delta" && raw.delta?.type === "text_delta") {
        updateExecutionProgress(sessionId, "正在整理回复", "Claude 正在生成可见答复", "running", true);
        if (!view.activeAgentMessage) {
            view.activeAgentMessage = appendMessage(sessionId, "agent", "", `agent-${payload.uuid || Date.now()}`);
        }
        view.activeAgentMessage.textContent += raw.delta.text || "";
        scrollTranscript();
    }
}

function handleAssistantEvent(sessionId, payload) {
    const blocks = payload.message?.content || [];
    for (const block of blocks) {
        if (block.type === "tool_use") {
            renderToolCard(sessionId, block);
            updateExecutionProgress(sessionId, "正在执行", describeToolUse(block), "running");
            appendActivity(sessionId, describeToolUse(block), "running");
        } else if (block.type === "thinking" && block.thinking) {
            renderThinkingBlock(sessionId, block, payload.uuid);
        }
    }
}

function markWorking(sessionId, kind) {
    const stableKinds = ["init", "session_state_changed", "result", "permission_denied", "informational"];
    if (stableKinds.includes(kind)) {
        return;
    }

    const view = ensureView(sessionId);
    if (view.isConfiguring) {
        return;
    }
    const session = state.sessions.find((item) => item.id === sessionId);
    if (session && !["needs-input", "stopping", "stopped", "completed", "failed"].includes(session.status)) {
        updateSessionStatus(sessionId, "working");
    }
}

function renderToolCard(sessionId, block) {
    const view = ensureView(sessionId);
    const key = `tool-${block.id}`;
    if (view.messageKeys.has(key)) {
        return view.toolCardsByUseId.get(block.id);
    }

    recordChange(sessionId, block.name, block.input);

    const details = document.createElement("details");
    details.className = "tool-card";

    const summary = document.createElement("summary");
    const label = toolLabels[block.name] || block.name;
    const target = block.input?.file_path || block.input?.notebook_path || block.input?.command || block.input?.pattern || block.input?.query;
    summary.innerHTML = `<i data-lucide="chevron-right" class="tool-card-chevron"></i><span class="tool-card-label">${escapeHtml(label)}</span>${target ? `<span class="tool-card-target">${escapeHtml(shorten(String(target), 90))}</span>` : ""}<span class="tool-card-status" data-status="running">运行中</span>`;

    const body = document.createElement("div");
    body.className = "tool-card-body";
    const inputPre = document.createElement("pre");
    inputPre.className = "tool-card-input";
    inputPre.textContent = prettyJson(block.input);
    body.append(inputPre);

    details.append(summary, body);
    view.messages.push({ key, role: "tool", element: details });
    view.messageKeys.add(key);
    const card = { details, body, statusEl: summary.querySelector(".tool-card-status") };
    view.toolCardsByUseId.set(block.id, card);
    if (state.activeSessionId === sessionId) {
        renderTranscript();
        createIcons();
    }
    return card;
}

function fillToolResult(sessionId, block) {
    const view = ensureView(sessionId);
    const card = view.toolCardsByUseId.get(block.tool_use_id);
    const text = extractToolResultText(block.content);
    if (!card) {
        updateExecutionProgress(sessionId, "工具已返回", "工具执行完成", block.is_error ? "attention" : "complete");
        appendActivity(sessionId, "工具结果已返回", block.is_error ? "attention" : "complete");
        return;
    }

    card.statusEl.textContent = block.is_error ? "失败" : "已完成";
    card.statusEl.dataset.status = block.is_error ? "error" : "complete";
    if (text) {
        const output = document.createElement("pre");
        output.className = "tool-card-output";
        output.textContent = shorten(text, 4000);
        card.body.append(output);
    }
    updateExecutionProgress(sessionId, block.is_error ? "工具执行失败" : "工具执行完成", text ? shorten(text.replace(/\s+/g, " "), 160) : "工具已返回结果", block.is_error ? "attention" : "complete");
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
}

function extractToolResultText(content) {
    if (typeof content === "string") {
        return content;
    }
    if (Array.isArray(content)) {
        return content.filter((entry) => entry.type === "text").map((entry) => entry.text).join("\n");
    }
    return "";
}

function renderThinkingBlock(sessionId, block, uuid) {
    const view = ensureView(sessionId);
    const key = `thinking-${uuid || block.thinking.slice(0, 40)}`;
    if (view.messageKeys.has(key)) {
        return;
    }

    const details = document.createElement("details");
    details.className = "message message-thinking";
    const summary = document.createElement("summary");
    summary.textContent = "思考过程";
    const body = document.createElement("div");
    body.className = "thinking-body";
    body.textContent = block.thinking;
    details.append(summary, body);

    view.messages.push({ key, role: "thinking", element: details });
    view.messageKeys.add(key);
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
}

function createStreamingThinkingBlock(sessionId, uuid) {
    const view = ensureView(sessionId);
    const key = `thinking-stream-${uuid || crypto.randomUUID()}`;
    const existing = view.messages.find((message) => message.key === key);
    if (existing) {
        return { details: existing.element, body: existing.element.querySelector(".thinking-body") };
    }

    const details = document.createElement("details");
    details.className = "message message-thinking";
    details.open = true;
    const summary = document.createElement("summary");
    summary.textContent = "思考中";
    const body = document.createElement("div");
    body.className = "thinking-body";
    details.append(summary, body);
    view.messages.push({ key, role: "thinking", element: details });
    view.messageKeys.add(key);
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
    return { details, body };
}

function startExecutionProgress(sessionId, summary) {
    const view = ensureView(sessionId);
    if (view.executionProgress?.details?.isConnected) {
        view.executionProgress.details.open = true;
        updateExecutionProgress(sessionId, summary, "", "running", true);
        return;
    }

    const key = `execution-progress-${crypto.randomUUID()}`;
    const details = document.createElement("details");
    details.className = "message execution-progress";
    details.open = true;
    const header = document.createElement("summary");
    header.innerHTML = "<span class='execution-progress-indicator' aria-hidden='true'></span><span class='execution-progress-title'>思考与执行过程</span><span class='execution-progress-status'>正在分析</span>";
    const body = document.createElement("div");
    body.className = "execution-progress-body";
    const list = document.createElement("ol");
    list.className = "execution-progress-list";
    body.append(list);
    details.append(header, body);

    view.messages.push({ key, role: "progress", element: details });
    view.messageKeys.add(key);
    view.executionProgress = {
        details,
        list,
        status: header.querySelector(".execution-progress-status"),
        lastText: ""
    };
    updateExecutionProgress(sessionId, summary, "Agent 已收到任务，正在规划下一步。", "running");
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
}

function updateExecutionProgress(sessionId, summary, detail, tone = "running", transient = false) {
    const view = ensureView(sessionId);
    if (!view.executionProgress) {
        startExecutionProgress(sessionId, summary || "正在分析你的任务");
    }
    const progress = view.executionProgress;
    progress.status.textContent = summary || "正在处理";
    progress.status.dataset.status = tone;
    progress.details.open = true;

    const normalizedDetail = String(detail || "").trim();
    if (!normalizedDetail || (transient && progress.lastText === normalizedDetail)) {
        return;
    }
    if (progress.lastText === normalizedDetail) {
        return;
    }

    const item = document.createElement("li");
    item.dataset.tone = tone;
    item.innerHTML = `<span class="execution-step-dot" aria-hidden="true"></span><div><strong>${escapeHtml(summary || "正在处理")}</strong><span>${escapeHtml(normalizedDetail)}</span></div>`;
    progress.list.append(item);
    while (progress.list.children.length > 12) {
        progress.list.firstElementChild.remove();
    }
    progress.lastText = normalizedDetail;
    if (state.activeSessionId === sessionId) {
        renderTranscript();
        scrollTranscript();
    }
}

function finishExecutionProgress(sessionId, summary, tone = "complete") {
    const view = ensureView(sessionId);
    const progress = view.executionProgress;
    if (!progress?.details?.isConnected) {
        return;
    }
    progress.status.textContent = summary;
    progress.status.dataset.status = tone;
    progress.details.open = false;
    view.executionProgress = null;
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
}

function handleUserEvent(sessionId, payload) {
    const content = payload.message?.content;
    const blocks = Array.isArray(content)
        ? content
        : typeof content === "string" && content
            ? [{ type: "text", text: content }]
            : [];

    // Tool results and skill bodies arrive as "user" role turns too; never show those as chat bubbles.
    const toolResultBlocks = blocks.filter((block) => block.type === "tool_result");
    if (toolResultBlocks.length > 0) {
        for (const block of toolResultBlocks) {
            fillToolResult(sessionId, block);
        }
        return;
    }

    const text = blocks.filter((block) => block.type === "text").map((block) => block.text).join("\n").trim();
    if (!text) {
        return;
    }

    const view = ensureView(sessionId);
    const alreadyShown = view.messages.some((existing) => existing.role === "user" && existing.element.textContent === text);
    if (alreadyShown) {
        return;
    }

    const isHumanOrigin = payload.origin?.kind === "human";
    if (!isHumanOrigin || text.length > 400) {
        appendActivity(sessionId, "Agent 已加载额外上下文（技能或工具内容）", "info");
        return;
    }

    appendMessage(sessionId, "user", text, `user-${payload.uuid || text}`);
}

async function submitPrompt() {
    const session = activeSession();
    const message = elements.input.value.trim();
    if (!session || (!message && state.attachments.length === 0)) {
        return;
    }

    if (state.attachments.length === 0 && await handleComposerSettingsCommand(session, message)) {
        elements.input.value = "";
        resizeComposer();
        return;
    }

    const attachments = state.attachments.map(({ mediaType, data, fileName }) => ({ mediaType, data, fileName }));
    elements.input.value = "";
    resizeComposer();
    if (message) {
        appendMessage(session.id, "user", message, `optimistic-${crypto.randomUUID()}`);
    }
    startExecutionProgress(session.id, "正在分析你的任务");
    appendActivity(session.id, "消息已加入 Agent 队列", "info");
    state.attachments = [];
    renderAttachments();
    render();

    try {
        await requestJson(`/api/agent/sessions/${encodeURIComponent(session.id)}/prompts`, {
            method: "POST",
            body: JSON.stringify({ message, attachments })
        });
        updateSessionStatus(session.id, "working");
        render();
    } catch (error) {
        appendMessage(session.id, "error", error.message, `request-${Date.now()}`);
        render();
    }
}

async function handleComposerSettingsCommand(session, message) {
    const [command, ...argumentsList] = message.split(/\s+/);
    const normalizedCommand = command.toLowerCase();
    if (normalizedCommand !== "/model" && normalizedCommand !== "/effort") {
        return false;
    }

    if (session.status !== "idle") {
        appendMessage(session.id, "error", "请等待当前任务结束后再修改模型或思考等级。", `settings-busy-${Date.now()}`);
        render();
        return true;
    }

    const argument = argumentsList.join(" ").trim();
    if (normalizedCommand === "/model") {
        if (!argument) {
            elements.modelSelect.focus();
            appendActivity(session.id, "请在右下角选择 Claude 模型", "info");
            return true;
        }

        const view = ensureView(session.id);
        const match = view.models.find((model) =>
            model.value.toLowerCase() === argument.toLowerCase() ||
            model.displayName.toLowerCase() === argument.toLowerCase());
        if (!match) {
            appendMessage(session.id, "error", `未找到模型“${argument}”。请使用右下角下拉列表选择。`, `model-not-found-${Date.now()}`);
            render();
            return true;
        }

        elements.modelSelect.value = match.value;
        await updateSessionSettings({ model: match.value });
        return true;
    }

    const thinkingTokens = {
        low: 4096,
        medium: 8192,
        high: 16384,
        extra: 32768,
        max: 65536,
        ultracode: 131072
    }[argument.toLowerCase()];
    if (!thinkingTokens) {
        elements.thinkingLevel.focus();
        appendActivity(session.id, "Effort：Low、Medium、High、Extra、Max、Ultracode", "info");
        return true;
    }

    elements.thinkingLevel.value = String(thinkingTokens);
    await updateSessionSettings({ maxThinkingTokens: thinkingTokens });
    return true;
}

async function interruptActiveSession() {
    const session = activeSession();
    if (!session) {
        return;
    }

    elements.sendButton.disabled = true;
    try {
        await requestJson(`/api/agent/sessions/${encodeURIComponent(session.id)}/interrupt`, { method: "POST" });
        updateSessionStatus(session.id, "stopping");
        appendActivity(session.id, "正在请求停止 Agent", "attention");
    } catch (error) {
        appendMessage(session.id, "error", error.message, `interrupt-${Date.now()}`);
    } finally {
        render();
    }
}

function handleComposerPaste(event) {
    const items = [...(event.clipboardData?.items || [])];
    const files = items
        .filter((item) => item.kind === "file" && item.type.startsWith("image/"))
        .map((item) => item.getAsFile())
        .filter(Boolean);
    if (files.length === 0) {
        return;
    }

    // Only swallow the paste once we know it actually contained an image; plain text paste is untouched.
    event.preventDefault();
    void addAttachments(files);
}

async function addAttachments(fileList) {
    const files = [...(fileList || [])];
    const remainingSlots = 5 - state.attachments.length;
    if (files.length > remainingSlots) {
        showRuntimeError("每条消息最多可添加 5 张图片。");
    }

    for (const file of files.slice(0, remainingSlots)) {
        if (!file.type.startsWith("image/")) {
            showRuntimeError("当前仅支持图片附件。");
            continue;
        }
        if (file.size > 10 * 1024 * 1024) {
            showRuntimeError(`${file.name} 超过 10 MB 限制。`);
            continue;
        }

        const data = await readFileAsBase64(file);
        state.attachments.push({
            id: crypto.randomUUID(),
            fileName: file.name || "粘贴的图片.png",
            fileSize: file.size,
            mediaType: file.type,
            data,
            previewUrl: URL.createObjectURL(file)
        });
    }

    elements.attachmentInput.value = "";
    renderAttachments();
}

function renderAttachments() {
    elements.attachmentList.replaceChildren();
    for (const attachment of state.attachments) {
        const item = document.createElement("div");
        item.className = "attachment-chip";

        const thumb = document.createElement("div");
        thumb.className = "attachment-thumb";
        const image = document.createElement("img");
        image.src = attachment.previewUrl;
        image.alt = attachment.fileName;
        image.addEventListener("error", () => thumb.classList.add("attachment-thumb-broken"), { once: true });
        thumb.append(image);

        const meta = document.createElement("div");
        meta.className = "attachment-meta";
        const name = document.createElement("span");
        name.className = "attachment-name";
        name.textContent = attachment.fileName;
        const size = document.createElement("span");
        size.className = "attachment-size";
        size.textContent = formatBytes(attachment.fileSize);
        meta.append(name, size);

        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "attachment-remove";
        remove.setAttribute("aria-label", `移除 ${attachment.fileName}`);
        remove.innerHTML = "<i data-lucide='x'></i>";
        remove.addEventListener("click", () => {
            URL.revokeObjectURL(attachment.previewUrl);
            state.attachments = state.attachments.filter((item) => item.id !== attachment.id);
            renderAttachments();
        });

        item.append(thumb, meta, remove);
        elements.attachmentList.append(item);
    }
    createIcons();
}

function handleComposerKeydown(event) {
    if (event.key === "Enter" && !event.shiftKey && !elements.commandMenu.hidden) {
        const firstCommand = elements.commandMenu.querySelector("button");
        if (firstCommand) {
            event.preventDefault();
            firstCommand.click();
        }
        return;
    }

    if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        if (elements.sendButton.dataset.action === "interrupt") {
            void interruptActiveSession();
            return;
        }
        elements.form.requestSubmit();
    }

    if (event.key === "Escape") {
        elements.commandMenu.hidden = true;
    }
}

function renderCommandMenu() {
    const session = activeSession();
    const text = elements.input.value.trimStart();
    if (!session || !text.startsWith("/")) {
        elements.commandMenu.hidden = true;
        return;
    }

    const matches = ensureView(session.id).commands
        .filter((command) => `/${command}`.toLowerCase().startsWith(text.toLowerCase()))
        .slice(0, 8);
    if (matches.length === 0) {
        elements.commandMenu.hidden = true;
        return;
    }

    elements.commandMenu.replaceChildren();
    for (const command of matches) {
        const button = document.createElement("button");
        button.type = "button";
        button.role = "option";
        button.innerHTML = `<i data-lucide="slash"></i><span>/${escapeHtml(command)}</span>`;
        button.addEventListener("click", () => {
            elements.input.value = `/${command} `;
            elements.commandMenu.hidden = true;
            elements.input.focus();
            resizeComposer();
        });
        elements.commandMenu.append(button);
    }
    elements.commandMenu.hidden = false;
    createIcons();
}

function renderInteraction() {
    const session = activeSession();
    const interaction = session ? ensureView(session.id).pendingRequest : null;
    elements.interactionDock.replaceChildren();
    elements.interactionDock.hidden = !interaction;
    if (!interaction || !session) {
        return;
    }

    const { payload } = interaction;
    const title = document.createElement("div");
    title.className = "interaction-title";
    title.innerHTML = `<i data-lucide="${interaction.type === "question-request" ? "circle-help" : "shield-check"}"></i><div><strong>${interaction.type === "question-request" ? "Claude 需要你的决定" : "请求执行权限"}</strong><span>${payload.title || payload.displayName || payload.toolName || "Agent 请求"}</span></div>`;
    elements.interactionDock.append(title);

    if (interaction.type === "question-request") {
        renderQuestionInteraction(session, payload);
    } else {
        renderPermissionInteraction(session, payload);
    }
    createIcons();
}

function renderPermissionInteraction(session, payload) {
    const description = document.createElement("p");
    description.className = "interaction-description";
    description.textContent = payload.description || payload.decisionReason || "请确认是否允许本次操作。";
    const detail = document.createElement("pre");
    detail.className = "interaction-detail";
    detail.textContent = prettyJson(payload.input);
    const actions = document.createElement("div");
    actions.className = "interaction-actions";
    const remember = document.createElement("label");
    remember.className = "remember-choice";
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.disabled = !(payload.suggestions || []).length;
    remember.append(checkbox, document.createTextNode("记住此规则"));
    const deny = actionButton("拒绝", "button button-secondary", () => void respondToInteraction(session.id, {
        requestId: payload.requestId,
        decision: "deny",
        message: "用户拒绝了这次操作。"
    }));
    const allow = actionButton("允许一次", "button button-primary", () => void respondToInteraction(session.id, {
        requestId: payload.requestId,
        decision: "allow",
        remember: checkbox.checked
    }));
    actions.append(remember, deny, allow);
    elements.interactionDock.append(description, detail, actions);
}

function renderQuestionInteraction(session, payload) {
    const form = document.createElement("form");
    form.className = "question-form";
    const questions = payload.input?.questions || [];
    for (const [index, question] of questions.entries()) {
        const fieldset = document.createElement("fieldset");
        const legend = document.createElement("legend");
        legend.textContent = question.header || `问题 ${index + 1}`;
        const prompt = document.createElement("p");
        prompt.textContent = question.question;
        const select = document.createElement(question.multiSelect ? "select" : "select");
        select.name = question.question;
        select.multiple = Boolean(question.multiSelect);
        select.required = true;
        for (const option of question.options || []) {
            const choice = document.createElement("option");
            choice.value = option.label;
            choice.textContent = option.description ? `${option.label} - ${option.description}` : option.label;
            select.append(choice);
        }
        fieldset.append(legend, prompt, select);
        form.append(fieldset);
    }

    const freeText = document.createElement("textarea");
    freeText.name = "free-text";
    freeText.rows = 2;
    freeText.placeholder = "补充说明（可选）";
    const actions = document.createElement("div");
    actions.className = "interaction-actions";
    actions.append(actionButton("暂不回答", "button button-secondary", () => void respondToInteraction(session.id, {
        requestId: payload.requestId,
        decision: "deny",
        message: "用户暂时不提供答案。"
    })), actionButton("提交答案", "button button-primary", () => form.requestSubmit()));
    form.append(freeText, actions);
    form.addEventListener("submit", (event) => {
        event.preventDefault();
        const answers = {};
        for (const question of questions) {
            const control = form.elements.namedItem(question.question);
            if (control instanceof HTMLSelectElement) {
                const selected = [...control.selectedOptions].map((option) => option.value);
                answers[question.question] = question.multiSelect ? selected : selected[0] || "";
            }
        }
        void respondToInteraction(session.id, {
            requestId: payload.requestId,
            decision: "allow",
            answers,
            response: freeText.value.trim() || undefined
        });
    });
    elements.interactionDock.append(form);
}

async function respondToInteraction(sessionId, response) {
    try {
        await requestJson(`/api/agent/sessions/${encodeURIComponent(sessionId)}/responses`, {
            method: "POST",
            body: JSON.stringify(response)
        });
        const view = ensureView(sessionId);
        view.pendingRequest = null;
        updateSessionStatus(sessionId, "working");
        render();
    } catch (error) {
        appendMessage(sessionId, "error", error.message, `response-${Date.now()}`);
        render();
    }
}

function render() {
    renderWorkspaceOptions();
    renderSessionList();
    renderSessionHeader();
    renderTranscript();
    renderActivity();
    renderChanges();
    renderInteraction();
    renderComposer();
    createIcons();
}

function renderWorkspaceOptions() {
    const selected = elements.workspaceSelect.value;
    elements.workspaceSelect.replaceChildren();
    for (const workspace of state.workspaces) {
        const option = document.createElement("option");
        option.value = workspace.id;
        option.textContent = workspace.isGitRepository ? `${workspace.name} - Git` : workspace.name;
        elements.workspaceSelect.append(option);
    }
    if (state.workspaces.some((workspace) => workspace.id === selected)) {
        elements.workspaceSelect.value = selected;
    }
    const existing = findActiveUnisolatedSession(elements.workspaceSelect.value);
    elements.newSessionButton.disabled = state.workspaces.length === 0;
    elements.newSessionButton.title = existing ? "该非 Git 工作区已有活动会话，请继续现有会话。" : "新建会话";
    elements.newSessionButton.querySelector("span").textContent = existing ? "继续现有会话" : "新建会话";
}

function findActiveUnisolatedSession(workspaceId) {
    return state.sessions.find((session) =>
        session.workspaceId === workspaceId &&
        !session.isIsolated &&
        !session.isHistorical &&
        !["completed", "failed", "stopped"].includes(session.status));
}

function renderSessionList() {
    elements.sessionList.replaceChildren();
    elements.sessionCount.textContent = String(state.sessions.length);
    if (state.sessions.length === 0) {
        const blank = document.createElement("p");
        blank.className = "session-list-empty";
        blank.textContent = "还没有会话";
        elements.sessionList.append(blank);
        return;
    }

    for (const session of state.sessions) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "session-item";
        item.classList.toggle("is-active", session.id === state.activeSessionId);
        item.setAttribute("aria-current", session.id === state.activeSessionId ? "page" : "false");
        item.innerHTML = `<span class="session-item-dot status-${escapeHtml(session.status)}"></span><span class="session-item-copy"><strong>${escapeHtml(session.name)}</strong><small>${escapeHtml(statusLabels[session.status] || session.status)}</small></span>${session.isIsolated ? "<i data-lucide='git-branch' aria-hidden='true'></i>" : ""}`;
        item.addEventListener("click", () => selectSession(session.id));
        item.addEventListener("contextmenu", (event) => showSessionContextMenu(event, session.id));
        item.addEventListener("keydown", (event) => {
            if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
                showSessionContextMenu(event, session.id);
            }
        });
        elements.sessionList.append(item);
    }
}

function renderSessionHeader() {
    const session = activeSession();
    if (!session) {
        elements.sessionTitle.textContent = "未选择会话";
        elements.sessionStatus.textContent = "选择一个工作区以开始";
        elements.sessionStatusDot.className = "status-dot status-idle";
        return;
    }

    elements.sessionTitle.textContent = session.name;
    elements.sessionStatus.textContent = statusLabels[session.status] || session.status;
    elements.sessionStatusDot.className = `status-dot status-${session.status}`;
}

function renderTranscript() {
    const session = activeSession();
    elements.transcript.replaceChildren();
    if (!session) {
        elements.transcript.append(elements.emptyState);
        return;
    }

    const view = ensureView(session.id);
    if (view.messages.length === 0) {
        const blank = document.createElement("div");
        blank.className = "empty-state compact-empty";
        blank.innerHTML = "<div class='empty-glyph'><i data-lucide='message-square'></i></div><h2>告诉 Agent 你要完成什么</h2><p>技能、项目指令和已配置的 MCP 会自动随会话加载。</p>";
        elements.transcript.append(blank);
        return;
    }

    for (const message of view.messages) {
        elements.transcript.append(message.element);
    }
    scrollTranscript();
}

function renderActivity() {
    const session = activeSession();
    elements.activityList.replaceChildren();
    if (!session) {
        return;
    }

    const activities = ensureView(session.id).activities.slice(-40).reverse();
    for (const activity of activities) {
        const item = document.createElement("li");
        item.className = `activity-item activity-${activity.tone}`;
        item.innerHTML = `<span></span><div><strong>${escapeHtml(activity.text)}</strong><small>${formatTime(activity.at)}</small></div>`;
        elements.activityList.append(item);
    }
}

function renderChanges() {
    const session = activeSession();
    elements.changesList.replaceChildren();
    if (!session) {
        return;
    }

    const changes = ensureView(session.id).changes;
    if (changes.length === 0) {
        const blank = document.createElement("p");
        blank.className = "changes-empty";
        blank.textContent = "尚未观察到文件写入事件";
        elements.changesList.append(blank);
        return;
    }

    for (const change of changes) {
        const item = document.createElement("div");
        item.className = "change-item";
        item.innerHTML = `<i data-lucide="file-pen-line"></i><div><strong>${escapeHtml(change.file || change.tool)}</strong><small>${escapeHtml(change.action)}</small></div>`;
        elements.changesList.append(item);
    }
}

function renderComposer() {
    const session = activeSession();
    const isTerminal = session && ["stopped", "completed", "failed"].includes(session.status);
    const resumable = Boolean(session?.claudeSessionId);
    const disabled = !session || (isTerminal && !resumable);
    const isRunning = session && ["starting", "working"].includes(session.status);
    const isStopping = session?.status === "stopping";
    const canConfigure = Boolean(session && session.status === "idle");
    elements.input.disabled = disabled;
    elements.sendButton.disabled = disabled || isStopping;
    elements.attachmentInput.disabled = disabled;
    elements.permissionMode.disabled = !canConfigure;
    elements.modelSelect.disabled = !canConfigure;
    elements.thinkingLevel.disabled = !canConfigure;
    renderModelOptions(session);
    if (session?.permissionMode) {
        elements.permissionMode.value = session.permissionMode;
    }
    elements.sendButton.dataset.action = isRunning ? "interrupt" : "send";
    elements.sendButton.type = isRunning ? "button" : "submit";
    elements.sendButton.classList.toggle("is-stop", Boolean(isRunning || isStopping));
    elements.sendButton.setAttribute("aria-label", isRunning ? "停止 Agent" : "发送消息");
    elements.sendButton.innerHTML = isRunning || isStopping
        ? "<i data-lucide='square'></i><span class='sr-only'>停止</span>"
        : "<i data-lucide='arrow-up'></i><span class='sr-only'>发送</span>";
    elements.input.placeholder = disabled
        ? session ? "此会话无法恢复，请新建会话继续" : "先从左侧选择工作区并新建会话"
        : isTerminal ? "发送消息以恢复此会话"
        : "描述任务，或输入 / 使用原生命令与技能";
}

function renderModelOptions(session) {
    const view = session ? ensureView(session.id) : null;
    const models = view?.models || [];
    const selectedModel = view?.model || "";
    elements.modelSelect.replaceChildren();

    const defaultOption = document.createElement("option");
    defaultOption.value = "";
    defaultOption.textContent = models.length > 0 ? "默认模型" : "正在加载模型";
    elements.modelSelect.append(defaultOption);

    for (const model of models) {
        const option = document.createElement("option");
        option.value = model.value;
        option.textContent = model.displayName || model.value;
        option.title = model.description || option.textContent;
        elements.modelSelect.append(option);
    }

    if (selectedModel && !models.some((model) => model.value === selectedModel)) {
        const option = document.createElement("option");
        option.value = selectedModel;
        option.textContent = selectedModel;
        elements.modelSelect.append(option);
    }
    elements.modelSelect.value = selectedModel;
    elements.thinkingLevel.value = String(view?.maxThinkingTokens || 8192);
}

function updateSessionSettings(patch) {
    state.settingsUpdate = state.settingsUpdate
        .catch(() => undefined)
        .then(() => applySessionSettings(patch));
    return state.settingsUpdate;
}

async function applySessionSettings(patch) {
    const session = activeSession();
    if (!session || session.status !== "idle") {
        return;
    }

    const view = ensureView(session.id);
    const model = patch.model ?? view.model ?? null;
    const maxThinkingTokens = patch.maxThinkingTokens ?? view.maxThinkingTokens ?? 8192;
    const permissionMode = patch.permissionMode ?? session.permissionMode;
    view.isConfiguring = true;
    setBusy(elements.modelSelect, true);
    setBusy(elements.thinkingLevel, true);
    setBusy(elements.permissionMode, true);
    try {
        await requestJson(`/api/agent/sessions/${encodeURIComponent(session.id)}/settings`, {
            method: "POST",
            body: JSON.stringify({ model, maxThinkingTokens, permissionMode })
        });
        view.model = model;
        view.maxThinkingTokens = maxThinkingTokens;
        updateSession(session.id, { permissionMode, status: "idle" });
        appendActivity(session.id, "已应用 Mode、模型与 Effort", "complete");
    } catch (error) {
        appendMessage(session.id, "error", error.message, `settings-${Date.now()}`);
    } finally {
        view.isConfiguring = false;
        render();
    }
}

function activateInspectorTab(tab) {
    const panelId = tab.dataset.panel;
    document.querySelectorAll(".inspector-tab").forEach((item) => {
        item.setAttribute("aria-selected", String(item === tab));
    });
    document.querySelectorAll(".inspector-panel").forEach((panel) => {
        panel.hidden = panel.id !== panelId;
    });
}

function appendMessage(sessionId, role, text, key) {
    const view = ensureView(sessionId);
    if (view.messageKeys.has(key)) {
        return view.messages.find((message) => message.key === key)?.element;
    }
    if (role === "user") {
        const last = view.messages.at(-1);
        if (last?.role === "user" && last.element.textContent === text) {
            return last.element;
        }
    }

    const article = document.createElement("article");
    article.className = `message message-${role}`;
    article.textContent = text;
    view.messages.push({ key, role, element: article });
    view.messageKeys.add(key);
    if (state.activeSessionId === sessionId) {
        renderTranscript();
    }
    scheduleHistorySave(sessionId);
    return article;
}

function appendActivity(sessionId, text, tone = "info") {
    const view = ensureView(sessionId);
    const previous = view.activities.at(-1);
    if (previous?.text === text && Date.now() - previous.at < 2_000) {
        return;
    }
    view.activities.push({ text, tone, at: Date.now() });
    if (view.activities.length > 80) {
        view.activities.shift();
    }
    if (state.activeSessionId === sessionId) {
        renderActivity();
    }
    scheduleHistorySave(sessionId);
}

function scheduleHistorySave(sessionId) {
    const session = state.sessions.find((item) => item.id === sessionId);
    if (!session || session.isHistorical) {
        return;
    }
    clearTimeout(state.historyWriteTimers.get(sessionId));
    state.historyWriteTimers.set(sessionId, setTimeout(() => void persistHistorySession(sessionId), 250));
}

async function persistHistorySession(sessionId) {
    const session = state.sessions.find((item) => item.id === sessionId);
    const view = state.views.get(sessionId);
    if (!session || !view) {
        return;
    }
    const record = {
        id: sessionId,
        summary: { ...session, isHistorical: false },
        updatedAt: new Date().toISOString(),
        view: {
            activities: view.activities.slice(-80),
            commands: view.commands,
            maxThinkingTokens: view.maxThinkingTokens,
            messages: view.messages.map((message) => ({ role: message.role, text: message.element.textContent || "" })).slice(-300),
            model: view.model,
            models: view.models,
            sequences: [...view.sequences]
        }
    };
    state.history.set(sessionId, record);
    try {
        await historyDatabase.put(record);
    } catch (error) {
        console.warn("Unable to save local conversation history.", error);
    }
}

function restoreViewSnapshot(sessionId, snapshot = {}) {
    const view = ensureView(sessionId);
    if (view.messages.length > 0 || !snapshot) {
        return;
    }
    view.activities = Array.isArray(snapshot.activities) ? snapshot.activities : [];
    view.commands = Array.isArray(snapshot.commands) ? snapshot.commands : [];
    view.maxThinkingTokens = snapshot.maxThinkingTokens || view.maxThinkingTokens;
    view.model = snapshot.model || null;
    view.models = Array.isArray(snapshot.models) ? snapshot.models : [];
    view.sequences = new Set(Array.isArray(snapshot.sequences) ? snapshot.sequences : []);
    for (const message of snapshot.messages || []) {
        const role = message.role || "agent";
        const text = message.text || "";
        if (view.messages.some((existing) => existing.role === role && existing.element.textContent === text)) {
            continue;
        }
        const article = document.createElement("article");
        article.className = `message message-${role}`;
        article.textContent = text;
        const key = `history-${crypto.randomUUID()}`;
        view.messages.push({ key, role, element: article });
        view.messageKeys.add(key);
    }
}

function showSessionContextMenu(event, sessionId) {
    event.preventDefault();
    state.menuSessionId = sessionId;
    const menu = elements.sessionContextMenu;
    menu.hidden = false;
    menu.style.left = `${Math.min(event.clientX, window.innerWidth - 190)}px`;
    menu.style.top = `${Math.min(event.clientY, window.innerHeight - 100)}px`;
}

function hideSessionContextMenu() {
    elements.sessionContextMenu.hidden = true;
    state.menuSessionId = null;
}

function handleSessionContextAction(event) {
    const action = event.target.closest("[data-session-action]")?.dataset.sessionAction;
    const session = state.sessions.find((item) => item.id === state.menuSessionId);
    hideSessionContextMenu();
    if (!action || !session) {
        return;
    }
    state.pendingDialogSessionId = session.id;
    if (action === "rename") {
        elements.sessionRenameInput.value = session.name;
        elements.sessionRenameDialog.showModal();
        elements.sessionRenameInput.focus();
        elements.sessionRenameInput.select();
        return;
    }
    elements.sessionDeleteDialog.showModal();
}

async function submitSessionRename(event) {
    event.preventDefault();
    const session = state.sessions.find((item) => item.id === state.pendingDialogSessionId);
    const name = elements.sessionRenameInput.value.trim();
    if (!session || !name) {
        return;
    }
    try {
        let updated = { ...session, name };
        if (session.isHistorical) {
            const record = state.history.get(session.id);
            if (record) {
                const renamedRecord = {
                    ...record,
                    summary: { ...record.summary, name },
                    updatedAt: new Date().toISOString()
                };
                state.history.set(session.id, renamedRecord);
                await historyDatabase.put(renamedRecord);
            }
        } else {
            updated = await requestJson(`/api/agent/sessions/${encodeURIComponent(session.id)}`, {
                method: "PATCH",
                body: JSON.stringify({ name })
            });
        }
        updateSession(session.id, updated);
        elements.sessionRenameDialog.close();
        render();
    } catch (error) {
        showRuntimeError(error.message);
    }
}

async function submitSessionDelete(event) {
    event.preventDefault();
    const session = state.sessions.find((item) => item.id === state.pendingDialogSessionId);
    if (!session) {
        return;
    }
    try {
        if (!session.isHistorical) {
            await requestJson(`/api/agent/sessions/${encodeURIComponent(session.id)}`, { method: "DELETE" });
        }
        clearTimeout(state.historyWriteTimers.get(session.id));
        state.history.delete(session.id);
        state.views.delete(session.id);
        await historyDatabase.remove(session.id);
        state.sessions = state.sessions.filter((item) => item.id !== session.id);
        if (state.activeSessionId === session.id) {
            closeEventSource();
            state.activeSessionId = state.sessions[0]?.id || null;
            if (state.activeSessionId) {
                selectSession(state.activeSessionId);
            }
        }
        elements.sessionDeleteDialog.close();
        render();
    } catch (error) {
        showRuntimeError(error.message);
    }
}

function recordChange(sessionId, tool, input) {
    if (!["Edit", "Write", "NotebookEdit"].includes(tool)) {
        return;
    }
    const view = ensureView(sessionId);
    const file = input?.file_path || input?.notebook_path || "未命名文件";
    view.changes.unshift({ tool, file, action: toolLabels[tool] || tool });
    view.changes = view.changes.slice(0, 20);
}

function describeToolUse(block) {
    const target = block.input?.file_path || block.input?.notebook_path || block.input?.command || block.input?.pattern || block.input?.query;
    return `${toolLabels[block.name] || block.name}${target ? `：${shorten(String(target), 76)}` : ""}`;
}

function ensureView(sessionId) {
    if (!state.views.has(sessionId)) {
        state.views.set(sessionId, {
            activities: [],
            activeAgentMessage: null,
            activeThinkingMessage: null,
            changes: [],
            commands: [],
            executionProgress: null,
            maxThinkingTokens: 8192,
            messageKeys: new Set(),
            messages: [],
            model: null,
            models: [],
            isConfiguring: false,
            pendingRequest: null,
            sequences: new Set(),
            toolCardsByUseId: new Map()
        });
    }
    return state.views.get(sessionId);
}

function activeSession() {
    return state.sessions.find((session) => session.id === state.activeSessionId) || null;
}

function updateSession(sessionId, patch) {
    state.sessions = state.sessions.map((session) => session.id === sessionId ? { ...session, ...patch } : session);
    scheduleHistorySave(sessionId);
}

function updateSessionStatus(sessionId, status) {
    updateSession(sessionId, { status });
}

function mapAgentState(stateName) {
    return {
        idle: "idle",
        requires_action: "needs-input"
    }[stateName] || null;
}

async function requestJson(url, options = {}) {
    const headers = new Headers(options.headers || {});
    const method = (options.method || "GET").toUpperCase();
    headers.set("Accept", "application/json");
    if (options.body) {
        headers.set("Content-Type", "application/json");
    }
    if (method !== "GET" && method !== "HEAD" && elements.csrfToken?.value) {
        headers.set("X-CSRF-TOKEN", elements.csrfToken.value);
    }

    const response = await fetch(url, { ...options, headers });
    if (!response.ok) {
        const payload = await response.json().catch(() => ({}));
        throw new Error(payload.error || `请求失败 (${response.status})`);
    }
    if (response.status === 204 || response.status === 202) {
        return null;
    }
    return response.json();
}

function readFileAsBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).split(",", 2)[1]);
        reader.onerror = () => reject(new Error(`无法读取附件 ${file.name}`));
        reader.readAsDataURL(file);
    });
}

function actionButton(label, className, action) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.textContent = label;
    button.addEventListener("click", action);
    return button;
}

function setBusy(button, busy) {
    button.disabled = busy;
    button.setAttribute("aria-busy", String(busy));
}

function resizeComposer() {
    elements.input.style.height = "auto";
    const maxHeight = 176;
    elements.input.style.height = `${Math.min(elements.input.scrollHeight, maxHeight)}px`;
    elements.input.style.overflowY = elements.input.scrollHeight > maxHeight ? "auto" : "hidden";
}

function scrollTranscript() {
    elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function showRuntimeError(message) {
    elements.sessionStatus.textContent = message;
    elements.sessionStatusDot.className = "status-dot status-failed";
}

function prettyJson(value) {
    try {
        return JSON.stringify(value, null, 2);
    } catch {
        return String(value || "");
    }
}

function shorten(value, limit) {
    return value.length <= limit ? value : `${value.slice(0, limit - 3)}...`;
}

function formatBytes(bytes) {
    if (!Number.isFinite(bytes) || bytes <= 0) {
        return "";
    }
    if (bytes < 1024) {
        return `${bytes} B`;
    }
    const units = ["KB", "MB", "GB"];
    let value = bytes / 1024;
    let unitIndex = 0;
    while (value >= 1024 && unitIndex < units.length - 1) {
        value /= 1024;
        unitIndex += 1;
    }
    return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unitIndex]}`;
}

function formatTime(value) {
    return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit" }).format(value);
}

function escapeHtml(value) {
    return String(value).replace(/[&<>'"]/g, (character) => ({
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        "'": "&#39;",
        "\"": "&quot;"
    })[character]);
}

function createIcons() {
    window.lucide?.createIcons({ attrs: { "stroke-width": 1.8 } });
}
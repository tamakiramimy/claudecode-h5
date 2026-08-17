import { createInterface } from "node:readline";
import { access, realpath, stat } from "node:fs/promises";
import { constants } from "node:fs";
import { randomUUID } from "node:crypto";
import { query } from "@anthropic-ai/claude-agent-sdk";

const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
const MAX_ATTACHMENTS_PER_MESSAGE = 5;
const IMAGE_MEDIA_TYPES = new Set([
    "image/gif",
    "image/jpeg",
    "image/png",
    "image/webp"
]);
const PERMISSION_MODES = new Set(["default", "acceptEdits", "plan", "bypassPermissions"]);
const sessions = new Map();

class AsyncQueue {
    #closed = false;
    #items = [];
    #waiters = [];

    push(item) {
        if (this.#closed) {
            throw new Error("The agent input stream has already closed.");
        }

        const waiter = this.#waiters.shift();
        if (waiter) {
            waiter({ value: item, done: false });
            return;
        }

        this.#items.push(item);
    }

    close() {
        if (this.#closed) {
            return;
        }

        this.#closed = true;
        for (const waiter of this.#waiters.splice(0)) {
            waiter({ value: undefined, done: true });
        }
    }

    async next() {
        if (this.#items.length > 0) {
            return { value: this.#items.shift(), done: false };
        }

        if (this.#closed) {
            return { value: undefined, done: true };
        }

        return new Promise((resolve) => this.#waiters.push(resolve));
    }

    [Symbol.asyncIterator]() {
        return this;
    }
}

async function main() {
    const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
    for await (const line of input) {
        if (!line.trim()) {
            continue;
        }

        try {
            await handleCommand(JSON.parse(line));
        } catch (error) {
            emit({
                type: "command-error",
                commandId: typeof tryGetCommandId(line) === "string" ? tryGetCommandId(line) : undefined,
                error: errorMessage(error)
            });
        }
    }

    await closeAllSessions("stdin-closed");
}

async function handleCommand(command) {
    if (!isObject(command) || typeof command.type !== "string") {
        throw new Error("Each bridge command must be a JSON object with a type.");
    }

    switch (command.type) {
        case "ping":
            emit({
                type: "pong",
                commandId: command.commandId,
                bridgeVersion: "0.1.0",
                nodeVersion: process.version
            });
            return;
        case "start":
            await startSession(command);
            return;
        case "prompt":
            enqueuePrompt(command);
            return;
        case "interrupt":
            await interruptSession(command);
            return;
        case "configure":
            await configureSession(command);
            return;
        case "respond":
            respondToRequest(command);
            return;
        case "close":
            await closeSession(command.sessionId, "requested");
            emit({ type: "closed", commandId: command.commandId, sessionId: command.sessionId });
            return;
        case "shutdown":
            await closeAllSessions("shutdown");
            emit({ type: "shutdown-complete", commandId: command.commandId });
            return;
        default:
            throw new Error(`Unsupported bridge command: ${command.type}`);
    }
}

async function startSession(command) {
    const sessionId = requireSessionId(command.sessionId);
    if (sessions.has(sessionId)) {
        throw new Error(`The agent session '${sessionId}' already exists.`);
    }

    const cwd = await resolveDirectory(command.cwd, "cwd");
    const executablePath = await resolveExecutable(command.executablePath);
    const permissionMode = normalizePermissionMode(command.permissionMode);
    const input = new AsyncQueue();
    const session = {
        id: sessionId,
        input,
        pendingRequests: new Map(),
        query: undefined,
        closed: false
    };

    const options = {
        cwd,
        includePartialMessages: true,
        pathToClaudeCodeExecutable: executablePath,
        permissionMode,
        allowDangerouslySkipPermissions: true,
        canUseTool: (toolName, toolInput, requestOptions) => requestPermission(session, toolName, toolInput, requestOptions)
    };

    if (typeof command.model === "string" && command.model.trim()) {
        options.model = command.model.trim();
    }

    if (typeof command.resume === "string" && command.resume.trim()) {
        options.resume = command.resume.trim();
    }

    if (Number.isInteger(command.maxTurns) && command.maxTurns > 0) {
        options.maxTurns = command.maxTurns;
    }

    if (Number.isInteger(command.maxThinkingTokens) && command.maxThinkingTokens > 0) {
        options.maxThinkingTokens = command.maxThinkingTokens;
    }

    if (typeof command.settingsPath === "string" && command.settingsPath.trim()) {
        options.settings = await resolveFile(command.settingsPath, "settingsPath");
    }

    if (Array.isArray(command.settingSources)) {
        options.settingSources = command.settingSources;
    }

    session.query = query({ prompt: input, options });
    sessions.set(sessionId, session);
    void publishCapabilities(session);
    void forwardEvents(session);

    emit({
        type: "started",
        commandId: command.commandId,
        sessionId,
        cwd,
        permissionMode
    });
}

async function publishCapabilities(session) {
    try {
        const [commands, models] = await Promise.all([
            session.query.supportedCommands(),
            session.query.supportedModels()
        ]);
        if (session.closed) {
            return;
        }

        const slashCommands = commands
            .map((command) => typeof command === "string"
                ? command
                : command.name ?? command.command ?? command.value)
            .filter((command) => typeof command === "string" && command.trim())
            .map((command) => command.replace(/^\//, ""));
        emit({
            type: "event",
            sessionId: session.id,
            event: {
                kind: "capabilities",
                payload: {
                    slash_commands: slashCommands,
                    models: models
                        .filter((model) => model && typeof model.value === "string")
                        .map((model) => ({
                            value: model.value,
                            displayName: typeof model.displayName === "string" ? model.displayName : model.value,
                            description: typeof model.description === "string" ? model.description : ""
                        }))
                }
            }
        });
    } catch (error) {
        if (!session.closed) {
            emit({
                type: "event",
                sessionId: session.id,
                event: {
                    kind: "informational",
                    payload: { content: `命令清单将在 Claude 初始化后加载：${errorMessage(error)}` }
                }
            });
        }
    }
}

async function configureSession(command) {
    const session = getOpenSession(command.sessionId);
    const model = typeof command.model === "string" && command.model.trim() ? command.model.trim() : null;
    const maxThinkingTokens = Number.isInteger(command.maxThinkingTokens) && command.maxThinkingTokens > 0
        ? command.maxThinkingTokens
        : null;

    if (model) {
        await session.query.setModel(model);
    }
    if (maxThinkingTokens) {
        await session.query.setMaxThinkingTokens(maxThinkingTokens);
    }
    const permissionMode = typeof command.permissionMode === "string" && command.permissionMode.trim()
        ? normalizePermissionMode(command.permissionMode)
        : null;
    if (permissionMode) {
        await session.query.setPermissionMode(permissionMode);
    }

    emit({
        type: "configured",
        commandId: command.commandId,
        sessionId: session.id,
        model,
        maxThinkingTokens,
        permissionMode
    });
}

function enqueuePrompt(command) {
    const session = getOpenSession(command.sessionId);
    const userMessage = createUserMessage(command);
    session.input.push(userMessage);
    emit({
        type: "prompt-queued",
        commandId: command.commandId,
        sessionId: session.id,
        messageId: userMessage.uuid
    });
}

async function interruptSession(command) {
    const session = getOpenSession(command.sessionId);
    const receipt = await session.query.interrupt();
    emit({
        type: "interrupted",
        commandId: command.commandId,
        sessionId: session.id,
        receipt: receipt ?? null
    });
}

function respondToRequest(command) {
    const session = getOpenSession(command.sessionId);
    if (typeof command.requestId !== "string") {
        throw new Error("A bridge response requires requestId.");
    }

    const request = session.pendingRequests.get(command.requestId);
    if (!request) {
        throw new Error(`No pending request '${command.requestId}' exists for this session.`);
    }

    session.pendingRequests.delete(command.requestId);
    if (command.decision === "allow") {
        const updatedInput = buildApprovedInput(request, command);
        request.resolve({
            behavior: "allow",
            updatedInput,
            updatedPermissions: command.remember === true ? request.suggestions : undefined
        });
    } else {
        request.resolve({
            behavior: "deny",
            interrupt: command.interrupt === true,
            message: typeof command.message === "string" && command.message.trim()
                ? command.message.trim()
                : "The user declined this action."
        });
    }

    emit({
        type: "request-resolved",
        commandId: command.commandId,
        sessionId: session.id,
        requestId: command.requestId,
        decision: command.decision === "allow" ? "allow" : "deny"
    });
}

async function forwardEvents(session) {
    try {
        for await (const message of session.query) {
            emit({
                type: "event",
                sessionId: session.id,
                event: serializeEvent(message)
            });
        }

        emit({ type: "session-ended", sessionId: session.id, reason: "completed" });
    } catch (error) {
        emit({
            type: "session-ended",
            sessionId: session.id,
            reason: "error",
            error: errorMessage(error)
        });
    } finally {
        if (!session.closed) {
            await closeSession(session.id, "agent-ended");
        }
    }
}

function requestPermission(session, toolName, toolInput, options) {
    return new Promise((resolve) => {
        if (session.closed) {
            resolve({ behavior: "deny", message: "The web session was closed before the request could be approved." });
            return;
        }

        session.pendingRequests.set(options.requestId, {
            input: toolInput,
            resolve,
            suggestions: options.suggestions ?? [],
            toolName
        });

        emit({
            type: toolName === "AskUserQuestion" ? "question-request" : "permission-request",
            sessionId: session.id,
            requestId: options.requestId,
            toolUseId: options.toolUseID,
            toolName,
            input: sanitizeForEvent(toolInput),
            title: options.title,
            displayName: options.displayName,
            description: options.description,
            decisionReason: options.decisionReason,
            blockedPath: options.blockedPath,
            agentId: options.agentID,
            matchedAskRule: options.matchedAskRule,
            suggestions: sanitizeForEvent(options.suggestions ?? [])
        });
    });
}

async function closeSession(sessionId, reason) {
    const session = sessions.get(sessionId);
    if (!session || session.closed) {
        return;
    }

    session.closed = true;
    sessions.delete(sessionId);
    for (const [requestId, request] of session.pendingRequests) {
        request.resolve({ behavior: "deny", message: `The session closed before request '${requestId}' was answered.` });
    }

    session.pendingRequests.clear();
    session.input.close();
    session.query.close();
    emit({ type: "session-closed", sessionId, reason });
}

async function closeAllSessions(reason) {
    await Promise.all([...sessions.keys()].map((sessionId) => closeSession(sessionId, reason)));
}

function createUserMessage(command) {
    const text = typeof command.message === "string" ? command.message.trim() : "";
    const attachments = Array.isArray(command.attachments) ? command.attachments : [];
    if (!text && attachments.length === 0) {
        throw new Error("A prompt needs text, an attachment, or both.");
    }

    if (attachments.length > MAX_ATTACHMENTS_PER_MESSAGE) {
        throw new Error(`A prompt can include at most ${MAX_ATTACHMENTS_PER_MESSAGE} attachments.`);
    }

    const content = attachments.length === 0
        ? text
        : [
            ...(text ? [{ type: "text", text }] : []),
            ...attachments.map(normalizeImageAttachment)
        ];

    return {
        type: "user",
        uuid: randomUUID(),
        parent_tool_use_id: null,
        origin: { kind: "human" },
        message: {
            role: "user",
            content
        }
    };
}

function normalizeImageAttachment(attachment) {
    if (!isObject(attachment) || typeof attachment.mediaType !== "string" || typeof attachment.data !== "string") {
        throw new Error("Every attachment needs a mediaType and base64 data.");
    }

    if (!IMAGE_MEDIA_TYPES.has(attachment.mediaType)) {
        throw new Error(`Unsupported attachment media type: ${attachment.mediaType}`);
    }

    const data = attachment.data.replace(/\s/g, "");
    if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(data)) {
        throw new Error("Attachment data must be valid base64.");
    }

    const byteLength = Buffer.byteLength(data, "base64");
    if (byteLength > MAX_ATTACHMENT_BYTES) {
        throw new Error(`Each attachment must be ${MAX_ATTACHMENT_BYTES / (1024 * 1024)} MB or smaller.`);
    }

    return {
        type: "image",
        source: {
            type: "base64",
            media_type: attachment.mediaType,
            data
        }
    };
}

function buildApprovedInput(request, command) {
    if (request.toolName !== "AskUserQuestion") {
        return isObject(command.updatedInput) ? command.updatedInput : request.input;
    }

    if (isObject(command.updatedInput)) {
        return command.updatedInput;
    }

    return {
        questions: request.input.questions,
        answers: isObject(command.answers) ? command.answers : {},
        ...(typeof command.response === "string" && command.response.trim() ? { response: command.response.trim() } : {})
    };
}

function serializeEvent(message) {
    return {
        kind: message.type === "system" ? message.subtype : message.type,
        payload: sanitizeForEvent(message)
    };
}

async function resolveDirectory(value, name) {
    if (typeof value !== "string" || !value.trim()) {
        throw new Error(`${name} must be a non-empty absolute path.`);
    }

    const resolved = await realpath(value);
    const info = await stat(resolved);
    if (!info.isDirectory()) {
        throw new Error(`${name} must point to a directory.`);
    }

    return resolved;
}

async function resolveExecutable(value) {
    const resolved = await resolveFile(value, "executablePath");
    await access(resolved, constants.X_OK);
    return resolved;
}

async function resolveFile(value, name) {
    if (typeof value !== "string" || !value.trim()) {
        throw new Error(`${name} must be a non-empty path.`);
    }

    const resolved = await realpath(value);
    const info = await stat(resolved);
    if (!info.isFile()) {
        throw new Error(`${name} must point to a file.`);
    }

    return resolved;
}

function normalizePermissionMode(value) {
    const mode = typeof value === "string" && value.trim() ? value.trim() : "default";
    if (!PERMISSION_MODES.has(mode)) {
        throw new Error(`Unsupported permission mode: ${mode}`);
    }

    return mode;
}

function getOpenSession(sessionId) {
    const session = sessions.get(requireSessionId(sessionId));
    if (!session || session.closed) {
        throw new Error(`No active agent session '${sessionId}' exists.`);
    }

    return session;
}

function requireSessionId(value) {
    if (typeof value !== "string" || !/^[A-Za-z0-9_-]{1,128}$/.test(value)) {
        throw new Error("sessionId must contain 1 to 128 letters, digits, underscores, or hyphens.");
    }

    return value;
}

function sanitizeForEvent(value, depth = 0) {
    if (depth > 8) {
        return "[truncated depth]";
    }

    if (typeof value === "string") {
        return value.length <= 8_000 ? value : `${value.slice(0, 7_997)}...`;
    }

    if (typeof value === "bigint") {
        return value.toString();
    }

    if (Array.isArray(value)) {
        return value.slice(0, 100).map((item) => sanitizeForEvent(item, depth + 1));
    }

    if (isObject(value)) {
        return Object.fromEntries(
            Object.entries(value).slice(0, 100).map(([key, item]) => [key, sanitizeForEvent(item, depth + 1)])
        );
    }

    return value;
}

function emit(value) {
    process.stdout.write(`${JSON.stringify(value)}\n`);
}

function errorMessage(error) {
    return error instanceof Error ? error.message : String(error);
}

function isObject(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}

function tryGetCommandId(line) {
    try {
        return JSON.parse(line).commandId;
    } catch {
        return undefined;
    }
}

process.once("SIGTERM", () => void closeAllSessions("sigterm"));
process.once("SIGINT", () => void closeAllSessions("sigint"));

void main();
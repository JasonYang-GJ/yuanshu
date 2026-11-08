import { act, fireEvent, render, renderHook, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "../App";
import { actionAvailable, decodeAssistantSnapshot, type AssistantSnapshot } from "./protocol";
import { useAssistantBridge } from "./useAssistantBridge";
import { RESULT_HOLD_MS } from "./useAssistantSurface";
import type { AssistantSurfaceState } from "./useAssistantInteraction";
import type { AssistantPlacement } from "./useAssistantPlacement";
import { createIslandCoordinator } from "../coordinator";
import { isVisualConsentActuallyVisible } from "./visibleConsent";

const native = vi.hoisted(() => ({ invoke: vi.fn(), focus: undefined as undefined | ((event: { payload: boolean }) => void), reset: undefined as undefined | ((event: { payload: string }) => void), surface: undefined as undefined | ((event: { payload: unknown }) => void), placement: undefined as undefined | ((event: { payload: unknown }) => void) }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: native.invoke }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({
  onFocusChanged: (callback: typeof native.focus) => { native.focus = callback; return Promise.resolve(() => {}); },
  listen: (name: string, callback: typeof native.reset) => {
    if (name === "island-mode-reset") native.reset = callback;
    if (name === "assistant-surface-changed") native.surface = callback as typeof native.surface;
    if (name === "assistant-placement-changed") native.placement = callback as typeof native.placement;
    return Promise.resolve(() => {});
  },
}) }));
const firstId = "11111111-1111-4111-8111-111111111111";
const secondId = "22222222-2222-4222-8222-222222222222";
const consentCases = [
  ["需要这次视觉搜索许可", "允许请说“允许这次视觉搜索”；拒绝请说“拒绝这次视觉搜索”。"],
  ["需要只读结果许可", "请保持软件窗口不动。允许请说“允许读取这个临时窗口”；拒绝请说“拒绝读取这个临时窗口”。"],
  ["需要本次软件任务许可", "本次读取合成窗口文字并交给选定模型完成任务。允许请说“允许这次任务”；拒绝请说“拒绝这次任务”。"],
  ["需要确认当前这一步", "本步将把合成输入框的旧文字替换为指定内容。允许请说“确认这一步”；拒绝请说“拒绝这一步”。"],
  ["需要手动确认", "语音确认已停用。请点击“查看确认”，在管理窗口核对完整内容后手动确认。"],
] as const;
function fixture(overrides: Partial<AssistantSnapshot> = {}): AssistantSnapshot {
  return { protocolVersion: 1, instanceId: firstId, revision: 1, controlToken: secondId, phase: "listening", status: "正在聆听", transcript: "", detail: "", microphoneOn: true, canStart: false, canPause: true, canStop: false, needsAttention: false, updatedAtUtc: "2026-10-11T02:00:00Z", ...overrides };
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
let current: unknown;
let surface: AssistantSurfaceState;
let placement: AssistantPlacement;
async function controls(interactive: boolean, shortcutAvailable = true) {
  surface = { interactive, shortcutAvailable, revision: surface.revision + 1 };
  act(() => { native.surface?.({ payload: surface }); if (interactive) native.reset?.({ payload: "expanded" }); });
  await flush();
}
function response(snapshot: unknown = current) { return { ok: true, error: null, snapshot }; }
async function flush() { await act(async () => { await Promise.resolve(); await Promise.resolve(); }); }
async function advance(ms: number) { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); }
function calls(name: string) { return native.invoke.mock.calls.filter(([command]) => command === name); }
beforeEach(() => {
  vi.useFakeTimers(); current = fixture();
  surface = { interactive: false, shortcutAvailable: true, revision: 1 };
  placement = { mode: "compact", revision: 1, edge: null, dragging: false, error: null };
  native.surface = undefined;
  native.invoke.mockReset();
  native.invoke.mockImplementation(async (command: string) => {
    if (command === "assistant_read") return response();
    if (command === "assistant_presented" || command === "assistant_command") return response();
    if (command === "assistant_heartbeat") return response(null);
    if (command === "assistant_surface_state") return surface;
    if (command === "assistant_placement_state") return placement;
    if (command === "assistant_release_controls") return surface = { ...surface, interactive: false, revision: surface.revision + 1 };
    if (command === "get_startup_low_presence" || command === "get_fullscreen_presence") return false;
    if (command === "get_yuanshu_core_bridge_state") return { kind: "clear" };
    return undefined;
  });
});
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

describe("assistant projection protocol", () => {
  it("does not send a visibility heartbeat before the first presented handshake", async () => {
    renderHook(useAssistantBridge); await flush(); await advance(3500);
    expect(calls("assistant_read").length).toBeGreaterThan(10);
    expect(calls("assistant_heartbeat")).toHaveLength(0);
  });

  it("rejects unknown phases, mismatched types, oversized/private extras and malformed identity", () => {
    expect(decodeAssistantSnapshot(fixture())).not.toBeNull();
    expect(decodeAssistantSnapshot(fixture({ updatedAtUtc: "2026-10-11T02:00:00.1234567+00:00" }))).not.toBeNull();
    for (const change of [{ phase: "execute-anything" }, { revision: -1 }, { revision: 1.5 }, { microphoneOn: "true" }, { instanceId: "other" }, { transcript: "字".repeat(2001) }, { detail: "字".repeat(4001) }, { secret: "unexpected" }, { updatedAtUtc: "today" }]) {
      expect(decodeAssistantSnapshot({ ...fixture(), ...change })).toBeNull();
    }
    expect(actionAvailable(fixture(), "start")).toBe(false);
    expect(actionAvailable(fixture(), "stop")).toBe(false);
    expect(actionAvailable(fixture(), "pause")).toBe(true);
    expect(actionAvailable(fixture(), "confirm" as never)).toBe(false);
  });

  it("never overlaps reads, clears text when a read stalls and discards the late result", async () => {
    const { result } = renderHook(useAssistantBridge);
    await flush(); expect(result.current.snapshot).not.toBeNull();
    const held = deferred<unknown>();
    native.invoke.mockImplementation((name: string) => name === "assistant_read" ? held.promise : Promise.resolve(response()));
    await advance(200); await advance(1700);
    expect(calls("assistant_read")).toHaveLength(2);
    expect(result.current.snapshot).toBeNull();
    await act(async () => { held.resolve(response(fixture({ revision: 20, transcript: "late" }))); });
    expect(result.current.snapshot).toBeNull();
  });

  it.each([
    fixture({ instanceId: secondId, revision: 100 }), fixture({ revision: 0 }), fixture({ transcript: "changed-without-revision" }),
    { ...fixture({ revision: 2 }), phase: "unknown" }, null,
  ])("clears private display and rejects stale or invalid projection %#", async (invalid) => {
    current = fixture({ transcript: "本机语音" });
    const { result } = renderHook(useAssistantBridge); await flush();
    expect(result.current.snapshot?.transcript).toBe("本机语音");
    current = invalid; await advance(200);
    expect(result.current.snapshot).toBeNull(); expect(result.current.integrated).toBe(true);
    await act(async () => { await result.current.command("management"); });
    expect(calls("assistant_command")).toHaveLength(0);

  });

  it("does not retry a command or double-submit while its receipt is delayed", async () => {
    const { result } = renderHook(useAssistantBridge); await flush();
    const held = deferred<unknown>();
    native.invoke.mockImplementation((name: string) => name === "assistant_command" ? held.promise : Promise.resolve(response()));
    let pending!: Promise<void>;
    await act(async () => { pending = result.current.command("pause"); void result.current.command("pause"); });
    await advance(1000);
    expect(calls("assistant_read")).toHaveLength(1);
    expect(result.current.snapshot?.microphoneOn).toBe(true);
    expect(calls("assistant_command")).toEqual([["assistant_command", { instanceId: firstId, controlToken: secondId, action: "pause" }]]);
    await act(async () => { held.resolve({ ok: false, error: "stale_control", snapshot: null }); await pending; });
    expect(result.current.commandError).toBeTruthy();
    expect(result.current.snapshot?.microphoneOn).toBe(true);
    await advance(500); expect(calls("assistant_command")).toHaveLength(1);
  });

  it("serializes control behind the current read and rejects a changed token without replay", async () => {
    const { result } = renderHook(useAssistantBridge); await flush();
    const held = deferred<unknown>();
    native.invoke.mockImplementation((name: string) => name === "assistant_read" ? held.promise : Promise.resolve(response()));
    await advance(200);
    let pending!: Promise<void>;
    await act(async () => { pending = result.current.command("pause"); });
    expect(calls("assistant_command")).toHaveLength(0);
    await act(async () => { held.resolve(response(fixture({ revision: 2, controlToken: firstId }))); await pending; });
    expect(calls("assistant_command")).toHaveLength(0);
    expect(result.current.snapshot?.revision).toBe(2);
    expect(result.current.commandBusy).toBe(false);
  });

  it("holds reads while a rendered receipt is in flight without treating busy as disconnected", async () => {
    const { result } = renderHook(useAssistantBridge); await flush();
    const held = deferred<unknown>();
    native.invoke.mockImplementation((name: string) => name === "assistant_presented" ? held.promise : Promise.resolve(response()));
    let pending!: Promise<void>;
    await act(async () => { pending = result.current.presented(result.current.snapshot!); });
    await advance(1000);
    expect(calls("assistant_read")).toHaveLength(1);
    expect(result.current.snapshot).not.toBeNull();
    await act(async () => { held.resolve(response()); await pending; });
    await advance(200); expect(calls("assistant_read")).toHaveLength(2);
  });
});

describe("assistant surface", () => {
  it("keeps native visibility heartbeats while a new clipped permission stays unacknowledged", async () => {
    render(<App />); await flush(); await advance(50);
    expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 1 }]);
    current = fixture({ revision: 2, phase: "waiting", status: "需要确认当前这一步",
      detail: "需完整核对的覆盖内容。".repeat(80), needsAttention: true });
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute("data-assistant-detail") ? new DOMRect(25, 100, 420, 900)
        : new DOMRect(20, 60, 440, 220);
    });
    await advance(4000);
    expect(calls("assistant_heartbeat").length).toBeGreaterThan(10);
    expect(calls("assistant_heartbeat").every(([, args]) =>
      JSON.stringify(args) === JSON.stringify({ instanceId: firstId }))).toBe(true);
    expect(calls("assistant_presented").filter(([, args]) => args.revision === 2)).toHaveLength(0);
    expect(screen.getByText((current as AssistantSnapshot).detail)).toBeInTheDocument();
    expect(calls("assistant_command")).toHaveLength(0);

    current = fixture({ revision: 3, phase: "waiting", status: "需要手动确认", needsAttention: true,
      detail: "语音确认已停用。请点击查看确认，在管理窗口核对完整内容后手动确认。" });
    vi.mocked(HTMLElement.prototype.getBoundingClientRect).mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute("data-assistant-detail")
        ? new DOMRect(25, 100, 420, this.textContent?.startsWith("需完整核对") ? 900 : 150)
        : new DOMRect(20, 60, 440, 220);
    });
    await advance(200); await advance(50);
    expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 3 }]);
    expect(calls("assistant_presented").filter(([, args]) => args.revision === 2)).toHaveLength(0);
    expect(screen.getByText((current as AssistantSnapshot).detail)).toBeInTheDocument();
    expect(calls("assistant_command")).toHaveLength(0);
  });

  it("clears the projection on a rejected heartbeat instead of retrying authorization", async () => {
    const { result } = renderHook(useAssistantBridge); await flush();
    await act(async () => { await result.current.presented(result.current.snapshot!); });
    native.invoke.mockImplementation(async (command: string) => command === "assistant_heartbeat"
      ? { ok: false, error: "instance_mismatch", snapshot: null } : response());
    await advance(200);
    expect(calls("assistant_heartbeat")).toHaveLength(1);
    expect(result.current.snapshot).toBeNull();
    expect(calls("assistant_command")).toHaveLength(0);
  });

  it.each(consentCases)("shows the private %s in full before acknowledging, without public bridge instructions", async (status, phrase) => {
    const coordinator = createIslandCoordinator();
    const detail = "仅在本机查看合成应用窗口，不打开结果或发送消息。\n目标窗口：合成窗口\n" + phrase;
    current = fixture({ status, phase: "waiting", detail, transcript: "在合成应用搜索示例", needsAttention: true });
    const bounds = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute("data-assistant-detail") ? new DOMRect(25, 100, 420, 150)
        : new DOMRect(20, 60, 440, 220);
    });
    const mounted = render(<App coordinator={coordinator} />); await flush();
    act(() => { coordinator.dispatch({ type: "upsert", presentation: {
      source: "yuanshu.core", key: "test-wait", kind: "WaitingUser", title: "需要选择窗口", subtitle: "请返回元枢主窗口确认。",
    } }); });
    expect(screen.getByText(detail, { normalizer: (text) => text })).toBeVisible();
    expect(screen.getAllByText(detail, { normalizer: (text) => text })).toHaveLength(1);
    expect(document.querySelectorAll("[data-assistant-detail]")).toHaveLength(1);
    expect(screen.queryByText("请返回元枢主窗口确认。")).not.toBeInTheDocument();
    expect(screen.queryByText("在合成应用搜索示例")).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "媒体与桌面状态" })).not.toBeInTheDocument();
    expect(calls("assistant_presented")).toHaveLength(0);
    await advance(50);
    expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 1 }]);
    await controls(true);
    expect(screen.getByText(detail, { normalizer: (text) => text })).toBeVisible();
    expect(screen.getAllByText(detail, { normalizer: (text) => text })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "任务与设置" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "停止" })).toBeDisabled();
    expect(calls("assistant_command")).toHaveLength(0);
    mounted.unmount(); coordinator.destroy(); bounds.mockRestore();
  });

  it.each(consentCases)("does not acknowledge absent, clipped or hidden %s permission", async (status, phrase) => {
    current = fixture({ status, phase: "waiting", detail: "合成窗口许可。" + phrase,
      needsAttention: true });
    expect(isVisualConsentActuallyVisible(current as AssistantSnapshot)).toBe(false);
    let clipped = true;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute("data-assistant-detail") ? new DOMRect(25, 100, 420, clipped ? 300 : 150)
        : new DOMRect(20, 60, 440, 220);
    });
    render(<App />); await flush(); await advance(100);
    expect(screen.getByText((current as AssistantSnapshot).detail)).toBeInTheDocument();
    expect(calls("assistant_presented")).toHaveLength(0);
    clipped = false; await advance(200); await advance(50);
    expect(calls("assistant_presented")).toHaveLength(1);
    const exact = current as AssistantSnapshot;
    expect(isVisualConsentActuallyVisible(exact)).toBe(true);
    expect(isVisualConsentActuallyVisible({ ...exact, instanceId: secondId })).toBe(false);
    expect(isVisualConsentActuallyVisible({ ...exact, revision: 2 })).toBe(false);
    expect(isVisualConsentActuallyVisible({ ...exact, detail: exact.detail + " altered" })).toBe(false);
    screen.getByRole("region", { name: "元枢语音助手" }).setAttribute("aria-hidden", "true");
    expect(isVisualConsentActuallyVisible(exact)).toBe(false);
    const beforeHidden = calls("assistant_presented").length;
    await advance(200); await advance(50);
    expect(calls("assistant_presented")).toHaveLength(beforeHidden);
  });

  it("shows retained media and transient volume without replacing microphone state or assistant controls", async () => {
    const coordinator = createIslandCoordinator();
    const mounted = render(<App coordinator={coordinator} />); await flush();
    act(() => { coordinator.dispatch({ type: "upsert", presentation: {
      source: "windows.media", key: "current-session", kind: "Media", title: "测试曲目", subtitle: "测试歌手", metadata: { playbackState: "playing" },
    } }); });
    expect(screen.getByRole("button", { name: /打开元枢操作面板，麦克风开启/ })).toHaveTextContent("媒体 · 测试曲目");
    await controls(true);
    expect(screen.getByRole("region", { name: "媒体与桌面状态" })).toHaveTextContent("测试曲目 · 测试歌手");
    expect(screen.getByRole("button", { name: "暂停语音" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "任务与设置" }));
    expect(calls("open_island_panel")).toHaveLength(1);
    act(() => { coordinator.dispatch({ type: "upsert", presentation: {
      source: "windows.volume", key: "default-playback", kind: "SystemEvent", category: "volume", title: "音量", metadata: { volume: 47, muted: false }, dismiss: { mode: "auto", durationMs: 2000 },
    } }); });
    expect(screen.getByRole("progressbar", { name: "系统音量" })).toHaveAttribute("value", "47");
    expect(screen.getByRole("region", { name: "媒体与桌面状态" })).toHaveTextContent("47%");
    await advance(2100);
    expect(screen.queryByRole("progressbar", { name: "系统音量" })).not.toBeInTheDocument();
    expect(screen.getByRole("region", { name: "媒体与桌面状态" })).toHaveTextContent("测试曲目");
    expect(calls("assistant_command")).toHaveLength(0);
    mounted.unmount(); coordinator.destroy();
  });

  it("keeps a listening microphone compact without orb or tuck commands, and opens native-authorized controls", async () => {
    render(<App />); await flush(); await advance(10_000);
    const root = screen.getByRole("main"); expect(root).toHaveAttribute("data-mode", "compact");
    expect(calls("set_island_mode").some(([, args]) => ["orb", "tucked"].includes(args.mode))).toBe(false);
    expect(screen.getAllByRole("button")).toHaveLength(1);
    expect(screen.getByRole("button", { name: /打开元枢操作面板，麦克风开启/ })).toHaveTextContent("拖动移动");
    await controls(true);
    expect(root).toHaveAttribute("data-mode", "expanded");
    act(() => { native.focus?.({ payload: false }); });
    await advance(10_000); expect(root).toHaveAttribute("data-mode", "expanded");
    expect(screen.getByRole("button", { name: "暂停语音" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "停止" })).toBeDisabled();
  });

  it("shows partial then final text before acknowledging each rendered version, then execution", async () => {
    render(<App />); await flush(); await advance(50);
    current = fixture({ phase: "transcribing", revision: 2, transcript: "打开我的" });
    await advance(160);
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    expect(screen.getByText("打开我的")).toBeVisible();
    await advance(50); expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 2 }]);
    current = fixture({ phase: "recognized", revision: 3, transcript: "打开我的网易云", status: "已听清，准备执行" });
    await advance(150);
    expect(screen.getByText("打开我的网易云")).toBeVisible();
    expect(within(screen.getByRole("region", { name: "元枢语音助手" })).getByText("已听清，准备执行")).toBeVisible();
    const before = calls("assistant_presented").filter(([, args]) => args.revision === 3);
    expect(before).toHaveLength(0);
    await advance(50); expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 3 }]);
    current = fixture({ phase: "working", revision: 4, transcript: "打开我的网易云", status: "正在打开网易云音乐", canStop: true });
    await advance(200);
    expect(within(screen.getByRole("region", { name: "元枢语音助手" })).getByText("正在打开网易云音乐")).toBeVisible();
    expect(screen.queryByRole("button", { name: "停止" })).toBeNull();
    expect(screen.getByText("鼠标可直接操作下方页面")).toBeVisible();
    expect(calls("assistant_command")).toHaveLength(0);
  });

  it("renews presented heartbeat even when revision is unchanged", async () => {
    render(<App />); await flush();
    for (let i = 0; i < 6; i++) { await advance(200); await advance(50); }
    expect(calls("assistant_presented").length).toBeGreaterThanOrEqual(5);
    expect(calls("assistant_presented").every(([, args]) => args.instanceId === firstId && args.revision === 1)).toBe(true);
  });

  it("waits for actual native resize completion and two frames before acknowledging final text", async () => {
    const resized = deferred<unknown>();
    native.invoke.mockImplementation(async (name: string, args?: { mode?: string }) => {
      if (name === "set_island_mode" && args?.mode === "expanded") return resized.promise;
      if (name.startsWith("assistant_")) return response();
      return false;
    });
    current = fixture({ phase: "recognized", transcript: "请打开微信" });
    render(<App />); await flush(); await advance(100);
    expect(screen.getByText("请打开微信")).toBeVisible();
    expect(calls("assistant_presented")).toHaveLength(0);
    await act(async () => { resized.resolve(undefined); });
    await advance(16); expect(calls("assistant_presented")).toHaveLength(0);
    await advance(32); expect(calls("assistant_presented")).toHaveLength(1);
  });

  it("expands even a very short completed action and keeps it briefly before compact", async () => {
    render(<App />); await flush();
    current = fixture({ revision: 2, phase: "success", status: "已打开", detail: "网易云音乐已打开。" });
    await advance(200); expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    expect(screen.getByText("网易云音乐已打开。")).toBeVisible();
    await advance(RESULT_HOLD_MS - 100); expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    await advance(150); expect(screen.getByRole("main")).toHaveAttribute("data-mode", "compact");
  });

  it("holds explicitly opened controls until release even across spoken requests and short results", async () => {
    current = fixture({ phase: "paused", status: "语音已暂停", microphoneOn: false, canStart: true, canPause: false });
    render(<App />); await flush();
    await controls(true);
    fireEvent.click(screen.getByRole("button", { name: "开始语音" })); await flush();
    current = fixture({ phase: "transcribing", revision: 2, transcript: "打开微信" });
    await advance(200);
    current = fixture({ phase: "success", revision: 3, status: "已打开微信" });
    await advance(200); await advance(RESULT_HOLD_MS + 100);
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    fireEvent.click(screen.getByRole("button", { name: "收起操作面板" })); await flush();
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "compact");
    expect(calls("assistant_release_controls")).toHaveLength(1);
    expect(calls("assistant_command")).toHaveLength(1);
  });

  it("shows standby mic truth, pause/start controls and only opens management for confirmation", async () => {
    current = fixture({ phase: "standby", status: "正在待命" });
    render(<App />); await flush();
    await controls(true);
    expect(screen.getByText("麦克风开启 · 仅等待唤醒")).toBeVisible();
    current = fixture({ phase: "paused", revision: 2, status: "语音已暂停", microphoneOn: false, canStart: true, canPause: false });
    await advance(200); expect(screen.getByText("麦克风已关闭")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "开始语音" })); await flush();
    expect(calls("assistant_command").at(-1)?.[1].action).toBe("start");
    current = fixture({ phase: "waiting", revision: 3, status: "需要你确认", detail: "请确认要打开的文件。", needsAttention: true });
    await advance(200);
    fireEvent.click(screen.getByRole("button", { name: "查看确认" })); await flush();
    expect(calls("assistant_command").at(-1)?.[1].action).toBe("management");
    expect(calls("assistant_command").some(([, args]) => args.action === "confirm")).toBe(false);
    for (const [label, action] of [["记忆", "memory"], ["退出", "exit"]]) {
      fireEvent.click(screen.getByRole("button", { name: label })); await flush();
      expect(calls("assistant_command").at(-1)?.[1].action).toBe(action);
    }
    fireEvent.keyDown(window, { key: "Escape" }); await flush();
    expect(calls("assistant_release_controls")).toHaveLength(1);
    expect(calls("assistant_command").some(([, args]) => args.action === "pause")).toBe(false);
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByText("请确认要打开的文件。")).toBeVisible();
  });

  it("clears transcript immediately on disconnection and never renders text as HTML", async () => {
    current = fixture({ phase: "recognized", transcript: "<img src=x onerror=alert(1)>" });
    render(<App />); await flush();
    await controls(true);
    const details = screen.getByRole("region", { name: "元枢语音助手" });
    expect(within(details).getByText("<img src=x onerror=alert(1)>")).toBeVisible();
    expect(details.querySelector("img")).toBeNull();
    native.invoke.mockImplementation(async (name: string) => name === "assistant_read" ? { ok: false, error: "pipe_unavailable", snapshot: null } : response());
    await advance(200);
    expect(screen.queryByText("<img src=x onerror=alert(1)>")).toBeNull();
    expect(screen.getByText("语音连接已中断")).toBeVisible();
    expect(screen.getByRole("button", { name: "开始语音" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "管理" })).toBeDisabled();
  });

  it("shows a usable tray fallback when the shortcut is occupied and never changes voice on Escape", async () => {
    surface = { interactive: false, shortcutAvailable: false, revision: 1 };
    current = fixture({ phase: "paused", microphoneOn: false, canPause: false, canStart: true });
    render(<App />); await flush();
    expect(screen.getByRole("button", { name: /打开元枢操作面板/ })).toHaveTextContent("拖动移动");
    fireEvent.keyDown(window, { key: "Escape" }); await flush();
    expect(calls("assistant_release_controls")).toHaveLength(0);
    await controls(true, false);
    fireEvent.keyDown(window, { key: "Escape" }); await flush();
    expect(calls("assistant_release_controls")).toHaveLength(1);
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "compact");
    expect(calls("assistant_command")).toHaveLength(0);
  });

  it("keeps manual controls expanded through paused, slow starting and listening", async () => {
    current = fixture({ phase: "paused", microphoneOn: false, canPause: false, canStart: true });
    render(<App />); await flush(); await controls(true);
    fireEvent.click(screen.getByRole("button", { name: "开始语音" })); await flush();
    current = fixture({ phase: "idle", revision: 2, status: "正在开启语音", microphoneOn: false });
    await advance(3500);
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    current = fixture({ phase: "listening", revision: 3 }); await advance(300);
    expect(screen.getByRole("button", { name: "收起操作面板" })).toBeVisible();
    expect(calls("set_island_mode").some(([, args]) => args.mode === "compact")).toBe(false);
  });

  it("projects an edge orb without a native orb command and retains microphone truth", async () => {
    placement = { ...placement, mode: "orb", edge: "left" };
    render(<App />); await flush();
    expect(document.querySelector(".assistant-island")).toHaveAttribute("data-resting-mode", "orb");
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "compact");
    expect(screen.getByRole("button", { name: /麦克风开启/ })).toHaveAttribute("title", expect.stringContaining("按住拖动"));
    fireEvent.click(screen.getByRole("button", { name: /打开元枢操作面板/ })); await flush();
    expect(calls("assistant_open_controls")).toHaveLength(1);
    expect(calls("set_island_mode").some(([, args]) => args.mode === "orb")).toBe(false);
  });

  it("keeps old rendered heartbeats during a long drag and does not acknowledge final text until release and resize", async () => {
    render(<App />); await flush(); await advance(50);
    act(() => { native.placement?.({ payload: { ...placement, revision: 2, dragging: true } }); });
    current = fixture({ phase: "recognized", revision: 2, transcript: "松手后显示这条新指令" });
    for (let i = 0; i < 16; i++) { await advance(200); await advance(50); }
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "compact");
    expect(screen.queryByText("松手后显示这条新指令")).toBeNull();
    expect(calls("assistant_presented").length).toBeGreaterThan(10);
    expect(calls("assistant_presented").every(([, args]) => args.revision === 1)).toBe(true);
    act(() => { native.placement?.({ payload: { ...placement, revision: 3, dragging: false } }); });
    await advance(200); await advance(50);
    expect(screen.getByText("松手后显示这条新指令")).toBeVisible();
    expect(screen.getByRole("main")).toHaveAttribute("data-mode", "expanded");
    expect(calls("assistant_presented")).toContainEqual(["assistant_presented", { instanceId: firstId, revision: 2 }]);
  });
});

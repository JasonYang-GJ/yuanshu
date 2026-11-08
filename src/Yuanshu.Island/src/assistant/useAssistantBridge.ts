import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { actionAvailable, decodeAssistantResponse, type AssistantAction, type AssistantSnapshot } from "./protocol";

export const ASSISTANT_POLL_MS = 200;
const READ_DEADLINE_MS = 1500;
export function useAssistantBridge(deferProjection = false) {
  const [snapshot, setSnapshot] = useState<AssistantSnapshot | null>(null);
  const [integrated, setIntegrated] = useState(false);
  const [poll, setPoll] = useState(0);
  const [commandBusy, setCommandBusy] = useState(false);
  const [commandError, setCommandError] = useState<string | null>(null);
  const latest = useRef<AssistantSnapshot | null>(null);
  const identity = useRef<string | null>(null);
  const highest = useRef<AssistantSnapshot | null>(null);
  const alive = useRef(false);
  const commanding = useRef(false);
  const acknowledging = useRef(false);
  const hasPresented = useRef(false);
  const nativeRequest = useRef<Promise<unknown> | null>(null);
  const deferredProjection = useRef(deferProjection);
  deferredProjection.current = deferProjection;
  const invokeNative = useCallback((command: string, args?: Record<string, unknown>) => {
    const request = invoke<unknown>(command, args);
    nativeRequest.current = request;
    void request.finally(() => { if (nativeRequest.current === request) nativeRequest.current = null; }).catch(() => {});
    return request;
  }, []);
  const fail = useCallback(() => {
    if (!alive.current) return;
    latest.current = null;
    hasPresented.current = false;
    setSnapshot(null);
    setIntegrated(true);
  }, []);

  useEffect(() => {
    alive.current = true;
    let disposed = false;
    let standalone = false;
    let timer: number | undefined;
    const read = async () => {
      if (nativeRequest.current || commanding.current) {
        if (!disposed) timer = window.setTimeout(() => { void read(); }, ASSISTANT_POLL_MS);
        return;
      }
      let expired = false;
      const deadline = window.setTimeout(() => { expired = true; if (!disposed) fail(); }, READ_DEADLINE_MS);
      try {
        const raw: unknown = await invokeNative("assistant_read");
        if (disposed || expired) return;
        if (raw === null && identity.current === null) { standalone = true; setIntegrated(false); return; }
        const response = decodeAssistantResponse(raw);
        const next = response?.ok ? response.snapshot : null;
        if (!next || (identity.current !== null && next.instanceId !== identity.current) ||
            (highest.current !== null && (next.revision < highest.current.revision ||
              (next.revision === highest.current.revision && JSON.stringify(next) !== JSON.stringify(highest.current))))) {
          fail(); return;
        }
        identity.current = next.instanceId;
        highest.current = next;
        setIntegrated(true);
        // Keep renewing the already displayed revision while native dragging
        // freezes the window. Never acknowledge new final text before it fits.
        if (!deferredProjection.current || latest.current === null) {
          latest.current = next;
          setSnapshot(next);
        }
        // Native window visibility is a liveness signal, not proof that this
        // revision's complete consent text was rendered. Never bootstrap the
        // initial presentation handshake with a heartbeat.
        if (hasPresented.current) {
          const heartbeat = decodeAssistantResponse(await invokeNative("assistant_heartbeat", { instanceId: next.instanceId }));
          if (disposed || expired) return;
          if (!heartbeat?.ok || heartbeat.snapshot !== null) { fail(); return; }
        }
        setPoll((value) => value + 1);
      } catch { if (!disposed) fail(); }
      finally {
        window.clearTimeout(deadline);
        // A timed-out native call is never overlapped by another invocation.
        if (!disposed && !standalone) timer = window.setTimeout(() => { void read(); }, ASSISTANT_POLL_MS);
      }
    };
    void read();
    return () => { disposed = true; alive.current = false; window.clearTimeout(timer); };
  }, [fail, invokeNative]);

  const presented = useCallback(async (target: AssistantSnapshot) => {
    if (!alive.current || acknowledging.current || nativeRequest.current || commanding.current || latest.current?.instanceId !== target.instanceId || latest.current.revision !== target.revision) return;
    acknowledging.current = true;
    try {
      const response = decodeAssistantResponse(await invokeNative("assistant_presented", { instanceId: target.instanceId, revision: target.revision }));
      // The reply is only a receipt. New state always comes through the ordered read loop.
      if ((!response?.ok || (response.snapshot && response.snapshot.instanceId !== target.instanceId)) && latest.current?.instanceId === target.instanceId && latest.current.revision === target.revision) fail();
      else if (response?.ok) hasPresented.current = true;
    } catch {
      if (latest.current?.instanceId === target.instanceId && latest.current.revision === target.revision) fail();
    } finally { acknowledging.current = false; }
  }, [fail, invokeNative]);

  const command = useCallback(async (action: AssistantAction) => {
    const target = latest.current;
    if (!alive.current || !target || commanding.current || !actionAvailable(target, action)) return;
    commanding.current = true;
    setCommandBusy(true);
    setCommandError(null);
    try {
      if (nativeRequest.current) await nativeRequest.current.catch(() => {});
      // The user action is queued only behind the current transport operation,
      // then checked again. It never migrates to another control generation.
      if (!alive.current || latest.current?.instanceId !== target.instanceId || latest.current.controlToken !== target.controlToken || !actionAvailable(latest.current, action)) return;
      const response = decodeAssistantResponse(await invokeNative("assistant_command", { instanceId: target.instanceId, controlToken: target.controlToken, action }));
      if (!alive.current || latest.current?.instanceId !== target.instanceId || latest.current.controlToken !== target.controlToken) return;
      if (!response?.ok || (response.snapshot && response.snapshot.instanceId !== target.instanceId)) setCommandError("操作未完成，请查看当前状态后重试。");
    } catch {
      if (alive.current) { fail(); setCommandError("连接已中断，请回到元枢恢复连接。"); }
    } finally {
      commanding.current = false;
      if (alive.current) setCommandBusy(false);
    }
  }, [fail, invokeNative]);
  return { integrated, snapshot, poll, presented, command, commandBusy, commandError };
}

import { useEffect, useRef } from "react";
import { needsExpandedSurface, type AssistantSnapshot } from "./protocol";
import type { IslandMode } from "../hooks/useIslandController";
import { isVisualConsentActuallyVisible } from "./visibleConsent";

export const RESULT_HOLD_MS = 6500;
export function useAssistantSurface(integrated: boolean, snapshot: AssistantSnapshot | null, mode: IslandMode, expand: () => void, collapse: () => void, interactive = false, dragging = false) {
  const wasInteractive = useRef(false);
  const held = useRef(false);
  const phase = snapshot?.phase;
  const urgent = snapshot !== null && needsExpandedSurface(snapshot);
  useEffect(() => {
    if (!integrated || dragging) return;
    const released = wasInteractive.current && !interactive;
    wasInteractive.current = interactive;
    if (interactive) { expand(); return; }
    if (!snapshot || urgent) {
      held.current = true; expand(); return;
    }
    if (released) { held.current = false; collapse(); return; }
    if (phase === "success" || phase === "error" || held.current) {
      if (phase === "success" || phase === "error") expand();
      const timer = window.setTimeout(() => { held.current = false; collapse(); }, RESULT_HOLD_MS);
      return () => window.clearTimeout(timer);
    }
    collapse();
  }, [integrated, phase, urgent, snapshot === null, expand, collapse, interactive, dragging]);
  useEffect(() => {
    if (integrated && !dragging && (mode === "orb" || mode === "tucked")) {
      if (urgent || interactive) expand(); else collapse();
    }
  }, [integrated, mode, urgent, expand, collapse, interactive, dragging]);
  return {
    presentationReady: snapshot !== null && (!urgent || mode === "expanded"),
  };
}

export function useAssistantPresented(snapshot: AssistantSnapshot | null, poll: number, ready: boolean, presented: (snapshot: AssistantSnapshot) => Promise<void>) {
  useEffect(() => {
    if (!snapshot || !ready) return;
    let second = 0;
    let cancelled = false;
    const first = requestAnimationFrame(() => {
      second = requestAnimationFrame(() => {
        if (!cancelled && isVisualConsentActuallyVisible(snapshot)) void presented(snapshot);
      });
    });
    return () => { cancelled = true; cancelAnimationFrame(first); cancelAnimationFrame(second); };
  }, [snapshot, poll, ready, presented]);
}

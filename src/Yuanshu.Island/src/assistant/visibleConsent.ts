import type { AssistantSnapshot } from "./protocol";

// Presentation classification only. Permission still belongs to the Client/Host's
// exact nonce; this view never accepts an action or grants a window capability.
// Search and general software tasks share the full-text consent presentation gate.
export function isVisualSearchConsent(snapshot: AssistantSnapshot | null): boolean {
  return snapshot?.phase === "waiting" && snapshot.needsAttention
    && (snapshot.status === "需要这次视觉搜索许可" || snapshot.status === "需要只读结果许可"
      || snapshot.status === "需要本次软件任务许可" || snapshot.status === "需要确认当前这一步"
      || snapshot.status === "需要手动确认");
}

export function isVisualConsentActuallyVisible(snapshot: AssistantSnapshot): boolean {
  if (!isVisualSearchConsent(snapshot)) return true;
  const surface = document.querySelector<HTMLElement>(".assistant-island");
  const viewport = surface?.querySelector<HTMLElement>(".assistant-reading");
  const detail = surface?.querySelector<HTMLElement>("[data-assistant-detail]");
  if (!surface || !viewport || !detail || !snapshot.detail
    || surface.dataset.assistantInstance !== snapshot.instanceId
    || surface.dataset.assistantRevision !== String(snapshot.revision)
    || surface.dataset.state !== "expanded" || detail.textContent !== snapshot.detail
    || detail.closest('[aria-hidden="true"]')) return false;
  for (const element of [surface, viewport, detail]) {
    const style = getComputedStyle(element);
    if (style.display === "none" || style.visibility !== "visible" || style.opacity === "0") return false;
  }
  const bounds = detail.getBoundingClientRect();
  const clip = viewport.getBoundingClientRect();
  // A mounted paragraph below a scrolled/clipped viewport is not a visible
  // permission. Check the whole detail, not just its first line or DOM presence.
  return bounds.width > 0 && bounds.height > 0 && clip.width > 0 && clip.height > 0
    && bounds.top >= Math.max(0, clip.top) && bounds.left >= Math.max(0, clip.left)
    && bounds.bottom <= Math.min(window.innerHeight, clip.bottom)
    && bounds.right <= Math.min(window.innerWidth, clip.right);
}

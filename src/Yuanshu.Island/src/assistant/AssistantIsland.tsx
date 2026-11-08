import { useEffect, useLayoutEffect, useRef, type RefObject } from "react";
import { PivotGlass } from "../components/Island/Island";
import type { IslandMode } from "../hooks/useIslandController";
import { actionAvailable, type AssistantAction, type AssistantSnapshot } from "./protocol";
import type { AssistantPlacement } from "./useAssistantPlacement";
import { useAssistantDragHandle } from "./useAssistantDragHandle";
import "./assistant.css";
import type { IslandPresentation } from "../coordinator";
import { isVisualSearchConsent } from "./visibleConsent";

interface Props {
  mode: IslandMode;
  snapshot: AssistantSnapshot | null;
  busy: boolean;
  error: string | null;
  interactive: boolean;
  shortcutAvailable: boolean;
  placement: AssistantPlacement | null;
  dragging: boolean;
  onBeginDrag: () => Promise<void>;
  onOpenControls: () => Promise<void>;
  onCommand: (action: AssistantAction) => Promise<void>;
  onCollapse: () => void;
  collapseButtonRef: RefObject<HTMLButtonElement | null>;
  desktopPresentation?: IslandPresentation;
  onOpenPanel?: () => void;
}
function presentation(snapshot: AssistantSnapshot | null) {
  if (!snapshot || snapshot.phase === "error") return "Error";
  if (snapshot.phase === "waiting") return "WaitingUser";
  if (snapshot.phase === "working" || snapshot.phase === "stopping") return "Executing";
  if (snapshot.phase === "transcribing" || snapshot.phase === "listening") return "Listening";
  return "Idle";
}
export function AssistantIsland({ mode, snapshot, busy, error, interactive, shortcutAvailable, placement, dragging, onBeginDrag, onOpenControls, onCommand, onCollapse, collapseButtonRef, desktopPresentation, onOpenPanel }: Props) {
  const expanded = mode === "expanded";
  const orb = !expanded && placement?.mode === "orb";
  const compactDrag = useAssistantDragHandle(!expanded, onBeginDrag, onOpenControls);
  const headerDrag = useAssistantDragHandle(expanded && interactive, onBeginDrag, undefined, 88);
  const textRef = useRef<HTMLParagraphElement>(null);
  const readingRef = useRef<HTMLDivElement>(null);
  const visualConsent = isVisualSearchConsent(snapshot);
  const status = snapshot?.status || "与元枢的连接已中断";
  const microphone = !snapshot ? "语音连接已中断" : !snapshot.microphoneOn ? "麦克风已关闭" : snapshot.phase === "standby" ? "麦克风开启 · 仅等待唤醒" : "麦克风开启";
  const voiceAction = snapshot?.canPause ? "pause" : "start";
  const compactStatus = !snapshot ? "已断开" : !snapshot.microphoneOn ? "已暂停" : snapshot.phase === "standby" ? "待唤醒" : "聆听中";
  const entryHint = shortcutAvailable ? "Ctrl+Alt+空格 打开操作" : "从系统托盘打开操作";
  const desktop = desktopPresentation?.kind !== "Idle" ? desktopPresentation : undefined;
  const volume = desktop?.kind === "SystemEvent" && desktop.category === "volume"
    && typeof desktop.metadata?.volume === "number" ? desktop.metadata.volume : undefined;
  const desktopLabel = desktop?.kind === "Media" ? "媒体" : volume !== undefined ? "音量" : "元枢任务";
  const desktopStatus = volume !== undefined ? desktop?.metadata?.muted === true ? "已静音" : `${Math.round(volume)}%`
    : desktop?.kind === "Media" ? desktop.metadata?.playbackState === "paused" ? "已暂停" : "播放中"
    : desktop?.subtitle ?? desktop?.indicator;
  const desktopSummary = desktop ? `${desktopLabel} · ${volume !== undefined ? desktopStatus : desktop.title}` : null;
  useLayoutEffect(() => {
    if (visualConsent && readingRef.current) readingRef.current.scrollTop = 0;
  }, [visualConsent, snapshot?.instanceId, snapshot?.revision]);
  useEffect(() => {
    const node = textRef.current;
    if (node && snapshot?.phase === "transcribing") node.scrollTop = node.scrollHeight;
  }, [snapshot?.transcript, snapshot?.phase]);
  const button = (action: AssistantAction, label: string, primary = false) => <button
    type="button" className={`island-action${primary ? " island-action--primary" : ""}`}
    disabled={!snapshot || busy || !actionAvailable(snapshot, action)}
    tabIndex={expanded && interactive ? 0 : -1} onClick={() => { if (interactive) void onCommand(action); }}
  >{label}</button>;
  return <div className={`island-shell island-shell--${mode} assistant-island`} data-state={mode} data-resting-mode={orb ? "orb" : "compact"} data-edge={placement?.edge ?? "none"} data-dragging={dragging} data-interactive={interactive} data-presentation={presentation(snapshot)} data-assistant-instance={snapshot?.instanceId} data-assistant-revision={snapshot?.revision} data-visual-consent={visualConsent}>
    <button type="button" className={`island-compact glass-surface ${orb ? "assistant-orb" : "assistant-ribbon"}`} aria-label={`打开元枢操作面板，${microphone}，可拖动移动`} title={`${microphone} · 点击操作，按住拖动，靠边收成小球${error ? ` · ${error}` : ""}`} aria-expanded={expanded} aria-controls="assistant-details" aria-hidden={mode !== "compact"} tabIndex={mode === "compact" ? 0 : -1} {...compactDrag}>
      {orb ? <><PivotGlass size="medium" /><span className="assistant-orb__microphone" data-microphone={snapshot?.microphoneOn === true} aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"><rect x="6" y="2" width="4" height="7" rx="2" /><path d="M4 7v1a4 4 0 0 0 8 0V7M8 12v2M6 14h4" />{!snapshot?.microphoneOn && <path d="m2 2 12 12" />}</svg></span></> : <><span className="island-compact__brand"><PivotGlass size="small" /><span className="island-compact__name">元枢</span></span>
      <span className="assistant-ribbon__status">{compactStatus}</span>
      <span className="assistant-ribbon__shortcut" title={desktopSummary ?? undefined}>{dragging ? "松开后继续显示" : desktopSummary ?? "拖动移动"}</span></>}
    </button>
    <section id="assistant-details" className="island-details glass-surface assistant-details" aria-label="元枢语音助手" aria-hidden={!expanded}>
      <header className="island-details__header assistant-header" data-draggable={interactive} title={interactive ? "按住标题区拖动位置" : undefined} {...headerDrag}>
        <div className="island-details__identity"><PivotGlass size="medium" /><div><h1>元枢</h1><p className="assistant-microphone" data-microphone={snapshot?.microphoneOn === true}>{microphone}</p></div></div>
        {interactive ? <button ref={collapseButtonRef} type="button" className="island-action assistant-collapse" tabIndex={expanded ? 0 : -1} aria-label="收起操作面板" title="收起操作面板，语音状态保持不变" onPointerDown={(event) => event.stopPropagation()} onClick={onCollapse}>收起 · Esc</button> : <span className="assistant-display-tag">文字展示</span>}
      </header>
      <div ref={readingRef} className="assistant-reading" tabIndex={expanded && interactive ? 0 : -1} aria-label="语音文字和执行进度">
        <p className="assistant-phase" role="status" aria-live="polite">{status}</p>
        {visualConsent && snapshot && <div className="assistant-response assistant-consent"><p data-assistant-detail>{snapshot.detail}</p></div>}
        {!visualConsent && desktop && <section className="assistant-desktop-state" aria-label="媒体与桌面状态">
          <p><strong>{desktopLabel}</strong><span>{desktopStatus}</span></p>
          {volume !== undefined ? <progress aria-label="系统音量" max={100} value={volume} />
            : <p className="assistant-desktop-title">{desktop.title}{desktop.kind === "Media" && desktop.subtitle ? ` · ${desktop.subtitle}` : ""}</p>}
        </section>}
        {!visualConsent && interactive && onOpenPanel && <button className="island-action assistant-panel-entry" type="button" tabIndex={expanded ? 0 : -1} onClick={onOpenPanel}>任务与设置</button>}
        {dragging && <p className="assistant-drag-hint" role="status">正在移动，松开后继续显示</p>}
        {!visualConsent && snapshot?.transcript && <div className="assistant-transcript"><span className="assistant-label">{snapshot.phase === "transcribing" ? "语音文字 · 识别中" : "你说"}</span><p ref={textRef} className="assistant-transcript__text">{snapshot.transcript}</p></div>}
        {!visualConsent && snapshot?.detail && <div className="assistant-response"><span className="assistant-label">{snapshot.phase === "waiting" ? "需要你确认" : "元枢"}</span><p>{snapshot.detail}</p></div>}
        {snapshot && !snapshot.transcript && !snapshot.detail && <p className="assistant-hint">{snapshot.phase === "standby" ? "说“元枢”唤醒，再告诉我你想做什么。" : snapshot.microphoneOn ? "直接说出你想做的事，语音文字会显示在这里。" : interactive ? "点击“开始语音”，再直接告诉我你想做什么。" : "打开操作面板后，点击“开始语音”。"}</p>}
        {!snapshot && <p className="assistant-hint">请回到元枢的恢复窗口继续。这里不会重试执行刚才的指令。</p>}
        {error && <p className="assistant-command-error" role="alert">{error}</p>}
      </div>
      {interactive ? <footer className="assistant-controls" aria-label="助手操作">
        {button(voiceAction, voiceAction === "pause" ? "暂停语音" : "开始语音", true)}
        {button("stop", "停止")}
        {button("management", snapshot?.phase === "waiting" ? "查看确认" : "管理")}
        {button("memory", "记忆")}
        {button("exit", "退出")}
      </footer> : <footer className="assistant-display-footer"><span>鼠标可直接操作下方页面</span><span>{entryHint}</span></footer>}
    </section>
  </div>;
}

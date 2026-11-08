//! Ephemeral projection of the existing desktop assistant. No audio, persistence,
//! general RPC, credentials, or independent execution authority lives here.
use std::{
    collections::VecDeque,
    fs::OpenOptions,
    os::windows::{fs::OpenOptionsExt, io::AsRawHandle},
    sync::{Arc, Mutex},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager, State, WebviewWindow};
use windows::Win32::{
    Foundation::{CloseHandle, ERROR_IO_PENDING, HANDLE, WAIT_OBJECT_0},
    Storage::FileSystem::{ReadFile, WriteFile, FILE_FLAG_OVERLAPPED},
    System::{
        Threading::{CreateEventW, WaitForSingleObject},
        IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED},
    },
};

const PIPE_PREFIX: &str = "Yuanshu.Assistant.";
const FRAME_LIMIT: usize = 32_768;
const REQUEST_TIMEOUT: Duration = Duration::from_millis(1_500);
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AssistantSnapshot {
    protocol_version: u32,
    instance_id: String,
    revision: u64,
    control_token: String,
    phase: String,
    status: String,
    transcript: String,
    detail: String,
    microphone_on: bool,
    can_start: bool,
    can_pause: bool,
    can_stop: bool,
    needs_attention: bool,
    updated_at_utc: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AssistantResponse {
    ok: bool,
    // deserialize_with also makes these nullable fields required on the wire.
    #[serde(deserialize_with = "Option::deserialize")]
    error: Option<String>,
    #[serde(deserialize_with = "Option::deserialize")]
    snapshot: Option<AssistantSnapshot>,
}

impl AssistantResponse {
    fn failure(error: &'static str) -> Self {
        Self {
            ok: false,
            error: Some(error.into()),
            snapshot: None,
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AssistantRequest<'a> {
    protocol_version: u32,
    operation: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    instance_id: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    revision: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    visible: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    control_token: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    action: Option<&'a str>,
}

#[derive(Default)]
struct ProjectionIdentity {
    latest: Option<AssistantSnapshot>,
    observed_revisions: VecDeque<u64>,
}

impl ProjectionIdentity {
    fn accept(&mut self, snapshot: &AssistantSnapshot) -> Result<(), &'static str> {
        if let Some(current) = &self.latest {
            if current.instance_id != snapshot.instance_id {
                return Err("instance_changed");
            }
            if snapshot.revision < current.revision
                || (snapshot.revision == current.revision && snapshot != current)
            {
                return Err("stale_snapshot");
            }
        }
        if !self.observed_revisions.contains(&snapshot.revision) {
            self.observed_revisions.push_back(snapshot.revision);
            if self.observed_revisions.len() > 128 {
                self.observed_revisions.pop_front();
            }
        }
        self.latest = Some(snapshot.clone());
        Ok(())
    }

    fn permits_presentation(&self, instance: &str, revision: u64) -> bool {
        self.latest.as_ref().is_some_and(|current| {
            current.instance_id == instance
                && revision <= current.revision
                && self.observed_revisions.contains(&revision)
        })
    }

    fn permits_heartbeat(&self, instance: &str) -> bool {
        self.latest.as_ref().is_some_and(|current| current.instance_id == instance)
    }

    fn permits_command(&self, instance: &str, token: &str) -> bool {
        self.latest.as_ref().is_some_and(|current| {
            current.instance_id == instance && current.control_token == token
        })
    }
}

pub struct AssistantBridge {
    pipe: Option<String>,
    identity: Arc<Mutex<ProjectionIdentity>>,
}

impl AssistantBridge {
    pub fn from_environment() -> Self {
        let pipe = configured_pipe();
        Self {
            pipe,
            identity: Arc::new(Mutex::new(ProjectionIdentity::default())),
        }
    }

    pub fn enabled(&self) -> bool {
        self.pipe.is_some()
    }
}

fn configured_pipe() -> Option<String> {
    let enabled = std::env::args_os().any(|arg| arg == "--assistant-bridge");
    std::env::var("YUANSHU_ASSISTANT_PIPE")
        .ok()
        .filter(|name| enabled && valid_pipe_name(name))
}

pub fn integrated_core_pipe() -> Result<Option<String>, String> {
    core_pipe_for_mode(
        configured_pipe().is_some(),
        std::env::var("YUANSHU_CORE_PIPE").ok(),
    )
    .map_err(str::to_string)
}

fn core_pipe_for_mode(
    integrated: bool,
    pipe: Option<String>,
) -> Result<Option<String>, &'static str> {
    if !integrated {
        return Ok(None);
    }
    pipe.filter(|name| {
        !name.is_empty()
            && name.len() <= 180
            && name
                .bytes()
                .all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, b'.' | b'_' | b'-'))
    })
    .map(Some)
    .ok_or("integrated_core_unavailable")
}

pub fn is_integrated(app: &AppHandle) -> bool {
    app.try_state::<AssistantBridge>()
        .is_some_and(|state| state.enabled())
}

fn valid_pipe_name(name: &str) -> bool {
    name.strip_prefix(PIPE_PREFIX).is_some_and(|suffix| {
        suffix.len() == 32
            && suffix
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    })
}

#[tauri::command]
pub async fn assistant_read(
    state: State<'_, AssistantBridge>,
) -> Result<Option<AssistantResponse>, String> {
    let Some(pipe) = state.pipe.clone() else {
        return Ok(None);
    };
    let identity = Arc::clone(&state.identity);
    Ok(Some(
        exchange_async(pipe, identity, Operation::Snapshot).await,
    ))
}

#[tauri::command]
pub async fn assistant_heartbeat(
    window: WebviewWindow,
    state: State<'_, AssistantBridge>,
    instance_id: String,
) -> Result<AssistantResponse, String> {
    let Some(pipe) = state.pipe.clone() else {
        return Ok(AssistantResponse::failure("unavailable"));
    };
    if window.label() != "main" || !valid_uuid(&instance_id) {
        return Ok(AssistantResponse::failure("invalid_request"));
    }
    // JS supplies only the instance. Native visibility never proves that any
    // particular text revision was displayed and carries no revision/authority.
    let visible = window.is_visible().unwrap_or(false) && !window.is_minimized().unwrap_or(true);
    Ok(exchange_async(
        pipe,
        Arc::clone(&state.identity),
        Operation::Heartbeat { instance_id, visible },
    )
    .await)
}

#[tauri::command]
pub async fn assistant_presented(
    window: WebviewWindow,
    state: State<'_, AssistantBridge>,
    instance_id: String,
    revision: u64,
) -> Result<AssistantResponse, String> {
    let Some(pipe) = state.pipe.clone() else {
        return Ok(AssistantResponse::failure("unavailable"));
    };
    if window.label() != "main" || !valid_uuid(&instance_id) || revision > MAX_SAFE_INTEGER {
        return Ok(AssistantResponse::failure("invalid_request"));
    }
    // Visibility is measured natively, never supplied by JS or inferred from focus.
    let visible = window.is_visible().unwrap_or(false) && !window.is_minimized().unwrap_or(true);
    Ok(exchange_async(
        pipe,
        Arc::clone(&state.identity),
        Operation::Presented {
            instance_id,
            revision,
            visible,
        },
    )
    .await)
}

#[tauri::command]
pub async fn assistant_command(
    window: WebviewWindow,
    state: State<'_, AssistantBridge>,
    instance_id: String,
    control_token: String,
    action: String,
) -> Result<AssistantResponse, String> {
    let Some(pipe) = state.pipe.clone() else {
        return Ok(AssistantResponse::failure("unavailable"));
    };
    if window.label() != "main"
        || !valid_uuid(&instance_id)
        || !valid_uuid(&control_token)
        || !valid_action(&action)
    {
        return Ok(AssistantResponse::failure("invalid_request"));
    }
    Ok(exchange_async(
        pipe,
        Arc::clone(&state.identity),
        Operation::Command {
            instance_id,
            control_token,
            action,
        },
    )
    .await)
}

enum Operation {
    Snapshot,
    Heartbeat {
        instance_id: String,
        visible: bool,
    },
    Presented {
        instance_id: String,
        revision: u64,
        visible: bool,
    },
    Command {
        instance_id: String,
        control_token: String,
        action: String,
    },
}

async fn exchange_async(
    pipe: String,
    identity: Arc<Mutex<ProjectionIdentity>>,
    operation: Operation,
) -> AssistantResponse {
    tauri::async_runtime::spawn_blocking(move || {
        // Do not queue stale clicks or overlapping polls behind an in-flight request.
        let Ok(mut state) = identity.try_lock() else {
            return AssistantResponse::failure("busy");
        };
        let mut request = AssistantRequest {
            protocol_version: 1,
            operation: "snapshot",
            instance_id: None,
            revision: None,
            visible: None,
            control_token: None,
            action: None,
        };
        match &operation {
            Operation::Snapshot => {}
            Operation::Heartbeat { instance_id, visible } => {
                if !state.permits_heartbeat(instance_id) {
                    return AssistantResponse::failure("instance_mismatch");
                }
                request.operation = "heartbeat";
                request.instance_id = Some(instance_id);
                request.visible = Some(*visible);
            }
            Operation::Presented {
                instance_id,
                revision,
                visible,
            } => {
                if !state.permits_presentation(instance_id, *revision) {
                    return AssistantResponse::failure("stale_presentation");
                }
                request.operation = "presented";
                request.instance_id = Some(instance_id);
                request.revision = Some(*revision);
                request.visible = Some(*visible);
            }
            Operation::Command {
                instance_id,
                control_token,
                action,
            } => {
                if !state.permits_command(instance_id, control_token) {
                    return AssistantResponse::failure("stale_control");
                }
                request.operation = "command";
                request.instance_id = Some(instance_id);
                request.control_token = Some(control_token);
                request.action = Some(action);
            }
        }
        let response = match exchange(&pipe, &request, REQUEST_TIMEOUT)
            .and_then(|bytes| if matches!(operation, Operation::Heartbeat { .. }) {
                decode_heartbeat_response(&bytes)
            } else { decode_response(&bytes) })
        {
            Ok(value) => value,
            Err(error) => return AssistantResponse::failure(error),
        };
        if let Some(snapshot) = &response.snapshot {
            if let Err(error) = state.accept(snapshot) {
                return AssistantResponse::failure(error);
            }
        }
        response
    })
    .await
    .unwrap_or_else(|_| AssistantResponse::failure("unavailable"))
}

fn valid_action(action: &str) -> bool {
    matches!(
        action,
        "start" | "pause" | "stop" | "management" | "memory" | "exit"
    )
}

fn valid_uuid(value: &str) -> bool {
    value.len() == 36
        && value != "00000000-0000-0000-0000-000000000000"
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
}

fn safe_text(value: &str, maximum: usize, multiline: bool) -> bool {
    value.chars().count() <= maximum && value.chars().all(|ch| {
        (!ch.is_control() || (multiline && matches!(ch, '\r' | '\n' | '\t')))
            && !matches!(ch, '\u{061c}' | '\u{200e}' | '\u{200f}' | '\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}' | '\u{feff}')
    })
}

fn valid_snapshot(snapshot: &AssistantSnapshot) -> bool {
    snapshot.protocol_version == 1
        && valid_uuid(&snapshot.instance_id)
        && valid_uuid(&snapshot.control_token)
        && snapshot.revision <= MAX_SAFE_INTEGER
        && matches!(
            snapshot.phase.as_str(),
            "idle"
                | "listening"
                | "transcribing"
                | "recognized"
                | "working"
                | "speaking"
                | "waiting"
                | "success"
                | "error"
                | "standby"
                | "paused"
                | "stopping"
        )
        && safe_text(&snapshot.status, 160, false)
        && safe_text(&snapshot.transcript, 2000, true)
        && safe_text(&snapshot.detail, 4000, true)
        && valid_timestamp(&snapshot.updated_at_utc)
}

fn decode_response(bytes: &[u8]) -> Result<AssistantResponse, &'static str> {
    decode_response_kind(bytes, false)
}

fn decode_heartbeat_response(bytes: &[u8]) -> Result<AssistantResponse, &'static str> {
    decode_response_kind(bytes, true)
}

fn decode_response_kind(bytes: &[u8], heartbeat: bool) -> Result<AssistantResponse, &'static str> {
    if bytes.is_empty() || bytes.len() > FRAME_LIMIT {
        return Err("invalid_response");
    }
    let response: AssistantResponse =
        serde_json::from_slice(bytes).map_err(|_| "invalid_response")?;
    if (heartbeat && response.snapshot.is_some())
        || (response.ok && (response.error.is_some() || !heartbeat && response.snapshot.is_none()))
        || (!response.ok
            && response.error.as_deref().is_none_or(|error| {
                error.is_empty()
                    || error.len() > 64
                    || !error
                        .bytes()
                        .all(|ch| ch.is_ascii_lowercase() || ch.is_ascii_digit() || ch == b'_')
            }))
        || response
            .snapshot
            .as_ref()
            .is_some_and(|snapshot| !valid_snapshot(snapshot))
    {
        return Err("invalid_response");
    }
    Ok(response)
}

fn valid_timestamp(value: &str) -> bool {
    // Accept UTC RFC3339 from both .NET (seven fractional digits / +00:00)
    // and JS. Never silently accept local-time or future-dated projections.
    let Some(utc) = value
        .strip_suffix('Z')
        .or_else(|| value.strip_suffix("+00:00"))
    else {
        return false;
    };
    let (base, fraction) = utc
        .split_once('.')
        .map_or((utc, None), |(base, fraction)| (base, Some(fraction)));
    if base.len() != 19
        || fraction.is_some_and(|part| {
            part.is_empty() || part.len() > 7 || !part.bytes().all(|ch| ch.is_ascii_digit())
        })
    {
        return false;
    }
    let bytes = base.as_bytes();
    if !bytes.iter().enumerate().all(|(index, ch)| match index {
        4 | 7 => *ch == b'-',
        10 => *ch == b'T',
        13 | 16 => *ch == b':',
        _ => ch.is_ascii_digit(),
    }) {
        return false;
    }
    let number = |start: usize, end: usize| base[start..end].parse::<i64>().unwrap_or(-1);
    let (year, month, day, hour, minute, second) = (
        number(0, 4),
        number(5, 7),
        number(8, 10),
        number(11, 13),
        number(14, 16),
        number(17, 19),
    );
    if !(1970..=9999).contains(&year)
        || !(1..=12).contains(&month)
        || !(0..=23).contains(&hour)
        || !(0..=59).contains(&minute)
        || !(0..=59).contains(&second)
    {
        return false;
    }
    let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    let days = [
        31,
        if leap { 29 } else { 28 },
        31,
        30,
        31,
        30,
        31,
        31,
        30,
        31,
        30,
        31,
    ];
    if day < 1 || day > days[month as usize - 1] {
        return false;
    }
    let prior_leaps = |year: i64| year / 4 - year / 100 + year / 400;
    let whole_days = (year - 1970) * 365 + prior_leaps(year - 1) - prior_leaps(1969)
        + days[..month as usize - 1].iter().sum::<i64>()
        + day
        - 1;
    let seconds = (whole_days * 86_400 + hour * 3_600 + minute * 60 + second) as u64;
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .is_ok_and(|now| seconds <= now.as_secs().saturating_add(60))
}

struct OwnedEvent(HANDLE);
impl Drop for OwnedEvent {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}

fn exchange(
    pipe: &str,
    request: &AssistantRequest<'_>,
    timeout: Duration,
) -> Result<Vec<u8>, &'static str> {
    if !valid_pipe_name(pipe) {
        return Err("unavailable");
    }
    let body = serde_json::to_vec(request).map_err(|_| "invalid_request")?;
    if body.is_empty() || body.len() > FRAME_LIMIT {
        return Err("invalid_request");
    }
    // Open returns immediately for absent/busy pipes. No WaitNamedPipe or retries
    // can resend a command whose delivery was ambiguous.
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(FILE_FLAG_OVERLAPPED.0)
        .open(format!(r"\\.\pipe\{pipe}"))
        .map_err(|_| "disconnected")?;
    let handle = HANDLE(file.as_raw_handle());
    let deadline = Instant::now() + timeout;
    let mut frame = Vec::with_capacity(body.len() + 4);
    frame.extend_from_slice(&(body.len() as u32).to_le_bytes());
    frame.extend_from_slice(&body);
    let mut offset = 0;
    while offset < frame.len() {
        offset += transfer(handle, IoBuffer::Write(&frame[offset..]), deadline)?;
    }
    let mut header = [0; 4];
    read_exact(handle, &mut header, deadline)?;
    let size = u32::from_le_bytes(header) as usize;
    if size == 0 || size > FRAME_LIMIT {
        return Err("invalid_response");
    }
    let mut result = vec![0; size];
    read_exact(handle, &mut result, deadline)?;
    Ok(result)
}

fn read_exact(handle: HANDLE, buffer: &mut [u8], deadline: Instant) -> Result<(), &'static str> {
    let mut offset = 0;
    while offset < buffer.len() {
        offset += transfer(handle, IoBuffer::Read(&mut buffer[offset..]), deadline)?;
    }
    Ok(())
}

enum IoBuffer<'a> {
    Read(&'a mut [u8]),
    Write(&'a [u8]),
}

fn transfer(
    handle: HANDLE,
    buffer: IoBuffer<'_>,
    deadline: Instant,
) -> Result<usize, &'static str> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return Err("timeout");
    }
    let event = OwnedEvent(
        unsafe { CreateEventW(None, true, false, windows::core::PCWSTR::null()) }
            .map_err(|_| "unavailable")?,
    );
    let mut overlapped = OVERLAPPED {
        hEvent: event.0,
        ..Default::default()
    };
    let mut count = 0;
    let started = match buffer {
        IoBuffer::Read(buffer) => unsafe {
            ReadFile(
                handle,
                Some(buffer),
                Some(&mut count),
                Some(&mut overlapped),
            )
        },
        IoBuffer::Write(buffer) => unsafe {
            WriteFile(
                handle,
                Some(buffer),
                Some(&mut count),
                Some(&mut overlapped),
            )
        },
    };
    if let Err(error) = started {
        if error.code() != ERROR_IO_PENDING.to_hresult() {
            return Err("disconnected");
        }
        let wait = unsafe {
            WaitForSingleObject(
                event.0,
                remaining.as_millis().max(1).min(u32::MAX as u128) as u32,
            )
        };
        if wait != WAIT_OBJECT_0 {
            // Named-pipe cancellation completes before stack buffers/OVERLAPPED
            // are released. This is kernel cleanup, not a protocol retry.
            let _ = unsafe { CancelIoEx(handle, Some(&overlapped)) };
            let _ = unsafe { GetOverlappedResult(handle, &overlapped, &mut count, true) };
            return Err("timeout");
        }
        unsafe { GetOverlappedResult(handle, &overlapped, &mut count, false) }
            .map_err(|_| "disconnected")?;
    }
    if count == 0 {
        return Err("disconnected");
    }
    Ok(count as usize)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::{
        fs::File,
        io::{Read, Write},
        os::windows::io::FromRawHandle,
        sync::mpsc,
        thread,
    };
    use windows::{
        core::PCWSTR,
        Win32::{
            Foundation::ERROR_PIPE_CONNECTED,
            Storage::FileSystem::{FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX},
            System::Pipes::{
                ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_TYPE_BYTE, PIPE_WAIT,
            },
        },
    };

    fn snapshot() -> AssistantSnapshot {
        serde_json::from_value(json!({
            "protocolVersion": 1, "instanceId": "a2345678-1234-1234-1234-123456789abc", "revision": 4,
            "controlToken": "b2345678-1234-1234-1234-123456789abc", "phase": "recognized", "status": "已听到",
            "transcript": "打开测试应用", "detail": "等待显示后提交", "microphoneOn": false,
            "canStart": false, "canPause": false, "canStop": true, "needsAttention": false,
            "updatedAtUtc": "2025-01-02T03:04:05.1234567+00:00"
        })).unwrap()
    }

    #[test]
    fn pipe_names_only_accept_random_local_assistant_names() {
        assert!(valid_pipe_name(
            "Yuanshu.Assistant.0123456789abcdef0123456789abcdef"
        ));
        for name in [
            "Yuanshu.Assistant.test",
            "Yuanshu.Assistant.0123456789ABCDEF0123456789ABCDEF",
            "\\\\host\\pipe\\test",
            "../test",
            "ScreenGuide.DesktopHost.V01",
        ] {
            assert!(!valid_pipe_name(name));
        }
    }

    #[test]
    fn integrated_task_bridge_never_falls_back_to_another_default_host() {
        assert_eq!(core_pipe_for_mode(false, None), Ok(None));
        assert_eq!(core_pipe_for_mode(false, Some("ignored".into())), Ok(None));
        assert_eq!(
            core_pipe_for_mode(true, Some("ScreenGuide.AssistantFollowup.20261011".into())),
            Ok(Some("ScreenGuide.AssistantFollowup.20261011".into()))
        );
        assert_eq!(
            core_pipe_for_mode(true, None),
            Err("integrated_core_unavailable")
        );
        assert_eq!(
            core_pipe_for_mode(true, Some(r"\\other\pipe\Core".into())),
            Err("integrated_core_unavailable")
        );
    }

    #[test]
    fn responses_require_exact_schema_valid_state_and_safe_bounds() {
        let valid = json!({ "ok": true, "error": null, "snapshot": snapshot() });
        assert!(decode_response(&serde_json::to_vec(&valid).unwrap()).is_ok());
        let mut unicode = valid.clone();
        unicode["snapshot"]["transcript"] = json!("👩‍💻\n正常文字\t继续\r\n");
        assert!(decode_response(&serde_json::to_vec(&unicode).unwrap()).is_ok());
        for (field, value) in [
            ("protocolVersion", json!(2)),
            ("phase", json!("publish")),
            ("instanceId", json!("not-a-uuid")),
            ("revision", json!(MAX_SAFE_INTEGER + 1)),
            ("status", json!("a".repeat(161))),
            ("transcript", json!("a".repeat(2001))),
            ("detail", json!("a".repeat(4001))),
            ("status", json!("hidden\u{202e}text")),
            ("updatedAtUtc", json!("9999-01-01T00:00:00Z")),
        ] {
            let mut invalid = valid.clone();
            invalid["snapshot"][field] = value;
            assert!(
                decode_response(&serde_json::to_vec(&invalid).unwrap()).is_err(),
                "{field}"
            );
        }
        for invalid in [
            json!({"ok":true,"snapshot":snapshot()}),
            json!({"ok":true,"error":null,"snapshot":null}),
            json!({"ok":false,"error":null,"snapshot":null}),
            json!({"ok":false,"error":"private path","snapshot":null}),
            json!({"ok":true,"error":null,"snapshot":snapshot(),"extra":true}),
        ] {
            assert!(decode_response(&serde_json::to_vec(&invalid).unwrap()).is_err());
        }
        assert!(
            decode_response(br#"{"ok":false,"ok":false,"error":"busy","snapshot":null}"#).is_err()
        );
    }

    #[test]
    fn acknowledgements_cannot_invent_future_or_replace_instance() {
        let mut identity = ProjectionIdentity::default();
        let initial = snapshot();
        assert!(!identity.permits_presentation(&initial.instance_id, 0));
        identity.accept(&initial).unwrap();
        assert!(identity.permits_presentation(&initial.instance_id, 4));
        assert!(!identity.permits_presentation(&initial.instance_id, 5));
        assert!(!identity.permits_presentation(&initial.instance_id, 3));
        let mut newer = initial.clone();
        newer.revision = 5;
        identity.accept(&newer).unwrap();
        assert!(identity.permits_presentation(&initial.instance_id, 4));
        let mut stale = initial.clone();
        stale.revision = 3;
        assert_eq!(identity.accept(&stale), Err("stale_snapshot"));
        stale.revision = 5;
        stale.transcript = "不能复用版本修改正文".into();
        assert_eq!(identity.accept(&stale), Err("stale_snapshot"));
        stale.revision = 5;
        stale.instance_id = initial.control_token.clone();
        assert_eq!(identity.accept(&stale), Err("instance_changed"));
        assert!(!identity.permits_command(&initial.instance_id, &initial.instance_id));
        assert!(identity.permits_command(&initial.instance_id, &initial.control_token));
    }

    #[test]
    fn command_allowlist_never_accepts_execution_or_confirmation() {
        for action in ["start", "pause", "stop", "management", "memory", "exit"] {
            assert!(valid_action(action));
        }
        for action in ["approve", "execute", "delete", "command", "START", ""] {
            assert!(!valid_action(action));
        }
    }

    #[test]
    fn utc_timestamp_requires_valid_calendar_and_explicit_utc() {
        for value in [
            "2024-02-29T23:59:59Z",
            "2025-01-01T00:00:00.1Z",
            "2025-01-01T00:00:00+00:00",
        ] {
            assert!(valid_timestamp(value));
        }
        for value in [
            "2025-02-29T00:00:00Z",
            "2025-01-01T24:00:00Z",
            "2025-01-01T00:00:00+08:00",
            "2025-01-01T00:00:00",
            "2025-01-01T00:00:00.Z",
            "9999-01-01T00:00:00Z",
        ] {
            assert!(!valid_timestamp(value));
        }
    }

    // These use only isolated synthetic data, with a native pipe server. They
    // exercise the real framing/overlapped path, not a mock transport function.
    fn pipe_server(
        handler: impl FnOnce(File) + Send + 'static,
    ) -> (String, thread::JoinHandle<()>) {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let name = format!("{PIPE_PREFIX}{suffix:032x}");
        let path = format!(r"\\.\pipe\{name}")
            .encode_utf16()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let (ready, connected) = mpsc::channel();
        let worker = thread::spawn(move || {
            let handle = unsafe {
                CreateNamedPipeW(
                    PCWSTR(path.as_ptr()),
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                    1,
                    4096,
                    4096,
                    0,
                    None,
                )
            };
            assert!(!handle.is_invalid());
            let file = unsafe { File::from_raw_handle(handle.0) };
            ready.send(()).unwrap();
            let connection = unsafe { ConnectNamedPipe(handle, None) };
            assert!(
                connection.is_ok()
                    || connection.unwrap_err().code() == ERROR_PIPE_CONNECTED.to_hresult()
            );
            handler(file);
        });
        connected.recv_timeout(Duration::from_secs(2)).unwrap();
        (name, worker)
    }

    fn request() -> AssistantRequest<'static> {
        AssistantRequest {
            protocol_version: 1,
            operation: "snapshot",
            instance_id: None,
            revision: None,
            visible: None,
            control_token: None,
            action: None,
        }
    }

    fn receive_request(file: &mut File) -> serde_json::Value {
        let mut header = [0; 4];
        file.read_exact(&mut header).unwrap();
        let mut body = vec![0; u32::from_le_bytes(header) as usize];
        file.read_exact(&mut body).unwrap();
        serde_json::from_slice(&body).unwrap()
    }

    #[test]
    fn native_heartbeat_has_no_revision_or_control_authority() {
        for visible in [true, false] {
            let initial = snapshot();
            let instance = initial.instance_id.clone();
            let expected_instance = instance.clone();
            let identity = Arc::new(Mutex::new(ProjectionIdentity::default()));
            identity.lock().unwrap().accept(&initial).unwrap();
            let (pipe, worker) = pipe_server(move |mut file| {
                assert_eq!(receive_request(&mut file), json!({
                    "protocolVersion":1,"operation":"heartbeat","instanceId":expected_instance,"visible":visible
                }));
                let response = br#"{"ok":true,"error":null,"snapshot":null}"#;
                file.write_all(&(response.len() as u32).to_le_bytes()).unwrap();
                file.write_all(response).unwrap();
                let mut trailing = [0; 1];
                let _ = file.read(&mut trailing);
            });
            let response = tauri::async_runtime::block_on(exchange_async(pipe, Arc::clone(&identity),
                Operation::Heartbeat { instance_id: instance, visible }));
            assert!(response.ok);
            assert!(response.snapshot.is_none());
            let state = identity.lock().unwrap();
            assert_eq!(state.latest.as_ref(), Some(&initial));
            assert!(!state.permits_presentation(&initial.instance_id, initial.revision + 1));
            worker.join().unwrap();
        }
    }

    #[test]
    fn heartbeat_rejects_missing_or_changed_instance_before_transport() {
        let initial = snapshot();
        let identity = Arc::new(Mutex::new(ProjectionIdentity::default()));
        for known in [false, true] {
            if known { identity.lock().unwrap().accept(&initial).unwrap(); }
            let instance_id = if known { initial.control_token.clone() } else { initial.instance_id.clone() };
            let response = tauri::async_runtime::block_on(exchange_async("unused-no-server".into(),
                Arc::clone(&identity), Operation::Heartbeat { instance_id, visible: true }));
            assert!(!response.ok);
            assert_eq!(response.error.as_deref(), Some("instance_mismatch"));
        }
    }

    #[test]
    fn heartbeat_response_requires_an_exact_empty_receipt() {
        assert!(decode_heartbeat_response(br#"{"ok":true,"error":null,"snapshot":null}"#).unwrap().ok);
        assert!(!decode_heartbeat_response(br#"{"ok":false,"error":"presentation_required","snapshot":null}"#).unwrap().ok);
        for invalid in [
            json!({"ok":true,"error":null,"snapshot":snapshot()}),
            json!({"ok":false,"error":"busy","snapshot":snapshot()}),
            json!({"ok":true,"snapshot":null}),
            json!({"ok":true,"error":null}),
            json!({"ok":false,"error":null,"snapshot":null}),
            json!({"ok":true,"error":null,"snapshot":null,"revision":4}),
        ] {
            assert!(decode_heartbeat_response(&serde_json::to_vec(&invalid).unwrap()).is_err());
        }
    }

    #[test]
    fn heartbeat_response_cannot_register_an_unread_revision() {
        let initial = snapshot();
        let mut newer = initial.clone();
        newer.revision += 1;
        let identity = Arc::new(Mutex::new(ProjectionIdentity::default()));
        identity.lock().unwrap().accept(&initial).unwrap();
        let (pipe, worker) = pipe_server(move |mut file| {
            assert_eq!(receive_request(&mut file)["operation"], "heartbeat");
            let response = serde_json::to_vec(&json!({"ok":true,"error":null,"snapshot":newer})).unwrap();
            file.write_all(&(response.len() as u32).to_le_bytes()).unwrap();
            file.write_all(&response).unwrap();
            let mut trailing = [0; 1];
            let _ = file.read(&mut trailing);
        });
        let response = tauri::async_runtime::block_on(exchange_async(pipe, Arc::clone(&identity),
            Operation::Heartbeat { instance_id: initial.instance_id.clone(), visible: true }));
        assert!(!response.ok);
        assert_eq!(response.error.as_deref(), Some("invalid_response"));
        assert_eq!(identity.lock().unwrap().latest.as_ref(), Some(&initial));
        worker.join().unwrap();
    }

    #[test]
    fn native_pipe_reads_fragmented_header_and_body_without_replaying_request() {
        let expected =
            serde_json::to_vec(&json!({"ok":true,"error":null,"snapshot":snapshot()})).unwrap();
        let outgoing = expected.clone();
        let (pipe, worker) = pipe_server(move |mut file| {
            assert_eq!(
                receive_request(&mut file),
                json!({"protocolVersion":1,"operation":"snapshot"})
            );
            let header = (outgoing.len() as u32).to_le_bytes();
            file.write_all(&header[..2]).unwrap();
            thread::sleep(Duration::from_millis(10));
            file.write_all(&header[2..]).unwrap();
            for bytes in outgoing.chunks(31) {
                file.write_all(bytes).unwrap();
            }
            // Keep server alive until client has consumed its response.
            let mut trailing = [0; 1];
            assert!(matches!(file.read(&mut trailing), Ok(0) | Err(_)));
        });
        assert_eq!(
            exchange(&pipe, &request(), Duration::from_secs(1)).unwrap(),
            expected
        );
        worker.join().unwrap();
    }

    #[test]
    fn native_pipe_read_deadline_cancels_unresponsive_server() {
        let (pipe, worker) = pipe_server(|mut file| {
            receive_request(&mut file);
            thread::sleep(Duration::from_millis(250));
        });
        let started = Instant::now();
        assert_eq!(
            exchange(&pipe, &request(), Duration::from_millis(60)),
            Err("timeout")
        );
        assert!(started.elapsed() < Duration::from_millis(700));
        worker.join().unwrap();
    }

    #[test]
    fn native_pipe_write_deadline_cancels_server_that_never_reads() {
        let (pipe, worker) = pipe_server(|_file| {
            thread::sleep(Duration::from_millis(250));
        });
        let payload = "a".repeat(30_000);
        let mut large_request = request();
        large_request.instance_id = Some(&payload);
        let started = Instant::now();
        assert_eq!(
            exchange(&pipe, &large_request, Duration::from_millis(60)),
            Err("timeout")
        );
        assert!(started.elapsed() < Duration::from_millis(700));
        worker.join().unwrap();
    }

    #[test]
    fn native_pipe_rejects_oversized_body_before_allocation() {
        let (pipe, worker) = pipe_server(|mut file| {
            receive_request(&mut file);
            file.write_all(&((FRAME_LIMIT + 1) as u32).to_le_bytes())
                .unwrap();
            let mut trailing = [0; 1];
            let _ = file.read(&mut trailing);
        });
        assert_eq!(
            exchange(&pipe, &request(), Duration::from_secs(1)),
            Err("invalid_response")
        );
        worker.join().unwrap();
    }
}

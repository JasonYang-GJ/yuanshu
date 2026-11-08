#[cfg(windows)]
mod assistant;
#[cfg(windows)]
mod assistant_placement;
#[cfg(windows)]
mod assistant_surface;
#[cfg(windows)]
mod bridge;
#[cfg(windows)]
mod embedded_startup;
#[cfg(windows)]
mod media;
#[cfg(windows)]
mod presence;
#[cfg(windows)]
mod settings;
#[cfg(windows)]
mod volume;
mod window;

use window::AnimationController;

#[cfg(windows)]
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let assistant_bridge = assistant::AssistantBridge::from_environment();
    let activation = std::env::args_os().any(|argument| is_assistant_activation(&argument));
    if !may_start_component(assistant_bridge.enabled(), activation) {
        explain_component_entry();
        return;
    }
    let builder = tauri::Builder::default()
        // The single-instance plugin must be registered before every other plugin.
        .plugin(tauri_plugin_single_instance::init(|app, args, _cwd| {
            // A new integrated child cannot attach its private pipe to an old
            // process. Let the parent observe this child exit and recover.
            if args.iter().any(|argument| argument == "--assistant-bridge") {
                return;
            }
            if args
                .iter()
                .any(|argument| opens_settings(argument.as_ref()))
            {
                let _ = settings::open_island_panel(app.clone());
            } else {
                let _ = presence::request_show(app, true);
            }
        }))
        .manage(AnimationController::default())
        .manage(window::StartupState::new(false))
        .manage(assistant_bridge)
        .manage(assistant_surface::AssistantSurface::default())
        .manage(presence::WindowVisibilityState::default());
    #[cfg(windows)]
    let builder = builder.invoke_handler(tauri::generate_handler![
        window::set_island_mode,
        window::get_startup_low_presence,
        media::get_windows_media_state,
        bridge::get_yuanshu_core_bridge_state,
        presence::get_fullscreen_presence,
        settings::get_island_settings,
        settings::save_island_settings,
        settings::get_island_autostart,
        settings::set_island_autostart,
        settings::open_island_panel,
        settings::set_island_visible,
        bridge::get_island_control_status,
        bridge::cancel_island_task,
        bridge::get_island_diagnostics,
        assistant::assistant_read,
        assistant::assistant_heartbeat,
        assistant::assistant_presented,
        assistant::assistant_command,
        assistant_surface::assistant_surface_state,
        assistant_surface::assistant_release_controls,
        assistant_placement::assistant_placement_state,
        assistant_placement::assistant_begin_drag,
        assistant_placement::assistant_open_controls
    ]);
    #[cfg(not(windows))]
    let builder = builder.invoke_handler(tauri::generate_handler![window::set_island_mode]);

    builder
        .setup(|app| {
            // This activation-only process normally exits in the single-instance
            // plugin. If the original child disappeared in the meantime, never
            // create a disconnected standalone island as a replacement.
            if std::env::args_os().any(|argument| is_assistant_activation(&argument)) {
                app.handle().exit(0);
                return Ok(());
            }
            let mut config_root = app.path().app_config_dir()?;
            if let Some(profile) = settings::profile().map_err(desktop_error)? {
                config_root = config_root.join("profiles").join(profile);
            }
            let config = config_root.join("preferences.json");
            app.manage(settings::SettingsStore::load(config));
            app.manage(assistant_placement::AssistantPlacement::load(
                config_root.join("placement.json"),
            ));
            let low_presence = app.state::<window::StartupState>().low_presence;
            window::initialize_main_window(app, low_presence).map_err(desktop_error)?;
            app.manage(presence::start(app.handle().clone()).map_err(desktop_error)?);
            presence::show_initial_window(app.handle()).map_err(desktop_error)?;
            // DesktopClient owns the only application tray and exit lifecycle.
            if let Ok(shortcut) = assistant_surface::start_shortcut(app.handle().clone()) {
                app.manage(shortcut);
            }
            if std::env::args_os().any(|argument| argument == "--settings") {
                settings::open_island_panel(app.handle().clone()).map_err(desktop_error)?;
            }
            #[cfg(windows)]
            {
                app.manage(volume::start(app.handle().clone()).map_err(desktop_error)?);
                app.manage(media::start(app.handle().clone()).map_err(desktop_error)?);
                app.manage(bridge::start(app.handle().clone()).map_err(desktop_error)?);
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() == "main" && assistant::is_integrated(window.app_handle()) {
                if let tauri::WindowEvent::Focused(false) = event {
                    // WebView child-focus transfers and queued blur events can
                    // arrive while the native island still owns foreground.
                    if !assistant_placement::is_dragging(window.app_handle())
                        && !window::assistant_owns_foreground(window.app_handle())
                    {
                        let _ = assistant_surface::release_controls(window.app_handle());
                    }
                }
            }
            if window.label() == "panel" {
                if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .build(tauri::generate_context!())
        .expect("Yuanshu Island failed to start")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                if let Some(placement) = app.try_state::<assistant_placement::AssistantPlacement>()
                {
                    placement.shutdown();
                }
                if let Some(shortcut) = app.try_state::<assistant_surface::ShortcutWorker>() {
                    shortcut.stop();
                }
            }
        });
}

fn desktop_error(message: String) -> Box<dyn std::error::Error> {
    std::io::Error::other(message).into()
}

fn opens_settings(argument: &std::ffi::OsStr) -> bool {
    argument == "--settings" || argument == "--assistant-settings"
}

fn is_assistant_activation(argument: &std::ffi::OsStr) -> bool {
    argument == "--show-controls" || opens_settings(argument)
}

fn may_start_component(bridge_configured: bool, activation: bool) -> bool {
    bridge_configured || activation
}

fn explain_component_entry() {
    use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONINFORMATION, MB_OK};
    use windows_core::w;
    // An explicit direct launch gets a visible explanation, without creating
    // another tray, a disconnected task surface, or a startup registration.
    unsafe {
        MessageBoxW(
            None,
            w!("灵动岛已内置到元枢。请从桌面或开始菜单中的“元枢”启动。"),
            w!("请从元枢启动"),
            MB_OK | MB_ICONINFORMATION,
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn assistant_settings_activates_settings_but_never_starts_a_detached_island() {
        let argument = std::ffi::OsStr::new("--assistant-settings");
        assert!(opens_settings(argument));
        assert!(is_assistant_activation(argument));
        assert!(is_assistant_activation(std::ffi::OsStr::new(
            "--show-controls"
        )));
        assert!(!opens_settings(std::ffi::OsStr::new("--show-controls")));
    }

    #[test]
    fn legacy_settings_can_only_activate_an_existing_component() {
        assert!(opens_settings(std::ffi::OsStr::new("--settings")));
        assert!(is_assistant_activation(std::ffi::OsStr::new("--settings")));
        for argument in [
            "--autostart",
            "--assistant-bridge",
            "--assistant-settings-extra",
            "",
        ] {
            assert!(!is_assistant_activation(std::ffi::OsStr::new(argument)));
        }
        assert!(!opens_settings(std::ffi::OsStr::new(
            "--assistant-settings-extra"
        )));
    }

    #[test]
    fn standalone_and_invalid_bridge_launches_cannot_create_an_island() {
        assert!(!may_start_component(false, false));
        assert!(may_start_component(true, false));
        assert!(may_start_component(false, true));
        assert!(may_start_component(true, true));
    }
}

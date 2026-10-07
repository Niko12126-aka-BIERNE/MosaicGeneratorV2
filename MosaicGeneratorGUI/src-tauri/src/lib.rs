// All app logic lives in the React frontend and the C# sidecar. Rust only registers the
// plugins the frontend uses:
//   shell  - starts the sidecar and talks to it over stdin/stdout
//   dialog - native open/save file pickers
//   opener - opens the finished mosaic in the default image viewer or browser
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_opener::init())
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

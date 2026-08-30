/// Demonstrates M3: process tree cleanup.
///
/// Spawns a process that immediately forks a long-lived background child, then
/// exits. Without M3, that child would survive as an orphan. AER guarantees the
/// entire tree is dead before run() returns — no new API required.
///
/// Run with: pixi run example-tree
use aer_core::{Event, Task};

fn main() {
    println!("Spawning a process that forks a long-lived background child...\n");
    println!("The root exits immediately; without AER the child would be an orphan.");
    println!("AER guarantees the entire process tree is dead when run() returns.\n");

    // The root spawns a background child (sleep / ping) and exits immediately.
    // AER's job object (Windows) or killpg (Unix) terminates the child before
    // run() returns.
    #[cfg(target_os = "windows")]
    let task = Task::new(
        "powershell",
        vec![
            "-NoProfile",
            "-Command",
            "Start-Process -NoNewWindow -FilePath ping \
             -ArgumentList @('-n','60','127.0.0.1'); \
             Write-Host 'root exiting'",
        ],
    );
    #[cfg(not(target_os = "windows"))]
    let task = Task::new("sh", vec!["-c", "sleep 60 & echo 'root exiting'"]);

    task.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {pid})"),
        Event::Exited { code, .. } => println!("  → Exited   (code {code})"),
        _ => {}
    })
    .expect("task failed");

    println!("\nDone. Background child is gone — no orphans.");
}

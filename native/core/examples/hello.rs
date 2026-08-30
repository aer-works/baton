/// Demonstrates the M1 AER lifecycle: spawn a process, observe STARTED and EXITED events.
///
/// Run with: pixi run example
use aer_core::{AerError, Event, Task};

fn main() -> Result<(), AerError> {
    #[cfg(target_os = "windows")]
    let task = Task::new("cmd", vec!["/c", "echo Hello from AER!"]);
    #[cfg(not(target_os = "windows"))]
    let task = Task::new("sh", vec!["-c", "echo Hello from AER!"]);

    println!("Spawning task...\n");

    task.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {})", pid),
        Event::Exited { code, .. } => println!("  → Exited   (code {})\n", code),
        _ => {}
    })?;

    println!("Done.");
    Ok(())
}

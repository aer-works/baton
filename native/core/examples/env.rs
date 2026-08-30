/// Demonstrates environment variable injection and working directory selection:
/// `with_env` and `with_cwd`.
///
/// A single variable is set on the child and its working directory is pointed
/// at the system temp dir; the child echoes the variable and prints its cwd
/// so we can see both took effect.
///
/// Run with: pixi run example-env
use aer_core::{AerError, Event, Task};
use std::env;

fn main() -> Result<(), AerError> {
    let cwd = env::temp_dir();

    #[cfg(target_os = "windows")]
    let task = Task::new("cmd", vec!["/c", "echo %AER_EXAMPLE_VAR% & cd"]);
    #[cfg(not(target_os = "windows"))]
    let task = Task::new("sh", vec!["-c", "echo $AER_EXAMPLE_VAR; pwd"]);

    let task = task
        .with_env("AER_EXAMPLE_VAR", "hello_from_aer")
        .with_cwd(&cwd)
        .with_capture_output(true);

    println!(
        "Spawning with AER_EXAMPLE_VAR=hello_from_aer, cwd={}\n",
        cwd.display()
    );

    let mut stdout_buf: Vec<u8> = Vec::new();

    task.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {pid})"),
        Event::StdoutChunk { bytes, .. } => stdout_buf.extend_from_slice(&bytes),
        Event::Exited { code, .. } => println!("  → Exited   (code {code})"),
        _ => {}
    })?;

    println!("\n--- what the child saw ---");
    print!("{}", String::from_utf8_lossy(&stdout_buf));

    println!("\nDone.");
    Ok(())
}

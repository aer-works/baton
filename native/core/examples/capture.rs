/// Demonstrates M4b: opt-in stdout/stderr capture via StdoutChunk and StderrChunk events.
///
/// Run with: pixi run example-capture
use aer_core::{AerError, Event, Task};

fn main() -> Result<(), AerError> {
    // A command that writes a few lines to both stdout and stderr.
    #[cfg(target_os = "windows")]
    let task = Task::new(
        "cmd",
        vec![
            "/c",
            "(echo stdout line 1) & \
             (echo stderr line 1 1>&2) & \
             (echo stdout line 2) & \
             (echo stderr line 2 1>&2) & \
             (echo stdout line 3)",
        ],
    );
    #[cfg(not(target_os = "windows"))]
    let task = Task::new(
        "sh",
        vec![
            "-c",
            "echo 'stdout line 1'; \
             echo 'stderr line 1' >&2; \
             echo 'stdout line 2'; \
             echo 'stderr line 2' >&2; \
             echo 'stdout line 3'",
        ],
    );

    let task = task.with_capture_output(true);

    println!("Spawning with capture enabled...\n");

    let mut stdout_buf: Vec<u8> = Vec::new();
    let mut stderr_buf: Vec<u8> = Vec::new();

    task.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {pid})"),
        Event::StdoutChunk { seq, bytes } => {
            println!("  → StdoutChunk  seq={seq} len={}", bytes.len());
            stdout_buf.extend_from_slice(&bytes);
        }
        Event::StderrChunk { seq, bytes } => {
            println!("  → StderrChunk  seq={seq} len={}", bytes.len());
            stderr_buf.extend_from_slice(&bytes);
        }
        Event::Exited { code, reason } => println!("  → Exited   (code {code}, reason {reason:?})"),
    })?;

    println!("\n--- stdout ({} bytes) ---", stdout_buf.len());
    print!("{}", String::from_utf8_lossy(&stdout_buf));

    println!("\n--- stderr ({} bytes) ---", stderr_buf.len());
    print!("{}", String::from_utf8_lossy(&stderr_buf));

    println!("\nDone.");
    Ok(())
}

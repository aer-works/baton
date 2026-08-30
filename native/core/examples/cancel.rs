/// Demonstrates M4c: on-demand cancellation via CancelHandle.
///
/// A long-running process is started on the main thread. A separate thread
/// waits 1 second and then cancels it. The Exited event carries
/// reason=CancelRequested and run_with_cancel returns Err(Cancelled).
///
/// Run with: pixi run example-cancel
use aer_core::{AerError, CancelHandle, Event, Task};
use std::thread;
use std::time::Duration;

fn main() {
    // A process that would run for 60 seconds if not cancelled.
    #[cfg(target_os = "windows")]
    let task = Task::new("ping", vec!["-n", "61", "127.0.0.1"]);
    #[cfg(not(target_os = "windows"))]
    let task = Task::new("sh", vec!["-c", "sleep 60"]);

    let cancel = CancelHandle::new();
    let cancel_for_thread = cancel.clone();

    // Cancel from a background thread after 1 second.
    let cancel_thread = thread::spawn(move || {
        thread::sleep(Duration::from_secs(1));
        println!("  [canceller] sending cancel...");
        cancel_for_thread.cancel();
    });

    println!("Spawning long-running process (will be cancelled in ~1 s)...\n");

    let result = task.run_with_cancel(
        |event| match event {
            Event::Started { pid } => println!("  → Started  (pid {pid})"),
            Event::Exited { code, reason } => {
                println!("  → Exited   (code {code}, reason {reason:?})")
            }
            _ => {}
        },
        &cancel,
    );

    cancel_thread.join().unwrap();

    match result {
        Err(AerError::Cancelled) => {
            println!("\nrun_with_cancel returned Err(Cancelled) — as expected.")
        }
        other => println!("\nunexpected result: {other:?}"),
    }
}

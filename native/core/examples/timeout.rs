/// Demonstrates M2: timeout and kill escalation.
///
/// Shows two cases:
///   1. A long-running process killed by the timeout deadline.
///   2. A fast process that completes before the timeout fires.
///
/// Run with: pixi run example-timeout
use aer_core::{AerError, Event, Task};
use std::time::Duration;

fn main() {
    // --- Case 1: timeout fires ---
    println!("=== Process outlives timeout (2 s) ===\n");

    #[cfg(target_os = "windows")]
    let slow = Task::new("ping", vec!["-n", "61", "127.0.0.1"]);
    #[cfg(not(target_os = "windows"))]
    let slow = Task::new("sh", vec!["-c", "sleep 60"]);

    let slow = slow.with_timeout(Duration::from_secs(2));

    match slow.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {pid})"),
        Event::Exited { code, .. } => println!("  → Exited   (code {code})"),
        _ => {}
    }) {
        Err(AerError::TimedOut) => println!("  → TimedOut\n"),
        other => println!("  unexpected result: {other:?}\n"),
    }

    // --- Case 2: process exits before timeout ---
    println!("=== Process exits before timeout (30 s) ===\n");

    #[cfg(target_os = "windows")]
    let fast = Task::new("cmd", vec!["/c", "echo Hello from AER!"]);
    #[cfg(not(target_os = "windows"))]
    let fast = Task::new("sh", vec!["-c", "echo Hello from AER!"]);

    let fast = fast.with_timeout(Duration::from_secs(30));

    match fast.run(|event| match event {
        Event::Started { pid } => println!("  → Started  (pid {pid})"),
        Event::Exited { code, .. } => println!("  → Exited   (code {code})"),
        _ => {}
    }) {
        Ok(()) => println!("  → Completed normally"),
        other => println!("  unexpected result: {other:?}"),
    }
}

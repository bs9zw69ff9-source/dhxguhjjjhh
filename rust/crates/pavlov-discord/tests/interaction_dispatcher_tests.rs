//! Port of `InteractionDispatcherTests.cs`: work runs off the read loop, is drained on
//! shutdown, and a fault is contained.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::{Duration, Instant};

use pavlov_discord::dispatcher::InteractionDispatcher;
use tokio::sync::oneshot;

async fn until(condition: impl Fn() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    condition()
}

#[tokio::test]
async fn run_returns_while_the_work_is_still_blocked() {
    let dispatcher = InteractionDispatcher::new();
    let (release, released) = oneshot::channel::<()>();

    dispatcher.run("slow", async move {
        let _ = released.await;
    });

    assert_eq!(dispatcher.in_flight(), 1);
    release.send(()).unwrap();
    assert!(until(|| dispatcher.in_flight() == 0).await);
}

#[tokio::test]
async fn drain_waits_for_work_in_flight() {
    let dispatcher = InteractionDispatcher::new();
    let (release, released) = oneshot::channel::<()>();
    dispatcher.run("slow", async move {
        let _ = released.await;
    });

    assert!(!dispatcher.drain(Duration::from_millis(50)).await);

    release.send(()).unwrap();
    assert!(dispatcher.drain(Duration::from_secs(5)).await);
}

#[tokio::test]
async fn a_fault_is_contained_and_logged_not_thrown() {
    let dispatcher = InteractionDispatcher::new();
    dispatcher.run("broken", async { panic!("boom") });

    assert!(dispatcher.drain(Duration::from_secs(5)).await);
    assert_eq!(dispatcher.in_flight(), 0);
}

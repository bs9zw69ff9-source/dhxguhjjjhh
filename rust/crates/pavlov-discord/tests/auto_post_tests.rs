//! Port of `AutoPostTests.cs`: "leaderboards are being deleted then reset when they should
//! just keep updating", and the duplicate money board.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;
use std::convert::Infallible;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_discord::autopost::AutoPost;
use pavlov_discord::component::{ActionRow, Button, ButtonStyle};
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::post::{ChannelPoster, PostEdit};
use tokio::sync::Notify;

#[derive(Default)]
struct Seen {
    sends: usize,
    edits: usize,
    next: u64,
    last_edit_components: Option<Vec<ActionRow>>,
    last_send_components: Option<Vec<ActionRow>>,
}

#[derive(Default)]
struct FakeTarget {
    seen: Mutex<Seen>,
    /// Messages somebody actually removed: the only case that earns a re-post.
    deleted: Mutex<HashSet<u64>>,
    /// The edit cannot be carried out and we CANNOT TELL WHY.
    edits_fail: Mutex<bool>,
    block_sends: Mutex<Option<Arc<Notify>>>,
}

impl FakeTarget {
    fn sends(&self) -> usize {
        self.seen.lock().unwrap().sends
    }
    fn edits(&self) -> usize {
        self.seen.lock().unwrap().edits
    }
}

impl ChannelPoster for FakeTarget {
    fn edit(
        &self,
        _: u64,
        message: u64,
        _: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, PostEdit> {
        Box::pin(async move {
            tokio::task::yield_now().await;
            if *self.edits_fail.lock().unwrap() {
                return PostEdit::Failed;
            }
            if self.deleted.lock().unwrap().contains(&message) {
                return PostEdit::Missing;
            }
            let mut seen = self.seen.lock().unwrap();
            seen.edits += 1;
            seen.last_edit_components = Some(components);
            PostEdit::Edited
        })
    }

    fn send(
        &self,
        _: u64,
        _: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        Box::pin(async move {
            self.seen.lock().unwrap().last_send_components = Some(components);
            let block = self.block_sends.lock().unwrap().clone();
            if let Some(block) = block {
                tokio::time::timeout(Duration::from_secs(5), block.notified())
                    .await
                    .unwrap();
            }
            let mut seen = self.seen.lock().unwrap();
            seen.sends += 1;
            let id = 100 + seen.next;
            seen.next += 1;
            Ok(Some(id))
        })
    }
}

struct Rig {
    _dir: tempfile::TempDir,
    target: Arc<FakeTarget>,
    autopost: Arc<AutoPost>,
}

fn rig() -> Rig {
    let dir = tempfile::tempdir().unwrap();
    let store = Arc::new(SerializedStore::new(Arc::new(
        pavlov_storage::FileKeyValueBackend::new(dir.path()).unwrap(),
    )));
    let target = Arc::new(FakeTarget::default());
    let autopost = Arc::new(AutoPost::new(
        Arc::clone(&target) as Arc<dyn ChannelPoster>,
        store,
    ));
    Rig {
        _dir: dir,
        target,
        autopost,
    }
}

async fn board() -> Result<Option<Embed>, Infallible> {
    Ok(Some(Embed::new().title("Leaderboard").description("rows")))
}

impl Rig {
    async fn post(&self, key: &str, channel: Option<u64>) {
        self.autopost.post(key, channel, board(), Vec::new()).await;
    }
}

#[tokio::test]
async fn buttons_are_reapplied_on_every_edit_not_just_the_first_post() {
    let r = rig();
    let buttons = vec![ActionRow::Buttons(vec![Button::new(
        ButtonStyle::Success,
        "Verify",
        "verify:start",
    )])];

    r.autopost
        .post("verifypanel", Some(1), board(), buttons.clone())
        .await;
    r.autopost
        .post("verifypanel", Some(1), board(), buttons.clone())
        .await;

    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 1);
    let seen = r.target.seen.lock().unwrap();
    assert_eq!(seen.last_send_components.as_ref(), Some(&buttons));
    assert_eq!(seen.last_edit_components.as_ref(), Some(&buttons));
}

#[tokio::test]
async fn a_board_with_no_buttons_passes_none() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    r.post("leaderboard", Some(1)).await;

    let seen = r.target.seen.lock().unwrap();
    assert_eq!(seen.last_send_components.as_deref(), Some(&[][..]));
    assert_eq!(seen.last_edit_components.as_deref(), Some(&[][..]));
}

#[tokio::test]
async fn the_first_post_sends_and_later_ones_edit() {
    let r = rig();
    for _ in 0..3 {
        r.post("leaderboard", Some(1)).await;
    }
    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 2);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn two_overlapping_calls_produce_one_board() {
    let r = rig();
    let block = Arc::new(Notify::new());
    *r.target.block_sends.lock().unwrap() = Some(Arc::clone(&block));

    let (a, b) = (Arc::clone(&r.autopost), Arc::clone(&r.autopost));
    let first =
        tokio::spawn(async move { a.post("leaderboard", Some(1), board(), Vec::new()).await });
    let second =
        tokio::spawn(async move { b.post("leaderboard", Some(1), board(), Vec::new()).await });
    tokio::time::sleep(Duration::from_millis(50)).await;
    *r.target.block_sends.lock().unwrap() = None;
    block.notify_waiters();
    block.notify_one();
    first.await.unwrap();
    second.await.unwrap();

    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 1); // the second call edited what the first posted
}

#[tokio::test]
async fn a_deleted_board_is_re_posted_rather_than_lost_forever() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    r.target.deleted.lock().unwrap().insert(100);

    r.post("leaderboard", Some(1)).await;
    assert_eq!(r.target.sends(), 2);
}

#[tokio::test]
async fn an_edit_that_merely_failed_does_not_produce_a_second_board() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    *r.target.edits_fail.lock().unwrap() = true;
    r.post("leaderboard", Some(1)).await;
    r.post("leaderboard", Some(1)).await;

    assert_eq!(r.target.sends(), 1);
}

#[tokio::test]
async fn the_original_board_is_still_the_one_edited_once_the_failure_passes() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    *r.target.edits_fail.lock().unwrap() = true;
    r.post("leaderboard", Some(1)).await;
    *r.target.edits_fail.lock().unwrap() = false;
    r.post("leaderboard", Some(1)).await;

    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 1);
}

#[tokio::test]
async fn a_first_post_still_happens_when_there_is_nothing_to_edit() {
    let r = rig();
    *r.target.edits_fail.lock().unwrap() = true;
    r.post("leaderboard", Some(1)).await;
    assert_eq!(r.target.sends(), 1);
}

#[tokio::test]
async fn a_failed_build_leaves_the_existing_board_alone() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    r.autopost
        .post(
            "leaderboard",
            Some(1),
            async { Err::<Option<Embed>, _>("no data") },
            Vec::new(),
        )
        .await;

    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 0);
}

#[tokio::test]
async fn a_null_embed_skips_the_cycle_without_clearing_the_board() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    r.autopost
        .post(
            "leaderboard",
            Some(1),
            async { Ok::<_, Infallible>(None) },
            Vec::new(),
        )
        .await;

    assert_eq!(r.target.sends(), 1);
    assert_eq!(r.target.edits(), 0);
}

#[tokio::test]
async fn an_unconfigured_channel_is_a_no_op() {
    let r = rig();
    r.post("leaderboard", None).await;
    assert_eq!(r.target.sends(), 0);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn different_boards_do_not_wait_on_each_other() {
    let r = rig();
    let block = Arc::new(Notify::new());
    *r.target.block_sends.lock().unwrap() = Some(Arc::clone(&block));
    let slow_post = Arc::clone(&r.autopost);
    let slow = tokio::spawn(async move {
        slow_post
            .post("leaderboard", Some(1), board(), Vec::new())
            .await
    });
    tokio::time::sleep(Duration::from_millis(50)).await;

    // The leaderboard is still blocked in its send; the arrests board must not wait on it.
    *r.target.block_sends.lock().unwrap() = None;
    tokio::time::timeout(Duration::from_secs(5), r.post("arrests", Some(2)))
        .await
        .expect("an unrelated board waited on the blocked one");
    block.notify_one();
    slow.await.unwrap();

    assert_eq!(r.target.sends(), 2);
}

#[tokio::test]
async fn forgetting_a_board_makes_the_next_post_create_a_fresh_one() {
    let r = rig();
    r.post("leaderboard", Some(1)).await;
    r.autopost.forget("leaderboard").await.unwrap();
    r.post("leaderboard", Some(1)).await;
    assert_eq!(r.target.sends(), 2);
}

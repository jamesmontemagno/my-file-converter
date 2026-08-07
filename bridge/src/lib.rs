pub mod api;
pub mod command;
pub mod config;
pub mod ffmpeg;
pub mod progress;
pub mod state;
pub mod validation;

use std::time::Duration;
use std::{io, net::SocketAddr, sync::Arc};

use api::{router, AppState};
use config::BridgeConfig;
use ffmpeg::discover_ffmpeg;
use state::JobStore;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");

pub async fn start(config: BridgeConfig) -> io::Result<(SocketAddr, AppState)> {
    api::cleanup_orphaned_job_directories().await?;
    let ffmpeg = discover_ffmpeg();
    let state = AppState {
        config: Arc::new(config),
        ffmpeg,
        jobs: Arc::new(JobStore::new()),
    };
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", state.config.port)).await?;
    let address = listener.local_addr()?;
    let app = router(state.clone());
    api::spawn_cleanup(state.clone());
    tokio::spawn(async move {
        let _ = axum::serve(listener, app).await;
    });
    Ok((address, state))
}

pub async fn shutdown(state: AppState) {
    state.jobs.cancel_all().await;
    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    while state.jobs.has_active_jobs().await && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
    state.jobs.cleanup_all().await;
}

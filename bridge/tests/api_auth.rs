use std::{path::PathBuf, sync::Arc};

use axum::{
    body::{to_bytes, Body},
    http::{header, Request, StatusCode},
};
use localmorph_bridge::{
    api::{router, AppState},
    config::BridgeConfig,
    state::JobStore,
};
use tower::ServiceExt;

fn app() -> axum::Router {
    router(AppState {
        config: Arc::new(BridgeConfig::new(0, "test-token".to_owned())),
        ffmpeg: None,
        jobs: Arc::new(JobStore::new()),
        job_root: PathBuf::from("unused-jobs"),
    })
}

#[tokio::test]
async fn health_requires_an_allowed_origin_and_per_launch_token() {
    let response = app()
        .oneshot(
            Request::builder()
                .uri("/v1/health")
                .header(header::ORIGIN, "https://localmorph.com")
                .header(header::AUTHORIZATION, "Bearer test-token")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();

    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(
        response.headers().get(header::ACCESS_CONTROL_ALLOW_ORIGIN),
        Some(&header::HeaderValue::from_static("https://localmorph.com"))
    );
    let body = to_bytes(response.into_body(), 1024).await.unwrap();
    assert!(std::str::from_utf8(&body)
        .unwrap()
        .contains("\"available\":false"));
}

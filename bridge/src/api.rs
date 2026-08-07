use std::{
    convert::Infallible,
    io,
    path::{Path, PathBuf},
    sync::Arc,
    time::Duration,
};

use axum::{
    body::Body,
    extract::{DefaultBodyLimit, Multipart, Path as AxumPath, State},
    http::{header, HeaderValue, Method, Request, StatusCode},
    middleware::{self, Next},
    response::{
        sse::{Event, Sse},
        IntoResponse, Response,
    },
    routing::{delete, get, post},
    Json, Router,
};
use futures_util::{stream, StreamExt};
use serde::Serialize;
use tokio::{
    io::{AsyncBufReadExt, AsyncRead, AsyncReadExt, AsyncWriteExt, BufReader},
    process::Command,
};
use tokio_stream::wrappers::BroadcastStream;
use uuid::Uuid;

use crate::{
    command::{build_command, output_path},
    config::BridgeConfig,
    ffmpeg::FfmpegBinary,
    progress::ProgressParser,
    state::{JobStatus, JobStore, JobView},
    validation::ConversionRequest,
    VERSION,
};

const MAX_UPLOAD_BYTES: usize = 2 * 1024 * 1024 * 1024;
const MAX_OUTPUT_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_STDERR_BYTES: usize = 16 * 1024;

#[derive(Clone)]
pub struct AppState {
    pub config: Arc<BridgeConfig>,
    pub ffmpeg: Option<FfmpegBinary>,
    pub jobs: Arc<JobStore>,
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/v1/health", get(health))
        .route("/v1/jobs", post(create_job))
        .route("/v1/jobs/:id", get(job_status))
        .route("/v1/jobs/:id/events", get(job_events))
        .route("/v1/jobs/:id", delete(cancel_job))
        .route("/v1/jobs/:id/output", get(download_output))
        .layer(DefaultBodyLimit::max(MAX_UPLOAD_BYTES))
        .with_state(state.clone())
        .layer(middleware::from_fn_with_state(state, auth_and_origin))
}

async fn auth_and_origin(
    State(state): State<AppState>,
    request: Request<Body>,
    next: Next,
) -> Response {
    let origin = match request
        .headers()
        .get(header::ORIGIN)
        .and_then(|value| value.to_str().ok())
    {
        Some(origin) if state.config.allowed_origins.contains(origin) => origin.to_owned(),
        _ => return api_error(StatusCode::FORBIDDEN, "origin is not allowed"),
    };

    if request.method() == Method::OPTIONS {
        let private_network = request
            .headers()
            .get("access-control-request-private-network")
            .is_some_and(|value| value == "true");
        return cors_response(StatusCode::NO_CONTENT, &origin, private_network).into_response();
    }
    let expected = format!("Bearer {}", state.config.token);
    if request
        .headers()
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        != Some(expected.as_str())
    {
        return api_error(StatusCode::UNAUTHORIZED, "missing or invalid bearer token");
    }
    let mut response = next.run(request).await;
    add_cors_headers(response.headers_mut(), &origin, false);
    response
}

fn cors_response(status: StatusCode, origin: &str, private_network: bool) -> Response {
    let mut response = status.into_response();
    add_cors_headers(response.headers_mut(), origin, private_network);
    response
}

fn add_cors_headers(headers: &mut axum::http::HeaderMap, origin: &str, private_network: bool) {
    headers.insert(
        header::ACCESS_CONTROL_ALLOW_ORIGIN,
        HeaderValue::from_str(origin).unwrap(),
    );
    headers.insert(
        header::ACCESS_CONTROL_ALLOW_HEADERS,
        HeaderValue::from_static("authorization, content-type"),
    );
    headers.insert(
        header::ACCESS_CONTROL_ALLOW_METHODS,
        HeaderValue::from_static("GET, POST, DELETE, OPTIONS"),
    );
    headers.insert(
        header::VARY,
        HeaderValue::from_static("Origin, Access-Control-Request-Private-Network"),
    );
    if private_network {
        headers.insert(
            "access-control-allow-private-network",
            HeaderValue::from_static("true"),
        );
    }
}

#[derive(Serialize)]
struct ErrorBody<'a> {
    error: &'a str,
}

fn api_error(status: StatusCode, message: &'static str) -> Response {
    (status, Json(ErrorBody { error: message })).into_response()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Health {
    version: &'static str,
    ffmpeg: FfmpegHealth,
    supported_targets: [&'static str; 6],
}

#[derive(Serialize)]
struct FfmpegHealth {
    available: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    version: Option<String>,
}

async fn health(State(state): State<AppState>) -> Json<Health> {
    Json(Health {
        version: VERSION,
        ffmpeg: FfmpegHealth {
            available: state.ffmpeg.is_some(),
            version: state
                .ffmpeg
                .as_ref()
                .and_then(|binary| binary.version.clone()),
        },
        supported_targets: [
            "video/mp4",
            "video/quicktime",
            "video/webm",
            "image/gif",
            "audio/mpeg",
            "audio/wav",
        ],
    })
}

async fn create_job(State(state): State<AppState>, mut multipart: Multipart) -> Response {
    let Some(ffmpeg) = state.ffmpeg.as_ref().map(|binary| binary.path.clone()) else {
        return api_error(
            StatusCode::SERVICE_UNAVAILABLE,
            "ffmpeg was not found on PATH",
        );
    };
    let id = Uuid::new_v4();
    let directory = job_directory(id);
    let input = directory.join("input");
    let mut cleanup = JobDirectoryGuard::new(directory.clone());
    let (request, uploaded) = match collect_multipart(&mut multipart, &directory, &input).await {
        Ok(parts) => parts,
        Err(response) => return response,
    };
    if request.validate().is_err() {
        return api_error(StatusCode::BAD_REQUEST, "unsupported conversion options");
    }
    if !uploaded {
        return api_error(StatusCode::BAD_REQUEST, "file field is required");
    }
    let output = output_path(&directory, &request);
    let cancellation = state
        .jobs
        .insert(id, directory, output.clone(), request.output_name.clone())
        .await;
    spawn_job(
        state.jobs.clone(),
        ffmpeg,
        input,
        output,
        request,
        id,
        cancellation,
    );
    cleanup.disarm();
    (StatusCode::ACCEPTED, Json(JobCreated { id })).into_response()
}

#[derive(Serialize)]
struct JobCreated {
    id: Uuid,
}

struct JobDirectoryGuard {
    directory: PathBuf,
    retained: bool,
}

impl JobDirectoryGuard {
    fn new(directory: PathBuf) -> Self {
        Self {
            directory,
            retained: true,
        }
    }

    fn disarm(&mut self) {
        self.retained = false;
    }
}

impl Drop for JobDirectoryGuard {
    fn drop(&mut self) {
        if self.retained {
            let _ = std::fs::remove_dir_all(&self.directory);
        }
    }
}

async fn collect_multipart(
    multipart: &mut Multipart,
    directory: &Path,
    input: &Path,
) -> Result<(ConversionRequest, bool), Response> {
    let mut request = None;
    let mut uploaded = false;
    loop {
        let field = match multipart.next_field().await {
            Ok(Some(field)) => field,
            Ok(None) => break,
            Err(_) => return Err(api_error(StatusCode::BAD_REQUEST, "invalid multipart body")),
        };
        match field.name() {
            Some("request") if request.is_none() => {
                let bytes = field
                    .bytes()
                    .await
                    .map_err(|_| api_error(StatusCode::BAD_REQUEST, "invalid request part"))?;
                let parsed = serde_json::from_slice::<ConversionRequest>(&bytes)
                    .map_err(|_| api_error(StatusCode::BAD_REQUEST, "invalid request JSON"))?;
                request = Some(parsed);
            }
            Some("file") if !uploaded => {
                tokio::fs::create_dir_all(directory).await.map_err(|_| {
                    api_error(
                        StatusCode::INTERNAL_SERVER_ERROR,
                        "could not create job directory",
                    )
                })?;
                let mut field = field;
                write_upload(&mut field, input).await.map_err(|_| {
                    api_error(
                        StatusCode::BAD_REQUEST,
                        "file is empty or exceeds upload limit",
                    )
                })?;
                uploaded = true;
            }
            _ => {
                return Err(api_error(
                    StatusCode::BAD_REQUEST,
                    "unexpected or duplicate multipart field",
                ))
            }
        }
    }
    request
        .map(|request| (request, uploaded))
        .ok_or_else(|| api_error(StatusCode::BAD_REQUEST, "request JSON field is required"))
}

async fn write_upload(
    field: &mut axum::extract::multipart::Field<'_>,
    path: &Path,
) -> Result<(), ()> {
    let mut file = tokio::fs::File::create(path).await.map_err(|_| ())?;
    let mut total = 0_usize;
    while let Some(chunk) = field.chunk().await.map_err(|_| ())? {
        total = total.checked_add(chunk.len()).ok_or(())?;
        if total > MAX_UPLOAD_BYTES {
            return Err(());
        }
        file.write_all(&chunk).await.map_err(|_| ())?;
    }
    (total > 0).then_some(()).ok_or(())
}

async fn job_status(State(state): State<AppState>, AxumPath(id): AxumPath<Uuid>) -> Response {
    match state.jobs.view(id).await {
        Some(job) => Json(job).into_response(),
        None => api_error(StatusCode::NOT_FOUND, "job not found"),
    }
}

async fn cancel_job(State(state): State<AppState>, AxumPath(id): AxumPath<Uuid>) -> Response {
    match state.jobs.cancel(id).await {
        Some(_) => StatusCode::ACCEPTED.into_response(),
        None => api_error(StatusCode::NOT_FOUND, "job not found"),
    }
}

async fn job_events(State(state): State<AppState>, AxumPath(id): AxumPath<Uuid>) -> Response {
    let Some((current, receiver)) = state.jobs.subscribe_with_current(id).await else {
        return api_error(StatusCode::NOT_FOUND, "job not found");
    };
    let initial = stream::once(async move { Ok::<Event, Infallible>(event_from_job(&current)) });
    let updates = BroadcastStream::new(receiver).filter_map(|result| async move {
        result
            .ok()
            .map(|job| Ok::<Event, Infallible>(event_from_job(&job)))
    });
    Sse::new(initial.chain(updates)).into_response()
}

fn event_from_job(job: &JobView) -> Event {
    Event::default()
        .event("status")
        .json_data(SseJobEvent::from(job))
        .unwrap_or_else(|_| Event::default().event("status").data("{}"))
}

#[derive(Serialize)]
struct SseJobEvent {
    status: JobStatus,
    progress: u8,
    message: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    detail: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "rawOutput")]
    raw_output: Option<String>,
}

impl From<&JobView> for SseJobEvent {
    fn from(job: &JobView) -> Self {
        let (message, detail) = match job.status {
            JobStatus::Queued => ("Job queued", None),
            JobStatus::Running => ("Conversion in progress", None),
            JobStatus::Completed => ("Conversion completed", None),
            JobStatus::Failed => ("Conversion failed", job.error.clone()),
            JobStatus::Canceled => ("Conversion canceled", None),
        };
        Self {
            status: job.status,
            progress: job.progress_percent.unwrap_or(0),
            message,
            detail,
            raw_output: None,
        }
    }
}

async fn download_output(State(state): State<AppState>, AxumPath(id): AxumPath<Uuid>) -> Response {
    let Some((path, output_name)) = state.jobs.output(id).await else {
        return api_error(StatusCode::NOT_FOUND, "completed output not found");
    };
    match tokio::fs::metadata(&path).await {
        Ok(metadata) if metadata.len() <= MAX_OUTPUT_BYTES => {
            match tokio::fs::File::open(path).await {
                Ok(file) => {
                    let length = metadata.len();
                    let output = stream::try_unfold(file, |mut file| async move {
                        let mut buffer = vec![0; 64 * 1024];
                        let read = file.read(&mut buffer).await?;
                        buffer.truncate(read);
                        Ok::<_, io::Error>((read > 0).then_some((buffer, file)))
                    });
                    let mut response = Body::from_stream(output).into_response();
                    response.headers_mut().insert(
                        header::CONTENT_TYPE,
                        HeaderValue::from_static("application/octet-stream"),
                    );
                    response.headers_mut().insert(
                        header::CONTENT_LENGTH,
                        HeaderValue::from_str(&length.to_string())
                            .expect("output length is a valid header"),
                    );
                    response.headers_mut().insert(
                        header::CONTENT_DISPOSITION,
                        HeaderValue::from_str(&format!("attachment; filename=\"{output_name}\""))
                            .expect("validated output name is a valid header value"),
                    );
                    response
                }
                Err(_) => api_error(StatusCode::NOT_FOUND, "completed output not found"),
            }
        }
        Ok(_) => api_error(
            StatusCode::PAYLOAD_TOO_LARGE,
            "completed output exceeds size limit",
        ),
        Err(_) => api_error(StatusCode::NOT_FOUND, "completed output not found"),
    }
}

fn job_directory(id: Uuid) -> PathBuf {
    job_root().join(id.to_string())
}

fn job_root() -> PathBuf {
    #[cfg(windows)]
    let root = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    #[cfg(not(windows))]
    let root = std::env::var_os("HOME")
        .map(|home| PathBuf::from(home).join(".local").join("share"))
        .unwrap_or_else(|| PathBuf::from("."));
    root.join("LocalMorphBridge").join("jobs")
}

pub async fn cleanup_orphaned_job_directories() -> io::Result<()> {
    match tokio::fs::remove_dir_all(job_root()).await {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
    }
}

fn spawn_job(
    jobs: Arc<JobStore>,
    ffmpeg: PathBuf,
    input: PathBuf,
    output: PathBuf,
    request: ConversionRequest,
    id: Uuid,
    cancellation: Arc<tokio::sync::Notify>,
) {
    tokio::spawn(async move {
        jobs.transition(id, JobStatus::Running, Some(0), None).await;
        if !matches!(
            jobs.view(id).await.map(|job| job.status),
            Some(JobStatus::Running)
        ) {
            return;
        }
        let mut command: Command = build_command(&ffmpeg, &input, &output, &request);
        let mut child = match command.spawn() {
            Ok(child) => child,
            Err(_) => {
                jobs.transition(
                    id,
                    JobStatus::Failed,
                    None,
                    Some("FFmpeg could not be started".into()),
                )
                .await;
                return;
            }
        };
        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        let progress_task = stdout.map(|stdout| {
            let jobs = jobs.clone();
            tokio::spawn(read_progress(stdout, jobs, id))
        });
        let stderr_task = stderr.map(|stderr| tokio::spawn(read_stderr(stderr)));
        let cancel = cancellation.notified();
        tokio::pin!(cancel);
        let result = tokio::select! {
            result = child.wait() => (result, false),
            _ = &mut cancel => {
                let _ = child.kill().await;
                (child.wait().await, true)
            }
        };
        if let Some(task) = progress_task {
            let _ = task.await;
        }
        let stderr = match stderr_task {
            Some(task) => task.await.unwrap_or_default(),
            None => String::new(),
        };
        if result.1 {
            jobs.transition(id, JobStatus::Canceled, None, None).await;
        } else if result.0.map(|status| status.success()).unwrap_or(false)
            && output
                .metadata()
                .map(|metadata| metadata.len() <= MAX_OUTPUT_BYTES)
                .unwrap_or(false)
        {
            jobs.transition(id, JobStatus::Completed, Some(100), None)
                .await;
        } else {
            let _ = tokio::fs::remove_file(&output).await;
            let message = if stderr.is_empty() {
                "FFmpeg conversion failed or exceeded the output size limit".to_owned()
            } else {
                format!(
                    "FFmpeg conversion failed: {}",
                    stderr.lines().last().unwrap_or("unknown error")
                )
            };
            jobs.transition(id, JobStatus::Failed, None, Some(message))
                .await;
        }
    });
}

async fn read_progress(stdout: impl AsyncRead + Unpin, jobs: Arc<JobStore>, id: Uuid) {
    let mut parser = ProgressParser::new(None);
    let mut lines = BufReader::new(stdout).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        if let Some(update) = parser.consume(&line) {
            jobs.progress(id, update.percent).await;
        }
    }
}

async fn read_stderr(stderr: impl AsyncRead + Unpin) -> String {
    let mut reader = BufReader::new(stderr);
    let mut buffer = [0_u8; 4096];
    let mut tail = Vec::new();
    while let Ok(read) = reader.read(&mut buffer).await {
        if read == 0 {
            break;
        }
        tail.extend_from_slice(&buffer[..read]);
        if tail.len() > MAX_STDERR_BYTES {
            let start = tail.len() - MAX_STDERR_BYTES;
            tail.drain(..start);
        }
    }
    String::from_utf8_lossy(&tail).into_owned()
}

pub fn spawn_cleanup(state: AppState) {
    tokio::spawn(async move {
        let ttl = Duration::from_secs(state.config.job_ttl_seconds);
        loop {
            tokio::time::sleep(Duration::from_secs(60)).await;
            state.jobs.cleanup_expired(ttl).await;
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{body::to_bytes, extract::FromRequest, http::Request};
    use tower::ServiceExt;

    fn test_state() -> AppState {
        AppState {
            config: Arc::new(BridgeConfig::new(0, "secret".to_owned())),
            ffmpeg: None,
            jobs: Arc::new(JobStore::new()),
        }
    }

    #[tokio::test]
    async fn rejects_missing_origin_and_token() {
        let response = router(test_state())
            .oneshot(
                Request::builder()
                    .uri("/v1/health")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn accepts_exact_allowed_origin_and_bearer_token() {
        let response = router(test_state())
            .oneshot(
                Request::builder()
                    .uri("/v1/health")
                    .header(header::ORIGIN, "http://localhost:5173")
                    .header(header::AUTHORIZATION, "Bearer secret")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let body = to_bytes(response.into_body(), 1024).await.unwrap();
        assert!(std::str::from_utf8(&body)
            .unwrap()
            .contains("supportedTargets"));
    }

    #[tokio::test]
    async fn rejects_lookalike_origin() {
        let response = router(test_state())
            .oneshot(
                Request::builder()
                    .uri("/v1/health")
                    .header(header::ORIGIN, "https://localmorph.com.evil.example")
                    .header(header::AUTHORIZATION, "Bearer secret")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn allows_private_network_preflight_only_for_an_allowed_origin() {
        let response = router(test_state())
            .oneshot(
                Request::builder()
                    .method(Method::OPTIONS)
                    .uri("/v1/jobs")
                    .header(header::ORIGIN, "http://localhost:5173")
                    .header("access-control-request-private-network", "true")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::NO_CONTENT);
        assert_eq!(
            response
                .headers()
                .get("access-control-allow-private-network"),
            Some(&HeaderValue::from_static("true"))
        );

        let response = router(test_state())
            .oneshot(
                Request::builder()
                    .method(Method::OPTIONS)
                    .uri("/v1/jobs")
                    .header(header::ORIGIN, "https://localmorph.com.evil.example")
                    .header("access-control-request-private-network", "true")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        assert!(response
            .headers()
            .get("access-control-allow-private-network")
            .is_none());
    }

    #[test]
    fn preregistration_guard_removes_an_incomplete_upload_directory() {
        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(format!(".test-incomplete-job-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::write(directory.join("input"), b"incomplete").unwrap();
        drop(JobDirectoryGuard::new(directory.clone()));
        assert!(!directory.exists());
    }

    #[tokio::test]
    async fn multipart_failure_after_writing_input_removes_the_job_directory() {
        let boundary = "incomplete-upload";
        let body = format!(
            "--{boundary}\r\n\
             Content-Disposition: form-data; name=\"file\"; filename=\"input.bin\"\r\n\
             Content-Type: application/octet-stream\r\n\r\n\
             input\r\n\
             --{boundary}\r\n\
             Content-Disposition: form-data; name=\"file\"; filename=\"duplicate.bin\"\r\n\
             Content-Type: application/octet-stream\r\n\r\n\
             duplicate\r\n\
             --{boundary}--\r\n"
        );
        let request = Request::builder()
            .header(
                header::CONTENT_TYPE,
                format!("multipart/form-data; boundary={boundary}"),
            )
            .body(Body::from(body))
            .unwrap();
        let mut multipart = Multipart::from_request(request, &()).await.unwrap();
        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(format!(".test-multipart-job-{}", Uuid::new_v4()));
        let input = directory.join("input");
        let cleanup = JobDirectoryGuard::new(directory.clone());

        assert!(collect_multipart(&mut multipart, &directory, &input)
            .await
            .is_err());
        assert!(input.exists());
        drop(cleanup);
        assert!(!directory.exists());
    }
}

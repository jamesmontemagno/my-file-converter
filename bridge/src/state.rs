use std::{
    collections::HashMap,
    path::PathBuf,
    sync::Arc,
    time::{Duration, SystemTime},
};

use serde::Serialize;
use tokio::sync::{broadcast, Mutex, Notify};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum JobStatus {
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

impl JobStatus {
    pub fn is_terminal(self) -> bool {
        matches!(self, Self::Completed | Self::Failed | Self::Canceled)
    }
}

#[derive(Debug, Clone, Serialize)]
pub struct JobView {
    pub id: Uuid,
    pub status: JobStatus,
    pub progress_percent: Option<u8>,
    pub error: Option<String>,
}

pub struct JobRecord {
    pub view: JobView,
    pub directory: PathBuf,
    pub output: PathBuf,
    pub output_name: String,
    pub terminal_at: Option<SystemTime>,
    pub events: broadcast::Sender<JobView>,
    pub cancellation: Arc<Notify>,
}

pub struct JobStore {
    jobs: Mutex<HashMap<Uuid, JobRecord>>,
}

impl JobStore {
    pub fn new() -> Self {
        Self {
            jobs: Mutex::new(HashMap::new()),
        }
    }

    pub async fn insert(
        &self,
        id: Uuid,
        directory: PathBuf,
        output: PathBuf,
        output_name: String,
    ) -> Arc<Notify> {
        let (events, _) = broadcast::channel(32);
        let cancellation = Arc::new(Notify::new());
        let view = JobView {
            id,
            status: JobStatus::Queued,
            progress_percent: None,
            error: None,
        };
        events.send(view.clone()).ok();
        self.jobs.lock().await.insert(
            id,
            JobRecord {
                view,
                directory,
                output,
                output_name,
                terminal_at: None,
                events,
                cancellation: cancellation.clone(),
            },
        );
        cancellation
    }

    pub async fn view(&self, id: Uuid) -> Option<JobView> {
        self.jobs.lock().await.get(&id).map(|job| job.view.clone())
    }

    pub async fn subscribe(&self, id: Uuid) -> Option<broadcast::Receiver<JobView>> {
        self.subscribe_with_current(id)
            .await
            .map(|(_, receiver)| receiver)
    }

    pub async fn subscribe_with_current(
        &self,
        id: Uuid,
    ) -> Option<(JobView, broadcast::Receiver<JobView>)> {
        self.jobs
            .lock()
            .await
            .get(&id)
            .map(|job| (job.view.clone(), job.events.subscribe()))
    }

    pub async fn output(&self, id: Uuid) -> Option<(PathBuf, String)> {
        let jobs = self.jobs.lock().await;
        let job = jobs.get(&id)?;
        (job.view.status == JobStatus::Completed)
            .then(|| (job.output.clone(), job.output_name.clone()))
    }

    pub async fn cancel(&self, id: Uuid) -> Option<JobView> {
        let mut jobs = self.jobs.lock().await;
        let job = jobs.get_mut(&id)?;
        if !job.view.status.is_terminal() {
            if job.view.status == JobStatus::Queued {
                job.view.status = JobStatus::Canceled;
                job.terminal_at = Some(SystemTime::now());
                job.events.send(job.view.clone()).ok();
            }
            // There is one worker per job. `notify_one` retains a permit when
            // the worker has not reached its cancellation wait yet.
            job.cancellation.notify_one();
        }
        Some(job.view.clone())
    }

    pub async fn transition(
        &self,
        id: Uuid,
        status: JobStatus,
        progress_percent: Option<u8>,
        error: Option<String>,
    ) {
        let mut jobs = self.jobs.lock().await;
        if let Some(job) = jobs.get_mut(&id) {
            if valid_transition(job.view.status, status) {
                job.view.status = status;
                job.view.progress_percent = progress_percent.or(job.view.progress_percent);
                job.view.error = error;
                if status.is_terminal() {
                    job.terminal_at = Some(SystemTime::now());
                }
                job.events.send(job.view.clone()).ok();
            }
        }
    }

    pub async fn progress(&self, id: Uuid, percent: Option<u8>) {
        let mut jobs = self.jobs.lock().await;
        if let Some(job) = jobs.get_mut(&id) {
            if job.view.status == JobStatus::Running {
                job.view.progress_percent = percent.or(job.view.progress_percent);
                job.events.send(job.view.clone()).ok();
            }
        }
    }

    pub async fn cleanup_expired(&self, ttl: Duration) {
        let mut jobs = self.jobs.lock().await;
        let expired: Vec<_> = jobs
            .iter()
            .filter(|(_, job)| {
                job.view.status.is_terminal()
                    && job
                        .terminal_at
                        .is_some_and(|terminal_at| terminal_at.elapsed().unwrap_or_default() >= ttl)
            })
            .map(|(id, job)| (*id, job.directory.clone()))
            .collect();
        for (id, directory) in expired {
            jobs.remove(&id);
            let _ = tokio::fs::remove_dir_all(directory).await;
        }
    }

    pub async fn cancel_all(&self) {
        let mut jobs = self.jobs.lock().await;
        for job in jobs.values_mut() {
            if !job.view.status.is_terminal() {
                if job.view.status == JobStatus::Queued {
                    job.view.status = JobStatus::Canceled;
                    job.terminal_at = Some(SystemTime::now());
                    job.events.send(job.view.clone()).ok();
                }
                job.cancellation.notify_one();
            }
        }
    }

    pub async fn has_active_jobs(&self) -> bool {
        self.jobs
            .lock()
            .await
            .values()
            .any(|job| !job.view.status.is_terminal())
    }

    pub async fn cleanup_all(&self) {
        let directories: Vec<_> = self
            .jobs
            .lock()
            .await
            .drain()
            .map(|(_, job)| job.directory)
            .collect();
        for directory in directories {
            let _ = tokio::fs::remove_dir_all(directory).await;
        }
    }
}

fn valid_transition(from: JobStatus, to: JobStatus) -> bool {
    matches!(
        (from, to),
        (JobStatus::Queued, JobStatus::Running)
            | (JobStatus::Queued, JobStatus::Canceled)
            | (JobStatus::Running, JobStatus::Completed)
            | (JobStatus::Running, JobStatus::Failed)
            | (JobStatus::Running, JobStatus::Canceled)
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_allows_expected_state_changes() {
        assert!(valid_transition(JobStatus::Queued, JobStatus::Running));
        assert!(valid_transition(JobStatus::Running, JobStatus::Completed));
        assert!(!valid_transition(JobStatus::Completed, JobStatus::Running));
        assert!(!valid_transition(JobStatus::Queued, JobStatus::Completed));
    }

    #[tokio::test]
    async fn subscription_returns_terminal_snapshot_without_waiting_for_a_broadcast() {
        let store = JobStore::new();
        let id = Uuid::new_v4();
        store
            .insert(
                id,
                PathBuf::from("unused"),
                PathBuf::from("unused-output"),
                "output".into(),
            )
            .await;
        store
            .transition(id, JobStatus::Running, Some(0), None)
            .await;
        store
            .transition(id, JobStatus::Completed, Some(100), None)
            .await;
        let (current, _) = store.subscribe_with_current(id).await.unwrap();
        assert_eq!(current.status, JobStatus::Completed);
    }
}

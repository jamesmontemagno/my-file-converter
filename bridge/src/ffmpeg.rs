use std::{
    env,
    path::{Path, PathBuf},
    process::Command,
};

#[derive(Clone, Debug)]
pub struct FfmpegBinary {
    pub path: PathBuf,
    pub version: Option<String>,
}

pub fn discover_ffmpeg() -> Option<FfmpegBinary> {
    let path = env::var_os("PATH")?;
    discover_in_paths(env::split_paths(&path)).map(|path| FfmpegBinary {
        version: ffmpeg_version(&path),
        path,
    })
}

pub fn discover_in_paths(paths: impl IntoIterator<Item = PathBuf>) -> Option<PathBuf> {
    let candidates: &[&str] = if cfg!(windows) {
        &["ffmpeg.exe", "ffmpeg"]
    } else {
        &["ffmpeg"]
    };
    paths.into_iter().find_map(|directory| {
        candidates
            .iter()
            .map(|name| directory.join(name))
            .find(|candidate| executable_file(candidate))
    })
}

fn executable_file(path: &Path) -> bool {
    path.is_file()
}

fn ffmpeg_version(path: &Path) -> Option<String> {
    let output = Command::new(path).arg("-version").output().ok()?;
    if !output.status.success() {
        return None;
    }
    String::from_utf8(output.stdout)
        .ok()?
        .lines()
        .next()
        .map(str::to_owned)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_ffmpeg_in_supplied_path() {
        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(format!(".test-ffmpeg-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&directory).unwrap();
        let binary = directory.join(if cfg!(windows) {
            "ffmpeg.exe"
        } else {
            "ffmpeg"
        });
        std::fs::write(&binary, []).unwrap();
        assert_eq!(discover_in_paths(vec![directory.clone()]), Some(binary));
        std::fs::remove_dir_all(directory).unwrap();
    }
}

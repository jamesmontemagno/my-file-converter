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
    if !path.is_file() {
        return false;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;

        return path
            .metadata()
            .map(|metadata| metadata.permissions().mode() & 0o111 != 0)
            .unwrap_or(false);
    }
    #[cfg(not(unix))]
    {
        true
    }
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
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;

            let mut permissions = std::fs::metadata(&binary).unwrap().permissions();
            permissions.set_mode(0o755);
            std::fs::set_permissions(&binary, permissions).unwrap();
        }
        assert_eq!(discover_in_paths(vec![directory.clone()]), Some(binary));
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn rejects_non_executable_ffmpeg_files() {
        use std::os::unix::fs::PermissionsExt;

        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(format!(".test-ffmpeg-no-execute-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&directory).unwrap();
        let binary = directory.join("ffmpeg");
        std::fs::write(&binary, []).unwrap();
        let mut permissions = std::fs::metadata(&binary).unwrap().permissions();
        permissions.set_mode(0o644);
        std::fs::set_permissions(&binary, permissions).unwrap();

        assert_eq!(discover_in_paths(vec![directory.clone()]), None);
        std::fs::remove_dir_all(directory).unwrap();
    }
}

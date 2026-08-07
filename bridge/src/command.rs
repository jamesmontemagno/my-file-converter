use std::path::{Path, PathBuf};

use tokio::process::Command;

use crate::validation::{ChannelMode, ConversionRequest, TargetMime};

pub fn output_path(job_dir: &Path, request: &ConversionRequest) -> PathBuf {
    job_dir.join(format!("output.{}", request.extension()))
}

pub fn build_command(
    ffmpeg: &Path,
    input: &Path,
    output: &Path,
    request: &ConversionRequest,
) -> Command {
    let mut command = Command::new(ffmpeg);
    command
        .arg("-hide_banner")
        .arg("-y")
        .arg("-i")
        .arg(input)
        .arg("-progress")
        .arg("pipe:1")
        .arg("-nostats");

    if let Some(trim_start) = request.media.trim_start {
        command.args(["-ss", &trim_start.to_string()]);
    }
    if let Some(trim_end) = request.media.trim_end {
        command.args(["-to", &trim_end.to_string()]);
    }
    match request.target_mime {
        TargetMime::VideoMp4 | TargetMime::VideoQuicktime => {
            command.args(["-c:v", "libx264", "-preset", "medium"]);
            if let Some(quality) = request.quality {
                command.args(["-crf", &quality_to_crf(quality).to_string()]);
            }
            command.args(["-c:a", "aac"]);
        }
        TargetMime::VideoWebm => {
            command.args(["-c:v", "libvpx-vp9"]);
            if let Some(quality) = request.quality {
                command.args(["-crf", &quality_to_crf(quality).to_string(), "-b:v", "0"]);
            }
            command.args(["-c:a", "libopus"]);
        }
        TargetMime::ImageGif => {
            command.args(["-vf", &gif_filter(request)]);
        }
        TargetMime::AudioMpeg => {
            command.args(["-vn", "-c:a", "libmp3lame"]);
        }
        TargetMime::AudioWav => {
            command.args(["-vn", "-c:a", "pcm_s16le"]);
        }
    }
    match request.media.channel_mode {
        ChannelMode::Mono => {
            command.args(["-ac", "1"]);
        }
        ChannelMode::Stereo => {
            command.args(["-ac", "2"]);
        }
        ChannelMode::Source => {}
    }
    command
        .arg("-fs")
        .arg("2147483648")
        .arg(output)
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped());
    command
}

fn quality_to_crf(quality: u8) -> u8 {
    51 - ((u16::from(quality) * 33 / 100) as u8)
}

fn gif_filter(request: &ConversionRequest) -> String {
    match (request.image.width, request.image.height) {
        (None, None) => "fps=15".to_owned(),
        (Some(width), None) => format!("fps=15,scale={width}:-2"),
        (None, Some(height)) => format!("fps=15,scale=-2:{height}"),
        (Some(width), Some(height)) if request.image.keep_aspect_ratio => {
            format!("fps=15,scale={width}:{height}:force_original_aspect_ratio=decrease")
        }
        (Some(width), Some(height)) => format!("fps=15,scale={width}:{height}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::validation::{ImageOptions, MediaOptions, MediaType, TargetMime};

    #[test]
    fn uses_whitelisted_ffmpeg_arguments() {
        let request = ConversionRequest {
            target_mime: TargetMime::VideoMp4,
            output_name: "output.mp4".into(),
            media_type: MediaType::Video,
            quality: Some(75),
            image: ImageOptions {
                width: Some(1920),
                height: Some(1080),
                keep_aspect_ratio: true,
            },
            media: MediaOptions::default(),
        };
        let command = build_command(
            Path::new("ffmpeg"),
            Path::new("input.bin"),
            Path::new("output.mp4"),
            &request,
        );
        let args: Vec<_> = command
            .as_std()
            .get_args()
            .map(|argument| argument.to_string_lossy().into_owned())
            .collect();
        assert_eq!(args[0..4], ["-hide_banner", "-y", "-i", "input.bin"]);
        assert!(args.windows(2).any(|pair| pair == ["-c:v", "libx264"]));
        assert!(!args.iter().any(|argument| argument.contains(';')));
    }

    #[test]
    fn gif_dimensions_are_preserved_unless_requested() {
        let mut request = ConversionRequest {
            target_mime: TargetMime::ImageGif,
            output_name: "output.gif".into(),
            media_type: MediaType::Image,
            quality: None,
            image: ImageOptions::default(),
            media: MediaOptions::default(),
        };
        assert_eq!(gif_filter(&request), "fps=15");
        request.image.width = Some(640);
        assert_eq!(gif_filter(&request), "fps=15,scale=640:-2");
        request.image.width = None;
        request.image.height = Some(480);
        assert_eq!(gif_filter(&request), "fps=15,scale=-2:480");
    }
}

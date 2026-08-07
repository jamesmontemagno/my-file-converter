use serde::Deserialize;
use thiserror::Error;

#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ConversionRequest {
    pub target_mime: TargetMime,
    pub output_name: String,
    pub media_type: MediaType,
    #[serde(default)]
    pub quality: Option<u8>,
    #[serde(default)]
    pub image: ImageOptions,
    #[serde(default)]
    pub media: MediaOptions,
}

#[derive(Debug, Deserialize, Clone, Copy, PartialEq, Eq)]
pub enum TargetMime {
    #[serde(rename = "video/mp4")]
    VideoMp4,
    #[serde(rename = "video/quicktime")]
    VideoQuicktime,
    #[serde(rename = "video/webm")]
    VideoWebm,
    #[serde(rename = "image/gif")]
    ImageGif,
    #[serde(rename = "audio/mpeg")]
    AudioMpeg,
    #[serde(rename = "audio/wav")]
    AudioWav,
}

#[derive(Debug, Deserialize, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum MediaType {
    Video,
    Audio,
    Image,
}

#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ImageOptions {
    #[serde(default)]
    pub width: Option<u32>,
    #[serde(default)]
    pub height: Option<u32>,
    pub keep_aspect_ratio: bool,
}

impl Default for ImageOptions {
    fn default() -> Self {
        Self {
            width: None,
            height: None,
            keep_aspect_ratio: true,
        }
    }
}

#[derive(Debug, Deserialize, Clone, Default)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct MediaOptions {
    #[serde(default)]
    pub trim_start: Option<f64>,
    #[serde(default)]
    pub trim_end: Option<f64>,
    #[serde(default)]
    pub channel_mode: ChannelMode,
}

#[derive(Debug, Deserialize, Clone, Copy, PartialEq, Eq, Default)]
#[serde(rename_all = "lowercase")]
pub enum ChannelMode {
    Mono,
    Stereo,
    #[default]
    Source,
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum ValidationError {
    #[error("quality must be between 1 and 100")]
    QualityOutOfRange,
    #[error("outputName must be a short, plain filename")]
    UnsafeOutputName,
    #[error("image dimensions must be between 1 and 16384")]
    ImageDimensions,
    #[error("trim values must be non-negative, finite, and ordered")]
    InvalidTrim,
    #[error("mediaType does not match targetMime")]
    TargetMediaMismatch,
}

impl ConversionRequest {
    pub fn validate(&self) -> Result<(), ValidationError> {
        if let Some(quality) = self.quality {
            if !(1..=100).contains(&quality) {
                return Err(ValidationError::QualityOutOfRange);
            }
        }
        if self.output_name.is_empty()
            || self.output_name.len() > 128
            || !self
                .output_name
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        {
            return Err(ValidationError::UnsafeOutputName);
        }
        if self
            .image
            .width
            .is_some_and(|width| !(1..=16_384).contains(&width))
            || self
                .image
                .height
                .is_some_and(|height| !(1..=16_384).contains(&height))
        {
            return Err(ValidationError::ImageDimensions);
        }
        let trim_start = self.media.trim_start.unwrap_or(0.0);
        if !trim_start.is_finite()
            || trim_start < 0.0
            || self
                .media
                .trim_end
                .is_some_and(|end| !end.is_finite() || end <= trim_start)
        {
            return Err(ValidationError::InvalidTrim);
        }
        if !matches!(
            (self.target_mime, self.media_type),
            (
                TargetMime::VideoMp4 | TargetMime::VideoQuicktime | TargetMime::VideoWebm,
                MediaType::Video
            ) | (TargetMime::ImageGif, MediaType::Image)
                | (
                    TargetMime::AudioMpeg | TargetMime::AudioWav,
                    MediaType::Audio
                )
        ) {
            return Err(ValidationError::TargetMediaMismatch);
        }
        Ok(())
    }

    pub fn extension(&self) -> &'static str {
        match self.target_mime {
            TargetMime::VideoMp4 => "mp4",
            TargetMime::VideoQuicktime => "mov",
            TargetMime::VideoWebm => "webm",
            TargetMime::ImageGif => "gif",
            TargetMime::AudioMpeg => "mp3",
            TargetMime::AudioWav => "wav",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_invalid_options() {
        let request = ConversionRequest {
            target_mime: TargetMime::VideoMp4,
            output_name: "../unsafe.mp4".into(),
            media_type: MediaType::Video,
            quality: Some(20),
            image: ImageOptions {
                width: Some(100),
                height: Some(100),
                keep_aspect_ratio: true,
            },
            media: MediaOptions::default(),
        };
        assert_eq!(request.validate(), Err(ValidationError::UnsafeOutputName));
    }

    #[test]
    fn accepts_video_options() {
        let request = ConversionRequest {
            target_mime: TargetMime::VideoMp4,
            output_name: "result.mp4".into(),
            media_type: MediaType::Video,
            quality: Some(75),
            image: ImageOptions {
                width: Some(1920),
                height: Some(1080),
                keep_aspect_ratio: true,
            },
            media: MediaOptions::default(),
        };
        assert!(request.validate().is_ok());
    }

    #[test]
    fn deserializes_the_camel_case_client_contract() {
        let request: ConversionRequest = serde_json::from_str(
            r#"{
                "targetMime":"audio/mpeg",
                "outputName":"voice.mp3",
                "mediaType":"audio",
                "image":{"keepAspectRatio":true},
                "media":{"trimStart":0,"trimEnd":12.5,"channelMode":"mono"}
            }"#,
        )
        .unwrap();
        assert!(request.validate().is_ok());
    }

    #[test]
    fn accepts_omitted_image_dimensions() {
        let request: ConversionRequest = serde_json::from_str(
            r#"{
                "targetMime":"image/gif",
                "outputName":"animation.gif",
                "mediaType":"image",
                "image":{"keepAspectRatio":true},
                "media":{"channelMode":"source"}
            }"#,
        )
        .unwrap();
        assert!(request.validate().is_ok());
    }
}

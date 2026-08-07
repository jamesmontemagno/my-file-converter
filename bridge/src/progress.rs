#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct ProgressUpdate {
    pub percent: Option<u8>,
    pub completed: bool,
}

#[derive(Debug, Default)]
pub struct ProgressParser {
    duration_us: Option<u64>,
    current_us: Option<u64>,
}

impl ProgressParser {
    pub fn new(duration_us: Option<u64>) -> Self {
        Self {
            duration_us,
            current_us: None,
        }
    }

    pub fn consume(&mut self, line: &str) -> Option<ProgressUpdate> {
        let (key, value) = line.split_once('=')?;
        match key {
            "out_time_us" | "out_time_ms" => {
                self.current_us = value.parse().ok();
                Some(self.update(false))
            }
            "progress" if value == "end" => Some(self.update(true)),
            _ => None,
        }
    }

    fn update(&self, completed: bool) -> ProgressUpdate {
        let percent = match (self.current_us, self.duration_us) {
            (_, _) if completed => Some(100),
            (Some(current), Some(duration)) if duration > 0 => {
                Some(((current.saturating_mul(100) / duration).min(99)) as u8)
            }
            _ => None,
        };
        ProgressUpdate { percent, completed }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_ffmpeg_progress_records() {
        let mut parser = ProgressParser::new(Some(10_000_000));
        assert_eq!(
            parser.consume("out_time_us=5000000"),
            Some(ProgressUpdate {
                percent: Some(50),
                completed: false
            })
        );
        assert_eq!(
            parser.consume("progress=end"),
            Some(ProgressUpdate {
                percent: Some(100),
                completed: true
            })
        );
    }
}

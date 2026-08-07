use std::{collections::BTreeSet, env};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use rand::RngCore;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum ConfigError {
    #[error("LOCALMORPH_BRIDGE_PORT must be an unsigned 16-bit port")]
    InvalidPort,
}

#[derive(Clone, Debug)]
pub struct BridgeConfig {
    pub port: u16,
    pub token: String,
    pub allowed_origins: BTreeSet<String>,
    pub job_ttl_seconds: u64,
}

impl BridgeConfig {
    pub fn from_env() -> Result<Self, ConfigError> {
        let port = match env::var("LOCALMORPH_BRIDGE_PORT") {
            Ok(value) => value.parse().map_err(|_| ConfigError::InvalidPort)?,
            Err(_) => 0,
        };
        Ok(Self::new(port, new_token()))
    }

    pub fn new(port: u16, token: String) -> Self {
        Self {
            port,
            token,
            allowed_origins: default_origins(),
            job_ttl_seconds: 60 * 60,
        }
    }
}

pub fn default_origins() -> BTreeSet<String> {
    [
        "https://localmorph.com",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:4173",
        "http://127.0.0.1:4173",
    ]
    .into_iter()
    .map(String::from)
    .collect()
}

fn new_token() -> String {
    let mut bytes = [0_u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    URL_SAFE_NO_PAD.encode(bytes)
}

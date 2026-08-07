use localmorph_bridge::{config::BridgeConfig, shutdown, start, VERSION};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let config = BridgeConfig::from_env()?;
    let (address, state) = start(config).await?;
    println!(
        "LOCALMORPH_BRIDGE={}",
        serde_json::json!({
            "baseUrl": format!("http://{}", address),
            "token": state.config.token,
            "version": VERSION,
        })
    );
    tokio::signal::ctrl_c().await?;
    shutdown(state).await;
    Ok(())
}

//! Read the same application dispatcher used by the desktop; no private catalog
//! parser or second discovery authority is introduced by the CLI client.
use bridge_app::observations::{ProfilesObservations, ReadOnlyApplication};
use bridge_contracts::v1::decode_request;
use bridge_engine::host::LocalHost;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = std::env::args_os()
        .nth(1)
        .ok_or("native input root required")?;
    let mut host =
        ReadOnlyApplication::new(ProfilesObservations::open(std::path::Path::new(&root)))?;
    let request=decode_request(br#"{"protocolVersion":1,"requestId":"10000000-0000-4000-8000-000000000001","body":{"type":"query","query":{"name":"snapshot","input":{}}}}"#)?;
    let reply = host.dispatch(request)?;
    println!("{}", serde_json::to_string_pretty(reply.as_inner())?);
    Ok(())
}

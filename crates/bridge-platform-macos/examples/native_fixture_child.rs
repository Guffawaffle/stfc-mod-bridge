//! Qualification-only fixed owned child. No shell, path, account, catalog,
//! keychain, configuration, network or game operation. Native fixtures bind the
//! compiled binary digest and private owning path before executing this helper.
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
fn main() {
    use std::io::Read;
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 3
        || args[1] != "bridge-native-child-v1"
        || args[2].len() != 32
        || !args[2]
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        std::process::exit(64);
    }
    let mut command = [0; 1];
    match std::io::stdin().read_exact(&mut command) {
        Ok(()) if command[0] == b'q' => (),
        _ => std::process::exit(65),
    }
}

#[cfg(not(all(target_os = "macos", target_arch = "aarch64")))]
fn main() {
    std::process::exit(64);
}

//! Fallible clock and entropy providers. Only Apple Silicon macOS calls native
//! APIs; other hosts refuse without substituting a clock or random source.
//!
//! Suspend policy: elapsed time includes system sleep. Continuous Mach ticks
//! have an arbitrary boot-local origin; these milliseconds are not Unix time
//! and must not be persisted as a deadline for a later boot or host epoch.
use bridge_domain::platform::{PlatformError, PlatformErrorCode};

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
#[repr(C)]
struct MachTimebaseInfo {
    numer: u32,
    denom: u32,
}

// Apple <mach/mach_time.h> declares two uint32_t fields and kern_return_t
// (32-bit signed). The pinned libc 0.2.190 agrees on that ABI but does not
// declare mach_continuous_time; declaring both here avoids its deprecated API.
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
#[link(name = "System")]
unsafe extern "C" {
    fn mach_timebase_info(info: *mut MachTimebaseInfo) -> i32;
    fn mach_continuous_time() -> u64;
}

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
#[link(name = "Security", kind = "framework")]
unsafe extern "C" {
    static kSecRandomDefault: *const std::ffi::c_void;
    fn SecRandomCopyBytes(
        random: *const std::ffi::c_void,
        count: usize,
        bytes: *mut std::ffi::c_void,
    ) -> i32;
}

/// Monotonic milliseconds with system-sleep time included, rounded down.
///
/// No wall-clock, uptime or cached successful observation substitutes for a
/// failed timebase call, invalid ratio or unrepresentable conversion.
/// See <https://developer.apple.com/documentation/kernel/1646199-mach_continuous_time>.
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub fn monotonic_millis() -> Result<u64, PlatformError> {
    read_monotonic_with(
        || {
            let mut info = MachTimebaseInfo { numer: 0, denom: 0 };
            // SAFETY: exactly sized, initialized writable ABI storage. The
            // native status is checked before any returned fields are used.
            let status = unsafe { mach_timebase_info(&raw mut info) };
            (status, info.numer, info.denom)
        },
        || {
            // SAFETY: argument-free read of the continuous system clock.
            unsafe { mach_continuous_time() }
        },
    )
}

#[cfg(not(all(target_os = "macos", target_arch = "aarch64")))]
pub fn monotonic_millis() -> Result<u64, PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
}

/// Return exactly sixteen bytes from Security's default cryptographic source.
///
/// A failed native call may partially fill the initialized buffer. Discarded
/// Rust-owned output is overwritten before returning an error; no fallback or
/// automatic retry occurs. Caller-owned successful output remains its custody.
/// See <https://developer.apple.com/documentation/security/secrandomcopybytes(_:_:_:)>.
#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
pub fn secure_random_16() -> Result<[u8; 16], PlatformError> {
    random_with(|output| {
        // SAFETY: the framework supplies its default source; the initialized,
        // exclusively borrowed array has exactly the requested writable size.
        unsafe { SecRandomCopyBytes(kSecRandomDefault, output.len(), output.as_mut_ptr().cast()) }
    })
}

#[cfg(not(all(target_os = "macos", target_arch = "aarch64")))]
pub fn secure_random_16() -> Result<[u8; 16], PlatformError> {
    Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn native_failure(status: i32) -> PlatformError {
    PlatformError {
        code: PlatformErrorCode::NativeFailure,
        native_code: Some(i64::from(status)),
    }
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn read_monotonic_with(
    timebase: impl FnOnce() -> (i32, u32, u32),
    ticks: impl FnOnce() -> u64,
) -> Result<u64, PlatformError> {
    let (status, numer, denom) = timebase();
    if status != 0 {
        return Err(native_failure(status));
    }
    if numer == 0 || denom == 0 {
        return Err(PlatformError::new(PlatformErrorCode::NativeFailure));
    }
    ticks_to_millis(ticks(), numer, denom)
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn ticks_to_millis(ticks: u64, numer: u32, denom: u32) -> Result<u64, PlatformError> {
    if numer == 0 || denom == 0 {
        return Err(PlatformError::new(PlatformErrorCode::NativeFailure));
    }
    let millis = u128::from(ticks)
        .checked_mul(u128::from(numer))
        .and_then(|value| value.checked_div(u128::from(denom)))
        .and_then(|value| value.checked_div(1_000_000))
        .ok_or_else(|| PlatformError::new(PlatformErrorCode::TooLarge))?;
    u64::try_from(millis).map_err(|_| PlatformError::new(PlatformErrorCode::TooLarge))
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn random_with(fill: impl FnOnce(&mut [u8; 16]) -> i32) -> Result<[u8; 16], PlatformError> {
    let mut output = [0; 16];
    let status = fill(&mut output);
    finish_random(status, &mut output)
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn finish_random(status: i32, output: &mut [u8; 16]) -> Result<[u8; 16], PlatformError> {
    if status != 0 {
        wipe(output);
        return Err(native_failure(status));
    }
    let result = *output;
    wipe(output);
    Ok(result)
}

#[cfg(any(test, all(target_os = "macos", target_arch = "aarch64")))]
fn wipe(output: &mut [u8; 16]) {
    for byte in output {
        // SAFETY: each byte is exclusively borrowed and live. Volatile writes
        // keep the discarded output wipe from dead-store elimination.
        unsafe { std::ptr::write_volatile(byte, 0) };
    }
    std::sync::atomic::compiler_fence(std::sync::atomic::Ordering::SeqCst);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rational_timebase_converts_ticks_to_milliseconds() {
        assert_eq!(ticks_to_millis(500_000, 125, 3), Ok(20));
    }

    #[test]
    fn conversion_floors_submillisecond_values_and_accepts_zero_ticks() {
        assert_eq!(ticks_to_millis(0, 125, 3), Ok(0));
        assert_eq!(ticks_to_millis(999_999, 1, 1), Ok(0));
        assert_eq!(ticks_to_millis(1_000_000, 1, 1), Ok(1));
        assert_eq!(ticks_to_millis(1_999_999, 1, 1), Ok(1));
    }

    #[test]
    fn conversion_preserves_wide_intermediate_values() {
        assert_eq!(
            ticks_to_millis(u64::MAX, u32::MAX, u32::MAX),
            Ok(u64::MAX / 1_000_000),
        );
    }

    #[test]
    fn invalid_timebase_never_produces_a_timestamp() {
        for (numer, denom) in [(0, 1), (1, 0), (0, 0)] {
            assert_eq!(
                ticks_to_millis(1, numer, denom),
                Err(PlatformError::new(PlatformErrorCode::NativeFailure)),
            );
            assert!(
                read_monotonic_with(|| (0, numer, denom), || panic!("clock must not be read"))
                    .is_err(),
            );
        }
    }

    #[test]
    fn unrepresentable_milliseconds_are_refused() {
        assert_eq!(
            ticks_to_millis(u64::MAX, u32::MAX, 1),
            Err(PlatformError::new(PlatformErrorCode::TooLarge)),
        );
    }

    #[test]
    fn native_timebase_failure_never_reads_ticks_or_uses_partial_output() {
        assert_eq!(
            read_monotonic_with(|| (5, 125, 3), || panic!("clock must not be read")),
            Err(native_failure(5)),
        );
    }

    #[test]
    fn random_buffer_is_initialized_before_fill_and_success_is_returned_exactly() {
        assert_eq!(
            random_with(|output| {
                assert_eq!(*output, [0; 16]);
                *output = [0xa5; 16];
                0
            }),
            Ok([0xa5; 16]),
        );
        // Raw entropy is not a UUID or domain identifier. A successful zero
        // sample is valid; identifier policy belongs to the owning caller.
        assert_eq!(random_with(|_| 0), Ok([0; 16]));
    }

    #[test]
    fn random_failure_wipes_partial_output_and_preserves_native_status() {
        for status in [-50, 1] {
            let mut output = [0xa5; 16];
            assert_eq!(
                finish_random(status, &mut output),
                Err(native_failure(status))
            );
            assert_eq!(output, [0; 16]);
        }
    }

    #[test]
    fn successful_random_call_wipes_the_discarded_local_copy() {
        let mut output = [0x5a; 16];
        assert_eq!(finish_random(0, &mut output), Ok([0x5a; 16]));
        assert_eq!(output, [0; 16]);
    }

    #[cfg(not(all(target_os = "macos", target_arch = "aarch64")))]
    #[test]
    fn foreign_host_refuses_both_native_providers() {
        let expected = Err(PlatformError::new(PlatformErrorCode::UnsupportedHost));
        assert_eq!(monotonic_millis(), expected);
        assert_eq!(
            secure_random_16(),
            Err(PlatformError::new(PlatformErrorCode::UnsupportedHost)),
        );
    }

    #[cfg(all(target_os = "macos", target_arch = "aarch64"))]
    #[test]
    #[ignore = "explicit real Apple Silicon provider qualification; does not test system suspend"]
    fn native_continuous_milliseconds_are_readable_and_advance() {
        let before = monotonic_millis().expect("native timebase and clock must succeed");
        std::thread::sleep(std::time::Duration::from_millis(5));
        let after = monotonic_millis().expect("native timebase and clock must succeed");
        assert!(after > before);
    }

    #[cfg(all(target_os = "macos", target_arch = "aarch64"))]
    #[test]
    #[ignore = "explicit real Apple Silicon Security provider qualification; no statistical claim"]
    fn native_secure_random_returns_an_owned_sample_without_logging_it() {
        let mut output = secure_random_16().expect("native Security random source must succeed");
        wipe(&mut output);
    }
}

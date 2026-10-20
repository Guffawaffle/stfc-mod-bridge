//! Native time and entropy for the owning application's bootstrap. These
//! observations create no catalog, operation identity, permission or exclusion.

use bridge_domain::platform::{PlatformError, PlatformErrorCode};
use windows::Win32::Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom};
use windows::Win32::System::SystemInformation::GetTickCount64;

/// Milliseconds since this Windows boot, including sleep and hibernation.
///
/// Suspend time deliberately counts toward preparation expiry. This is neither
/// UTC nor a persisted epoch: compare readings only within the current host
/// lifetime, and use checked arithmetic when constructing deadlines. The system
/// timer's typical 10-16 ms resolution permits equal consecutive readings.
/// GetTickCount64 has no failure sentinel; zero is a valid uptime reading.
/// See https://learn.microsoft.com/en-us/windows/win32/sysinfo/windows-time.
pub fn monotonic_millis() -> Result<u64, PlatformError> {
    crate::require_windows_x64()?;
    // SAFETY: GetTickCount64 takes no pointers or handles and is available on
    // the supported Windows host. No wall-clock or alternate-timer fallback.
    Ok(unsafe { GetTickCount64() })
}

/// Exactly 16 owned bytes from Windows' system-preferred random number source.
///
/// This function does not encode a UUID, establish uniqueness or choose an
/// identity. Any nonzero native status refuses the sample, with discarded output
/// wiped before return. No clock, seed, prior bytes or alternate RNG is used.
/// See https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptgenrandom.
pub fn secure_random_16() -> Result<[u8; 16], PlatformError> {
    crate::require_windows_x64()?;
    fill_random_16(|bytes| {
        // SAFETY: None is the required NULL algorithm handle for the preferred
        // RNG flag. The initialized, uniquely borrowed 16-byte buffer remains
        // valid for this synchronous call; the pinned binding supplies its length.
        unsafe { BCryptGenRandom(None, bytes, BCRYPT_USE_SYSTEM_PREFERRED_RNG) }.0
    })
}

fn fill_random_16(generate: impl FnOnce(&mut [u8; 16]) -> i32) -> Result<[u8; 16], PlatformError> {
    let mut bytes = [0; 16];
    let status = generate(&mut bytes);
    finish_random_16(&mut bytes, status)?;
    Ok(bytes)
}

fn finish_random_16(bytes: &mut [u8; 16], status: i32) -> Result<(), PlatformError> {
    if status == 0 {
        return Ok(());
    }
    for byte in bytes {
        // SAFETY: each byte is uniquely borrowed and valid. Volatile stores
        // preserve clearing of partial native output before it is discarded.
        unsafe { std::ptr::write_volatile(byte, 0) };
    }
    std::sync::atomic::compiler_fence(std::sync::atomic::Ordering::SeqCst);
    Err(PlatformError {
        code: PlatformErrorCode::NativeFailure,
        native_code: Some(i64::from(status)),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    #[test]
    fn random_fill_uses_initialized_owned_bytes_once_and_preserves_non_uuid_bits() {
        let calls = Cell::new(0);
        let expected = std::array::from_fn(|index| index as u8);
        let actual = fill_random_16(|bytes| {
            calls.set(calls.get() + 1);
            assert_eq!(*bytes, [0; 16]);
            *bytes = expected;
            0
        })
        .unwrap();
        assert_eq!(calls.get(), 1);
        assert_eq!(actual, expected);
        // UUID version/variant bits are deliberately not imposed by this port.
        assert_eq!(actual[6], 6);
        assert_eq!(actual[8], 8);
    }

    #[test]
    fn every_nonzero_rng_status_refuses_and_wipes_partial_or_complete_output() {
        for status in [i32::MIN, -1, 1, 0x4000_0000, i32::MAX] {
            for written in [0, 7, 16] {
                let mut bytes = [0; 16];
                bytes[..written].fill(0xa5);
                let refusal = finish_random_16(&mut bytes, status).unwrap_err();
                assert_eq!(bytes, [0; 16]);
                assert_eq!(refusal.code, PlatformErrorCode::NativeFailure);
                assert_eq!(refusal.native_code, Some(i64::from(status)));
                assert_eq!(refusal.to_string(), "platform observation NativeFailure");
            }
        }
    }

    #[test]
    fn failed_rng_call_does_not_retry_or_return_partially_written_bytes() {
        let calls = Cell::new(0);
        let result = fill_random_16(|bytes| {
            calls.set(calls.get() + 1);
            assert_eq!(*bytes, [0; 16]);
            bytes[..7].fill(0xa5);
            -1
        });
        assert_eq!(calls.get(), 1);
        assert_eq!(
            result,
            Err(PlatformError {
                code: PlatformErrorCode::NativeFailure,
                native_code: Some(-1),
            })
        );
    }

    #[test]
    fn native_monotonic_reading_is_bracketed_by_windows_uptime_in_milliseconds() {
        // SAFETY: parameterless observations on the supported native test host.
        let before = unsafe { GetTickCount64() };
        let observed = monotonic_millis().unwrap();
        // SAFETY: same parameterless observation after the provider call.
        let after = unsafe { GetTickCount64() };
        assert!(before <= observed && observed <= after);
    }

    #[test]
    fn native_preferred_rng_returns_bounded_owned_raw_bytes() {
        // Native success is observed without printing bytes or claiming a
        // distribution, UUID format, nonzero output or uniqueness across samples.
        for _ in 0..4 {
            let bytes = secure_random_16().unwrap();
            assert_eq!(bytes.len(), 16);
        }
    }
}

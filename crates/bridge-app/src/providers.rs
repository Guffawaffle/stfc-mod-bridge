//! Native host inputs with closed failures and no alternate clock or entropy.
//!
//! These providers create values only. Their successful observations do not
//! establish durable host identity, permission, exclusions or native custody.

use bridge_contracts::v1::{
    HostEpoch, InvalidPrimitive, OperationId, PlanId, StreamId, UtcTimestamp,
};
use bridge_domain::platform::{PlatformError, PlatformErrorCode};
use bridge_engine::operations::{ClockReading, HostClock, IdentitySource, ProviderFailure};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use time::{OffsetDateTime, SignedDuration, format_description::well_known::Rfc3339};

#[cfg(all(target_os = "macos", target_arch = "aarch64"))]
use bridge_platform_macos::providers as native;
#[cfg(all(target_os = "windows", target_arch = "x86_64"))]
use bridge_platform_windows::providers as native;

type MonotonicSample = fn() -> Result<u64, PlatformError>;
type EntropySample = fn() -> Result<[u8; 16], PlatformError>;

/// A native elapsed clock paired with one UTC wall observation per sample.
///
/// Suspension counts toward elapsed time on the supported native adapters.
/// UTC is evidence, not the authority for preparation expiry. Deadlines use
/// their supplied sample and never observe either clock again.
#[derive(Debug)]
pub struct NativeClock {
    sources: ClockSources<MonotonicSample, fn() -> SystemTime>,
}

impl NativeClock {
    pub const fn new() -> Self {
        Self {
            sources: ClockSources {
                monotonic: native_monotonic,
                wall: SystemTime::now,
            },
        }
    }
}

impl Default for NativeClock {
    fn default() -> Self {
        Self::new()
    }
}

impl HostClock for NativeClock {
    fn now(&self) -> Result<ClockReading, ProviderFailure> {
        self.sources.now()
    }

    fn deadline(
        &self,
        sampled: &ClockReading,
        lifetime_millis: u64,
    ) -> Result<ClockReading, ProviderFailure> {
        self.sources.deadline(sampled, lifetime_millis)
    }
}

#[derive(Debug)]
struct ClockSources<M, W> {
    monotonic: M,
    wall: W,
}

impl<M, W> ClockSources<M, W>
where
    M: Fn() -> Result<u64, PlatformError>,
    W: Fn() -> SystemTime,
{
    fn now(&self) -> Result<ClockReading, ProviderFailure> {
        let monotonic_millis = (self.monotonic)().map_err(|failure| match failure.code {
            PlatformErrorCode::TooLarge => ProviderFailure::ClockRange,
            _ => ProviderFailure::ClockUnavailable,
        })?;
        // A failed native elapsed observation must not sample the wall clock.
        let nanos = signed_unix_nanos((self.wall)())?;
        Ok(ClockReading {
            monotonic_millis,
            utc: utc_from_unix_nanos(nanos)?,
        })
    }

    fn deadline(
        &self,
        sampled: &ClockReading,
        lifetime_millis: u64,
    ) -> Result<ClockReading, ProviderFailure> {
        let monotonic_millis = sampled
            .monotonic_millis
            .checked_add(lifetime_millis)
            .ok_or(ProviderFailure::DeadlineOverflow)?;
        let utc = OffsetDateTime::parse(sampled.utc.as_str(), &Rfc3339)
            .map_err(|_| ProviderFailure::DeadlineOverflow)?;
        let lifetime = SignedDuration::try_from(Duration::from_millis(lifetime_millis))
            .map_err(|_| ProviderFailure::DeadlineOverflow)?;
        let utc = utc
            .checked_add(lifetime)
            .ok_or(ProviderFailure::DeadlineOverflow)?;
        Ok(ClockReading {
            monotonic_millis,
            utc: contract_utc(utc, ProviderFailure::DeadlineOverflow)?,
        })
    }
}

fn signed_unix_nanos(wall: SystemTime) -> Result<i128, ProviderFailure> {
    let (elapsed, negative) = match wall.duration_since(UNIX_EPOCH) {
        Ok(elapsed) => (elapsed, false),
        Err(before) => (before.duration(), true),
    };
    signed_elapsed_nanos(elapsed, negative)
}

fn signed_elapsed_nanos(elapsed: Duration, negative: bool) -> Result<i128, ProviderFailure> {
    let nanos = i128::try_from(elapsed.as_nanos()).map_err(|_| ProviderFailure::ClockRange)?;
    if negative {
        nanos.checked_neg().ok_or(ProviderFailure::ClockRange)
    } else {
        Ok(nanos)
    }
}

fn utc_from_unix_nanos(nanos: i128) -> Result<UtcTimestamp, ProviderFailure> {
    let utc = OffsetDateTime::from_unix_timestamp_nanos(nanos)
        .map_err(|_| ProviderFailure::ClockRange)?;
    contract_utc(utc, ProviderFailure::ClockRange)
}

fn contract_utc(
    utc: OffsetDateTime,
    failure: ProviderFailure,
) -> Result<UtcTimestamp, ProviderFailure> {
    let encoded = utc.format(&Rfc3339).map_err(|_| failure)?;
    UtcTimestamp::new(encoded).map_err(|_| failure)
}

/// Independent native entropy for each host, stream, plan or operation ID.
///
/// Every call makes one sixteen-byte native draw, sets only UUID v4/variant
/// bits, and validates the canonical lowercase value. No uniqueness assertion,
/// cache, retry, seed or alternate random source is supplied here.
#[derive(Debug)]
pub struct NativeIdentitySource {
    source: Identities<EntropySample>,
}

impl NativeIdentitySource {
    pub const fn new() -> Self {
        Self {
            source: Identities {
                draw: native_entropy,
            },
        }
    }

    pub fn host_epoch(&mut self) -> Result<HostEpoch, ProviderFailure> {
        self.source.host_epoch()
    }

    pub fn stream_id(&mut self) -> Result<StreamId, ProviderFailure> {
        self.source.stream_id()
    }
}

impl Default for NativeIdentitySource {
    fn default() -> Self {
        Self::new()
    }
}

impl IdentitySource for NativeIdentitySource {
    fn plan_id(&mut self) -> Result<PlanId, ProviderFailure> {
        self.source.plan_id()
    }

    fn operation_id(&mut self) -> Result<OperationId, ProviderFailure> {
        self.source.operation_id()
    }
}

#[derive(Debug)]
struct Identities<R> {
    draw: R,
}

impl<R: FnMut() -> Result<[u8; 16], PlatformError>> Identities<R> {
    fn host_epoch(&mut self) -> Result<HostEpoch, ProviderFailure> {
        self.identity(HostEpoch::new)
    }

    fn stream_id(&mut self) -> Result<StreamId, ProviderFailure> {
        self.identity(StreamId::new)
    }

    fn plan_id(&mut self) -> Result<PlanId, ProviderFailure> {
        self.identity(PlanId::new)
    }

    fn operation_id(&mut self) -> Result<OperationId, ProviderFailure> {
        self.identity(OperationId::new)
    }

    fn identity<T>(
        &mut self,
        validate: impl FnOnce(String) -> Result<T, InvalidPrimitive>,
    ) -> Result<T, ProviderFailure> {
        let bytes = (self.draw)().map_err(|_| ProviderFailure::EntropyUnavailable)?;
        validate(uuid_v4(bytes)).map_err(|_| ProviderFailure::InvalidIdentity)
    }
}

fn uuid_v4(mut bytes: [u8; 16]) -> String {
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = String::with_capacity(36);
    for (index, byte) in bytes.into_iter().enumerate() {
        if matches!(index, 4 | 6 | 8 | 10) {
            encoded.push('-');
        }
        encoded.push(char::from(HEX[usize::from(byte >> 4)]));
        encoded.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    encoded
}

fn native_monotonic() -> Result<u64, PlatformError> {
    #[cfg(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    ))]
    {
        native::monotonic_millis()
    }
    #[cfg(not(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    )))]
    {
        Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
    }
}

fn native_entropy() -> Result<[u8; 16], PlatformError> {
    #[cfg(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    ))]
    {
        native::secure_random_16()
    }
    #[cfg(not(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    )))]
    {
        Err(PlatformError::new(PlatformErrorCode::UnsupportedHost))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::{Cell, RefCell};

    const PLATFORM_FAILURES: [PlatformErrorCode; 13] = [
        PlatformErrorCode::UnsupportedHost,
        PlatformErrorCode::FeatureUnavailable,
        PlatformErrorCode::InvalidInput,
        PlatformErrorCode::Missing,
        PlatformErrorCode::AccessDenied,
        PlatformErrorCode::LinkOrReparsePoint,
        PlatformErrorCode::WrongKind,
        PlatformErrorCode::IdentityChanged,
        PlatformErrorCode::ProcessExited,
        PlatformErrorCode::Busy,
        PlatformErrorCode::TooLarge,
        PlatformErrorCode::NativeFailure,
        PlatformErrorCode::UnknownObservation,
    ];

    fn reading(monotonic_millis: u64, utc: &str) -> ClockReading {
        ClockReading {
            monotonic_millis,
            utc: UtcTimestamp::new(utc).unwrap(),
        }
    }

    #[test]
    fn clock_samples_elapsed_then_wall_once_and_accepts_zero_elapsed() {
        let order = RefCell::new(Vec::new());
        let source = ClockSources {
            monotonic: || {
                order.borrow_mut().push("elapsed");
                Ok(0)
            },
            wall: || {
                order.borrow_mut().push("wall");
                UNIX_EPOCH
            },
        };
        let observed = source.now().unwrap();
        assert_eq!(*order.borrow(), ["elapsed", "wall"]);
        assert_eq!(observed.monotonic_millis, 0);
        assert_eq!(observed.utc.as_str(), "1970-01-01T00:00:00Z");
    }

    #[test]
    fn native_clock_failures_refuse_before_wall_sampling_with_closed_mapping() {
        for code in PLATFORM_FAILURES {
            let calls = Cell::new(0);
            let source = ClockSources {
                monotonic: || {
                    calls.set(calls.get() + 1);
                    Err(PlatformError {
                        code,
                        native_code: Some(-913),
                    })
                },
                wall: || panic!("native refusal must precede wall observation"),
            };
            let expected = if code == PlatformErrorCode::TooLarge {
                ProviderFailure::ClockRange
            } else {
                ProviderFailure::ClockUnavailable
            };
            assert_eq!(source.now().unwrap_err(), expected);
            assert_eq!(calls.get(), 1);
        }
    }

    #[test]
    fn wall_conversion_preserves_epoch_and_signed_fractional_nanoseconds() {
        let cases = [
            (UNIX_EPOCH, 0, "1970-01-01T00:00:00Z"),
            (
                UNIX_EPOCH
                    .checked_add(Duration::new(1, 123_456_700))
                    .unwrap(),
                1_123_456_700,
                "1970-01-01T00:00:01.1234567Z",
            ),
            (
                UNIX_EPOCH.checked_sub(Duration::from_nanos(100)).unwrap(),
                -100,
                "1969-12-31T23:59:59.9999999Z",
            ),
            (
                UNIX_EPOCH
                    .checked_sub(Duration::new(1, 250_000_000))
                    .unwrap(),
                -1_250_000_000,
                "1969-12-31T23:59:58.75Z",
            ),
        ];
        for (wall, nanos, expected) in cases {
            assert_eq!(signed_unix_nanos(wall), Ok(nanos));
            let source = ClockSources {
                monotonic: || Ok(17),
                wall: || wall,
            };
            let observed = source.now().unwrap();
            assert_eq!(observed.monotonic_millis, 17);
            assert_eq!(observed.utc.as_str(), expected);
        }
        // SystemTime has host-dependent resolution (100 ns on Windows).
        // Exercise the conversion itself with all nine fractional digits.
        for (elapsed, negative, nanos, expected) in [
            (
                Duration::from_nanos(1),
                true,
                -1,
                "1969-12-31T23:59:59.999999999Z",
            ),
            (
                Duration::new(1, 123_456_789),
                false,
                1_123_456_789,
                "1970-01-01T00:00:01.123456789Z",
            ),
        ] {
            assert_eq!(signed_elapsed_nanos(elapsed, negative), Ok(nanos));
            assert_eq!(utc_from_unix_nanos(nanos).unwrap().as_str(), expected);
        }
    }

    #[test]
    fn wall_conversion_accepts_contract_year_boundaries_and_refuses_outside_them() {
        // Keep SystemTime fixtures inside Windows' FILETIME range. Contract
        // year 0000 and the nanosecond edges are tested directly below.
        let earliest = UNIX_EPOCH
            .checked_sub(Duration::from_secs(11_644_473_600))
            .unwrap();
        let latest = UNIX_EPOCH
            .checked_add(Duration::new(253_402_300_799, 999_999_900))
            .unwrap();
        for (wall, expected) in [
            (earliest, "1601-01-01T00:00:00Z"),
            (latest, "9999-12-31T23:59:59.9999999Z"),
        ] {
            let source = ClockSources {
                monotonic: || Ok(1),
                wall: || wall,
            };
            assert_eq!(source.now().unwrap().utc.as_str(), expected);
        }
        let calls = Cell::new(0);
        let outside = latest.checked_add(Duration::from_nanos(100)).unwrap();
        let source = ClockSources {
            monotonic: || Ok(1),
            wall: || {
                calls.set(calls.get() + 1);
                outside
            },
        };
        assert_eq!(source.now().unwrap_err(), ProviderFailure::ClockRange);
        assert_eq!(calls.get(), 1);
        assert_eq!(
            utc_from_unix_nanos(-62_167_219_200_000_000_000)
                .unwrap()
                .as_str(),
            "0000-01-01T00:00:00Z"
        );
        assert_eq!(
            utc_from_unix_nanos(253_402_300_799_999_999_999)
                .unwrap()
                .as_str(),
            "9999-12-31T23:59:59.999999999Z"
        );
        for nanos in [
            i128::MIN,
            i128::MAX,
            -62_167_219_200_000_000_001,
            253_402_300_800_000_000_000,
        ] {
            assert_eq!(utc_from_unix_nanos(nanos), Err(ProviderFailure::ClockRange));
        }
    }

    #[test]
    fn deadline_preserves_nanoseconds_through_second_day_leap_and_year_rollovers() {
        let clock = NativeClock::new();
        for (utc, millis, expected) in [
            (
                "1970-01-01T00:00:00.123456789Z",
                0,
                "1970-01-01T00:00:00.123456789Z",
            ),
            (
                "1970-01-01T00:00:00.999999999Z",
                1,
                "1970-01-01T00:00:01.000999999Z",
            ),
            (
                "2024-02-28T23:59:59.999999999Z",
                1,
                "2024-02-29T00:00:00.000999999Z",
            ),
            ("1999-12-31T23:59:59.9995Z", 1, "2000-01-01T00:00:00.0005Z"),
            ("0000-01-01T00:00:00Z", 1, "0000-01-01T00:00:00.001Z"),
        ] {
            let sample = reading(100, utc);
            let deadline = clock.deadline(&sample, millis).unwrap();
            assert_eq!(deadline.monotonic_millis, 100 + millis);
            assert_eq!(deadline.utc.as_str(), expected);
            assert_eq!(sample.monotonic_millis, 100);
            assert_eq!(sample.utc.as_str(), utc);
        }
    }

    #[test]
    fn deadline_refuses_elapsed_and_utc_overflow_without_saturation() {
        let clock = NativeClock::new();
        let upper = reading(u64::MAX - 1, "1970-01-01T00:00:00Z");
        assert_eq!(
            clock.deadline(&upper, 1).unwrap().monotonic_millis,
            u64::MAX
        );
        for (sample, millis) in [
            (upper, 2),
            (reading(u64::MAX, "1970-01-01T00:00:00Z"), 1),
            (reading(0, "9999-12-31T23:59:59.999999999Z"), 1),
            (reading(0, "1970-01-01T00:00:00Z"), u64::MAX),
        ] {
            assert_eq!(
                clock.deadline(&sample, millis).unwrap_err(),
                ProviderFailure::DeadlineOverflow
            );
        }
    }

    #[test]
    fn deadline_uses_supplied_sample_without_resampling_after_wall_adjustment() {
        let elapsed_calls = Cell::new(0);
        let wall_calls = Cell::new(0);
        let wall = Cell::new(UNIX_EPOCH);
        let source = ClockSources {
            monotonic: || {
                elapsed_calls.set(elapsed_calls.get() + 1);
                Ok(53)
            },
            wall: || {
                wall_calls.set(wall_calls.get() + 1);
                wall.get()
            },
        };
        let sample = source.now().unwrap();
        for changed in [
            UNIX_EPOCH.checked_sub(Duration::from_secs(86_400)).unwrap(),
            UNIX_EPOCH.checked_add(Duration::from_secs(86_400)).unwrap(),
        ] {
            wall.set(changed);
            let deadline = source.deadline(&sample, 1_000).unwrap();
            assert_eq!(deadline.monotonic_millis, 1_053);
            assert_eq!(deadline.utc.as_str(), "1970-01-01T00:00:01Z");
        }
        assert_eq!(elapsed_calls.get(), 1);
        assert_eq!(wall_calls.get(), 1);
    }

    #[test]
    fn four_identity_kinds_draw_independently_once_without_a_uniqueness_policy() {
        let calls = Cell::new(0);
        let mut samples = [
            [0; 16],
            [0xff; 16],
            std::array::from_fn(|i| i as u8),
            [0; 16],
        ]
        .into_iter();
        let mut source = Identities {
            draw: || {
                calls.set(calls.get() + 1);
                Ok(samples.next().unwrap())
            },
        };
        assert_eq!(
            source.host_epoch().unwrap().as_str(),
            "00000000-0000-4000-8000-000000000000"
        );
        assert_eq!(calls.get(), 1);
        assert_eq!(
            source.stream_id().unwrap().as_str(),
            "ffffffff-ffff-4fff-bfff-ffffffffffff"
        );
        assert_eq!(calls.get(), 2);
        assert_eq!(
            source.plan_id().unwrap().as_str(),
            "00010203-0405-4607-8809-0a0b0c0d0e0f"
        );
        assert_eq!(calls.get(), 3);
        assert_eq!(
            source.operation_id().unwrap().as_str(),
            "00000000-0000-4000-8000-000000000000"
        );
        assert_eq!(calls.get(), 4);
    }

    #[test]
    fn uuid_encoding_masks_only_version_and_variant_for_zero_ff_and_mixed_bytes() {
        for (bytes, expected) in [
            ([0; 16], "00000000-0000-4000-8000-000000000000"),
            ([0xff; 16], "ffffffff-ffff-4fff-bfff-ffffffffffff"),
            (
                std::array::from_fn(|i| i as u8),
                "00010203-0405-4607-8809-0a0b0c0d0e0f",
            ),
        ] {
            let encoded = uuid_v4(bytes);
            assert_eq!(encoded, expected);
            assert!(HostEpoch::new(encoded.clone()).is_ok());
            assert!(StreamId::new(encoded.clone()).is_ok());
            assert!(PlanId::new(encoded.clone()).is_ok());
            assert!(OperationId::new(encoded).is_ok());
        }
    }

    #[test]
    fn every_entropy_failure_refuses_each_kind_without_retry_or_validation() {
        for code in PLATFORM_FAILURES {
            let calls = Cell::new(0);
            let mut source = Identities {
                draw: || {
                    calls.set(calls.get() + 1);
                    Err(PlatformError {
                        code,
                        native_code: Some(-913),
                    })
                },
            };
            assert_eq!(
                source.host_epoch(),
                Err(ProviderFailure::EntropyUnavailable)
            );
            assert_eq!(calls.get(), 1);
            assert_eq!(source.stream_id(), Err(ProviderFailure::EntropyUnavailable));
            assert_eq!(calls.get(), 2);
            assert_eq!(source.plan_id(), Err(ProviderFailure::EntropyUnavailable));
            assert_eq!(calls.get(), 3);
            assert_eq!(
                source.operation_id(),
                Err(ProviderFailure::EntropyUnavailable)
            );
            assert_eq!(calls.get(), 4);
            let result = source.identity(|_| -> Result<HostEpoch, InvalidPrimitive> {
                panic!("failed entropy must never reach identity validation")
            });
            assert_eq!(result, Err(ProviderFailure::EntropyUnavailable));
            assert_eq!(calls.get(), 5);
        }
    }

    #[test]
    fn encoded_identity_refusal_is_closed_and_does_not_retry() {
        let calls = Cell::new(0);
        let mut source = Identities {
            draw: || {
                calls.set(calls.get() + 1);
                Ok([0; 16])
            },
        };
        let result = source.identity(|_| Err::<HostEpoch, _>(InvalidPrimitive));
        assert_eq!(result, Err(ProviderFailure::InvalidIdentity));
        assert_eq!(calls.get(), 1);
    }

    #[cfg(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    ))]
    #[test]
    fn supported_native_providers_return_contract_valid_values_without_entropy_logging() {
        // Read-only observations of this host only. No sleep, randomness
        // distribution, uniqueness or another native host is qualified here.
        let before = native_monotonic().unwrap();
        let clock = NativeClock::new();
        let observed = clock.now().unwrap();
        let after = native_monotonic().unwrap();
        assert!(before <= observed.monotonic_millis && observed.monotonic_millis <= after);
        assert!(UtcTimestamp::new(observed.utc.as_str()).is_ok());
        assert!(clock.deadline(&observed, 1).is_ok());
        let mut source = NativeIdentitySource::new();
        assert!(source.host_epoch().is_ok());
        assert!(source.stream_id().is_ok());
        assert!(source.plan_id().is_ok());
        assert!(source.operation_id().is_ok());
    }

    #[cfg(not(any(
        all(target_os = "windows", target_arch = "x86_64"),
        all(target_os = "macos", target_arch = "aarch64")
    )))]
    #[test]
    fn unsupported_native_hosts_refuse_clock_and_every_identity_kind() {
        assert_eq!(
            NativeClock::new().now().unwrap_err(),
            ProviderFailure::ClockUnavailable
        );
        let mut source = NativeIdentitySource::new();
        assert_eq!(
            source.host_epoch(),
            Err(ProviderFailure::EntropyUnavailable)
        );
        assert_eq!(source.stream_id(), Err(ProviderFailure::EntropyUnavailable));
        assert_eq!(source.plan_id(), Err(ProviderFailure::EntropyUnavailable));
        assert_eq!(
            source.operation_id(),
            Err(ProviderFailure::EntropyUnavailable)
        );
    }
}

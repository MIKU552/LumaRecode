//! Bounded HDR10+ T.35 parsing. This module never owns a media file, a decoder,
//! a task thread or a GPU resource; the C++ engine owns those lifetimes.
//!
//! Syntax and representability match the stable C# implementation. Integer code
//! values survive parsing unchanged, including reduced rational representations
//! at the later ffprobe/x265 boundary.

use std::panic::{AssertUnwindSafe, catch_unwind};

const PREFIX: &[u8; 6] = &[0xb5, 0, 0x3c, 0, 1, 4];
const MAX_RECORD_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParseError {
    NotFound,
    InvalidData,
    Unsupported,
}

/// This layout is checked from the C, C++ and managed boundary tests. Counts
/// limit active entries; unused fixed-size array elements remain zero.
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Metadata {
    pub struct_size: u32,
    pub abi_version: u32,
    pub application_version: u32,
    pub number_of_windows: u32,
    pub targeted_display_luminance: u32,
    pub max_scl: [u32; 3],
    pub average_rgb: u32,
    pub distribution_count: u32,
    pub distribution_indices: [u32; 15],
    pub distribution_values: [u32; 15],
    pub tone_mapping: u32,
    pub knee_point_x: u32,
    pub knee_point_y: u32,
    pub anchor_count: u32,
    pub anchors: [u32; 15],
}

struct Bits<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl Bits<'_> {
    fn read(&mut self, width: usize) -> Result<u32, ParseError> {
        let end = self
            .position
            .checked_add(width)
            .ok_or(ParseError::InvalidData)?;
        if width > 32 || end > self.bytes.len().saturating_mul(8) {
            return Err(ParseError::InvalidData);
        }
        let mut value = 0;
        while self.position < end {
            value = (value << 1)
                | u32::from((self.bytes[self.position / 8] >> (7 - self.position % 8)) & 1);
            self.position += 1;
        }
        Ok(value)
    }
}

/// Parse a complete payload with bounded work and no heap allocation. Syntactic
/// corruption is distinct from valid HDR10+ features our output cannot retain.
pub fn parse_t35(payload: &[u8]) -> Result<Metadata, ParseError> {
    if payload.len() > MAX_RECORD_BYTES {
        return Err(ParseError::InvalidData);
    }
    if !payload.starts_with(PREFIX) {
        return Err(ParseError::NotFound);
    }
    let mut bits = Bits {
        bytes: &payload[6..],
        position: 0,
    };
    let mut result = Metadata {
        struct_size: size_of::<Metadata>() as u32,
        abi_version: 1,
        application_version: bits.read(8)?,
        number_of_windows: bits.read(2)?,
        ..Metadata::default()
    };
    if result.application_version != 1 || result.number_of_windows != 1 {
        return Err(ParseError::Unsupported);
    }
    result.targeted_display_luminance = bits.read(27)?;
    if result.targeted_display_luminance > 10000 {
        return Err(ParseError::InvalidData);
    }
    if bits.read(1)? != 0 {
        return Err(ParseError::Unsupported);
    }
    for value in &mut result.max_scl {
        *value = bits.read(17)?;
        if *value > 100000 {
            return Err(ParseError::InvalidData);
        }
    }
    result.average_rgb = bits.read(17)?;
    if result.average_rgb > 100000 {
        return Err(ParseError::InvalidData);
    }
    result.distribution_count = bits.read(4)?;
    let mut previous = None;
    for index in 0..result.distribution_count as usize {
        let percentage = bits.read(7)?;
        let value = bits.read(17)?;
        if percentage > 100 || value > 100000 || previous.is_some_and(|n| n >= percentage) {
            return Err(ParseError::InvalidData);
        }
        result.distribution_indices[index] = percentage;
        result.distribution_values[index] = value;
        previous = Some(percentage);
    }
    if bits.read(10)? != 0 {
        return Err(ParseError::Unsupported);
    }
    if bits.read(1)? != 0 {
        return Err(ParseError::Unsupported);
    }
    result.tone_mapping = bits.read(1)?;
    if result.tone_mapping != 0 {
        result.knee_point_x = bits.read(12)?;
        result.knee_point_y = bits.read(12)?;
        result.anchor_count = bits.read(4)?;
        if result.targeted_display_luminance == 0 || result.anchor_count == 0 {
            return Err(ParseError::InvalidData);
        }
        for index in 0..result.anchor_count as usize {
            result.anchors[index] = bits.read(10)?;
        }
    } else if result.targeted_display_luminance != 0 {
        return Err(ParseError::InvalidData);
    }
    if bits.read(1)? != 0 {
        return Err(ParseError::Unsupported);
    }
    // Only byte-alignment zero padding is accepted by our current interchange.
    // Longer all-zero tails were accepted by the baseline and remain accepted.
    while bits.position < bits.bytes.len() * 8 {
        if bits.read(1)? != 0 {
            return Err(ParseError::InvalidData);
        }
    }
    Ok(result)
}

/// Private C++ bridge. The public core wrapper validates the ABI generation.
///
/// # Safety
/// `payload` points to `length` readable bytes and `output` is an aligned,
/// writable Metadata. Both allocations outlive this call and do not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn luma_hdr10plus_parse_t35(
    payload: *const u8,
    length: u64,
    output: *mut Metadata,
) -> i32 {
    if payload.is_null() || output.is_null() || length > MAX_RECORD_BYTES as u64 {
        return 1;
    }
    // SAFETY: The bridge contract guarantees readable borrowed input. The
    // per-record cap is below isize::MAX on every supported target.
    let bytes = unsafe { std::slice::from_raw_parts(payload, length as usize) };
    match catch_unwind(AssertUnwindSafe(|| parse_t35(bytes))) {
        Ok(Ok(metadata)) => {
            // SAFETY: The caller guarantees valid writable output. Publish only
            // after complete validation, preserving caller output on failure.
            unsafe { output.write(metadata) };
            0
        }
        Ok(Err(ParseError::NotFound)) => 9,
        Ok(Err(ParseError::InvalidData)) => 4,
        Ok(Err(ParseError::Unsupported)) => 2,
        Err(_) => 7,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(curve: bool) -> Vec<u8> {
        let mut fields = vec![
            (1_u32, 8),
            (1, 2),
            (if curve { 1000 } else { 0 }, 27),
            (0, 1),
            (1000, 17),
            (2000, 17),
            (3000, 17),
            (1500, 17),
            (2, 4),
            (1, 7),
            (500, 17),
            (100, 7),
            (3000, 17),
            (0, 10),
            (0, 1),
            (u32::from(curve), 1),
        ];
        if curve {
            fields.extend([(1024, 12), (2048, 12), (2, 4), (100, 10), (900, 10)]);
        }
        fields.push((0, 1));
        let mut result = PREFIX.to_vec();
        let mut byte = 0;
        let mut used = 0;
        for (value, width) in fields {
            for bit in (0..width).rev() {
                byte = (byte << 1) | ((value >> bit) & 1) as u8;
                used += 1;
                if used == 8 {
                    result.push(byte);
                    used = 0;
                    byte = 0;
                }
            }
        }
        if used != 0 {
            result.push(byte << (8 - used));
        }
        result
    }

    #[test]
    fn profiles_preserve_integer_codes() {
        for curve in [false, true] {
            let parsed = parse_t35(&fixture(curve)).unwrap();
            assert_eq!(parsed.max_scl, [1000, 2000, 3000]);
            assert_eq!(parsed.average_rgb, 1500);
            assert_eq!(&parsed.distribution_indices[..2], &[1, 100]);
            assert_eq!(&parsed.distribution_values[..2], &[500, 3000]);
            assert_eq!(parsed.tone_mapping, u32::from(curve));
            if curve {
                assert_eq!(&parsed.anchors[..2], &[100, 900]);
            }
        }
    }

    #[test]
    fn truncated_records_and_nonzero_padding_fail() {
        let mut bytes = fixture(true);
        for end in 0..bytes.len() {
            assert!(parse_t35(&bytes[..end]).is_err(), "prefix {end}");
        }
        bytes.push(1);
        assert_eq!(parse_t35(&bytes), Err(ParseError::InvalidData));
    }

    #[test]
    fn unrelated_payload_and_future_application_are_distinct() {
        assert_eq!(parse_t35(b"unrelated"), Err(ParseError::NotFound));
        let mut bytes = fixture(false);
        bytes[6] = 2;
        assert_eq!(parse_t35(&bytes), Err(ParseError::Unsupported));
    }

    #[test]
    fn c_layout_and_failure_publication_are_stable() {
        assert_eq!(size_of::<Metadata>(), 59 * 4);
        let bytes = fixture(false);
        let mut output = Metadata {
            average_rgb: 42,
            ..Metadata::default()
        };
        // SAFETY: The owned test allocations are valid and non-overlapping.
        let invalid_status = unsafe { luma_hdr10plus_parse_t35(bytes.as_ptr(), 7, &mut output) };
        assert_eq!(invalid_status, 4);
        assert_eq!(output.average_rgb, 42);
        // SAFETY: The owned test allocations are valid and non-overlapping.
        let valid_status =
            unsafe { luma_hdr10plus_parse_t35(bytes.as_ptr(), bytes.len() as u64, &mut output) };
        assert_eq!(valid_status, 0);
        assert_eq!(output.average_rgb, 1500);
    }

    #[test]
    fn arbitrary_short_inputs_never_panic() {
        // Deterministic adversarial corpus; fuzzing can extend this same safe
        // entry without involving an FFI pointer or allocating video buffers.
        let mut seed = 0x7061_7273_6572_u64;
        for length in 0..512 {
            let mut bytes = PREFIX.to_vec();
            for _ in 0..length {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                bytes.push(seed as u8);
            }
            let _ = parse_t35(&bytes);
        }
    }
}

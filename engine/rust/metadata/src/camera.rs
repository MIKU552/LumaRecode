//! Allocation-free parsing of camera wire data. A packet is bounded to one MiB;
//! nested DJI messages are borrowed slices with a fixed traversal depth.

use std::panic::{AssertUnwindSafe, catch_unwind};

const INVALID_DATA: i32 = 4;
const NOT_FOUND: i32 = 9;
const MAX_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Value<'a> {
    Integer(u64),
    Bytes(&'a [u8]),
    Fixed,
}

fn integer(bytes: &[u8], position: &mut usize) -> Result<u64, i32> {
    let mut result = 0;
    for shift in (0..70).step_by(7) {
        let byte = *bytes.get(*position).ok_or(INVALID_DATA)?;
        *position += 1;
        if shift == 63 && byte > 1 {
            return Err(INVALID_DATA);
        }
        result |= u64::from(byte & 127) << shift;
        if byte < 128 {
            return Ok(result);
        }
    }
    Err(INVALID_DATA)
}

/// Validate every field, including skipped fields and duplicate occurrences.
/// First occurrence wins, matching the existing DJI reader.
fn field(bytes: &[u8], wanted: u32) -> Result<Option<Value<'_>>, i32> {
    let mut position = 0;
    let mut result = None;
    while position < bytes.len() {
        let key = integer(bytes, &mut position)?;
        let number = key >> 3;
        if number == 0 || number > 0x1fff_ffff {
            return Err(INVALID_DATA);
        }
        let value = match key & 7 {
            0 => Value::Integer(integer(bytes, &mut position)?),
            wire @ (1 | 2 | 5) => {
                let length = match wire {
                    1 => 8,
                    5 => 4,
                    _ => {
                        usize::try_from(integer(bytes, &mut position)?).map_err(|_| INVALID_DATA)?
                    }
                };
                let end = position.checked_add(length).ok_or(INVALID_DATA)?;
                let data = bytes.get(position..end).ok_or(INVALID_DATA)?;
                position = end;
                if wire == 2 {
                    Value::Bytes(data)
                } else {
                    Value::Fixed
                }
            }
            _ => return Err(INVALID_DATA),
        };
        if number == u64::from(wanted) && result.is_none() {
            result = Some(value);
        }
    }
    Ok(result)
}

fn nested(bytes: &[u8], number: u32) -> Result<&[u8], i32> {
    match field(bytes, number)? {
        Some(Value::Bytes(value)) => Ok(value),
        _ => Err(INVALID_DATA),
    }
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct DjiColor {
    struct_size: u32,
    abi_version: u32,
    schema: u32,
    reserved: u32,
    mode: u64,
}

fn dji(bytes: &[u8]) -> Result<DjiColor, i32> {
    let schema = nested(nested(nested(bytes, 1)?, 1)?, 1)?;
    let schema = match schema {
        b"dvtm_PP-101.proto" => 1,
        b"dvtm_ac206.proto" => 2,
        b"dvtm_oq101.proto" => 3,
        _ => return Err(NOT_FOUND),
    };
    let color = nested(nested(bytes, 2)?, 4)?;
    let mode = match field(color, 1)? {
        Some(Value::Integer(value)) => value,
        None => 0,
        _ => return Err(INVALID_DATA),
    };
    Ok(DjiColor {
        struct_size: size_of::<DjiColor>() as u32,
        abi_version: 1,
        schema,
        reserved: 0,
        mode,
    })
}

fn start_code(bytes: &[u8], start: usize) -> Option<(usize, usize)> {
    (start..bytes.len().saturating_sub(2)).find_map(|index| {
        if bytes.get(index..index + 3) == Some(&[0, 0, 1]) {
            Some((index, index + 3))
        } else if bytes.get(index..index + 4) == Some(&[0, 0, 0, 1]) {
            Some((index, index + 4))
        } else {
            None
        }
    })
}

fn sps(bytes: &[u8]) -> Result<&[u8], i32> {
    if bytes.first() == Some(&1) {
        if bytes.len() < 8 || bytes[5] & 31 == 0 {
            return Err(INVALID_DATA);
        }
        let length = usize::from(u16::from_be_bytes([bytes[6], bytes[7]]));
        return bytes.get(8..8 + length).ok_or(INVALID_DATA);
    }
    let mut next = start_code(bytes, 0);
    while let Some((_, offset)) = next {
        next = start_code(bytes, offset);
        if bytes.get(offset).is_some_and(|byte| byte & 31 == 7) {
            let end = next.map_or(bytes.len(), |(start, _)| start);
            return Ok(&bytes[offset..end]);
        }
    }
    Err(NOT_FOUND)
}

struct Rbsp<'a> {
    nal: &'a [u8],
    position: usize,
    byte: u8,
    remaining: u8,
}

impl Rbsp<'_> {
    fn bit(&mut self) -> Result<u8, i32> {
        if self.remaining == 0 {
            loop {
                let value = *self.nal.get(self.position).ok_or(INVALID_DATA)?;
                let skip = self.position >= 3
                    && value == 3
                    && self.nal[self.position - 1] == 0
                    && self.nal[self.position - 2] == 0;
                self.position += 1;
                if !skip {
                    self.byte = value;
                    self.remaining = 8;
                    break;
                }
            }
        }
        self.remaining -= 1;
        Ok((self.byte >> self.remaining) & 1)
    }
    fn byte(&mut self) -> Result<u8, i32> {
        let mut value = 0;
        for _ in 0..8 {
            value = (value << 1) | self.bit()?;
        }
        Ok(value)
    }
}

fn monochrome(bytes: &[u8]) -> Result<bool, i32> {
    let nal = sps(bytes)?;
    if nal.len() < 4 || nal[0] & 31 != 7 {
        return Err(INVALID_DATA);
    }
    let mut bits = Rbsp {
        nal,
        position: 1,
        byte: 0,
        remaining: 0,
    };
    let profile = bits.byte()?;
    bits.byte()?;
    bits.byte()?;
    if ![
        44, 83, 86, 100, 110, 118, 122, 128, 134, 135, 138, 139, 144, 244,
    ]
    .contains(&profile)
    {
        return Ok(false);
    }
    let mut zeros = 0;
    while bits.bit()? == 0 {
        zeros += 1;
        if zeros > 31 {
            return Err(INVALID_DATA);
        }
    }
    for _ in 0..zeros {
        bits.bit()?;
    }
    Ok(bits.bit()? == 1)
}

// SAFETY: C callers provide readable input and aligned writable output for the
// stated lengths. Public C++ wrappers validate layouts and bound byte counts.
unsafe fn input<'a>(pointer: *const u8, size: u64) -> Result<&'a [u8], i32> {
    let size = usize::try_from(size).map_err(|_| 1)?;
    if size > MAX_BYTES || (pointer.is_null() && size != 0) {
        return Err(1);
    }
    if size == 0 {
        return Ok(&[]);
    }
    // SAFETY: The caller guarantees the pointer's readable extent for this call.
    Ok(unsafe { std::slice::from_raw_parts(pointer, size) })
}

#[unsafe(no_mangle)]
/// # Safety
/// Input points to `size` readable bytes for this call; output points to an
/// aligned writable DjiColor. Input and output storage must not overlap.
pub unsafe extern "C" fn luma_dji_parse_color(
    packet: *const u8,
    size: u64,
    output: *mut DjiColor,
) -> i32 {
    if output.is_null() {
        return 1;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: This FFI entry forwards the caller's input contract unchanged.
        dji(unsafe { input(packet, size)? })
    }));
    match result {
        Ok(Ok(value)) => {
            // SAFETY: The caller guarantees an aligned writable DjiColor.
            unsafe {
                output.write(value);
            }
            0
        }
        Ok(Err(status)) => status,
        Err(_) => 7,
    }
}

#[unsafe(no_mangle)]
/// # Safety
/// Input points to `size` readable bytes for this call; output points to an
/// aligned writable u32. Input and output storage must not overlap.
pub unsafe extern "C" fn luma_avc_is_monochrome(
    data: *const u8,
    size: u64,
    output: *mut u32,
) -> i32 {
    if output.is_null() {
        return 1;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: This FFI entry forwards the caller's input contract unchanged.
        monochrome(unsafe { input(data, size)? })
    }));
    match result {
        Ok(Ok(value)) => {
            // SAFETY: The caller guarantees an aligned writable u32.
            unsafe {
                output.write(u32::from(value));
            }
            0
        }
        Ok(Err(status)) => status,
        Err(_) => 7,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn message(number: u8, bytes: &[u8]) -> Vec<u8> {
        let mut result = vec![number << 3 | 2, bytes.len() as u8];
        result.extend_from_slice(bytes);
        result
    }
    fn packet(schema: &[u8], color: &[u8]) -> Vec<u8> {
        let mut result = message(1, &message(1, &message(1, schema)));
        result.extend(message(2, &message(4, color)));
        result
    }
    #[test]
    fn dji_schema_and_duplicate_fields() {
        let known = packet(b"dvtm_PP-101.proto", &[8, 19, 8, 9]);
        assert_eq!(dji(&known).unwrap().mode, 19);
        assert_eq!(dji(&packet(b"dvtm_ac206.proto", &[])).unwrap().mode, 0);
        assert_eq!(
            dji(&packet(b"unrecognized.proto", &[8, 19])),
            Err(NOT_FOUND)
        );
        assert_eq!(
            dji(&packet(b"dvtm_oq101.proto", &[8, 128])),
            Err(INVALID_DATA)
        );
        for end in 0..known.len() {
            assert!(dji(&known[..end]).is_err());
        }
        assert_eq!(field(&[0], 1), Err(INVALID_DATA));
        assert_eq!(
            field(&[8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2], 1),
            Err(INVALID_DATA)
        );
        assert_eq!(field(&[11], 1), Err(INVALID_DATA));
    }
    #[test]
    fn avcc_annexb_and_truncation() {
        let mono = [0, 0, 0, 1, 0x67, 100, 0, 40, 0xc0];
        assert_eq!(monochrome(&mono), Ok(true));
        assert_eq!(monochrome(&[0, 0, 1, 0x67, 100, 0, 40, 0xa0]), Ok(false));
        assert_eq!(
            monochrome(&[1, 100, 0, 40, 255, 225, 0, 5, 0x67, 100, 0, 40, 0xc0]),
            Ok(true)
        );
        assert_eq!(monochrome(&[0, 0, 1, 0x67, 66, 0, 40, 0xc0]), Ok(false));
        for end in 0..mono.len() {
            assert!(monochrome(&mono[..end]).is_err());
        }
        assert_eq!(
            monochrome(&[1, 100, 0, 40, 255, 225, 255, 255]),
            Err(INVALID_DATA)
        );
        assert_eq!(monochrome(&[0, 0, 1, 0x68, 0, 0, 0]), Err(NOT_FOUND));
    }
}

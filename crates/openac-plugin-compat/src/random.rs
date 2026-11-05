//! The seeded pseudo-random generator of the .NET runtime (a subtractive
//! generator over a 55-entry table), reproduced so a seeded sequence matches
//! the C# value for value: the client's weather roll and particle jitter, and
//! a plugin's `new Random(seed)`.

const MBIG: i32 = i32::MAX;
const MSEED: i32 = 161_803_398;

/// The generator a seeded .NET `new Random(seed)` creates, value for value.
#[derive(Debug, Clone)]
pub struct DotNetRandom {
    seed_array: [i32; 56],
    inext: usize,
    inextp: usize,
}

impl DotNetRandom {
    /// The generator `new Random(seed)` creates.
    #[must_use]
    pub fn new(seed: i32) -> Self {
        let mut seed_array = [0i32; 56];
        let subtraction = if seed == i32::MIN {
            i32::MAX
        } else {
            seed.abs()
        };
        let mut mj = MSEED.wrapping_sub(subtraction);
        seed_array[55] = mj;
        let mut mk: i32 = 1;
        let mut ii = 0usize;
        for _ in 1..55 {
            ii += 21;
            if ii >= 55 {
                ii -= 55;
            }
            seed_array[ii] = mk;
            mk = mj.wrapping_sub(mk);
            if mk < 0 {
                mk = mk.wrapping_add(MBIG);
            }
            mj = seed_array[ii];
        }
        for _ in 1..5 {
            for i in 1..56 {
                let mut n = i + 30;
                if n >= 55 {
                    n -= 55;
                }
                seed_array[i] = seed_array[i].wrapping_sub(seed_array[1 + n]);
                if seed_array[i] < 0 {
                    seed_array[i] = seed_array[i].wrapping_add(MBIG);
                }
            }
        }
        Self {
            seed_array,
            inext: 0,
            inextp: 21,
        }
    }

    fn internal_sample(&mut self) -> i32 {
        let mut loc_inext = self.inext + 1;
        if loc_inext >= 56 {
            loc_inext = 1;
        }
        let mut loc_inextp = self.inextp + 1;
        if loc_inextp >= 56 {
            loc_inextp = 1;
        }
        let mut ret = self.seed_array[loc_inext].wrapping_sub(self.seed_array[loc_inextp]);
        if ret == MBIG {
            ret -= 1;
        }
        if ret < 0 {
            ret = ret.wrapping_add(MBIG);
        }
        self.seed_array[loc_inext] = ret;
        self.inext = loc_inext;
        self.inextp = loc_inextp;
        ret
    }

    fn sample(&mut self) -> f64 {
        f64::from(self.internal_sample()) * (1.0 / f64::from(MBIG))
    }

    /// A sample in `[0, 1)` spread over the whole `i32` range, for ranges wider
    /// than `i32::MAX`.
    fn sample_for_large_range(&mut self) -> f64 {
        let mut result = self.internal_sample();
        // The second sample only decides the sign.
        if self.internal_sample() % 2 == 0 {
            result = -result;
        }
        let mut d = f64::from(result);
        d += f64::from(i32::MAX - 1);
        d /= 2.0 * f64::from(i32::MAX as u32) - 1.0;
        d
    }

    /// `Next()`: a value in `[0, i32::MAX)`.
    pub fn next_int(&mut self) -> i32 {
        self.internal_sample()
    }

    /// `Next(maxValue)`: a value in `[0, max_value)`; 0 when `max_value` is 0
    /// (a sample is still drawn).
    ///
    /// # Panics
    ///
    /// When `max_value` is negative (.NET throws `ArgumentOutOfRangeException`).
    pub fn next_max(&mut self, max_value: i32) -> i32 {
        assert!(
            max_value >= 0,
            "Random.Next: max_value {max_value} is negative"
        );
        // Truncation toward zero, as the C# cast.
        (self.sample() * f64::from(max_value)) as i32
    }

    /// `Next(minValue, maxValue)`: a value in `[min_value, max_value)`;
    /// `min_value` when they are equal (a sample is still drawn).
    ///
    /// # Panics
    ///
    /// When `min_value > max_value` (.NET throws `ArgumentOutOfRangeException`).
    pub fn next_range(&mut self, min_value: i32, max_value: i32) -> i32 {
        assert!(
            min_value <= max_value,
            "Random.Next: min_value {min_value} is greater than max_value {max_value}"
        );
        let range = i64::from(max_value) - i64::from(min_value);
        if range <= i64::from(i32::MAX) {
            ((self.sample() * range as f64) as i32).wrapping_add(min_value)
        } else {
            ((self.sample_for_large_range() * range as f64) as i64 + i64::from(min_value)) as i32
        }
    }

    /// `NextDouble()`: a value in `[0, 1)`.
    pub fn next_double(&mut self) -> f64 {
        self.sample()
    }

    /// `NextBytes(buffer)`: one sample per byte, its low eight bits.
    pub fn next_bytes(&mut self, buffer: &mut [u8]) {
        for byte in buffer {
            *byte = self.internal_sample() as u8;
        }
    }

    /// A version-4 UUID drawn from this generator, written as .NET's
    /// `Guid.ToString("N")` writes one: 32 lower-case hex digits, no dashes.
    ///
    /// .NET's `Guid.NewGuid()` draws from the operating system, so no C#
    /// sequence can be matched; the shape (version nibble 4, variant `10`)
    /// is what callers rely on.
    pub fn uuid_v4_n(&mut self) -> String {
        let mut bytes = [0u8; 16];
        self.next_bytes(&mut bytes);
        bytes[6] = (bytes[6] & 0x0F) | 0x40;
        bytes[8] = (bytes[8] & 0x3F) | 0x80;
        let mut out = String::with_capacity(32);
        for b in bytes {
            out.push(char::from_digit(u32::from(b >> 4), 16).unwrap_or('0'));
            out.push(char::from_digit(u32::from(b & 0x0F), 16).unwrap_or('0'));
        }
        out
    }
}

#[cfg(test)]
mod tests {
    //! Golden values printed by .NET 10 `new Random(seed)` (the full table
    //! is `tests/data/random.tsv`).
    use super::*;

    fn first_three(seed: i32) -> [f64; 3] {
        let mut r = DotNetRandom::new(seed);
        [r.next_double(), r.next_double(), r.next_double()]
    }

    #[test]
    fn matches_the_dotnet_sequence() {
        assert_eq!(
            first_three(0),
            [0.7262432699679598, 0.8173253595909687, 0.7680226893946634]
        );
        assert_eq!(
            first_three(1),
            [
                0.24866858415709278,
                0.11074397718102856,
                0.46701067987224587
            ]
        );
        assert_eq!(
            first_three(42),
            [0.6681064659115423, 0.14090729837348093, 0.12551828945312568]
        );
        assert_eq!(first_three(-1), first_three(1));
        assert_eq!(
            first_three(i32::MIN),
            [0.7262432699679598, 0.8173253595909687, 0.7680226921886312]
        );
        assert_eq!(first_three(i32::MAX), first_three(i32::MIN));
        assert_eq!(
            first_three(0x9E37_79B1_u32 as i32),
            [0.3357987712769763, 0.9299241867521425, 0.7327763879358659]
        );
    }

    #[test]
    fn uuid_has_the_version_and_variant_bits() {
        let mut r = DotNetRandom::new(1234);
        let id = r.uuid_v4_n();
        assert_eq!(id.len(), 32);
        assert!(
            id.chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
        );
        assert_eq!(&id[12..13], "4");
        assert!(matches!(&id[16..17], "8" | "9" | "a" | "b"));
        // Deterministic for a seed.
        assert_eq!(DotNetRandom::new(1234).uuid_v4_n(), id);
    }
}

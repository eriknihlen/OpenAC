//! The random numbers effects draw from, behind a seedable source so a
//! simulation can be replayed exactly.

pub use ac_core::sky::DotNetRandom;

/// Uniform random numbers in `[0, 1)`.
pub trait RandomSource: Send {
    /// The next number, `0.0 <= x < 1.0`.
    fn next_unit(&mut self) -> f64;
}

impl RandomSource for DotNetRandom {
    fn next_unit(&mut self) -> f64 {
        self.next_double()
    }
}

/// Always the same number (tests, and callers that want no jitter).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ConstantRandom(pub f64);

impl RandomSource for ConstantRandom {
    fn next_unit(&mut self) -> f64 {
        self.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seeded_source_is_the_dotnet_sequence() {
        // Golden value: .NET 10 `new Random(42).NextDouble()`.
        let mut r = DotNetRandom::new(42);
        assert_eq!(r.next_unit(), 0.6681064659115423);
    }
}

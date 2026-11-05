//! `DotNetRandom` against the sequences .NET 10 prints for `new Random(seed)`
//! (`tests/data/random.tsv`, from a .NET generator program).

use openac_plugin_compat::DotNetRandom;

fn run(seed: i32, op: &str) -> String {
    let mut r = DotNetRandom::new(seed);
    let list = |v: Vec<String>| v.join(",");
    match op {
        "next" => list((0..6).map(|_| r.next_int().to_string()).collect()),
        "next_max(10)" => list((0..8).map(|_| r.next_max(10).to_string()).collect()),
        "next_range(-5,5)" => list((0..8).map(|_| r.next_range(-5, 5).to_string()).collect()),
        "next_range(min,max)" => list(
            (0..6)
                .map(|_| r.next_range(i32::MIN, i32::MAX).to_string())
                .collect(),
        ),
        "next_double" => list((0..4).map(|_| format!("{:?}", r.next_double())).collect()),
        "next_bytes(12)" => {
            let mut b = [0u8; 12];
            r.next_bytes(&mut b);
            b.iter().map(|x| format!("{x:02X}")).collect()
        }
        "next_range(0,0)+next_max(0)+next" => {
            let a = r.next_range(0, 0);
            let b = r.next_max(0);
            let c = r.next_int();
            format!("{a},{b},{c}")
        }
        other => panic!("unknown op {other}"),
    }
}

#[test]
fn every_sequence_matches_dotnet() {
    let data = include_str!("data/random.tsv");
    let mut rows = 0;
    for line in data
        .lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
    {
        let mut cols = line.split('\t');
        let seed: i32 = cols.next().unwrap().parse().unwrap();
        let op = cols.next().unwrap();
        let expected = cols.next().unwrap();
        assert_eq!(run(seed, op), expected, "seed {seed}, {op}");
        rows += 1;
    }
    assert_eq!(rows, 49);
}

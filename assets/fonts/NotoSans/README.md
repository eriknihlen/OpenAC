# Noto Sans Regular and SemiBold

Unmodified hinted TrueType fonts from the Noto project, under SIL OFL 1.1:
the Regular is the same file OpenAC bundles (`assets/fonts/NotoSans/` there).

| File | Source | SHA-256 |
|---|---|---|
| NotoSans-Regular.ttf | https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/hinted/ttf/NotoSans/NotoSans-Regular.ttf | b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5 |
| NotoSans-SemiBold.ttf | https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/hinted/ttf/NotoSans/NotoSans-SemiBold.ttf | 87a8b90ece1e89746b544e4e086f85a3710e41485a8078f9be874837dfad45d5 |

Full licence: `OFL.txt` beside this file. The fonts are compiled into the
`ac-plugin-ui` crate (`include_bytes!` in `crates/ac-plugin-ui/src/fonts.rs`), so they
ship inside the `openac` executable; a release ships `OFL.txt` with them.

//! `actool`: inspect game data from the command line.

mod dat;
mod gfx;
mod migrate;
mod options;
mod perf;
mod ui;

use std::process::ExitCode;

use options::{Command, Options, USAGE};

type Result<T> = core::result::Result<T, Box<dyn std::error::Error>>;

/// Compare two PNGs and write the report; true when they pass.
fn image_compare(
    expected: &std::path::Path,
    actual: &std::path::Path,
    out: &std::path::Path,
    tolerance: u8,
    max_fraction: f64,
    mask: Option<&std::path::Path>,
) -> Result<bool> {
    let name = |p: &std::path::Path| {
        p.file_name()
            .map_or_else(String::new, |n| n.to_string_lossy().into_owned())
    };
    let mask_image = mask.map(perf::read_png).transpose()?;
    let (en, an, mn) = (name(expected), name(actual), mask.map(name));
    let c = perf::compare(
        &perf::read_png(expected)?,
        &perf::read_png(actual)?,
        (&en, &an, mn.as_deref()),
        tolerance,
        max_fraction,
        mask_image.as_ref(),
    )?;
    std::fs::write(out, &c.json)?;
    let fraction = if c.compared_pixels == 0 {
        1.0
    } else {
        c.different_pixels as f64 / c.compared_pixels as f64
    };
    println!(
        "screenshot comparison {}: {:.6}% different",
        if c.passed { "passed" } else { "failed" },
        fraction * 100.0
    );
    Ok(c.passed)
}

/// Summarise a frame history into `out`.
fn frames_summarize(
    frames: &std::path::Path,
    checkpoints: &std::path::Path,
    markers: &std::path::Path,
    out: &std::path::Path,
) -> Result<()> {
    let read = |p: &std::path::Path| std::fs::read_to_string(p);
    let name = frames
        .file_name()
        .map_or_else(String::new, |n| n.to_string_lossy().into_owned());
    let json = perf::summarize(&name, &read(frames)?, &read(checkpoints)?, &read(markers)?)?;
    std::fs::write(out, json)?;
    println!("wrote frame-history summary: {}", out.display());
    Ok(())
}

fn main() -> ExitCode {
    let args: Vec<std::ffi::OsString> = std::env::args_os().skip(1).collect();
    if args.first().is_some_and(|first| first == "migrate-classic") {
        return ExitCode::from(migrate::run(
            &args[1..],
            &mut std::io::stdout(),
            &mut std::io::stderr(),
        ));
    }
    let options = match Options::parse(args, |name| std::env::var_os(name)) {
        Ok(options) => options,
        Err(message) => {
            eprintln!("actool: {message}\n\n{USAGE}");
            return ExitCode::from(2);
        }
    };
    let result = match options.command {
        Command::Help => {
            println!("{USAGE}");
            Ok(())
        }
        Command::DatInfo => dat::info(&options.dat_dir),
        Command::DatLandblock(id) => dat::landblock(&options.dat_dir, id),
        Command::DatSetup(id) => dat::setup(&options.dat_dir, id),
        Command::DatMotionTable(id) => dat::motion_table(&options.dat_dir, id),
        Command::DatEnvCell(id) => dat::env_cell(&options.dat_dir, id),
        Command::DatRegion(id) => dat::region(&options.dat_dir, id),
        Command::DatGfxObj(id) => gfx::gfxobj(&options.dat_dir, id),
        Command::DatTexture { id, ref out } => gfx::texture(&options.dat_dir, id, out),
        Command::DatLayoutIndex => ui::layout_index(&options.dat_dir),
        Command::DatLayout(id) => ui::layout(&options.dat_dir, id),
        Command::DatFont { id, ref out } => ui::font(&options.dat_dir, id, out.as_deref()),
        Command::DatEdges(id) => gfx::edges(&options.dat_dir, id),
        Command::DatSprites { ref ids, ref out } => ui::sprites(&options.dat_dir, ids, out),
        Command::DatVitalsBars => ui::vitals_bars(&options.dat_dir),
        Command::DatVitalsLayouts => ui::vitals_layouts(&options.dat_dir),
        Command::DatMockSelbar { ref out } => ui::mock_selbar(&options.dat_dir, out),
        Command::ImageCompare {
            ref expected,
            ref actual,
            ref out,
            tolerance,
            max_fraction,
            ref mask,
        } => {
            match image_compare(
                expected,
                actual,
                out,
                tolerance,
                max_fraction,
                mask.as_deref(),
            ) {
                Ok(true) => Ok(()),
                Ok(false) => return ExitCode::FAILURE,
                Err(e) => Err(e),
            }
        }
        Command::ImageCrop {
            ref input,
            region,
            ref out,
        } => perf::read_png(input).map_err(Into::into).and_then(|img| {
            let [x, y, w, h, zoom] = region;
            let c = perf::crop(&img, x, y, w, h, u32::try_from(zoom).unwrap_or(1));
            std::fs::write(out, ac_bits::png::encode_rgba(c.width, c.height, &c.rgba))?;
            println!("wrote {} ({}x{})", out.display(), c.width, c.height);
            Ok(())
        }),
        Command::ImageProbe { ref input, block } => {
            perf::read_png(input).map_err(Into::into).map(|img| {
                let [x0, y0, x1, y1] = block;
                println!("{}", input.display());
                for line in perf::probe(&img, x0, y0, x1, y1) {
                    println!("{line}");
                }
            })
        }
        Command::FramesSummarize {
            ref frames,
            ref checkpoints,
            ref markers,
            ref out,
        } => frames_summarize(frames, checkpoints, markers, out),
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("actool: {error}");
            let mut source = error.source();
            while let Some(cause) = source {
                eprintln!("  caused by: {cause}");
                source = cause.source();
            }
            ExitCode::FAILURE
        }
    }
}

use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::time::{Duration, Instant};
use micro_wakeword::{Detector, AUDIO_BLOCK_SAMPLES};
use serde_json::json;

fn value(args: &[String], key: &str) -> Option<String> {
    args.windows(2).find(|a| a[0] == key).map(|a| a[1].clone())
}
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();
    let config = value(&args, "--config");
    let model = value(&args, "--model");
    if config.is_none() && model.is_none() {
        return Err("Provide --config model.json or --model model.tflite".into());
    }
    let word = value(&args, "--word").unwrap_or_else(|| "Мартин".into());
    let cutoff = value(&args, "--threshold").unwrap_or_else(|| "0.87".into()).parse::<f32>()?;
    let window = value(&args, "--window").unwrap_or_else(|| "5".into()).parse::<usize>()?;
    let cooldown = value(&args, "--cooldown").unwrap_or_else(|| "1.5".into()).parse::<f32>()?;
    if !cutoff.is_finite() || !(0.05..=0.999).contains(&cutoff) ||
        !(1..=30).contains(&window) || !cooldown.is_finite() || !(0.0..=15.0).contains(&cooldown) {
        return Err("threshold, window or cooldown out of range".into());
    }
    let mut detector = if let Some(path) = config {
        Detector::from_config(PathBuf::from(path))?
    } else {
        Detector::builder(PathBuf::from(model.unwrap()))
            .wake_word(word.clone())
            .probability_cutoff(cutoff)
            .sliding_window_size(window)
            .build()?
    };
    // Stdout is reserved for machine-readable JSONL events. Diagnostics go to stderr.
    eprintln!("MicroWakeWord: streaming PCM16 16 kHz; detector ready");
    let mut stdin = io::stdin().lock();
    let mut stdout = io::stdout().lock();
    let mut bytes = [0_u8; AUDIO_BLOCK_SAMPLES * 2];
    let mut samples = [0_i16; AUDIO_BLOCK_SAMPLES];
    let mut last_wake = Instant::now() - Duration::from_secs(20);
    let holdoff = Duration::from_secs_f32(cooldown);
    loop {
        match stdin.read_exact(&mut bytes) {
            Ok(()) => {}
            Err(err) if err.kind() == io::ErrorKind::UnexpectedEof => break,
            Err(err) => return Err(Box::new(err)),
        }
        for (i, sample) in samples.iter_mut().enumerate() {
            *sample = i16::from_le_bytes([bytes[i * 2], bytes[i * 2 + 1]]);
        }
        if let Some(event) = detector.process_audio(&samples)? {
            if last_wake.elapsed() >= holdoff {
                last_wake = Instant::now();
                writeln!(stdout, "{}", json!({
                    "type":"wake",
                    "word":event.wake_word,
                    "score":event.probability
                }))?;
                stdout.flush()?;
            }
        }
    }
    Ok(())
}
fn main() {
    if let Err(err) = run() {
        eprintln!("Wake detector: {err}");
        std::process::exit(1);
    }
}

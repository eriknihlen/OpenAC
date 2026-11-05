//! The default output device, on a dedicated audio thread.
//!
//! The thread opens the device, hands the mixer to the device's callback
//! and then waits; dropping [`OutputDevice`] wakes it, which stops the
//! stream and ends the thread. Nothing outside this module touches the
//! device library.

use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{FromSample, SampleFormat, SizedSample, StreamConfig};

use crate::error::AudioError;
use crate::mixer::Mixer;

/// What was opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceInfo {
    /// The device's name as the system gives it.
    pub name: String,
    /// Output frames per second.
    pub sample_rate: u32,
    /// Output channels (the mix goes to the first two; one is a mono mix).
    pub channels: u16,
    /// The device's native sample format.
    pub sample_format: String,
}

/// The open device and its thread.
#[derive(Debug)]
pub(crate) struct OutputDevice {
    info: DeviceInfo,
    stop: Option<Sender<()>>,
    thread: Option<JoinHandle<()>>,
    error: Arc<Mutex<Option<String>>>,
}

type Ready = Result<DeviceInfo, AudioError>;

impl OutputDevice {
    /// Open the default output device and start pulling from `mixer`.
    pub(crate) fn open(mixer: Mixer) -> Result<Self, AudioError> {
        let (ready_tx, ready_rx) = mpsc::channel::<Ready>();
        let (stop_tx, stop_rx) = mpsc::channel::<()>();
        let error = Arc::new(Mutex::new(None));
        let stream_error = error.clone();
        let thread = std::thread::Builder::new()
            .name("openac-audio".into())
            .spawn(move || {
                // The opener waits on `ready_rx` until it hears back, so
                // these sends only fail if the opener itself is gone.
                match start(mixer, stream_error) {
                    Ok((stream, info)) => {
                        if ready_tx.send(Ok(info)).is_ok() {
                            wait_for_stop(&stop_rx);
                        }
                        drop(stream);
                    }
                    Err(e) => {
                        let _ = ready_tx.send(Err(e));
                    }
                }
            })
            .map_err(|e| AudioError::Device(format!("starting the audio thread: {e}")))?;
        let info = match ready_rx.recv() {
            Ok(Ok(info)) => info,
            Ok(Err(e)) => {
                let _ = thread.join();
                return Err(e);
            }
            Err(_) => {
                let _ = thread.join();
                return Err(AudioError::Device(
                    "the audio thread ended before opening the device".into(),
                ));
            }
        };
        Ok(Self {
            info,
            stop: Some(stop_tx),
            thread: Some(thread),
            error,
        })
    }

    pub(crate) fn info(&self) -> &DeviceInfo {
        &self.info
    }

    /// The last error the device reported while running, if any.
    pub(crate) fn error(&self) -> Option<String> {
        self.error
            .lock()
            .expect("audio error slot poisoned")
            .clone()
    }
}

impl Drop for OutputDevice {
    fn drop(&mut self) {
        // Closing the channel wakes the thread, which drops the stream.
        self.stop.take();
        if let Some(thread) = self.thread.take() {
            // A panic on the audio thread has already been reported by the
            // panic hook; there is nothing left to stop.
            let _ = thread.join();
        }
    }
}

fn wait_for_stop(stop: &Receiver<()>) {
    // Returns on an explicit stop or when the sender is dropped.
    let _ = stop.recv();
}

fn start(
    mut mixer: Mixer,
    error: Arc<Mutex<Option<String>>>,
) -> Result<(cpal::Stream, DeviceInfo), AudioError> {
    let host = cpal::default_host();
    let device = host
        .default_output_device()
        .ok_or_else(|| AudioError::Device("no default output device".into()))?;
    let name = device
        .description()
        .map(|d| d.name().to_owned())
        .unwrap_or_else(|_| "(unnamed device)".into());
    let supported = device
        .default_output_config()
        .map_err(|e| AudioError::Device(format!("{name}: no output format: {e}")))?;
    let format = supported.sample_format();
    let config: StreamConfig = supported.config();
    mixer.set_output_rate(config.sample_rate);
    let info = DeviceInfo {
        name: name.clone(),
        sample_rate: config.sample_rate,
        channels: config.channels,
        sample_format: format!("{format:?}"),
    };
    let stream = match format {
        SampleFormat::F32 => build::<f32>(&device, config, mixer, error),
        SampleFormat::F64 => build::<f64>(&device, config, mixer, error),
        SampleFormat::I16 => build::<i16>(&device, config, mixer, error),
        SampleFormat::I32 => build::<i32>(&device, config, mixer, error),
        SampleFormat::I8 => build::<i8>(&device, config, mixer, error),
        SampleFormat::U16 => build::<u16>(&device, config, mixer, error),
        SampleFormat::U8 => build::<u8>(&device, config, mixer, error),
        other => {
            return Err(AudioError::Device(format!(
                "{name}: unsupported sample format {other:?}"
            )));
        }
    }
    .map_err(|e| AudioError::Device(format!("{name}: opening the stream: {e}")))?;
    stream
        .play()
        .map_err(|e| AudioError::Device(format!("{name}: starting the stream: {e}")))?;
    Ok((stream, info))
}

fn build<T>(
    device: &cpal::Device,
    config: StreamConfig,
    mut mixer: Mixer,
    error: Arc<Mutex<Option<String>>>,
) -> Result<cpal::Stream, cpal::Error>
where
    T: SizedSample + FromSample<f32>,
{
    let channels = usize::from(config.channels.max(1));
    // Stereo scratch for the mixer; grows to the largest callback once.
    let mut stereo: Vec<f32> = vec![0.0; 4096 * 2];
    device.build_output_stream::<T, _, _>(
        config,
        move |data: &mut [T], _| {
            let frames = data.len() / channels;
            if stereo.len() < frames * 2 {
                stereo.resize(frames * 2, 0.0);
            }
            let mix = &mut stereo[..frames * 2];
            mixer.render(mix);
            for (frame, lr) in data.chunks_exact_mut(channels).zip(mix.chunks_exact(2)) {
                if channels == 1 {
                    frame[0] = T::from_sample((lr[0] + lr[1]) * 0.5);
                    continue;
                }
                frame[0] = T::from_sample(lr[0]);
                frame[1] = T::from_sample(lr[1]);
                for s in &mut frame[2..] {
                    *s = T::from_sample(0.0f32);
                }
            }
        },
        move |e| {
            if let Ok(mut slot) = error.lock() {
                *slot = Some(e.to_string());
            }
        },
        None,
    )
}

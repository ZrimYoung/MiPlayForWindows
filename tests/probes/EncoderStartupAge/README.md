# Encoder startup age probe

Runs synthetic 48 kHz stereo PCM through the production `MiPlayFfmpegAacEncoder`,
`MiPlayOrderedAccessUnitBuffer` and, in the comparison, `MiPlayEncoderOutputGate`.
Requires .NET 10 and a local FFmpeg executable with the `aac` encoder.

```sh
dotnet run --project tests/probes/EncoderStartupAge/EncoderStartupAge.csproj -- /absolute/path/to/ffmpeg > startup-age.json
```

The 18 runs compare 100 / 1000 / 3000 ms of simulated connection setup, three
times each. The baseline begins encoding before readiness and consumes its
12-frame FIFO without additional pacing after readiness, as the current desktop
standalone session does. The comparison continuously drains pre-play output and
arms publication at readiness; it does not discard any post-readiness input frame.

This is a local queue experiment, **not a complete sender or latency benchmark**:

- No phone commands, physical capture, network endpoints or audible playback.
- Input time is assigned immediately before writing synthetic PCM to FFmpeg.
  Input/output association is FIFO, matching the current adapter; codec priming
  and decoded waveform alignment are not measured.
- Receiver jitter buffers, RTSP, TIME_OFFSET, PCR, operating-system capture
  buffering and network backpressure are not simulated.
- The current standalone runner does not use the group's initial burst pacing;
  the probe consequently does not insert that pacing either.
- First-output age reveals whether connection setup leaves old audio waiting.
  Steady-state age shows whether the queue catches up after readiness. Neither is
  a measurement of what a person hears or a guarantee of a production improvement.
- A real pre-play gate must be integrated with the session's time origin and
  readiness flow. Do not drop live AAC frames to force this number down.

Each child encoder is disposed when its run ends, including cancellation/failure.
The probe exits nonzero on encoding errors and writes measurements as JSON to stdout.

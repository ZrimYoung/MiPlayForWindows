# System media bridge

The source subset in `vendor` is from [ungive/mediaremote-adapter](https://github.com/ungive/mediaremote-adapter),
commit `14eed9389241d06e29f7326c19e9ebc6ec8a5f36`, under BSD-3-Clause (`vendor/LICENSE`).
These files are unmodified. Only the observation/stream implementation and its dependencies are built.
Upstream command implementations and the test player are excluded. `observe.pl` exposes only the observation entrypoint.
The separate MiPlay `control.m` / `control.pl` adapter allows previous, toggle play/pause, next, and seek,
only when the user presses a transport button or releases the progress slider. Seek is bounded to the
observed duration and cancelled if the track changes during a drag. Controls check the expected player
bundle ID against the active client before dispatch. The UI waits for an observed state change.
`check` reports symbol availability; `target-check <bundle-id>` reads the active client without changing playback.
Unknown commands, invalid targets and seek values are rejected before loading the library. Opening the panel, previewing, observing, and automated testing
never sends a transport command. Each explicit command runs in a separate bounded-lifetime child process,
which waits for MediaRemote delivery before exiting; the UI still checks the observed player response.

The system Perl host loads the library without linking it into the UI. This follows the upstream approach
for recent macOS versions restricting direct MediaRemote clients. No system settings, entitlements,
SIP settings or audio routing are modified. Observation does not change player state; transport buttons do.
This is a private API and remains experimental. A successful send means the system accepted the command,
not proof that a particular player acted on it; playback state is still taken from observed metadata.
If it fails, the UI falls back to the existing text-only system script query.

Upstream explains that `MRNowPlayingRequest` through osascript cannot load artwork. The stream supplies
actual artwork bytes, including subsequent artwork updates. MiPlay accepts full snapshots, bounds frames
and image sizes, downsamples images off the UI thread, and clears stale state on disable/exit/failure.
Non-data messages do not clear a valid track. A player exit clears its item immediately; late events
cannot resurrect it until it launches again. On observation failure, text fallback remains available
while the bridge is retried after 15 seconds. Fractional or missing timestamps preserve progress
interpolation, while paused items stay still. QQ Music regenerates uniqueIdentifier on ordinary
position updates, so action guards use player/title/artist/album instead. Playback-state updates with
an unchanged position anchor freeze/resume the displayed position until a fresh player anchor arrives.

# Volume reply probe

`dotnet run --project tests/probes/VolumeReply/VolumeReply.csproj -- 'Exact advertised name'` reads current volume from one named speaker after legacy authentication.

Adding `--echo-current-volume` sends **one SetVolume of the immediately read value**, observes its acknowledgement, then reads back to check it remains unchanged. This is a diagnostic write, not a read-only operation. Use it only for a specifically authorized target; do not use during another controller's volume changes. It never sends Open, source setters, playback/stop commands or audio. It does not access the phone.

OH2P live result on 2026-11-16: current 57, SetVolume request `00000039`, same-sequence acknowledgement `0000000039`, subsequent readback 57. No private device/account identifiers are printed. Firmware version remains unrecorded.

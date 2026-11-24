# Read-only speaker identity probe

Discovers local MiPlay speakers and queries their advertised control endpoint using only legacy version, challenge response and `GetDeviceInfo`. It never starts capture, opens playback, changes volume/settings, or stops another sender. Unknown authentication paths are not attempted. Output contains only display name, model, group identity/name, channel and master role; account and home identifiers are discarded.

Run `dotnet run --project tests/probes/SpeakerIdentity/SpeakerIdentity.csproj` on the same LAN. Group IDs are local device data: keep raw output out of public artifacts. A shared nonempty group ID establishes membership; a name suffix alone does not. Device metadata does not prove playback or acoustic synchronization.

For a known receiver, `--trace <IPv4>` reads its initial challenge, sends the existing legacy version/ack prefix, waits for the version reply, then requests device info. It prints only command/sequence/length and display fields. It never sends Open, audio or setting commands. A peer closing before the version reply establishes an initialization incompatibility, not its exact authentication cause.

For the user-authorized Xiaomi PC Manager diagnostic, `--trace-safety <IPv4>`
uses the existing 18.0.0.3 source profile (3.1.6030516), sends only the legacy
prefix plus SafetyInfo, and stops after reporting the selected algorithm types.
`--trace-auth <IPv4>` additionally permits one encrypted SHA-256 mutual
challenge exchange for the exact observed type tuple (1,4,1,1,2). It verifies the
receiver's proof locally and stops immediately after sending its own proof;
this does not establish that the receiver accepted that proof. Neither modern
mode sends GetDeviceInfo, identity updates, Open, audio or playback controls.
Unknown auth envelopes and unexpected proof sequences stop the probe;
no fallback algorithms or credential material are printed. These modes are
research probes, not a replacement for the application's legacy sender path.

`--trace-identity <IPv4>` extends that diagnostic by exactly one encrypted
GetDeviceInfo (sequence 3) using the continued cipher state, then reads only
display name/model/type. On this PC the first query currently closes without
a reply; this remains an unresolved framing/authentication lifecycle boundary.
The phone's actual SafetyDataDeal constructor copies the key string into both
key and IV buffers (0x2bbf24 and 0x2bbf54); this explicit diagnostic profile uses
that behavior rather than silently trying several IVs. No production default
is changed.

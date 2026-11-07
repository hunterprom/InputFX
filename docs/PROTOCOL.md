# Device protocol notes

These notes describe what InputFX sends to the two devices it can light up. Everything here was learned by recording the vendors' own apps with USBPcap. InputFX only sends those recorded commands, or replays of them with one documented value changed. Do not invent new writes: several of these go into the device's flash memory.

Only one program can own a HID device at a time, so the vendor apps (EXCALIBUR Software `OemDrv.exe`, `Gaming Mouse 3.0.exe`) must be closed first.

## Keyboard: EXCALIBUR / White Shark K916 (SINOWEALTH)

- USB 258A:019D (wireless dongle 258A:0150). 104 keys, 126 LEDs.
- LED index = `row + 6 * column` (19 columns x 6 rows). Key positions are in `src/InputFX/Assets/layout.csv`.

### Per-key streaming

- HID feature report id `0x09`, 520 bytes.
- Header: `09 08 00 00 01 00 7A 01`, then 126 x RGB starting at byte 8.
- The keyboard accepts about 7 frames per second (about 136 ms per `SetFeature`). This is a device limit. Custom bursts, backgrounds and the games are all built around it.
- Streaming does not write flash.

### Built-in modes

Built-in modes run inside the keyboard: smooth, zero latency and kept after InputFX closes. A mode is set by writing frame `84` and then frame `04`. Captured templates: `src/InputFX/Assets/kb_cfg84.bin` and `kb_cfg04.bin`.

- Byte 18 is the effect id.
- In the `04` frame, byte `64 + 2*id` is that effect's brightness (1, 5, 10, 15 or 20) and byte `65 + 2*id` is `(speed 0-4 << 4) | colour nibble`.

| Id | Mode | Reacts to key presses |
| --- | --- | --- |
| 0 | Off | |
| 1 | Fixed on | |
| 2 | Respire | |
| 3 | Rainbow | |
| 4 | Flash away | yes |
| 5 | Raindrops | |
| 7 | Ripples shining | yes |
| 8 | Stars twinkle | |
| 9 | Shadow disappear | |
| 10 | Retro snake | |
| 11 | Neon stream | |
| 12 | Reaction | yes |
| 13 | Sine wave | |
| 15 | Rotating windmill | |
| 16 | Colorful waterfall | |
| 17 | Blossoming | |
| 21 | Self-define | |

These writes go into the keyboard's flash. InputFX makes them only when you pick a mode or a profile changes, never on a timer or per key press.

## Mouse: Galahad GM-5007 "USB GAMING MOUSE"

- USB 30FA:1440, HID feature report id `0x07`, 8-byte reports.
- Settings live in the mouse's own memory.

### Built-in backlight modes

Applying a mode replays about 867 frames (about 5 seconds) from `src/InputFX/Assets/mouse_replay.bin` with byte `0x1C` (and bit 7 of byte `0x33`) patched. The 14 named modes are in `HwLab.MouseVariants`. For example "Button Response" (`9C`/`3F`) lights on click and "Ambilight" is `F4`/`3F`.

### Live lighting

Used for the click pulse and music breathing. While live, the mouse's normal lighting is off.

1. Enter live mode by writing zeros to the lighting block `0x18`-`0x1F` in RAM, without the commit frame:
   - `07 18 03 N 18 00 00 07` (x8)
   - `07 18 00 00 18 00 00 00`
   - `07 18 05 N 18 00 00 07` (x8)
   - `07 18 00 00 00 00 00 00`
   - Do not send `07 18 09` (commit).
2. Wait about 2.2 seconds, then send the handshake `07 13 FF 52 0B 00 83 EB`.
3. Zone frames: `07 13 FF A2 0B 00 03 FF` followed by `07 14 8F MASK 0B 00 03 FF`. `MASK FF` lights every zone at once; `F0` and `0F` are the two halves. This gives a symmetrical flash on both sides.
4. The level-bar command `07 13 FF E1 L 00 03 FF` only sweeps from the far left to the middle.
5. Exit live mode by writing the lighting block back (`HwLab.MouseBlock18`), which restores the last chosen built-in mode.

See `src/InputFX/MouseLive.cs`.

## Recording your own device

`tools/pcap_dump.py` is a minimal USBPcap (`.pcap`, link type 249) parser. Record the vendor app while you change one setting at a time, then compare the control transfers. A device support request with such a recording makes adding a new device much easier.

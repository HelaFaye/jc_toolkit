"""Test data shared by the tests and the app's self-test."""


def small_midi():
    """Format 1, 96 ticks per quarter. Track 0: 120 BPM, then 60 BPM at beat 2. Track 1
    "Lead", channel 1: C4 (beat 0-1), G4 + C5 chord (beat 1-2), E4 (beat 2-3, at 60 BPM).
    Track 2, channel 10 (drums): an F#2 hi-hat at beat 0. Length: 2.0 seconds."""
    def chunk(body):
        return b"MTrk" + len(body).to_bytes(4, "big") + bytes(body)
    tempo = chunk([0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0x81, 0x40, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40,
                   0x00, 0xFF, 0x2F, 0x00])
    lead = chunk([0x00, 0xFF, 0x03, 0x04] + list(b"Lead") +
                 [0x00, 0x90, 60, 100, 0x60, 0x80, 60, 0, 0x00, 0x90, 67, 90, 0x00, 72, 110,
                  0x60, 0x80, 67, 0, 0x00, 72, 0, 0x00, 0x90, 64, 127, 0x60, 64, 0, 0x00, 0xFF, 0x2F, 0x00])
    drums = chunk([0x00, 0x99, 42, 100, 0x30, 0x89, 42, 0, 0x00, 0xFF, 0x2F, 0x00])
    header = b"MThd" + (6).to_bytes(4, "big") + (1).to_bytes(2, "big") + (3).to_bytes(2, "big") + (96).to_bytes(2, "big")
    return header + tempo + lead + drums

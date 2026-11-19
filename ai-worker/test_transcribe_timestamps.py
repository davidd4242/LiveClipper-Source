import unittest
from types import SimpleNamespace

from transcribe import stamp, transcript_row


class TranscriptTimestampsTest(unittest.TestCase):
    def test_real_eof_prediction(self):
        duration = 696871 / 16000
        row = transcript_row(SimpleNamespace(start=36.53, end=43.77, text=" speech "), duration)
        self.assertEqual(row, dict(start=36.53, end=duration, text="speech"))
        self.assertEqual(stamp(row["end"]), "00:00:43,554")

    def test_prediction_entirely_after_eof_has_no_cue(self):
        self.assertIsNone(transcript_row(SimpleNamespace(start=45, end=46, text="tail"), 44))

    def test_in_bounds_prediction_keeps_its_timing(self):
        self.assertEqual(transcript_row(SimpleNamespace(start=1.2, end=3.4, text="ok"), 44),
                         dict(start=1.2, end=3.4, text="ok"))

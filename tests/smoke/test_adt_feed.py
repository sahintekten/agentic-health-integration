from pathlib import Path

OUTBOX = Path(__file__).resolve().parents[2] / "mock-systems" / "adt-feed" / "outbox"

SAMPLE_A01 = "\r".join(
    [
        "MSH|^~\\&|SMOKE|TEST|ADT_FEED|HOSP|20260101120000||ADT^A01^ADT_A01|SMOKE0001|P|2.5",
        "EVN|A01|20260101120000",
        "PID|1||HOSP^1234^MR||TEST^SMOKE||19800101|U",
        "PV1|1|I|MED1^001^A^HOSP",
    ]
)


def test_adt_message_gets_ack(mllp):
    ack = mllp(SAMPLE_A01)
    assert ack.startswith("MSH|")
    assert "MSA|AA|SMOKE0001" in ack


def test_non_hl7_message_is_rejected(mllp):
    assert "MSA|AR" in mllp("hello")


def test_outbox_has_hl7_files():
    assert any(OUTBOX.glob("*.hl7"))

from app import main


def test_main() -> None:
    assert main() == "fixture"

from __future__ import annotations

from scripts.export_contract import CONTRACTS, build


def test_committed_contract_matches_the_service() -> None:
    """Fails when the API changes without regenerating contracts/ (python -m scripts.export_contract)."""
    for name, expected in build().items():
        committed = (CONTRACTS / name).read_text(encoding="utf-8")
        assert committed == expected, f"{name} is out of date; run: python -m scripts.export_contract"
